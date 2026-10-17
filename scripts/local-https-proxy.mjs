import { request as httpRequest } from 'node:http';
import { createServer as createHttpsServer } from 'node:https';
import { readFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';

export const upstreams = new Map([
  ['excellore.localhost', 3000],
  ['profile.excellore.localhost', 3001],
  ['access.excellore.localhost', 3002],
  ['platform.excellore.localhost', 3003],
  ['auth.excellore.localhost', 8080],
]);

export function normalizeHost(value = '') {
  return value.toLowerCase().replace(/:443$/, '');
}

export function upstreamPort(value) {
  return upstreams.get(normalizeHost(value));
}

export function forwardedHeaders(source, publicHost) {
  const headers = { ...source };
  for (const name of [
    'forwarded',
    'x-forwarded-for',
    'x-forwarded-host',
    'x-forwarded-port',
    'x-forwarded-proto',
  ])
    delete headers[name];
  headers.host = publicHost;
  headers['x-forwarded-for'] = '127.0.0.1';
  headers['x-forwarded-host'] = publicHost;
  headers['x-forwarded-port'] = '443';
  headers['x-forwarded-proto'] = 'https';
  return headers;
}

function reject(response, statusCode) {
  response.writeHead(statusCode, {
    'content-type': 'text/plain; charset=utf-8',
    'cache-control': 'no-store',
  });
  response.end(
    statusCode === 502
      ? 'Local service unavailable.\n'
      : 'Unknown local host.\n',
  );
}

export function createProxy(options) {
  const server = createHttpsServer(
    {
      cert: readFileSync(options.certificate),
      key: readFileSync(options.privateKey),
      minVersion: 'TLSv1.2',
    },
    (request, response) => {
      const publicHost = normalizeHost(request.headers.host);
      const port = upstreamPort(publicHost);
      if (!port) return reject(response, 421);

      const upstream = httpRequest(
        {
          hostname: '127.0.0.1',
          port,
          method: request.method,
          path: request.url,
          headers: forwardedHeaders(request.headers, publicHost),
        },
        (upstreamResponse) => {
          response.writeHead(
            upstreamResponse.statusCode ?? 502,
            upstreamResponse.headers,
          );
          upstreamResponse.pipe(response);
        },
      );
      upstream.on('error', () => reject(response, 502));
      request.pipe(upstream);
    },
  );

  server.on('upgrade', (request, socket, head) => {
    const publicHost = normalizeHost(request.headers.host);
    const port = upstreamPort(publicHost);
    if (!port) return socket.destroy();

    const upstream = httpRequest({
      hostname: '127.0.0.1',
      port,
      method: request.method,
      path: request.url,
      headers: forwardedHeaders(request.headers, publicHost),
    });
    upstream.once(
      'upgrade',
      (upstreamResponse, upstreamSocket, upstreamHead) => {
        const status = `HTTP/1.1 ${upstreamResponse.statusCode} ${upstreamResponse.statusMessage}\r\n`;
        const headers = upstreamResponse.rawHeaders
          .reduce((rows, value, index, all) => {
            if (index % 2 === 0) rows.push(`${value}: ${all[index + 1]}\r\n`);
            return rows;
          }, [])
          .join('');
        socket.write(`${status}${headers}\r\n`);
        if (head.length) upstreamSocket.write(head);
        if (upstreamHead.length) socket.write(upstreamHead);
        upstreamSocket.pipe(socket).pipe(upstreamSocket);
      },
    );
    upstream.once('error', () => socket.destroy());
    upstream.end();
  });
  server.on('clientError', (_error, socket) => socket.destroy());
  return server;
}

const invokedPath =
  process.argv[1] && fileURLToPath(import.meta.url) === process.argv[1];
if (invokedPath) {
  const root = fileURLToPath(new URL('../', import.meta.url));
  const server = createProxy({
    certificate: `${root}.local/certs/excellore.pem`,
    privateKey: `${root}.local/certs/excellore-key.pem`,
  });
  server.listen(443, '127.0.0.1', () => {
    console.log(
      'Local HTTPS gateway listening on https://excellore.localhost.',
    );
  });
}
