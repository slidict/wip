// Fixed-load HTTP generator with no external dependencies, so it behaves identically
// regardless of which container platform/shell is under test. Always invoked from the
// Windows host process per the benchmark protocol (same load source for every config).
//
// Usage: node load-gen.mjs --url http://localhost:18080/ --concurrency 20 --duration 30 --out results.json
import http from 'node:http';
import { writeFileSync } from 'node:fs';

function parseArgs(argv) {
  const out = { url: 'http://127.0.0.1:18080/', concurrency: 20, duration: 30, out: null };
  for (let i = 0; i < argv.length; i++) {
    const a = argv[i];
    if (a === '--url') out.url = argv[++i];
    else if (a === '--concurrency') out.concurrency = Number(argv[++i]);
    else if (a === '--duration') out.duration = Number(argv[++i]);
    else if (a === '--out') out.out = argv[++i];
  }
  return out;
}

const args = parseArgs(process.argv.slice(2));
const target = new URL(args.url);
const agent = new http.Agent({ keepAlive: true, maxSockets: args.concurrency });

const latenciesMs = [];
let success = 0;
let failed = 0;
let stop = false;

function oneRequest() {
  return new Promise((resolve) => {
    const start = process.hrtime.bigint();
    const req = http.request(
      {
        hostname: target.hostname,
        port: target.port,
        path: target.pathname + target.search,
        method: 'GET',
        agent,
        timeout: 5000,
      },
      (res) => {
        res.on('data', () => {});
        res.on('end', () => {
          const ms = Number(process.hrtime.bigint() - start) / 1e6;
          if (res.statusCode && res.statusCode >= 200 && res.statusCode < 300) {
            success++;
            latenciesMs.push(ms);
          } else {
            failed++;
          }
          resolve();
        });
      },
    );
    req.on('timeout', () => req.destroy(new Error('timeout')));
    req.on('error', () => {
      failed++;
      resolve();
    });
    req.end();
  });
}

async function worker() {
  while (!stop) {
    await oneRequest();
  }
}

function percentile(sorted, p) {
  if (sorted.length === 0) return null;
  const idx = Math.min(sorted.length - 1, Math.floor((p / 100) * sorted.length));
  return sorted[idx];
}

const wallStart = Date.now();
const workers = Array.from({ length: args.concurrency }, () => worker());
setTimeout(() => {
  stop = true;
}, args.duration * 1000);

await Promise.all(workers);
const wallMs = Date.now() - wallStart;

latenciesMs.sort((a, b) => a - b);
const total = success + failed;
const result = {
  url: args.url,
  concurrency: args.concurrency,
  requestedDurationSec: args.duration,
  actualDurationMs: wallMs,
  totalRequests: total,
  successRequests: success,
  failedRequests: failed,
  errorRate: total === 0 ? null : failed / total,
  throughputRps: wallMs === 0 ? null : success / (wallMs / 1000),
  latencyMsMedian: percentile(latenciesMs, 50),
  latencyMsP95: percentile(latenciesMs, 95),
  latencyMsMin: latenciesMs[0] ?? null,
  latencyMsMax: latenciesMs[latenciesMs.length - 1] ?? null,
};

const text = JSON.stringify(result, null, 2);
if (args.out) {
  writeFileSync(args.out, text);
}
console.log(text);
