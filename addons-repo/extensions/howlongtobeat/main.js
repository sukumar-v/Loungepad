/*
 * HowLongToBeat for Loungepad: how long each game takes to beat, from howlongtobeat.com.
 *
 * The site has no public API. Its own page asks /api/search/site/init for a token and posts the
 * search to /api/search/site with it in an x-auth-token header (October 2026; it has changed
 * shape several times before and will again -- which is the reason this is an extension and not
 * part of the launcher). The token is tied to the User-Agent that asked for it, so every request
 * here sends the same one. A 403 is an expired token and is answered by asking for another once.
 *
 * Matching is strict on purpose: a wrong answer looks exactly like a right one. A result is
 * taken only when its name, or one of the aliases the site lists for it, is the game's title
 * after normalising (case, accents, "&", punctuation, one trailing edition suffix) -- so
 * "Portal" never matches "Portal 2". Two results with the same name are told apart by year.
 */

const SITE = "https://howlongtobeat.com";
const UA = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/130.0.0.0 Safari/537.36";

let token = null;

export async function activate() {
  try { token = (await loungepad.storage.get("token")) || null; } catch { token = null; }
}

/* One game: the stored record, or null for "nothing for this one". */
export async function enrich(game) {
  const title = String(game.title || "").trim();
  if (!title) return null;
  let hit = pickMatch(await search(title), game, loungepad.settings);
  // The site's search wants every word: a dash, a trademark mark or an edition suffix in the
  // store's title ("The Witcher 3: Wild Hunt — Remastered") finds nothing. One more search with
  // the normalised title, for misses only.
  if (!hit) {
    const plain = titleKey(title);
    if (plain && plain !== title.toLowerCase()) hit = pickMatch(await search(plain), game, loungepad.settings);
  }
  if (!hit) return null;
  return {
    id: hit.game_id,
    name: hit.game_name,
    main: hours(hit.comp_main),
    mainExtra: hours(hit.comp_plus),
    completionist: hours(hit.comp_100),
    allStyles: hours(hit.comp_all),
    reviewScore: hit.review_score > 0 ? hit.review_score : null,
    year: hit.release_world > 0 ? hit.release_world : null,
    url: `${SITE}/game/${hit.game_id}`,
  };
}

/* ---- the site ---- */

async function getToken(force) {
  if (token && !force) return token;
  const res = await loungepad.fetch(`${SITE}/api/search/site/init?t=${Date.now()}`, {
    headers: { "User-Agent": UA, "Referer": `${SITE}/`, "Accept": "*/*" },
  });
  if (!res.ok) throw new Error(`token: HTTP ${res.status}`);
  const t = res.json().token;
  if (!t) throw new Error("token: the site answered without one");
  token = t;
  try { await loungepad.storage.set("token", t); } catch { /* kept in memory */ }
  return t;
}

/* The site's own search, as its page sends it. The words of the title as the terms, the site's
   defaults for everything else, twenty results by popularity. */
export async function search(title, retry = true) {
  const t = await getToken(false);
  const payload = {
    searchType: "games",
    searchTerms: title.split(/\s+/).filter(Boolean),
    searchPage: 1,
    size: 20,
    searchOptions: {
      games: {
        userId: 0, platform: "", sortCategory: "popular", rangeCategory: "main",
        rangeTime: { min: 0, max: 0 }, gameplay: { perspective: "", flow: "", genre: "" }, year: "", modifier: "",
      },
      users: { sortCategory: "postcount" },
      lists: { sortCategory: "follows" },
      filter: "", sort: 0, randomizer: 0,
    },
    useCache: true,
  };
  const res = await loungepad.fetch(`${SITE}/api/search/site`, {
    method: "POST",
    headers: {
      "User-Agent": UA, "Referer": `${SITE}/`, "Origin": SITE, "Accept": "*/*",
      "Content-Type": "application/json", "x-auth-token": t,
    },
    body: JSON.stringify(payload),
  });
  if (res.status === 403 && retry) {
    await getToken(true);
    return search(title, false);
  }
  if (res.status === 429) throw new Error("the site is rate-limiting searches; try again in a minute");
  if (!res.ok) throw new Error(`search: HTTP ${res.status}`);
  const data = res.json().data;
  return Array.isArray(data) ? data : [];
}

/* ---- matching ---- */

const EDITIONS = /\s+(game of the year|goty|definitive|deluxe|complete|ultimate|special|collectors?|digital|standard|enhanced|anniversary|legendary|gold|premium|directors? cut|remastered)(\s+edition)?$/;

/* A title as the launcher compares titles: lower case, accents dropped, "&" as "and", trademark
   marks and punctuation gone, one trailing edition suffix discounted. */
export function titleKey(s) {
  let k = String(s || "").normalize("NFD").replace(/[̀-ͯ]/g, "").toLowerCase();
  k = k.replace(/[™®©]/g, " ").replace(/&/g, " and ").replace(/['’]/g, "");
  k = k.replace(/[^a-z0-9]+/g, " ").trim().replace(/\s+/g, " ");
  k = k.replace(/\s+edition$/, "").replace(EDITIONS, "");
  return k.replace(/^the\s+/, "");
}

function aliases(hit) {
  return String(hit.game_alias || "").split(",").map(a => a.trim()).filter(Boolean);
}

/* The one result that IS the game, or null. Exact on the key of the name or an alias; among
   several, the same year as the game's own first, then the most popular. With "match-year" on,
   a result more than two years from the game's date is refused outright. */
export function pickMatch(results, game, settings) {
  const key = titleKey(game.title);
  if (!key) return null;
  const exact = (results || []).filter(h => h && h.game_type !== "dlc"
    && (titleKey(h.game_name) === key || aliases(h).some(a => titleKey(a) === key)));
  if (!exact.length) return null;
  const year = Number(game.year) || null;
  const strict = !!(settings && settings["match-year"]);
  const near = h => year && h.release_world > 0 ? Math.abs(h.release_world - year) : null;
  let pool = exact;
  if (year) {
    const same = exact.filter(h => h.release_world === year);
    if (same.length) pool = same;
    else if (strict) {
      pool = exact.filter(h => { const d = near(h); return d === null || d <= 2; });
      if (!pool.length) return null;
    }
  }
  pool = [...pool].sort((a, b) => (b.profile_popular || 0) - (a.profile_popular || 0));
  return pool[0];
}

/* Seconds to hours at the site's own precision, the nearest half hour. 0 is "no data". */
export function hours(seconds) {
  const s = Number(seconds);
  if (!isFinite(s) || s <= 0) return null;
  return Math.max(0.5, Math.round(s / 1800) / 2);
}
