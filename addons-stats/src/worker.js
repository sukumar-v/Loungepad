/*
 * Add-on downloads and likes, for Settings → Add-ons (docs/ADDONS.md).
 *
 * Reached only through the metadata service's service binding, at https://api.loungepad.app/v1/addons/*
 * (this worker has no URL of its own). Three routes, all needing the launcher's client header:
 *
 *   GET  /v1/addons/stats                 { counts: { "<kind>:<id>": { downloads, likes } } }
 *   POST /v1/addons/download  {key}       after an install from the repository passed its hashes
 *   POST /v1/addons/like      {key, on}   a like (on: true) or taking one back (on: false)
 *
 * Nothing identifies a person or a PC (the user's rule, Oct 9 2026). The launcher remembers what it
 * liked on its own disk; this end only stops one address from counting twice in a day. That is a
 * "mark": a SHA-256 of the day's random salt, the address, the action and the add-on, kept until
 * the end of the day. The address is never stored, and the daily cron deletes yesterday's salt, so
 * a mark cannot be tied back to an address once its day is over. People behind one address (a
 * household, a mobile network) share one like per add-on per day; that is the price of no ids.
 *
 * Only add-ons the repository's index lists can be counted, so the table cannot be filled with
 * made-up keys. The index is read from raw.githubusercontent.com and kept for ten minutes.
 */

const CLIENT_HEADER = "x-loungepad-client";
const CLIENT_VALUE = "1";
const INDEX_URL = "https://raw.githubusercontent.com/sukumar-v/loungepad-addons/main/index.json";
const INDEX_TTL_MS = 10 * 60 * 1000;
const KEY = /^(theme|extension):[a-z][a-z0-9-]{0,39}$/;
const RATE_WINDOW = 60;   // seconds; WRITE_LIMIT's period in wrangler.toml

export default {
  async fetch(request, env, ctx) {
    const url = new URL(request.url);
    if (request.headers.get(CLIENT_HEADER) !== CLIENT_VALUE) return json({ error: "forbidden" }, 403);
    try {
      if (url.pathname === "/v1/addons/stats" && request.method === "GET") return await stats(env);
      if (url.pathname === "/v1/addons/download" && request.method === "POST") return await count(request, env, "download");
      if (url.pathname === "/v1/addons/like" && request.method === "POST") return await count(request, env, "like");
      return json({ error: "not found" }, 404);
    } catch (err) {
      console.error(`${request.method} ${url.pathname}: ${err && err.message}`);
      return json({ error: "failed" }, 500);
    }
  },

  // Once a day: the marks past their day, and every salt but today's.
  async scheduled(event, env, ctx) {
    const marks = await env.DB.prepare("DELETE FROM marks WHERE expires <= ?1").bind(nowSeconds()).run();
    const salts = await env.DB.prepare("DELETE FROM salts WHERE day <> ?1").bind(today()).run();
    console.log(`stats: removed ${marks.meta.changes} marks and ${salts.meta.changes} old salts`);
  },
};

/* ---------------------------------------------------------------- routes */

async function stats(env) {
  const { results } = await env.DB.prepare("SELECT key, downloads, likes FROM counts").bind().all();
  const counts = {};
  for (const r of results || []) counts[r.key] = { downloads: r.downloads, likes: r.likes };
  // A minute in any cache on the way: the numbers move slowly and the launcher asks at most every
  // few minutes anyway.
  return json({ counts }, 200, { "Cache-Control": "public, max-age=60" });
}

/** A download, or a like taken or given. Returns the add-on's counts after it, and whether this
    one counted ("counted": false when the address already did this today). */
async function count(request, env, kind) {
  const ip = request.headers.get("CF-Connecting-IP");
  // Fail closed: without an address there is nothing to limit by, and a forwarder that dropped the
  // header would otherwise make every request "the same address" or none at all.
  if (!ip) return json({ error: "no client address" }, 400);

  let body;
  try { body = await request.json(); } catch { return json({ error: "body must be JSON" }, 400); }
  const key = typeof body?.key === "string" ? body.key : "";
  if (!KEY.test(key)) return json({ error: "bad key" }, 400);
  const on = kind === "like" ? body.on !== false : true;

  if (await rateLimited(env, ip)) return json({ error: "slow down" }, 429, { "Retry-After": String(RATE_WINDOW) });

  let listed;
  try { listed = await listedKeys(); }
  catch (err) {
    console.error(`index: ${err && err.message}`);
    return json({ error: "the add-ons list is unavailable" }, 503);
  }
  if (!listed.has(key)) return json({ error: "not a listed add-on" }, 404);

  const action = kind === "download" ? "download" : on ? "like" : "unlike";
  const salt = await daySalt(env);
  const mark = await sha256Hex(`${salt}|${ip}|${action}|${key}`);
  const fresh = await env.DB.prepare("INSERT OR IGNORE INTO marks (k, expires) VALUES (?1, ?2)").bind(mark, endOfDay()).run();
  const counted = fresh.meta.changes > 0;

  if (counted) {
    const sql = action === "download"
      ? "INSERT INTO counts (key, downloads, likes) VALUES (?1, 1, 0) ON CONFLICT(key) DO UPDATE SET downloads = downloads + 1"
      : action === "like"
        ? "INSERT INTO counts (key, downloads, likes) VALUES (?1, 0, 1) ON CONFLICT(key) DO UPDATE SET likes = likes + 1"
        // Never below zero: nothing here can tell an unlike from someone who never liked.
        : "INSERT INTO counts (key, downloads, likes) VALUES (?1, 0, 0) ON CONFLICT(key) DO UPDATE SET likes = MAX(0, likes - 1)";
    await env.DB.prepare(sql).bind(key).run();
  }
  const row = await env.DB.prepare("SELECT downloads, likes FROM counts WHERE key = ?1").bind(key).first();
  return json({ key, counted, downloads: row?.downloads ?? 0, likes: row?.likes ?? 0 });
}

/* ------------------------------------------------------------- the index */

let indexCache = { at: 0, keys: null };

async function listedKeys() {
  if (indexCache.keys && Date.now() - indexCache.at < INDEX_TTL_MS) return indexCache.keys;
  const res = await fetch(INDEX_URL, { cf: { cacheTtl: 300, cacheEverything: true } });
  if (!res.ok) throw new Error(`HTTP ${res.status}`);
  const index = await res.json();
  const keys = new Set((index.addons || []).filter(a => a && typeof a.kind === "string" && typeof a.id === "string").map(a => `${a.kind}:${a.id}`));
  indexCache = { at: Date.now(), keys };
  return keys;
}

/** For the harness: forget the cached index. */
export function resetIndexCache() { indexCache = { at: 0, keys: null }; }

/* ------------------------------------------------------------ the limits */

async function rateLimited(env, ip) {
  try {
    const { success } = await env.WRITE_LIMIT.limit({ key: await sha256Hex(ip) });
    return !success;
  } catch (err) {
    // The marks still stop double counting; the limiter is the second line.
    console.error(`ratelimit: skipped: ${err && err.message}`);
    return false;
  }
}

async function daySalt(env) {
  const day = today();
  const fresh = crypto.getRandomValues(new Uint8Array(32));
  const hex = [...fresh].map(b => b.toString(16).padStart(2, "0")).join("");
  // Made by whichever request is first today; every other one reads it.
  await env.DB.prepare("INSERT OR IGNORE INTO salts (day, salt) VALUES (?1, ?2)").bind(day, hex).run();
  const row = await env.DB.prepare("SELECT salt FROM salts WHERE day = ?1").bind(day).first();
  return row.salt;
}

/* --------------------------------------------------------------- helpers */

function json(body, status = 200, headers = {}) {
  return new Response(JSON.stringify(body), {
    status,
    headers: { "Content-Type": "application/json; charset=utf-8", "Cache-Control": "no-store", ...headers },
  });
}

async function sha256Hex(s) {
  const d = await crypto.subtle.digest("SHA-256", new TextEncoder().encode(s));
  return [...new Uint8Array(d)].map(b => b.toString(16).padStart(2, "0")).join("");
}

const nowSeconds = () => Math.floor(Date.now() / 1000);
const today = () => new Date().toISOString().slice(0, 10);
function endOfDay() {
  const d = new Date();
  return Math.floor(Date.UTC(d.getUTCFullYear(), d.getUTCMonth(), d.getUTCDate() + 1) / 1000);
}
