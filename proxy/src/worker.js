/*
 * Loungepad metadata proxy.
 *
 * Holds the IGDB and SteamGridDB credentials so the launcher does not have to. This is the same
 * shape Playnite uses -- its IGDB plugin ships no keys and talks to api2.playnite.link -- and it
 * exists for the same reason: a desktop binary cannot keep a secret, and Twitch's terms say the
 * client secret must never be exposed to users.
 *
 * Two endpoints, all GET, both about a game and never about a person:
 *
 *   /v1/facts?title=<title>   description, developer, genres, release date, critic score
 *   /v1/art?title=<title>     portrait / tile / hero / logo image URLs
 *
 * Both take an optional appid (a Steam app id) and facts takes an optional platform list; both
 * may answer 404, which means "no confident answer", not "something broke". The launcher treats a
 * 404 and a network failure identically: it keeps whatever art it already had.
 *
 * There used to be two more, /v1/owned and /v1/achievements, which took a SteamID and read that
 * account's library and unlocks with a Steam key held here. They are gone, on both ends: they were
 * the only routes that saw anything identifying, they let anyone with the URL look up any Steam
 * account through this service's key, and the launcher reads both from Steam directly with the
 * user's own sign-in or key now. Nothing this service receives names a user, and nothing it
 * stores is keyed on one.
 *
 * Every request has to carry the launcher's client header (CLIENT_HEADER). It is a speed bump,
 * not a secret -- the value is in the shipped binary -- but it turns away the scrapers and bots
 * that only know the URL before they cost an upstream call or a cache write.
 *
 * The cache is the whole economy of this service. IGDB allows 4 requests a second across the
 * entire credential -- not per user -- so an uncached proxy would fall over the moment more than
 * a handful of people scanned at once. Game metadata is effectively static and libraries overlap
 * enormously (everyone owns Hollow Knight), so a shared cache turns thousands of users into a few
 * thousand upstream requests, once, ever.
 */

const CACHE_TTL = 60 * 60 * 24 * 30;   // 30 days. Game facts do not change; art rarely does.
const MISS_TTL = 60 * 60 * 24 * 3;     // Remember "no match" too, but re-check sooner: a game may
                                       // be added to a database after we first ask for it.
const SCHEMA = "v7";                   // bump when a fetcher changes shape or its picking (v7: the gallery)
                                       // rules; it is part of every cache key, so stale answers retire
const RATE_WINDOW = 60;                // seconds; the MISS_LIMIT period in wrangler.toml, which also
                                       // holds the limit itself
// The header every launcher sends (MetadataProxyClient.ClientHeader on the host side).
const CLIENT_HEADER = "x-loungepad-client";
const CLIENT_VALUE = "1";

export default {
  async fetch(request, env, ctx) {
    const url = new URL(request.url);

    if (request.method !== "GET") return json({ error: "method not allowed" }, 405);
    if (url.pathname === "/v1/health") return json({ ok: true });
    if (request.headers.get(CLIENT_HEADER) !== CLIENT_VALUE) return json({ error: "forbidden" }, 403);

    // The title is quoted into an APIcalypse query (igdbFacts). Quotes, backslashes and control
    // characters are the only things that could change what the query means, and no game is
    // named with them; the launcher's own strict match rejects anything that comes back wrong.
    const title = (url.searchParams.get("title") || "").replace(/["\\\u0000-\u001f\u007f]/g, " ").trim();
    // A Steam app id, when the caller has one. Worth far more than a title: both upstreams can be
    // asked by it directly, so there is no name matching and therefore no way to answer with a
    // different game. The title is still sent alongside as the fallback.
    const appid = (url.searchParams.get("appid") || "").trim();
    // IGDB platform ids, for a ROM: the launcher knows which system's folder the file came from,
    // and confining the title search to it is what separates "Doom" (1993, on the SNES) from
    // "DOOM" (2016). Only the facts lookup uses it; SteamGridDB has no platform filter.
    const platform = (url.searchParams.get("platform") || "").trim();
    if (!title && !appid) return json({ error: "title or appid is required" }, 400);
    if (title.length > 200) return json({ error: "title too long" }, 400);
    if (appid && !/^\d{1,10}$/.test(appid)) return json({ error: "appid must be a number" }, 400);
    if (platform && !/^\d{1,6}(,\d{1,6}){0,7}$/.test(platform)) return json({ error: "platform must be numeric ids" }, 400);

    try {
      if (url.pathname === "/v1/facts") return await serve(env, ctx, "facts", title, appid, igdbFacts, request, platform);
      if (url.pathname === "/v1/art") return await serve(env, ctx, "art", title, appid, gridArt, request, "");
      return json({ error: "not found" }, 404);
    } catch (err) {
      // Never leak an upstream error body: it can carry our own credentials back to the caller.
      console.error(`${url.pathname} "${title || appid}": ${err && err.message}`);
      return json({ error: "upstream failed" }, 502);
    }
  },

  // The daily cron in wrangler.toml. KV forgot an expired entry by itself; D1 keeps a row until it
  // is deleted. Reads already refuse an expired row, so this is only about the table's size.
  async scheduled(event, env, ctx) {
    const res = await env.CACHE.prepare("DELETE FROM cache WHERE expires <= ?1").bind(nowSeconds()).run();
    console.log(`cache: removed ${res.meta.changes} expired entries`);
  },
};

/* ---------------------------------------------------------------- serving */

/**
 * Cache-first. A hit costs one row read and no upstream call at all, which is what keeps this
 * inside both IGDB's rate limit and a free hosting tier.
 *
 * Keyed on the app id when there is one. Two people who own the same game on Steam share a cache
 * entry even if their launchers spell the title differently, and the entry cannot be poisoned by
 * a near-miss title.
 */
async function serve(env, ctx, kind, title, appid, fetcher, request, platform) {
  // A platform-confined search is a different question with a possibly different answer, so it
  // gets a key of its own; the unconfined key is untouched and needs no schema bump.
  const key = appid
    ? `${kind}:${SCHEMA}:steam:${appid}`
    : `${kind}:${SCHEMA}:${normalise(title)}${platform ? `:p${platform}` : ""}`;

  const cached = await cacheGet(env, key);
  if (cached) {
    return cached.miss
      ? json({ error: "no match" }, 404, { "X-Cache": "HIT" })
      : json(cached.data, 200, { "X-Cache": "HIT" });
  }

  // Rate limited only from here down, where a request is actually about to cost an upstream call.
  // The limit exists to protect IGDB's budget, and a cache hit does not touch it.
  if (await rateLimited(request, env))
    return json({ error: "slow down" }, 429, { "Retry-After": String(RATE_WINDOW) });

  const data = await fetcher(env, title, appid, platform);

  // Written after the response is on its way, so a cache write never delays the caller.
  ctx.waitUntil(cachePut(env, key, data ? { data } : { miss: true }, data ? CACHE_TTL : MISS_TTL));

  return data
    ? json(data, 200, { "X-Cache": "MISS" })
    : json({ error: "no match" }, 404, { "X-Cache": "MISS" });
}

/* ------------------------------------------------------------------ cache */

/*
 * The cache is a D1 table (migrations/0001_cache.sql), not KV, since Oct 8 2026. KV's free tier
 * allows 1,000 writes a day, and every lookup of something new cost two: the answer, and the rate
 * counter that used to live beside it. A game is two lookups, so 250 new games spent the day, and
 * on Oct 7-8 one launcher's pass over a ROM collection several thousand strong ran for five hours,
 * keeping nothing it learned after the first six minutes. D1 writes are a hundred times that
 * allowance (and a write here is one row: the table is WITHOUT ROWID, so there is no index to write
 * as well), and the counter is Cloudflare's now (rateLimited).
 *
 * The answers KV already holds are still read when D1 has no row for a key, so the move threw
 * nothing away. KV is never written any more; its entries expire within 30 days of the last write,
 * and after Nov 8 2026 the fallback finds nothing and can be deleted with the binding.
 *
 * A failed read is a miss, not an outage: the answer is one upstream call away, and the rate limit
 * is what keeps that from costing IGDB's budget. A failed write is logged and the answer in hand is
 * returned anyway.
 */
const nowSeconds = () => Math.floor(Date.now() / 1000);

async function cacheGet(env, key) {
  try {
    const row = await env.CACHE.prepare("SELECT value, expires FROM cache WHERE key = ?1").bind(key).first();
    // An expired row is a miss, not a reason to look in KV: the KV entry for the key is older.
    if (row) return row.expires > nowSeconds() ? JSON.parse(row.value) : null;
  } catch (err) {
    console.error(`cache: read failed: ${err && err.message}`);
  }
  try {
    return await env.METADATA.get(key, { type: "json" });
  } catch (err) {
    console.error(`kv: read failed: ${err && err.message}`);
    return null;
  }
}

async function cachePut(env, key, value, ttl) {
  try {
    await env.CACHE.prepare(
      "INSERT INTO cache (key, value, expires) VALUES (?1, ?2, ?3) " +
      "ON CONFLICT (key) DO UPDATE SET value = excluded.value, expires = excluded.expires",
    ).bind(key, JSON.stringify(value), nowSeconds() + ttl).run();
  } catch (err) {
    console.error(`cache: write failed: ${err && err.message}`);
  }
}

/*
 * Only for the cache key, and deliberately looser than the launcher's own matching: it just has to
 * make "Hollow Knight" and "hollow  knight" share a cache entry. The launcher re-checks the name
 * that comes back against its own strict rule before it accepts anything, so a sloppy key here
 * cannot put the wrong game's art on a tile.
 */
function normalise(title) {
  return title.toLowerCase().normalize("NFD")
    .replace(/[\u0300-\u036f]/g, "")
    .replace(/['\u2018\u2019`]/g, "")
    .replace(/[^a-z0-9]+/g, " ")
    .trim();
}

/* ------------------------------------------------------------------- IGDB */

const BASE_FIELDS =
  "name, summary, first_release_date, aggregated_rating, category, " +
  "follows, total_rating_count, version_parent, " +
  "genres.name, cover.image_id, artworks.image_id, videos.name, videos.video_id, screenshots.image_id, " +
  "involved_companies.developer, involved_companies.publisher, involved_companies.company.name";

/*
 * Age ratings, in the shapes IGDB has used, newest first.
 *
 * They moved this from a pair of numeric enums (category/rating) to references
 * (organization/rating_category), and a shipped worker cannot know which is live -- APIcalypse
 * fails the WHOLE query with a 400 for one unknown field, so guessing wrong would cost every
 * game's description and score, not just its rating. So the shapes are tried in order, a 400
 * steps down to the next, and the one that worked is remembered for the life of the isolate.
 * The last entry asks for no age fields at all and therefore always works.
 *
 * The modern shape is asked for by name rather than by id: "PEGI" and "Sixteen" survive IGDB
 * renumbering its enums, where a 2 and a 4 do not.
 */
const AGE_SHAPES = [
  ", age_ratings.organization.name, age_ratings.rating_category.rating",
  ", age_ratings.category, age_ratings.rating",
  "",
];
let ageShape = 0;

const fieldsFor = (i) => `fields ${BASE_FIELDS}${AGE_SHAPES[i]}; `;

async function igdbFacts(env, title, appid, platform) {
  const token = await igdbToken(env);
  if (!token) return null;

  // By app id first. IGDB records a game's storefront ids in external_games, category 1 being
  // Steam, so this is an exact lookup with no title involved -- the same guarantee the launcher
  // gets from Steam itself, which is what makes it safe to ask this service first.
  if (appid) {
    const byId = await igdbGames(env, token,
      `where external_games.category = 1 & external_games.uid = "${appid}";`, 5);
    const main = (byId || []).filter(g => g.version_parent === undefined);
    if (main.length) return shape(main.sort(popularityFirst)[0]);
  }

  if (!title) return null;

  // "(a, b)" is APIcalypse for "released on any of these", which is the right question for a
  // ROM: the SNES folder's "Doom" is whichever DOOM has an SNES release.
  const where = platform ? ` where platforms = (${platform});` : "";
  const all = await igdbGames(env, token, `search "${title}";${where}`, 20);
  if (!all) return null;

  // category 0 is a main game. The rest are DLC, bundles, ports and episodes, which share their
  // parent's title and would otherwise win the match on a coin toss. version_parent marks an
  // edition or regional variant of another entry; those inherit their parent's title too.
  const games = all.filter(g =>
    (g.category === undefined || g.category === 0) && g.version_parent === undefined);
  const hit = pick(title, games, g => g.name);
  return hit ? shape(hit) : null;
}

/**
 * One /games query, retried down the age-rating shapes when IGDB rejects an age-rating field.
 *
 * Only a 400 whose body names an age_ratings field steps down: that is IGDB saying "I do not know
 * that field", and it is the only failure another shape can fix. A 400 for any other reason -- a
 * title it will not take, some other field -- used to step down too, which walked the isolate to
 * the last shape (no age fields at all) and latched it there, and every answer it then cached
 * carried no rating for 30 days. Now that 400 is thrown like any other upstream failure, so it is
 * a 502 for that one request and is never cached. The isolate is still latched once a shape is
 * found; a fresh isolate starts from the top.
 */
async function igdbGames(env, token, clause, limit) {
  for (let i = ageShape; i < AGE_SHAPES.length; i++) {
    const res = await igdbQuery(env, token, `${clause} ${fieldsFor(i)} limit ${limit};`);
    if (res.ok) {
      if (i !== ageShape) {
        console.log(`igdb: age-rating fields fell back to shape ${i}`);
        ageShape = i;
      }
      return res.json();
    }
    if (res.status !== 400) throw new Error(`igdb ${res.status}`);
    const body = await res.text().catch(() => "");
    if (!/age_ratings/i.test(body) || AGE_SHAPES[i] === "") throw new Error("igdb 400");
  }
  throw new Error("igdb 400");
}

async function igdbQuery(env, token, body) {
  return fetch("https://api.igdb.com/v4/games", {
    method: "POST",
    headers: {
      "Client-ID": env.IGDB_CLIENT_ID,
      "Authorization": `Bearer ${token}`,
      "Content-Type": "text/plain",
    },
    body,
  });
}

function shape(hit) {
  const companies = hit.involved_companies || [];
  const named = (flag) => {
    const c = companies.find(x => x[flag] && x.company && x.company.name);
    return c ? c.company.name : null;
  };

  return {
    name: hit.name,
    summary: hit.summary || null,
    developer: named("developer"),
    publisher: named("publisher"),
    genres: (hit.genres || []).map(g => g.name).filter(Boolean),
    released: hit.first_release_date
      ? new Date(hit.first_release_date * 1000).toISOString().slice(0, 10)
      : null,
    criticScore: typeof hit.aggregated_rating === "number"
      ? Math.round(hit.aggregated_rating) : null,
    pegi: pegiOf(hit),
    cover: hit.cover && hit.cover.image_id ? igdbImage(hit.cover.image_id, "cover_big_2x") : null,
    artwork: hit.artworks && hit.artworks.length && hit.artworks[0].image_id
      ? igdbImage(hit.artworks[0].image_id, "1080p") : null,
    video: videoOf(hit),
    // The gallery on the game's page: every video, and the screenshots at 1080p.
    videos: (hit.videos || [])
      .filter(v => v && typeof v.video_id === "string" && v.video_id)
      .slice(0, 6)
      .map(v => ({ id: v.video_id, name: v.name || null })),
    screenshots: (hit.screenshots || [])
      .filter(s => s && s.image_id)
      .slice(0, 12)
      .map(s => igdbImage(s.image_id, "1080p")),
  };
}

/*
 * The YouTube id of the game's trailer, or null. IGDB keeps videos as YouTube ids -- there is no
 * file to serve -- so the launcher plays them through YouTube's embedded player, and only for
 * games Steam has no trailer of its own for. The one named as a trailer is preferred; failing
 * that, the first listed.
 */
function videoOf(hit) {
  const list = (hit.videos || []).filter(v => v && typeof v.video_id === "string" && v.video_id);
  if (!list.length) return null;
  const named = list.find(v => /trailer/i.test(v.name || ""));
  return (named || list[0]).video_id;
}

/*
 * The PEGI age, 3/7/12/16/18, or null. A game can carry a rating from half a dozen boards -- ESRB,
 * CERO, USK, ACB -- so the board has to be identified before the number means anything; an ESRB
 * "M" and a PEGI "16" sit in the same list.
 *
 * Both field shapes are read, because which one arrived depends on which AGE_SHAPES variant IGDB
 * accepted. The legacy enums are the documented v4 ones: category 2 is PEGI, and ratings 1 to 5
 * are Three, Seven, Twelve, Sixteen and Eighteen in order.
 */
const PEGI_NAMES = { three: 3, seven: 7, twelve: 12, sixteen: 16, eighteen: 18 };
const PEGI_LEGACY = { 1: 3, 2: 7, 3: 12, 4: 16, 5: 18 };

function pegiOf(hit) {
  for (const r of hit.age_ratings || []) {
    const org = r.organization && r.organization.name;
    if (org) {
      if (!/pegi/i.test(org)) continue;
      const name = r.rating_category && r.rating_category.rating;
      const age = PEGI_NAMES[String(name || "").toLowerCase()];
      if (age) return age;
    } else if (r.category === 2) {
      const age = PEGI_LEGACY[r.rating];
      if (age) return age;
    }
  }
  return null;
}

const igdbImage = (id, size) => `https://images.igdb.com/igdb/image/upload/t_${size}/${id}.jpg`;

/**
 * Twitch client-credentials token, cached against its own stated lifetime. Without the cache every
 * cold worker would mint a fresh token, and Twitch rate limits that too. (KV kept it as a bare
 * string under "igdb:token", which the JSON read of the fallback cannot take, so it is under a new
 * key here and the first request after the move mints one.)
 */
async function igdbToken(env) {
  const cached = await cacheGet(env, "twitch:token");
  if (cached) return cached;

  const res = await fetch("https://id.twitch.tv/oauth2/token", {
    method: "POST",
    headers: { "Content-Type": "application/x-www-form-urlencoded" },
    body: new URLSearchParams({
      client_id: env.IGDB_CLIENT_ID,
      client_secret: env.IGDB_CLIENT_SECRET,
      grant_type: "client_credentials",
    }),
  });
  if (!res.ok) throw new Error(`twitch token ${res.status}`);

  const data = await res.json();
  if (!data.access_token) throw new Error("twitch token missing");

  // A minute early, so a token cannot expire between our check and IGDB's. The token is in hand
  // whether or not the write takes, so a refused one is not a failure (cachePut logs it).
  await cachePut(env, "twitch:token", data.access_token, Math.max(60, (data.expires_in || 3600) - 60));
  return data.access_token;
}

/* ------------------------------------------------------------ SteamGridDB */

async function gridArt(env, title, appid) {
  // SteamGridDB indexes by Steam app id directly, so when the launcher has one there is no search
  // and no matching -- the art is definitionally the right game's.
  let id = null;
  let name = null;
  if (appid) {
    const g = await sgdb(env, `/games/steam/${appid}`);
    if (g && g.data && g.data.id) { id = g.data.id; name = g.data.name || null; }
  }

  if (id === null) {
    if (!title) return null;
    const search = await sgdb(env, `/search/autocomplete/${encodeURIComponent(title)}`);
    if (!search || !Array.isArray(search.data)) return null;
    const hit = pick(title, search.data, g => g.name);
    if (!hit || !hit.id) return null;
    id = hit.id;
    name = hit.name;
  }

  // Each shape is independent: a game with no art at a given size answers empty, which is normal.
  const [portrait, tile, hero, logo] = await Promise.all([
    firstUrl(env, `/grids/game/${id}?dimensions=600x900`),
    firstUrl(env, `/grids/game/${id}?dimensions=920x430,460x215`),
    firstUrl(env, `/heroes/game/${id}`),
    firstUrl(env, `/logos/game/${id}`),
  ]);

  if (!portrait && !tile && !hero && !logo) return null;
  return { name, portrait, tile, hero, logo };
}

async function firstUrl(env, path) {
  const body = await sgdb(env, path);
  if (!body || !Array.isArray(body.data)) return null;
  const found = body.data.find(x => typeof x.url === "string");
  return found ? found.url : null;
}

async function sgdb(env, path) {
  const res = await fetch(`https://www.steamgriddb.com/api/v2${path}`, {
    headers: { Authorization: `Bearer ${env.SGDB_KEY}` },
  });
  if (res.status === 401) throw new Error("sgdb key rejected");
  if (!res.ok) return null;   // 404 means "nothing of that shape", which is not an error
  return res.json();
}

/* ------------------------------------------------------------- shared bits */

/**
 * Exact-after-normalising, preferring an exact hit. This mirrors the launcher's rule so the proxy
 * does not waste a cache slot on something the client will reject anyway -- but the client checks
 * again regardless, and its check is the one that counts.
 *
 * Ties are the interesting case and are NOT harmless. IGDB carries several entries named exactly
 * "Fortnite" (the game, and the delisted Chinese version published by Tencent) and two named
 * exactly "DOOM" (1993 and 2016). Taking whichever came back first gave Fortnite the wrong
 * developer and a summary about a regional variant. Neither the proxy's title check nor the
 * launcher's can see this -- the names really are identical -- so the tie has to be broken on
 * something else, and popularity picks the canonical entry every time.
 */
function pick(wanted, candidates, nameOf) {
  const want = normalise(wanted);
  if (!want) return null;
  const exact = candidates.filter(c => normalise(nameOf(c) || "") === want);
  if (exact.length === 0) return null;
  return exact.sort(popularityFirst)[0];
}

const weight = (g) => (g.follows || 0) * 10 + (g.total_rating_count || 0);
const popularityFirst = (a, b) => weight(b) - weight(a);

/**
 * A crude per-IP cap. Not a security boundary -- an IP is cheap to change -- just enough that one
 * broken client cannot burn the whole IGDB budget for everyone else.
 *
 * Counted by Cloudflare's rate limiting binding (MISS_LIMIT in wrangler.toml), which costs no
 * writes and keeps its counts per Cloudflare location. It used to be a KV key per address and
 * minute, one write per miss, and a KV that refused the write let the request through -- so once
 * the day's writes were spent the limit was off for everyone, exactly when one client was busiest.
 *
 * The key is a hash of the address, not the address: a client's IP is personal data and is not
 * handed to anything that keeps it. A binding that throws lets the request through rather than
 * failing it, as the counter did: a limit is bookkeeping, not a reason for a 502.
 */
async function rateLimited(request, env) {
  const ip = request.headers.get("CF-Connecting-IP") || "unknown";
  try {
    const { success } = await env.MISS_LIMIT.limit({ key: await sha256Hex(ip) });
    return !success;
  } catch (err) {
    console.error(`ratelimit: skipped: ${err && err.message}`);
    return false;
  }
}

async function sha256Hex(text) {
  const digest = await crypto.subtle.digest("SHA-256", new TextEncoder().encode(text));
  return [...new Uint8Array(digest)].map(b => b.toString(16).padStart(2, "0")).join("");
}

function json(body, status = 200, headers = {}) {
  // A hit and a confident miss are both answers about a game and may sit in any cache for as long
  // as ours keeps them. A refusal, a rate limit or an upstream failure is about this moment and
  // must not be held by a shared cache on the way: a 502 cached for an hour is an hour's outage.
  const cache = status === 200 ? `public, max-age=${CACHE_TTL}`
    : status === 404 ? `public, max-age=${MISS_TTL}`
    : "no-store";
  return new Response(JSON.stringify(body), {
    status,
    headers: {
      "Content-Type": "application/json; charset=utf-8",
      "Cache-Control": cache,
      ...headers,
    },
  });
}
