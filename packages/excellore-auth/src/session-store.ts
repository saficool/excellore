import { createHash, createHmac, randomBytes } from 'node:crypto';
import { CompactEncrypt, compactDecrypt } from 'jose';
import type { Redis } from 'iovalkey';
import { z } from 'zod';

const recordSchema = z.object({
  sid: z.string(),
  kcSid: z.string(),
  sub: z.string(),
  acr: z.string(),
  displayName: z.string().trim().min(1).max(200).optional(),
  email: z.string().trim().min(1).max(320).optional(),
  accessToken: z.string(),
  accessTokenExpiresAt: z.number().int(),
  refreshToken: z.string(),
  idToken: z.string(),
  createdAt: z.number().int(),
  lastSeenAt: z.number().int(),
  logoutMarker: z.string().default(''),
});

export type SessionRecord = z.infer<typeof recordSchema>;
type NewSession = Omit<
  SessionRecord,
  'sid' | 'createdAt' | 'lastSeenAt' | 'logoutMarker'
>;
export interface LoginFlow {
  state: string;
  nonce: string;
  verifier: string;
  returnTo: string;
  expectedSub?: string | undefined;
  minimumAcr?: string | undefined;
}

const IDLE_SECONDS = 30 * 60;
const ABSOLUTE_SECONDS = 9 * 60 * 60;
const FLOW_SECONDS = 5 * 60;
const casScript = `
  if redis.call('GET', KEYS[1]) ~= ARGV[1] then return 0 end
  redis.call('SET', KEYS[1], ARGV[2], 'EX', ARGV[3])
  return 1
`;
const releaseScript = `
  if redis.call('GET', KEYS[1]) == ARGV[1] then return redis.call('DEL', KEYS[1]) end
  return 0
`;
const extendScript = `
  if redis.call('GET', KEYS[1]) == ARGV[1] then return redis.call('PEXPIRE', KEYS[1], ARGV[2]) end
  return 0
`;

export function randomOpaqueId(): string {
  return randomBytes(32).toString('base64url');
}

export class ValkeySessionStore {
  private readonly key: Uint8Array;

  constructor(
    private readonly redis: Redis,
    private readonly prefix: string,
    encryptionKey: string,
  ) {
    if (!/^[a-z][a-z0-9-]*:sess:$/.test(prefix))
      throw new Error('Invalid session prefix');
    const key = Buffer.from(encryptionKey, 'base64url');
    if (key.length !== 32) throw new Error('Invalid session encryption key');
    this.key = key;
  }

  private sessionKey(sid: string): string {
    return `${this.prefix}${sid}`;
  }
  private flowKey(id: string): string {
    return `${this.prefix}flow:${id}`;
  }
  private indexKey(kind: 'kcSid' | 'sub', value: string): string {
    const digest = createHmac('sha256', this.key)
      .update(`${kind}:${value}`)
      .digest('hex');
    return `${this.prefix}idx:${kind}:${digest}`;
  }
  private globalLogoutKey(sub: string): string {
    return `excellore:logout:${createHash('sha256').update(sub).digest('hex')}`;
  }
  async revokeSubjectGlobally(sub: string): Promise<void> {
    await this.redis.set(
      this.globalLogoutKey(sub),
      randomOpaqueId(),
      'EX',
      ABSOLUTE_SECONDS,
    );
  }
  private async encrypt(value: unknown): Promise<string> {
    return new CompactEncrypt(new TextEncoder().encode(JSON.stringify(value)))
      .setProtectedHeader({ alg: 'dir', enc: 'A256GCM' })
      .encrypt(this.key);
  }
  private async decrypt(value: string): Promise<unknown> {
    const { plaintext } = await compactDecrypt(value, this.key);
    return JSON.parse(new TextDecoder().decode(plaintext));
  }

  async createFlow(id: string, flow: LoginFlow): Promise<void> {
    await this.redis.set(
      this.flowKey(id),
      await this.encrypt(flow),
      'EX',
      FLOW_SECONDS,
      'NX',
    );
  }
  async consumeFlow(id: string): Promise<LoginFlow | null> {
    const value = await this.redis.getdel(this.flowKey(id));
    if (!value) return null;
    return z
      .object({
        state: z.string(),
        nonce: z.string(),
        verifier: z.string(),
        returnTo: z.string(),
        expectedSub: z.string().optional(),
        minimumAcr: z.string().optional(),
      })
      .parse(await this.decrypt(value));
  }

  async create(input: NewSession): Promise<SessionRecord> {
    const now = Math.floor(Date.now() / 1000);
    const record: SessionRecord = {
      ...input,
      sid: randomOpaqueId(),
      createdAt: now,
      lastSeenAt: now,
      logoutMarker:
        (await this.redis.get(this.globalLogoutKey(input.sub))) ?? '',
    };
    const value = await this.encrypt(record);
    const kcIndex = this.indexKey('kcSid', record.kcSid);
    const subIndex = this.indexKey('sub', record.sub);
    await this.redis
      .multi()
      .set(this.sessionKey(record.sid), value, 'EX', IDLE_SECONDS)
      .sadd(kcIndex, record.sid)
      .expire(kcIndex, ABSOLUTE_SECONDS)
      .sadd(subIndex, record.sid)
      .expire(subIndex, ABSOLUTE_SECONDS)
      .exec();
    return record;
  }
  private async stored(
    sid: string,
  ): Promise<{ record: SessionRecord; value: string } | null> {
    if (!/^[A-Za-z0-9_-]{43}$/.test(sid)) return null;
    const value = await this.redis.get(this.sessionKey(sid));
    if (!value) return null;
    const record = recordSchema.parse(await this.decrypt(value));
    if (record.sid !== sid) throw new Error('Invalid session record');
    return { record, value };
  }
  async get(sid: string): Promise<SessionRecord | null> {
    return this.update(sid, (record) => ({
      ...record,
      lastSeenAt: Math.floor(Date.now() / 1000),
    }));
  }
  async update(
    sid: string,
    mutate: (record: SessionRecord) => SessionRecord,
  ): Promise<SessionRecord | null> {
    for (let attempt = 0; attempt < 10; attempt += 1) {
      const stored = await this.stored(sid);
      if (!stored) return null;
      const logoutMarker =
        (await this.redis.get(this.globalLogoutKey(stored.record.sub))) ?? '';
      if (stored.record.logoutMarker !== logoutMarker) {
        await this.delete(sid);
        return null;
      }
      const now = Math.floor(Date.now() / 1000);
      const remaining = stored.record.createdAt + ABSOLUTE_SECONDS - now;
      if (remaining <= 0 || now - stored.record.lastSeenAt > IDLE_SECONDS) {
        await this.delete(sid);
        return null;
      }
      const next = mutate(stored.record);
      if (
        next.sid !== sid ||
        next.sub !== stored.record.sub ||
        next.kcSid !== stored.record.kcSid ||
        next.createdAt !== stored.record.createdAt
      )
        throw new Error('Session identity cannot change');
      const value = await this.encrypt(next);
      const saved = await this.redis.eval(
        casScript,
        1,
        this.sessionKey(sid),
        stored.value,
        value,
        String(Math.min(IDLE_SECONDS, remaining)),
      );
      if (saved === 1) return next;
    }
    throw new Error('Session update contention');
  }
  async delete(sid: string): Promise<void> {
    const stored = await this.stored(sid);
    if (!stored) return;
    await this.redis
      .multi()
      .del(this.sessionKey(sid))
      .srem(this.indexKey('kcSid', stored.record.kcSid), sid)
      .srem(this.indexKey('sub', stored.record.sub), sid)
      .exec();
  }
  async deleteByKeycloakSession(kcSid: string): Promise<void> {
    await this.deleteIndexed('kcSid', kcSid);
  }
  async deleteBySubject(sub: string): Promise<void> {
    await this.deleteIndexed('sub', sub);
  }
  private async deleteIndexed(
    kind: 'kcSid' | 'sub',
    value: string,
  ): Promise<void> {
    const index = this.indexKey(kind, value);
    const sids = await this.redis.smembers(index);
    for (const sid of sids) {
      const stored = await this.stored(sid);
      if (stored && stored.record[kind] === value) await this.delete(sid);
    }
    // Do not remove the index: a new login may have added a session while this scan ran.
  }
  async acquireRefreshLock(sid: string, owner: string): Promise<boolean> {
    return (
      (await this.redis.set(
        `${this.sessionKey(sid)}:refresh`,
        owner,
        'PX',
        30_000,
        'NX',
      )) === 'OK'
    );
  }
  async extendRefreshLock(sid: string, owner: string): Promise<boolean> {
    return (
      (await this.redis.eval(
        extendScript,
        1,
        `${this.sessionKey(sid)}:refresh`,
        owner,
        '30000',
      )) === 1
    );
  }
  async releaseRefreshLock(sid: string, owner: string): Promise<void> {
    await this.redis.eval(
      releaseScript,
      1,
      `${this.sessionKey(sid)}:refresh`,
      owner,
    );
  }
  async claimLogoutToken(jti: string): Promise<boolean> {
    return (
      (await this.redis.set(
        this.indexKey('sub', `logout:${jti}`),
        '1',
        'EX',
        600,
        'NX',
      )) === 'OK'
    );
  }
}
