/*
 * Exercises addons-stats/src/worker.js without Cloudflare: the migration as written in node:sqlite
 * (D1 is SQLite), a fake rate-limit binding, and a faked add-ons index.
 *
 *   node tools\addons-stats-harness.mjs
 */
import { DatabaseSync } from "node:sqlite";
import { readFileSync } from "node:fs";
import { fileURLToPath, pathToFileURL } from "node:url";
import path from "node:path";

const dir = path.join(path.dirname(fileURLToPath(import.meta.url)), "..", "addons-stats");
const mod = await import(pathToFileURL(path.join(dir, "src", "worker.js")).href);
const worker = mod.default;

const db = new DatabaseSync(":memory:");
for (const m of ["0001_stats.sql", "0002_like_state.sql"]) db.exec(readFileSync(path.join(dir, "migrations", m), "utf8"));
const DB = {
  prepare(sql) {
    const stmt = db.prepare(sql.replace(/\?\d+/g, "?"));
    const bound = (...a) => ({
      first: async () => stmt.get(...a) ?? null,
      all: async () => ({ results: stmt.all(...a) }),
      run: async () => ({ meta: { changes: Number(stmt.run(...a).changes) } }),
    });
    return { bind: bound };
  },
};
let limit = 1000; const hits = new Map(); let limiterThrows = false;
const WRITE_LIMIT = {
  async limit({ key }) {
    if (limiterThrows) throw new Error("down");
    if (!/^[0-9a-f]{64}$/.test(key)) throw new Error("not a hash");
    const n = (hits.get(key) || 0) + 1; hits.set(key, n); return { success: n <= limit };
  },
};
const env = { DB, WRITE_LIMIT };

let indexStatus = 200, indexFetches = 0;
globalThis.fetch = async (url) => {
  if (String(url).startsWith("https://raw.githubusercontent.com/sukumar-v/loungepad-addons/")) {
    indexFetches++;
    if (indexStatus !== 200) return new Response("nope", { status: indexStatus });
    return new Response(JSON.stringify({ addons: [{ kind: "extension", id: "howlongtobeat" }, { kind: "theme", id: "arcade" }] }), { headers: { "Content-Type": "application/json" } });
  }
  throw new Error(`unexpected fetch ${url}`);
};

let ip = "203.0.113.5";
async function call(method, p, body, headers = { "X-Loungepad-Client": "1" }) {
  const h = { ...headers };
  if (ip) h["CF-Connecting-IP"] = ip;
  if (body !== undefined) h["Content-Type"] = "application/json";
  const res = await worker.fetch(new Request(`https://api.example${p}`, { method, headers: h, body: body === undefined ? undefined : JSON.stringify(body) }), env, { waitUntil() {} });
  return { status: res.status, body: await res.json() };
}
const like = (key, on = true) => call("POST", "/v1/addons/like", { key, on });
const dl = (key) => call("POST", "/v1/addons/download", { key });
const stats = async () => (await call("GET", "/v1/addons/stats")).body.counts;

let pass = 0, fail = 0;
const check = (name, ok, extra = "") => { ok ? pass++ : fail++; console.log(`${ok ? "ok  " : "FAIL"} ${name}${extra ? `  (${extra})` : ""}`); };
console.error = () => {};

check("no client header is 403", (await call("GET", "/v1/addons/stats", undefined, {})).status === 403);
check("unknown route is 404", (await call("GET", "/v1/addons/nope")).status === 404);
check("GET on a write route is 404", (await call("GET", "/v1/addons/like")).status === 404);
check("stats start empty", JSON.stringify(await stats()) === "{}");

let r = await dl("extension:howlongtobeat");
check("a download counts", r.status === 200 && r.body.counted && r.body.downloads === 1 && r.body.likes === 0);
r = await dl("extension:howlongtobeat");
check("the same address again today does not", r.status === 200 && !r.body.counted && r.body.downloads === 1);
ip = "198.51.100.7";
r = await dl("extension:howlongtobeat");
check("another address counts", r.body.counted && r.body.downloads === 2);

r = await like("extension:howlongtobeat");
check("a like counts", r.body.counted && r.body.likes === 1);
r = await like("extension:howlongtobeat");
check("a second like from the address does not", !r.body.counted && r.body.likes === 1);
r = await like("extension:howlongtobeat", false);
check("an unlike counts", r.body.counted && r.body.likes === 0);
r = await like("extension:howlongtobeat", false);
check("a second unlike in a row does not", !r.body.counted && r.body.likes === 0);
r = await like("extension:howlongtobeat");
check("liking again after the unlike counts (the user's report)", r.body.counted && r.body.likes === 1);
for (let i = 0; i < 10; i++) await like("extension:howlongtobeat", i % 2 === 1);
r = await like("extension:howlongtobeat");
check("changes stop counting at the day's cap, and the count stays sane", !r.body.counted && r.body.likes <= 1 && r.body.likes >= 0, `likes=${r.body.likes}`);
const cap = db.prepare("SELECT changes FROM like_state").all().map(x => x.changes);
check("no more than six changes recorded for one address and add-on", cap.every(n => n <= 6), cap.join(","));
ip = "198.51.100.99";
r = await like("extension:howlongtobeat", false);
check("an unlike with no state today (a like from an earlier day) counts", r.body.counted);
ip = "198.51.100.7";
ip = "192.0.2.9";
r = await like("theme:arcade", false);
check("likes never go below zero", r.body.counted && r.body.likes === 0);

const s = await stats();
check("stats carry every counted add-on", s["extension:howlongtobeat"]?.downloads === 2 && s["extension:howlongtobeat"]?.likes === 0 && s["theme:arcade"]?.likes === 0);

check("a key the index does not list is 404", (await like("extension:not-there")).status === 404);
check("a malformed key is 400", (await like("../etc")).status === 400 && (await like("Extension:X")).status === 400);
check("a body that is not JSON is 400", (await worker.fetch(new Request("https://api.example/v1/addons/like", { method: "POST", headers: { "X-Loungepad-Client": "1", "CF-Connecting-IP": ip }, body: "nope" }), env, {})).status === 400);
ip = null;
check("no client address is refused", (await like("theme:arcade")).status === 400);
ip = "192.0.2.10";

check("the address is never stored", !JSON.stringify(db.prepare("SELECT * FROM marks").all()).includes("192.0.2") && db.prepare("SELECT * FROM marks").all().every(m => /^[0-9a-f]{64}$/.test(m.k)));
check("one salt for today", db.prepare("SELECT count(*) n FROM salts").get().n === 1);
check("the index is fetched once and kept", indexFetches === 1, `fetches=${indexFetches}`);

mod.resetIndexCache(); indexStatus = 500;
check("index unavailable: writes are 503", (await dl("theme:arcade")).status === 503);
check("index unavailable: stats still answer", (await call("GET", "/v1/addons/stats")).status === 200);
indexStatus = 200;

limit = 2; hits.clear(); ip = "192.0.2.50";
await dl("theme:arcade"); await like("theme:arcade");
r = await like("theme:arcade", false);
check("the third write in a window is 429", r.status === 429);
limiterThrows = true;
check("a failing limiter does not block", (await dl("extension:howlongtobeat")).status === 200);
limiterThrows = false; limit = 1000;

// The cron: yesterday's salt and expired marks go, today's stay.
db.prepare("INSERT INTO salts (day, salt) VALUES ('2000-01-01', 'old')").run();
db.prepare("INSERT INTO marks (k, expires) VALUES ('old', 1)").run();
db.prepare("INSERT INTO like_state (k, liked, changes, expires) VALUES ('old', 1, 1, 1)").run();
const marksBefore = db.prepare("SELECT count(*) n FROM marks").get().n;
console.log = (() => { const l = console.log; return (...a) => { if (!String(a[0]).startsWith("stats:")) l(...a); }; })();
await worker.scheduled({}, env, {});
check("cron drops old salts only", db.prepare("SELECT count(*) n FROM salts").get().n === 1 && !db.prepare("SELECT 1 FROM salts WHERE day = '2000-01-01'").get());
check("cron drops expired marks only", db.prepare("SELECT count(*) n FROM marks").get().n === marksBefore - 1);
check("cron drops expired like states", !db.prepare("SELECT 1 FROM like_state WHERE k = 'old'").get() && db.prepare("SELECT count(*) n FROM like_state").get().n > 0);
check("no address in like_state either", db.prepare("SELECT k FROM like_state").all().every(x => /^[0-9a-f]{64}$/.test(x.k)));

process.stdout.write(`\n${pass} passed, ${fail} failed\n`);
process.exit(fail ? 1 : 0);
