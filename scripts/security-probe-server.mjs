import { createServer } from 'node:http';
import { createBffSecurityHeaders } from '../packages/excellore-auth/dist/index.js';

const port = Number(process.env.SECURITY_PROBE_PORT ?? 4173);
const server = createServer((request, response) => {
  const { responseHeaders } = createBffSecurityHeaders(
    new Headers(
      Object.entries(request.headers).flatMap(([name, value]) =>
        value === undefined
          ? []
          : [[name, Array.isArray(value) ? value.join(', ') : value]],
      ),
    ),
    { development: false, secureOrigin: false },
  );
  responseHeaders.forEach((value, name) => response.setHeader(name, value));
  response.setHeader('Content-Type', 'text/html; charset=utf-8');
  response.setHeader('Cache-Control', 'no-store');
  response.statusCode = request.url === '/' ? 200 : 404;
  response.end(
    '<!doctype html><html><head><title>Excellore security probe</title></head><body><main>Excellore</main></body></html>',
  );
});

server.listen(port, '0.0.0.0', () =>
  process.stdout.write(`Security probe listening on ${port}\n`),
);
