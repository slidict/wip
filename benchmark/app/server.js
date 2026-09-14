// Fixed-response benchmark app: no framework, no dependencies, same behavior under
// Docker Desktop and Wip/WSLC so the benchmark measures the container platform, not the app.
const http = require('http');

const PORT = process.env.PORT || 3000;
const BODY = Buffer.from(JSON.stringify({ ok: true, service: 'wip-bench' }));

const server = http.createServer((req, res) => {
  if (req.url === '/health') {
    res.writeHead(200, { 'Content-Type': 'application/json', 'Content-Length': BODY.length });
    res.end(BODY);
    return;
  }
  res.writeHead(200, { 'Content-Type': 'application/json', 'Content-Length': BODY.length });
  res.end(BODY);
});

server.listen(PORT, '0.0.0.0', () => {
  console.log(`wip-bench listening on ${PORT}`);
});
