/*
 * Exercises proxy/src/worker.js without Cloudflare.
 *
 * The worker runs in Node against a real SQLite table made by the migration as written (D1 is
 * SQLite), a fake KV that throws if anything writes to it, a fake rate-limit binding, and faked
 * Twitch, IGDB and SteamGridDB, so nothing leaves the machine. It checks the cache (hits, misses,
 * expiry, the KV fallback), the rate limit, failing storage and the daily cron.
 *
 *   node tools\proxy-harness.mjs
 *
 * Needs Node 22.13 or newer (node:sqlite without a flag). It cannot check D1's own SQL dialect:
 * run `npx wrangler dev --local --test-scheduled` in proxy\ for that.
 */
import { DatabaseSync } from "node:sqlite";
import { readFileSync } from "node:fs";
import { fileURLToPath, pathToFileURL } from "node:url";
import path from "node:path";

const proxy = path.join(path.dirname(fileURLToPath(import.meta.url)), "..", "proxy");
const worker = (await import(pathToFileURL(path.join(proxy, "src", "worker.js")).href)).default;

const db = new DatabaseSync(":memory:");
db.exec(readFileSync(path.join(proxy, "migrations", "0001_cache.sql"), "utf8"));

const trace = { d1Writes: 0, d1ReadFail: false, d1WriteFail: false, limiterThrows: false, sgdbUrls: [] };
const D1 = {
  prepare(sql) {
    // node:sqlite binds an array only to plain ?, and takes D1's ?1 ?2 for names; the worker uses each
    // number once and in order, so plain ? is the same statement. wrangler dev checks the real syntax.
    const stmt = db.prepare(sql.replace(/\?\d+/g, "?"));
    return {
      bind: (...a) => ({
        first: async () => { if (trace.d1ReadFail) throw new Error("d1 down"); return stmt.get(...a) ?? null; },
        run: async () => {
          if (trace.d1WriteFail) throw new Error("d1 down");
          if (/^\s*INSERT/i.test(sql)) trace.d1Writes++;
          const r = stmt.run(...a);
          return { meta: { changes: Number(r.changes) } };
        },
      }),
    };
  },
};
const kv = new Map();
const KV = {
  async get(key, opts) { const v = kv.get(key); if (v === undefined) return null; return opts?.type === "json" ? JSON.parse(v) : v; },
  async put() { throw new Error("KV must never be written"); },
};
let limit = 1000; const counts = new Map();
const LIMITER = {
  async limit({ key }) {
    if (trace.limiterThrows) throw new Error("binding down");
    if (!/^[0-9a-f]{64}$/.test(key)) throw new Error(`limiter key is not a hash: ${key}`);
    const n = (counts.get(key) || 0) + 1; counts.set(key, n);
    return { success: n <= limit };
  },
};
const env = { CACHE: D1, METADATA: KV, MISS_LIMIT: LIMITER, IGDB_CLIENT_ID: "id", IGDB_CLIENT_SECRET: "secret", SGDB_KEY: "k" };

const upstream = { twitch: 0, igdb: 0, sgdb: 0 };
globalThis.fetch = async (url, init = {}) => {
  const u = String(url);
  const body = (b, s = 200) => new Response(JSON.stringify(b), { status: s, headers: { "Content-Type": "application/json" } });
  if (u.startsWith("https://id.twitch.tv/")) { upstream.twitch++; return body({ access_token: "tok", expires_in: 5000000 }); }
  if (u.startsWith("https://api.igdb.com/")) {
    if (init.headers && init.headers.Authorization === "Bearer stale") { upstream.igdb++; return body({ message: "invalid token" }, 401); }
    upstream.igdb++;
    const q = String(init.body);
    const m = q.match(/search "([^"]+)"/);
    return body(m && /^(Known Game|Other Game|Stale Game)$/.test(m[1]) ? [{ name: m[1], category: 0, summary: "s" }] : []);
  }
  if (u.startsWith("https://www.steamgriddb.com/")) {
    upstream.sgdb++;
    if (u.includes("/search/autocomplete/")) return body({ data: u.includes("Known%20Game") ? [{ id: 7, name: "Known Game" }] : [] });
    if (u.includes("/games/steam/")) return body(u.endsWith("/440") ? { data: { id: 9, name: "Steam Game" } } : { success: false }, u.endsWith("/440") ? 200 : 404);
    trace.sgdbUrls.push(u);
    if (u.includes("dimensions=512x512") && u.includes("/game/9?")) return body({ data: [] });
    return body({ data: [{ url: "https://img.example/a.png" }] });
  }
  throw new Error(`unexpected fetch ${u}`);
};

let ip = "203.0.113.5";
async function get(p, headers = { "X-Loungepad-Client": "1" }) {
  const pending = [];
  const res = await worker.fetch(new Request(`https://w.example${p}`, { headers: { ...headers, "CF-Connecting-IP": ip } }), env, { waitUntil: (x) => pending.push(x) });
  await Promise.all(pending);
  return { status: res.status, cache: res.headers.get("X-Cache"), retry: res.headers.get("Retry-After"), body: await res.json() };
}
const row = (key) => db.prepare("SELECT value, expires FROM cache WHERE key = ?").get(key);
const now = () => Math.floor(Date.now() / 1000);
const D = 86400;

let pass = 0, fail = 0;
const check = (name, ok, extra = "") => { ok ? pass++ : fail++; console.log(`${ok ? "ok  " : "FAIL"} ${name}${extra ? `  (${extra})` : ""}`); };
const quietErrors = []; console.error = (...a) => quietErrors.push(a.join(" "));

// Routing and the client header.
check("health", (await get("/v1/health", {})).status === 200);
check("no client header is 403", (await get("/v1/facts?title=Known%20Game", {})).status === 403);

// A found answer: fetched once, written once as one row, then served from D1.
let r = await get("/v1/facts?title=Known%20Game");
check("first ask is a MISS 200", r.status === 200 && r.cache === "MISS" && r.body.name === "Known Game");
let k = row("facts:v7:known game");
check("answer stored with a 30-day expiry", k && Math.abs(k.expires - (now() + 30 * D)) < 5, k && `${k.expires - now()}s`);
check("one row written for the answer, plus the token", trace.d1Writes === 2, `writes=${trace.d1Writes}`);
check("token stored in D1 under its client id", !!row("twitch:token:id"));
const before = { ...upstream };
r = await get("/v1/facts?title=known%20%20GAME");
check("second ask is a HIT with no upstream call", r.status === 200 && r.cache === "HIT" && upstream.igdb === before.igdb && upstream.twitch === before.twitch);

// The token is reused from D1 for the next miss.
await get("/v1/facts?title=Other%20Game");
check("next miss reuses the stored token", upstream.twitch === 1, `twitch=${upstream.twitch}`);

// A confident miss: stored for 3 days, then served as a 404 HIT.
r = await get("/v1/facts?title=Nobody%20Has%20This");
check("unknown title is a 404 MISS", r.status === 404 && r.cache === "MISS");
k = row("facts:v7:nobody has this");
check("miss stored with a 3-day expiry", k && JSON.parse(k.value).miss === true && Math.abs(k.expires - (now() + 3 * D)) < 5);
r = await get("/v1/facts?title=Nobody%20Has%20This");
check("miss served as a 404 HIT", r.status === 404 && r.cache === "HIT");

// An expired row is never served; the refetch replaces it in place.
db.prepare("INSERT INTO cache (key, value, expires) VALUES (?, ?, ?)").run("facts:v7:stale game", JSON.stringify({ data: { name: "Old" } }), now() - 10);
kv.set("facts:v7:stale game", JSON.stringify({ data: { name: "From KV" } }));
let igdb = upstream.igdb;
r = await get("/v1/facts?title=Stale%20Game");
check("expired row is a MISS, KV not consulted", r.status === 200 && r.cache === "MISS" && r.body.name === "Stale Game" && upstream.igdb > igdb);
check("expired row replaced in place", db.prepare("SELECT count(*) n FROM cache WHERE key = ?").get("facts:v7:stale game").n === 1 && row("facts:v7:stale game").expires > now());

// The KV fallback: an answer only KV has is served, and not copied.
kv.set("facts:v7:legacy game", JSON.stringify({ data: { name: "Legacy Game" } }));
kv.set("facts:v7:legacy miss", JSON.stringify({ miss: true }));
igdb = upstream.igdb; const writes = trace.d1Writes;
r = await get("/v1/facts?title=Legacy%20Game");
check("KV-only answer served as a HIT", r.status === 200 && r.cache === "HIT" && r.body.name === "Legacy Game" && upstream.igdb === igdb);
r = await get("/v1/facts?title=Legacy%20Miss");
check("KV-only miss served as a 404 HIT", r.status === 404 && r.cache === "HIT");
check("nothing written for a KV hit", trace.d1Writes === writes);
// The old token, a bare string in KV, does not break anything: it is under another key now.
kv.set("igdb:token", "bare-token-string");

// Art goes through the same cache.
r = await get("/v1/art?title=Known%20Game");
check("art MISS", r.status === 200 && r.cache === "MISS" && r.body.portrait === "https://img.example/a.png");
r = await get("/v1/art?title=Known%20Game");
check("art HIT", r.status === 200 && r.cache === "HIT");

// The square: a route of its own, its own cache key, square sizes only, and a game with none is a
// cached 404.
r = await get("/v1/square?title=Known%20Game");
check("square MISS", r.status === 200 && r.cache === "MISS" && r.body.square === "https://img.example/a.png" && r.body.name === "Known Game");
check("square asks for square grids, static and safe", trace.sgdbUrls.some(x => x.endsWith("/grids/game/7?dimensions=512x512,1024x1024&types=static&nsfw=false&humor=false")), trace.sgdbUrls.slice(-1)[0]);
check("square is cached under its own kind", !!row("square:v7:known game") && !!row("art:v7:known game"));
r = await get("/v1/square?title=Known%20Game");
check("square HIT", r.status === 200 && r.cache === "HIT");
r = await get("/v1/square?appid=440&title=Steam%20Game");
check("a game with no square is a 404, cached by app id", r.status === 404 && !!row("square:v7:steam:440"));

// The rate limit: counted on misses only, 429 with Retry-After, hits still served.
ip = "198.51.100.9"; limit = 2;
const a = await get("/v1/facts?title=Rl%20One"), b = await get("/v1/facts?title=Rl%20Two"), c = await get("/v1/facts?title=Rl%20Three");
check("third miss in the window is a 429", a.status === 404 && b.status === 404 && c.status === 429 && c.retry === "60", `${a.status} ${b.status} ${c.status} retry=${c.retry}`);
check("a 429 is not cached", !row("facts:v7:rl three"));
r = await get("/v1/facts?title=Known%20Game");
check("a hit is served while limited", r.status === 200 && r.cache === "HIT");
check("limiter key is a hash, never the address", [...counts.keys()].every(x => !x.includes(ip)));
trace.limiterThrows = true;
r = await get("/v1/facts?title=Rl%20Four");
check("a broken limiter lets the request through", r.status === 404 && quietErrors.some(e => e.includes("ratelimit: skipped")));
trace.limiterThrows = false; limit = 1000;

// Storage failures degrade, never 502.
trace.d1ReadFail = true;
r = await get("/v1/facts?title=Legacy%20Game");
check("D1 read failure falls back to KV", r.status === 200 && r.cache === "HIT" && r.body.name === "Legacy Game");
r = await get("/v1/facts?title=Known%20Game");
check("D1 read failure with no KV entry asks upstream", r.status === 200 && r.cache === "MISS");
trace.d1ReadFail = false; trace.d1WriteFail = true;
r = await get("/v1/facts?title=Write%20Fails");
check("D1 write failure still answers", r.status === 404 && r.cache === "MISS" && quietErrors.some(e => e.includes("cache: write failed")));
trace.d1WriteFail = false;

// The cron: expired rows go, live ones stay.
db.prepare("INSERT INTO cache (key, value, expires) VALUES (?, ?, ?)").run("facts:v7:gone1", "{}", now() - 1);
db.prepare("INSERT INTO cache (key, value, expires) VALUES (?, ?, ?)").run("facts:v7:gone2", "{}", now() - 999);
const live = db.prepare("SELECT count(*) n FROM cache WHERE expires > ?").get(now()).n;
const logs = []; const log = console.log; console.log = (...x) => logs.push(x.join(" "));
await worker.scheduled({ cron: "17 4 * * *" }, env, { waitUntil() {} });
console.log = log;
check("cron removes expired rows only", !row("facts:v7:gone1") && !row("facts:v7:gone2") && db.prepare("SELECT count(*) n FROM cache").get().n === live, logs.join(" "));

// A stored token IGDB refuses (another application's, or revoked): one new token, then the answer.
const tok = row("twitch:token:id");
db.prepare("UPDATE cache SET value = ? WHERE key = ?").run(tok.value.replace(/tok/, "stale"), "twitch:token:id");
const twitchBefore = upstream.twitch;
r = await get("/v1/facts?title=Known%20Game&platform=6");
check("a refused token is replaced once and the lookup answers", r.status === 200 && r.body.name === "Known Game" && upstream.twitch === twitchBefore + 1, `twitch=${upstream.twitch - twitchBefore}`);
check("the new token is stored", !row("twitch:token:id").value.includes("stale"));

console.log(`\n${pass} passed, ${fail} failed`);
process.exit(fail ? 1 : 0);
