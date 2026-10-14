const cookieId = /^[A-Za-z0-9_-]{43}$/;

export function readOpaqueCookie(
  request: Request,
  name: string,
): string | null {
  for (const part of (request.headers.get('cookie') ?? '').split(';')) {
    const [key, value] = part.trim().split('=', 2);
    if (key === name && value && cookieId.test(value)) return value;
  }
  return null;
}
