export function canonicalBffRequest(
  request: Request,
  appBaseUrl: string,
): Request | null {
  let expected: URL;
  try {
    expected = new URL(appBaseUrl);
  } catch {
    return null;
  }
  if (
    request.headers.get('host')?.toLowerCase() !== expected.host.toLowerCase()
  )
    return null;
  const incoming = new URL(request.url);
  if (!['http:', 'https:'].includes(incoming.protocol)) return null;
  const canonical = new URL(incoming.pathname + incoming.search, expected);
  return new Request(canonical, request);
}
