import http from 'k6/http';
import { check } from 'k6';

const shell = __ENV.SHELL_URL || 'http://shell.excellore.localhost:3000';
const access = __ENV.ACCESS_API_URL || 'http://127.0.0.1:5002';
const profile = __ENV.PROFILE_URL || 'http://profile.excellore.localhost:3001';
const token = __ENV.ACCESS_TOKEN || 'load-test-token';
const cookie = __ENV.SESSION_COOKIE || 'load-test-session';
const userId = __ENV.USER_ID || '11111111-1111-4111-8111-111111111111';
const tenantId = __ENV.TENANT_ID || '22222222-2222-4222-8222-222222222222';
const roleId = __ENV.ROLE_ID || '33333333-3333-4333-8333-333333333333';

export const options = {
  scenarios: {
    login_burst: {
      executor: 'constant-arrival-rate',
      exec: 'loginBurst',
      rate: 10,
      timeUnit: '1s',
      duration: '30s',
      preAllocatedVUs: 10,
    },
    authz_resolve: {
      executor: 'constant-arrival-rate',
      exec: 'authzResolve',
      rate: 50,
      timeUnit: '1s',
      duration: '30s',
      preAllocatedVUs: 20,
    },
    refresh_storm: {
      executor: 'per-vu-iterations',
      exec: 'refreshStorm',
      vus: 20,
      iterations: 5,
      maxDuration: '30s',
    },
    bulk_assignment: {
      executor: 'constant-vus',
      exec: 'bulkAssignment',
      vus: 2,
      duration: '15s',
    },
  },
  thresholds: {
    http_req_failed: ['rate<0.01'],
    'http_req_duration{scenario:authz_resolve}': ['p(95)<50'],
    'http_req_duration{scenario:login_burst}': ['p(95)<500'],
  },
};

const bearer = {
  Authorization: `Bearer ${token}`,
  'Content-Type': 'application/json',
};

export function loginBurst() {
  const response = http.get(`${shell}/auth/login?returnTo=%2F`, {
    redirects: 0,
    tags: { scenario: 'login_burst' },
  });
  check(response, { 'login redirects': (item) => item.status === 302 });
}

export function authzResolve() {
  const response = http.post(
    `${access}/internal/v1/authz/resolve`,
    JSON.stringify({ userId, tenantId }),
    { headers: bearer, tags: { scenario: 'authz_resolve' } },
  );
  check(response, { 'resolve succeeds': (item) => item.status === 200 });
}

export function refreshStorm() {
  const response = http.get(`${profile}/auth/session`, {
    headers: { Cookie: cookie },
    tags: { scenario: 'refresh_storm' },
  });
  check(response, { 'session remains valid': (item) => item.status === 200 });
}

export function bulkAssignment() {
  const response = http.post(
    `${access}/v1/tenants/${tenantId}/role-assignment-batches`,
    JSON.stringify({
      roleId,
      targetUserIds: [userId],
      reason: 'wp13-load-test',
    }),
    { headers: bearer, tags: { scenario: 'bulk_assignment' } },
  );
  check(response, {
    'batch accepted': (item) => item.status === 202 || item.status === 409,
  });
}
