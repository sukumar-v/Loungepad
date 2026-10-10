/* ============================================================================
   What a theme can bind to, and what it can make happen
   ============================================================================

   theme.js is the template language; this file is the app's side of the contract. Three things,
   all documented for theme authors in docs/THEMES.md -- keep the two in step:

   1. THE MODELS. gameModel(g) is everything about a game a template can print: the art, the
      store's facts, the score and the age rating, playtime, achievements down to the recent and
      rarest unlocks, the gallery, and what every extension stored. Expensive parts are getters
      that run on first use, so a grid tile that prints a title and a cover pays for a title and
      a cover. libraryModel() is the library itself: its counts and the tabs of its game list.

   2. THE ACTIONS. Any element in a template with data-act="<name>" from THEME_ACTS becomes a real
      button: highlightable, pressed with A or a click, Y for the game's menu when it stands for a
      game. There is no other way for a theme to make something happen, and nothing here takes a
      value a theme could use to reach further than these names.

   3. THE LIVE REGIONS. <div data-render="<template>" data-model="game|library"> in a screen
      template is filled from that item template and kept filled: on the library with the game
      under the highlight, on a game's page with that game. That is how a theme draws a whole
      game hub -- logo, Play, trophies, a row of media -- under a row of tiles, and how a game's
      page becomes a layout of the theme's own.

   The library's two pages (the home and the game list, with its tabs) are state on the screen:
   #screen-library[data-library-page="home"|"games"] and [data-library-tab]. A theme lays the
   list out where it likes and shows it for "games"; data-act="library" opens it.
   ============================================================================ */

/* ---------------------------------------------------------------- models -- */

/** A field computed on first read, then kept on the object like any other. */
function lazy(obj, name, fn) {
  Object.defineProperty(obj, name, {
    configurable: true, enumerable: true,
    get() { const v = fn(); Object.defineProperty(obj, name, { value: v, enumerable: true }); return v; },
  });
}

/**
 * Everything about one game, for a template. `live` is set for the game a live region draws:
 * only that one may ask the host for what the page does not have yet (a gallery, the recent and
 * rarest achievements), or a template that printed {{hasMedia}} on every tile would ask for
 * hundreds of them.
 */
function gameModel(g, opts) {
  const live = !!(opts && opts.live);
  const m = {
    id: g.id, title: g.title, platform: g.platform, emulated: !!g.emulated,
    installed: !!g.installed, favorite: !!g.favorite, hidden: !!g.hidden,
    running: !!(S.gameRunning && S.runningGameId === g.id),
    cover: coverUrl(g) || "", banner: bannerUrl(g) || "",
    hero: heroUrl(g) || "", logo: logoUrl(g) || "",
    backdrop: backdropUrl(g) || "",
    initials: initials(g.title),
    playtimeMinutes: g.playtimeMinutes || 0, sizeBytes: g.sizeBytes || 0,
    playtime: fmtPlaytime(g.playtimeMinutes),
    played: (g.playtimeMinutes || 0) > 0,
    lastPlayed: fmtLastPlayed(g.lastPlayed),
    size: fmtSize(g.sizeBytes),
    sessions: g.sessions || 0,
    meta: g.installed ? shortMeta(g) : uninstalledMeta(g),
    playLabel: playLabel(g),
    // Fetched metadata. Empty string rather than undefined, so a template that prints one of
    // these for a game we know nothing about leaves a gap instead of the word "undefined".
    description: g.description || "",
    developer: g.developer || "",
    publisher: g.publisher || "",
    genres: (g.genres || []).join(", "),
    releaseDate: g.releaseDate || "",
    year: releaseYear(g.releaseDate) || "",
    score: typeof g.criticScore === "number" ? String(Math.round(g.criticScore)) : "",
    scoreBand: typeof g.criticScore === "number" ? scoreBand(g.criticScore) : "",
    pegi: typeof g.pegiRating === "number" ? String(g.pegiRating) : "",
    // The tile fields from before there was a model: the share as a whole-number string, and
    // `achievements` true only while the theme's "Achievement progress" option is on.
    ...achievementView(g),
    // What the extensions stored: {{ext.<id>.<key>}}, and {{ext.<id>.<key>Text}} as the
    // extension's manifest formats it.
    ext: extView(g),
  };

  lazy(m, "released", () => fmtReleased(g.releaseDate) || "");
  lazy(m, "genreList", () => (g.genres || []).map(name => ({ name })));
  lazy(m, "avgSession", () => (g.sessions > 0 && g.playtimeMinutes > 0 ? fmtPlaytime(g.playtimeMinutes / g.sessions) : ""));
  lazy(m, "hasTrailer", () => !!trailerUrl(g));
  lazy(m, "platformIcon", () => platformIcon(g) || "");
  // A square picture, for a theme that draws icons. Read by a template, it asks the host for one
  // (batched, once a session): only a theme that prints {{square}} ever costs a lookup.
  lazy(m, "square", () => { const u = squareUrl(g); if (!u) requestSquare(g); return u || ""; });

  // The age rating in the board Settings asks for, else the other one; the mark is the board's
  // own logo, at a URL the page serves.
  lazy(m, "age", () => {
    const a = ageRating(g);
    return a ? { board: a.board, label: a.label, image: `ratings/${a.file}.svg`,
      descriptors: a.descriptors.map(text => ({ text })) } : null;
  });
  lazy(m, "descriptors", () => (m.age ? m.age.descriptors : []));

  // The line under a title: year, developer, publisher when it is somebody else, three genres.
  lazy(m, "facts", () => {
    const bits = [];
    if (m.year) bits.push(m.year);
    if (g.developer) bits.push(g.developer);
    if (g.publisher && g.publisher !== g.developer) bits.push(g.publisher);
    if (g.genres && g.genres.length) bits.push(g.genres.slice(0, 3).join(", "));
    if (!bits.length) bits.push(g.platform);
    return bits.map(text => ({ text }));
  });

  // The numbers a game's page shows, each only when it has something to say.
  lazy(m, "stats", () => {
    const out = [{ id: "playtime", label: "Playtime", value: m.playtime }];
    if (g.sessions > 0) out.push({ id: "sessions", label: "Sessions", value: String(g.sessions) });
    if (m.avgSession) out.push({ id: "avgSession", label: "Avg session", value: m.avgSession });
    if (g.lastPlayed) out.push({ id: "lastPlayed", label: "Last played", value: m.lastPlayed });
    if (m.ach.has) out.push({ id: "achievements", label: "Achievements", value: `${m.ach.unlocked} / ${m.ach.total}` });
    if (m.released) out.push({ id: "released", label: "Released", value: m.released });
    if (g.installed || g.sizeBytes) out.push({ id: "size", label: g.installed ? "On disk" : "Download", value: m.size });
    return out;
  });

  // Every store this game is in, the one its tile launches first.
  lazy(m, "stores", () => editionsOf(g).map((e, i) => ({ id: e.id, name: e.platform, installed: !!e.installed, current: e.id === g.id, first: i === 0 })));

  // The gallery: the trailer first, then the other films and the screenshots. `index` is what a
  // data-act="media" element passes to open one.
  lazy(m, "media", () => {
    if (live) requestMedia(g);
    return detailMediaItems(g).map((it, index) => ({
      index, kind: it.kind, isVideo: it.kind === "video", isPicture: it.kind !== "video",
      url: it.url, thumb: it.thumb || "", name: it.name || (it.kind === "video" ? "Video" : "Screenshot"),
    }));
  });
  lazy(m, "videos", () => m.media.filter(x => x.isVideo));
  lazy(m, "pictures", () => m.media.filter(x => x.isPicture));
  lazy(m, "hasMedia", () => m.media.length > 0);
  lazy(m, "mediaCount", () => m.media.length);

  lazy(m, "ach", () => achModel(g, live));

  // What the extensions stored, as lists: one line per extension, and every fact flat.
  lazy(m, "extLines", () => {
    const lines = [];
    for (const a of activeExtensions()) {
      const data = extData(g, a.id);
      const facts = (a.contributes && a.contributes.gameFacts) || [];
      if (!data || !facts.length) continue;
      const parts = facts.map(f => ({ key: f.key, label: f.label, text: fmtExtValue(data[f.key], f.format) })).filter(p => p.text);
      if (parts.length) lines.push({ id: a.id, name: a.name, facts: parts });
    }
    return lines;
  });
  lazy(m, "extFacts", () => m.extLines.flatMap(l => l.facts.map(f => ({ ...f, source: l.name, sourceId: l.id }))));
  return m;
}

/* The tiles' name for it, from before there was a model. */
function gameView(g) { return gameModel(g); }

/* ---- squares ---- */
const squareAsked = new Set();
const SQUARE_ROUTE_LIVE = Date.parse("2026-10-10T08:02:05Z");
let squareBatch = [], squareRedraw = null;

function requestSquare(g) {
  if (squareAsked.has(g.id)) return;
  // Looked for this month and none found: the host would refuse anyway. A stamp from before the
  // route went live (MetadataService.SquareRouteLive) was the old service's 404 and does not count.
  const checked = g.squareCheckedAt ? Date.parse(g.squareCheckedAt) : 0;
  if (checked > SQUARE_ROUTE_LIVE && Date.now() - checked < 30 * 864e5) return;
  squareAsked.add(g.id);
  squareBatch.push(g.id);
  if (squareBatch.length === 1) setTimeout(() => { const ids = squareBatch; squareBatch = []; send({ cmd: "fetchSquares", ids }); }, 200);
}

/* Squares arrive one by one; the library is drawn again once for a run of them, keeping its place. */
function squareLanded() {
  bumpLive();
  if (squareRedraw) return;
  squareRedraw = setTimeout(() => { squareRedraw = null; renderLibrary(); if (view === "detail") paintNav(); }, 900);
}

/* ---- achievements ----
   The counts ride every state push. The lists behind the hub's trophy card -- the latest
   unlocks, the rarest, the ones most players have that you do not -- are asked of the host per
   game (achievementsPeek), kept here, and forgotten when that game's summary changes. */
const achPeek = new Map();          // game id -> { recent, rarest, next, list } as the host sent them
const achPeekAsked = new Set();

function achPeekFor(g) {
  if (!achPeek.has(g.id) && !achPeekAsked.has(g.id) && achievementSummary(g)) {
    achPeekAsked.add(g.id);
    send({ cmd: "achievementsPeek", id: g.id });
  }
  return achPeek.get(g.id) || null;
}

function onAchievementsPeek(m) {
  achPeekAsked.delete(m.id);
  achPeek.set(m.id, m.set || { recent: [], rarest: [], next: [], list: [] });
  bumpLive();
}

/** A summary moved: the lists behind it are stale. */
function forgetAchPeek(id) {
  achPeek.delete(id);
  achPeekAsked.delete(id);
  bumpLive();
}

function achItemModel(a) {
  const band = rarityBand(a.percent);
  // A locked hidden one is named as hidden and nothing more, the way the store shows it.
  const secret = !!a.hidden && !a.unlocked;
  return {
    id: a.id || "",
    name: secret ? "Hidden achievement" : a.name || "", description: secret ? "Keep playing to find out" : a.description || "",
    unlocked: !!a.unlocked, hidden: !!a.hidden,
    icon: achItemIcon(a) || "", when: a.unlockedAt ? fmtDate(a.unlockedAt) : "", ago: a.unlockedAt ? fmtAgo(a.unlockedAt) : "",
    percent: fmtPct(a.percent), rarity: band ? band.label : "", rarityId: band ? band.id : "",
  };
}

function achModel(g, live) {
  const sum = achievementSummary(g);
  const peek = live ? achPeekFor(g) : achPeek.get(g.id) || null;
  const unlocked = sum ? sum.unlocked : 0, total = sum ? sum.total : 0;
  return {
    has: !!sum, canHave: canHaveAchievements(g),
    unlocked, total, locked: total - unlocked,
    percent: total ? Math.round(100 * unlocked / total) : 0,
    done: total > 0 && unlocked === total,
    score: sum && sum.totalScore ? sum.score : 0, totalScore: sum ? sum.totalScore || 0 : 0,
    lastUnlock: sum && sum.lastUnlock ? fmtAgo(sum.lastUnlock) : "",
    loaded: !!peek,
    recent: peek ? (peek.recent || []).map(achItemModel) : [],
    rarest: peek ? (peek.rarest || []).map(achItemModel) : [],
    next: peek ? (peek.next || []).map(achItemModel) : [],
    // Up to forty for a row of them: the unlocked, latest first, then the locked ones most
    // players have, then the hidden ones.
    list: peek ? (peek.list || []).map(achItemModel) : [],
  };
}

/* ---- the library ---- */

const LIBRARY_TABS = [
  { id: "installed", label: "Installed" },
  { id: "collection", label: "Your collection" },
  { id: "all", label: "All games" },
  { id: "favorites", label: "Favorites" },
];

/** The games a tab of the game list holds, as library groups (see libraryData). */
function libraryTabFilter(groups, tab) {
  if (tab === "installed") return groups.filter(x => x.rep.installed);
  if (tab === "collection") return groups.filter(x => !x.rep.installed);
  if (tab === "favorites") return groups.filter(x => x.members.some(m => m.favorite));
  return groups;
}

function libraryModel() {
  const groups = collapseEditions(visibleGames());
  const count = (tab) => libraryTabFilter(groups, tab).length;
  const tab = themeLib.page === "games" ? themeLib.tab : "";
  const tabs = LIBRARY_TABS.map(t => ({ id: t.id, label: t.label, count: count(t.id), active: t.id === tab }));
  const current = tabs.find(t => t.active);
  return {
    page: themeLib.page, tab, tabLabel: current ? current.label : "", tabs,
    all: groups.length, installed: count("installed"), collection: count("collection"), favorites: count("favorites"),
    shown: libraryShown, summary: libraryShownSummary,
    sortLabel: (SORTS.find(s => s.id === F.sort) || SORTS[0]).label,
    filtered: !!(F.platforms.size || F.status.size || F.collections.size || F.fav || F.hidden),
    search: F.search || "", searching: !!F.search,
  };
}

/* What the grid is showing right now, which renderLibrary knows and the model reports. */
let libraryShown = 0, libraryShownSummary = "";

/* --------------------------------------------------------------- actions -- */

/* Every name a theme can put in data-act. `game` ones act on the game the element stands for
   (the live region's game, which the app writes onto the element) and are inert without one. */
const THEME_ACTS = {
  play:         { game: true, run: g => playOrResume(g) },
  options:      { game: true, run: g => openGameMenu(g.id, view) },
  details:      { game: true, run: g => openDetail(g.id, view === "detail" ? detailReturn : view) },
  favorite:     { game: true, run: g => send({ cmd: "toggleFavorite", id: g.id }) },
  // data-id: the achievement to open the list on.
  achievements: { game: true, run: (g, el) => openAchievements(g.id, view, el.dataset.id || null) },
  stats:        { game: true, run: g => openActivity(g.id, view) },
  collect:      { game: true, run: g => openCollect(g.id) },
  // The game's page's own sheet, over whatever screen asked: it is about detailGameId.
  manage:       { game: true, run: g => { detailGameId = g.id; openManage(); } },
  mods:         { game: true, run: g => openMods(g.id) },
  media:        { game: true, run: (g, el) => openMediaView(parseInt(el.dataset.index, 10) || 0, g.id) },
  library:      { run: (g, el) => openLibraryPage(el.dataset.tab || "all") },
  home:         { run: () => closeLibraryPage() },
  search:       { run: () => openSearch() },
  filter:       { run: () => openFilter() },
  "add-game":   { run: () => send({ cmd: "addManual" }) },
  settings:     { run: () => switchView("settings") },
  back:         { run: () => handleInput("B") },
};

/* "activity" is what the built-in page calls Stats. */
THEME_ACTS.activity = THEME_ACTS.stats;

function playOrResume(g) {
  if (S.gameRunning && S.runningGameId === g.id) send({ cmd: "resumeGame" });
  else if (!g.installed) offerInstall(g);
  else launchGame(g);
}

/**
 * Make a rendered template's [data-act] elements buttons: highlightable, with a focus key that
 * survives the region being drawn again, the game they act on, and the mouse wired the way it is
 * on a tile. An unknown name is left inert rather than guessed at.
 */
function armActions(root, gameId) {
  const els = [root, ...root.querySelectorAll("[data-act]")].filter(el => el.dataset && el.dataset.act);
  for (const el of els) {
    const def = THEME_ACTS[el.dataset.act];
    if (!def) continue;
    el.dataset.focusable = "";
    if (def.game && gameId) el.dataset.gameId = gameId;
    if (el.dataset.act === "media") el.dataset.mediaIndex = parseInt(el.dataset.index, 10) || 0;
    if (!el.dataset.focusKey) {
      el.dataset.focusKey = "act:" + el.dataset.act
        + (el.dataset.tab ? ":" + el.dataset.tab : "") + (el.dataset.index !== undefined ? ":" + el.dataset.index : "");
    }
    el.addEventListener("mouseenter", () => { if (hoverEnabled()) { setFocusEl(el); paintNav(); } });
    el.addEventListener("click", () => { setFocusEl(el); paintNav(); runThemeAction(el, "A"); });
  }
}

/** A or Y on a theme's button. False when it is not one, so the caller does what it would have. */
function runThemeAction(el, btn) {
  const def = el && THEME_ACTS[el.dataset.act];
  if (!def) return false;
  const g = el.dataset.gameId ? gameById(el.dataset.gameId) : null;
  if (btn === "Y") {
    if (!g) return false;
    openGameMenu(g.id, view);
    return true;
  }
  if (btn !== "A") return false;
  if (def.game && !g) return true;
  def.run(g, el);
  return true;
}

/* ---------------------------------------------------------- live regions --
   Drawn again only when what they show changed: a different game, or `liveVersion` moved (a
   state push, a gallery or a trophy list arriving). Called at the top of paintNav, before the
   highlight is painted, so a region drawn again gets its highlight in the same pass; the
   highlight itself is found again by its focus key. */
let liveVersion = 0;
function bumpLive() { liveVersion++; }

function updateLiveRegions() {
  for (const host of document.querySelectorAll(".screen [data-render]")) {
    // One the layout is not showing waits until it is: drawn hidden it would still ask the host
    // for a gallery for every game the highlight passed (the home hub, under a theme's game list).
    if (!host.getClientRects().length) continue;
    const screen = host.closest(".screen");
    const kind = host.dataset.model === "library" ? "library" : "game";
    const g = kind === "game" ? liveGame(host, screen) : null;
    const gameId = g ? g.id : "";
    const sig = `${host.dataset.render}|${gameId}|${liveVersion}|${kind === "library" ? themeLib.page + themeLib.tab : ""}`;
    if (host.__sig === sig) continue;
    host.__sig = sig;
    // Built only now: on most steps nothing a region shows has changed, and the library model
    // walks the whole library to count its tabs.
    fillLive(host, kind === "library" ? libraryModel() : g ? gameModel(g, { live: true }) : null, gameId);
  }
}

/** The game a region on this screen is about. */
function liveGame(host, screen) {
  if (screen.id === "screen-detail") return gameById(detailGameId);
  const el = focusEl(screen);
  // The highlight inside the region itself -- on its Play button, along its media -- is still
  // the game the region was drawn for.
  if (el && host.contains(el)) return gameById(host.__gameId);
  if (el && el.dataset.gameId) return gameById(el.dataset.gameId);
  // On something that is not a game (a tab, the game list's tile): keep the last one if the theme
  // asks for that with data-hold, else nothing.
  return host.dataset.hold !== undefined && host.__gameId ? gameById(host.__gameId) : null;
}

function fillLive(host, data, gameId) {
  const el = data ? Theme.render(host.dataset.render, data) : null;
  // The same game drawn again keeps where its rows were scrolled to; a different one starts over.
  const same = host.__gameId === gameId;
  const kept = same ? [...host.querySelectorAll("*")].map((n, i) => [i, n.scrollLeft, n.scrollTop]).filter(x => x[1] || x[2]) : [];
  host.replaceChildren(...(el ? [el] : []));
  host.__gameId = gameId;
  host.toggleAttribute("data-empty", !el);
  if (!el) return;
  armActions(el, gameId);
  if (kept.length) {
    const all = host.querySelectorAll("*");
    for (const [i, l, t] of kept) if (all[i]) { all[i].scrollLeft = l; all[i].scrollTop = t; }
  }
}

/* A screen layout's own buttons -- a search icon in the top bar, a rail of them beside the game
   list -- are armed once, when the layout is put in. The live regions arm what they draw. */
function armLayout(screen) {
  const layout = screen && [...screen.children].find(c => c.dataset.themeLayout !== undefined);
  if (!layout) return;
  layout.querySelectorAll("[data-act]").forEach(el => { if (!el.closest("[data-render]")) armActions(el, null); });
}

/* The tile a theme puts after the recents (`continue-end`, typically the way into the game list),
   drawn with the library model. Its root is the button. */
function themeContinueEnd(index) {
  const el = Theme.render("continue-end", libraryModel());
  if (!el) return null;
  armActions(el, null);
  const btn = el.dataset.act ? el : el.querySelector("[data-focusable]");
  if (btn) btn.dataset.contIndex = index;
  return el;
}

/* ---------------------------------------------------- the library's pages --
   "home" is the library as it opens; "games" is the game list on one of its tabs. Only a theme
   that asks for the list (data-act="library") ever leaves home, and the state is two attributes
   on the screen for its CSS to show one page or the other. */

function syncLibraryPage() {
  const scr = document.getElementById("screen-library");
  if (!scr) return;
  scr.dataset.libraryPage = themeLib.page;
  if (themeLib.page === "games") scr.dataset.libraryTab = themeLib.tab; else delete scr.dataset.libraryTab;
}

function openLibraryPage(tab) {
  const scr = document.getElementById("screen-library");
  if (view !== "library") switchView("library");
  if (themeLib.page !== "games") themeLib.returnKey = scopeKey(scr);
  themeLib.page = "games";
  themeLib.tab = LIBRARY_TABS.some(t => t.id === tab) ? tab : "all";
  syncLibraryPage();
  renderLibrary();
  // The list opens at its top, on its first game.
  const grid = $("gridScroll");
  [libraryScroller(), grid].forEach(sc => { if (sc) { stopScroll(sc, "y"); sc.scrollTop = 0; } });
  const first = Nav.focusables(scr).find(el => grid.contains(el));
  if (first) setFocusEl(first);
  updateLibraryFocus();
  renderLibraryLegend();
}

function closeLibraryPage() {
  const scr = document.getElementById("screen-library");
  themeLib.page = "home";
  themeLib.tab = "";
  syncLibraryPage();
  renderLibrary();
  const sc = libraryScroller();
  if (sc) { stopScroll(sc, "y"); sc.scrollTop = 0; }
  setScopeKey(scr, themeLib.returnKey);
  themeLib.returnKey = null;
  clampFocus();
  updateLibraryFocus();
  renderLibraryLegend();
}

/** LB / RB on the game list: the tab before or after, among the ones the theme draws. */
function stepLibraryTab(dir) {
  const scr = document.getElementById("screen-library");
  const drawn = [...scr.querySelectorAll('[data-act="library"][data-tab]')].filter(el => el.getClientRects().length);
  const ids = [...new Set(drawn.map(el => el.dataset.tab))].filter(id => LIBRARY_TABS.some(t => t.id === id));
  if (ids.length < 2) return;
  const i = ids.indexOf(themeLib.tab);
  openLibraryPage(ids[(i + dir + ids.length) % ids.length]);
}
