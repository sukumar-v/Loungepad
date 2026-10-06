
"use strict";

/* ============================== bridge ============================== */

const HOST = window.chrome && window.chrome.webview ? window.chrome.webview : null;

function send(msg) {
  if (HOST) HOST.postMessage(msg);
  else mockHandle(msg);
}

/* ============================== state ============================== */

let S = {
  games: [],
  collections: [],
  settings: null,
  displays: [],
  themes: [],
  startupRegistered: false,
  gameRunning: false,
  runningGameId: null,
  scanning: false,
  scanProgress: [],                      // [{ id, state: pending|running|done|failed|off, count }] -- the last scan, source by source (BeginSteps on the host)
  padName: null,                         // the pad in hand as Windows names it, for the first-run setup's welcome
  steamAccount: null,                    // { steamId, personaName, ownedCount, fetchedAt, error }
  stores: null,                          // { epic|gog|xbox: { signedIn, user, count, fetchedAt, error }, gamePass: { count, fetchedAt, error } }
  emulation: null,                       // { emulators: [...], romFolders: [...], platforms: [{ id, name, shortName, extensions, hasCores }] }
  update: null,                          // { state, current, latest, progress, message, checkedAt, userAsked } -- see UpdateService
  actions: null,                         // { apps: [{ id, name, exes, icon, installed, custom, pinned, modified, actions: [...] }] } -- see actions.js
  xboxButton: null,                      // { gameBar, xboxMode, steam, steamRunning, steamBusy } -- who else reacts to the Xbox button; xboxMode/steam null where there is none
  rest: null,                            // { phase: awake|resting|asleep, paused, wake: { canSleep, modernStandby, signInOnWake, devices: [{ name, armed, kind }], lastWake } | null } -- see RestService and WakeInfo
  gamePaused: false,                     // the running game is frozen (the in-game menu's Pause, or rest mode)
  padConnected: false,
  achievements: {},                      // game id -> { unlocked, total, score, totalScore, lastUnlock, source, fetchedAt, error } -- see activity.js
  sessionStart: null,                    // when the running game was started, for the "playing for" readout
  telemetry: null,                       // the last hardware reading of the running session; pushed only under the in-game menu
};

let view = "library";                    // library | detail | settings
// Library focus lives on the scope element (see the spatial focus section), not in a zone+row+col
// triple -- that is what lets a theme lay the screen out any way it likes.

let detailGameId = null;
let detailReturn = "library";            // where B goes back to from detail

/* filter & sort (session state) — empty sets mean "no restriction" */
const F = { platforms: new Set(), status: new Set(), collections: new Set(), fav: false, hidden: false, sort: "az", search: "" };
/* The stores. An emulated game's platform is its SYSTEM -- "Super Nintendo", "PlayStation" -- so
   the filter lists those too, but only the ones the library actually has (see emulatedPlatforms):
   forty consoles with nothing under them would bury the five rows anyone uses. */
const PLATFORMS = ["Steam", "Epic", "GOG", "Xbox", "Manual"];

/** The systems the library holds ROMs for, in the catalogue's order, each with its count. */
function emulatedPlatforms() {
  const counts = new Map();
  for (const g of S.games) if (g.emulated) counts.set(g.platform, (counts.get(g.platform) || 0) + 1);
  const order = (S.emulation && S.emulation.platforms || []).map(p => p.name);
  return [...counts.keys()]
    .sort((a, b) => (order.indexOf(a) + 1 || 999) - (order.indexOf(b) + 1 || 999) || a.localeCompare(b))
    .map(name => ({ name, count: counts.get(name) }));
}

/* The catalogue entry behind an emulated game's platform id, and the emulator it runs with:
   its own, if it was given one under Manage, otherwise its folder's. */
function platformDef(id) { return (S.emulation && S.emulation.platforms || []).find(p => p.id === id) || null; }
function emulatorById(id) { return id && S.emulation ? (S.emulation.emulators || []).find(e => e.id === id) || null : null; }
function romFolderById(id) { return id && S.emulation ? (S.emulation.romFolders || []).find(f => f.id === id) || null : null; }
function emulatorFor(g) {
  if (!g || !g.emulated) return null;
  const folder = romFolderById(g.romFolderId);
  return emulatorById(g.emulatorId) || (folder ? emulatorById(folder.emulatorId) : null);
}
const STATUSES = ["Installed", "Not installed"];
const MINIMIZE_COMBOS = ["LS + RS", "LB + RB", "LT + RT + LB + RB", "Guide", "View + Menu", "LS + RB", "LB + RS", "Off"];
/* Deliberately combos rather than single buttons: inside a game every face and shoulder button
   belongs to the game, so a one-button binding would fire in the middle of play. */
const SCREENSHOT_COMBOS = ["Off", "View + Y", "View + X", "View + A", "View + B", "LB + RB", "LS + RS"];

const SORTS = [
  { id: "az", label: "Installed, then A – Z" },
  { id: "za", label: "Z – A" },
  { id: "recent", label: "Recently played" },
  { id: "played", label: "Most played" },
  { id: "sizeDesc", label: "Largest first" },
  { id: "sizeAsc", label: "Smallest first" },
  { id: "score", label: "Highest rated" },
  { id: "ach", label: "Achievement progress" },
];

function resetFilters() {
  F.platforms.clear();
  F.status.clear();
  F.collections.clear();
  F.fav = false;
  F.hidden = false;
  F.sort = "az";
  setSearch("");
}

function activeFilterCount() {
  return F.platforms.size + F.status.size + F.collections.size + (F.fav ? 1 : 0) + (F.hidden ? 1 : 0)
    + (F.search ? 1 : 0);
}

/* Collections a game belongs to are stored on the collection, not the game, so membership is a
   lookup rather than a property. Rebuilt per call: the sets are small and collections change
   from the same screen that reads them. */
/* Deleting one is worth a confirmation: it is the only destructive thing in Settings that cannot
   be undone by pressing the same button again. */
function askDeleteCollection(c) {
  const n = (c.gameIds || []).length;
  confirmState = {
    title: `Delete “${c.name}”?`,
    body: n ? `The collection goes; the ${n} game${n === 1 ? "" : "s"} in it stay in your library.`
            : "The collection is empty, so nothing else changes.",
    yesLabel: "Yes, delete",
    onYes: () => { send({ cmd: "deleteCollection", id: c.id }); toast(`${c.name} deleted`); },
  };
  confirmIdx = 0;
  showOverlay("overlay-confirm");
  renderConfirm();
}

function gameInSelectedCollection(g) {
  return S.collections.some(c => F.collections.has(c.id) && (c.gameIds || []).includes(g.id));
}

/* input mode: "pad" hides the pointer and ignores hover; "pointer" is stick or real mouse.
   Starts on "pad" so the launcher boots couch-first with a visible highlight. */
let inputMode = "pad";

function setInputMode(mode) {
  if (inputMode === mode) return;
  inputMode = mode;
  document.body.classList.toggle("pad-mode", mode === "pad");
  // focusVisible() just changed, so whatever is on screen needs its highlight re-painted.
  // A host-driven switch (the stick moved, or the launcher came back to the foreground)
  // arrives with no mouse event behind it to trigger that on its own.
  repaintFocus();
  // Keep the host's copy in step. It only pushes "pointer" when its own idea of the mode
  // changes, so a switch we made locally (opening an overlay, centring the pointer) would
  // otherwise leave it believing we are already in pointer mode -- and the next stick move
  // would push nothing, stranding the UI in pad mode with the cursor hidden.
  send({ cmd: "inputMode", mode });
}

/* Set while a radial/in-game overlay is up. Those menus are pad-driven and hide the cursor,
   so hovering must not steer them and the highlight must always be painted — otherwise A can
   land on nothing and the menu looks frozen. */
let overlayMode = false;

/** Hover should only move focus when the pointer is actually the active input. */
function hoverEnabled() { return inputMode === "pointer" && !overlayMode; }

/* Whether the pointer currently rests on something selectable. In pointer mode with the
   cursor over empty space nothing is highlighted and A does nothing, so the pointer can
   never "arm" a stale item. The index is still remembered, so picking the D-pad back up
   resumes from the last selected item. */
let pointerOnItem = false;
/* Screens migrated to the spatial engine mark their items with [data-focusable]; the class
   list covers the ones still on index navigation. Both are here until the migration finishes. */
const FOCUSABLE_SEL = "[data-focusable], .cont-item, .grid-item, .tab, .set-row, .ov-row, .coll-card, .pill-btn";

/** Should a focus highlight be painted at all right now? */
function focusVisible() { return overlayMode || inputMode === "pad" || pointerOnItem; }

function setPointerOnItem(on) {
  if (pointerOnItem === on) return;
  pointerOnItem = on;
  repaintFocus();
}

/** Rebuild every screen. Used when the theme changes what the markup should be. */
function rerenderAll() {
  renderTabbars();
  renderLibrary();
  if (view === "settings") renderSettings();
  if (view === "detail") renderDetail();
}

/** Re-apply focus styling for whatever screen/overlay is currently up. */
function repaintFocus() {
  // Ordered like handleInput: whatever owns the input owns the highlight. The radial submenu
  // comes first for the same reason it does there -- it sits on top of everything else, and
  // repainting the library underneath it would leave the visible menu unhighlighted.
  if (actionWheelOpen) renderActionWheel();
  else if (keyPick) renderKeyPick();
  else if (captureState) renderCapture();
  else if (radialSub) renderRadialSub();
  else if (mediaView) renderMediaViewFoot();   // nothing to highlight; the pad is the only thing drawn
  else if (filterOpen) renderFilter();
  else if (gameMenu) renderGameMenu();
  else if (collectOpen) renderCollect();
  else if (manageOpen) renderManage();
  else if (choiceState) renderChoice();
  else if (modsState) renderMods();
  else if (confirmState) renderConfirm();
  else if (formState) renderForm();
  else if (sessionState) { /* nothing on it takes the highlight */ }
  else if (actState) paintActFocus();
  else if (achState) paintAchFocus();
  else if (dayState) paintDayFocus();
  else if (view === "library") updateLibraryFocus(true);
  else if (view === "detail") updateDetailFocus();
  else if (view === "settings") renderSettings();
  else if (view === "stats") renderStats();
  else if (view === "onboarding") renderOnboarding();
}

/* ============================== spatial focus ==============================
   The app-side half of nav.js. Focus is an element identified by a stable key
   rather than a row/column pair, so a re-render (or a theme that lays the same
   games out completely differently) lands the highlight back on the same thing. */

let navAnchor = null;      // sticky cross-axis coordinate, held along a straight run
let navAnchorAxis = null;  // "x" while moving vertically, "y" while moving horizontally

/* Each scope remembers its own highlight, parked on the scope element itself.
   One global key could not survive an overlay: opening the game menu over the library
   would overwrite the library's position, and closing it would drop you back on the
   wrong tile. Per-scope keys make open/close free, and nest correctly. */
function scopeKey(scope) { return scope ? scope.dataset.focusCurrent || null : null; }

/* Placing the highlight ends whatever run was in progress.

   Without this, walking down the settings categories and pressing A left the anchor
   sitting in the tab column; the first Down inside the options then scored the tabs as
   "straight below" and threw the highlight back out of the list. Anything that puts the
   highlight somewhere -- a render, an overlay opening, a click -- is a fresh start, so
   only navMove keeps the anchor, by restoring it after this. */
function setScopeKey(scope, key) {
  if (!scope) return;
  if (key) scope.dataset.focusCurrent = key;
  else delete scope.dataset.focusCurrent;
  navAnchor = null;
  navAnchorAxis = null;
}

function focusEl(scope) {
  scope = scope || Nav.activeScope();
  const key = scopeKey(scope);
  if (!scope || !key) return null;
  return Nav.focusables(scope).find(el => Nav.keyOf(el) === key) || null;
}

/** Focus an element outright: hover, a click, or landing on a screen. Ends any run. */
function setFocusEl(el) {
  if (!el) return;
  setScopeKey(el.closest("[data-focus-scope]"), Nav.keyOf(el));
  navAnchor = null;
  navAnchorAxis = null;
}

function clearFocus(scope) { setScopeKey(scope || Nav.activeScope(), null); }

/* Where the highlight should land when a screen has no remembered position.

   Never the tab bar: it is first in DOM order on every screen, so the naive "first
   focusable" dropped you on "Library" and made you press Down before you could do
   anything. Content first, then anything that is not a tab. */
function preferredFocus(list) {
  return list.find(el => el.dataset.gameId || el.dataset.collId)
      || list.find(el => !el.dataset.tab)
      || list[0]
      || null;
}

/** Put the highlight somewhere sensible in a scope that has lost it. */
function ensureFocus(scope) {
  if (focusEl(scope)) return;
  const el = preferredFocus(Nav.focusables(scope));
  if (el) setFocusEl(el); else clearFocus(scope);
}

/** What scrolls the library: the grid itself, or the page around it, which carries the recents up
    off the screen with it (Shelf's .lib-body in app.css, the Loungepad theme's .tv-view). Asked of
    the CSS rather than known, since a theme decides; unlike scrollParentOf it answers whether or
    not it overflows. */
function libraryScroller() {
  const grid = $("gridScroll");
  for (let p = grid; p && p.id !== "screen-library"; p = p.parentElement)
    if (/(auto|scroll)/.test(getComputedStyle(p).overflowY)) return p;
  return grid;
}

/** Nearest ancestor that actually scrolls on the given axis. */
function scrollParentOf(el, axis) {
  for (let p = el.parentElement; p; p = p.parentElement) {
    const s = getComputedStyle(p);
    if (axis === "x") {
      if (/(auto|scroll)/.test(s.overflowX) && p.scrollWidth > p.clientWidth + 1) return p;
    } else if (/(auto|scroll)/.test(s.overflowY) && p.scrollHeight > p.clientHeight + 1) return p;
  }
  return null;
}

/** A memoised "which scroller is this element in", for one navMove: a library is hundreds of
    tiles, and walking each one's ancestors through getComputedStyle would cost more than the step.
    The map holds, for each element visited, the nearest scroller among it and its ancestors. */
function scrollerLookup() {
  const memo = new Map();
  const scrolls = (p) => {
    const s = getComputedStyle(p);
    return (/(auto|scroll)/.test(s.overflowY) && p.scrollHeight > p.clientHeight + 1)
        || (/(auto|scroll)/.test(s.overflowX) && p.scrollWidth > p.clientWidth + 1);
  };
  return (el) => {
    const path = [];
    let found = null;
    for (let p = el.parentElement; p; p = p.parentElement) {
      if (memo.has(p)) { found = memo.get(p); break; }
      path.push(p);
      if (scrolls(p)) { found = p; break; }
    }
    for (const x of path) memo.set(x, found);
    return found;
  };
}

/** Offset along one axis, summed to the scroller. Layout pixels, to match scrollTop/Left. */
function offsetWithin(el, container, axis) {
  let n = 0;
  for (let e = el; e && e !== container; e = e.offsetParent) n += axis === "x" ? e.offsetLeft : e.offsetTop;
  return n;
}

/* How far a scroller has to move to put an element in view, or null if it already is.
   Snaps fully to either end so the first item keeps its focus-glow padding and the last
   is not left hanging a few pixels short. */
function revealOffset(sc, el, axis) {
  const near = offsetWithin(el, sc, axis);
  const far = near + (axis === "x" ? el.offsetWidth : el.offsetHeight);
  // Where the list is going, not where it is: mid-glide the two differ, and measuring against the
  // current position asked for the same scroll twice or turned round for a row already on its way in.
  const viewNear = scrollTarget(sc, axis);
  const size = axis === "x" ? sc.clientWidth : sc.clientHeight;
  const total = axis === "x" ? sc.scrollWidth : sc.scrollHeight;
  const viewFar = viewNear + size;

  /* Nothing focusable above the first row means the space above it is not slack -- it is that
     row's section heading. Clearing REVEAL_MARGIN for the focus glow scrolled that heading off
     the top the moment you walked back up the list, which is why the first category in every
     Settings tab kept vanishing. Snap to the end instead; the glow has its room there -- when the
     element is in view there at all. The Loungepad theme's page opens on a hero most of a screen
     tall, and a first tile under it would be left in the peek. The ends are the first and last
     that are drawn: the library's Playing card is in the DOM, hidden, whenever nothing is running,
     and as "the first" it kept the rule from ever applying to the row under it. */
  const ends = sc.querySelectorAll(FOCUSABLE_SEL);
  const drawn = (e) => e.getClientRects().length > 0;   // display:none anywhere up the tree: no box
  let first = 0, last = ends.length - 1;
  while (first <= last && !drawn(ends[first])) first++;
  while (last >= first && !drawn(ends[last])) last--;
  if (first <= last) {
    // The whole first row, not only its first item: nothing is above any of it.
    if (near <= offsetWithin(ends[first], sc, axis) + 1 && far + REVEAL_MARGIN <= size) return 0;
    if (ends[last] === el) return total;
  }

  /* The same thing one level down, for a scroller holding several headed sections -- Shelf's
     library page, where Playing, Continue and All games each head a run of tiles. Walking up onto
     a section's first row brings its heading back into view with it, rather than leaving the
     heading cut under the top edge. */
  let reach = near;
  const sec = axis === "y" ? el.closest(".lib-section") : null;
  if (sec && sec !== sc && sc.contains(sec)) {
    const lead = sec.querySelector(FOCUSABLE_SEL);
    if (lead && near <= offsetWithin(lead, sc, axis) + 1) reach = offsetWithin(sec, sc, axis);
  }

  /* A theme can ask for more room before an element with CSS's own scroll-margin. The Loungepad
     theme gives its recents a screen of it, so walking up onto them from the grid brings the hero
     above back too -- the top of the page -- rather than parking the row against the top edge. */
  const room = parseFloat(getComputedStyle(el)[axis === "x" ? "scrollMarginLeft" : "scrollMarginTop"]) || 0;
  if (room > 0) reach = Math.min(reach, near - room);

  if (reach - REVEAL_MARGIN <= 0) return 0;
  if (far + REVEAL_MARGIN >= total) return total;
  if (reach - REVEAL_MARGIN < viewNear) return reach - REVEAL_MARGIN;
  if (far + REVEAL_MARGIN > viewFar) return far + REVEAL_MARGIN - size;
  return null;
}

/**
 * Bring the focused element into view, on whichever axis its container scrolls.
 *
 * Both axes, because a theme is free to lay the grid out sideways -- flipping the scroller
 * to horizontal is one of the easiest things a theme can do, and without this the highlight
 * would walk straight off the edge of the screen.
 */
function revealFocus(el) {
  for (const axis of ["y", "x"]) {
    const sc = scrollParentOf(el, axis);
    if (!sc) continue;
    if (axis === "y") watchScrolled(sc);
    const next = revealOffset(sc, el, axis);
    if (next === null) continue;
    animateScroll(sc, axis, next);
  }
}

/*
 * Scrolling that follows the highlight, as a critically damped spring.
 *
 * Not scrollTo({behavior: "smooth"}). That starts a fresh eased animation from standstill on every
 * call, so a run of steps -- a held D-pad at 9 a second, a held arrow key at 30 -- kept throwing
 * away the motion in progress and accelerating from zero again: the list lurched forward, stalled,
 * lurched. With a held key it could not keep up at all and caught up in one jump when the key was
 * let go. A spring can be retargeted mid-flight without losing its velocity, so a run of steps is
 * one continuous glide, and a single step still eases in and out.
 *
 * A scroll the animator did not make -- the mouse wheel, the right stick, a drag -- hands control
 * back at once rather than being fought.
 */
const SCROLL_OMEGA = 22;          // spring stiffness, rad/s: settles in about 250 ms
const scrollAnims = new Map();    // "y"/"x" -> WeakMap(element -> state)
["x", "y"].forEach(a => scrollAnims.set(a, new WeakMap()));

function scrollProp(axis) { return axis === "x" ? "scrollLeft" : "scrollTop"; }

/** Where a scroller is heading: the animation's target, or where it is when nothing is running. */
function scrollTarget(sc, axis) {
  const a = scrollAnims.get(axis).get(sc);
  return a && a.running ? a.target : sc[scrollProp(axis)];
}

function stopScroll(sc, axis) {
  const a = scrollAnims.get(axis || "y").get(sc);
  if (a) a.running = false;
}

function animateScroll(sc, axis, to) {
  const prop = scrollProp(axis);
  const max = Math.max(0, axis === "x" ? sc.scrollWidth - sc.clientWidth : sc.scrollHeight - sc.clientHeight);
  to = Math.max(0, Math.min(to, max));
  const map = scrollAnims.get(axis);
  let a = map.get(sc);
  if (!a || !a.running) {
    a = { pos: sc[prop], vel: 0, target: to, written: sc[prop], running: true, t: null, lastFrameAt: performance.now() };
    map.set(sc, a);
  } else {
    a.target = to;
    return;                       // already gliding: the next frame heads for the new target
  }

  const step = (now) => {
    if (!a.running || map.get(sc) !== a) return;
    a.lastFrameAt = performance.now();
    // Somebody else moved it (wheel, stick, drag): let them have it.
    if (Math.abs(sc[prop] - a.written) > 2) { a.running = false; return; }
    // The first frame's timestamp is when that frame began, which can be BEFORE this animation
    // was asked for; measured from it, the first step moved nothing at all. Give it one frame.
    let dt = a.t === null ? 1 / 60 : Math.min(0.064, Math.max(0, (now - a.t) / 1000));
    a.t = now;
    while (dt > 0) {              // small fixed substeps keep the spring stable on a slow frame
      const h = Math.min(dt, 0.008);
      dt -= h;
      a.vel += (SCROLL_OMEGA * SCROLL_OMEGA * (a.target - a.pos) - 2 * SCROLL_OMEGA * a.vel) * h;
      a.pos += a.vel * h;
    }
    if (Math.abs(a.target - a.pos) < 0.5 && Math.abs(a.vel) < 20) { a.pos = a.target; a.running = false; }
    sc[prop] = a.pos;
    a.written = sc[prop];
    if (a.running) requestAnimationFrame(step);
  };
  requestAnimationFrame(step);

  // Frames that stop coming -- the window hidden mid-glide, or the preview, which runs
  // requestAnimationFrame once and then not again -- must still leave the list where it was going,
  // not stranded halfway. Checked for as long as the glide runs, not just at its start.
  const watch = () => {
    if (!a.running || map.get(sc) !== a) return;
    if (performance.now() - a.lastFrameAt > 150) {
      a.running = false;
      sc[prop] = a.target;
      return;
    }
    setTimeout(watch, 150);
  };
  setTimeout(watch, 150);
}

/** Move the highlight one step. Returns false when there is nowhere to go. `within`, when given,
    narrows the candidates to the elements it accepts (see paneMove). */
function navMove(dir, within) {
  const scope = Nav.activeScope();
  const list = within ? Nav.focusables(scope).filter(within) : Nav.focusables(scope);
  if (!list.length) return false;

  const cur = focusEl();
  if (!cur) { setFocusEl(list[0]); afterFocusMove(); return true; }

  // Scrolled away with the right stick: the highlight is somewhere off screen, and stepping from
  // there would scroll the list straight back to it. Land on the first thing in view instead.
  const resumed = wheelScrolled && resumeInView(cur, list);
  wheelScrolled = false;
  if (resumed) {
    setScopeKey(scope, Nav.keyOf(resumed));
    afterFocusMove();
    return true;
  }

  const horizontal = dir === "Left" || dir === "Right";
  const axis = horizontal ? "y" : "x";
  const from = cur.getBoundingClientRect();
  // A change of axis starts a new run, and the anchor is re-taken from where we are.
  if (navAnchor === null || navAnchorAxis !== axis) {
    const c = Nav.centre(from);
    navAnchor = horizontal ? c.y : c.x;
    navAnchorAxis = axis;
  }
  const anchorNow = navAnchor;

  /* Left/Right stay in their row while the row has anywhere left to go.

     Without this, Right off the last tile of a grid row scored some item on a
     different line as "to the right and a bit up" and jumped there -- pressing
     Right on the last tile threw you into the carousel. Confining the pool to
     things that share the row keeps the common case sane; when the row really is
     exhausted the wrap below takes over, and only if there is nothing to wrap to
     does it fall back to the whole scope (which is what lets a theme put a
     sidebar to the left of a grid and have Right cross into it). */
  /* Rows scrolled out of a list are clipped, not gone: their rects are still real, and they sit
     wherever the scroll put them -- once, in the Loungepad theme, directly behind a recents row
     parked over the top of the grid (it has since become a row of the same page). Two rules keep
     the engine from walking into them.

     A list is walked to its end before the highlight leaves it (samePlace, below). Holding Up
     through the grid, the scroll's glide trails the highlight by a few dozen pixels, and that lag
     was enough for a recents tile to score nearer than the next row up: the highlight jumped to
     the recents for one step, the row slid down, and the next Up dived back into a hidden row.

     And a thing scrolled wholly out of its own list cannot be reached from outside that list:
     from the recents, the grid rows hidden behind them were "above" and took the highlight. */
  const scrollerOf = scrollerLookup();
  const curScroller = scrollerOf(cur);
  const reachable = (el) => {
    const sc = scrollerOf(el);
    if (!sc || sc === curScroller) return true;
    const r = el.getBoundingClientRect(), v = sc.getBoundingClientRect();
    return r.bottom > v.top && r.top < v.bottom && r.right > v.left && r.left < v.right;
  };

  // data-nav-skip: can hold the highlight, but is never walked to. The search box is reached with
  // View or from the Filter menu only -- Up off the top row landing in it read as a mistake.
  const walkable = list.filter(el => !el.hasAttribute("data-nav-skip") && (el === cur || reachable(el)));
  let pool = walkable.filter(el => el !== cur);
  if (horizontal) {
    const sameBand = pool.filter(el => {
      const r = el.getBoundingClientRect();
      return r.bottom > from.top && r.top < from.bottom;
    });
    if (sameBand.length) pool = sameBand;
  }

  const pick = (candidates) => {
    let best = null, bestScore = Infinity;
    for (const el of candidates) {
      const s = Nav.score(from, el.getBoundingClientRect(), dir, navAnchor);
      if (s < bestScore) { bestScore = s; best = el; }
    }
    return best;
  };

  let best = curScroller ? pick(pool.filter(el => scrollerOf(el) === curScroller)) : null;
  if (!best) best = pick(pool);
  if (!best) best = Nav.wrapTarget(walkable, cur, dir);
  if (!best && pool.length !== walkable.length - 1) best = pick(walkable.filter(el => el !== cur));
  if (!best) return false;

  setScopeKey(scope, Nav.keyOf(best));
  // setScopeKey ends a run; this is a step within one, so put the anchor back.
  navAnchor = anchorNow;
  navAnchorAxis = axis;
  afterFocusMove();
  return true;
}

/*
 * A step inside one pane of a two-pane screen (Settings, Stats): the highlight never leaves the
 * category's own rows for the sidebar. Up off the first row comes round to the last and Down off
 * the last to the first, in the nearest column for the Actions grid; Left and Right only move
 * between tiles. The categories are reached with B, or a click. Walking off the top into the
 * sidebar used to swap the category under the highlight, which read as the list vanishing.
 */
function paneMove(dir, inPane) {
  const scope = Nav.activeScope();
  const cur = focusEl(scope);
  if (!cur || !inPane(cur)) return navMove(dir);
  if (navMove(dir, inPane)) return true;
  if (dir !== "Up" && dir !== "Down") return false;
  const rows = Nav.focusables(scope).filter(el => inPane(el) && el !== cur && !el.hasAttribute("data-nav-skip"));
  if (!rows.length) return false;
  const rects = rows.map(el => ({ el, r: el.getBoundingClientRect() }));
  const edge = dir === "Up" ? Math.max(...rects.map(x => x.r.top)) : Math.min(...rects.map(x => x.r.top));
  const x = Nav.centre(cur.getBoundingClientRect()).x;
  const band = rects.filter(o => Math.abs(o.r.top - edge) < 8);
  const target = band.reduce((best, o) => Math.abs(Nav.centre(o.r).x - x) < Math.abs(Nav.centre(best.r).x - x) ? o : best).el;
  setScopeKey(scope, Nav.keyOf(target));
  afterFocusMove();
  return true;
}

/*
 * Up and Down in a list come round at the ends: Up off the first item lands on the last, Down off
 * the last on the first (the user's call, Oct 1 2026). Every menu, sheet and category list steps
 * through here, or through paneMove, which it is; Left and Right already wrap their line in nav.js.
 * The library grid and a game's page are screens rather than lists, and keep their ends -- falling
 * off the bottom of a library onto its top bar is the disorientation nav.js was written to avoid.
 */
function listMove(dir, within) {
  if (dir !== "Up" && dir !== "Down") return navMove(dir, within);
  return paneMove(dir, within || (() => true));
}

/*
 * Set by a vertical wheel -- which is what the right stick sends -- and consumed by the next
 * D-pad step. Only a WHEEL counts: revealFocus scrolls smoothly, so during a fast run of Down
 * presses the highlight is briefly half out of view on every step, and treating that as "the
 * user scrolled away" would throw the run back to the top of the screen.
 */
let wheelScrolled = false;
window.addEventListener("wheel", (e) => {
  if (Math.abs(e.deltaY) > Math.abs(e.deltaX)) wheelScrolled = true;
}, { passive: true, capture: true });

/*
 * The right stick sends real wheel events, and a wheel goes to whatever is under the cursor. In
 * pad mode the cursor is hidden and could be anywhere -- over the Continue row, the top bar, the
 * backdrop -- and in Shelf the grid is only the bottom half of the screen, so most of the time
 * the stick scrolled nothing at all. The Loungepad theme gets away with it because its page fills
 * the screen.
 *
 * So a vertical wheel that lands on nothing that can scroll that way is handed to the list the
 * user is actually in: the open menu, else the scroller around the highlight, else the screen's
 * own list. A wheel that DOES land on a scroller is left alone, so a real mouse is unaffected.
 */
function wheelHome() {
  const scope = Nav.activeScope();
  if (!scope) return null;
  if (scope.classList.contains("overlay")) return scope.querySelector(".ov-scroll, .guide-body");
  const cur = focusEl(scope);
  const around = cur && scrollParentOf(cur, "y");
  if (around) return around;
  if (view === "library") return libraryScroller();
  if (view === "settings") return $("settingsScroll");
  if (view === "onboarding") return $("onbRows");
  return null;
}

function canScrollY(el, dy) {
  if (!el || el.scrollHeight <= el.clientHeight + 1) return false;
  if (!/(auto|scroll)/.test(getComputedStyle(el).overflowY)) return false;
  return dy > 0 ? el.scrollTop + el.clientHeight < el.scrollHeight - 1 : el.scrollTop > 0;
}

/*
 * The jumps between the art and the grid. Where a library page opens on a screen of art with the
 * grid under the fold -- the Loungepad theme, whose recents sit under the hero and whose grid is a
 * peek below them -- the stretch between the top of the page and the grid is never somewhere the
 * page comes to rest (the user's calls, Oct 6 2026). A move down from anywhere above the grid goes
 * the whole way, and All games fills the view; a move up that would carry the page above the grid
 * goes the whole way back to the top. Inside the grid the page scrolls like any other. The D-pad's
 * step from the recents into the grid jumps down (and its step back onto them already lands on the
 * top, see the recents' scroll-margin); the wheel and the stick jump both ways. Shelf's grid starts
 * on screen, so there is nothing to jump to and nothing changes there.
 */
let gridJumpTo = null, gridJumpDir = 0;

/**
 * Where the page stands with All games filling the view: exactly where revealOffset puts the page
 * for a highlight on the grid's first row -- the section's top less REVEAL_MARGIN, the room it keeps
 * for the lift and the glow. Landing on the grid's own top edge instead left that room to be made
 * by the first step along the row, which read as the row settling a moment after it had landed.
 * Null where the grid starts on screen at the top of the page (Shelf), so there is nothing to jump.
 */
function gridJumpStop(sc) {
  const grid = $("gridScroll");
  if (view !== "library" || !sc || !grid || sc === grid || sc !== libraryScroller()) return null;
  const first = grid.querySelector(FOCUSABLE_SEL);
  if (!first) return null;
  const near = offsetWithin(first, sc, "y");
  if (near + first.offsetHeight + REVEAL_MARGIN <= sc.clientHeight) return null;
  const sec = first.closest(".lib-section");
  const reach = sec && sec !== sc && sc.contains(sec) ? offsetWithin(sec, sc, "y") : near;
  return Math.max(0, Math.min(reach - REVEAL_MARGIN, sc.scrollHeight - sc.clientHeight));
}

/** Where a move `dir` (1 down, -1 up) of `by` pixels lands instead, or null to let it scroll. */
function gridJumpFor(sc, dir, by) {
  const stop = gridJumpStop(sc);
  if (stop === null || !dir) return null;
  const at = scrollTarget(sc, "y");
  if (dir > 0) return at < stop - 1 ? stop : null;
  return at > 1 && at - by < stop - 1 ? 0 : null;
}

function startGridJump(sc, to) {
  gridJumpDir = Math.sign(to - scrollTarget(sc, "y"));
  animateScroll(sc, "y", to);
  gridJumpTo = scrollTarget(sc, "y");   // clamped: a short library jumps to its end
}

/** A jump in that direction is still gliding. A wheel or the stick pushing the same way meanwhile
    would tear it in half (the animator hands over to any scroll it did not make), so it waits; the
    other way is a change of mind and is answered. */
function gridJumpRunning(sc, dir) {
  const a = sc && scrollAnims.get("y").get(sc);
  return gridJumpTo !== null && dir === gridJumpDir && !!a && a.running && a.target === gridJumpTo;
}

window.addEventListener("wheel", (e) => {
  if (Math.abs(e.deltaY) <= Math.abs(e.deltaX) || e.ctrlKey) return;
  if (!searchOpen && Nav.activeScope() === $("screen-library")) {
    const page = libraryScroller();
    const dir = Math.sign(e.deltaY);
    if (gridJumpRunning(page, dir)) { e.preventDefault(); return; }
    const by = Math.abs(e.deltaMode === 1 ? e.deltaY * 40 : e.deltaMode === 2 ? e.deltaY * page.clientHeight : e.deltaY);
    const to = gridJumpFor(page, dir, by);
    if (to !== null) { e.preventDefault(); startGridJump(page, to); return; }
  }
  for (let p = e.target instanceof Element ? e.target : null; p; p = p.parentElement)
    if (canScrollY(p, e.deltaY)) return;          // already over something that will scroll
  const home = wheelHome();
  if (!home || !canScrollY(home, e.deltaY)) return;
  e.preventDefault();
  const step = e.deltaMode === 1 ? e.deltaY * 40 : e.deltaMode === 2 ? e.deltaY * home.clientHeight : e.deltaY;
  stopScroll(home, "y");
  home.scrollTop += step;
}, { passive: false });

/*
 * The right stick, scrolled by the frame.
 *
 * Over the launcher the host sends the stick's speed (wheel notches a second, up positive) rather
 * than wheel notches. A notch is a 100px jump, and they arrived up to 18 times a second on
 * whichever poll crossed the line, so however smoothly the stick was held the list moved in uneven
 * lurches. Here the list moves by speed x frame time on every frame, and the speed itself eases
 * towards what the stick says, so pushing and letting go ramp rather than snap.
 *
 * Which list: the one under the pointer when the pointer is in use, otherwise the one the user is
 * in (wheelHome). A speed older than 250 ms is treated as zero, so a lost "stop" cannot leave the
 * list running.
 */
const STICK_PX_PER_NOTCH = 100;     // what one wheel notch scrolls, so the speed matches the old feel
const STICK_EASE_SEC = 0.08;
let stickTarget = 0, stickVel = 0, stickAt = 0, stickRunning = false;
let stickEl = null, stickPos = 0;
let lastClientX = NaN, lastClientY = NaN;

/** The next frame, or 50 ms from now if frames have stopped (a hidden window, the preview). */
function nextFrame(cb) {
  let done = false;
  const run = () => { if (done) return; done = true; cb(performance.now()); };
  requestAnimationFrame(run);
  setTimeout(run, 50);
}

function stickScrollEl() {
  if (inputMode === "pointer" && !isNaN(lastClientX)) {
    for (let p = document.elementFromPoint(lastClientX, lastClientY); p; p = p.parentElement)
      if (p.scrollHeight > p.clientHeight + 1 && /(auto|scroll)/.test(getComputedStyle(p).overflowY)) return p;
  }
  return wheelHome();
}

function onStickScroll(notchesPerSec) {
  stickTarget = -notchesPerSec * STICK_PX_PER_NOTCH;   // up on the stick is up the list: scrollTop falls
  stickAt = performance.now();
  if (notchesPerSec !== 0) wheelScrolled = true;       // the next D-pad step resumes from what is in view
  if (stickRunning || notchesPerSec === 0) return;
  stickRunning = true;
  stickEl = null;
  let last = performance.now();
  const frame = (now) => {
    const dt = Math.min(0.1, Math.max(0, (now - last) / 1000));
    last = now;
    const target = now - stickAt > 250 ? 0 : stickTarget;
    stickVel += (target - stickVel) * (1 - Math.exp(-dt / STICK_EASE_SEC));
    if (target === 0 && Math.abs(stickVel) < 8) { stickVel = 0; stickRunning = false; return; }

    const el = stickScrollEl();
    if (el !== stickEl) { stickEl = el; stickPos = el ? el.scrollTop : 0; }
    // Which way it is going: the stick's push, or, once it is let go, the coast -- a coast that
    // carries the page up past the grid finishes the move like a push would.
    const dir = Math.sign(target || stickVel);
    const jump = el && !gridJumpRunning(el, dir) ? gridJumpFor(el, dir, Math.abs(stickVel * dt)) : null;
    if (jump !== null) {
      // Down from above the grid lands on it in one glide, up past its top lands on the top of the
      // page (see gridJumpFor); held on, the stick carries on from there once it lands.
      startGridJump(el, jump);
      stickPos = el.scrollTop;
    } else if (el && gridJumpRunning(el, dir)) {
      stickPos = el.scrollTop;
    } else if (el) {
      // Something else moved it -- the D-pad's glide, a real wheel -- so carry on from there.
      if (Math.abs(el.scrollTop - stickPos) > 2) stickPos = el.scrollTop;
      stopScroll(el, "y");
      const max = el.scrollHeight - el.clientHeight;
      stickPos = Math.max(0, Math.min(max, stickPos + stickVel * dt));
      el.scrollTop = stickPos;
    }
    nextFrame(frame);
  };
  nextFrame(frame);
}

/** The first focusable in view in the scroller the highlight has been scrolled out of, or null
    when the highlight is still on screen and an ordinary step should happen. */
function resumeInView(cur, list) {
  const sc = scrollParentOf(cur, "y");
  if (!sc) return null;
  const view = sc.getBoundingClientRect();
  const visible = (r) => {
    const h = Math.min(r.bottom, view.bottom) - Math.max(r.top, view.top);
    return h >= r.height * 0.6;
  };
  if (visible(cur.getBoundingClientRect())) return null;
  const inView = list.filter(el => sc.contains(el) && visible(el.getBoundingClientRect()));
  if (!inView.length) return null;
  // Top row first, then leftmost: the first item of the first row on screen.
  inView.sort((a, b) => {
    const ra = a.getBoundingClientRect(), rb = b.getBoundingClientRect();
    return (Math.round(ra.top) - Math.round(rb.top)) || (ra.left - rb.left);
  });
  return inView[0];
}

function afterFocusMove() {
  paintNav();
  const el = focusEl();
  if (el) revealFocus(el);
}

/** Apply the highlight, the section dimming and the backdrop from the DOM alone. */
function paintNav() {
  const scope = Nav.activeScope();
  if (!scope) return;
  const show = focusVisible();
  const cur = focusEl();

  Nav.focusables(scope).forEach(el => el.classList.toggle("focused", show && el === cur));
  // Anything marked as a dim group fades unless the highlight is inside it. Themes opt
  // in by adding data-dim-group; nothing here knows what a "Continue row" is.
  scope.querySelectorAll("[data-dim-group]").forEach(g =>
    g.classList.toggle("zone-dim", !!cur && !g.contains(cur)));

  /* Which region the highlight is in, published on the scope element so a theme can style a
     whole state off it -- collapsing a hero when focus reaches the grid, say. A theme cannot
     run script, so without this the only "where am I" signal available to CSS is the focus
     ring itself, which is far too local to drive a layout.

     Deliberately the remembered focus rather than the visible one: moving the mouse off an
     item clears the ring, and a layout that flipped back every time the pointer wandered
     would be unusable. */
  const region = cur ? cur.closest("[data-region]") : null;
  // The search box sits in the top bar, but what it is ABOUT is the grid: while a search is being
  // typed or is standing, say "grid" so a theme can lay the results out in view. (The Loungepad
  // theme's page is scrolled to them instead; see revealSearchResults.)
  if (cur && cur.id === "libSearch" && (searchOpen || F.search)) scope.dataset.focusRegion = "grid";
  else if (region) scope.dataset.focusRegion = region.dataset.region;
  else delete scope.dataset.focusRegion;

  updateContinueScroll(true);
  const g = focusedGame();
  // On a game's page the gallery has a say: a highlighted screenshot is the picture behind the
  // page, in the backdrop (Loungepad) and in the page's own art (Shelf) both.
  const override = focusedMediaOverride();
  if (view === "detail") { updateMediaScroll(); updateDetailArt(g, override); renderDetailLegend(g); }
  else if (view === "library") renderLibraryLegend();
  scheduleBackdrop(g, override);
  updateFocusDetail(g);
  syncTrailers();
}

/* Keep the opt-in focus-detail region filled. Cheap enough to do on every move -- it is a
   handful of textContent writes -- and doing it unconditionally means a theme can slot the
   region in at any point and find it already correct. */
function updateFocusDetail(g) {
  const panel = $("fdTitle");
  if (!panel) return;
  panel.textContent = g ? g.title : "";
  $("fdMeta").textContent = g ? ((g.installed ? shortMeta(g) : `${g.platform} · NOT INSTALLED`) + achievementMeta(g)).toUpperCase() : "";
  $("fdDesc").textContent = g && g.installDir ? g.installDir : "";
  $("fdPlaytime").textContent = g ? fmtPlaytime(g.playtimeMinutes) : "";
  $("fdLastPlayed").textContent = g ? fmtLastPlayed(g.lastPlayed) : "";
  $("fdSize").textContent = g ? fmtSize(g.sizeBytes) : "";
}

/** The game the highlight is on, straight off the element. */
function focusedGame() {
  if (view === "detail") return gameById(detailGameId);
  // Settings sits over the library, so the picture behind it stays the library's own.
  const el = focusEl(view === "settings" || view === "stats" || view === "onboarding" ? document.getElementById("screen-library") : undefined);
  return el && el.dataset.gameId ? gameById(el.dataset.gameId) : null;
}

/* ============================== theme ============================== */

/* The presets. Every one is a light, saturated tone: the accent is used as a fill behind dark
   text (the A badge, the Play button) as well as for rings and glows, so a dark accent would
   take the label down with it. A custom colour is allowed to be anything -- see accentRow. */
const ACCENTS = [
  { name: "Ember", hex: "#F0A253" },   // the default; the design's own colour
  { name: "Coral", hex: "#E97A6C" },
  { name: "Rose", hex: "#F07AA8" },
  { name: "Orchid", hex: "#C78BE8" },
  { name: "Indigo", hex: "#8098F0" },
  { name: "Aqua", hex: "#5FC9D6" },
  { name: "Mint", hex: "#6FCF97" },
  { name: "Lime", hex: "#B8D96B" },
];
const DEFAULT_ACCENT = ACCENTS[0].hex;

function accentName(hex) {
  const preset = ACCENTS.find(a => a.hex.toUpperCase() === String(hex).toUpperCase());
  return preset ? preset.name : "Custom";
}

/** Only "#RRGGBB" reaches the stylesheet. Mirrors SettingsStore.IsHexColor on the host. */
function isHexColor(v) { return typeof v === "string" && /^#[0-9A-Fa-f]{6}$/.test(v); }

/* Relative luminance, WCAG's formula. Used only to decide what colour sits legibly *on* the
   accent: the presets are all light enough for dark text, but a custom colour can be anything,
   and a navy accent with near-black text on it is an unreadable Play button. */
function luminance(hex) {
  const ch = i => {
    const v = parseInt(hex.substr(1 + i * 2, 2), 16) / 255;
    return v <= 0.03928 ? v / 12.92 : Math.pow((v + 0.055) / 1.055, 2.4);
  };
  return 0.2126 * ch(0) + 0.7152 * ch(1) + 0.0722 * ch(2);
}

/* Push the settings' colours onto the :root tokens. Everything in app.css resolves to those, so
   this one call recolours the whole UI -- no re-render, and nothing else has to know a theme
   exists. Setting a token to "" removes the override and falls back to the stylesheet's own
   value, which is what makes a bad or missing colour a no-op rather than a blank screen. */
/* The theme's markup is fetched, not linked, because it has to be parsed rather than
   rendered. Tracked by URL (which carries the file's mtime) so a save reloads it and an
   unchanged theme does not refetch on every state push. */
let themeHtmlUrl = null;

async function applyThemeMarkup() {
  const theme = currentTheme();
  const url = theme && theme.html ? theme.html : null;
  if (url === themeHtmlUrl) return false;
  themeHtmlUrl = url;

  if (!url) { Theme.clear(); applyThemeLayout(); return true; }
  try {
    const res = await fetch(url);
    if (!res.ok) throw new Error("HTTP " + res.status);
    Theme.load(await res.text(), url);
  } catch (e) {
    // A theme with broken markup keeps its styling and falls back to the built-in
    // layout, rather than taking the whole launcher down with it.
    Theme.clear();
    toast(`Theme markup failed to load: ${e.message}`);
  }
  applyThemeLayout();
  return true;
}

/** Hand each screen to the theme's layout, or put it back if the theme has none. */
function applyThemeLayout() {
  ["library", "detail", "settings"].forEach(id =>
    Theme.applyScreen(document.getElementById("screen-" + id), "screen-" + id));
}

function applyTheme() {
  applyThemeSheet();
  // A class rather than a per-screen render, because every screen has its own legend and a
  // theme may have moved it somewhere of its own.
  document.body.classList.toggle("no-legend", lookHideHints());
  // Fire and forget: the markup arrives a tick later and re-renders then, so the
  // colours are not held up waiting on a file read.
  applyThemeMarkup().then(changed => { if (changed) rerenderAll(); });

  const root = document.documentElement.style;
  // The theme's own tokens go on first so the accent setting still wins: a user who picks a
  // colour expects it to hold whatever theme is loaded, and a theme that wants to own the
  // accent simply ships a theme.css rule, which the stylesheet layer below cannot override.
  const theme = currentTheme();
  const tokens = (theme && theme.tokens) || {};
  for (const [name, value] of Object.entries(appliedTokens))
    if (!(name in tokens)) root.removeProperty(name);
  appliedTokens = {};
  for (const [name, value] of Object.entries(tokens)) {
    if (!/^--[A-Za-z0-9_-]+$/.test(name) || typeof value !== "string") continue;
    root.setProperty(name, value);
    appliedTokens[name] = value;
  }

  const hex = lookAccent();
  const ok = isHexColor(hex);
  // The theme's own options go on before the accent, so a theme cannot offer one that takes
  // the accent away from the user; and the motion multiplier last, because it is not a colour.
  applyThemeSettings(theme);
  root.setProperty("--accent", ok ? hex : "");
  // 0.5 rather than WCAG's 0.179 contrast crossover: the ink is off-white and the deep is
  // near-black, so both are legible over a mid-tone and the eye prefers dark ink there.
  root.setProperty("--on-accent", ok && luminance(hex) < 0.5 ? "var(--ink)" : "");
  applyMotion();
}

/* ---- the look, per theme ----
   The accent, the hints and the animation settings belong to the theme in use, not to the app:
   a warm accent on one theme, animations off on another. They live in the same per-theme bag as
   the theme's own options (settings.themeSettings[themeId]) under ids no theme may declare, and
   what a theme has not set falls back to the app-wide fields in settings.json -- which is where a
   value from before this lived, so an accent chosen last year still shows. "Restore <theme>'s
   defaults" empties that one bag and nothing else. */
const LOOK_IDS = {
  accent: "accent", hideHints: "hide-hints", animations: "animations", speed: "animation-speed",
  trailers: "trailers", trailerSound: "trailer-sound", achievements: "achievement-progress",
};
const RESERVED_IDS = new Set(Object.values(LOOK_IDS));

/** The current theme's bag of values, made on demand when `create` is set. Shelf's id is "". */
function lookBag(create) {
  const s = S.settings;
  if (!s) return null;
  const id = s.theme || "";
  if (!s.themeSettings || typeof s.themeSettings !== "object") { if (!create) return null; s.themeSettings = {}; }
  if (!s.themeSettings[id]) { if (!create) return null; s.themeSettings[id] = {}; }
  return s.themeSettings[id];
}
function lookGet(id) { const b = lookBag(false); return b ? b[id] : undefined; }
function lookSet(id, v) { lookBag(true)[id] = v; }

function lookAccent() {
  const v = lookGet(LOOK_IDS.accent), s = S.settings;
  return isHexColor(v) ? v.toUpperCase() : s && isHexColor(s.accentColor) ? s.accentColor.toUpperCase() : DEFAULT_ACCENT;
}
function lookHideHints() {
  const v = lookGet(LOOK_IDS.hideHints);
  return typeof v === "boolean" ? v : !!(S.settings && S.settings.hideLegend);
}
function lookAnimations() {
  const v = lookGet(LOOK_IDS.animations);
  return typeof v === "boolean" ? v : !(S.settings && S.settings.animationsEnabled === false);
}
function lookSpeed() {
  const v = lookGet(LOOK_IDS.speed), s = S.settings;
  const speed = typeof v === "number" && isFinite(v) ? v
    : s && typeof s.animationSpeed === "number" && isFinite(s.animationSpeed) ? s.animationSpeed : 1;
  return Math.max(0.5, Math.min(2, speed));
}
/* Where a trailer may play. Per theme like the rest of the look, because how much of the screen
   a film gets is a fact about the layout the theme draws. No app-level fallback: there was no
   such setting before this, so the default is the default. */
const TRAILER_MODES = ["all", "detail", "off"];
const TRAILER_LABELS = { all: "Library and details", detail: "Details page only", off: "Off" };
/** Whether the theme in force lets a film play behind its library: `--trailers` on #backdrop is
    anything but `none`. Shelf leaves the app's default, none, so its library is never touched. */
function libraryCanHostTrailers() {
  const bd = $("backdrop");
  return !!bd && getComputedStyle(bd).getPropertyValue("--trailers").trim() !== "none";
}
/** The modes this theme can offer: the library option only where the theme can draw it. */
function trailerModes() { return libraryCanHostTrailers() ? TRAILER_MODES : TRAILER_MODES.slice(1); }
function lookTrailers() {
  const v = lookGet(LOOK_IDS.trailers);
  const mode = TRAILER_MODES.includes(v) ? v : "all";
  return mode === "all" && !libraryCanHostTrailers() ? "detail" : mode;
}
function lookTrailerSound() { return lookGet(LOOK_IDS.trailerSound) !== false; }
/* The unlocked share on the library: the tiles' percentages and Loungepad's "23/50 achievements".
   Per theme since Oct 6 2026 (the user's ask, for both themes); it was one switch under Stats, and
   that switch's value in settings.json is what a theme with nothing set still reads. */
function lookAchievements() {
  const v = lookGet(LOOK_IDS.achievements);
  return typeof v === "boolean" ? v : !(S.settings && S.settings.achievementsOnTiles === false);
}

/* ---- animation ----
   One number on the root, --motion, that every duration in app.css (and a well-behaved theme.css)
   is multiplied by. 1 is the design's own timing; the speed slider divides it, so 2× is half the
   time; off writes 0, which makes every transition and animation instant without a single rule
   having to check. The body class is for the few things a zero cannot switch off -- an infinite
   pulse just sits on a frame at 0s. */
function motionScale() {
  return lookAnimations() ? 1 / lookSpeed() : 0;
}

function applyMotion() {
  const scale = motionScale();
  document.documentElement.style.setProperty("--motion", String(scale));
  document.body.classList.toggle("no-motion", scale === 0);
}

/* ---- a theme's own options ----
   theme.json may declare settings, and they belong to that theme alone: the values are kept
   under its id (settings.themeSettings[themeId][optionId]) and reach the page only as CSS. Each
   one is written to the root as the custom property named by its `token`, and as a
   data-theme-<id> attribute on <html> -- so a theme, which cannot run script, keys its rules off
   either: `.tv { --tile-w: var(--tv-tile, 270px) }`, or
   `html[data-theme-labels="true"] .tv-name { opacity: 1 }`. The README's Themes section is the
   author's side of this.

   Definitions are checked here rather than trusted. A theme is a folder anyone can edit, and a
   bad entry should cost that one row, not the Appearance screen. */
const THEME_SETTING_TYPES = new Set(["toggle", "select", "slider"]);
const THEME_SETTING_ID = /^[a-z][a-z0-9-]{0,31}$/;

/** A JSON value as a string, if it is the kind of thing that has one. */
function plainString(v) {
  return typeof v === "string" ? v : typeof v === "number" || typeof v === "boolean" ? String(v) : null;
}
function stringMap(obj) {
  if (!obj || typeof obj !== "object" || Array.isArray(obj)) return null;
  const out = {};
  for (const [k, v] of Object.entries(obj)) { const s = plainString(v); if (s !== null) out[k] = s; }
  return out;
}

function themeSettingDefs(theme) {
  const list = theme && Array.isArray(theme.settings) ? theme.settings : [];
  const seen = new Set();
  const out = [];
  for (const raw of list) {
    if (!raw || typeof raw !== "object") continue;
    const id = String(raw.id || "").toLowerCase();
    if (!THEME_SETTING_ID.test(id) || seen.has(id) || RESERVED_IDS.has(id)) continue;
    const type = String(raw.type || "").toLowerCase();
    if (!THEME_SETTING_TYPES.has(type)) continue;
    const d = {
      id, type,
      name: plainString(raw.name) || id,
      hint: typeof raw.hint === "string" ? raw.hint : null,
      token: typeof raw.token === "string" && /^--[A-Za-z0-9_-]+$/.test(raw.token) ? raw.token : null,
      unit: typeof raw.unit === "string" ? raw.unit.slice(0, 8) : "",
      values: stringMap(raw.values),
    };
    if (type === "select") {
      d.options = (Array.isArray(raw.options) ? raw.options : []).map(plainString).filter(o => o).slice(0, 24);
      if (d.options.length < 2) continue;
      d.labels = stringMap(raw.labels);
      const def = plainString(raw.default);
      d.default = d.options.includes(def) ? def : d.options[0];
    } else if (type === "slider") {
      const num = (v, f) => (typeof v === "number" && isFinite(v) ? v : f);
      d.min = num(raw.min, 0); d.max = num(raw.max, 100); d.step = num(raw.step, 1);
      if (!(d.max > d.min) || !(d.step > 0)) continue;
      d.default = Math.max(d.min, Math.min(d.max, num(raw.default, d.min)));
      d.decimals = (String(d.step).split(".")[1] || "").length;
    } else {
      d.default = raw.default === true;
    }
    seen.add(id);
    out.push(d);
  }
  return out;
}

/** The value in force for one of a theme's options: the saved one when it is valid, else the default. */
function themeSettingValue(theme, d) {
  const bag = S.settings && S.settings.themeSettings && S.settings.themeSettings[theme.id];
  const v = bag ? bag[d.id] : undefined;
  if (d.type === "toggle") return typeof v === "boolean" ? v : d.default;
  if (d.type === "select") return d.options.includes(v) ? v : d.default;
  return typeof v === "number" && isFinite(v) ? Math.max(d.min, Math.min(d.max, v)) : d.default;
}

/** What the value becomes in the stylesheet: the theme's own mapping when it gives one, else the value. */
function themeSettingCss(d, v) {
  const key = String(v);
  if (d.values && typeof d.values[key] === "string") return d.values[key];
  if (d.type === "toggle") return v ? "1" : "0";
  if (d.type === "slider") return v + d.unit;
  return key;
}

function fmtThemeValue(d, v) {
  return v.toFixed(d.decimals || 0) + (d.unit ? " " + d.unit : "");
}

/* What the current theme's options put on the root, so a change of theme takes them off again. */
let appliedThemeSettings = { tokens: [], attrs: [] };

function applyThemeSettings(theme) {
  const root = document.documentElement;
  appliedThemeSettings.tokens.forEach(t => root.style.removeProperty(t));
  appliedThemeSettings.attrs.forEach(a => root.removeAttribute(a));
  appliedThemeSettings = { tokens: [], attrs: [] };
  if (!theme) return;
  for (const d of themeSettingDefs(theme)) {
    const v = themeSettingValue(theme, d);
    const attr = "data-theme-" + d.id;
    root.setAttribute(attr, String(v));
    appliedThemeSettings.attrs.push(attr);
    if (d.token) {
      root.style.setProperty(d.token, themeSettingCss(d, v));
      appliedThemeSettings.tokens.push(d.token);
    }
  }
}

/* Tokens this theme set, so switching themes can take them off again -- otherwise a token
   from the old theme survives into a new one that never mentions it. */
let appliedTokens = {};

function currentTheme() {
  const id = (S.settings && S.settings.theme) || "";
  return (S.themes || []).find(t => t.id === id) || null;
}

/* The theme's stylesheet is one <link> appended after app.css, so a theme overrides by
   ordinary cascade order and needs no !important anywhere.

   The href carries a cache-busting stamp from the host (the file's mtime). Re-setting the
   same href would not reload, which is exactly what made saving a theme edit look like it
   had done nothing. */
function applyThemeSheet() {
  const theme = currentTheme();
  const href = theme && theme.css ? theme.css : null;
  let link = document.getElementById("themeSheet");

  if (!href) { if (link) link.remove(); return; }
  if (link && link.getAttribute("href") === href) return;

  if (!link) {
    link = document.createElement("link");
    link.id = "themeSheet";
    link.rel = "stylesheet";
    document.head.appendChild(link);
  }
  link.setAttribute("href", href);
}

/* Only a pointer that actually moved counts. The browser also raises mousemove when the page
   scrolls or relayouts under a cursor that is sitting still -- which is exactly what happens
   while the arrow keys walk the grid past a parked pointer -- and treating that as the mouse
   being picked up threw the page into pointer mode and let hover steal the highlight mid-run. */
let lastMouseX = NaN, lastMouseY = NaN;
window.addEventListener("mousemove", (e) => {
  lastClientX = e.clientX; lastClientY = e.clientY;
  if (e.screenX === lastMouseX && e.screenY === lastMouseY) return;
  lastMouseX = e.screenX; lastMouseY = e.screenY;
  const wasPad = inputMode === "pad";
  setInputMode("pointer");
  setPointerOnItem(!!(e.target instanceof Element && e.target.closest(FOCUSABLE_SEL)));

  // Boundary events fire before the mousemove that caused them, so the mouseenter for the item
  // the pointer just arrived on ran while hover was still disabled and its handler ignored it.
  // Nothing would highlight until the pointer left the item and came back. Replay it against
  // whatever is under the cursor now — re-queried, because switching modes repaints and the
  // node from the event may already be detached.
  if (wasPad) {
    const under = document.elementFromPoint(e.clientX, e.clientY);
    const item = under && under.closest(FOCUSABLE_SEL);
    if (item) item.dispatchEvent(new MouseEvent("mouseenter"));
  }
});

// A click on an item always counts as being on it, even without a preceding move.
window.addEventListener("mousedown", (e) => {
  if (e.target instanceof Element && e.target.closest(FOCUSABLE_SEL)) setPointerOnItem(true);
}, true);

/*
 * A focused tile grows past its own box: scale(1.05) pushes it about 12px up (the transform
 * origin is its bottom edge) and the ring adds 3px all round. Anything less than that much
 * clearance and the highlight is shaved off against the scroller's edge.
 */
const REVEAL_MARGIN = 30;

/**
 * Mark a scroller while it is scrolled away from the top, which is what turns on the top fade.
 * Attaches once per element; the scroller nodes outlive the rows rendered into them.
 */
function watchScrolled(scroller) {
  const mark = () => scroller.classList.toggle("scrolled", scroller.scrollTop > 1);
  if (!scroller.dataset.scrollWatched) {
    scroller.dataset.scrollWatched = "1";
    scroller.addEventListener("scroll", mark, { passive: true });
  }
  mark();
}

/*
 * A slot is where a button is drawn. It carries the button's name, so paintButtons can redraw
 * every one on the page when the pad in hand changes without anything being re-rendered.
 * `padOnly` marks a slot that is about a gamepad whatever is in use: the button rows in Settings.
 */
function slot(btn, padOnly, kb) {
  return `<span class="btn-slot" data-btn="${esc(canonBtn(btn))}"${padOnly ? ' data-pad=""' : ""}${kb ? ` data-kb="${esc(kb)}"` : ""}>` +
    slotIcon(btn, padOnly ? padFamily : inputFamily, kb) + `</span>`;
}

/* `kb` is the key a slot stands for on a keyboard when that is not the button's usual key: on the
   library Tab is Settings, ` is Stats and Ctrl is a game's options (LIBRARY_KEYCAPS), where
   everywhere else Menu is M, LB is [ and Y is Y. A pad draws the button whatever kb says. */
function slotIcon(btn, family, kb) {
  return kb && family === "keyboard" ? keycap(kb) : btnIcon(btn, family);
}

/** "LS + RS" as pictures, for a settings value. */
function comboHtml(combo) {
  if (!combo || combo === "Off") return "Off";
  return `<span class="combo">` + combo.split("+").map(p => slot(p.trim(), true)).join(`<span class="combo-plus">+</span>`) + `</span>`;
}

/** Text with [[A]]-style references drawn as buttons. Escapes the text first, so a title cannot smuggle markup in. */
function hintHtml(text) {
  return esc(text).replace(/\[\[(\w+)\]\]/g, (_, b) => slot(b, true));
}

/** Redraw every slot for the families now in use. */
function paintButtons(root) {
  (root || document).querySelectorAll("[data-btn]").forEach(el => {
    el.innerHTML = slotIcon(el.dataset.btn, "pad" in el.dataset ? padFamily : inputFamily, el.dataset.kb);
  });
}

/*
 * The last thing the user touched. A pad announces its family with every press and whenever the
 * host sees it picked up; a keypress or a mouse click says "keyboard". Mouse MOVEMENT does not
 * count: the left stick moves the real Windows pointer, so a mousemove can be the pad.
 */
function setInputFamily(family) {
  if (!family || !BTN_NAMES[family]) return;
  if (family !== "keyboard") padFamily = family;
  if (inputFamily === family) return;
  inputFamily = family;
  document.body.dataset.input = family;
  paintButtons(document);
  // Settings names buttons in its hints and warnings, and those are words, not slots.
  if (view === "settings") renderSettings();
}

/** One entry of a legend: the button, then what it does. Clickable, so a mouse can press it. */
function legendItem(btn, label, kb) {
  const press = /^Dpad/.test(btn) ? "" : ` data-press="${esc(canonBtn(btn))}"`;
  return `<div class="legend-item"${press}>${slot(btn, false, kb)}<span>${esc(label)}</span></div>`;
}

const LIBRARY_LEGEND = [["A", "Launch"], ["X", "Filter"], ["Y", "Options"], ["View", "Search"], ["LB", "Stats"], ["Menu", "Settings"]];

/* The keyboard on the library, with nothing over it (the user's call, Oct 1 2026): Enter launches, X
   filters, Ctrl opens a game's options, / searches, Tab opens Settings and ` opens Stats -- the keys
   the legend names, rather than Y, M and [, which all still work. Esc is Back here as it is on every
   other screen: it was Settings for a day, and one key that always cancels was worth more. Ctrl is
   handled on its own (ctrlTap, by the keymap), because it is a modifier as well as a key. */
const LIBRARY_KEYS = { Tab: "Menu", Backquote: "LB", "`": "LB" };
const LIBRARY_KEYCAPS = { Menu: "Tab", LB: "`", Y: "Ctrl" };

function libraryTakesKeys() {
  return view === "library" && !overlayOpen() && !searchOpen && !overlayMode
    && !radialOpen && !radialSub && !actionWheelOpen && !ingameOpen;
}

/* B only appears while a search is standing, because that is when it does something worth saying:
   from the grid it goes back to the top, and from the top it clears the search. Rebuilt on every
   highlight move, so it is only written when it changes. */
function renderLibraryLegend() {
  const el = $("libraryFoot");
  if (!el) return;
  const items = [...LIBRARY_LEGEND];
  if (F.search) items.splice(1, 0, ["B", atLibraryTop(focusEl(document.getElementById("screen-library"))) ? "Clear search" : "Back"]);
  const html = foot(...items.map(([b, label]) => [b, label, LIBRARY_KEYCAPS[b]]));
  if (el.__html !== html) { el.innerHTML = html; el.__html = html; }
}

function renderDetailLegend(g) {
  const el = $("detailFoot");
  if (!el) return;
  // On the gallery A opens the item; everywhere else on the page it presses what is under it.
  const onStrip = !!focusedMediaItem();
  el.innerHTML = foot(["A", onStrip ? "View" : "Select"], ["B", "Back"], ["X", g && g.favorite ? "Unfavorite" : "Favorite"]);
}

// A click on a legend entry is that button. Delegated, because footers are rebuilt constantly.
document.addEventListener("click", (e) => {
  const item = e.target instanceof Element ? e.target.closest(".legend-item[data-press]") : null;
  if (!item || inputOpen) return;
  handleInput(item.dataset.press, "mouse");
});

/**
 * Shared renderer for every overlay menu, so the game options, manage, collection and
 * confirm menus all read like the filter menu. Items are
 * { cat } headers or { label, icon, sub, checked, radio, danger, summary, action }.
 *
 * All five render through here, so marking rows up once puts every one of them on the
 * spatial engine. `idx` stays the caller's own selection -- these lists have real index
 * semantics -- but the highlight and the movement come from the DOM, so a theme can lay a
 * menu out as a row or a grid without any of the callers knowing.
 */
function renderMenu(listEl, footEl, items, idx, footHtml, onHover, onClick) {
  // Every step rebuilds the list, and emptying it snapped the scroll back to the top -- so on a
  // menu longer than its box the smooth reveal restarted from 0 on every press and the last rows
  // could go unseen. Hold the position across the rebuild; revealFocus moves it from there.
  const keepTop = listEl.scrollTop;
  listEl.innerHTML = "";
  items.forEach((r, i) => {
    if (r.cat) {
      const c = document.createElement("div");
      c.className = "ov-cat";
      c.textContent = r.cat;
      listEl.appendChild(c);
      return;
    }
    const el = document.createElement("div");
    el.className = "ov-row" + (r.danger ? " danger" : "") + (r.thumb ? " has-thumb" : "");
    el.dataset.focusable = "";
    el.dataset.focusKey = "row:" + i;
    el.dataset.rowIndex = i;
    let right = "";
    if (r.summary !== undefined) right = `<div class="ov-value"><span class="ov-summary">${esc(r.summary)}</span><span class="arrow">▸</span></div>`;
    else if (r.checked !== undefined) right = `<span class="ov-check${r.checked ? "" : " off"}">${r.checked ? (r.star ? "★" : r.radio ? "●" : "✓") : "○"}</span>`;
    // A picture of the window replaces the icon rather than joining it: the icon was standing in
    // for exactly this, and showing both says the same thing twice.
    const lead = r.thumb
      ? `<div class="ov-thumb${r.thumbIsIcon ? " is-icon" : ""}" style="background-image:url('${r.thumb}')"></div>`
      : r.swatch ? `<span class="swatch ov-swatch" style="background:${esc(r.swatch)}"></span>`
      : iconSvg(r.icon);
    // `html` is a label the caller built itself -- button pictures for a combo -- and is trusted;
    // everything else is text.
    const name = r.html || esc(r.label ?? r.name);
    // The subtitle goes UNDER the label, never beside it. Side by side, a long subtitle took the
    // row's width and squeezed the label down to its first two letters -- "Install" read "In…".
    const text = r.sub
      ? `<span class="ov-text"><span class="ov-name">${name}</span><span class="ov-sub">${esc(r.sub)}</span></span>`
      : `<span>${name}</span>`;
    if (r.sub) el.classList.add("two-line");
    el.innerHTML = `<div class="ov-label">${lead}${text}</div>${right}`;
    el.addEventListener("mouseenter", () => { if (hoverEnabled()) onHover(i); });
    el.addEventListener("click", () => onClick(i));
    listEl.appendChild(el);
  });
  if (footEl) footEl.innerHTML = footHtml;
  listEl.scrollTop = keepTop;
  watchOverflow(listEl);

  // The caller owns the index, so point the scope's highlight at whatever it chose.
  const scope = listEl.closest("[data-focus-scope]");
  setScopeKey(scope, "row:" + idx);
  const cur = focusEl(scope);
  const show = focusVisible();
  Nav.focusables(scope).forEach(el => el.classList.toggle("focused", show && el === cur));
  if (cur && show) revealFocus(cur);
}

/* A menu taller than its box fades at whichever edge has more beyond it. The scrollbar is hidden
   on purpose, and without this a list that ran past its box simply looked complete. */
function markOverflow(el) {
  el.classList.toggle("more-above", el.scrollTop > 2);
  el.classList.toggle("more-below", el.scrollTop + el.clientHeight < el.scrollHeight - 2);
}
function watchOverflow(el) {
  if (!el.dataset.overflowWatched) {
    el.dataset.overflowWatched = "1";
    el.addEventListener("scroll", () => markOverflow(el), { passive: true });
  }
  markOverflow(el);
}

/** Step an overlay's index by moving through the DOM, so the list need not be a column. The ends
    come round (listMove). */
function menuStep(dir, idx, count) {
  if (!listMove(dir)) return idx;
  const el = focusEl();
  const next = el && el.dataset.rowIndex !== undefined ? parseInt(el.dataset.rowIndex, 10) : idx;
  return Math.max(0, Math.min(count - 1, next));
}

/** Standard footer hints: [button, label] pairs, drawn for the pad in hand. */
function foot(...pairs) {
  return pairs.map(([btn, label, kb]) => legendItem(btn, label, kb)).join("");
}

/* overlays */
let filterOpen = false, filterIdx = 0;
let manageOpen = false, manageIdx = 0;
let collectOpen = false, collectIdx = 0;
let confirmState = null, confirmIdx = 0; // { title, onYes }
let inputOpen = false, inputConfirm = null;
let guideOpen = false;
/* The action wheel and its two pickers (actions.js). Declared here because this file reads them
   while it boots, before actions.js has run. */
let actionWheelOpen = false;
let captureState = null;                 // { app, action, onDone, timer, note }
let keyPick = null;                      // { onDone, mods: Set }
let overlayTargetProcess = "";           // the exe behind the window a menu opened over; "" for none
let overlayTargetIsGame = false;         // that window is the running game's (the wheel's Close spoke)
// The sheets and the screen activity.js draws. Declared here rather than there for the same
// reason as the three above: app.js reads them while it boots.
let achState = null;                     // { gameId, idx, filter, sort, set, head, reveal, from }
let actState = null;                     // { gameId, idx, sessions }
let sessionState = null;                 // { session, samples, unlocks, from }
let dayState = null;                     // { key, idx } -- the Stats overview's Day sheet
let formState = null;                    // { kind, kicker, title, sub, rows, idx, onClose } -- see openForm
// The first-run setup (onboarding.js), a view like Settings. Declared here for the same reason.
let onboardState = null;                 // { step, from, info, vortex, pinPoll } -- see openOnboarding
let statsUi = { tab: "overview", pane: "nav", idx: 0, period: "30d", achSort: "progress", from: "library", day: null };
let statsData = { sessions: null, sources: null, ach: null };

let settingsIdx = 0;
let saveTimer = null;

/* Kept only for the carousel geometry (how many tiles fit, how far it can slide).
   Focus no longer indexes into either of these. */
let contItems = [];
let gridRows = [];

/* ============================== helpers ============================== */

const $ = (id) => document.getElementById(id);

/* ============================== motion ==============================
 *
 * Every screen and overlay has two classes: .active means it owns input, .closing means it is
 * only still being drawn while its exit animation runs. The split is what keeps an animation from
 * changing behaviour: Nav.activeScope only ever looks at .active, so the D-pad moves to whatever is
 * underneath the moment a menu is dismissed, not after its fade, and a click cannot land on a
 * card that is on its way out. The stylesheet holds the animations; this only sets the classes
 * and takes .closing off again when the exit has run.
 *
 * How long that is comes from the stylesheet itself -- the longest animation on the element or
 * its direct children, delay included -- so a theme that lengthens a card's exit is not cut off
 * by the wash's, and with animations off (every duration 0) the class comes off at once, in the
 * same call, so nothing is ever left mid-state for a timer to clean up.
 */
function motionMs(el) {
  const time = (v) => { v = v.trim(); return (v.endsWith("ms") ? parseFloat(v) : parseFloat(v) * 1000) || 0; };
  let ms = 0;
  for (const node of [el, ...el.children]) {
    const cs = getComputedStyle(node);
    const delays = cs.animationDelay.split(",");
    cs.animationDuration.split(",").forEach((d, i) => { ms = Math.max(ms, time(d) + time(delays[i] ?? delays[0])); });
  }
  return ms;
}

function motionEnter(el) {
  if (!el) return;
  clearTimeout(el.__leave);
  el.__leave = null;
  el.classList.remove("closing");
  el.classList.add("active");
}

function motionLeave(el) {
  // Not active means already gone, or already going: putting .closing on a hidden element would
  // flash it up just to fade it.
  if (!el || !el.classList.contains("active")) return;
  el.classList.remove("active");
  el.classList.add("closing");
  const done = () => { el.__leave = null; el.classList.remove("closing"); };
  const ms = motionMs(el);
  if (ms <= 0) { done(); return; }
  clearTimeout(el.__leave);
  el.__leave = setTimeout(done, ms + 30);
}

// The trailer check is deferred a tick: the flag that says an overlay is open (overlayOpen) is
// set by the caller around this call, in either order, and a tick later it is settled.
function showOverlay(id) { motionEnter($(id)); setTimeout(syncTrailers, 0); }
function hideOverlay(id) { motionLeave($(id)); setTimeout(syncTrailers, 0); }

/** Replay a one-shot animation class on an element whose content has just been replaced. */
function pulse(el, cls = "swap") {
  if (!el) return;
  el.classList.remove(cls);
  void el.offsetWidth;   // a reflow between the remove and the add, or the browser sees no change
  el.classList.add(cls);
}
// esc() lives in glyphs.js: the drawings escape their labels with it, and the browse window's
// bar loads only that file.

function artUrl(name) {
  if (!name) return null;
  return HOST ? `https://loungepad.data/covers/${encodeURIComponent(name)}` : name;
}

function coverUrl(g) { return artUrl(g.coverFile); }

/* The ~16:9 tile art. Falls back to the portrait cover so a landscape tile is never empty, but
   never to the hero: a 3:1 backdrop centre-cropped into a tile shows background, not the game. */
function bannerUrl(g) { return artUrl(g.bannerFile) || coverUrl(g); }

/* The wide backdrop. Falls back to the tile, which is at least landscape, and then stops.
   NOT to the portrait cover: 2:3 art hung across a screen at its own aspect is a tall narrow
   column of box art, and because the backdrop follows the highlight, walking along a row of
   tiles made the picture behind them change shape every time it landed on a game with no hero.
   A flat colour is a better answer than a cover in a slot that is not for covers. */
function heroUrl(g) { return artUrl(g.heroFile) || artUrl(g.bannerFile); }

/*
 * Everything that could go behind a whole screen, best first.
 *
 * The hero leads. It used to be second, on the theory that IGDB's 1920x1080 artwork is the shape
 * of the screen and therefore fills one with nothing cropped -- which is true about its SHAPE and
 * says nothing about the picture. IGDB's artworks are user uploads in no particular order, and
 * the first one is as likely to be a flat background plate as the game: Persona 3's was a blue
 * diagonal, two floating leaves and 31 KB of JPEG, against 873 KB of key art in Steam's hero.
 * Steam's library_hero is curated, it is the same picture the store shows, and the band-over-a-
 * blurred-bed treatment handles its 3.1:1 perfectly well. So the 16:9 slot is now the fallback,
 * which is what it is actually good for: games with no hero at all.
 *
 * A LIST rather than one URL because a name in the library can outlive the file it points at, and
 * the right answer to a picture that will not load is the next one down, not a flat colour.
 */
function backdropUrls(g) {
  return [artUrl(g.heroFile), artUrl(g.backdropFile), artUrl(g.bannerFile)].filter(Boolean);
}

function backdropUrl(g) { return backdropUrls(g)[0] || null; }

function logoUrl(g) { return artUrl(g.logoFile); }

/* The trailer: the copy on this PC once the host has one, the store's stream until then. The
   cached name lives under a different top-level folder from the art because it is a different
   kind of thing -- gigabytes of video under Local, not covers in a roaming profile -- and the
   host routes the two by that first path segment. */
function trailerUrl(g) {
  if (!g) return null;
  if (g.trailerFile) return HOST ? `https://loungepad.data/trailers/${encodeURIComponent(g.trailerFile)}` : g.trailerFile;
  return g.trailerUrl || null;
}

/* Which film a URL IS, as opposed to where it is being read from. The game's own trailer can be
   read from the store or from the cached copy, and a cached copy landing changes the address
   and not the film, so a player showing it must not restart. Any other film is its address. */
function filmKey(g, url) {
  return g && (url === trailerUrl(g) || url === g.trailerUrl) ? "trailer:" + (g.trailerUrl || url) : url;
}

/* Art the user picked by hand. The host writes it under a "custom_" name precisely so nothing
   else can ever write that name, which makes the prefix a reliable answer to "did somebody
   choose this?" -- and therefore to "is there anything to undo?". */
function isCustomArt(file) { return typeof file === "string" && file.startsWith("custom_"); }

function hashHue(str) {
  let h = 0;
  for (let i = 0; i < str.length; i++) h = (h * 31 + str.charCodeAt(i)) >>> 0;
  return h % 360;
}

function initials(title) {
  const words = title.split(/\s+/).filter(Boolean);
  return (words.length >= 2 ? words[0][0] + words[1][0] : title.slice(0, 2)).toUpperCase();
}

/*
 * Cover art loads through a small queue.
 *
 * WebView2's virtual-host mapping drops requests when a whole screen's worth of tiles
 * fire at once: with ~22 images requested simultaneously from https://loungepad.data only
 * the first handful resolved and the rest failed outright, leaving most tiles blank
 * even though every file was present and valid. Capping concurrency and retrying with
 * a cache-busting suffix makes the load reliable; anything still failing after its
 * retries falls back to the initials placeholder instead of an empty tile.
 */
const IMG_MAX_CONCURRENT = 6;
const IMG_MAX_ATTEMPTS = 4;
const imgQueue = [];
let imgActive = 0;

function queueArt(url, done) {
  imgQueue.push({ url, done, attempt: 0 });
  pumpImgQueue();
}

function pumpImgQueue() {
  while (imgActive < IMG_MAX_CONCURRENT && imgQueue.length) {
    const job = imgQueue.shift();
    imgActive++;
    const probe = new Image();
    probe.onload = () => {
      imgActive--;
      // The natural size goes back with the URL so the caller can decide whether this picture
      // survives a cover-crop into its box.
      job.done(probe.src, probe.naturalWidth, probe.naturalHeight);
      pumpImgQueue();
    };
    probe.onerror = () => {
      imgActive--;
      job.attempt++;
      if (job.attempt < IMG_MAX_ATTEMPTS) {
        setTimeout(() => { imgQueue.push(job); pumpImgQueue(); }, 80 * job.attempt);
      } else {
        job.done(null);
      }
      pumpImgQueue();
    };
    // A fresh query string on retry also sidesteps any negatively-cached response.
    probe.src = job.attempt ? `${job.url}?r=${job.attempt}` : job.url;
  }
}

function paintPlaceholder(g, el) {
  unbedArt(el);
  const h = hashHue(g.title);
  el.style.background = `linear-gradient(150deg, hsl(${h},16%,15%) 0%, hsl(${(h + 40) % 360},22%,9%) 100%)`;
  el.classList.add("ph");
  el.innerHTML = `<span>${esc(initials(g.title))}</span>`;
}

/*
 * How far a picture may be off its box before cover-cropping does visible damage.
 *
 * Landscape tile art has the game name burnt into it, running close to the edges. Steam capsules
 * are 1.75:1 and land inside this; header.jpg is 2.14:1 and does not, and cover was shaving 18%
 * off its width -- REANIMAL lost the end of its own name.
 *
 * Only landscape boxes get the treatment. Portrait box art is drawn to be cropped and always has
 * been cropped here, so a portrait tile keeps cover and looks exactly as it did.
 */
const ART_FIT_TOLERANCE = 1.02;

/*
 * The aspect of the box a background is actually painted into.
 *
 * Not clientWidth/clientHeight: `background-origin: content-box` -- which is what holds tile art
 * clear of the rounded corners -- makes `cover` size against the CONTENT box, while clientWidth
 * includes the padding. Measuring the padding box made the continue row look 1.79:1 when it was
 * really 1.86:1, so a 1.75:1 capsule read as a near-perfect fit and was cropped 6% narrower --
 * a strip off each side, taking the edge of the logo with it.
 */
function artBoxAspect(el) {
  const cs = getComputedStyle(el);
  const px = (v) => parseFloat(v) || 0;
  const inset = cs.backgroundOrigin === "content-box";
  const w = el.clientWidth - (inset ? px(cs.paddingLeft) + px(cs.paddingRight) : 0);
  const h = el.clientHeight - (inset ? px(cs.paddingTop) + px(cs.paddingBottom) : 0);
  return w > 0 && h > 0 ? w / h : NaN;
}

function artFit(el, w, h) {
  if (!w || !h) return "cover";                     // never measured; keep the old behaviour
  const box = artBoxAspect(el);
  if (!isFinite(box) || box <= 1.2) return "cover"; // portrait or square: crop as before
  const off = (w / h) / box;
  return (off > 1 ? off : 1 / off) > ART_FIT_TOLERANCE ? "contain" : "cover";
}

/**
 * `fit` forces the sizing for art that is scenery rather than a tile. A full-bleed backdrop must
 * always cover: it has no edges of its own to protect, and letterboxing one leaves bars down the
 * screen -- which is what artFit did to the detail page, whose 3:1 hero is nowhere near its 16:9
 * box. Only tiles, whose art carries the game's name near the edges, are worth fitting.
 */
/* Natural sizes of pictures already loaded, by URL. A box cut to its picture (Shelf's recents, the
   detail hero) takes its shape from --art-aspect, and every state push rebuilds the row: without
   the size known up front, each rebuild drew a wide tile at the default width for a frame and then
   pushed its neighbours along when the picture arrived. */
const artSizes = new Map();

function applyArt(g, el, url, fit) {
  if (!url) { paintPlaceholder(g, el); setArtAspect(el, 0, 0); return; }
  const known = artSizes.get(url);
  if (known) setArtAspect(el, known.w, known.h);
  queueArt(url, (src, w, h) => {
    if (!el.isConnected) return;          // tile was re-rendered while loading
    if (!src) { paintPlaceholder(g, el); setArtAspect(el, 0, 0); return; }
    if (w && h) artSizes.set(url, { w, h });
    setArtAspect(el, w, h);
    const size = fit || artFit(el, w, h);

    if (size === "contain") { bedArt(el, src); return; }

    unbedArt(el);
    el.style.backgroundImage = `url('${src}')`;
    el.style.backgroundSize = size;
    el.style.backgroundRepeat = "no-repeat";
  });
}

/*
 * Fitted art does not reach every edge of its box, and what it leaves behind should not be two
 * black bars. The box is filled with a blurred, dimmed copy of the same picture and the sharp one
 * laid over it -- the same thing the library backdrop does with a hero that does not fit the
 * screen, and the only answer that neither crops the art nor leaves a hole.
 *
 * Both layers are children rather than one being the element's own background, because a child
 * always paints ABOVE its parent's background and never below it -- and because a filter on the
 * element would blur the sharp layer along with the bed.
 *
 * Only built when it is actually needed, so the great majority of tiles, whose art fills the box,
 * carry no extra layer and no blur.
 */
function bedArt(el, src) {
  el.style.backgroundImage = "none";
  layer(el, "art-bed").style.backgroundImage = `url('${src}')`;
  layer(el, "art-top").style.backgroundImage = `url('${src}')`;
}

function unbedArt(el) {
  el.querySelectorAll(":scope > .art-bed, :scope > .art-top").forEach(n => n.remove());
}

/** One of the two layers, made on demand. Appended in order, which is paint order. */
function layer(el, cls) {
  let node = el.querySelector(":scope > ." + cls);
  if (!node) {
    node = document.createElement("div");
    node.className = cls;
    el.appendChild(node);
  }
  return node;
}

/*
 * Publish the shape of the picture that landed, so CSS can cut the box to it.
 *
 * The alternative is to guess, and both places that show a full-width picture were guessing:
 * the detail page and the library backdrop were sized for Steam's 3.1:1 hero, so anything else
 * in that slot -- IGDB hands back 16:9 artwork -- was blown up and cropped to fit a box chosen
 * for a different picture. A box cut to the art's own aspect has nothing to crop.
 */
function setArtAspect(el, w, h) {
  if (w && h) el.style.setProperty("--art-aspect", (w / h).toFixed(4));
  else el.style.removeProperty("--art-aspect");
}

function fmtPlaytime(min) {
  if (!min || min < 1) return "—";
  const h = Math.floor(min / 60), m = Math.round(min % 60);
  return h > 0 ? `${h}h ${String(m).padStart(2, "0")}m` : `${m}m`;
}

function fmtSize(bytes) {
  if (!bytes) return "—";
  const gb = bytes / (1024 ** 3);
  return gb >= 1 ? `${gb.toFixed(1)} GB` : `${(bytes / 1024 ** 2).toFixed(0)} MB`;
}

function fmtLastPlayed(iso) {
  if (!iso) return "—";
  const d = new Date(iso);
  if (isNaN(d)) return "—";
  const now = new Date();
  const time = `${String(d.getHours()).padStart(2, "0")}:${String(d.getMinutes()).padStart(2, "0")}`;
  const day0 = new Date(now.getFullYear(), now.getMonth(), now.getDate());
  const dayD = new Date(d.getFullYear(), d.getMonth(), d.getDate());
  const diff = Math.round((day0 - dayD) / 86400000);
  if (diff === 0) return `Today, ${time}`;
  if (diff === 1) return `Yesterday, ${time}`;
  if (diff < 365) return `${d.getDate()} ${d.toLocaleString("en", { month: "short" })}, ${time}`;
  return d.toLocaleDateString();
}

function shortMeta(g) {
  const played = g.playtimeMinutes >= 60 ? ` · ${Math.round(g.playtimeMinutes / 60)}h` : "";
  return g.platform + played;
}

/* A game that was never installed has no size to report: the Steam account lists what you own,
   not how big it is. Printing the dash fmtSize gives for zero read as a broken field. */
function uninstalledMeta(g) {
  return g.sizeBytes ? `${g.platform} · ${fmtSize(g.sizeBytes)} · not installed`
                     : `${g.platform} · not installed`;
}

function gameById(id) { return S.games.find(g => g.id === id) || null; }

function toast(msg, ms = 2600) {
  const t = $("toast");
  t.textContent = msg;
  t.classList.add("show");
  clearTimeout(t._timer);
  t._timer = setTimeout(() => t.classList.remove("show"), ms);
}

/* ============================== stage / clock / battery ============================== */

function fitStage() {
  const sc = Math.min(window.innerWidth / 1920, window.innerHeight / 1080);
  $("stage").style.transform = `translate(-50%, -50%) scale(${sc})`;
}
window.addEventListener("resize", fitStage);

function tickClock() {
  const now = new Date();
  const days = ["SUN", "MON", "TUE", "WED", "THU", "FRI", "SAT"];
  const months = ["JAN", "FEB", "MAR", "APR", "MAY", "JUN", "JUL", "AUG", "SEP", "OCT", "NOV", "DEC"];
  let h = now.getHours();
  const suffix = h >= 12 ? "PM" : "AM";
  h = h % 12 || 12;                       // 0 and 12 both display as 12
  const time = `${h}:${String(now.getMinutes()).padStart(2, "0")} ${suffix}`;
  const date = `${days[now.getDay()]} ${now.getDate()} ${months[now.getMonth()]}`;
  document.querySelectorAll(".clock").forEach(el => el.textContent = `${date} · ${time}`);
}
setInterval(tickClock, 10000);
// The "playing for" readout on the resume card moves with the clock.
setInterval(() => { if (S.gameRunning) updatePlayingMeta(); }, 30000);

/* Battery indicator: only shown for controllers that actually run on a battery
   (wireless pads report ALKALINE/NIMH); wired pads and no-pad show nothing. */
/**
 * Controller status: a pad icon, a battery bar and the percentage.
 *
 * `percent` is null when nothing can tell us the real charge -- XInput only reports four coarse
 * levels, and over Bluetooth often reports no battery at all. In that case the bar is drawn at
 * the coarse level and no number is shown, rather than inventing a precise-looking figure.
 */
function updateBattery(m) {
  const el = $("padBattery");
  el.classList.remove("low", "charging", "off");

  // The message's own `present` decides, not S.padConnected. The pad is usually detected while
  // the WebView is still starting, so that first "connected" push has no bridge to travel over
  // and is lost -- gating on it left a connected controller showing as missing.
  if (!m || m.present === false) {
    el.innerHTML = iconSvg("controllerOff");
    el.classList.add("off");
    el.title = "No controller connected";
    return;
  }
  S.padConnected = true;

  const pct = typeof m.percent === "number" ? Math.max(0, Math.min(100, m.percent)) : null;
  // Coarse levels are 0..3; -1 means even that is unknown, so show an empty-looking bar.
  const fill = pct !== null ? pct
             : m.level >= 0 ? [12, 38, 68, 100][Math.min(m.level, 3)]
             : 0;

  if (m.charging) el.classList.add("charging");
  else if (pct !== null && pct <= 15) el.classList.add("low");
  else if (pct === null && m.level === 0) el.classList.add("low");

  el.innerHTML =
    iconSvg("controller") +
    `<span class="batt"><span class="batt-fill" style="width:${fill}%"></span></span>` +
    (m.charging ? `<span class="batt-pct">${pct !== null ? pct + "%" : ""}⚡</span>`
                : pct !== null ? `<span class="batt-pct">${pct}%</span>` : "");
  el.title = m.charging ? "Controller charging"
           : pct !== null ? `Controller battery ${pct}%`
           : "Controller connected; battery level unavailable";
}

/* ============================== backdrop ============================== */

let bdFront = "bdA";
let bdCurrentKey = null;

/*
 * The backdrop follows the highlight, but not tile by tile during a fast run.
 *
 * Every change decodes a full-size hero, crossfades two screen-sized layers and re-blurs a third
 * (#backdrop::before, blur(64px) across the whole screen). Done on each step of a held key that
 * was the single most expensive thing in the frame, and it is what made the grid stutter under
 * the scroll. A run of steps now changes the text straight away and the picture once the
 * highlight settles -- which is also what a console dashboard does.
 */
const BACKDROP_SETTLE_MS = 170;
let bdDeferTimer = null, lastPaintAt = -Infinity;

function scheduleBackdrop(g, override) {
  const now = performance.now();
  const fast = now - lastPaintAt < BACKDROP_SETTLE_MS;
  lastPaintAt = now;
  if (!fast) { setBackdrop(g, override); return; }
  clearTimeout(bdDeferTimer);
  bdDeferTimer = setTimeout(() => { bdDeferTimer = null; setBackdrop(focusedGame(), focusedMediaOverride()); }, BACKDROP_SETTLE_MS);
}

/** `override` is a picture to hang instead of the game's own -- a screenshot highlighted in the
    page's gallery -- with the game's own pictures behind it as the fallbacks. */
function setBackdrop(game, override) {
  // A direct call -- the detail page and Settings clear it -- wins over one still waiting.
  clearTimeout(bdDeferTimer);
  bdDeferTimer = null;
  // Keyed on the picture, not the game. On the game id alone this skipped every repaint while
  // the highlight stayed put -- including the one after a metadata pass swapped the art out from
  // under it, which left the element pointing at a file that no longer existed and the screen
  // black until you moved. The id is still in the key so two games that share a fallback picture
  // do not confuse it.
  const url = game && (override || backdropUrl(game));
  const key = game ? game.id + "|" + (url || "") : "none";
  if (key === bdCurrentKey) return;
  bdCurrentKey = key;

  const front = $(bdFront);
  const backId = bdFront === "bdA" ? "bdB" : "bdA";
  const back = $(backId);

  const flat = () => {
    setArtAspect(back, 0, 0);
    if (!game) { back.style.backgroundImage = "none"; return; }
    const h = hashHue(game.title);
    back.style.backgroundImage = `linear-gradient(150deg, hsl(${h},18%,16%), hsl(${(h + 40) % 360},22%,7%))`;
  };
  const wrap = document.getElementById("backdrop");
  const show = (image) => {
    // The bed is one shared layer rather than one per crossfade slot: it is blurred past
    // recognition, so swapping it outright is invisible where crossfading two of them is just
    // two more full-screen filters for the compositor to run.
    if (image) wrap.style.setProperty("--bd-image", `url('${image}')`);
    else wrap.style.removeProperty("--bd-image");
    wrap.classList.toggle("has-art", !!image);
    back.classList.add("visible");
    front.classList.remove("visible");
    bdFront = backId;
  };

  if (!url) { flat(); show(null); return; }

  // Loaded rather than assigned, because the shape of the picture decides the height of the
  // element a theme hangs it in -- see setArtAspect. Waiting for the image also means the old
  // backdrop holds until the new one can be drawn at the right size, instead of appearing at the
  // wrong one and resizing. The key guard drops art the highlight has already moved past.
  //
  // Down the list on a failure rather than straight to a flat colour. A library entry can name a
  // file that is no longer on disk -- a download rejected for being the wrong shape is deleted,
  // and the field that pointed at it is not always cleared in the same pass -- and one stale name
  // should cost that picture, not the whole backdrop.
  const candidates = override ? [override, ...backdropUrls(game)] : backdropUrls(game);
  const tryFrom = (i) => {
    if (i >= candidates.length) { flat(); show(null); return; }
    queueArt(candidates[i], (src, w, h) => {
      if (bdCurrentKey !== key) return;
      if (!src) { tryFrom(i + 1); return; }
      back.style.backgroundImage = `url('${src}')`;
      setArtAspect(back, w, h);
      show(src);
    });
  };
  tryFrom(0);
}

/* ============================== trailers ==============================
   The focused game's trailer, played once the highlight has rested on it.

   Two surfaces, one clock. On the library the film goes into #backdrop, over the still art, after
   TRAILER_START_MS of the highlight sitting on one game -- but only under a theme that allows it
   (`--trailers` on #backdrop, none by default: Shelf's library stays as it was). The game's page
   has a player of its own under its shades, unless the theme draws the page over the backdrop and
   says so with `--trailer-surface: backdrop` on #screen-detail (Loungepad does): then the page is
   just another view of the same #backdrop film, which never stops, and the change of screen is
   the content crossfading over it. That is the only way to get a transition with no restart at
   all -- a <video> cannot change parents without a hiccup and an iframe cannot change them at all.

   Two kinds of source, one player. Steam's trailer is a plain mp4 URL: streamed from the store the
   first time, asked to be cached at the same moment (cacheTrailer), and played off disk once
   trailerCached has arrived; a cached file that fails to load -- evicted under us -- falls back to
   the stream, once. IGDB's is a YouTube id, because IGDB keeps no files, so it plays through
   YouTube's embedded player, chromeless, and is never cached. The URL says which it is.

   A theme that does not want the library film in some state writes `--trailers: none` on
   #backdrop, and the property is read here before every start. A menu over the library -- Y, the
   filter, the collection and manage sheets, a confirm -- leaves the film running underneath: it is
   what the menu is about. Typing a search, the Mods screen, the guide, a game running and the
   window being hidden stop it. syncTrailers is the one decision point and it is idempotent on
   purpose: every focus repaint and every screen change calls it.

   Nothing loops. A trailer ends on its title card and the still comes back; a screen restarting
   the same fifteen seconds is a screensaver, not a library. Moving away and back plays it again. */
const TRAILER_PRELOAD_MS = 1200;   // src assigned, so buffering gets a head start on the clock
const TRAILER_START_MS = 3000;     // and then it plays
const TRAILER_VOLUME = 0.7;        // trailers are mastered loud, and this is a living room

function isYouTubeUrl(url) { return /^https?:\/\/(www\.)?(youtube\.com|youtu\.be)\//i.test(url || ""); }
function youTubeId(url) {
  const m = /[?&]v=([\w-]{6,})/.exec(url) || /youtu\.be\/([\w-]{6,})/.exec(url) || /\/embed\/([\w-]{6,})/.exec(url);
  return m ? m[1] : null;
}

/** The length of an element's first transition, motion setting included. 0 when animations are off. */
function fadeMs(el) {
  const d = getComputedStyle(el).transitionDuration.split(",")[0].trim();
  const n = parseFloat(d);
  return !isFinite(n) ? 0 : d.endsWith("ms") ? n : n * 1000;
}

/* YouTube's player never runs in this page. Its API is a script off youtube.com, and a script runs
   with the authority of the page that loaded it -- here, the bridge and everything behind it. So
   the player lives in ui/player/player.html on an origin of its own (https://loungepad.player in
   the app; the same folder in the preview, where there is no second origin to be had), and this
   page drives it through postMessage. One frame per surface, made on the first YouTube trailer
   and never before: a library with only Steam games never loads it at all. */
const YT_PLAYER_PAGE = HOST ? "https://loungepad.player/player.html" : "player/player.html";
const YT_PLAYER_START_MS = 15000;   // a frame that never says ready (offline) is given up on
const YT_STATE = { UNSTARTED: -1, ENDED: 0, PLAYING: 1, PAUSED: 2, BUFFERING: 3 };

/* The two ways a film is shown, behind one small interface: load(url, at), play(), pause(),
   unload(), position(), setSound(on), loaded(), and `el`, the element that carries .playing. */
function videoBackend(el, on) {
  let wired = false;
  function wire() {
    if (wired) return;
    wired = true;
    el.addEventListener("playing", () => on.playing());
    el.addEventListener("ended", () => on.ended());
    el.addEventListener("error", () => on.error());
  }
  return {
    el,
    load(url) {
      wire();
      if (el.getAttribute("src") !== url) { el.preload = "auto"; el.src = url; el.load(); }
    },
    play() { const p = el.play(); if (p && p.catch) p.catch(() => { /* refused: the still stays */ }); },
    // quiet() is what a stop does on the way out; pause() and resume() are the viewer's.
    quiet() { try { el.pause(); } catch (e) { /* not playing */ } },
    pause() { try { el.pause(); } catch (e) { /* not playing */ } },
    resume() { const p = el.play(); if (p && p.catch) p.catch(() => {}); },
    paused() { return el.paused; },
    seek(d) { try { el.currentTime = Math.max(0, Math.min(isFinite(el.duration) ? el.duration : 0, el.currentTime + d)); } catch (e) { /* not seekable */ } },
    duration() { return isFinite(el.duration) ? el.duration : 0; },
    // Only once the fade is done: a <video> with no source draws a black box, and a black box
    // fading out is exactly the flash this is trying not to have.
    unload() { el.removeAttribute("src"); el.load(); },
    reload() { el.load(); },
    position() { return el.currentTime || 0; },
    setSound(sound) { el.muted = !sound; el.volume = TRAILER_VOLUME; },
    loaded() { return !!el.getAttribute("src"); },
  };
}

/* YouTube's player is not a <video>, and it dresses its film: a large play button until the
   first frame, a play/pause bezel in the middle as it starts, a title bar and a cards button
   along the top, captions when it feels like it, and a grid of suggested videos over the last
   frame. None of that is a trailer. What the embed allows is taken (controls off, keyboard off,
   annotations off), and the rest is worked around here: the fade-in waits YT_SHOW_DELAY_MS after
   playback starts so the button and the bezel have gone before the frame is shown; the captions
   module is unloaded on every start, which is the one way to keep them off; the film is cut
   YT_END_MARGIN_S before its end, so the end screen is drawn behind a wrapper already at zero;
   and the frame is overscanned (.trailer-yt in app.css) so the chrome bands at the top and the
   bottom fall outside the box. A stop mutes rather than pauses, because a paused embed shows
   the button again; the frame keeps moving under its own fade and is stopped after it. */
const YT_SHOW_DELAY_MS = 900;
const YT_END_MARGIN_S = 1.2;

function youTubeBackend(hostId, wrap, on) {
  let frame = null, origin = null, ready = null, videoId = null, sound = true;
  let showTimer = null, endWatch = false;
  // The player's clock, as it last reported it (every 250 ms): the viewer reads position and
  // duration synchronously, and a frame on another origin cannot be asked synchronously.
  let last = { t: 0, d: 0, s: YT_STATE.UNSTARTED };
  const stopWatching = () => { clearTimeout(showTimer); showTimer = null; endWatch = false; };
  const post = (m) => { if (frame && frame.contentWindow && origin) frame.contentWindow.postMessage(m, origin); };

  // Only the frame this backend made, and only from the origin it was given. Anything else that
  // posts to the page -- an embed inside the player's own frame, say -- is not ours and is dropped.
  const onMessage = (e) => {
    if (!frame || e.source !== frame.contentWindow || e.origin !== origin) return;
    const m = e.data;
    if (!m || typeof m.ev !== "string") return;
    if (m.ev === "state") {
      if (m.s === YT_STATE.PLAYING) {
        stopWatching();
        endWatch = true;
        showTimer = setTimeout(() => { showTimer = null; on.playing(); }, YT_SHOW_DELAY_MS);
      } else if (m.s === YT_STATE.ENDED) {
        stopWatching();
        on.ended();
      } else if (m.s === YT_STATE.PAUSED || m.s === YT_STATE.UNSTARTED) {
        stopWatching();
      }
    } else if (m.ev === "time") {
      last = { t: Number(m.t) || 0, d: Number(m.d) || 0, s: typeof m.s === "number" ? m.s : last.s };
      if (endWatch && last.d > 0 && last.t >= last.d - YT_END_MARGIN_S) { stopWatching(); on.ended(); }
    } else if (m.ev === "error") {
      stopWatching();
      on.error();
    }
  };

  const discard = () => {
    window.removeEventListener("message", onMessage);
    if (frame) { const host = document.createElement("div"); host.id = hostId; frame.replaceWith(host); }
    frame = null; origin = null; ready = null;
  };

  const create = () => ready || (ready = new Promise((resolve, reject) => {
    const host = $(hostId);
    if (!host) { reject(new Error("no host")); return; }
    const url = new URL(YT_PLAYER_PAGE, location.href);
    // The player page answers this origin and no other, and this page listens for that one.
    url.searchParams.set("o", location.origin);
    origin = url.origin;
    frame = document.createElement("iframe");
    frame.id = hostId;
    frame.src = url.href;
    frame.setAttribute("allow", "autoplay; encrypted-media");
    frame.setAttribute("title", "Trailer");
    frame.tabIndex = -1;
    let settled = false;
    const readyListener = (e) => {
      if (!frame || e.source !== frame.contentWindow || e.origin !== origin) return;
      if (e.data && e.data.ev === "ready") { settled = true; window.removeEventListener("message", readyListener); resolve(); }
      else if (e.data && e.data.ev === "error" && !settled) { settled = true; window.removeEventListener("message", readyListener); discard(); reject(new Error("YouTube player unavailable")); }
    };
    window.addEventListener("message", readyListener);
    window.addEventListener("message", onMessage);
    setTimeout(() => { if (!settled) { settled = true; window.removeEventListener("message", readyListener); discard(); reject(new Error("YouTube player did not start")); } }, YT_PLAYER_START_MS);
    host.replaceWith(frame);
  }));
  const applySound = () => { post({ cmd: sound ? "unmute" : "mute" }); post({ cmd: "volume", v: Math.round(TRAILER_VOLUME * 100) }); };
  return {
    el: wrap,
    load(url) {
      const id = youTubeId(url);
      create().then(() => {
        applySound();
        if (videoId === id) return;
        videoId = id;
        // Cued rather than loaded: loading plays at once, and the clock has not run yet.
        post({ cmd: "cue", id });
      }).catch(() => on.error());
    },
    play() { create().then(() => { applySound(); post({ cmd: "play" }); }).catch(() => { /* no player */ }); },
    // A stop is silence, not a pause: the frame goes on moving under the fade and is stopped
    // after it, because a paused embed puts YouTube's button back. The viewer's pause is a real
    // one, and the button it brings is the price of the user asking for a pause.
    quiet() { stopWatching(); post({ cmd: "mute" }); },
    pause() { post({ cmd: "pause" }); },
    resume() { post({ cmd: "play" }); },
    paused() { return !frame || (last.s !== YT_STATE.PLAYING && last.s !== YT_STATE.BUFFERING); },
    seek(d) { post({ cmd: "seek", t: Math.max(0, last.t + d) }); },
    duration() { return frame ? last.d : 0; },
    unload() { stopWatching(); videoId = null; last = { t: 0, d: 0, s: YT_STATE.UNSTARTED }; post({ cmd: "stop" }); },
    reload() { /* a YouTube error is a verdict on the video, not the line; nothing to retry */ },
    position() { return frame ? last.t : 0; },
    setSound(s) { sound = s; if (frame) applySound(); },
    loaded() { return !!videoId; },
  };
}

function makeTrailerPlayer(videoId, ytHostId, ytWrapId) {
  let game = null;          // the game on the clock or playing
  let armedKey = null;      // which film it is (filmKey), so a changed film is noticed
  let backend = null;       // which of the two is showing it
  let endedKey = null;      // the film that ran to the end and should not restart
  let timers = [];
  let unloadTimer = null;
  let fallingBack = false;
  let video = null, yt = null;

  const on = {
    playing: () => { if (game && backend) backend.el.classList.add("playing"); },
    ended: () => { endedKey = game ? armedKey : null; stop(); },
    error: () => {
      if (!game) return;
      // A cached copy that is no longer there (evicted, or the folder cleared by hand). Straight
      // to the stream, once; the host re-downloads on the next cacheTrailer.
      if (backend === video && !fallingBack && game.trailerUrl && game.trailerFile) {
        fallingBack = true;
        game.trailerFile = null;
        backend.load(game.trailerUrl);
        backend.play();
        return;
      }
      // A stream the CDN dropped on the first fetch -- Steam's does, now and then -- gets one
      // more go a moment later, on the same source.
      if (backend === video && !retried) {
        retried = true;
        const g = game, b = backend;
        timers.push(setTimeout(() => { if (game === g && backend === b) { b.reload(); b.play(); } }, 1500));
        return;
      }
      // Nothing left to try right now -- a YouTube video that will not embed, a stream that is
      // gone. The still stays, and the same film is not asked again for a while: not on every
      // repaint, which would hammer a dead link, and not never, because a closed connection is
      // usually a closed connection and not a verdict on the film.
      failed = { key: armedKey, at: performance.now() };
      stop();
    },
  };
  let failed = null, retried = false;
  const TRAILER_RETRY_MS = 30000;
  function backendFor(url) {
    if (isYouTubeUrl(url)) return yt || (yt = youTubeBackend(ytHostId, $(ytWrapId), on));
    return video || (video = videoBackend($(videoId), on));
  }
  function clearTimers() { timers.forEach(clearTimeout); timers = []; }

  /** Take the film down: fade, pause, and drop the source once it is out of sight. */
  function stop() {
    if (!game && !timers.length) return;
    clearTimers();
    const b = backend;
    game = null;
    backend = null;
    fallingBack = false;
    if (!b) return;
    b.el.classList.remove("playing");
    b.quiet();
    clearTimeout(unloadTimer);
    unloadTimer = setTimeout(() => b.unload(), fadeMs(b.el) + 40);
  }

  /**
   * Put a film on the clock. The same film again is a no-op, so a repaint never resets it; a
   * different film for the same game starts over. `delay` is how long the highlight has to rest
   * before it plays (the page's 3 s by default; the gallery's short beat; 0 for the viewer, which
   * plays at once), and `again` lets the viewer replay a film that ended or failed.
   */
  function arm(g, url, opts = {}) {
    if (!url) { stop(); return; }
    const key = filmKey(g, url);
    if (game && game.id === g.id && armedKey === key) return;
    if (opts.again) { if (endedKey === key) endedKey = null; if (failed && failed.key === key) failed = null; }
    if (endedKey === key) return;
    if (failed && failed.key === key && performance.now() - failed.at < TRAILER_RETRY_MS) return;
    stop();
    endedKey = null;
    retried = false;
    game = g;
    armedKey = key;
    backend = backendFor(url);
    // The old film may still be fading on this element; its unload would take the new source
    // with it, and the fade can outlast the preload delay, so the new source waits for it.
    clearTimeout(unloadTimer);
    backend.setSound(lookTrailerSound());
    const begin = () => {
      backend.load(url);
      // Only the game's own trailer is kept on disk; the rest of a gallery streams every time.
      if (key.startsWith("trailer:") && !isYouTubeUrl(url)) send({ cmd: "cacheTrailer", id: g.id });
    };
    const delay = opts.delay === undefined ? TRAILER_START_MS : opts.delay;
    if (delay <= 0) { begin(); backend.play(); return; }
    const preloadAt = Math.max(Math.min(TRAILER_PRELOAD_MS, delay), fadeMs(backend.el) + 80);
    timers.push(setTimeout(begin, preloadAt));
    timers.push(setTimeout(() => { if (game === g && backend && backend.loaded()) backend.play(); },
      Math.max(delay, preloadAt + 200)));
  }

  // The viewer's controls. Each is a question or an order to whichever backend has the film.
  function pause() { if (backend) backend.pause(); }
  function resume() { if (backend) backend.resume(); }
  function paused() { return backend ? backend.paused() : true; }
  function seek(d) { if (backend) backend.seek(d); }
  function position() { return backend ? backend.position() : 0; }
  function duration() { return backend ? backend.duration() : 0; }
  function active() { return !!game; }

  return { arm, stop, pause, resume, paused, seek, position, duration, active };
}

const libraryTrailer = makeTrailerPlayer("bdVideo", "bdYtHost", "bdYt");
const detailTrailer = makeTrailerPlayer("detailVideo", "detailYtHost", "detailYt");
const viewerTrailer = makeTrailerPlayer("mediaViewVideo", "mediaViewYtHost", "mediaViewYt");

/** Which surface should be showing which film right now, and make it so. */
function syncTrailers() {
  const mode = lookTrailers();
  // Nothing plays anywhere behind a game, in a hidden window or under the text field. The option
  // and the screens that are not about a game only silence the films the page plays on its own;
  // the viewer's film was asked for, and plays.
  // Resting counts as hidden: the screen is dark and a trailer's sound would carry on into the room.
  const resting = !!(S.rest && S.rest.phase && S.rest.phase !== "awake");
  const hard = S.gameRunning || overlayMode || document.visibilityState !== "visible" || inputOpen || resting;
  const quiet = hard || mode === "off" || !!modsState || guideOpen || !!mediaView;

  const inBackdrop = libraryCanHostTrailers();
  let lib = null, det = null, full = null;
  if (!quiet && view === "library" && mode === "all" && inBackdrop && !searchOpen) {
    // The library's own highlight, asked for by scope: with a menu up the active scope is the
    // menu, whose rows are not games, and asking it would stop the film the moment Y was pressed.
    const el = focusEl(document.getElementById("screen-library"));
    const g = el && el.dataset.gameId ? gameById(el.dataset.gameId) : null;
    const url = g ? trailerUrl(g) : null;
    if (url) lib = { g, url };
  } else if (!quiet && view === "detail") {
    const g = gameById(detailGameId);
    if (g) {
      // The strip decides: a highlighted film plays after a short beat (it was chosen), a
      // highlighted picture shows and no film runs, and off the strip the page plays its trailer.
      const item = focusedMediaItem();
      const url = item ? (item.kind === "video" ? item.url : null) : trailerUrl(g);
      if (url) {
        // A page the theme draws over the backdrop keeps the film there: the same player, the
        // same film, and arm() on the film already showing is a no-op, so nothing flickers.
        const page = $("screen-detail");
        const shared = inBackdrop && !!page
          && getComputedStyle(page).getPropertyValue("--trailer-surface").trim() === "backdrop";
        const plan = { g, url, delay: item ? GALLERY_DELAY_MS : undefined };
        if (shared) lib = plan; else det = plan;
      }
    }
  }
  if (!hard && mediaView && view === "detail") {
    const g = gameById(detailGameId), item = mediaViewItems()[mediaView.idx];
    if (g && item && item.kind === "video") full = { g, url: item.url, delay: 0 };
  }

  if (lib) libraryTrailer.arm(lib.g, lib.url, lib); else libraryTrailer.stop();
  if (det) detailTrailer.arm(det.g, det.url, det); else detailTrailer.stop();
  if (full) viewerTrailer.arm(full.g, full.url, full); else viewerTrailer.stop();
}

// The window going away -- parked behind a game, or hidden by the host -- and coming back.
document.addEventListener("visibilitychange", () => syncTrailers());

/* ============================== tab bars ============================== */

/* Empty on purpose. With one screen left there is nothing to switch between, and a lone "Library"
   tab was only a label taking up the top of the screen. The bars still render -- they carry the
   clock and the title count -- they simply have no tabs in them now. */
const TAB_DEFS = [];

function renderTabbars() {
  document.querySelectorAll("[data-tabbar]").forEach(bar => {
    const active = bar.dataset.tabbar;
    bar.innerHTML = "";
    TAB_DEFS.forEach((t, i) => {
      const el = document.createElement("div");
      el.className = "tab" + (t.id === active ? " tab-active" : "");
      el.textContent = t.label;
      el.dataset.tab = t.id;
      // Tabs are ordinary focusables now, so Up from the top row reaches them by geometry
      // rather than by a hardcoded zone list.
      el.dataset.focusable = "";
      el.dataset.action = "tab:" + t.id;
      el.addEventListener("mouseenter", () => {
        if (!hoverEnabled()) return;
        if (view === "library") { setFocusEl(el); updateLibraryFocus(true); }
      });
      el.addEventListener("click", () => { if (t.id !== view) switchView(t.id); });
      bar.appendChild(el);
    });
  });
}

/* ============================== tiles (shared) ============================== */

function makeAddTile(onHover, onClick) {
  const item = document.createElement("div");
  item.className = "grid-item add-tile";
  item.dataset.focusable = "";
  item.dataset.action = "addGame";
  item.innerHTML = `<div class="add-plus">+</div><div class="add-label">Add game</div>`;
  if (onHover) item.addEventListener("mouseenter", () => { if (hoverEnabled()) onHover(); });
  if (onClick) item.addEventListener("click", onClick);
  return item;
}

/**
 * The binding data a theme's templates see for one game.
 *
 * Formatted values sit alongside the raw ones on purpose: a template should be able to write
 * {{playtime}} without knowing that the app stores minutes, but {{playtimeMinutes}} is there
 * for a theme that wants to do its own thing with it.
 */
function gameView(g) {
  return {
    id: g.id, title: g.title, platform: g.platform, emulated: !!g.emulated,
    installed: g.installed, favorite: g.favorite, hidden: g.hidden,
    cover: coverUrl(g) || "", banner: bannerUrl(g) || "",
    hero: heroUrl(g) || "", logo: logoUrl(g) || "",
    backdrop: backdropUrl(g) || "",
    playtimeMinutes: g.playtimeMinutes || 0, sizeBytes: g.sizeBytes || 0,
    playtime: fmtPlaytime(g.playtimeMinutes),
    lastPlayed: fmtLastPlayed(g.lastPlayed),
    size: fmtSize(g.sizeBytes),
    sessions: g.sessions || 0,
    meta: g.installed ? shortMeta(g) : uninstalledMeta(g),
    initials: initials(g.title),
    // Fetched metadata. Empty string rather than undefined, so a template that prints one of
    // these for a game we know nothing about leaves a gap instead of the word "undefined".
    description: g.description || "",
    developer: g.developer || "",
    publisher: g.publisher || "",
    genres: (g.genres || []).join(", "),
    releaseDate: g.releaseDate || "",
    year: releaseYear(g.releaseDate) || "",
    score: typeof g.criticScore === "number" ? String(g.criticScore) : "",
    pegi: typeof g.pegiRating === "number" ? String(g.pegiRating) : "",
    // Achievements, for a template that wants to draw them: the counts as numbers, the share as
    // a whole-number string, and `achievements` as the thing to test with data-if.
    ...achievementView(g),
  };
}

/* A themed tile still gets the focus and identity attributes from here rather than trusting
   the template to carry them: a theme that forgets data-focusable would produce a grid you
   cannot navigate, which is a miserable thing to debug from a sofa. */
function themedTile(name, g, cls) {
  const el = Theme.render(name, gameView(g));
  if (!el) return null;
  el.classList.add(cls);
  if (!g.installed) el.classList.add("uninstalled");
  el.dataset.focusable = "";
  el.dataset.gameId = g.id;
  return el;
}

function makeGridTile(g, onHover, onClick, onDetails) {
  if (g.__add) return makeAddTile(onHover, onClick);

  let item = themedTile("game-tile", g, "grid-item");
  if (item) {
    if (onHover) item.addEventListener("mouseenter", () => { if (hoverEnabled()) onHover(); });
    if (onClick) item.addEventListener("click", onClick);
    if (onDetails) item.addEventListener("contextmenu", (e) => { e.preventDefault(); onDetails(); });
    return item;
  }

  item = document.createElement("div");
  item.className = "grid-item" + (g.installed ? "" : " uninstalled");
  // What makes the tile navigable and activatable; see the contract in nav.js.
  item.dataset.focusable = "";
  item.dataset.gameId = g.id;

  const art = document.createElement("div");
  art.className = "grid-art";
  applyArt(g, art, coverUrl(g));
  item.appendChild(art);

  const label = document.createElement("div");
  label.className = "grid-label";
  const meta = g.installed ? shortMeta(g) : uninstalledMeta(g);
  label.innerHTML = `<div class="grid-title">${esc(g.title)}</div><div class="grid-meta">${esc(meta)}${achievementChip(g)}</div>`;
  item.appendChild(label);

  if (g.favorite) {
    const star = document.createElement("div");
    star.className = "tile-fav";
    star.textContent = "★";
    item.appendChild(star);
  }

  if (onHover) item.addEventListener("mouseenter", () => { if (hoverEnabled()) onHover(); });
  if (onClick) item.addEventListener("click", onClick);
  if (onDetails) item.addEventListener("contextmenu", (e) => { e.preventDefault(); onDetails(); });
  return item;
}

/* ============================== library data ============================== */

function sortGames(list) {
  const score = (g) => (typeof g.criticScore === "number" ? g.criticScore : -1);
  const by = {
    // The default puts what you can play right now first. With a whole store account imported,
    // A to Z alone buried the dozen installed games among hundreds you would have to download.
    az: (a, b) => (b.installed ? 1 : 0) - (a.installed ? 1 : 0) || a.title.localeCompare(b.title),
    za: (a, b) => b.title.localeCompare(a.title),
    recent: (a, b) => new Date(b.lastPlayed || 0) - new Date(a.lastPlayed || 0),
    played: (a, b) => (b.playtimeMinutes || 0) - (a.playtimeMinutes || 0),
    sizeDesc: (a, b) => (b.sizeBytes || 0) - (a.sizeBytes || 0),
    sizeAsc: (a, b) => (a.sizeBytes || 0) - (b.sizeBytes || 0),
    // An unrated game sorts below a badly rated one rather than above it: a missing score is
    // not a low score, but a wall of blanks at the top is not what "highest rated" is for.
    // Ties fall back to the title so the order is stable between renders.
    score: (a, b) => score(b) - score(a) || a.title.localeCompare(b.title),
    // The unlocked share; a game with no achievements sorts last, like an unrated one above.
    ach: (a, b) => achievementShare(b) - achievementShare(a) || a.title.localeCompare(b.title),
  }[F.sort] || ((a, b) => a.title.localeCompare(b.title));
  return [...list].sort(by);
}

/** Tiles across one row of the all-games grid. Mirrors the width in .grid-item. */
const GRID_COLS = 9;

/** Games eligible for the library: everything the user hasn't hidden. */
function visibleGames() { return S.games.filter(g => !g.hidden); }

/* ============================== editions ==============================
 *
 * One game, several stores. 1000xRESIST owned on Steam and on Xbox is one tile, not two: the
 * library entries stay separate (each has its own install state, launch route and playtime, and
 * the host keys everything by id), and the page groups them by title for display.
 *
 * Which edition a tile stands for is decided in this order:
 *   1. the one the user picked under Manage → "Launch with", which the host remembers
 *   2. an installed one over one that is not -- the tile should launch, not offer a download
 *   3. the stores in EDITION_ORDER, Steam first, when more than one is installed
 *   4. the one with more playtime
 * The others are listed in the game menu ("Play on Xbox", "Install on Xbox") and under Manage.
 *
 * Two entries from the SAME store never group, even with identical titles: Steam sells two games
 * called exactly "DOOM", and merging them would hide one the account paid for.
 */
const EDITION_ORDER = ["Steam", "Epic", "GOG", "Xbox", "Manual"];
const EDITION_SUFFIXES = [
  "game of the year edition", "anniversary edition", "definitive edition", "enhanced edition",
  "complete edition", "ultimate edition", "standard edition", "special edition", "deluxe edition",
  "goty edition", "gold edition", "remastered",
];
let EDITIONS = new Map();   // game id -> { members: Game[] }

/* The same folding TitleMatch does on the host -- accents, apostrophes, "&", punctuation, one
   trailing edition -- plus the Microsoft Store's habit of labelling the PC build. */
function titleKey(title) {
  let s = String(title || "").toLowerCase()
    .replace(/&/g, " and ").replace(/['’‘´`]/g, "")
    .normalize("NFD").replace(/[̀-ͯ]/g, "").replace(/[™®©]/g, "");
  s = s.replace(/\s*\((?:game preview|early access|pc|windows(?: 1[01])?)\)\s*$/, "")
       .replace(/\s*[-–:]\s*(?:windows(?: 1[01])?|pc)(?: edition)?\s*$/, "")
       .replace(/[^a-z0-9]+/g, " ").trim();
  for (const suffix of EDITION_SUFFIXES)
    if (s.endsWith(" " + suffix)) { s = s.slice(0, -suffix.length - 1).trim(); break; }
  return s;
}

function rankEditions(members) {
  const order = (g) => { const i = EDITION_ORDER.indexOf(g.platform); return i < 0 ? 99 : i; };
  return [...members].sort((a, b) =>
    (b.preferredEdition ? 1 : 0) - (a.preferredEdition ? 1 : 0)
    || (b.installed ? 1 : 0) - (a.installed ? 1 : 0)
    || order(a) - order(b)
    || (b.playtimeMinutes || 0) - (a.playtimeMinutes || 0));
}

/* Rebuilt on every state push. Hidden games are left out of the grouping: hiding is done to a
   whole game (see the game menu), and a hidden one is its own entry in the hidden view. */
function buildEditions() {
  EDITIONS = new Map();
  const byKey = new Map();
  for (const g of S.games) {
    // A ROM never groups with a store copy, or with a ROM for another system: "Doom" on the SNES
    // and DOOM on Steam are different games that happen to share a name, and "Sonic the
    // Hedgehog" on the Genesis and on the Master System are two different games too.
    const key = g.hidden || g.emulated ? "" : titleKey(g.title);
    let group = null;
    if (key) {
      const groups = byKey.get(key) || [];
      byKey.set(key, groups);
      group = groups.find(x => !x.members.some(m => m.platform === g.platform)) || null;
      if (!group) { group = { members: [] }; groups.push(group); }
    } else {
      group = { members: [] };
    }
    group.members.push(g);
    EDITIONS.set(g.id, group);
  }
}

/** Every store this game is in, the one its tile launches first. */
function editionsOf(g) {
  const group = g && EDITIONS.get(g.id);
  return group ? rankEditions(group.members) : (g ? [g] : []);
}

/** The edition a tile for this game stands for. */
function primaryEdition(g) { return editionsOf(g)[0] || g; }

/*
 * One entry per game out of a list of library entries. When a filter has already narrowed the
 * list -- a platform filter of "Xbox", say -- the tile stands for the best edition that is still
 * IN the list, so filtering to Xbox shows the Xbox copy rather than hiding the game or showing
 * the Steam one.
 */
function collapseEditions(list) {
  const inList = new Set(list.map(g => g.id));
  const seen = new Set();
  const out = [];
  for (const g of list) {
    const group = EDITIONS.get(g.id);
    if (group) { if (seen.has(group)) continue; seen.add(group); }
    const members = group ? group.members : [g];
    const rep = rankEditions(members).find(m => inList.has(m.id)) || g;
    out.push({ rep, members });
  }
  return out;
}

/** Tell the host which edition launches, and apply it here straight away so the tile follows. */
function preferEdition(g) {
  const members = editionsOf(g);
  members.forEach(m => { m.preferredEdition = m.id === g.id; });
  send({ cmd: "preferEdition", id: g.id, siblings: members.filter(m => m.id !== g.id).map(m => m.id) });
}

/* Continue carousel geometry.
   Measured off the rendered tiles rather than assumed: a theme is free to change the tile and the
   gap, and Shelf's own tiles are not all one width -- each is cut to its picture, 300px for a
   Steam capsule and 368px for a 2.14:1 header (see .cont-art). So the track slides to the leading
   tile's real position, and how many fit is counted from wherever the row starts. */
const CONTINUE_MAX = 12;

/* How many recents the row shows. The built-in row is a carousel and takes twelve; a theme that
   lays them out with nowhere to scroll to says how many it has room for with `--continue-max` on
   the row (the Loungepad theme ties it to its column count, so the row is always exactly full).
   Read off the computed style, so it follows the theme's own options without the app knowing
   what they are. */
function continueMax() {
  const row = $("continueRow");
  const v = row ? parseInt(getComputedStyle(row).getPropertyValue("--continue-max"), 10) : NaN;
  return Number.isFinite(v) && v > 0 ? Math.min(v, CONTINUE_MAX) : CONTINUE_MAX;
}
const CONT_VIEWPORT = 1760;
let contScroll = 0;   // index of the leftmost visible tile

/** How far the track slides to put tile `i` at the start of the row. */
function contOffset(i) {
  const track = $("continueTrack");
  const first = track && track.children[0], lead = track && track.children[i];
  return first && lead ? lead.offsetLeft - first.offsetLeft : 0;
}

/* How many tiles fit with tile `start` leading, by walking them rather than dividing: the row's
   padding is cancelled by a negative margin so its width is not the usable width, and tiles need
   not all be one size, so the answer depends on where the row starts. */
function contPerView(start = 0) {
  const track = $("continueTrack");
  const kids = track ? track.children : [];
  const lead = kids[start];
  if (!lead) return 1;
  const row = $("continueRow");
  const width = row && row.clientWidth ? row.clientWidth : CONT_VIEWPORT;
  let n = 0;
  for (let k = start; k < kids.length; k++) {
    const el = kids[k];
    if (el.offsetLeft - lead.offsetLeft + el.offsetWidth > width + 1) break;
    n++;
  }
  return Math.max(1, n);
}

/** The furthest the row needs to go: the first start from which every remaining tile fits. */
function contMaxScroll() {
  const n = contItems.length;
  for (let s = 0; s < n; s++) if (s + contPerView(s) >= n) return s;
  return 0;
}

/**
 * Slide the carousel the minimum distance needed to keep the focused tile on screen.
 * `follow` is false for hover: letting the mouse drag the carousel makes tiles slide out
 * from under the cursor, which fires another hover and runs away.
 */
/** The carousel index the highlight is on, or null when it is elsewhere. */
function focusedContIndex() {
  const el = focusEl();
  if (!el || el.dataset.contIndex === undefined) return null;
  return parseInt(el.dataset.contIndex, 10);
}

function updateContinueScroll(follow) {
  const track = $("continueTrack");
  if (!track) return;
  const i = focusedContIndex();
  if (follow && i !== null) {
    if (i < contScroll) contScroll = i;
    else while (contScroll < i && i > contScroll + contPerView(contScroll) - 1) contScroll++;
  }
  contScroll = Math.max(0, Math.min(contScroll, contMaxScroll()));
  track.style.transform = `translateX(${-contOffset(contScroll)}px)`;
}

/* Right stick horizontal -> carousel. The host turns stick X into real HWHEEL events, so this
   also means a horizontal-scrolling mouse or trackpad drives the carousel. */
let hWheelAccum = 0;

function overlayOpen() {
  return inputOpen || filterOpen || !!gameMenu || collectOpen || manageOpen || !!choiceState || !!modsState || !!confirmState || guideOpen || !!mediaView
    || !!captureState || !!keyPick || !!achState || !!actState || !!sessionState || !!dayState || !!formState;
}

window.addEventListener("wheel", (e) => {
  if (Math.abs(e.deltaX) <= Math.abs(e.deltaY)) return;   // vertical intent: let it scroll normally
  if (overlayOpen() || view !== "library") return;
  if (focusedContIndex() === null) return;

  hWheelAccum += e.deltaX;
  let moved = false;
  while (Math.abs(hWheelAccum) >= 120) {
    const dir = Math.sign(hWheelAccum);
    hWheelAccum -= dir * 120;
    // Step along the carousel by moving focus, so the wheel and the D-pad end up in
    // exactly the same place rather than keeping two ideas of where the highlight is.
    if (!navMove(dir > 0 ? "Right" : "Left")) { hWheelAccum = 0; break; }
    const next = focusedContIndex();
    if (next === null) break;   // walked out of the carousel; stop rather than drift
    moved = true;
  }
  if (moved) {
    setInputMode("pad");   // the stick is navigating, so show the highlight it is moving
    updateLibraryFocus();
  }
}, { passive: true });

function libraryData() {
  // Hidden games are the whole library when you ask for them, and none of it otherwise. A
  // separate view rather than "show hidden as well": the point of asking is to look over what you
  // put away and take something back out, and mixing them back into 200 tiles is not that.
  const base = F.hidden ? S.games.filter(g => g.hidden) : visibleGames();
  // The platform filter narrows the entries BEFORE they are grouped into games, so it picks which
  // edition shows; everything else is a question about the game as a whole.
  let groups = collapseEditions(F.platforms.size ? base.filter(g => F.platforms.has(g.platform)) : base);
  if (F.fav) groups = groups.filter(x => x.members.some(m => m.favorite));
  if (F.collections.size) groups = groups.filter(x => x.members.some(gameInSelectedCollection));
  if (F.status.size === 1) {
    const wantInstalled = F.status.has("Installed");
    groups = groups.filter(x => x.rep.installed === wantInstalled);
  }
  const needle = searchKey(F.search);
  if (needle) groups = groups.filter(x => x.members.some(m => searchKey(m.title).includes(needle)));
  const filtered = groups.map(x => x.rep);

  const cont = collapseEditions(base.filter(g => g.lastPlayed && g.installed))
    .map(x => x.rep)
    .sort((a, b) => new Date(b.lastPlayed) - new Date(a.lastPlayed))
    .slice(0, continueMax());

  // The "add a game" tile always trails the grid so it's reachable without a menu.
  const items = [...sortGames(filtered), { __add: true }];
  const rows = [];
  // Nine, not eight. The tile is cut to 2:3 so box art is never cropped (see .grid-item), and at
  // eight across that shape would have been 199x298 -- tall enough to leave barely one row on
  // screen. Nine narrower columns keep the same 1760 run and the same row height.
  for (let i = 0; i < items.length; i += GRID_COLS) rows.push(items.slice(i, i + GRID_COLS));
  return { cont, rows, total: filtered.length };
}

function filterSummary(total) {
  const bits = [`${total} TITLE${total === 1 ? "" : "S"}`];
  if (F.platforms.size) bits.push([...F.platforms].join(" + ").toUpperCase());
  if (F.fav) bits.push("FAVORITES");
  if (F.collections.size) bits.push(S.collections.filter(c => F.collections.has(c.id)).map(c => c.name.toUpperCase()).join(" + "));
  if (F.status.size === 1) bits.push([...F.status][0].toUpperCase());
  if (F.hidden) bits.push("HIDDEN");
  if (F.search) bits.push(`MATCHING “${F.search.toUpperCase()}”`);
  const sort = SORTS.find(s => s.id === F.sort);
  if (F.sort !== "az" && sort) bits.push(sort.label.toUpperCase());
  // Only as a note on the library you ARE looking at. While the hidden view is on, these games
  // are the list, so counting them off to one side says the opposite of what it means.
  const hidden = S.games.length - visibleGames().length;
  if (hidden > 0 && !F.hidden) bits.push(`${hidden} HIDDEN`);
  return bits.join(" · ");
}

/* ============================== library render ============================== */

/** The running game shown as a banner above Continue, so it is the first thing focus lands on. */
function renderPlaying() {
  const sec = $("playingSection");
  const g = S.gameRunning ? gameById(S.runningGameId) : null;
  if (!g) { sec.style.display = "none"; return; }

  sec.style.display = "";
  const art = $("playingArt");
  art.className = "playing-art";
  art.innerHTML = "";
  art.style.background = "";
  applyArt(g, art, bannerUrl(g));
  $("playingName").textContent = g.title;
  // Until a process of the game has been seen it is only starting, which is also the state a
  // launch that went nowhere sits in; the label says so instead of claiming it is running.
  const label = sec.querySelector(".section-label");
  if (label) label.textContent = S.gameStarting ? "STARTING" : "RUNNING NOW";
  updatePlayingMeta();

  const card = $("playingCard");
  // data-role tells libraryAccept this tile resumes rather than relaunches.
  card.dataset.gameId = g.id;
  card.dataset.role = "playing";
  card.onmouseenter = () => { if (hoverEnabled()) { setFocusEl(card); updateLibraryFocus(true); } };
  card.onclick = () => { setFocusEl(card); updateLibraryFocus(true); send({ cmd: "resumeGame" }); };
}

function renderLibrary() {
  const { cont, rows, total } = libraryData();
  renderPlaying();
  contItems = cont;
  gridRows = rows;
  // Not only from revealFocus: scrolling with the wheel never moves the pad focus, and the top
  // fade still has to come on.
  const pageScroll = libraryScroller();
  const keepTop = pageScroll.scrollTop;
  watchScrolled(pageScroll);

  // Games, not entries: a game owned on two stores is one title.
  const titles = collapseEditions(visibleGames()).length;
  $("titleCount").textContent = `${titles} TITLE${titles === 1 ? "" : "S"}`;
  $("gridLabel").textContent = filterSummary(total);

  // Continue carousel (landscape banner art)
  const rowEl = $("continueRow");
  rowEl.innerHTML = "";
  const track = document.createElement("div");
  track.className = "continue-track";
  track.id = "continueTrack";
  rowEl.appendChild(track);
  $("continueSection").style.display = cont.length ? "" : "none";
  cont.forEach((g, i) => {
    let item = themedTile("continue-tile", g, "cont-item");
    if (!item) {
      item = document.createElement("div");
      item.className = "cont-item";

      const art = document.createElement("div");
      art.className = "cont-art";
      applyArt(g, art, bannerUrl(g));

      const meta = document.createElement("div");
      meta.className = "cont-meta";
      meta.innerHTML = `<div class="cont-title">${esc(g.title)}</div><div class="cont-sub">${esc(shortMeta(g))}${achievementChip(g)}</div>`;

      item.appendChild(art); item.appendChild(meta);
    }
    // A game can sit in both the carousel and the grid, so the key has to say which one
    // this is -- on the bare game id the highlight would jump between them.
    item.dataset.focusable = "";
    item.dataset.gameId = g.id;
    item.dataset.focusKey = "cont:" + g.id;
    item.dataset.contIndex = i;
    item.addEventListener("mouseenter", () => {
      if (!hoverEnabled()) return;
      setFocusEl(item); updateLibraryFocus(true);
    });
    item.addEventListener("click", () => { setFocusEl(item); updateLibraryFocus(true); libraryAccept("A"); });
    track.appendChild(item);
  });

  // Grid. Emptying a scroller snaps its scrollTop to 0, and this runs on every state push -- the
  // end of a scan, the end of a metadata pass, a favourite toggled -- so mid-browse the rows
  // jumped down to the top and the reveal glided them back up to the highlight. Hold the position
  // across the rebuild; revealFocus moves it from there only if it has to. In Shelf that is the
  // page's position, which the emptied grid (and the emptied row above) would clamp just the same.
  const scroll = $("gridScroll");
  scroll.innerHTML = "";
  if (total === 0) {
    const note = document.createElement("div");
    note.className = "empty-note";
    note.innerHTML = S.scanning
      ? "Scanning your Steam, Epic, GOG and Xbox libraries and your ROM folders…"
      : F.search
        ? `No games match “${esc(F.search)}”. Press ${slot("View")} to change the search, or ${slot("B")} to clear it.`
      : (visibleGames().length
        ? `Nothing matches the current filter. Press ${slot("X")} to change it, or ${slot("Y")} to reset.`
        : "No games found yet. Use the <b>+ Add game</b> tile below, or rescan from <b>Settings → Library</b>.");
    scroll.appendChild(note);
  }
  rows.forEach((row, r) => {
    const rowDiv = document.createElement("div");
    rowDiv.className = "grid-row";
    rowDiv.dataset.dimGroup = "";
    row.forEach((g, c) => {
      const item = makeGridTile(g, null, null);
      item.dataset.focusKey = g.__add ? "action:addGame" : "tile:" + g.id;
      item.addEventListener("mouseenter", () => { if (hoverEnabled()) { setFocusEl(item); updateLibraryFocus(true); } });
      item.addEventListener("click", () => { setFocusEl(item); updateLibraryFocus(true); libraryAccept("A"); });
      rowDiv.appendChild(item);
    });
    scroll.appendChild(rowDiv);
  });
  pageScroll.scrollTop = keepTop;

  clampFocus();
  updateLibraryFocus();
}

/** The focused element's game, including the trailing "add game" tile as null. */
function focusedCell() {
  const el = focusEl();
  if (!el) return null;
  if (el.dataset.action === "addGame") return { __add: true };
  return el.dataset.gameId ? gameById(el.dataset.gameId) : null;
}

/* Focus survives a re-render by key, but the thing it was on can disappear -- a filter
   change, a game uninstalled, the running game exiting. Fall back to the first focusable
   rather than leaving the highlight nowhere. */
function clampFocus() { ensureFocus(Nav.activeScope()); }

function updateLibraryFocus(noScroll) {
  paintNav();
  if (noScroll) return;
  const el = focusEl();
  if (el) revealFocus(el);
}

/* ============================== library nav ============================== */

function libraryNav(btn) {
  // A step from the recents (or the Playing card) into the grid lands with All games filling the
  // view (gridJumpFor). Asked before the step, whose own reveal moves the scroll target.
  const grid = $("gridScroll"), page = libraryScroller();
  const before = focusEl();
  const jump = before && !grid.contains(before) ? gridJumpFor(page, 1, 0) : null;
  if (!navMove(btn)) return;
  const now = focusEl();
  if (jump !== null && now && grid.contains(now)) startGridJump(page, jump);
}

/* What A / Y do is read off the focused element, not inferred from which zone the
   highlight is in. That is the whole point: a theme can put a launchable tile
   anywhere, or invent a row of its own, and activation still works. */
const ACTIONS = {
  addGame: () => send({ cmd: "addManual" }),
  search: () => openSearch(),
  resume: () => send({ cmd: "resumeGame" }),
  "tab:library": () => switchView("library"),
};

function libraryAccept(btn) {
  if (!focusVisible()) return;   // pointer is over empty space: nothing is armed
  const el = focusEl();
  if (!el) return;

  const g = el.dataset.gameId ? gameById(el.dataset.gameId) : null;
  if (g) {
    // The running game resumes rather than relaunching; the element says which it is.
    const running = el.dataset.role === "playing";
    if (btn === "A") {
      if (running) send({ cmd: "resumeGame" });
      else if (!g.installed) offerInstall(g);
      else launchGame(g);
    } else if (btn === "Y") {
      openGameMenu(g.id, "library");
    }
    return;
  }

  if (btn !== "A") return;
  const act = ACTIONS[el.dataset.action];
  if (act) act();
}

function libraryInput(btn) {
  switch (btn) {
    case "Up": case "Down": case "Left": case "Right": libraryNav(btn); break;
    case "A": case "Y": libraryAccept(btn); break;
    case "X": openFilter(); break;
    // View is the button with the two squares, left of the guide button. LB and RB were free too,
    // but RB is the keyboard toggle by default and the pair is a minimize combo option.
    case "View": openSearch(); break;
    // The Stats screen: play sessions and achievements across the library. LB was free (see
    // above), and it is claimed like View so a keyboard toggle bound to it still reaches here.
    case "LB": openStats("library"); break;
    // Settings lost its tab, so this is the way in. Menu is the pad's ☰ button; holding it is
    // still the keyboard toggle, and only a tap gets here.
    case "Menu": switchView("settings"); break;
    // B walks back to how the library opened: the first press goes up to the recents (the page
    // scrolls back to the top), the next one clears a standing search. Pressed repeatedly it
    // always ends on the whole library with the highlight at the top.
    case "B": {
      const top = libraryTop();
      const cur = focusEl();
      const page = libraryScroller();
      if (top && !atLibraryTop(cur)) {
        setFocusEl(top);
        // The page goes back to the top too, or the grid under the recents would be showing
        // wherever the highlight had got to. A theme whose grid scrolls on its own: its first row.
        if (page && page.scrollTop > 0) animateScroll(page, "y", 0);
        afterFocusMove();
      } else if (page && scrollTarget(page, "y") > 1) {
        // Already on the recents, but the stick or a wheel has scrolled the page away from them.
        animateScroll(page, "y", 0);
      } else if (F.search) {
        setSearch("");
        renderLibrary();
        pulse($("gridScroll"));
      }
      renderLibraryLegend();
      break;
    }
  }
}

/** Where B takes the highlight: the first recent game, or the first tile when there are none. */
function libraryTop() {
  const list = Nav.focusables(document.getElementById("screen-library"));
  return list.find(el => el.closest("#continueRow")) || list.find(el => el.closest("#gridScroll")) || null;
}

/** The highlight is already as far back as B takes it: on the recents (any of them), on the
    running game's card above them, or on the first tile of a library with no recents. */
function atLibraryTop(cur) {
  if (!cur) return false;
  if (cur.closest("#continueRow, #playingSection")) return true;
  return cur === libraryTop();
}

function launchGame(g) {
  if (S.gameRunning) {
    // Already playing something else. Offer the swap rather than just refusing -- from the
    // couch, "a game is already running" left you with nothing to do about it.
    if (S.runningGameId === g.id) { send({ cmd: "resumeGame" }); return; }
    const running = gameById(S.runningGameId);
    confirmState = {
      title: running ? `Close ${running.title}?` : "Close the running game?",
      body: `${g.title} will start once it has closed.`,
      yesLabel: `Close and play ${g.title}`,
      icon: "play", danger: false,
      onYes: () => { toast(`Closing ${running ? running.title : "the game"}…`); send({ cmd: "launch", id: g.id, replace: true }); },
    };
    confirmIdx = 0;
    showOverlay("overlay-confirm");
    renderConfirm();
    return;
  }
  toast(`Launching ${g.title}…`);
  send({ cmd: "launch", id: g.id });
}

/* The host decides what can be installed and writes the store's own URI onto the entry --
   steam://install, Epic's launcher, goggalaxy://, the Microsoft Store -- so the page only has to
   ask whether there is one. An installed game never carries it. */
function canInstall(g) { return !!g.installUri; }

const STORE_NAMES = { Steam: "Steam", Epic: "the Epic Games Launcher", GOG: "GOG Galaxy", Xbox: "the Microsoft Store" };
function storeName(g) {
  // A GOG game on a PC without Galaxy opens its own page on gog.com, where the installer is.
  if (g.platform === "GOG" && /^https:\/\/www\.gog\.com/.test(g.installUri || "")) return "gog.com";
  return STORE_NAMES[g.platform] || g.platform;
}

/* A confirm rather than a straight send, for two reasons. The launcher steps aside for the
   store's window -- it is topmost on the TV and the window would open behind it -- and vanishing
   on one press of A is a surprise; and the body is the only place to say how to come back. */
function offerInstall(g) {
  if (!canInstall(g)) { toast(`${g.title} is not installed`); return; }
  const combo = S.settings && S.settings.minimizeCombo && S.settings.minimizeCombo !== "Off"
    ? S.settings.minimizeCombo : null;
  const store = storeName(g);
  const Store = store.charAt(0).toUpperCase() + store.slice(1);
  confirmState = {
    title: `Install ${g.title}?`,
    body: `${Store} opens with ${g.title} ready to install. Loungepad steps aside while it does` +
      (combo ? `; press ${comboName(combo, padFamily)} to come back.` : "; start Loungepad again to come back.") +
      " The tile turns playable once the download has finished.",
    yesLabel: `Install with ${store.replace(/^the /, "")}`,
    icon: "download", danger: false,
    onYes: () => send({ cmd: "install", id: g.id }),
  };
  confirmIdx = 0;
  showOverlay("overlay-confirm");
  renderConfirm();
}

/* ============================== detail ============================== */

function openDetail(id, from) {
  detailGameId = id;
  detailReturn = from || "library";
  clearFocus(document.getElementById("screen-detail"));
  switchView("detail");
}

/* Metacritic's own bands, because the colour is only a shorthand if it matches the one people
   already know from the site: green 75+, yellow 50-74, red below. */
function scoreBand(n) { return n >= 75 ? "good" : n >= 50 ? "mixed" : "poor"; }

/*
 * Release dates arrive in whatever shape the source kept them in. Ours is an ISO timestamp, which
 * is the one worth rewriting: "2024-02-02T00:00:00Z" is not something to put on a page.
 *
 * Its three numbers are read straight out of the string rather than through Date's parser, which
 * would take the Z at its word, convert to local time on the way back out, and print every release
 * a day early anywhere west of Greenwich. A release date carries no time of day to convert.
 *
 * Everything else is passed through untouched -- Steam writes "2 Feb, 2024", which already reads
 * fine, and it also writes "Q1 2024" and bare years, which any reformatting would have to guess a
 * day for and would get wrong.
 */
function fmtReleased(date) {
  if (!date) return null;
  const iso = /^(\d{4})-(\d{2})-(\d{2})/.exec(String(date));
  if (!iso) return String(date);
  const d = new Date(+iso[1], +iso[2] - 1, +iso[3]);
  if (isNaN(d)) return String(date);
  return `${d.getDate()} ${d.toLocaleString("en", { month: "short" })} ${d.getFullYear()}`;
}

function releaseYear(date) {
  const m = /\b(\d{4})\b/.exec(date || "");
  return m ? m[1] : null;
}

/*
 * The numbers along the bottom. Built rather than hard-coded so a stat that has nothing to say
 * can be left out entirely: "SESSIONS —" next to "AVG SESSION —" on a game you have never opened
 * is four words saying nothing, and a row of dashes is what made this page feel like a form.
 *
 * Playtime and size are always shown, even at zero, because their absence would read as missing
 * data rather than as a game you have not played.
 */
function renderDetailStats(g) {
  const el = $("detailStats");
  const stats = [];
  const add = (label, value) => { if (value) stats.push({ label, value }); };

  stats.push({ label: "PLAYTIME", value: fmtPlaytime(g.playtimeMinutes) });
  add("SESSIONS", g.sessions > 0 ? String(g.sessions) : null);
  // Worth more than the raw total: it says whether this is a game you dip into or disappear into.
  add("AVG SESSION", g.sessions > 0 && g.playtimeMinutes > 0
    ? fmtPlaytime(g.playtimeMinutes / g.sessions) : null);
  add("LAST PLAYED", g.lastPlayed ? fmtLastPlayed(g.lastPlayed) : null);
  // The unlocked share, with a hairline of progress under it: the one number on this page that
  // says how far through the game you are rather than how long you have spent in it.
  const ach = achievementSummary(g);
  if (ach && ach.total > 0) stats.push({ label: "ACHIEVEMENTS", value: `${ach.unlocked} / ${ach.total}`, bar: ach.unlocked / ach.total });
  add("RELEASED", fmtReleased(g.releaseDate));
  // A game that was never installed has no size on record, and a dash in a stats row reads as
  // something missing rather than something unknowable.
  if (g.installed || g.sizeBytes) stats.push({ label: g.installed ? "ON DISK" : "DOWNLOAD", value: fmtSize(g.sizeBytes) });

  el.innerHTML = stats.map(s =>
    `<div class="stat"><div class="stat-label mono">${esc(s.label)}</div>` +
    `<div class="stat-value">${esc(s.value)}</div>` +
    (s.bar !== undefined ? `<div class="stat-bar"><i style="width:${Math.round(s.bar * 100)}%"></i></div>` : "") +
    `</div>`).join("");
}

/*
 * The boards' own marks, in ui/ratings. Taken from Wikimedia Commons (PEGI_18.svg, ESRB_Teen.svg,
 * ESRB_RP.svg and their siblings), where they are public domain as plain text logos -- they are
 * still the boards' trademarks. ESRB's files came with no viewBox, and one was added to each:
 * without it an <img> draws the art at 215x300 whatever size the box is and crops the rest.
 */
const ESRB_MARKS = {
  E: ["esrb-e", "Everyone"], "E10+": ["esrb-e10", "Everyone 10+"], T: ["esrb-t", "Teen"],
  M: ["esrb-m", "Mature 17+"], AO: ["esrb-ao", "Adults Only 18+"], RP: ["esrb-rp", "Rating Pending"],
};
const PEGI_AGES = [3, 7, 12, 16, 18];

/*
 * The one age rating a game's page shows, with the reasons that go under it: the board picked in
 * Settings, else the other one. Falling back cannot be mistaken for the board asked for, because
 * the mark is that board's own logo. The descriptors always come from the same board as the mark
 * -- PEGI's "Bad Language" under an ESRB M is one board's judgement credited to another.
 */
function ageRating(g) {
  const esrb = ESRB_MARKS[g.esrbRating] && {
    board: "ESRB", file: ESRB_MARKS[g.esrbRating][0],
    label: `ESRB ${ESRB_MARKS[g.esrbRating][1]}`, descriptors: g.esrbDescriptors || [],
  };
  const pegi = PEGI_AGES.includes(g.pegiRating) && {
    board: "PEGI", file: `pegi-${g.pegiRating}`,
    label: `PEGI ${g.pegiRating}`, descriptors: g.pegiDescriptors || [],
  };
  return (S.settings && S.settings.ageRatingBoard === "PEGI" ? pegi || esrb : esrb || pegi) || null;
}

/*
 * What other people made of the game: the critic score, and the age it is rated for.
 *
 * Both are given a panel rather than a number in the middle of the facts line. A bare "82" next
 * to the developer's name is a number with no unit -- it could be a rank, a count or a percentage
 * -- and a bare "16" is worse, because it looks like one of those too. With a scale and the name
 * of whoever said it, each reads at a glance from across a room, which is the whole job.
 *
 * The score is Metacritic's or there is none: the host takes it from Steam's store and nowhere
 * else. IGDB's own average used to fill the gaps under its own name, and a badge that changed
 * scale from one game to the next read worse than a gap.
 */
/*
 * The score drawn the way Metacritic draws it -- the number on a tile in its band's colour -- with
 * Metacritic's name under it, since a coloured square alone does not say whose score it is. The
 * age rating needs no caption: it is the board's own logo (ageRating).
 */
function metascoreBadge(n) {
  return `<div class="metascore" data-band="${scoreBand(n)}" role="img" ` +
    `aria-label="Metacritic score ${n} out of 100">` +
    `<div class="metascore-tile${n >= 100 ? " three" : ""}">${n}</div>` +
    `<div class="metascore-name">METACRITIC</div></div>`;
}

function renderDetailRatings(g) {
  const el = $("detailRatings");
  const parts = [];

  if (typeof g.criticScore === "number") parts.push(metascoreBadge(Math.round(g.criticScore)));

  // The board's real mark rather than one drawn in the page's materials: it is the picture
  // everybody already knows from the corner of a box, and nobody has to read it to know it.
  const age = ageRating(g);
  if (age) {
    parts.push(`<img class="age-mark" data-board="${age.board}" src="ratings/${age.file}.svg" ` +
      `alt="${esc(age.label)}" draggable="false">`);
  }

  // Empty rather than a row of "unrated" placeholders: most indies carry neither, and saying so
  // twice on every one of them is what made this page read like a form.
  el.innerHTML = parts.join("");
}

/*
 * Why the board rated it what it did, in the board's own words -- "Blood and Gore", "Mild
 * Lyrics". It belongs under the description because that is where the rest of the pitch is, and
 * it is the one line on this page that says something about the CONTENT rather than about the
 * file: everything else below is playtime, size and dates.
 */
function renderDetailDescriptors(g) {
  const el = $("detailDescriptors");
  const age = ageRating(g);
  const list = age ? age.descriptors : [];
  el.innerHTML = list.map(d => `<span class="descriptor">${esc(d)}</span>`).join("");
}

/*
 * Where the game came from, drawn in the same stroked 24x24 style as every other icon here
 * rather than pasted in as four brand logos: the set stays one family, it inherits currentColor,
 * and it costs nothing to load. The name sits beside it, because a silhouette alone is a
 * guessing game for anyone who does not already know the mark.
 */
const PLATFORM_ICONS = {
  Steam: "steam", Epic: "epic", GOG: "gog", Xbox: "xbox", Manual: "file",
};

/** The mark for where a game came from: its store's, or the cartridge for anything emulated. */
function platformIcon(g) { return g.emulated ? "cartridge" : PLATFORM_ICONS[g.platform] || "store"; }

function renderDetailPlatform(g) {
  const el = $("detailPlatform");
  const icon = platformIcon(g);
  // For a ROM the useful second fact is which program runs it, not which other stores have it.
  const emu = emulatorFor(g);
  const others = g.emulated ? [] : editionsOf(g).filter(m => m.id !== g.id).map(m => m.platform);
  el.innerHTML = (icon ? iconSvg(icon) : "") + `<span>${esc(g.platform)}</span>` +
    (others.length ? `<span class="also-on">also on ${esc(others.join(", "))}</span>` : "") +
    (g.emulated ? `<span class="also-on">${emu ? "via " + esc(emu.name) : "no emulator set"}</span>` : "");
}

/*
 * The line under the title. It used to say how the game launches and where its files are, which
 * is troubleshooting detail on the one screen meant to sell you on playing something -- that has
 * moved to Manage, where you go when you actually want to change it.
 *
 * Everything here is optional. A game with no fetched metadata falls back to its platform, so the
 * row is never empty and never a row of placeholder dashes.
 */
function renderDetailFacts(g) {
  const row = $("detailFacts");
  row.innerHTML = "";

  const add = (cls, text) => {
    const el = document.createElement("span");
    el.className = cls;
    el.textContent = text;
    row.appendChild(el);
  };

  const bits = [];
  const year = releaseYear(g.releaseDate);
  if (year) bits.push(year);
  if (g.developer) bits.push(g.developer);
  // Only when it is somebody else. On the great majority of games the publisher is the
  // developer, and printing the same name twice reads as a mistake.
  if (g.publisher && g.publisher !== g.developer) bits.push(g.publisher);
  // Three is what fits before the row starts wrapping, and the first three are the useful ones --
  // Steam lists "Indie" and "Casual" after whatever the game actually is.
  if (g.genres && g.genres.length) bits.push(g.genres.slice(0, 3).join(", "));
  if (!bits.length) bits.push(g.platform);

  bits.forEach((b, i) => {
    if (i) add("fact-dot", "·");
    add("fact", b);
  });

  // Worth its own chip rather than a word in the list: on a couch it is the difference between
  // starting the game and going to find a keyboard.
  if (g.controllerSupport === "full") add("fact-chip", "Full controller support");
  else if (g.controllerSupport === "partial") add("fact-chip", "Partial controller support");

  if (!g.installed) add("fact-chip fact-warn", "Not installed");
}

function renderDetail() {
  const g = gameById(detailGameId);
  if (!g) { switchView(detailReturn); return; }

  $("detailCrumb").textContent = g.platform.toUpperCase();
  $("detailTitle").textContent = g.title;
  $("detailFav").style.display = g.favorite ? "" : "none";
  renderDetailLegend(g);

  // The wordmark, where the art we already fetch has one. It is the game's own lettering rather
  // than ours, which is most of what makes this page look like a storefront instead of a form.
  // The text title stays in the DOM as the fallback and for anything reading the page.
  const logo = $("detailLogo"), titleRow = $("detailTitleRow");
  const logoSrc = logoUrl(g);
  logo.hidden = !logoSrc;
  titleRow.hidden = !!logoSrc;
  if (logoSrc) {
    logo.style.backgroundImage = `url('${logoSrc}')`;
    logo.setAttribute("aria-label", g.title);
  }

  renderDetailFacts(g);
  renderDetailRatings(g);
  renderDetailPlatform(g);
  $("detailDesc").textContent = g.description || "";
  renderDetailDescriptors(g);
  renderDetailStats(g);

  $("playLabel").textContent = !g.installed
    ? (canInstall(g) ? "Install" : "Not installed")
    : (g.playtimeMinutes > 0 ? "Continue" : "Play");
  // Achievements only for a game that can have a list; Stats for every game, because its sheet is
  // also where a session Loungepad did not see is logged by hand. Hidden elements are not
  // focusables, so the highlight simply never lands on a missing one.
  const achBtn = document.querySelector('#detailActions [data-act="achievements"]');
  const actBtn = document.querySelector('#detailActions [data-act="activity"]');
  if (achBtn) achBtn.hidden = !canHaveAchievements(g);
  if (actBtn) actBtn.hidden = false;

  // The gallery under the blurb, then the art: the page's own, or the screenshot the gallery's
  // highlight is on. Rebuilt on every push, the strip keeps its place by focus key like the grid.
  renderDetailMedia(g);
  detailArtUrl = null;
  updateDetailArt(g, focusedMediaOverride());

  document.querySelectorAll("#detailActions .pill-btn").forEach(el => {
    el.onmouseenter = () => { if (hoverEnabled()) { setFocusEl(el); paintNav(); } };
    el.onclick = () => { setFocusEl(el); paintNav(); detailActivate(); };
  });

  updateDetailFocus();
  syncTrailers();
}

/* ============================== the gallery ==============================
   The store page's films and pictures, in a strip under the blurb on the game's page: the
   trailer first (the film the page plays on its own), then every other film and every
   screenshot Steam or IGDB lists for it. Walking along the strip changes the picture behind
   the page -- a film plays after a short beat, a screenshot hangs where the hero would -- and
   A opens the highlighted one whole, in a viewer of its own with playback under the pad. */
const GALLERY_DELAY_MS = 700;      // a film highlighted in the strip starts after this, not the 3 s
const MEDIA_ITEM_W = 168, MEDIA_GAP = 12, MEDIA_STRIP_W = 900;   // as .media-item / .media-strip

function youTubeThumb(url) {
  const id = isYouTubeUrl(url) ? youTubeId(url) : null;
  return id ? `https://i.ytimg.com/vi/${id}/hqdefault.jpg` : null;
}

/** The strip's items for a game, trailer first. Empty for a game with nothing. */
function detailMediaItems(g) {
  if (!g) return [];
  const items = [];
  const media = Array.isArray(g.media) ? g.media : [];
  const t = trailerUrl(g);
  if (t) {
    const own = media.find(m => m && m.kind === "video" && m.url === g.trailerUrl);
    items.push({ kind: "video", url: t, thumb: (own && own.thumb) || youTubeThumb(t) || bannerUrl(g), name: (own && own.name) || "Trailer" });
  }
  for (const m of media) {
    if (!m || !m.url) continue;
    if (m.kind === "video" && m.url === g.trailerUrl) continue;   // the trailer is item 0 already
    items.push({ kind: m.kind === "video" ? "video" : "image", url: m.url, thumb: m.thumb || m.url, name: m.name || null });
  }
  return items;
}

/** The strip item under the highlight, or null when the highlight is elsewhere on the page. */
function focusedMediaItem() {
  if (view !== "detail" || mediaView) return null;
  const el = focusEl(document.getElementById("screen-detail"));
  if (!el || el.dataset.mediaIndex === undefined) return null;
  return detailMediaItems(gameById(detailGameId))[parseInt(el.dataset.mediaIndex, 10)] || null;
}

/** The screenshot to hang behind the page instead of the game's own art, or null. */
function focusedMediaOverride() {
  const item = focusedMediaItem();
  return item && item.kind === "image" ? item.url : null;
}

let mediaScroll = 0;   // index of the strip's leftmost visible item

/* A page that opens with nothing in its gallery asks the host for one, once per game per
   session: the pass fills galleries for installed games only, so this is how an uninstalled
   game's page gets its pictures. The answer is a `media` message. */
const mediaAsked = new Set();
function requestMedia(g) {
  if (!g || (Array.isArray(g.media) && g.media.length) || mediaAsked.has(g.id)) return;
  mediaAsked.add(g.id);
  send({ cmd: "fetchMedia", id: g.id });
}

function renderDetailMedia(g) {
  const strip = $("detailMedia"), track = $("detailMediaTrack");
  if (!strip || !track) return;
  requestMedia(g);
  const items = detailMediaItems(g);
  strip.hidden = items.length === 0;
  track.innerHTML = "";
  items.forEach((item, i) => {
    const el = document.createElement("div");
    el.className = "media-item" + (item.kind === "video" ? " is-video" : "");
    el.dataset.focusable = "";
    el.dataset.focusKey = "media:" + i;
    el.dataset.mediaIndex = i;
    const thumb = document.createElement("div");
    thumb.className = "media-thumb";
    thumb.style.backgroundImage = `url('${item.thumb}')`;
    el.appendChild(thumb);
    if (item.kind === "video")
      el.insertAdjacentHTML("beforeend", '<span class="media-play"><svg viewBox="0 0 12 12"><path d="M2 1.5v9l8-4.5z"/></svg></span>');
    el.addEventListener("mouseenter", () => { if (hoverEnabled()) { setFocusEl(el); paintNav(); } });
    el.addEventListener("click", () => { setFocusEl(el); paintNav(); openMediaView(i); });
    track.appendChild(el);
  });
  updateMediaScroll();
}

/* Slide the strip the least it needs to keep the highlighted item on screen -- with a tile of
   context on any side that has more, so the highlight is never on the edge tile that carries the
   "more this way" shade, and mark those edge tiles. */
function updateMediaScroll() {
  const track = $("detailMediaTrack"), strip = $("detailMedia");
  if (!track || !strip) return;
  const n = track.children.length;
  const perView = Math.max(1, Math.floor((MEDIA_STRIP_W + MEDIA_GAP) / (MEDIA_ITEM_W + MEDIA_GAP)));
  const last = Math.max(0, n - perView);
  const el = focusEl(document.getElementById("screen-detail"));
  const i = el && el.dataset.mediaIndex !== undefined ? parseInt(el.dataset.mediaIndex, 10) : null;
  if (i !== null && perView > 2) {
    const lo = mediaScroll + (mediaScroll > 0 ? 1 : 0);
    const hi = mediaScroll + perView - 1 - (mediaScroll + perView < n ? 1 : 0);
    if (i < lo) mediaScroll = Math.max(0, i - 1);
    else if (i > hi) mediaScroll = Math.min(last, i - perView + 2);
  } else if (i !== null) {
    if (i < mediaScroll) mediaScroll = i;
    else if (i > mediaScroll + perView - 1) mediaScroll = i - perView + 1;
  }
  mediaScroll = Math.max(0, Math.min(mediaScroll, last));
  track.style.transform = `translateX(${-mediaScroll * (MEDIA_ITEM_W + MEDIA_GAP)}px)`;
  const moreLeft = mediaScroll > 0, moreRight = mediaScroll + perView < n;
  strip.classList.toggle("more-left", moreLeft);
  strip.classList.toggle("more-right", moreRight);
  [...track.children].forEach((tile, k) => {
    tile.classList.toggle("edge-left", moreLeft && k === mediaScroll);
    tile.classList.toggle("edge-right", moreRight && k === mediaScroll + perView - 1);
  });
}

/* The page's own art -- Shelf's opaque page hangs the hero at the top; Loungepad hides this and
   shows the backdrop through. It follows the strip: a highlighted screenshot takes the hero's
   place. Applied only on a change, because this runs on every repaint. */
let detailArtUrl = null;
function updateDetailArt(g, override) {
  const art = $("detailArt");
  if (!art || !g) return;
  const url = override || backdropUrl(g);
  if (url === detailArtUrl) return;
  detailArtUrl = url;
  art.className = "detail-art";
  art.innerHTML = "";
  art.style.background = "";
  // "cover" on purpose: the box is cut to this picture's own aspect (see --art-aspect and
  // .detail-art), so there is nothing left for cover to crop, and scenery must never letterbox.
  applyArt(g, art, url, "cover");
}

/* ---- the viewer ----
   One item of the gallery, whole, over everything: a picture fitted entire, or a film with the
   pad on it. A pauses and resumes a film (on a picture it goes to the next), X and Y skip ten
   seconds back and ahead, Left and Right go to the previous and the next item -- and keep the
   strip on it, so B lands back where you were -- and B closes. The film plays in a player of
   the viewer's own, from the start, with the sound setting; the page's own film waits. */
let mediaView = null;      // { idx }
let mediaViewTimer = null;
const MEDIA_SEEK_S = 10;

function mediaViewItems() { return detailMediaItems(gameById(detailGameId)); }

function openMediaView(idx) {
  const items = mediaViewItems();
  if (!items.length) return;
  mediaView = { idx: Math.max(0, Math.min(idx, items.length - 1)) };
  showOverlay("overlay-media");
  renderMediaView();
  clearInterval(mediaViewTimer);
  mediaViewTimer = setInterval(tickMediaView, 250);
}

function closeMediaView() {
  if (!mediaView) return;
  mediaView = null;
  clearInterval(mediaViewTimer);
  mediaViewTimer = null;
  hideOverlay("overlay-media");
  paintNav();   // the backdrop and the page's own film pick up from the strip's highlight
}

function mediaViewStep(dir) {
  const items = mediaViewItems();
  if (!mediaView || !items.length) return;
  mediaView.idx = (mediaView.idx + dir + items.length) % items.length;
  const el = document.querySelector(`#detailMediaTrack [data-media-index="${mediaView.idx}"]`);
  if (el) { setFocusEl(el); updateMediaScroll(); }
  renderMediaView();
}

function renderMediaView() {
  if (!mediaView) return;
  const items = mediaViewItems();
  const item = items[mediaView.idx];
  if (!item) { closeMediaView(); return; }
  const img = $("mediaViewImg");
  img.style.backgroundImage = item.kind === "image" ? `url('${item.url}')` : "none";
  img.classList.toggle("shown", item.kind === "image");
  $("mediaViewName").textContent = item.name || (item.kind === "video" ? "Video" : "Screenshot");
  $("mediaViewCount").textContent = `${mediaView.idx + 1} / ${items.length}`;
  $("mediaViewProgress").hidden = item.kind !== "video";
  $("mediaViewProgress").firstElementChild.style.width = "0%";
  syncTrailers();
  renderMediaViewFoot();
}

function renderMediaViewFoot() {
  if (!mediaView) return;
  const items = mediaViewItems();
  const item = items[mediaView.idx];
  const many = items.length > 1;
  const pairs = [];
  if (item && item.kind === "video") pairs.push(["A", viewerTrailer.paused() ? "Play" : "Pause"], ["X", "Back 10 s"], ["Y", "Ahead 10 s"]);
  else if (many) pairs.push(["A", "Next"]);
  if (many) pairs.push(["DpadH", "Previous / Next"]);
  pairs.push(["B", "Back"]);
  $("mediaViewFoot").innerHTML = foot(...pairs);
}

/* Every quarter second while the viewer is up: the progress line, and the A label, which has
   to follow the film -- it ends on its own, and a paused one reads "Play". */
function tickMediaView() {
  if (!mediaView) return;
  const d = viewerTrailer.duration(), t = viewerTrailer.position();
  $("mediaViewProgress").firstElementChild.style.width = d > 0 ? Math.min(100, t / d * 100) + "%" : "0%";
  const label = $("mediaViewFoot").querySelector('[data-press="A"] span');
  if (!label || (label.textContent !== "Play" && label.textContent !== "Pause")) return;
  const want = viewerTrailer.paused() ? "Play" : "Pause";
  if (label.textContent !== want) label.textContent = want;
}

function mediaViewInput(btn) {
  if (!mediaView) return;
  const item = mediaViewItems()[mediaView.idx];
  const film = item && item.kind === "video";
  switch (btn) {
    case "Left": mediaViewStep(-1); break;
    case "Right": mediaViewStep(1); break;
    case "A":
      if (!film) { mediaViewStep(1); break; }
      // A film that ran to its end, or would not start, plays again from the top.
      if (!viewerTrailer.active()) viewerTrailer.arm(gameById(detailGameId), item.url, { delay: 0, again: true });
      else if (viewerTrailer.paused()) viewerTrailer.resume();
      else viewerTrailer.pause();
      renderMediaViewFoot();
      break;
    case "X": if (film) viewerTrailer.seek(-MEDIA_SEEK_S); break;
    case "Y": if (film) viewerTrailer.seek(MEDIA_SEEK_S); break;
    case "B": closeMediaView(); break;
  }
}

const DETAIL_BTNS = ["play", "collect", "manage", "achievements", "activity"];

function updateDetailFocus() {
  const scope = document.getElementById("screen-detail");
  // Default to Play the first time in, then let the engine hold the position.
  if (!focusEl(scope)) setScopeKey(scope, "detail:play");
  paintNav();
}

function detailActivate() {
  const g = gameById(detailGameId);
  const el = focusEl();
  if (el && el.dataset.mediaIndex !== undefined) { openMediaView(parseInt(el.dataset.mediaIndex, 10)); return; }
  const act = el ? el.dataset.act : null;
  if (act === "play" && g) { if (!g.installed) offerInstall(g); else launchGame(g); }
  else if (act === "collect") openCollect();
  else if (act === "manage") openManage();
  else if (act === "achievements" && g) openAchievements(g.id, "detail");
  else if (act === "activity" && g) openActivity(g.id, "detail");
}

function detailInput(btn) {
  const g = gameById(detailGameId);
  switch (btn) {
    case "Left": case "Right": case "Up": case "Down": navMove(btn); break;
    case "A": if (focusVisible()) detailActivate(); break;
    case "X": if (g) send({ cmd: "toggleFavorite", id: g.id }); break;
    case "B": switchView(detailReturn); break;
  }
}

/* ============================== settings ============================== */

function displayLabel(d) {
  if (!d) return "None";
  const num = d.deviceName.replace(/\D/g, "");
  return `${d.friendlyName} · ${d.width}×${d.height}${d.isPrimary ? " · PRIMARY" : ""} (Display ${num})`;
}

function allSettingsRows() {
  const s = S.settings;
  if (!s) return [];
  // applyTheme here as well as on the host's state push: the push is 350ms of debounce away, and
  // the accent has to move with the ◂ ▸ that changed it or the picker looks broken.
  const set = (fn) => { fn(); applyTheme(); scheduleSave(); renderSettings(); };

  const rows = [];
  // Appearance, all of it per theme: the theme, then its look, its motion, and last the options
  // it declares for itself with the row that puts it all back.
  rows.push({ section: "THEME", cat: "appearance" });
  const themes = S.themes && S.themes.length ? S.themes : [{ id: "", name: "Shelf" }];
  const theme = themes.find(t => t.id === (s.theme || "")) || themes[0];
  rows.push(cycleRow("Theme", themes.map(t => t.id), () => (s.theme || ""), v => set(() => s.theme = v),
    theme && theme.error ? null
      : [theme && theme.author ? "by " + theme.author : null,
         theme && theme.version ? "v" + theme.version : null,
         theme && theme.description ? theme.description : null]
        .filter(Boolean).join(" · ") || "Drop a theme folder into the themes directory to add one",
    theme && theme.error ? theme.error : null,
    Object.fromEntries(themes.map(t => [t.id, t.name]))));
  rows.push({
    name: "Themes folder", hint: "A theme is a folder with a theme.css and an optional theme.json. Edits apply as you save",
    type: "action", label: "Open",
    action: () => send({ cmd: "openThemesFolder" }),
  });
  rows.push({ section: "LOOK", cat: "appearance" });
  rows.push(accentRow(set));
  rows.push(toggleRow("Hide the button hints", "Drops the bar along the bottom of every screen. The buttons still do the same things",
    () => lookHideHints(), v => set(() => lookSet(LOOK_IDS.hideHints, v))));
  rows.push({ section: "ANIMATION", cat: "appearance" });
  rows.push(toggleRow("Animations", "Screens, menus and the Power Wheel move into place rather than appearing. Off makes every change instant",
    () => lookAnimations(), v => set(() => lookSet(LOOK_IDS.animations, v))));
  if (lookAnimations())
    rows.push(sliderRow("Animation speed", () => lookSpeed(), 0.5, 2.0, 0.25,
      v => set(() => lookSet(LOOK_IDS.speed, v)), v => v.toFixed(2) + "×",
      "Higher is quicker. 1× is the launcher's own timing, and a theme's own animations scale with it too"));
  rows.push(...themeSettingRows(theme, s, set));

  rows.push({ section: "DISPLAY", cat: "general" });
  rows.push({
    name: "TV display", hint: "Loungepad opens here, and games are steered onto it",
    type: "select",
    value: displayLabel(S.displays.find(d => d.deviceName === s.tvDeviceName) || null),
    choices: S.displays.map(d => ({ value: d.deviceName, label: displayLabel(d) })),
    current: s.tvDeviceName,
    pick: (v) => set(() => { s.tvDeviceName = v; }),
    adjust: (dir) => set(() => {
      if (!S.displays.length) return;
      let i = S.displays.findIndex(d => d.deviceName === s.tvDeviceName);
      i = (i + dir + S.displays.length) % S.displays.length;
      s.tvDeviceName = S.displays[i].deviceName;
    }),
  });
  rows.push(toggleRow("Switch primary display on launch", "Games default to the primary display, so the TV becomes primary while a game runs",
    () => s.switchPrimaryOnLaunch, v => set(() => s.switchPrimaryOnLaunch = v)));
  rows.push(toggleRow("Reposition game windows", "If a game still opens on another monitor, nudge its window onto the TV",
    () => s.repositionGameWindow, v => set(() => s.repositionGameWindow = v)));
  rows.push(toggleRow("Keep launcher focused", "Pull focus back if the desktop steals it while no game is running",
    () => s.keepFocus, v => set(() => s.keepFocus = v)));

  rows.push({ section: "GAMEPAD", cat: "input" });
  rows.push(toggleRow("Gamepad mouse", "Left stick moves the cursor; right stick scrolls",
    () => s.gamepadMouseEnabled, v => set(() => s.gamepadMouseEnabled = v)));
  rows.push(toggleRow("Stay active while a game is focused", "The gamepad-mouse always works when a game is running but not focused; this keeps it alive inside the game too (off avoids fighting native controller support)",
    () => s.gamepadMouseDuringGame, v => set(() => s.gamepadMouseDuringGame = v)));
  rows.push(sliderRow("Stick deadzone", () => s.deadzone, 0.05, 0.40, 0.01, v => set(() => s.deadzone = v), v => v.toFixed(2)));
  rows.push(sliderRow("Cursor sensitivity", () => s.sensitivity, 0.2, 3.0, 0.1, v => set(() => s.sensitivity = v), v => v.toFixed(1) + "×"));
  rows.push(sliderRow("Acceleration curve", () => s.accelExponent, 1.0, 3.0, 0.1, v => set(() => s.accelExponent = v), v => v.toFixed(1)));
  rows.push(buttonRow("Speed boost button", ["RT", "LT", "LB", "RB", "LS", "RS", "Off"], () => s.boostButton, v => set(() => s.boostButton = v),
    "Hold to move the cursor and scroll faster — crossing a 4K screen a nudge at a time gets old"));
  if (s.boostButton !== "Off")
    rows.push(sliderRow("Boost multiplier", () => s.boostMultiplier, 1.5, 5.0, 0.5, v => set(() => s.boostMultiplier = v), v => v.toFixed(1) + "×"));
  rows.push(buttonRow("Left click button", ["A", "B", "X", "Y", "LB", "RB", "LS", "RS"], () => s.leftClickButton, v => set(() => s.leftClickButton = v),
    "Sends a real mouse click when the launcher is not focused"));
  rows.push(buttonRow("Right click button", ["A", "B", "X", "Y", "LB", "RB", "LS", "RS"], () => s.rightClickButton, v => set(() => s.rightClickButton = v)));
  rows.push(toggleRow("Hide pointer system-wide", "The pointer always hides inside the launcher on D-pad input; this extends it to the rest of Windows. Replaces the system cursors, so it is restored when Loungepad exits",
    () => s.hideCursorSystemWide, v => set(() => s.hideCursorSystemWide = v)));
  // A setting the host defaults to on: a settings file from before it has no key, and undefined
  // must read as on rather than off.
  rows.push(toggleRow("Touchpad mouse", "DualSense and DualShock 4 as a laptop touchpad: one finger moves the pointer, two fingers scroll, pinch to zoom, press the pad to click (with two fingers down for a right click)",
    () => s.touchpadMouse !== false, v => set(() => s.touchpadMouse = v)));
  // Every touchpad setting defaults to on or to 1.0 on the host; a settings file from before one
  // existed has no key, and undefined must read as that default.
  if (s.touchpadMouse !== false) {
    rows.push(sliderRow("Touchpad sensitivity", () => s.touchpadSensitivity ?? 1, 0.25, 4.0, 0.25,
      v => set(() => s.touchpadSensitivity = v), v => v.toFixed(2) + "×",
      "Slow strokes move the pointer a little for precision, quick ones a lot; this scales both"));
    rows.push(toggleRow("Tap to click", "A light tap is a click, a two-finger tap a right click, two taps a double click. Pressing the pad down always clicks",
      () => s.touchpadTapToClick !== false, v => set(() => s.touchpadTapToClick = v)));
    if (s.touchpadTapToClick !== false)
      rows.push(toggleRow("Tap and drag", "Tap, then touch and hold: the button stays down while the finger moves, to drag a window or select text. Lift and touch again quickly to carry on; tap to let go. Makes a single tap wait a moment before it clicks",
        () => s.touchpadTapDrag !== false, v => set(() => s.touchpadTapDrag = v)));
    rows.push(cycleRow("Two-finger scroll direction", ["Natural", "Traditional"],
      () => (s.touchpadNaturalScroll === false ? "Traditional" : "Natural"),
      v => set(() => s.touchpadNaturalScroll = v === "Natural"),
      s.touchpadNaturalScroll === false
        ? "Fingers down scrolls down, like a mouse wheel"
        : "The page follows the fingers, like a phone. The Windows touchpad default"));
    rows.push(sliderRow("Scroll speed", () => s.touchpadScrollSpeed ?? 1, 0.25, 4.0, 0.25,
      v => set(() => s.touchpadScrollSpeed = v), v => v.toFixed(2) + "×",
      "How far two fingers scroll. A quick flick keeps the page coasting after they lift"));
  }
  rows.push(...menuComboRows(s, set));

  // A setting the host defaults to on: a settings file from before it has no key, and undefined
  // must read as on rather than off. The warning is the keyboard's: Create is View, and a Press
  // toggle that fires over games takes the press first.
  const shareShot = s.shareButtonScreenshot !== false;
  const shareClash = shareShot && !!s.keyboardInGame && (s.keyboardToggleMode || "Press") !== "Hold"
    && canonBtn(s.keyboardToggleButton || "Back") === "View";
  // A combo that holds View owns Create outright on a DualSense: the host leaves a button that is
  // part of the menu or screenshot combo to the combo, so a press of it alone takes no screenshot.
  const shareInCombo = shareShot && [["menu combo", s.minimizeCombo], ["screenshot combo", s.screenshotCombo]]
    .find(([, c]) => c && c !== "Off" && c.split("+").map(p => canonBtn(p.trim())).includes("View"));
  rows.push({
    ...toggleRow("Share button takes a screenshot",
      "Create on a DualSense (Share on a DualShock 4) and Capture on a Switch Pro take one while a game is focused: " +
      "F12, the Steam overlay's key, in a Steam game; Win+PrintScreen in anything else, which Windows saves to " +
      "Pictures › Screenshots. The Xbox Share button never reaches Loungepad: Windows and Steam answer that one themselves",
      () => shareShot, v => set(() => s.shareButtonScreenshot = v)),
    warn: shareClash
      ? "Create is also the keyboard button, and the keyboard opens over games, so on a DualSense the keyboard takes the press and no screenshot is taken. Switch the keyboard to Hold, or pick another button for it."
      : shareInCombo
      ? `[[View]] is part of the ${shareInCombo[0]}, and on a DualSense that is Create, so the combo keeps it and a press of Create alone takes no screenshot there.`
      : null,
  });
  rows.push(buttonRow("Screenshot combo", SCREENSHOT_COMBOS, () => s.screenshotCombo, v => set(() => s.screenshotCombo = v),
    "The same screenshot on a combo, for an Xbox pad (Windows keeps its Share button to itself and never passes it " +
    "to applications) or a game that ignores the share button. Works while a game is focused",
    s.screenshotCombo !== "Off" && s.screenshotCombo === s.minimizeCombo
      ? "Same as the menu combo above, so one press does both. Pick a different one."
      : null));

  rows.push({ section: "WINDOWS AND STEAM", cat: "input" });
  rows.push(...xboxButtonRows());

  rows.push({ section: "KEYBOARD", cat: "keyboard" });
  rows.push(cycleRow("Keyboard app", ["Builtin", "TabTip", "Osk"], () => s.keyboardApp, v => set(() => s.keyboardApp = v),
    "The Loungepad Keyboard never takes focus, so it keeps working over a game and leaves the caret " +
    "where it was. TabTip is the Windows touch keyboard; Osk is the classic on-screen keyboard",
    s.keyboardApp === "Builtin" ? null
      : "Windows' own keyboards are not built for a gamepad: TabTip only accepts one on its Gamepad " +
        "layout, which has to be picked by hand in its settings, and Osk accepts none at all — it " +
        "needs a real mouse. Both also take the foreground, so the field you were typing into can " +
        "lose its caret.",
    KEYBOARD_APP_LABELS));
  rows.push(buttonRow("Keyboard button", ["Back", "Start", "LS", "RS", "LB", "RB"], () => s.keyboardToggleButton, v => set(() => s.keyboardToggleButton = v),
    s.keyboardToggleButton === "Back"
      ? "Shows and hides the keyboard from anywhere. On the library [[View]] opens search instead, which brings the keyboard up with it"
      : "Shows and hides the keyboard from anywhere in the launcher"));
  // Press is quicker and is the default. Hold exists because it leaves the tap free, which is the
  // only way to keep a button that already does something inside the launcher. View is the one
  // exception that needs neither: the library claims it for search (publishClaims), and search
  // raises the keyboard anyway. What a Press does take outright is Menu; and a combo the toggle
  // button is part of brings the keyboard up as well, since the press fires before the combo is whole.
  const pressMode = (s.keyboardToggleMode || "Press") !== "Hold";
  const toggleBtn = canonBtn(s.keyboardToggleButton || "Back");
  const sharedCombo = [["menu combo", s.minimizeCombo], ["screenshot button", s.screenshotCombo]]
    .find(([, c]) => c && c !== "Off" && c.includes("+") && c.split("+").map(p => canonBtn(p.trim())).includes(toggleBtn));
  rows.push(cycleRow("Opens on", ["Press", "Hold"], () => s.keyboardToggleMode, v => set(() => s.keyboardToggleMode = v),
    pressMode
      ? "One tap. The button does nothing else while this is set"
      : "Hold the button down. A tap still does whatever that button normally does",
    !pressMode ? null
      : toggleBtn === "Menu" ? "[[Menu]] opens Settings from the library, and on Press the keyboard takes it outright — pick another button, or switch to Hold."
      : sharedCombo ? `[[${toggleBtn}]] is also part of the ${sharedCombo[0]}, so on Press the keyboard comes up every time that combo is used — pick another button, or switch to Hold.`
      : null,
    { Press: "Press", Hold: "Hold" }));
  if ((s.keyboardToggleMode || "Press") === "Hold")
    rows.push(sliderRow("Hold time", () => s.keyboardToggleHoldMs, 200, 2000, 100, v => set(() => s.keyboardToggleHoldMs = v), v => Math.round(v) + " ms"));
  rows.push(toggleRow("Show while a game is running",
    "Off by default: inside a game every button belongs to the game, and a keyboard arriving over one is a surprise. A keyboard already on screen can always be closed either way",
    () => !!s.keyboardInGame, v => set(() => s.keyboardInGame = v)));

  rows.push({ section: "LOUNGEPAD KEYBOARD", cat: "keyboard" });
  rows.push(sliderRow("Keyboard size", () => s.keyboardScale, 0.6, 1.6, 0.05,
    v => set(() => s.keyboardScale = v), v => Math.round(v * 100) + "%",
    "Scales the keys up or down from the size Loungepad picks for the TV. Also the − and + beside the gear on the keyboard"));
  // The same five switches are on the keyboard itself, behind the gear at the right end of its
  // suggestion bar. A change made there arrives here as a keyboardOptions message.
  rows.push(toggleRow("Word suggestions",
    "Finishes the word you are typing and guesses the next one, from Windows' own dictionary. " +
    "[[RS]] takes the first suggestion. Also switched from the gear on the keyboard",
    () => s.keyboardSuggestions !== false, v => set(() => s.keyboardSuggestions = v)));
  rows.push(toggleRow("Function keys", "F1 to F12 along the top, with Print Screen, Scroll Lock and Pause over the navigation keys",
    () => !!s.keyboardFunctionKeys, v => set(() => s.keyboardFunctionKeys = v)));
  rows.push(toggleRow("Navigation keys", "Insert, Delete, Home, End, Page Up, Page Down and all four arrows, beside the letters",
    () => !!s.keyboardNavKeys, v => set(() => s.keyboardNavKeys = v)));
  rows.push(toggleRow("Number pad", "The number pad of a full-size keyboard. Games see real number-pad keys, so a binding to Num 8 works",
    () => !!s.keyboardNumpad, v => set(() => s.keyboardNumpad = v)));
  rows.push(toggleRow("Ctrl, Win and Alt", "On the bottom row. Each holds for the next key, like Shift, so Ctrl then C copies",
    () => !!s.keyboardModifiers, v => set(() => s.keyboardModifiers = v)));
  rows.push(sliderRow("D-pad repeat delay", () => s.keyRepeatDelayMs, 120, 900, 10,
    v => set(() => s.keyRepeatDelayMs = v), v => Math.round(v) + " ms",
    "How long a direction is held before the highlight starts moving on its own"));
  rows.push(sliderRow("D-pad repeat speed", () => s.keyRepeatIntervalMs, 20, 300, 5,
    v => set(() => s.keyRepeatIntervalMs = v), v => Math.round(v) + " ms",
    "Gap between steps once it is moving — lower is faster"));
  rows.push({
    name: "Show keyboard now", type: "action", label: "Toggle",
    action: () => send({ cmd: "toggleKeyboard" }),
  });

  rows.push({ section: "LIBRARY", cat: "library" });
  const romCount = S.games.filter(g => g.emulated).length;
  const counts = PLATFORMS.map(p => `${p} ${S.games.filter(g => g.platform === p).length}`)
    .concat(romCount ? [`Emulated ${romCount}`] : []).join(" · ");
  rows.push({
    name: "Rescan platforms",
    hint: (counts ? counts + " · " : "") + "Steam, Epic, GOG and the Xbox app from their install data, plus every ROM folder and playlist",
    type: "action", label: S.scanning ? "Scanning…" : "Rescan",
    action: () => { if (!S.scanning) send({ cmd: "rescan" }); },
  });
  rows.push({
    name: "Add a game manually", hint: "Point to an .exe and optional cover art",
    type: "action", label: "Add",
    action: () => send({ cmd: "addManual" }),
  });

  rows.push({ section: "EMULATORS & ROM FOLDERS", cat: "library" });
  rows.push(...emulationRows(s, set));

  // The manager is one program for the whole library, so its row is here; each game's own mods
  // are under that game's Options. The row is where "is it installed, and where" gets answered
  // without opening a game.
  rows.push({ section: "MODS", cat: "library" });
  const vx = S.mods || null;
  rows.push({
    name: "Vortex",
    hint: vx && vx.installed
      ? `Nexus Mods' mod manager · ${vx.path}${vx.version ? " · v" + vx.version : ""}${vx.running ? " · running" : ""}. Each game's mods are under Options → Mods`
      : "Nexus Mods' free mod manager. Loungepad drives it to download mods, install them and switch them on and off, for over 250 games. Not installed on this PC yet: this opens the download page",
    type: "action", label: vx && vx.installed ? "Open" : "Get Vortex",
    action: () => send({ cmd: vx && vx.installed ? "modsShowVortex" : "modsGetVortex" }),
  });
  rows.push({
    name: "Vortex location",
    hint: "Optional. Only if Vortex is somewhere the launcher does not look: a portable copy, or another drive. Its folder, or Vortex.exe itself",
    type: "action", label: s.vortexPath ? s.vortexPath : "Automatic",
    action: () => openInput("VORTEX LOCATION", s.vortexPath || "", v => set(() => s.vortexPath = v.trim())),
  });

  // Every store in one place: the accounts and switches first, the optional keys after them.
  rows.push({ section: "STORES & LAUNCHERS", cat: "library" });
  rows.push(...storeAccountRows(s, set));
  rows.push(secretRow("Steam Web API key", s,
    "The other way to read the games you own and your achievements, instead of the Steam sign-in above. Free from steamcommunity.com/dev/apikey — any domain name will do. Your account's data goes to Steam and nowhere else either way",
    () => s.steamApiKey, v => set(() => s.steamApiKey = v)));
  rows.push(secretRow("Xbox sign-in app id", s,
    "Optional. Only if Microsoft stops accepting the Xbox app's own sign-in: the client id of an app registration of your own. See the README",
    () => s.xboxClientId, v => set(() => s.xboxClientId = v)));

  // Collections are made from a game's own menu, so this is only the other half of that: the
  // place to get rid of one. Nothing lists them otherwise now that the tab is gone.
  if (S.collections.length) {
    rows.push({ section: "COLLECTIONS", cat: "library" });
    S.collections.forEach(c => {
      const n = (c.gameIds || []).length;
      rows.push({
        name: c.name,
        hint: n === 1 ? "1 game · filter by it with [[X]] on the library" : `${n} games · filter by it with [[X]] on the library`,
        type: "action", label: "Delete", danger: true,
        action: () => askDeleteCollection(c),
      });
    });
  }

  rows.push({ section: "ARTWORK & METADATA", cat: "library" });
  rows.push({
    name: "Refresh artwork & metadata",
    hint: "Covers, descriptions and scores are fetched automatically in the background. Nothing below needs setting up",
    type: "action", label: "Refresh",
    action: () => { send({ cmd: "refreshMetadata" }); toast("Fetching in the background"); },
  });
  rows.push(toggleRow("Keep trailers on this PC",
    "A trailer streams from Steam the first time it plays and is kept for next time, up to 4 GB with the oldest going first. Off streams every time",
    () => s.cacheTrailers !== false, v => set(() => s.cacheTrailers = v)));
  rows.push(cycleRow("Age rating", ["ESRB", "PEGI"], () => s.ageRatingBoard,
    v => set(() => s.ageRatingBoard = v),
    "Which board's mark a game's page shows: ESRB for North America, PEGI for Europe. A game only one of them has rated shows that one either way"));
  // Everything from here down is an escape hatch, not a setup step. Worth keeping visible -- some
  // people would rather not route anything through a shared service -- but the hints have to say
  // plainly that leaving them alone is the normal thing to do.
  rows.push(secretRow("SteamGridDB key", s,
    "Optional. Use your own key instead of the shared service. Free from steamgriddb.com",
    () => s.steamGridDbKey, v => set(() => s.steamGridDbKey = v)));
  rows.push(secretRow("IGDB client ID", s,
    "Optional. Register an application at dev.twitch.tv to use your own instead of the shared service",
    () => s.igdbClientId, v => set(() => s.igdbClientId = v)));
  rows.push(secretRow("IGDB client secret", s,
    "The secret from the same Twitch application. Kept sealed for your Windows account in settings.json",
    () => s.igdbClientSecret, v => set(() => s.igdbClientSecret = v)));
  rows.push({
    name: "Metadata service", hint: "Where the shared lookups go: a game's title and Steam id, never anything about you. Leave blank for the built-in one",
    type: "action", label: s.metadataEndpoint ? "Custom" : "Default",
    action: () => openInput("METADATA SERVICE URL", s.metadataEndpoint || "",
      v => set(() => s.metadataEndpoint = v.trim())),
  });

  // Play sessions and achievements: the Stats category, built in activity.js.
  rows.push(...activitySettingsRows(s, set));

  rows.push({ section: "UPDATES", cat: "general" });
  rows.push(updateRow());
  rows.push(toggleRow("Update automatically",
    "Downloads new versions in the background and installs them the next time Loungepad starts. Off, nothing is checked until you ask",
    () => s.autoUpdate !== false, v => set(() => s.autoUpdate = v)));

  rows.push({ section: "FROM PLAYNITE", cat: "general" });
  rows.push({
    name: "Import from Playnite",
    hint: "Playtime, favourites, categories, games added by hand, GameActivity's sessions and SuccessStory's lists. Only fills what Loungepad has nothing for -- nothing here is overwritten -- and it can be undone",
    type: "action", label: "Open", action: () => openPlayniteImport(),
  });

  /* REST & SLEEP: a console's rest mode. The timers and the two switches are settings; the rows
     after them are read off Windows (S.rest.wake, see WakeInfo on the host): which controller
     devices may wake the PC from sleep, one Allow per device that could, and whether a wake lands
     on the lock screen. Both changes are elevated on the host, so each costs one UAC prompt. */
  rows.push({ section: "REST & SLEEP", cat: "general" });
  const restAfter = typeof s.restAfterMinutes === "number" ? s.restAfterMinutes : 60;
  const sleepAfter = typeof s.sleepAfterRestMinutes === "number" ? s.sleepAfterRestMinutes : -1;
  const wake = S.rest && S.rest.wake ? S.rest.wake : null;
  rows.push(cycleRow("Rest after", REST_AFTER, () => restAfter, v => set(() => s.restAfterMinutes = v),
    "With nothing touched on the pad, keyboard or mouse for this long, the TV goes dark and the game is " +
    "paused where it stands. A button on the pad, a key or a mouse click brings both back; moving the mouse or " +
    "a stick never does. The Power Wheel rests the PC at once",
    null, REST_AFTER_LABELS));
  if (restAfter > 0)
    rows.push(toggleRow("Also while a game is running",
      "Like a console: this long without a press mid-game means nobody is playing. Off if you leave games running by themselves",
      () => s.restDuringGame !== false, v => set(() => s.restDuringGame = v)));
  rows.push(toggleRow("Pause the game while resting",
    "Freezes the game's processes, the way PlayState does, so it draws and computes nothing until you are back and " +
    "picks up exactly where it was. Off leaves it running to a dark screen: for an online game, or one whose anti-cheat objects",
    () => s.restPausesGame !== false, v => set(() => s.restPausesGame = v)));
  rows.push(cycleRow("Then sleep the PC after", SLEEP_AFTER, () => sleepAfter, v => set(() => s.sleepAfterRestMinutes = v),
    "Resting keeps the PC on at a desktop's idle draw; sleep takes it to a few watts and comes back where it left off. " +
    "Off unless you turn it on, because what can wake a sleeping PC is the row below, and on many desktops that is not the pad",
    wake && !wake.canSleep && sleepAfter >= 0 ? "Windows says this PC cannot sleep, so resting is as far as it goes." : null,
    SLEEP_AFTER_LABELS));
  // Both on demand, for trying the timers' effect without waiting for them. Rest needs no
  // confirm (one press brings it back); sleep does, because what brings THAT back is the row
  // below, and on many a desktop that is not the pad.
  rows.push({
    name: "Sleep the PC now",
    hint: "Rests first, so the game is frozen and the pad parked, then Windows' own sleep. What wakes it is the row below",
    type: "action", label: "Sleep",
    action: () => askConfirm({
      title: "Put the PC to sleep?",
      body: "The game is paused where it stands and the PC goes to sleep. It comes back on whatever Windows allows to wake it, which the Rest and sleep rows describe.",
      yesLabel: "Sleep", icon: "power", danger: false,
      onYes: () => send({ cmd: "sleepPc" }),
    }),
  });
  rows.push({
    name: "Rest now",
    hint: "The TV goes dark and the game is paused where it stands, exactly as the timer would do it. A button, a key or a click brings it back; movement does not",
    type: "action", label: "Rest",
    action: () => send({ cmd: "rest" }),
  });
  restWakeRows(wake).forEach(r => rows.push(r));

  rows.push({ section: "STARTUP & LOCK SCREEN", cat: "general" });
  rows.push(toggleRow("Launch Loungepad at login", "Registers a startup entry so the launcher is ready after wake or reboot",
    () => s.launchOnStartup, v => set(() => s.launchOnStartup = v)));
  rows.push({
    name: "Couch setup guide", hint: "Gamepad keyboard layout, PIN sign-in, controller wake, auto-start",
    type: "action", label: "Open guide",
    action: () => { guideOpen = true; showOverlay("overlay-guide"); },
  });
  rows.push({
    name: "First-time setup",
    hint: "The screens a new install opens on: the TV, the look, your stores, emulators, the Xbox button, signing in with the controller. Everything already set stays as it is",
    type: "action", label: "Run again",
    action: () => openOnboarding("settings"),
  });
  rows.push({
    name: "Restore default settings",
    hint: "Puts every setting back the way a fresh install has it. Your games and collections are untouched",
    type: "action", label: "Restore", danger: true,
    action: () => {
      confirmState = {
        title: "Restore default settings?",
        body: "Theme, accent, display, gamepad and keyboard settings all go back to their defaults. Your library, collections and playtime are not affected.",
        yesLabel: "Restore defaults",
        icon: "refresh", danger: true,
        onYes: () => send({ cmd: "resetSettings" }),
      };
      confirmIdx = 0;
      showOverlay("overlay-confirm");
      renderConfirm();
    },
  });
  rows.push({
    name: "Exit Loungepad", type: "action", label: "Exit", danger: true,
    action: () => send({ cmd: "exitApp" }),
  });
  return rows;
}


/* One row per ROM folder and one per emulator, each opening its own options list; the two "Add"
   rows are the way in. A folder with no emulator, or a RetroArch folder with no core, says so on
   its row: its games are in the library and cannot start, and that is the one thing worth a
   warning here. Settings → Library and the first-run setup's emulator step both list them. */
function emulationRows(s, set) {
  const rows = [];
  rows.push(toggleRow("Find emulators and ROMs automatically",
    "Every scan looks for installed emulators in the usual places, then for games: RetroArch's playlists, an Emulation\\roms layout, and folders named after a system. Anything you remove stays removed",
    () => s.detectEmulators !== false, v => set(() => s.detectEmulators = v)));
  const em = S.emulation || { emulators: [], romFolders: [], platforms: [] };
  (em.romFolders || []).forEach(f => {
    const p = platformDef(f.platformId);
    const emu = emulatorById(f.emulatorId);
    const n = S.games.filter(g => g.romFolderId === f.id).length;
    const bits = [`${n} game${n === 1 ? "" : "s"}`, emu ? emu.name : "no emulator"];
    if (usesCore(emu)) bits.push(f.core ? coreName(f.core) : "no core");
    if (f.detected) bits.push("found automatically");
    // A playlist is named for its file; the folder its games are in is the games' business.
    const where = f.playlist ? `RetroArch playlist · ${f.path.split(/[\\/]/).pop()}` : f.path;
    rows.push({
      name: p ? p.name : f.platformId, hint: `${where} · ${bits.join(" · ")}`,
      warn: !emu ? "No emulator is set for this folder, so its games cannot start yet"
          : usesCore(emu) && !f.core ? `${emu.name} needs a core for this system before its games can start` : undefined,
      type: "action", label: "Options",
      action: () => openRomFolderOptions(f),
    });
  });
  rows.push({
    name: "Add a ROM folder", hint: "One folder per system. You will be asked which system it is and which emulator runs it; the folder's name is a first guess",
    type: "action", label: "Add",
    action: () => send({ cmd: "romFolderPick" }),
  });
  (em.emulators || []).forEach(e => rows.push({
    name: e.name, hint: `${e.exePath} · ${e.args || '"{rom}"'}${e.detected ? " · found automatically" : ""}`,
    type: "action", label: "Options",
    action: () => openEmulatorOptions(e),
  }));
  rows.push({
    name: "Add an emulator", hint: "For one the scan did not find: point to its .exe. RetroArch, Dolphin, PCSX2, DuckStation, PPSSPP, mGBA, MAME and the other common ones are recognised and set up on their own",
    type: "action", label: "Add",
    action: () => send({ cmd: "emuAdd" }),
  });
  return rows;
}

/* The stores' libraries: Steam's switch, the three sign-ins and the Game Pass catalogue. Settings →
   Library lists its optional keys after these; the first-run setup's stores step lists only these. */
function storeAccountRows(s, set) {
  return [
    toggleRow("Steam: show games you own but haven't installed", steamAccountHint(),
      () => !!s.steamShowOwned, v => set(() => s.steamShowOwned = v)),
    steamSignInRow(),
    storeRow("epic", "Epic Games account",
      "Sign in to list every game you own on the Epic Games Store. Anything not installed shows greyed out and installs from here"),
    storeRow("gog", "GOG account",
      "Sign in to list every game you own on GOG. Installs go through GOG Galaxy when it is here, and through gog.com when it is not"),
    storeRow("xbox", "Xbox account",
      "Sign in with your Microsoft account to list the PC games on your Xbox profile — the ones it has seen you play"),
    toggleRow("Show the PC Game Pass catalogue", gamePassHint(),
      () => !!s.gamePassCatalog, v => set(() => s.gamePassCatalog = v)),
  ];
}

/* The menu combo and its gesture: Settings → Controller, and the first-run setup's Xbox button step.
   Who else acts on the Xbox button -- Windows' Game Bar and Xbox mode, and Steam -- is read by the
   host (see WindowsGuide and SteamGuide); their switches are xboxButtonRows, and the warning here
   only names what is still on. `where` says where those switches are on the screen asking. */
function menuComboRows(s, set, where = "under Windows and Steam, at the end of this list") {
  const tapHold = s.menuComboMode !== "DoubleTap";
  const xb = S.xboxButton || {};
  const usesGuide = /\bGuide\b/.test(s.minimizeCombo || "");
  const takers = [];
  if (usesGuide && xb.gameBar === true) takers.push("Xbox Game Bar");
  if (usesGuide && tapHold && xb.xboxMode === true) takers.push("Windows' Xbox mode");
  if (usesGuide && xb.steam === true) takers.push("Steam");
  const listed = takers.length < 2 ? takers.join("") : takers.slice(0, -1).join(", ") + " and " + takers[takers.length - 1];
  const comboWarn =
    takers.length ?
      `${listed} also ${takers.length === 1 ? "takes" : "take"} this button` +
      (takers.includes("Windows' Xbox mode") ? ", and Xbox mode opens Task View when it is held" : "") +
      `. Turn ${takers.length === 1 ? "it" : "them"} off ${where}.`
    : s.minimizeCombo === "View + Menu" ?
      (xb.gameBar === true ? `Game Bar treats View + Menu as the Xbox button in apps; turn that off ${where}. ` : "") +
      "Steam binds View + Menu (Back + Start) to open Big Picture. Disable it in Steam: Settings > " +
      "Controller > Guide Button Chord Layout, or turn off Steam Input for this controller. Restart " +
      "Steam afterwards."
    : null;

  return [
    buttonRow("Menu combo", MINIMIZE_COMBOS, () => s.minimizeCombo, v => set(() => s.minimizeCombo = v),
      "Opens the Power Wheel and brings Loungepad back, from anywhere, a game included",
      comboWarn),
    cycleRow("Combo gesture", ["TapHold", "DoubleTap"], () => (s.menuComboMode === "DoubleTap" ? "DoubleTap" : "TapHold"),
      v => set(() => s.menuComboMode = v),
      tapHold
        ? "A tap opens the Power Wheel, and another tap closes it. Hold for half a second to show or hide Loungepad, or for the in-game menu while a game runs"
        : "A tap shows or hides Loungepad once it is sure no second tap is coming; a quick double tap opens the Power Wheel",
      null, { TapHold: "Tap: Power Wheel · Hold: Loungepad", DoubleTap: "Tap: Loungepad · Double tap: Power Wheel" }),
  ];
}

/* WINDOWS AND STEAM: the three other things that react to the Xbox button, each a switch that
   shows what is set right now and changes it on the spot, plus one press for all of them. The
   host writes Windows' two in the registry and Steam's in its own settings file, which means
   closing and reopening Steam; see xboxButtonSet on the host. Settings → Controller and the
   first-run setup both list them. */
function xboxButtonRows() {
  const xb = S.xboxButton || {};
  const rows = [];
  const hasXboxMode = xb.xboxMode === true || xb.xboxMode === false;
  const hasSteam = xb.steam === true || xb.steam === false;
  const stillOn = [
    xb.gameBar === true ? "Xbox Game Bar stops opening on the Xbox button, and stops treating View + Menu as one" : null,
    xb.xboxMode === true ? "Windows' Xbox mode is turned off, so a hold no longer opens Task View" : null,
    xb.steam === true ? `Steam stops opening on the Xbox button, drops its Guide button shortcuts, and its desktop layout is emptied for every kind of pad so the stick is only the pointer${xb.steamRunning ? "; Steam closes and reopens for this" : ""}` : null,
  ].filter(Boolean);
  if (stillOn.length) rows.push({
    name: "Give Loungepad the Xbox button",
    hint: "Turns off everything below that still reacts to it, in one go",
    type: "action", label: "Turn all off",
    action: () => {
      if (xb.steam === true && xb.steamRunning && S.gameRunning) { toast("Close the game first: Steam has to restart for this"); return; }
      askConfirm({
        title: "Give Loungepad the Xbox button?",
        body: stillOn.join(". ") + ". Each can be turned back on in Settings → Controller.",
        yesLabel: "Turn all off", icon: "controller", danger: false,
        onYes: () => send({ cmd: "xboxButtonAllOff" }),
      });
    },
  });
  rows.push(toggleRow("Xbox Game Bar on the controller",
    "The Xbox button opens Game Bar, and View + Menu stands in for the Xbox button in apps. Win + G opens Game Bar either way",
    () => xb.gameBar === true, v => setXboxButton("gameBar", v)));
  if (hasXboxMode) rows.push(toggleRow("Windows Xbox mode",
    "Windows' own full-screen gaming home. While it is on, holding the Xbox button opens Task View, on top of the hold that brings Loungepad back",
    () => xb.xboxMode === true, v => setXboxButton("xboxMode", v)));
  if (hasSteam) {
    // One switch for everything Steam does with a pad outside a game: the Xbox button, and the
    // desktop layout, whose default turns the left stick and the D-pad into arrow keys, A into
    // Enter and the right stick into a mouse on top of what Loungepad already does with them.
    const steamHint = "Steam's “Guide Button Focuses Steam” and its Guide button shortcuts, such as Guide + a button for Big Picture or the keyboard, " +
      "and Steam's desktop layout, which turns the stick and D-pad into arrow keys while no game is running. Off writes an empty " +
      "desktop layout for every kind of pad (the Share button still takes a Steam screenshot); on puts back what was there" +
      (xb.steamRunning ? ". Steam closes and reopens to change this" : "");
    rows.push(xb.steamBusy
      ? { name: "Steam on the controller", hint: steamHint, type: "action", label: "Restarting Steam…", action: () => {} }
      : toggleRow("Steam on the controller", steamHint, () => xb.steam === true, v => setXboxButton("steam", v)));
  }
  return rows;
}

/* Rest mode's two timers, as the rows offer them. Minutes; 0 is never for the first, and for the
   second -1 keeps the PC on and 0 sleeps it straight after the screen goes dark. */
const REST_AFTER = [0, 1, 5, 10, 15, 20, 30, 45, 60, 90, 120, 180, 240];
const SLEEP_AFTER = [-1, 0, 1, 5, 10, 15, 30, 60, 120, 180, 240];
const restMinutesLabel = m => (m < 60 ? `${m} min` : m % 60 ? `${Math.floor(m / 60)} h ${m % 60} min` : `${m / 60} h`);
const REST_AFTER_LABELS = Object.fromEntries(REST_AFTER.map(m => [m, m === 0 ? "Never" : restMinutesLabel(m)]));
const SLEEP_AFTER_LABELS = Object.fromEntries(SLEEP_AFTER.map(m => [m, m < 0 ? "Keep the PC on" : m === 0 ? "Straight away" : restMinutesLabel(m)]));

/* What can wake this PC from sleep, as Windows reports it, and the sign-in that stands in the way.
   The one thing no program can do is make a controller wake a sleeping PC: that is the device's
   driver offering "Allow this device to wake the computer", and on a desktop in S3 sleep the
   Bluetooth radio usually does not, so a Bluetooth pad has no route at all; the Xbox Wireless
   Adapter and some wired pads do. The sentence names which case this PC is, and every device that
   could be allowed gets an Allow of its own. Rest mode itself needs none of this: the screen-off
   half wakes on any pad, because it is the launcher reading the pad. */
function restWakeRows(wake) {
  const rows = [];
  if (!wake) {
    rows.push({ name: "Waking from sleep", hint: "Reading what can wake this PC…", type: "html", valueHtml: "" });
    return rows;
  }
  const devices = wake.devices || [];
  const names = list => list.map(d => d.name).join(", ");
  const ctlOn = devices.filter(d => d.kind === "controller" && d.armed);
  const ctlOff = devices.filter(d => d.kind === "controller" && !d.armed);
  const btOn = devices.filter(d => d.kind === "bluetooth" && d.armed);
  const btOff = devices.filter(d => d.kind === "bluetooth" && !d.armed);
  let hint;
  if (!wake.canSleep) hint = "Windows reports that this PC cannot sleep at all, so nothing here applies: resting keeps it on with the screen dark";
  else if (wake.modernStandby) hint = "This PC uses Modern Standby, which keeps Bluetooth up while asleep, so a paired controller can usually wake it with its Xbox or PS button"
    + (ctlOff.length ? `. ${names(ctlOff)} could be allowed to as well, below` : "");
  else if (ctlOn.length) hint = `${names(ctlOn)} may wake the PC: press its Xbox or PS button`
    + (ctlOff.length ? `. ${names(ctlOff)} could too, once allowed below` : "");
  else if (ctlOff.length) hint = `${names(ctlOff)} could wake the PC but is not allowed to yet: allow it below`;
  else if (btOn.length) hint = `${names(btOn)} may wake the PC, so a Bluetooth controller might, depending on the pad and the board. A keyboard or mouse always can`;
  else if (btOff.length) hint = `${names(btOff)} could be allowed to wake the PC, below; whether a Bluetooth pad then does depends on the pad and the board`;
  else hint = "No controller or receiver on this PC can wake it from sleep: their drivers do not offer it, and a Bluetooth radio on a desktop rarely does. " +
    "A wireless keyboard or mouse, or the power button, wakes it. The Xbox Wireless Adapter and some wired pads can, and appear here once plugged in";
  if (wake.lastWake) hint += `. Last woken by: ${wake.lastWake}`;
  rows.push({
    name: "Waking from sleep", hint, type: "action", label: "Re-check",
    action: () => send({ cmd: "restRefresh", force: true }),
  });
  if (wake.canSleep) {
    for (const d of ctlOff.concat(btOff).slice(0, 6))
      rows.push({
        name: d.name,
        hint: d.kind === "controller"
          ? "Allow this device to wake the PC: Device Manager's own switch, with one Windows permission prompt"
          : "Allow the Bluetooth radio to wake the PC, with one Windows permission prompt. A pad paired to it may then wake it too",
        type: "action", label: "Allow",
        action: () => send({ cmd: "wakeAllow", name: d.name }),
      });
    rows.push(wake.signInOnWake
      ? {
          name: "Sign-in after waking",
          hint: "Windows asks for your PIN or password when the PC wakes, on a lock screen the pad cannot type into. " +
            "Turning it off lands you straight back where you were. One Windows permission prompt; the same switch is in Windows' Settings → Accounts → Sign-in options",
          warn: "On: a wake from sleep stops at the lock screen until it is signed into.",
          type: "action", label: "Turn off",
          action: () => askConfirm({
            title: "Skip the sign-in after waking?",
            body: "Anyone who wakes the PC lands straight in the session, with no PIN or password. Windows asks for permission once.",
            yesLabel: "Turn off", icon: "lock", danger: false,
            onYes: () => send({ cmd: "signInOnWake", on: false }),
          }),
        }
      : {
          name: "Sign-in after waking",
          hint: "Off: a wake lands straight back where it was. Turn it on to have Windows ask for your PIN or password after sleep (one Windows permission prompt)",
          type: "action", label: "Turn on",
          action: () => send({ cmd: "signInOnWake", on: true }),
        });
  }
  return rows;
}

/* The theme's own rows and the way back, as the last block of Appearance. The options the theme
   declares, read and written through the validated definitions (see themeSettingDefs), then a
   restore that puts THIS theme's look, animation and options back -- every other theme keeps its
   own. The block is there for a theme with no options of its own too: the restore still has the
   look and the animation to restore. */
function themeSettingRows(theme, s, set) {
  const defs = theme ? themeSettingDefs(theme) : [];
  const name = theme && theme.name ? theme.name : "Shelf";
  const rows = [{ section: `${name.toUpperCase()} OPTIONS`, cat: "appearance" }];
  // Trailers first, under every theme: where a film may play is a fact about the layout the
  // theme draws, and it is kept in the theme's own bag like the accent, so each theme decides.
  rows.push(cycleRow("Trailers", trailerModes(), () => lookTrailers(),
    v => set(() => lookSet(LOOK_IDS.trailers, v)),
    (libraryCanHostTrailers()
      ? "The game's trailer plays over the art once the highlight has rested on it for three seconds. "
      : "The game's trailer plays behind its page once it has been open for three seconds. ")
    + "Steam's, kept on this PC after the first play; through YouTube for a game Steam has no trailer for",
    null, TRAILER_LABELS));
  if (lookTrailers() !== "off")
    rows.push(toggleRow("Trailer sound", "Off, trailers play silently",
      () => lookTrailerSound(), v => set(() => lookSet(LOOK_IDS.trailerSound, v))));
  if (s.achievementsEnabled !== false)
    rows.push(toggleRow("Achievement progress", "How much of each game's achievements you have unlocked, shown on the library. Off, it shows only on a game's page and in Stats",
      () => lookAchievements(), v => set(() => { lookSet(LOOK_IDS.achievements, v); renderLibrary(); })));
  const put = (d, v) => set(() => { lookBag(true)[d.id] = v; });
  for (const d of defs) {
    const cur = () => themeSettingValue(theme, d);
    if (d.type === "toggle") rows.push(toggleRow(d.name, d.hint, cur, v => put(d, v)));
    else if (d.type === "select") rows.push(cycleRow(d.name, d.options, cur, v => put(d, v), d.hint, null, d.labels));
    else rows.push(sliderRow(d.name, cur, d.min, d.max, d.step, v => put(d, v), v => fmtThemeValue(d, v), d.hint));
  }
  const bag = lookBag(false);
  const touched = !!bag && Object.keys(bag).length > 0;
  rows.push({
    name: `Restore ${name}'s defaults`,
    hint: (defs.length
      ? `${name}'s colour, hints, animation and the options above all go back to how a fresh install has them`
      : `${name}'s colour, hints and animation go back to how a fresh install has them`)
      + ". Every other theme keeps its own",
    type: "action", label: "Restore",
    action: () => {
      if (!touched) { toast(`${name} is already at its defaults`); return; }
      set(() => { if (s.themeSettings) delete s.themeSettings[s.theme || ""]; });
      toast(`${name}'s defaults restored`);
    },
  });
  return rows;
}

/*
 * A credential. The page never holds one: the host keeps every key sealed for the Windows account
 * and sends the page only a marker that says a key is set (SECRET_SET, AppSettings.ForPage on the
 * host), so these rows -- which sit on a TV, the one screen in the house most likely to have
 * someone else looking at it -- can show "Set" and nothing more. A opens the text prompt EMPTY:
 * a new key replaces the old one, leaving the field empty keeps it, and typing "clear" removes it.
 * The marker goes back to the host untouched on a save, and the host reads it as "keep".
 */
const SECRET_SET = "dpapi:set";
function secretRow(name, s, hint, get, setV) {
  const cur = () => get() || "";
  const isSet = () => !!cur();
  return {
    name, hint, type: "action",
    label: isSet() ? "Set" : "Not set",
    action: () => openInput(name.toUpperCase(), "", v => {
      const typed = v.trim();
      if (!typed) return;                                  // empty keeps whatever is set
      if (typed.toLowerCase() === "clear") { setV(""); return; }
      setV(typed);
    }, { note: isSet() ? "A new key replaces the one that is set. Leave empty to keep it, or type clear to remove it" : "Pasted or typed here, and kept sealed on this PC. It is never shown again" }),
  };
}
/* The state of the Steam link, in one line under its toggle. It names the account because the
   account is read off the Steam client rather than typed in, and the wrong one would otherwise be
   invisible; it carries the count because an empty answer and a broken fetch look identical. */
function steamAccountHint() {
  const a = S.steamAccount;
  if (!a || !a.steamId) return "No Steam login was found on this PC. Sign in to Steam once, then rescan";
  const who = `Signed in to Steam as ${a.personaName || a.steamId}`;
  if (!S.settings || !S.settings.steamShowOwned)
    return `${who}. Lists your whole Steam library, with anything not on disk greyed out and installable with [[A]]`;
  if (a.error) return `${who} · ${a.error}`;
  if (a.fetchedAt) return `${who} · ${a.ownedCount} game${a.ownedCount === 1 ? "" : "s"} in your library`;
  return `${who} · fetching your library…`;
}

/* One row per store you sign in to, the way Playnite's library plugins work. Being signed in IS the
   opt-in: there is no separate toggle, because a store you have signed into and then hidden the
   games of is two settings saying opposite things. The hint carries the account name, because a
   wrong account would otherwise be invisible, and the count, because an empty answer and a broken
   fetch look identical. */
function storeRow(store, name, offHint) {
  const st = S.stores && S.stores[store];
  const signedIn = !!(st && st.signedIn);
  let hint = offHint;
  if (signedIn) {
    const who = st.user ? `Signed in as ${st.user}` : "Signed in";
    hint = st.error ? `${who} · ${st.error}`
      : st.fetchedAt ? `${who} · ${st.count} game${st.count === 1 ? "" : "s"}`
      : `${who} · fetching your library…`;
  }
  return {
    name, hint, type: "action", label: signedIn ? "Sign out" : "Sign in",
    action: () => {
      if (signedIn) askSignOut(store, name);
      else { toast("Opening the sign-in window…"); send({ cmd: "storeSignIn", store }); }
    },
  };
}

/* Steam's sign-in: the account's own token reads its library and achievements from Steam whatever
   the profile's privacy says (SteamWebSession on the host). It is one of the two ways in, the other
   being a Web API key of the user's own; the shared service no longer carries the account's data
   at all, so nothing identifying leaves for it. Signing in turns the switch above on, as signing
   in to any store does. */
function steamSignInRow() {
  const st = S.stores && S.stores.steam;
  const signedIn = !!(st && st.signedIn);
  const a = S.steamAccount || {};
  const who = (st && st.user) || a.personaName || a.steamId || "your account";
  const why = (st && st.error) || a.error;
  const hint = !signedIn
    ? "Sign in on Steam's own page and the games you own and your achievements are read from Steam with that sign-in, whatever your profile's privacy settings. Your password never reaches Loungepad, and your account's data goes to Steam and nowhere else"
    : why ? `Signed in as ${who} · ${why}`
    : a.fetchedAt ? `Signed in as ${who} · ${a.ownedCount} game${a.ownedCount === 1 ? "" : "s"} in your library`
    : `Signed in as ${who} · fetching your library…`;
  return {
    name: "Steam sign-in", hint, type: "action", label: signedIn ? "Sign out" : "Sign in",
    action: () => {
      if (signedIn) askSignOut("steam", "Steam account");
      else { toast("Opening the sign-in window…"); send({ cmd: "storeSignIn", store: "steam" }); }
    },
  };
}

function askSignOut(store, name) {
  confirmState = {
    title: `Sign out of ${name.replace(/ account$/i, "")}?`,
    body: "The games you own there leave the library. Anything installed stays, and signing in again brings the rest back.",
    yesLabel: "Sign out",
    icon: "x", danger: true,
    onYes: () => send({ cmd: "storeSignOut", store }),
  };
  confirmIdx = 0;
  showOverlay("overlay-confirm");
  renderConfirm();
}

function gamePassHint() {
  const g = S.stores && S.stores.gamePass;
  if (!S.settings || !S.settings.gamePassCatalog)
    return "Every game included with PC Game Pass, installable from here. Playing one needs the Xbox app and a subscription";
  if (g && g.error) return g.error;
  return g && g.count ? `${g.count} games in the catalogue` : "Fetching the catalogue…";
}

/* One row for the whole update: its name is the version running, its hint says where things
   stand, and its button is the next step -- check, install, or restart onto a download that is
   already here. A step that is under way gets a button that does nothing rather than a second
   request. */
function updateRow() {
  const u = S.update || { state: "idle", current: "" };
  const check = () => send({ cmd: "updateCheck" });
  const install = () => {
    confirmState = {
      title: `Install Loungepad ${u.latest}?`,
      body: "Loungepad closes and opens again on the new version. It takes a few seconds, and nothing in your library changes.",
      yesLabel: "Install and restart",
      icon: "refresh", danger: false,
      onYes: () => send({ cmd: "updateInstall" }),
    };
    confirmIdx = 0;
    showOverlay("overlay-confirm");
    renderConfirm();
  };
  const idle = () => {};
  const auto = !S.settings || S.settings.autoUpdate !== false;
  const [hint, label, action] = {
    checking:    ["Checking for updates…", "Checking…", idle],
    upToDate:    ["Up to date", "Check now", check],
    available:   [`Version ${u.latest} is available`, "Install", install],
    downloading: [`Downloading version ${u.latest}… ${u.progress}%`, `${u.progress}%`, idle],
    ready:       [auto ? `Version ${u.latest} is downloaded and installs the next time Loungepad starts`
                       : `Version ${u.latest} is downloaded`, "Restart now", install],
    failed:      [u.message || "The update did not work", "Try again", check],
    unsupported: [u.message || "This copy cannot update itself", "Unavailable", idle],
  }[u.state] || ["Checks GitHub for a newer release", "Check now", check];
  return { name: u.current ? `Loungepad ${u.current}` : "Loungepad", hint, type: "action", label, action };
}

function toggleRow(name, hint, get, setV) {
  return {
    name, hint, type: "toggle", value: get(),
    adjust: () => setV(!get()),
    action: () => setV(!get()),
  };
}

/* A row with a fixed set of values carries them as `choices` ({ value, label, html?, swatch? }),
   with `current` and `pick(value)`. A on the row opens them as a list (openSettingChoice), so every
   option is in view at once rather than met one at a time under ◂ ▸. `adjust` stays for the form
   sheet, which still nudges its dates and times sideways. */

/* Both row builders fall back when a setting is missing. A settings file written by an older
   build has no key for an option added since, and one undefined value used to throw inside the
   formatter and take the whole settings screen down with it. */
function sliderRow(name, get, min, max, step, setV, fmt, hint) {
  const cur = () => { const v = get(); return typeof v === "number" && isFinite(v) ? v : min; };
  // Every step as its own value, rounded to the step's own decimals so 0.05 + 3 x 0.01 is 0.08
  // and not 0.08000000000000002 -- the value is written back to settings.json exactly as listed.
  const places = (String(step).split(".")[1] || "").length + (String(min).split(".")[1] || "").length;
  const values = [];
  for (let i = 0; i <= Math.round((max - min) / step); i++) values.push(+(min + i * step).toFixed(places));
  const nearest = values.reduce((a, b) => (Math.abs(b - cur()) < Math.abs(a - cur()) ? b : a), values[0]);
  return {
    name, hint, type: "slider", value: cur(), min, max, fmt,
    choices: values.map(v => ({ value: v, label: fmt(v) })),
    current: nearest,
    pick: (v) => setV(v),
    adjust: (dir) => {
      let v = Math.round((cur() + dir * step) / step) * step;
      v = Math.max(min, Math.min(max, v));
      setV(v);
    },
  };
}

/* `labels` renames an option on screen without changing what is stored: "Builtin" is the value
   the host has always written to settings.json, but "Loungepad Keyboard" is what it is called. */
function cycleRow(name, options, get, setV, hint, warn, labels) {
  const cur = () => (options.includes(get()) ? get() : options[0]);
  return {
    name, hint, warn, type: "select", value: (labels && labels[cur()]) || cur(),
    choices: options.map(o => ({ value: o, label: (labels && labels[o]) || o })),
    current: cur(),
    pick: (v) => setV(v),
    adjust: (dir) => {
      const i = (options.indexOf(cur()) + dir + options.length) % options.length;
      setV(options[i]);
    },
  };
}

/* A row whose options are gamepad buttons or combos. The stored value is the XInput name, as it
   always was; what is shown is the button drawn for the pad last used, in the list as well. */
function buttonRow(name, options, get, setV, hint, warn) {
  const row = cycleRow(name, options, get, setV, hint, warn);
  row.valueHtml = comboHtml(options.includes(get()) ? get() : options[0]);
  row.choices.forEach(c => { c.html = comboHtml(c.value); });
  return row;
}

/* The accent picker: the presets as a list, each with its colour beside its name, and a last row
   that asks for a hex code. A hand-typed colour is listed as well while it is the one in use, so
   the list always has the current colour ticked. */
function accentRow(set) {
  const cur = () => lookAccent();
  const custom = () => !ACCENTS.some(a => a.hex === cur());
  const choices = ACCENTS.map(a => ({ value: a.hex, label: a.name, swatch: a.hex }));
  if (custom()) choices.push({ value: cur(), label: `Custom · ${cur()}`, swatch: cur() });

  return {
    name: "Accent colour",
    hint: "Focus rings, active tabs and sliders. Every shade of it is derived from this one value",
    type: "swatch",
    swatches: ACCENTS.map(a => a.hex),
    value: cur(),
    label: accentName(cur()),
    custom: custom(),
    choices,
    current: cur(),
    pick: (hex) => set(() => lookSet(LOOK_IDS.accent, hex)),
    more: [{
      label: "Enter a colour…", sub: "Any colour, as a hex code like #F0A253", icon: "edit",
      action: () => openInput("Accent colour (hex, e.g. #F0A253)", cur(), (v) => {
        const hex = v.startsWith("#") ? v : "#" + v;
        if (isHexColor(hex)) set(() => lookSet(LOOK_IDS.accent, hex.toUpperCase()));
        else toast("Enter a colour as #RRGGBB");
      }),
    }],
  };
}

/** A on a Settings row: a list of its values when it has them, else whatever the row does. */
function activateSettingRow(row) {
  if (row.choices) openSettingChoice(row);
  else if (row.action) row.action();
  else if (row.adjust) row.adjust(1);
}

/** Every value a row can take, as a list with the one in force ticked and highlighted. */
function openSettingChoice(row) {
  if (!row.choices.length) { toast("Nothing to choose from"); return; }
  const items = row.choices.map(c => ({
    label: c.label, html: c.html, swatch: c.swatch, sub: c.sub,
    checked: c.value === row.current, radio: true,
    action: () => row.pick(c.value),
  }));
  (row.more || []).forEach(m => items.push(m));
  openChoice(row.name, items);
}

const KEYBOARD_APP_LABELS = {
  Builtin: "Loungepad Keyboard",
  TabTip: "Windows touch keyboard",
  Osk: "Windows on-screen keyboard",
};

/* ---- settings categories ----
   One screen of every setting had become unreadable. The rows are unchanged; they are just
   filtered to the active category, reached with the sidebar. The shoulders are deliberately not
   bound: they used to move between Library, Collections and Settings, and with one screen left
   there is nothing for them to do -- so the legend does not offer them either. */
const SETTINGS_TABS = [
  { id: "general",    label: "General" },
  { id: "appearance", label: "Appearance" },
  { id: "input",      label: "Controller" },
  { id: "keyboard",   label: "Keyboard" },
  { id: "actions",    label: "Actions" },
  { id: "library",    label: "Library" },
  { id: "stats",      label: "Stats" },
];
let settingsTab = "general";
/* Which half of the screen has the highlight. Settings opens on the sidebar, so the first thing
   you choose is what you are configuring rather than being dropped into a list of rows; A (or
   Right) steps into the options and B (or Left) steps back out to the categories. */
let settingsPane = "nav";

function settingsRows() {
  // Actions is not a list of settings but a grid of apps that drills into lists; actions.js
  // builds whatever level is showing, in the same row shape.
  if (settingsTab === "actions") return actionsSettingsRows();
  let cat = null;
  return allSettingsRows().filter(r => {
    if (r.section) cat = r.cat;
    return (r.section ? r.cat : cat) === settingsTab;
  });
}

function setSettingsTab(id) {
  const changed = settingsTab !== id;
  if (changed) { settingsTab = id; settingsIdx = 0; }
  // Always the grid on the way in, never a list left open from last time.
  if (changed && id === "actions") actionsTabReset();
  renderSettings();
  // Only when the rows are a different category's, never on the re-render every move costs.
  if (changed) pulse($("settingsScroll"));
}

function settingsTabIdx() {
  const i = SETTINGS_TABS.findIndex(t => t.id === settingsTab);
  return i < 0 ? 0 : i;
}

function enterSettingsPane(pane) {
  settingsPane = pane;
  if (pane === "rows") settingsIdx = Math.min(settingsIdx, Math.max(0, settingsRows().filter(r => !r.section).length - 1));
  renderSettings();
}

function cycleSettingsTab(dir) {
  const i = settingsTabIdx();
  setSettingsTab(SETTINGS_TABS[(i + dir + SETTINGS_TABS.length) % SETTINGS_TABS.length].id);
}

function renderSettingsNav() {
  const nav = $("settingsNav");
  if (!nav) return;
  let cat = null;
  const counts = {};
  allSettingsRows().forEach(r => {
    if (r.section) { cat = r.cat; return; }
    counts[cat] = (counts[cat] || 0) + 1;
  });
  counts.actions = shownApps().length;

  // Built once; from then on only the active mark and the counts change, so the tab's own
  // transition runs and a node is never destroyed under a click.
  if (nav.children.length !== SETTINGS_TABS.length) {
    nav.innerHTML = "";
    SETTINGS_TABS.forEach(t => {
      const el = document.createElement("div");
      el.className = "set-tab";
      el.dataset.focusable = "";
      el.dataset.focusKey = "settab:" + t.id;
      el.dataset.settingsTab = t.id;
      el.innerHTML = `<span>${esc(t.label)}</span><span class="set-tab-count"></span>`;
      el.addEventListener("click", () => { setFocusEl(el); setSettingsTab(t.id); });
      el.addEventListener("mouseenter", () => {
        if (!hoverEnabled()) return;
        // Repaint the highlight only: a node destroyed between mousedown and mouseup never raises
        // a click, which is why the categories once could not be clicked at all.
        setFocusEl(el);
        paintNav();
      });
      nav.appendChild(el);
    });
  }
  SETTINGS_TABS.forEach((t, i) => {
    const el = nav.children[i];
    el.classList.toggle("active", t.id === settingsTab);
    el.querySelector(".set-tab-count").textContent = counts[t.id] || 0;
  });
}

/* The wake facts are read off Windows by spawning powercfg, so they are asked for when General
   is on screen and at most every half minute, not on every highlight move. */
let restRefreshedAt = 0;

function renderSettings() {
  renderSettingsNav();
  if (settingsTab === "general" && Date.now() - restRefreshedAt > 30000) {
    restRefreshedAt = Date.now();
    send({ cmd: "restRefresh" });
  }
  // The legend changes with the pane: on the categories B leaves Settings, inside the options it
  // only steps back to the categories, and saying so is cheaper than letting people find out.
  const footEl = $("settingsFoot");
  // No LB/RB entry: there is one screen left, so the shoulders switch between nothing. A legend
  // that names a button which does not respond is worse than a shorter legend. A says what it
  // does to the row under the highlight: a list of values, a switch, or the row's own action.
  const rows = settingsRows();
  const onRow = settingsPane === "rows" ? rows.filter(r => !r.section)[settingsIdx] : null;
  const aLabel = !onRow ? "Select" : onRow.choices ? "Change" : onRow.type === "toggle" ? "Switch" : "Select";
  if (footEl) footEl.innerHTML = settingsPane === "nav"
    ? foot(["A", "Open"], ["B", "Back"], ["DpadV", "Category"])
    : settingsTab !== "actions" ? foot(["A", aLabel], ["B", "Categories"])
    : actionsUi.level === "apps" ? foot(["A", "Open"], ["B", "Categories"])
    : actionsUi.level === "app" ? foot(["A", "Edit"], ["B", "Back"])
    : foot(["A", aLabel], ["B", "Back"]);
  const scroll = $("settingsScroll");
  // The Actions grid is the same scroller laid out as tiles; everything else is rows.
  scroll.classList.toggle("apps-grid", actionsGridMode());

  const focusables = rows.filter(r => !r.section);
  settingsIdx = Math.max(0, Math.min(settingsIdx, focusables.length - 1));

  /* Updated in place. The list used to be emptied and rebuilt on every highlight move, and
     emptying a scroller snaps its scrollTop to 0 -- so each step started a glide from the top back
     down to the row, which read as the list jittering under the D-pad. Now the elements stay
     unless the shape of the list changes (a different category, a toggle revealing a sub-row): a
     move only flips a class, the scroller is never touched, and the row's own focus transition
     gets to run. A rebuild keeps the scroll position, as renderMenu does. */
  const nodes = [];
  let fi = -1;
  rows.forEach(r => {
    if (r.section) { nodes.push({ section: r.section, bare: !!r.bare }); return; }
    fi++;
    nodes.push({ row: r, idx: fi, html: r.tile ? actionsTileHtml(r) : settingsRowHtml(r, true) });
  });
  // The Actions category's level is part of the shape: its list and its editor are different
  // rows under one tab, and the level tag is what makes the drill-down rebuild.
  const levelTag = settingsTab === "actions" ? `|${actionsUi.level}:${actionsUi.appId}:${actionsUi.actionId}` : "";
  const shape = settingsTab + levelTag + "|" + nodes.map(n => n.section !== undefined ? "s:" + n.section : n.row.tile ? "t" : "r").join("|");
  if (scroll.__shape !== shape) {
    const keepTop = scroll.scrollTop;
    scroll.innerHTML = "";
    nodes.forEach(n => scroll.appendChild(n.section !== undefined ? settingsSectionEl(n.section, n.bare) : n.row.tile ? actionsTileEl(n.idx) : settingsRowEl(n.idx)));
    scroll.scrollTop = keepTop;
    scroll.__shape = shape;
  }
  nodes.forEach((n, i) => {
    if (n.section !== undefined) return;
    const el = scroll.children[i];
    el.dataset.focusKey = "setrow:" + settingsTab + ":" + n.idx;
    el.classList.toggle("muted", !!n.row.muted);
    if (el.__html !== n.html) { el.innerHTML = n.html; el.__html = n.html; }
  });

  // Keep the highlight on the row the caller has selected, then let the engine paint.
  const scope = document.getElementById("screen-settings");
  if (settingsPane === "rows") setScopeKey(scope, "setrow:" + settingsTab + ":" + settingsIdx);
  else setScopeKey(scope, "settab:" + settingsTab);
  paintNav();
  const cur = focusEl(scope);
  if (cur && focusVisible()) revealFocus(cur);
}

/* A `bare` section still starts its category but draws no heading (Settings → Stats' first row). */
function settingsSectionEl(text, bare) {
  const el = document.createElement("div");
  el.className = "set-section" + (bare ? " bare" : "");
  el.textContent = text;
  return el;
}

/* A row's frame and its handlers, made once per shape. The handlers go by index and read the row
   afresh, so they stay right while the row's content is updated under them. */
function settingsRowEl(idx) {
  const el = document.createElement("div");
  el.className = "set-row";
  el.dataset.focusable = "";
  el.dataset.rowIndex = idx;
  el.addEventListener("mouseenter", () => {
    if (!hoverEnabled()) return;
    if (settingsIdx === idx && settingsPane === "rows") return;
    settingsIdx = idx; settingsPane = "rows"; renderSettings();
  });
  el.addEventListener("click", () => {
    settingsIdx = idx; settingsPane = "rows";
    const row = settingsRows().filter(x => !x.section)[idx];
    if (!row) return;
    renderSettings();
    // Anywhere on the row is the same as pressing A: a list of values, a switch, or the action.
    activateSettingRow(row);
  });
  return el;
}

/** What a row shows: its name and hint on the left, its value or its action on the right.
    `picker` is Settings, where a value is changed from a list (A) and a switch flips on A: no
    ◂ ▸, because Left and Right move between the panes there. The form sheet still steps its
    values sideways and keeps them. */
function settingsRowHtml(r, picker) {
  // The arrows carry a direction, so a mouse can step a value either way (see renderForm).
  const left = `<span class="arrow" data-dir="-1">◂</span>`, rightArrow = `<span class="arrow" data-dir="1">▸</span>`;
  const list = !!(picker && r.choices);
  // A value that opens a list ends in a chevron, as the filter menu's dropdowns do.
  const [pre, post] = list ? ["", `<span class="set-chev">›</span>`] : picker ? ["", ""] : [left, rightArrow];
  let right = "";
  if (r.type === "toggle") {
    right = r.value
      ? `${pre}<span class="set-toggle-on">ON</span>${post}`
      : `${pre}<span class="set-toggle-off">OFF</span>${post}`;
  } else if (r.type === "select") {
    right = `${list ? "" : left}<span>${r.valueHtml || esc(r.value)}</span>${list ? post : rightArrow}`;
  } else if (r.type === "swatch") {
    // The presets are shown as dots, the selected one ringed, with a trailing dot for a custom
    // colour so the strip reads as the row's full range rather than a value plus a mystery.
    const dot = (hex, on) =>
      `<span class="swatch${on ? " on" : ""}" style="background:${esc(hex)}"></span>`;
    const dots = r.swatches.map(hex => dot(hex, hex === r.value)).join("")
      + (r.custom ? dot(r.value, true) : "");
    right = `${pre}<span class="swatch-strip">${dots}</span>`
      + `<span class="swatch-name">${esc(r.label)}</span>${post}`;
  } else if (r.type === "slider") {
    const pct = ((r.value - r.min) / (r.max - r.min)) * 100;
    right = `<div class="slider">${pre}<div class="slider-track"><div class="slider-fill" style="width:${pct}%"></div></div>${list ? "" : post}<span class="slider-val">${esc(r.fmt(r.value))}</span>${list ? post : ""}</div>`;
  } else if (r.type === "action") {
    right = `<span class="set-action-label${r.danger ? " danger" : ""}">${esc(r.label)}</span>`;
  } else if (r.type === "html") {
    // The row draws its own value: key caps and a button glyph, for an action.
    right = r.valueHtml || "";
  }
  return `<div class="set-left"><div class="set-name">${esc(r.name)}</div>${r.hint ? `<div class="set-hint">${hintHtml(r.hint)}</div>` : ""}${r.warn ? `<div class="set-warn">${hintHtml(r.warn)}</div>` : ""}</div><div class="set-value">${right}</div>`;
}

/* Settings keeps its two panes, but they are now just two groups of focusables in one
   scope: the categories on the left, the rows on the right. Up and Down are geometric, and
   the pane is derived from where the highlight actually landed rather than tracked by
   hand -- which is what lets a theme stack them, or drop the sidebar entirely.

   Left and Right cross between the two panes: Right (or A) from a category into its rows, at
   the row that was last highlighted there, and Left from any row back to the category. A value
   is never changed sideways any more: A opens every value the row can take as a list
   (openSettingChoice), so the whole range is in view instead of being met one ◂ ▸ at a time.
   The one exception is the Actions grid, whose tiles sit side by side: Left and Right walk the
   row of tiles first, and only Left off its first tile goes back to the categories. */
function settingsInput(btn) {
  const scope = document.getElementById("screen-settings");
  const el = focusEl(scope);
  const rows = settingsRows().filter(r => !r.section);
  const row = el && el.dataset.rowIndex !== undefined ? rows[parseInt(el.dataset.rowIndex, 10)] : null;
  const onTab = !!(el && el.dataset.settingsTab);

  // Inside a category the highlight stays among its rows (paneMove), and on the categories among
  // the categories; both come round at the ends.
  const inRows = (x) => x.dataset.rowIndex !== undefined;
  const inTabs = (x) => x.dataset.settingsTab !== undefined;
  switch (btn) {
    case "Up": case "Down":
      if (row ? paneMove(btn, inRows) : onTab ? paneMove(btn, inTabs) : navMove(btn)) syncSettingsPane();
      break;

    case "Left":
      if (!row) break;
      if (row.tile && tileBeside(el, "Left", inRows) && navMove("Left", inRows)) { syncSettingsPane(); break; }
      enterSettingsPane("nav");
      break;

    case "Right":
      if (onTab) { setSettingsTab(el.dataset.settingsTab); enterSettingsPane("rows"); break; }
      if (row && row.tile && tileBeside(el, "Right", inRows) && navMove("Right", inRows)) syncSettingsPane();
      break;

    case "A":
      if (!focusVisible()) break;
      // The category comes off the element, not off settingsTab. Hovering a category highlights
      // it without selecting it, so A was opening whichever one had last been activated -- the
      // highlight said Keyboard and the rows that appeared were Controller's, which reads as A
      // not working at all.
      if (onTab) { setSettingsTab(el.dataset.settingsTab); enterSettingsPane("rows"); break; }
      if (row) activateSettingRow(row);
      break;

    // Back steps out to the categories first, and only leaves Settings from there. Inside the
    // Actions category it first climbs back out of an app's list or an action's editor.
    case "B":
      if (onTab) switchView("library");
      else if (settingsTab === "actions" && actionsBack()) break;
      else enterSettingsPane("nav");
      break;
  }
}

/** Whether another tile sits beside `el` on its own line, in that direction. navMove would wrap
    round the line instead of reporting the end of it, and the end is where Left leaves the grid. */
function tileBeside(el, dir, accept) {
  const r = el.getBoundingClientRect(), mid = Nav.centre(r).x;
  return Nav.focusables(Nav.activeScope()).some(x => {
    if (x === el || !accept(x)) return false;
    const o = x.getBoundingClientRect();
    if (!(o.bottom > r.top && o.top < r.bottom)) return false;
    const c = Nav.centre(o).x;
    return dir === "Left" ? c < mid - 1 : c > mid + 1;
  });
}

/** Pane and category follow the highlight, so nothing has to be kept in step by hand. */
function syncSettingsPane() {
  const el = focusEl(document.getElementById("screen-settings"));
  if (!el) return;
  if (el.dataset.settingsTab) {
    settingsPane = "nav";
    if (el.dataset.settingsTab !== settingsTab) setSettingsTab(el.dataset.settingsTab);
    else renderSettings();
  } else if (el.dataset.rowIndex !== undefined) {
    settingsPane = "rows";
    settingsIdx = parseInt(el.dataset.rowIndex, 10);
    renderSettings();
  }
}

/* One of the WINDOWS AND STEAM switches. Windows' two change on the spot and are shown at once;
   Steam has to close and reopen, which is said first, and cannot happen under a running game --
   closing Steam would end a Steam game with it. The host's state push confirms each one. */
function setXboxButton(what, on) {
  const xb = S.xboxButton || (S.xboxButton = {});
  if (what === "steam") {
    if (!xb.steamRunning) { send({ cmd: "xboxButtonSet", what, on }); return; }
    if (S.gameRunning) { toast("Close the game first: Steam has to restart for this"); return; }
    askConfirm({
      title: "Restart Steam?",
      body: "Steam rewrites its settings when it closes, so it has to close for this to stick. It opens again in the tray straight away, signed in as before.",
      yesLabel: "Restart Steam", icon: "refresh", danger: false,
      onYes: () => send({ cmd: "xboxButtonSet", what, on }),
    });
    return;
  }
  xb[what] = on;
  send({ cmd: "xboxButtonSet", what, on });
  if (view === "onboarding") renderOnboarding(); else renderSettings();
}

function scheduleSave() {
  clearTimeout(saveTimer);
  saveTimer = setTimeout(() => send({ cmd: "saveSettings", settings: S.settings }), 350);
}

/* ============================== filter overlay ============================== */

/* filterLevel: null = the Filter/Sort menu, "filter"/"sort" = an open dropdown */
let filterLevel = null;

function applyFilter() {
  focus = { zone: "grid", row: 0, col: 0 };
  renderLibrary();
  renderFilter();
  // The grid behind the menu is a new list now, and rising in says so.
  pulse($("gridScroll"));
}

function openFilter() {
  filterOpen = true; filterIdx = 0; filterLevel = null;
  showOverlay("overlay-filter");
  renderFilter();
}

function closeFilter() {
  filterOpen = false; filterLevel = null;
  hideOverlay("overlay-filter");
}

/** Rows of the top-level Filter / Sort menu. */
function filterMenuRows() {
  const n = activeFilterCount();
  return [
    { name: "Search", icon: "search", summary: F.search ? `“${F.search}”` : "By title", run: () => { closeFilter(); openSearch(); } },
    { name: "Filter", icon: "filter", summary: n ? `${n} active` : "All games", open: "filter" },
    { name: "Sort", icon: "sort", summary: SORTS.find(s => s.id === F.sort).label, open: "sort" },
  ];
}

/** Flat list of the multi-select dropdown, with category headers interleaved. */
function filterDropdownRows() {
  const rows = [{ cat: "PLATFORM" }];
  const platformRow = (p, icon, sub) => ({
    label: p, icon, sub, checked: F.platforms.has(p),
    toggle: () => { F.platforms.has(p) ? F.platforms.delete(p) : F.platforms.add(p); },
  });
  PLATFORMS.forEach(p => rows.push(platformRow(p, "gamepad")));
  // One row per system there are ROMs for, under their own heading. Same set as the stores --
  // ticking SNES and Steam shows both -- so a system is a platform in every sense the filter has.
  const systems = emulatedPlatforms();
  if (systems.length) {
    rows.push({ cat: "EMULATED" });
    systems.forEach(s => rows.push(platformRow(s.name, "cartridge", `${s.count}`)));
  }
  rows.push({ cat: "STATUS" });
  STATUSES.forEach(s => rows.push({
    label: s, icon: s === "Installed" ? "checkCircle" : "download", checked: F.status.has(s),
    toggle: () => { F.status.has(s) ? F.status.delete(s) : F.status.add(s); },
  }));
  // Only when there are some. An empty category reads as a broken feature, and collections are
  // made from the game menu rather than here, so there is nothing to offer until one exists.
  if (S.collections.length) {
    rows.push({ cat: "COLLECTIONS" });
    S.collections.forEach(c => rows.push({
      label: c.name, icon: "folder", sub: `${(c.gameIds || []).length}`,
      checked: F.collections.has(c.id),
      toggle: () => { F.collections.has(c.id) ? F.collections.delete(c.id) : F.collections.add(c.id); },
    }));
  }
  rows.push({ cat: "OTHER" });
  rows.push({
    label: "Favorites only", icon: "star", checked: F.fav,
    toggle: () => { F.fav = !F.fav; },
  });
  // The only way back to something you hid, short of turning the filter off again. Counted so the
  // row says how many there are: an empty hidden view and a broken filter look the same.
  const hiddenCount = S.games.filter(g => g.hidden).length;
  rows.push({
    label: "Hidden games", icon: "eyeOff", sub: `${hiddenCount}`,
    checked: F.hidden,
    toggle: () => { F.hidden = !F.hidden; },
  });
  return rows;
}

const SORT_ICONS = {
  az: "sortAsc", za: "sortDesc", recent: "clock",
  played: "timer", sizeDesc: "chevronsDown", sizeAsc: "chevronsUp", score: "star", ach: "trophy",
};

function sortDropdownRows() {
  return SORTS.map(s => ({
    label: s.label, icon: SORT_ICONS[s.id], checked: F.sort === s.id, radio: true,
    toggle: () => { F.sort = s.id; },
  }));
}

function currentFilterRows() {
  if (filterLevel === "filter") return filterDropdownRows();
  if (filterLevel === "sort") return sortDropdownRows();
  return filterMenuRows();
}

/** Indices of rows that can take focus (category headers can't). */
function filterFocusable(rows) {
  return rows.map((r, i) => r.cat ? -1 : i).filter(i => i >= 0);
}

function renderFilter() {
  const rows = currentFilterRows();
  const focusable = filterFocusable(rows);
  if (!focusable.includes(filterIdx)) filterIdx = focusable[0] ?? 0;

  $("filterTitle").textContent =
    filterLevel === "filter" ? "FILTER" : filterLevel === "sort" ? "SORT" : "FILTER & SORT";

  const footHtml = filterLevel === null
    ? foot(["A", "Open"], ["B", "Close"], ["Y", "Reset all"])
    : foot(["A", filterLevel === "sort" ? "Choose" : "Toggle"], ["B", "Back"], ["Y", "Reset all"]);

  renderMenu($("filterList"), $("filterFoot"), rows, filterIdx, footHtml,
    (i) => { if (filterIdx !== i) { filterIdx = i; renderFilter(); } },
    (i) => { filterIdx = i; filterActivate(); });
}

function filterActivate() {
  const row = currentFilterRows()[filterIdx];
  if (!row) return;
  if (row.run) { row.run(); return; }
  if (row.open) { filterLevel = row.open; filterIdx = 0; renderFilter(); return; }
  row.toggle();
  if (row.radio) { filterLevel = null; filterIdx = 2; }  // sort is single-select: pick and close
  applyFilter();
}

function filterInput(btn) {
  const rows = currentFilterRows();
  switch (btn) {
    // Category headers are not focusable, so the engine steps over them without the
    // caller having to keep its own list of which rows can be landed on.
    case "Up": case "Down": filterIdx = menuStep(btn, filterIdx, rows.length); renderFilter(); break;
    case "A": case "Right": if (focusVisible()) filterActivate(); break;
    case "Y": resetFilters(); applyFilter(); toast("Filters and sort reset"); break;
    case "Left": case "B":
      if (filterLevel) { filterLevel = null; filterIdx = 0; renderFilter(); }
      else if (btn === "B") closeFilter();
      break;
    case "X": closeFilter(); break;
  }
}

/* ============================== game context menu (Y) ============================== */

let gameMenu = null;   // { gameId, from, idx }

function openGameMenu(gameId, from) {
  gameMenu = { gameId, from, idx: 0 };
  showOverlay("overlay-gamemenu");
  renderGameMenu();
}

function closeGameMenu() {
  gameMenu = null;
  hideOverlay("overlay-gamemenu");
}

function gameMenuItems() {
  if (!gameMenu) return [];
  const g = gameById(gameMenu.gameId);
  if (!g) return [];
  const running = S.gameRunning && S.runningGameId === g.id;
  // A fixed order, whatever the game: View game first, then what gets you playing (Resume,
  // Install, the other stores), then the rest. Art is changed from the detail page's Manage.
  const top = [
    { label: "View game", icon: "info", sub: "Full details page", action: () => { const f = gameMenu.from; closeGameMenu(); openDetail(g.id, f); } },
  ];
  if (running) top.push({ label: "Resume game", icon: "play", sub: "Back to the running game",
    action: () => { closeGameMenu(); send({ cmd: "resumeGame" }); } });
  // Install only while the game is on this PC nowhere. Once it is installed in one store, a second
  // copy from another is a deliberate choice, made from the game's page under Manage -- not one row
  // away from Play here. That holds for the tile's own copy too: Launch with can make an
  // uninstalled store the default while another store's copy is on disk.
  const editions = editionsOf(g);
  const installedSomewhere = editions.some(m => m.installed);
  if (!g.installed && !installedSomewhere && canInstall(g)) top.push({ label: "Install", icon: "download",
    sub: `Through ${storeName(g)}; the tile turns playable when it is done`,
    action: () => { closeGameMenu(); offerInstall(g); } });
  // The same game in the other stores it is in. Played from here once, not made the default --
  // that is what Manage > Launch with is for.
  editions.filter(m => m.id !== g.id).forEach(m => {
    if (m.installed) top.push({ label: `Play on ${m.platform}`, icon: "play",
      sub: "Just this once. Manage → Launch with changes the default",
      action: () => { closeGameMenu(); launchGame(m); } });
    else if (!installedSomewhere && canInstall(m)) top.push({ label: `Install on ${m.platform}`, icon: "download",
      sub: `Through ${storeName(m)}`,
      action: () => { closeGameMenu(); offerInstall(m); } });
  });
  const items = [
    ...top,
    { label: g.favorite ? "Remove from favorites" : "Add to favorites", icon: "star", action: () => send({ cmd: "toggleFavorite", id: g.id }) },
    { label: "Add to collection", icon: "folderPlus", action: () => { closeGameMenu(); openCollect(g.id); } },
  ];
  // Mods need a game on this disk for a manager to deploy into; a ROM's files are the emulator's
  // business and an uninstalled game has nowhere to put them.
  if (canHaveMods(g)) items.push({ label: "Mods", icon: "chip", sub: "Switch mods on and off, remove them, find more — through Vortex",
    action: () => { closeGameMenu(); openMods(g.id); } });
  // The achievements and the sessions, from the library too, so neither needs the page opened first.
  if (canHaveAchievements(g)) items.push({ label: "Achievements", icon: "trophy", sub: achievementMenuSub(g),
    action: () => { const f = gameMenu.from; closeGameMenu(); openAchievements(g.id, f); } });
  items.push({ label: "Stats", icon: "chart", sub: g.sessions > 0 || g.playtimeMinutes > 0 ? `${g.sessions || 0} session${g.sessions === 1 ? "" : "s"} · ${fmtPlaytime(g.playtimeMinutes)}` : "Nothing yet · log a session played elsewhere",
    action: () => { const f = gameMenu.from; closeGameMenu(); openActivity(g.id, f); } });
  items.push(
    // Hiding is done to the whole game: hiding only the Steam copy would just bring the Xbox one
    // out from behind it as a tile of its own.
    { label: g.hidden ? "Unhide" : "Hide", icon: g.hidden ? "eye" : "eyeOff",
      sub: g.hidden ? "Show in the library again" : "Not a game? Keep it out of the library",
      action: () => { send({ cmd: "setHidden", ids: editionsOf(g).map(m => m.id), hidden: !g.hidden }); closeGameMenu(); } },
  );
  // While nothing of the game has been seen yet, closing is simply ending the session: the host
  // finds nothing to close and clears it, so the game can be launched again or another started.
  if (running) items.push({ label: "Close game", icon: "x", danger: true,
    sub: S.gameStarting ? "Nothing of it is running yet; this clears it so you can launch again" : undefined,
    action: () => { closeGameMenu(); send({ cmd: "closeGame" }); } });
  // Deleting the entry for the game you are in the middle of playing is never what you meant,
  // so this one only shows while it is not running.
  if (g.manual && !running) items.push({
    label: "Remove from library", icon: "trash", danger: true,
    action: () => { send({ cmd: "removeGame", id: g.id }); closeGameMenu(); },
  });
  return items;
}

function renderGameMenu() {
  if (!gameMenu) return;
  const g = gameById(gameMenu.gameId);
  $("gameMenuTitle").textContent = (g ? g.title : "GAME").toUpperCase();
  const items = gameMenuItems();
  gameMenu.idx = Math.max(0, Math.min(gameMenu.idx, items.length - 1));
  renderMenu($("gameMenuList"), $("gameMenuFoot"), items, gameMenu.idx,
    foot(["A", "Select"], ["B", "Back"]),
    (i) => { if (gameMenu.idx !== i) { gameMenu.idx = i; renderGameMenu(); } },
    (i) => items[i].action());
}

function gameMenuInput(btn) {
  const items = gameMenuItems();
  switch (btn) {
    case "Up": case "Down": gameMenu.idx = menuStep(btn, gameMenu.idx, items.length); renderGameMenu(); break;
    case "A": if (focusVisible() && items[gameMenu.idx]) items[gameMenu.idx].action(); break;
    case "B": case "Y": closeGameMenu(); break;
  }
}

/* ============================== add-to-collection overlay ============================== */

let collectTarget = null;

function collectItems() {
  const g = gameById(collectTarget);
  if (!g) return [];
  const items = [{
    label: "Favorites", icon: "star", star: true, checked: g.favorite,
    action: () => { send({ cmd: "toggleFavorite", id: g.id }); },
  }, {
    label: "Hidden", icon: "eyeOff", checked: g.hidden,
    action: () => { send({ cmd: "toggleHidden", id: g.id }); },
  }];
  S.collections.forEach(c => items.push({
    label: c.name, icon: "folder", checked: c.gameIds.includes(g.id),
    action: () => send({ cmd: "toggleInCollection", collectionId: c.id, id: g.id }),
  }));
  items.push({
    label: "New collection…", icon: "folderPlus",
    action: () => {
      closeCollect();
      openInput("NEW COLLECTION NAME", "", name => send({ cmd: "createCollection", name, gameId: g.id }));
    },
  });
  return items;
}

function openCollect(gameId) {
  collectTarget = gameId || detailGameId;
  collectOpen = true; collectIdx = 0;
  showOverlay("overlay-collect");
  renderCollect();
}
function closeCollect() { collectOpen = false; hideOverlay("overlay-collect"); }

function renderCollect() {
  const items = collectItems();
  collectIdx = Math.max(0, Math.min(collectIdx, items.length - 1));
  renderMenu($("collectList"), $("collectFoot"), items, collectIdx,
    foot(["A", "Toggle"], ["B", "Back"]),
    (i) => { if (collectIdx !== i) { collectIdx = i; renderCollect(); } },
    (i) => items[i].action());
}

function collectInput(btn) {
  const items = collectItems();
  switch (btn) {
    case "Up": case "Down": collectIdx = menuStep(btn, collectIdx, items.length); renderCollect(); break;
    case "A": if (focusVisible() && items[collectIdx]) items[collectIdx].action(); break;
    case "B": closeCollect(); break;
  }
}

/* ============================== manage overlay ============================== */

/* How this game actually starts, in one line. */
function launchRoute(g) {
  if (g.emulated) { const e = emulatorFor(g); return e ? `Through ${e.name}` : "No emulator is set for it"; }
  if (g.preferDirectLaunch && g.exePath) return g.exePath;
  if (g.platform === "Steam") return "Through the Steam client";
  if (g.platform === "Epic") return "Through the Epic Games launcher";
  if (g.platform === "Xbox") return "Through the Xbox app";
  return g.exePath || "Directly from its executable";
}

function manageItems() {
  const g = gameById(detailGameId);
  if (!g) return [];
  const items = [];
  // Only for a game in more than one store. Picking one makes it what the tile launches, over
  // the default of "whichever is installed, Steam first".
  const editions = editionsOf(g);
  if (editions.length > 1) {
    items.push({ cat: "LAUNCH WITH" });
    // Listed in the fixed store order, not in rank order. Ranked, the chosen store jumped to the
    // top the moment it was chosen, and the row under the highlight changed out from beneath it.
    const byStore = (a, b) => EDITION_ORDER.indexOf(a.platform) - EDITION_ORDER.indexOf(b.platform);
    [...editions].sort(byStore).forEach(m => items.push({
      label: m.platform, icon: platformIcon(m), radio: true,
      checked: m.id === g.id,
      action: () => {
        if (m.id === g.id) return;
        preferEdition(m);
        detailGameId = m.id;
        renderDetail();
        renderManage();
        renderLibrary();
        toast(`${g.title} now launches with ${m.platform}${m.installed ? "" : " (not installed yet)"}`);
      },
    }));
    // A copy from another store, on purpose: the game menu (Y) stops offering this once the game
    // is installed anywhere, so this is where a second copy is got.
    const installable = editions.filter(m => m.id !== g.id && !m.installed && canInstall(m));
    if (installable.length) {
      items.push({ cat: "INSTALL FROM ANOTHER STORE" });
      installable.sort(byStore).forEach(m => items.push({
        label: `Install on ${m.platform}`, icon: "download", sub: `Through ${storeName(m)}`,
        action: () => { closeManage(); offerInstall(m); },
      }));
    }
    items.push({ cat: "THIS COPY" });
  }
  if (g.emulated) {
    // A ROM has no executable of its own to change; what it has is an emulator, a title that
    // was guessed from its file name, and a command line that is the emulator's template
    // unless overridden here. "Remove" is absent on purpose: the file would be found again on
    // the next scan. Hide is the way to keep one out.
    const emu = emulatorFor(g);
    const folder = romFolderById(g.romFolderId);
    const template = g.args || (folder && folder.args) || (emu && emu.args) || '"{rom}"';
    items.push({
      label: "Rename", icon: "edit",
      sub: "The name is read off the file. Fixing it is how a wrongly matched game fetches the right details",
      action: () => {
        closeManage();
        openInput("GAME TITLE", g.title, v => { if (v && v !== g.title) send({ cmd: "setTitle", id: g.id, title: v }); });
      },
    });
    items.push({
      label: "Run with", icon: "cartridge", sub: launchRoute(g),
      action: () => { closeManage(); openEmulatorChoice(g); },
    });
    items.push({
      label: "Set launch arguments", icon: "terminal",
      sub: g.args ? g.args : `Uses ${emu ? emu.name + "'s" : "the emulator's"} own: ${template}`,
      action: () => {
        closeManage();
        openInput("LAUNCH ARGUMENTS ({rom} is the file)", g.args || template, v => send({ cmd: "setArgs", id: g.id, args: v === template ? "" : v }));
      },
    });
  } else {
    items.push({
      label: "Set launch arguments", icon: "terminal", sub: g.args || "e.g. --launcher-skip",
      action: () => {
        closeManage();
        openInput("LAUNCH ARGUMENTS", g.args || "", v => send({ cmd: "setArgs", id: g.id, args: v }));
      },
    });
    items.push({
      label: "Change executable", icon: "file",
      // The subtitle carries the launch route, which used to sit under the title on the detail
      // page. It is troubleshooting detail: it belongs on the screen you open to change it.
      sub: launchRoute(g),
      action: () => { send({ cmd: "pickExe", id: g.id }); closeManage(); },
    });
    if (g.preferDirectLaunch && (g.platform === "Steam" || g.platform === "Epic"))
      items.push({ label: `Launch through ${g.platform} again`, icon: "store", action: () => { send({ cmd: "launchViaStore", id: g.id }); closeManage(); } });
    if (canHaveMods(g)) items.push({
      label: "Mods", icon: "chip", sub: "Switch mods on and off, remove them, find more — through Vortex",
      action: () => { closeManage(); openMods(g.id); },
    });
  }
  // Two pictures, two entries. They are different shapes and they appear in different places, so
  // one "change artwork" that set both would put whichever file was chosen into a slot it is the
  // wrong shape for. The subtitles say where each one shows up, because "cover" and "tile" are
  // our words for them and nobody else's.
  items.push({
    label: "Change tile art", icon: "image", sub: "Library tiles · 1.75:1",
    action: () => { send({ cmd: "pickCover", id: g.id, slot: "tile" }); closeManage(); },
  });
  items.push({
    label: "Change cover art", icon: "image", sub: "Portrait box art · 2:3",
    action: () => { send({ cmd: "pickCover", id: g.id, slot: "cover" }); closeManage(); },
  });
  // Only once there is something to undo. A picked file outranks every source for good -- see
  // Assign -- so without this there is no way back to the fetched art short of editing the JSON.
  if (isCustomArt(g.bannerFile)) items.push({
    label: "Use the fetched tile art again", icon: "refresh",
    action: () => { send({ cmd: "resetArt", id: g.id, slot: "tile" }); closeManage(); },
  });
  if (isCustomArt(g.coverFile)) items.push({
    label: "Use the fetched cover again", icon: "refresh",
    action: () => { send({ cmd: "resetArt", id: g.id, slot: "cover" }); closeManage(); },
  });
  if (g.manual) items.push({
    label: "Remove from library", icon: "trash", danger: true,
    action: () => { send({ cmd: "removeGame", id: g.id }); closeManage(); switchView(detailReturn); },
  });
  return items;
}

function openManage() {
  manageOpen = true; manageIdx = 0;
  showOverlay("overlay-manage");
  renderManage();
}
function closeManage() { manageOpen = false; hideOverlay("overlay-manage"); }

function renderManage() {
  const items = manageItems();
  manageIdx = Math.max(0, Math.min(manageIdx, items.length - 1));
  // A section heading cannot take the highlight; start on the row under it.
  while (items[manageIdx] && items[manageIdx].cat && manageIdx < items.length - 1) manageIdx++;
  renderMenu($("manageList"), $("manageFoot"), items, manageIdx,
    foot(["A", "Select"], ["B", "Back"]),
    (i) => { if (manageIdx !== i) { manageIdx = i; renderManage(); } },
    (i) => items[i].action());
}

function manageInput(btn) {
  const items = manageItems();
  switch (btn) {
    case "Up": case "Down": manageIdx = menuStep(btn, manageIdx, items.length); renderManage(); break;
    case "A": if (focusVisible() && items[manageIdx]) items[manageIdx].action(); break;
    case "B": closeManage(); break;
  }
}

/* ============================== mods overlay ==============================
 *
 * One game's mods, through Vortex. The host does the talking (see the mods* commands in
 * UiBridge) and answers with one "mods" message that carries the whole screen: a state, the
 * sentence that explains it, and the list. This side only draws what it is handed and asks for
 * the next thing. Every state that is not "ready" is a short note above a couple of rows that
 * say what to do about it -- install Vortex, restart it, set the game up in it -- because the
 * one thing a list across the room must never be is silently empty.
 */
let modsState = null;   // { gameId, idx, view: the last "mods" message or null, busy: a sentence or null }

function canHaveMods(g) { return !!g && g.installed && !g.emulated; }

function openMods(gameId) {
  modsState = { gameId, idx: 0, view: null, busy: "Talking to Vortex…" };
  showOverlay("overlay-mods");
  renderMods();
  send({ cmd: "modsOpen", id: gameId });
}

function closeMods() { modsState = null; hideOverlay("overlay-mods"); }

/* Ask the host for something and say so on screen until its answer replaces the list. */
function modsAsk(cmd, extra, busy) {
  if (!modsState) return;
  modsState.busy = busy || null;
  renderMods();
  send({ cmd, id: modsState.gameId, ...(extra || {}) });
}

const MODS_BROWSE_SUB = "nexusmods.com opens over the launcher. “Mod manager download” on a mod sends it straight to Vortex";
const MODS_OPEN_SUB = "Vortex comes to the TV. Bring the launcher back the way you would after a game";

/* A question Vortex is showing, as a row whose A opens its buttons as a list. "Install this
   anyway?" from the fallback installer is the one nearly every unusual archive raises, and it
   used to mean walking to the desk; now it is two presses. A dialog that wants more than a
   button -- a folder, a checkbox -- only offers Vortex's window. */
function promptRows(v) {
  const rows = [];
  (v.prompts || []).forEach(p => {
    const title = p.title || "Vortex is asking something";
    rows.push({
      label: `Vortex asks: ${title}`, icon: "info", prompt: p,
      sub: p.message ? p.message : (p.answerable ? "Choose an answer" : "Needs Vortex's own window"),
      action: () => {
        if (!p.answerable) { send({ cmd: "modsShowVortex" }); return; }
        const gameId = modsState.gameId;
        openChoice(title, (p.actions || []).map(a => ({
          label: a, icon: a === p.defaultAction ? "checkCircle" : "circle", checked: a === p.defaultAction ? true : undefined,
          action: () => modsAsk("modsAnswer", { dialogId: p.id, action: a }, "Answering Vortex…"),
        })).concat([{ label: "Open Vortex instead", icon: "tornado", action: () => send({ cmd: "modsShowVortex" }) }]),
        { onBack: () => { if (modsState && modsState.gameId === gameId) renderMods(); } });
      },
    });
  });
  (v.notices || []).forEach(n => rows.push({ label: "Vortex says", icon: "info", sub: n, action: () => send({ cmd: "modsShowVortex" }) }));
  return rows;
}

function modsItems() {
  if (!modsState || !modsState.view) return [];
  const v = modsState.view;
  const gameId = modsState.gameId;
  const items = promptRows(v);
  const again = (label) => ({ label: label || "Check again", icon: "refresh", action: () => modsAsk("modsOpen", {}, "Talking to Vortex…") });
  const openVortex = (sub) => ({ label: "Open Vortex", icon: "tornado", sub: sub || MODS_OPEN_SUB, action: () => send({ cmd: "modsShowVortex" }) });
  // What the state calls for comes first. The standing rows -- Vortex's own window, a fresh
  // look -- sit together under MORE on every screen, so no row is ever a section of one.
  const more = [];
  switch (v.state) {
    case "notInstalled":
      items.push({ label: "Open the download page", icon: "download",
        sub: "In your browser, on the desktop. Come back to the launcher the way you would after a game",
        action: () => send({ cmd: "modsGetVortex" }) });
      more.push(again("I've installed it — check again"));
      break;
    case "needsRestart":
      items.push({ label: "Restart Vortex", icon: "refresh", sub: "Closes it and opens it again so the Loungepad bridge loads. Nothing is lost",
        action: () => modsAsk("modsRestartVortex", {}, "Restarting Vortex…") });
      more.push(openVortex(), again("Try again"));
      break;
    // Setting up is done from here: Vortex gets its first profile for the game and switches to
    // it, and whatever it asks on the way -- deployment method, staging folder -- lands at the
    // top of this list as a question with its buttons. Its own window is a last resort.
    case "notManaged":
      items.push({ label: "Set it up in Vortex", icon: "tornado",
        sub: "Vortex makes a profile for the game and switches to it. Anything it asks appears here",
        action: () => modsAsk("modsManage", {}, "Setting the game up in Vortex…") });
      more.push(openVortex(), again());
      break;
    // Vortex has the game's extension but has never been told where the game is. The launcher
    // knows, so the same row does both: the folder first, then the profile and the switch.
    case "notDiscovered":
      items.push({ label: "Set it up in Vortex", icon: "tornado",
        sub: "Loungepad gives Vortex the game's folder, then Vortex makes a profile and switches to it. Anything it asks appears here",
        action: () => modsAsk("modsManage", {}, "Setting the game up in Vortex…") });
      more.push(openVortex(), again());
      break;
    case "unsupported": {
      // Vortex learns a game through an extension. Its catalogue's candidates are offered by
      // name -- the exact match first -- and installing one is a click in Vortex's own extension
      // browser, which opens on the TV on that extension; or the extension's page on Nexus Mods,
      // whose "Mod manager download" installs it the same way a mod is.
      const exts = v.extensions || [];
      if (exts.length) items.push({ cat: "EXTENSIONS FOR IT" });
      exts.forEach(e => items.push({
        label: `Install “${e.name}”`, icon: "download",
        sub: (e.exact ? "Vortex's extension for this game" : `For ${e.gameName || "another game"}`)
          + (e.author ? ` · by ${e.author}` : "") + (e.version ? ` · v${e.version}` : "")
          + ". Vortex opens on it: choose Install there, then restart Vortex",
        action: () => send({ cmd: "modsExtension", id: gameId, modId: e.modId }),
      }));
      items.push({ label: exts.length ? "Get it from Nexus Mods instead" : "Look for an extension on Nexus Mods", icon: "search",
        sub: (exts.length ? "The first extension above, on the site. " : "The site's Vortex extensions. ")
          + "Mod manager download installs one into Vortex",
        action: () => send({ cmd: "modsBrowse", id: gameId, extensionModId: exts.length ? exts[0].modId : null, site: !exts.length }) });
      more.push(openVortex("Look under Games there; Vortex may need to scan again, or to be pointed at the folder"), again());
      break;
    }
    case "ready": {
      const mods = v.mods || [];
      const on = mods.filter(m => m.enabled).length;
      if (mods.length) items.push({ cat: `${mods.length} MOD${mods.length === 1 ? "" : "S"} · ${on} ENABLED` });
      mods.forEach(m => {
        const installed = m.state === "installed";
        const bits = [m.version ? "v" + m.version : null, m.author || null,
          m.state === "downloaded" ? "downloaded, not installed yet" : m.state === "installing" ? "installing…" : null].filter(Boolean);
        items.push({
          mod: m, label: m.name, icon: "chip", checked: installed && m.enabled, sub: bits.join(" · ") || undefined,
          action: () => {
            if (!installed) { toast("Vortex has not finished installing it. Open Vortex to see why"); return; }
            modsAsk("modsToggle", { modId: m.id, enabled: !m.enabled }, `${m.enabled ? "Disabling" : "Enabling"} ${m.name}…`);
          },
        });
      });
      more.push({ label: "Find mods on Nexus Mods", icon: "search", sub: MODS_BROWSE_SUB,
        action: () => send({ cmd: "modsBrowse", id: gameId }) });
      more.push(openVortex("For load order, conflicts and everything else this list does not do"));
      more.push(again("Refresh the list"));
      break;
    }
    default:   // error, unavailable
      more.push(again("Try again"), openVortex());
      break;
  }
  if (more.length) {
    items.push({ cat: v.state === "ready" && !(v.mods || []).length ? "GET STARTED" : "MORE" });
    items.push(...more);
  }
  return items;
}

/* The row's own page on Nexus Mods, when the mod came from there; the game's section otherwise. */
function modsBrowseCurrent(items) {
  const cur = items[modsState.idx];
  const m = cur && cur.mod;
  if (m && m.nexusModId) send({ cmd: "modsBrowse", id: modsState.gameId, nexusModId: m.nexusModId, nexusDomain: m.nexusDomain || null });
  else send({ cmd: "modsBrowse", id: modsState.gameId });
}

/* The note above the list: the state in words, and for a missing Vortex the whole story. */
function modsNoteHtml() {
  if (!modsState) return "";
  if (modsState.busy) return `<div class="mods-busy"><span class="mods-spinner"></span>${esc(modsState.busy)}</div>`;
  const v = modsState.view;
  if (!v) return "";
  const msg = v.message ? esc(v.message) : "";
  switch (v.state) {
    case "notInstalled":
      return `<div><b>Vortex isn't installed.</b> Vortex is Nexus Mods' free mod manager. Loungepad drives it to download mods, install them, and switch them on and off for over 250 games. To set it up:</div>
        <ol class="mods-steps">
          <li>Download the installer from <span class="mods-url">nexusmods.com/site/mods/1</span>, on its <b>Files</b> tab. It is free. A Nexus Mods account is free too, and is only needed to download mods.</li>
          <li>Run the installer. It installs for your own Windows account, needs no administrator, and opens Vortex when it is done.</li>
          <li>In Vortex, sign in to Nexus Mods (the button at the top right). Then come back here and choose <b>check again</b>.</li>
        </ol>`;
    case "needsRestart":
      return `<div class="mods-warn">${msg}</div><div>Vortex only loads its extensions when it starts, and the Loungepad bridge was just added to it.</div>`;
    case "notManaged":
    case "notDiscovered":
    case "unsupported":
      return `<div>${msg}</div>`;
    case "ready": {
      const parts = [];
      if (msg) parts.push(`<div class="mods-warn">${msg}</div>`);
      if (!(v.mods || []).length) parts.push(`<div>Vortex has no mods for this game yet. Find some on Nexus Mods below; each one you send it appears here.</div>`);
      return parts.join("");
    }
    default:
      return `<div class="mods-warn">${msg || "Vortex could not be reached"}</div>`;
  }
}

function renderMods() {
  if (!modsState) return;
  const g = gameById(modsState.gameId);
  $("modsTitle").textContent = `${g ? g.title : "GAME"} · MODS`.toUpperCase();
  const note = $("modsNote");
  const html = modsNoteHtml();
  note.hidden = !html;
  note.innerHTML = html;
  const items = modsItems();
  const focusable = items.map((r, i) => r.cat ? -1 : i).filter(i => i >= 0);
  if (!focusable.includes(modsState.idx)) modsState.idx = focusable[0] ?? 0;
  const ready = modsState.view && modsState.view.state === "ready";
  const onMod = ready && items[modsState.idx] && items[modsState.idx].mod;
  const onNexusMod = onMod && !!items[modsState.idx].mod.nexusModId;
  $("overlay-mods").classList.toggle("busy", !!modsState.busy);
  renderMenu($("modsList"), $("modsFoot"), items, modsState.idx,
    ready ? foot(["A", onMod ? "Toggle" : "Select"], ["X", "Remove"], ["Y", onNexusMod ? "Mod page" : "Nexus Mods"], ["B", "Back"]) : foot(["A", "Select"], ["B", "Back"]),
    (i) => { if (modsState && modsState.idx !== i) { modsState.idx = i; renderMods(); } },
    (i) => { if (!modsState || modsState.busy) return; const it = items[i]; if (it && it.action) { modsState.idx = i; it.action(); } });
}

function askRemoveMod(m) {
  confirmState = {
    title: `Remove ${m.name}?`,
    body: "Vortex takes it out of the game and deletes its installed files. The downloaded archive stays in Vortex, so it can be put back from there.",
    yesLabel: "Remove", icon: "trash", danger: true,
    onYes: () => modsAsk("modsRemove", { modId: m.id }, `Removing ${m.name}…`),
  };
  confirmIdx = 0;
  showOverlay("overlay-confirm");
  renderConfirm();
}

function modsInput(btn) {
  const items = modsItems();
  const cur = items[modsState.idx];
  switch (btn) {
    case "Up": case "Down": modsState.idx = menuStep(btn, modsState.idx, items.length); renderMods(); break;
    case "A": if (focusVisible() && cur && cur.action && !modsState.busy) cur.action(); break;
    case "X": if (cur && cur.mod && !modsState.busy) askRemoveMod(cur.mod); break;
    case "Y": if (modsState.view && modsState.view.state === "ready" && !modsState.busy) modsBrowseCurrent(items); break;
    case "B": closeMods(); break;
  }
}

/* The host's answer. Only for the game on screen: a slow answer for one that was closed, or for
   the game before this one, is dropped rather than drawn over the wrong title. */
function onModsMessage(m) {
  if (!modsState || modsState.gameId !== m.gameId) return;
  modsState.view = m;
  modsState.busy = null;
  renderMods();
}

/* ============================== choice overlay ==============================
 *
 * A pick-one list for whatever needs one and has no menu of its own: which system a ROM folder
 * is for, which emulator runs a game, what to do with a folder. The same card and rows as every
 * other menu. Choosing an item closes the list and then runs the item, so an item that opens
 * another list simply does -- which is how the two-step folder wizard is built out of it.
 */
let choiceState = null;   // { title, items, idx, onBack }

function openChoice(title, items, opts = {}) {
  const idx = Math.max(0, items.findIndex(i => !i.cat && i.checked));
  choiceState = { title, items, idx, onBack: opts.onBack || null };
  showOverlay("overlay-choice");
  // A fresh list opens at its own ticked row, already in place: renderMenu keeps the scroll of
  // whatever list was here before, and a Settings value can be forty rows down (a slider's
  // steps), which would otherwise glide in from wherever the last list was left.
  const list = $("choiceList");
  stopScroll(list, "y");
  list.scrollTop = 0;
  renderChoice();
  const cur = list.querySelector(".ov-row.focused") || list.querySelector(`[data-row-index="${idx}"]`);
  if (cur) {
    stopScroll(list, "y");
    list.scrollTop = Math.max(0, cur.offsetTop - (list.clientHeight - cur.offsetHeight) / 2);
    markOverflow(list);
  }
}

function closeChoice() { choiceState = null; hideOverlay("overlay-choice"); }

function renderChoice() {
  if (!choiceState) return;
  const { title, items } = choiceState;
  const focusable = items.map((r, i) => r.cat ? -1 : i).filter(i => i >= 0);
  if (!focusable.includes(choiceState.idx)) choiceState.idx = focusable[0] ?? 0;
  $("choiceTitle").textContent = title.toUpperCase();
  renderMenu($("choiceList"), $("choiceFoot"), items, choiceState.idx,
    foot(["A", "Choose"], ["B", choiceState.onBack ? "Back" : "Cancel"]),
    (i) => { if (choiceState && choiceState.idx !== i) { choiceState.idx = i; renderChoice(); } },
    (i) => { if (choiceState) { choiceState.idx = i; choiceActivate(); } });
}

function choiceActivate() {
  const item = choiceState && choiceState.items[choiceState.idx];
  if (!item || item.cat || !item.action) return;
  closeChoice();
  item.action();
}

function choiceInput(btn) {
  switch (btn) {
    case "Up": case "Down":
      choiceState.idx = menuStep(btn, choiceState.idx, choiceState.items.length);
      renderChoice();
      break;
    case "A": if (focusVisible()) choiceActivate(); break;
    case "B": { const back = choiceState.onBack; closeChoice(); if (back) back(); break; }
  }
}

/* ============================== emulators and ROM folders ==============================
 *
 * The host owns the lists (see the emu* commands in UiBridge); the page only asks. Adding a
 * folder is a three-step thing -- the host's folder dialog, then "which system", then "which
 * emulator" -- and the last two are lists here, where a gamepad can answer them. The folder's
 * name seeds the system, so the usual answer is A, A.
 */
let romWizard = null;        // { path, platformId } while a folder is being added
let pendingEmuPick = null;   // called with the new emulator's id after "Add an emulator…"

function coreName(path) { return String(path || "").split(/[\\/]/).pop().replace(/_libretro\.dll$/i, ""); }
function usesCore(emu) { return !!emu && /\{core\}/i.test(emu.args || ""); }

function startRomFolderWizard(path, guess) {
  romWizard = { path, platformId: guess || null };
  const name = path.split(/[\\/]/).filter(Boolean).pop() || path;
  openPlatformChoice(guess, id => { romWizard.platformId = id; wizardPickEmulator(); }, null,
    `Which system is ${name}?`);
}

function wizardPickEmulator() {
  const w = romWizard;
  openEmulatorPick(w.platformId, null, id => {
    send({ cmd: "romFolderAdd", path: w.path, platformId: w.platformId, emulatorId: id || "" });
    romWizard = null;
  }, () => startRomFolderWizard(w.path, w.platformId), { allowNone: true });
}

/* The catalogue, with the current or guessed system first so the likely answer is under the
   highlight. The extensions ride along as the subtitle: they are what "which system" means to
   the scan, and the quickest way to tell Sega CD (.cue, .chd) from Genesis (.md, .bin). */
function openPlatformChoice(currentId, onPick, onBack, title) {
  const list = S.emulation && S.emulation.platforms || [];
  const items = list.map(p => ({
    label: p.name, icon: "cartridge", radio: true, checked: p.id === currentId,
    sub: (p.extensions || []).slice(0, 6).map(e => "." + e).join("  "),
    action: () => onPick(p.id),
  }));
  const i = items.findIndex(x => x.checked);
  if (i > 0) items.unshift(items.splice(i, 1)[0]);
  openChoice(title || "Which system?", items, { onBack });
}

/* The emulators set up, the ones known to run this system first. "Add an emulator…" opens the
   host's file dialog and comes back through the emuAdded message with the new id. */
function openEmulatorPick(platformId, currentId, onPick, onBack, opts = {}) {
  const emus = [...(S.emulation && S.emulation.emulators || [])];
  const fits = e => (e.platforms || []).includes(platformId);
  emus.sort((a, b) => (fits(b) ? 1 : 0) - (fits(a) ? 1 : 0) || a.name.localeCompare(b.name));
  const items = emus.map(e => ({
    label: e.name, icon: "gamepad", radio: true, checked: e.id === currentId,
    sub: fits(e) ? "Runs this system" : e.exePath.split(/[\\/]/).pop(),
    action: () => onPick(e.id),
  }));
  items.push({
    label: "Add an emulator…", icon: "folderPlus", sub: "Point to its .exe. The common ones set themselves up",
    action: () => { pendingEmuPick = onPick; send({ cmd: "emuAdd" }); },
  });
  if (opts.allowNone) items.push({
    label: "Decide later", icon: "clock", sub: "The games are listed now and get an emulator when the folder does",
    action: () => onPick(""),
  });
  openChoice(opts.title || "Which emulator runs it?", items, { onBack });
}

/* Per game, from Manage: its own emulator, or back to its folder's. */
function openEmulatorChoice(g) {
  const folder = romFolderById(g.romFolderId);
  const folderEmu = folder ? emulatorById(folder.emulatorId) : null;
  const emus = S.emulation && S.emulation.emulators || [];
  const items = [{
    label: folderEmu ? `${folderEmu.name} — the folder's choice` : "The folder's emulator (none set yet)",
    icon: "romFolder", radio: true, checked: !g.emulatorId,
    action: () => send({ cmd: "setEmulator", id: g.id, emulatorId: "" }),
  }];
  emus.forEach(e => items.push({
    label: e.name, icon: "gamepad", radio: true, checked: g.emulatorId === e.id,
    sub: (e.platforms || []).includes(g.platformId) ? "Runs this system" : undefined,
    action: () => send({ cmd: "setEmulator", id: g.id, emulatorId: e.id }),
  }));
  openChoice(`Run ${g.title} with`, items);
}

function openRomFolderOptions(f) {
  const p = platformDef(f.platformId);
  const emu = emulatorById(f.emulatorId);
  const exts = f.extensions && f.extensions.length ? f.extensions : (p ? p.extensions : []);
  const again = () => openRomFolderOptions(romFolderById(f.id) || f);
  const items = [
    { label: "System", icon: "cartridge", sub: p ? p.name : f.platformId,
      action: () => openPlatformChoice(f.platformId, id => send({ cmd: "romFolderUpdate", id: f.id, platformId: id }), again) },
    { label: "Emulator", icon: "gamepad", sub: emu ? emu.name : "None set — the games cannot start until one is",
      action: () => openEmulatorPick(f.platformId, f.emulatorId, id => send({ cmd: "romFolderUpdate", id: f.id, emulatorId: id }), again) },
  ];
  if (usesCore(emu)) items.push({
    label: "Core", icon: "chip", sub: f.core ? coreName(f.core) : "Not chosen — pick one before playing",
    action: () => send({ cmd: "romFolderPickCore", id: f.id }),
  });
  items.push({
    label: "Launch arguments for this folder", icon: "terminal",
    sub: f.args || `Uses ${emu ? emu.name + "'s own" : "the emulator's own"}`,
    action: () => openInput("FOLDER LAUNCH ARGUMENTS", f.args || (emu ? emu.args : ""),
      v => send({ cmd: "romFolderUpdate", id: f.id, args: emu && v === emu.args ? "" : v })),
  });
  // A playlist lists its files by name; there are no extensions to choose.
  if (!f.playlist) items.push({
    label: "File types", icon: "file", sub: exts.map(e => "." + e).join("  "),
    action: () => openInput("FILE TYPES, COMMA SEPARATED", exts.join(", "),
      v => send({ cmd: "romFolderUpdate", id: f.id, extensions: v })),
  });
  items.push({
    label: f.playlist ? "Remove this playlist" : "Remove this folder", icon: "trash", danger: true,
    action: () => {
      const n = S.games.filter(g => g.romFolderId === f.id).length;
      confirmState = {
        title: `Remove ${p ? p.name : "this folder"}?`,
        body: `${f.path} leaves the library${n ? ` with its ${n} game${n === 1 ? "" : "s"}` : ""}. Nothing on disk is touched, and it will not be found again by itself.`,
        yesLabel: "Remove", icon: "trash", danger: true,
        onYes: () => send({ cmd: "romFolderRemove", id: f.id }),
      };
      confirmIdx = 0;
      showOverlay("overlay-confirm");
      renderConfirm();
    },
  });
  openChoice(p ? p.name : "ROM folder", items);
}

function openEmulatorOptions(e) {
  const users = (S.emulation && S.emulation.romFolders || []).filter(f => f.emulatorId === e.id);
  const items = [
    { label: "Rename", icon: "edit", sub: e.name,
      action: () => openInput("EMULATOR NAME", e.name, v => { if (v) send({ cmd: "emuUpdate", id: e.id, name: v }); }) },
    { label: "Change program", icon: "file", sub: e.exePath, action: () => send({ cmd: "emuPickExe", id: e.id }) },
    { label: "Launch arguments", icon: "terminal", sub: e.args || '"{rom}"',
      action: () => openInput("LAUNCH ARGUMENTS ({rom} is the file)", e.args || "", v => send({ cmd: "emuUpdate", id: e.id, args: v })) },
    { label: "Remove", icon: "trash", danger: true,
      action: () => {
        confirmState = {
          title: `Remove ${e.name}?`,
          body: users.length
            ? `${users.length} ROM folder${users.length === 1 ? "" : "s"} use${users.length === 1 ? "s" : ""} it and will need another emulator. The program itself is not touched.`
            : "The program itself is not touched.",
          yesLabel: "Remove", icon: "trash", danger: true,
          onYes: () => send({ cmd: "emuRemove", id: e.id }),
        };
        confirmIdx = 0;
        showOverlay("overlay-confirm");
        renderConfirm();
      } },
  ];
  openChoice(e.name, items);
}

/* ============================== confirm overlay ============================== */

/*
 * A dialog, not a menu. It used to be a one-row list -- a single highlighted option that could
 * not be moved off, with the explanation squeezed into that row's subtitle -- which read as a
 * menu with something missing. Now it is what a console asks you with: a heading, the details,
 * and the two buttons that answer it, A to go ahead and B to back out. There is nothing to
 * navigate, so nothing is highlighted.
 *
 * Defaults suit the destructive cases, which is most of them; an install or a game swap passes
 * its own icon and clears `danger`, since starting something is not a red action.
 */
function renderConfirm() {
  if (!confirmState) return;
  const danger = confirmState.danger !== false;
  $("confirmDialog").classList.toggle("danger", danger);
  $("confirmIcon").innerHTML = iconSvg(confirmState.icon || "trash");
  $("confirmTitle").textContent = confirmState.title;
  $("confirmBody").textContent = confirmState.body || "";
  $("confirmBody").hidden = !confirmState.body;
  $("confirmYesLabel").textContent = confirmState.yesLabel || "Delete";
}

function confirmChoose(yes) {
  const st = confirmState;
  confirmState = null;
  hideOverlay("overlay-confirm");
  if (yes && st) st.onYes();
}

/* A answers regardless of where the pointer is: the dialog is the only thing on screen that can
   take an answer, so there is no "A over empty space" to guard against. */
function confirmInput(btn) {
  switch (btn) {
    case "A": confirmChoose(true); break;
    case "B": confirmChoose(false); break;
  }
}

$("confirmYes").addEventListener("click", () => confirmChoose(true));
$("confirmNo").addEventListener("click", () => confirmChoose(false));
// A click on the dimmed screen around the dialog is a no, the way it is everywhere else.
$("overlay-confirm").addEventListener("click", (e) => { if (e.target.id === "overlay-confirm") confirmChoose(false); });

/* ============================== text input overlay ============================== */

function openInput(title, value, onConfirm, opts = {}) {
  inputOpen = true;
  inputConfirm = onConfirm;
  $("inputTitle").textContent = title;
  const field = $("inputField");
  field.value = value;
  // A line under the field for the one prompt that needs explaining: a key that is set and is
  // not shown (see secretRow). Hidden for every other prompt.
  const note = $("inputNote");
  if (note) { note.textContent = opts.note || ""; note.hidden = !opts.note; }
  showOverlay("overlay-input");
  send({ cmd: "focusPage" });   // see openSearch: no keystrokes reach the page without it
  setTimeout(() => { field.focus(); field.select(); }, 50);
  setTimeout(() => { if (document.activeElement !== field) { field.focus(); field.select(); } }, 250);
  send({ cmd: "showKeyboard" });
}

function closeInput(confirmed) {
  const cb = inputConfirm;
  const value = $("inputField").value.trim();
  inputOpen = false;
  inputConfirm = null;
  hideOverlay("overlay-input");
  $("inputField").blur();
  send({ cmd: "hideKeyboard" });
  if (confirmed && cb) cb(value);
}

$("inputField").addEventListener("keydown", (e) => {
  e.stopPropagation();
  if (e.key === "Enter") closeInput(true);
  if (e.key === "Escape") closeInput(false);
});

/*
 * The keyboard toggle can be bound to View, and in Press mode the host takes the toggle button
 * outright -- the page never saw the press, so on the library View raised the keyboard and search
 * never opened. The page now says when it wants View itself: on the library, with nothing on top
 * of it and no search already open. Everywhere else the toggle keeps the button. Search raises the
 * keyboard anyway, so nothing is lost.
 *
 * Published on change, from the input path and on a short timer as well, because overlays open
 * from the mouse and the host too, not only from the pad.
 */
let claimedButtons = "";
function publishClaims() {
  const overlayUp = overlayOpen() || radialOpen || !!radialSub || actionWheelOpen || ingameOpen
    || document.body.classList.contains("overlay-mode");
  // View for search and LB for the Stats screen, both on the library only.
  const want = view === "library" && !overlayUp && !searchOpen ? "View,LB" : "";
  if (want === claimedButtons) return;
  claimedButtons = want;
  send({ cmd: "claimButtons", buttons: want ? want.split(",") : [] });
}
setInterval(publishClaims, 400);

/* ============================== library search ==============================
 *
 * A field in the library top bar, so it is there in every theme -- both slot the top bar, and
 * the Loungepad theme hides the section headings. View opens it and raises the on-screen keyboard; every
 * keystroke refilters the grid live. Enter, A or Down keeps the search and drops the highlight on
 * the first result; Escape or B while typing clears it. With a search standing, the field shows
 * it and the grid heading says so, and View opens it again to change it. B on the library clears
 * it too, once the highlight is back at the top (see libraryInput).
 *
 * Matching is by folded title (see titleKey), across every store a game is in, and it is a
 * substring match on words -- "hollow" finds Hollow Knight and Hollow Knight: Silksong.
 */
let searchOpen = false;
let searchReturnKey = null;   // where the highlight was before View, for B to put it back

function searchKey(text) { return titleKey(text); }

function setSearch(value) {
  F.search = String(value || "").trim();
  const field = document.getElementById("searchField");
  if (field && field.value !== value) field.value = value || "";
  const box = document.getElementById("libSearch");
  if (box) box.classList.toggle("has-query", !!F.search);
}

function openSearch() {
  if (view !== "library") switchView("library");
  const scope = document.getElementById("screen-library");
  const box = $("libSearch");
  if (!searchOpen) {
    const was = scope.dataset.focusCurrent || null;
    searchReturnKey = was === "search" ? null : was;
  }
  searchOpen = true;
  box.classList.add("active");
  // The highlight moves up to the field, so what is being driven is what is lit.
  setInputMode("pad");
  setFocusEl(box);
  paintNav();
  revealSearchResults();
  const field = $("searchField");
  field.value = F.search;
  // The page only receives keystrokes once the HOST has put keyboard focus into the WebView --
  // the launcher is driven by the pad and nothing in the page is normally focused, so a DOM
  // focus() alone gives a field with no caret that the on-screen keyboard types into nothing.
  send({ cmd: "focusPage" });
  const place = () => { field.focus(); field.setSelectionRange(field.value.length, field.value.length); };
  place();
  setTimeout(place, 80);
  setTimeout(place, 250);
  send({ cmd: "showKeyboard" });
}

/* A search is about the grid, so opening one brings the grid up when it is under the fold: at the
   top of the Loungepad theme's page it is a peek below the recents, and the results would be
   typed into a sliver. It goes where the jump down lands (gridJumpStop), so taking the first
   result afterwards moves nothing; the theme keeps the page tall enough while searching. Shelf's
   grid starts on screen, and a page already scrolled down into the grid is left where it is; a
   grid that scrolls on its own needs nothing. */
function revealSearchResults() {
  const sc = libraryScroller(), grid = $("gridScroll");
  if (!sc || sc === grid) return;
  const first = grid.querySelector(FOCUSABLE_SEL) || grid;
  const below = offsetWithin(first, sc, "y") + first.offsetHeight + REVEAL_MARGIN;
  if (below <= scrollTarget(sc, "y") + sc.clientHeight) return;
  watchScrolled(sc);
  const stop = gridJumpStop(sc);
  animateScroll(sc, "y", stop !== null ? stop : offsetWithin(grid, sc, "y"));
}

function closeSearch(keep) {
  if (!searchOpen) return;
  searchOpen = false;
  $("libSearch").classList.remove("active");
  $("searchField").blur();
  send({ cmd: "hideKeyboard" });
  if (!keep) setSearch("");
  renderLibrary();
  const scope = document.getElementById("screen-library");
  // Kept: straight onto the first result, which is the point of having searched. Cleared: back
  // to wherever the highlight was before View, so a cancelled search changes nothing.
  const first = keep && F.search && document.querySelector("#gridScroll [data-focusable]");
  if (first) setFocusEl(first);
  else if (!keep && searchReturnKey) setScopeKey(scope, searchReturnKey);
  if (!focusEl(scope)) ensureFocus(scope);
  searchReturnKey = null;
  setInputMode("pad");
  afterFocusMove();
}

function searchInput(btn) {
  switch (btn) {
    case "A": case "Down": closeSearch(true); break;
    case "B": closeSearch(false); break;
  }
}

$("searchField").addEventListener("input", (e) => { setSearch(e.target.value); renderLibrary(); });
$("searchField").addEventListener("keydown", (e) => {
  e.stopPropagation();
  if (e.key === "Enter" || e.key === "ArrowDown") { e.preventDefault(); closeSearch(true); }
  if (e.key === "Escape") { e.preventDefault(); closeSearch(false); }
});
$("libSearch").addEventListener("mousedown", (e) => { if (!searchOpen) { e.preventDefault(); openSearch(); } });
$("libSearch").addEventListener("mouseenter", () => { if (hoverEnabled() && !searchOpen) { setFocusEl($("libSearch")); paintNav(); } });

/* ============================== couch setup guide ============================== */

const GUIDE_STEPS = [
  ["Sign in from the couch: set up a Windows Hello PIN", "Apps cannot type into the secure lock screen, but you don't need one: in <b>Settings → Accounts → Sign-in options</b>, add a <b>PIN (Windows Hello)</b>. The sign-in screen's PIN pad works with the touch keyboard, which supports gamepad input — so after a wake you can sign in without leaving the sofa. For a fully hands-off couch PC, enable automatic sign-in instead (<b>netplwiz</b>, untick \"Users must enter a user name and password\")."],
  ["Rest instead of leaving the PC on", "<b>Settings → General → Rest and sleep</b> is a console's rest mode: after a stretch with nothing pressed the TV goes dark and the game is paused where it stands, and one press on any controller brings both back. Left resting, the PC goes to sleep. Whether the controller can wake it from <i>sleep</i> is up to its receiver's driver: the same rows list every device that could and allow it with one press. Where none can (a Bluetooth pad on most desktops), a wireless keyboard or mouse, or the power button, wakes it -- set “Then sleep the PC after” to a longer time, or keep the PC on, to suit."],
  ["Auto-start Loungepad", "Turn on <b>Launch Loungepad at login</b> in Settings → Startup so the PC lands straight back on the TV with gamepad-mouse active after waking."],
];

function renderGuide() {
  $("guideBody").innerHTML = GUIDE_STEPS.map(([t, txt], i) =>
    `<div class="guide-step"><div class="guide-num">${String(i + 1).padStart(2, "0")}</div><div class="guide-step-body"><div class="guide-step-title">${t}</div><div class="guide-step-text">${txt}</div></div></div>`
  ).join("");
}

function guideInput(btn) {
  const body = $("guideBody");
  switch (btn) {
    case "Up": animateScroll(body, "y", scrollTarget(body, "y") - 160); break;
    case "Down": animateScroll(body, "y", scrollTarget(body, "y") + 160); break;
    case "B": case "A": guideOpen = false; hideOverlay("overlay-guide"); break;
  }
}

/**
 * The still the host grabbed of whatever was on screen before the menu opened. Deliberately
 * NOT cleared when overlay mode ends: the in-game menu drops overlay mode on its way into the
 * radial, and clearing here would blank the background halfway through that hop. It is simply
 * replaced by the next capture, and null (a capture the host could not make, e.g. a game in
 * exclusive fullscreen) falls back to the menu's own solid ground.
 */
function setOverlayShot(dataUri) {
  const el = $("overlayShot");
  el.style.backgroundImage = dataUri ? `url("${dataUri}")` : "none";
  el.classList.toggle("has-shot", !!dataUri);
}

/* ---- overlay mode: a menu over a still of the desktop or the game behind it ---- */
function setOverlayMode(on) {
  overlayMode = on;
  document.documentElement.classList.toggle("overlay-mode", on);
  document.body.classList.toggle("overlay-mode", on);
  syncTrailers();
  if (on) {
    setInputMode("pad");   // the pointer has no business here
    // Not redundant with the send inside setInputMode: if we were already in pad mode that
    // call returns early, and the host could still be sitting on a stale "pointer".
    send({ cmd: "inputMode", mode: "pad" });
  }
}

/** Nearest radial spoke for a stick vector (y is up from the pad, down in screen space). */
function stickToSpoke(x, y, count) {
  const ang = Math.atan2(-y, x) * 180 / Math.PI;      // screen-space degrees
  const step = 360 / count;
  return ((Math.round((ang + 90) / step) % count) + count) % count;
}

/* ============================== view switching ============================== */

/* The library is the home screen and the other two sit over it: the detail page as an opaque
   sheet, Settings as a box with the library blurred around it. The library is never taken down
   for either -- it stays drawn and unreachable as .under -- so its backdrop is never rebuilt, and
   the picture behind a game's page is the picture it left, in the same place, when the page goes.
   Coming back to it is no entrance at all (data-motion="none"): it was never away. */
function switchView(v) {
  const prev = view;
  view = v;
  document.body.dataset.view = v;   // what the stylesheet blurs under Settings hangs off this
  const next = $("screen-" + v);
  if (prev !== v) {
    document.querySelectorAll(".screen").forEach(s => {
      if (s === next) return;
      if (s.id === "screen-library" && s.classList.contains("active")) { s.classList.remove("active"); s.classList.add("under"); }
      else motionLeave(s);
    });
    if (v === "library") next.dataset.motion = "none"; else delete next.dataset.motion;
    next.classList.remove("under");
  }
  // Already up (boot, or Home from the in-game menu while the library is showing) just stays up.
  motionEnter(next);
  if (v === "library") { clampFocus(); updateLibraryFocus(); }
  if (v === "detail") renderDetail();
  // Always land on the categories, never mid-list in whatever was open last time.
  if (v === "settings") { settingsPane = "nav"; settingsIdx = 0; renderSettings(); }
  if (v === "stats") openStatsView(prev);
  if (v === "onboarding") renderOnboarding();
  else delete document.body.dataset.onbStep;   // what moves the library into view on the Look step
  syncTrailers();
}


/* ============================== the form sheet ==============================
   A short list of Settings-style rows over whatever is open, for the few things that are edited
   rather than picked from a menu: an achievement's unlock, a logged session, the Playnite import.
   The rows are Settings' own (settingsRowHtml), so a toggle, a ◂ ▸ value and an action look and
   answer exactly as they do there, and like there the highlight comes round at the ends. `rows`
   is a function, asked again after every change; a row may also be { section } or { note }, which
   take no highlight. `title` and `sub` may be functions too. */
function openForm(spec) {
  formState = { ...spec, idx: 0 };
  showOverlay("overlay-form");      // active before rendered, or the first row is never lit
  renderForm();
}

function closeForm() {
  if (!formState) return;
  const f = formState;
  formState = null;
  hideOverlay("overlay-form");
  if (f.onClose) f.onClose();
  repaintFocus();
}

function formRows() { return formState ? formState.rows() : []; }
function formFocusables(rows) { return rows.filter(r => !r.section && r.note === undefined); }

function renderForm() {
  if (!formState) return;
  const val = (v) => (typeof v === "function" ? v() : v) || "";
  $("formKicker").textContent = val(formState.kicker);
  $("formTitle").textContent = val(formState.title);
  const sub = val(formState.sub);
  $("formSub").textContent = sub;
  $("formSub").hidden = !sub;
  const rows = formRows();
  const focusables = formFocusables(rows);
  formState.idx = Math.max(0, Math.min(formState.idx, focusables.length - 1));
  const list = $("formList");
  const shape = (formState.kind || "") + "|" + rows.map(r => r.section ? "s:" + r.section : r.note !== undefined ? "n" : "r").join("|");
  if (list.__shape !== shape) {
    const keepTop = list.scrollTop;
    list.innerHTML = "";
    let fi = -1;
    rows.forEach(r => {
      if (r.section) { list.appendChild(settingsSectionEl(r.section)); return; }
      if (r.note !== undefined) { const n = document.createElement("div"); n.className = "form-note"; list.appendChild(n); return; }
      fi++;
      list.appendChild(formRowEl(fi));
    });
    list.scrollTop = keepTop;
    list.__shape = shape;
  }
  let fi = -1;
  rows.forEach((r, i) => {
    const el = list.children[i];
    if (r.section) return;
    if (r.note !== undefined) { if (el.__note !== r.note) { el.textContent = r.note; el.__note = r.note; } return; }
    fi++;
    el.dataset.focusKey = "form:" + fi;
    if (r.adjust) el.dataset.navLock = "horizontal"; else delete el.dataset.navLock;
    el.classList.toggle("muted", !!r.muted);
    const html = settingsRowHtml(r);
    if (el.__html !== html) { el.innerHTML = html; el.__html = html; }
  });
  const cur = focusables[formState.idx];
  $("formFoot").innerHTML = foot(
    ...(cur && !cur.muted && (cur.action || cur.adjust) ? [["A", cur.aLabel || (cur.type === "toggle" ? "Switch" : cur.type === "action" ? cur.label || "Select" : "Type")]] : []),
    ...(cur && cur.adjust && !cur.muted ? [["DpadH", "Adjust"]] : []),
    ["B", formState.backLabel || "Back"]);
  const scope = $("overlay-form");
  setScopeKey(scope, cur ? "form:" + formState.idx : null);
  const el = focusEl(scope);
  const show = focusVisible();
  Nav.focusables(scope).forEach(x => x.classList.toggle("focused", show && x === el));
  if (el && show) revealFocus(el);
}

function formRowEl(idx) {
  const el = document.createElement("div");
  el.className = "set-row";
  el.dataset.focusable = "";
  el.dataset.rowIndex = idx;
  el.addEventListener("mouseenter", () => {
    if (!hoverEnabled() || !formState || formState.idx === idx) return;
    formState.idx = idx; renderForm();
  });
  el.addEventListener("click", (e) => {
    if (!formState) return;
    formState.idx = idx;
    const row = formFocusables(formRows())[idx];
    if (!row || row.muted) { renderForm(); return; }
    const arrow = e.target instanceof Element ? e.target.closest(".arrow") : null;
    if (arrow && row.adjust) row.adjust(parseInt(arrow.dataset.dir, 10) || 1);
    else if (row.action) row.action(); else if (row.adjust) row.adjust(1);
    renderForm();
  });
  return el;
}

function formInput(btn) {
  const row = formFocusables(formRows())[formState.idx];
  const inRows = (x) => x.dataset.rowIndex !== undefined;
  switch (btn) {
    case "Up": case "Down":
      if (paneMove(btn, inRows)) {
        const el = focusEl();
        if (el && el.dataset.rowIndex !== undefined) formState.idx = parseInt(el.dataset.rowIndex, 10);
        renderForm();
      }
      break;
    case "Left": case "Right":
      if (row && row.adjust && !row.muted) { row.adjust(btn === "Right" ? 1 : -1); renderForm(); }
      break;
    case "A":
      if (!focusVisible() || !row || row.muted) break;
      if (row.action) row.action(); else if (row.adjust) row.adjust(1);
      renderForm();
      break;
    case "B": closeForm(); break;
  }
}

/* ---- dates and times on a pad ----
   ◂ ▸ nudge a value, and A on the row types one exactly (the on-screen keyboard comes up with the
   field). These read what was typed. */
function parseTypedDay(text) {
  const t = String(text || "").trim();
  const iso = /^(\d{4})-(\d{1,2})-(\d{1,2})$/.exec(t);
  if (iso) { const d = new Date(+iso[1], +iso[2] - 1, +iso[3]); return isNaN(d) ? null : d; }
  const dmy = /^(\d{1,2})[/.](\d{1,2})[/.](\d{4})$/.exec(t);
  if (dmy) { const d = new Date(+dmy[3], +dmy[2] - 1, +dmy[1]); return isNaN(d) ? null : d; }
  const parsed = Date.parse(t);
  if (isNaN(parsed)) return null;
  const d = new Date(parsed);
  return new Date(d.getFullYear(), d.getMonth(), d.getDate());
}
function parseTypedTime(text) {
  const m = /^(\d{1,2})(?:[:.](\d{2}))?\s*(am|pm)?$/i.exec(String(text || "").trim());
  if (!m) return null;
  let h = +m[1];
  const min = m[2] ? +m[2] : 0;
  if (m[3]) { const pm = m[3].toLowerCase() === "pm"; if (h === 12) h = pm ? 12 : 0; else if (pm) h += 12; }
  return h < 24 && min < 60 ? { h, min } : null;
}
/* "90", "1:30", "1h 30m", "2h", "45m" -- minutes out. */
function parseTypedLength(text) {
  const t = String(text || "").trim().toLowerCase();
  if (/^\d+$/.test(t)) return +t;
  const hm = /^(\d+):(\d{1,2})$/.exec(t);
  if (hm) return +hm[1] * 60 + +hm[2];
  const h = /(\d+(?:\.\d+)?)\s*h/.exec(t), m = /(\d+)\s*m/.exec(t);
  if (!h && !m) return null;
  return Math.round((h ? +h[1] * 60 : 0) + (m ? +m[1] : 0));
}
function isoDay(d) { return `${d.getFullYear()}-${String(d.getMonth() + 1).padStart(2, "0")}-${String(d.getDate()).padStart(2, "0")}`; }

/* ============================== input routing ============================== */

const DIRECTIONS = new Set(["Up", "Down", "Left", "Right"]);

function handleInput(btn, src) {
  // The text field (and the touch keyboard's own pad support) owns input -- except B, which is
  // how every other screen is left and has to be how this one is left too.
  if (inputOpen) { if (btn === "B") closeInput(false); return; }
  // Only a direction hands control back to the pad. A face button must never re-arm a
  // highlight the pointer has cleared, so A over empty space does nothing.
  if (DIRECTIONS.has(btn)) setInputMode("pad");
  if (mediaView) { mediaViewInput(btn); return; }
  if (confirmState) { confirmInput(btn); return; }
  if (captureState) { captureInput(btn); return; }
  if (keyPick) { keyPickInput(btn); return; }
  if (searchOpen) { searchInput(btn); return; }
  if (actionWheelOpen) { actionWheelInput(btn); return; }
  if (radialSub) { radialSubInput(btn); return; }
  if (radialOpen) { radialInput(btn); return; }
  if (ingameOpen) { ingameInput(btn); return; }
  if (guideOpen) { guideInput(btn); return; }
  if (filterOpen) { filterInput(btn); return; }
  if (gameMenu) { gameMenuInput(btn); return; }
  if (collectOpen) { collectInput(btn); return; }
  if (manageOpen) { manageInput(btn); return; }
  if (choiceState) { choiceInput(btn); return; }
  if (modsState) { modsInput(btn); return; }
  // The session's charts sit over a game's Stats sheet or a day's timeline; the achievements
  // sheet can sit over either of those. A day's timeline is only ever opened from the screen.
  // The form sheet is opened from those sheets (and from Settings), so it comes before them.
  if (formState) { formInput(btn); return; }
  if (sessionState) { sessionInput(btn); return; }
  if (actState) { actInput(btn); return; }
  if (achState) { achInput(btn); return; }
  if (dayState) { dayInput(btn); return; }

  if (view === "library") libraryInput(btn);
  else if (view === "detail") detailInput(btn);
  else if (view === "settings") settingsInput(btn);
  else if (view === "stats") statsInput(btn);
  else if (view === "onboarding") onboardingInput(btn);
}

// After every press, so the claim follows the screen the press just led to.
const routeInput = handleInput;
handleInput = function (btn, src) { routeInput(btn, src); publishClaims(); };

/*
 * The keyboard, as a pad. What each key stands for is what the legend draws for it (see
 * BUTTON_ART.keyboard), so the two have to move together: Enter is A, Esc is B, the letters are
 * the letters, the brackets are the shoulders, "/" opens search and M opens Settings.
 */
const KEYMAP = {
  ArrowUp: "Up", ArrowDown: "Down", ArrowLeft: "Left", ArrowRight: "Right",
  Enter: "A", Space: "A", Escape: "B", Backspace: "B",
  KeyY: "Y", KeyX: "X", KeyM: "Menu",
  BracketLeft: "LB", BracketRight: "RB",
  Slash: "View",
};
// By key as well as by physical code: some keyboards and injected input carry only the one.
const KEYMAP_BY_KEY = {
  ArrowUp: "Up", ArrowDown: "Down", ArrowLeft: "Left", ArrowRight: "Right",
  Enter: "A", " ": "A", Escape: "B", Backspace: "B",
  y: "Y", Y: "Y", x: "X", X: "X", m: "Menu", M: "Menu",
  "[": "LB", "]": "RB", "/": "View",
};

const KEY_REPEAT_MS = 85;
let lastKeyStepAt = -Infinity;

window.addEventListener("keydown", (e) => {
  if (inputOpen) return;
  const lib = libraryTakesKeys() ? (LIBRARY_KEYS[e.code] || LIBRARY_KEYS[e.key]) : null;
  const btn = lib || KEYMAP[e.code] || KEYMAP_BY_KEY[e.key];
  if (!btn || e.ctrlKey || e.altKey || e.metaKey) return;
  e.preventDefault();
  setInputFamily("keyboard");
  // A held direction walks on; a held Enter must not launch the game twice.
  if (e.repeat && !DIRECTIONS.has(btn)) return;
  // Windows repeats a held key about 30 times a second. Every one of those used to be handled,
  // each with a layout pass, faster than the page could paint -- so the screen froze while the
  // key was held and jumped to the end when it was let go. Repeats are paced to a rate the eye
  // can follow, and the ones in between are dropped rather than queued.
  const now = performance.now();
  if (e.repeat && now - lastKeyStepAt < KEY_REPEAT_MS) return;
  lastKeyStepAt = now;
  handleInput(btn, "kb");   // handleInput switches to pad mode on directions only
});

/*
 * Ctrl is the library's Y (a game's options), and Ctrl is also a modifier: a key or a click with it
 * held, Ctrl+Shift+Esc, and a pinch on a DualSense's touchpad, which the host sends as Ctrl plus the
 * wheel. So it fires on the release, and only when nothing else happened while it was down -- the
 * way the Windows key opens Start. Captured, so a listener that stops a key cannot hide it from here.
 */
let ctrlTap = false;
const isCtrl = (e) => e.key === "Control" || e.code === "ControlLeft" || e.code === "ControlRight";
window.addEventListener("keydown", (e) => {
  if (!isCtrl(e)) ctrlTap = false;
  else if (!e.repeat) ctrlTap = !e.shiftKey && !e.altKey && !e.metaKey;
}, true);
window.addEventListener("keyup", (e) => {
  if (!isCtrl(e) || !ctrlTap) return;
  ctrlTap = false;
  if (inputOpen || !libraryTakesKeys()) return;
  e.preventDefault();
  setInputFamily("keyboard");
  handleInput("Y", "kb");
});
for (const type of ["mousedown", "wheel", "blur"])
  window.addEventListener(type, () => { ctrlTap = false; }, { capture: true, passive: true });

/*
 * The mouse, as a pad. Buttons only: a real click means a hand is on the mouse, where a mousemove
 * can be the left stick driving the pointer. The right button is "back", and on a game it is that
 * game's menu -- the two things a mouse otherwise cannot do. A click on the dimmed screen around
 * any menu is "back" as well, as it already was on the confirm dialog.
 */
/* A click the host sent from a pad's touchpad is not the mouse. The host says so just before
   sending it, and the two can arrive in either order, so the note is kept for a moment AND puts
   the pad family back in case the click got here first. */
let padClickAt = -Infinity;
window.addEventListener("mousedown", () => {
  if (performance.now() - padClickAt > 400) setInputFamily("keyboard");
}, true);

window.addEventListener("contextmenu", (e) => {
  e.preventDefault();
  if (inputOpen) return;
  const tile = e.target instanceof Element ? e.target.closest("[data-game-id]") : null;
  if (tile && view === "library" && !overlayOpen() && !overlayMode) {
    setFocusEl(tile);
    setPointerOnItem(true);
    updateLibraryFocus(true);
    libraryAccept("Y");
    return;
  }
  handleInput("B", "mouse");
});

document.querySelectorAll(".overlay").forEach(ov => {
  if (ov.id === "overlay-confirm") return;   // has its own, and answers "no"
  ov.addEventListener("click", (e) => {
    if (e.target !== ov) return;
    if (ov.id === "overlay-input") closeInput(false);
    else handleInput("B", "mouse");
  });
});

/* The text prompt's own hints. Always keys, whatever is in hand: it is a text field. */
function renderInputHint() {
  const el = $("inputHint");
  if (!el) return;
  // Confirm stays the Enter key: A on a pad is the touch keyboard's own key press. Cancel is
  // drawn for whatever is in hand, since B on a pad now cancels too.
  el.innerHTML = `<div class="legend-item" id="inputOk">${keycap("Enter")}<span>Confirm</span></div>` +
    `<div class="legend-item" id="inputCancel">${slot("B")}<span>Cancel</span></div>`;
  $("inputOk").addEventListener("click", () => closeInput(true));
  $("inputCancel").addEventListener("click", () => closeInput(false));
}

/* ============================== host messages ============================== */

let lastBatteryMsg = null;

function handleHostMessage(m) {
  switch (m.type) {
    case "state": {
      const firstState = S.settings === null;
      const wasEmpty = S.games.length === 0;
      S.games = m.games || [];
      buildEditions();
      S.collections = m.collections || [];
      // A collection can be deleted while it is still being filtered on. Left alone, the stale id
      // matches nothing and the library goes empty with no visible reason why.
      for (const id of [...F.collections])
        if (!S.collections.some(c => c.id === id)) F.collections.delete(id);

      S.settings = m.settings;
      S.displays = m.displays || [];
      // Keep whatever the last themes push carried if this state has none, so a state
      // refresh cannot blank the list between watcher events.
      S.themes = m.themes || S.themes;
      S.startupRegistered = m.startupRegistered;
      S.gameRunning = m.gameRunning;
      S.runningGameId = m.runningGameId;
      S.gameStarting = !!(m.gameRunning && m.gameStarting);
      S.scanning = m.scanning;
      S.steamAccount = m.steamAccount || null;
      S.stores = m.stores || null;
      S.emulation = m.emulation || null;
      S.mods = m.mods || null;
      // A state push carries the apps without their icons (see ActionsPayload on the host); the
      // icons that came with the last actions message are carried over by app id.
      S.actions = withActionIcons(m.actions, S.actions);
      S.xboxButton = m.xboxButton || null;
      // The wake facts ride along once the host has read them; a push from before that keeps
      // whatever the last rest message carried.
      if (m.rest) { S.rest = m.rest.wake || !S.rest ? m.rest : { ...m.rest, wake: S.rest.wake }; S.gamePaused = !!m.rest.paused; }
      S.update = m.update || S.update;
      if (m.scanProgress) S.scanProgress = m.scanProgress;
      S.achievements = m.achievements || {};
      S.sessionStart = m.sessionStart || null;
      if (S.settings) S.settings.launchOnStartup = m.startupRegistered;
      applyTheme();
      // First real library: let clampFocus drop the highlight onto the first game rather
      // than leaving it wherever the empty screen had put it.
      if ((firstState || wasEmpty) && S.games.length) clearFocus(document.getElementById("screen-library"));
      renderLibrary();
      if (view === "settings") renderSettings();
      if (view === "detail") renderDetail();
      if (collectOpen) renderCollect();
      if (manageOpen) renderManage();
      if (gameMenu) renderGameMenu();
      if (filterOpen) renderFilter();
      if (choiceState) renderChoice();
      if (modsState) renderMods();
      if (view === "stats") renderStats();
      if (achState) renderAchievements();
      if (actState) renderActivity();
      if (view === "onboarding") renderOnboarding();
      // A new install opens on the first-run setup, which asks for the TV among other things. An
      // install that has been through it but has lost its TV (a display unplugged, say) still
      // gets sent to the one row that matters.
      if (firstState && S.settings && needsOnboarding()) openOnboarding("library");
      else if (firstState && S.settings && !S.settings.tvDeviceName && S.displays.length > 1) {
        switchView("settings");
        toast("Welcome — pick which display is your TV");
      }
      break;
    }
    // The last scan and the passes after it, source by source: the first-run setup's strip.
    case "scanProgress":
      S.scanProgress = m.steps || [];
      if (view === "onboarding") renderOnboarding();
      break;
    case "onboarding": onOnboardingInfo(m); break;
    case "onboardingPin": onOnboardingPin(m); break;
    case "onboardingVortex": onOnboardingVortex(m); break;
    // A switch flipped on the Loungepad keyboard's own options page. Merged, not replaced: the
    // page's copy may hold a change of its own that has not been saved yet.
    case "keyboardOptions":
      if (S.settings && m.settings) Object.assign(S.settings, m.settings);
      if (view === "settings") renderSettings();
      break;
    case "scanning":
      S.scanning = m.busy;
      $("scanStatus").textContent = m.busy ? "SCANNING…" : "";
      if (!m.busy && view === "settings") renderSettings();
      break;
    case "pad":
      // The family rides with the press, so the legend is right for the pad that was just used.
      setInputFamily(m.layout);
      handleInput(m.button, "pad");
      break;
    // The pad in hand changed, or one was picked up again after the keyboard had the legend.
    case "padLayout":
      if (m.name) S.padName = m.name;
      setInputFamily(m.layout);
      if (view === "onboarding") renderOnboarding();
      break;
    case "padClick":
      padClickAt = performance.now();
      setInputFamily(padFamily);
      break;
    case "stickScroll":
      onStickScroll(m.v || 0);
      break;
    case "overlay":
      overlayTargetTitle = m.targetTitle || "";
      overlayTargetProcess = m.targetProcess || "";
      overlayTargetIsGame = !!m.targetIsGame;
      setOverlayShot(m.shot);
      setOverlayMode(true);
      hostWindows = m.windows || [];
      if (m.runningGameId !== undefined) S.runningGameId = m.runningGameId;
      if (m.mode === "radial") openRadial(m.targetTitle);
      else if (m.mode === "ingame") openIngame();
      break;
    case "windows":
      hostWindows = m.windows || [];
      if (radialSub === "windows") renderRadialSub();
      break;
    // Thumbnails arrive one at a time, after the list is already on screen -- see
    // StartThumbnails on the host for why they are not part of it.
    case "windowThumb": {
      const w = hostWindows.find(x => x.handle === m.handle);
      if (w && m.image) {
        w.thumb = m.image;
        w.thumbIsIcon = !!m.icon;
        if (radialSub === "windows") renderRadialSub();
      }
      break;
    }
    case "dismiss":
      dismissOverlays();
      break;
    case "stick":
      if (actionWheelOpen) actionWheelStick(m.x, m.y);
      else if (radialOpen && !radialSub) {
        const i = stickToSpoke(m.x, m.y, RADIAL_ITEMS.length);
        if (i !== radialIdx) { radialIdx = i; renderRadial(); }
      }
      break;
    // The actions list changed: an edit answered, or detection finished with icons. Only the
    // places that draw it are repainted; the library is never touched for this.
    case "actions":
      S.actions = withActionIcons(m.actions, S.actions);
      if (view === "settings" && settingsTab === "actions") renderSettings();
      refreshActionWheel();
      break;
    case "actionCaptured":
      onActionCaptured(m.combo || null);
      break;
    case "actionCaptureRejected":
      onActionCaptureRejected(m.button || "");
      break;
    case "actionApps":
      onActionApps(m);
      break;
    case "actionAppAdded":
      onActionAppAdded(m);
      break;
    // A theme file changed on disk. The list carries a fresh cache-busting stamp, so
    // re-applying reloads the stylesheet without a restart.
    case "themes":
      S.themes = m.themes || [];
      applyTheme();
      if (view === "settings") renderSettings();
      break;

    case "inputMode":
      // Only the host can tell us the stick moved the cursor, or that the launcher just came
      // back to the foreground and the pad should be driving again. It never pushes "pad" off
      // a face button, so this can't re-arm a highlight the pointer has cleared.
      // "pointer" from the host is the stick and nothing else, so it is the pad being used.
      if (m.mode === "pointer") setInputFamily(padFamily);
      setInputMode(m.mode);
      break;
    case "padConnected":
      S.padConnected = m.connected;
      if (m.connected) toast("Controller connected");
      else updateBattery(null);
      if (lastBatteryMsg && m.connected) updateBattery(lastBatteryMsg);
      break;
    case "battery":
      lastBatteryMsg = m;
      updateBattery(m);
      if (view === "onboarding") renderOnboarding();
      break;
    case "game":
      S.gameRunning = m.running;
      S.runningGameId = m.id;
      S.sessionStart = m.since || null;
      S.gamePaused = !!(m.running && m.paused);
      // No process of the game seen yet: the card says STARTING, and Close game simply ends it.
      S.gameStarting = !!(m.running && m.starting);
      // The cards for what the session unlocked wait for the launcher to be back on the TV.
      if (!m.running) { S.telemetry = null; setTimeout(drainUnlocks, 800); }
      renderLibrary();
      if (ingameOpen) renderIngame();
      break;
    // Rest mode: the phase (the page stops its trailers while the screen is dark), whether the
    // game is frozen, and what can wake this PC for the Settings rows.
    case "rest":
      S.rest = m.rest || null;
      S.gamePaused = !!(S.gameRunning && m.rest && m.rest.paused);
      syncTrailers();
      if (ingameOpen) renderIngame();
      updatePlayingMeta();
      if (view === "settings") renderSettings();
      break;
    // Play sessions and achievements: see activity.js for every one of these.
    case "telemetry":
      S.telemetry = m;
      if (ingameOpen) renderIngameStats();
      break;
    case "activity": onActivityMessage(m); break;
    case "activitySession": onSessionMessage(m); break;
    case "activityAll": onActivityAll(m); break;
    case "activityRecorded": onActivityRecorded(m); break;
    case "playnite":
      onPlayniteMessage(m);
      if (view === "onboarding") onOnboardingPlaynite(m);
      break;
    case "achievements": onAchievementsMessage(m); break;
    case "achievementsSummary": onAchievementsSummary(m); break;
    case "achievementsAll": onAchievementsAll(m); break;
    case "achievementsUnlocked": onAchievementsUnlocked(m); break;
    // A trailer landed on disk. One field on one game, in place: a state push here would rebuild
    // the library under somebody browsing it, for a change nothing on screen shows.
    case "trailerCached": {
      const g = gameById(m.id);
      if (g) g.trailerFile = m.file || null;
      break;
    }
    // A gallery fetched on demand. Into the game in place, and onto the page if it is the one up.
    case "media": {
      const g = gameById(m.id);
      if (!g) break;
      g.media = Array.isArray(m.media) ? m.media : [];
      if (view === "detail" && detailGameId === m.id) { renderDetailMedia(g); updateDetailFocus(); }
      break;
    }
    // The host's folder dialog closed on a folder; the rest of adding it is asked here.
    case "romFolderPicked":
      startRomFolderWizard(m.path, m.platformId);
      break;
    // "Add an emulator…" from a pick list came back -- with an id, or with null when the file
    // dialog was cancelled. Either way the pick list that asked for it is put back up, so a
    // cancelled dialog cannot leave the folder wizard hanging with nothing on screen.
    case "emuAdded": {
      const cb = pendingEmuPick;
      pendingEmuPick = null;
      if (cb && m.id) cb(m.id);
      else if (cb && romWizard) wizardPickEmulator();
      break;
    }
    case "mods":
      onModsMessage(m);
      break;
    case "update": {
      const was = S.update && S.update.state;
      S.update = m.status;
      // Said once, when a background download finishes. Anything somebody asked for, they are
      // already looking at in Settings or the tray menu.
      if (m.status.state === "ready" && was !== "ready" && !m.status.userAsked)
        toast(`Loungepad ${m.status.latest} is ready. It installs the next time Loungepad starts`);
      if (view === "settings") renderSettings();
      break;
    }
    // B on the built-in keyboard closes the keyboard, and the press never reaches the page. With
    // a text field open behind it, that B means "I'm done with this field" as well.
    case "keyboardDismissed":
      if (inputOpen) closeInput(false);
      else if (searchOpen) closeSearch(false);
      break;
    case "toast":
      toast(m.message);
      break;
  }
}

if (HOST) HOST.addEventListener("message", (e) => handleHostMessage(e.data));

/* ============================== mock (browser preview only) ============================== */

const mockWindows = [
  { handle: 1, title: "Cyberpunk 2077", processName: "Cyberpunk2077", minimized: false, display: "" },
  { handle: 2, title: "Steam", processName: "steam", minimized: false, display: "" },
  { handle: 3, title: "Downloads - File Explorer", processName: "explorer", minimized: true, display: "" },
];

const mockCollections = [
  { id: "c1", name: "Cozy evenings", gameIds: ["gog:salttide", "manual:foundrynine"] },
];

/* A slice of the host's packs, so the wheel, the grid and the editor can be walked in the browser:
   two browsers (one of them not "installed", to check it stays out of the grid), a player, the
   Explorer, and an app the user added. Everywhere carries the bound-but-hidden arrows. */
const mockActions = (() => {
  const A = (id, name, keys, button = null, hidden = false, danger = false) => ({ id, name, keys, button, hidden, custom: false, danger });
  const browser = (downloads, priv) => [
    A("new-tab", "New tab", "Ctrl+T", "Y"), A("close-tab", "Close tab", "Ctrl+W"), A("reopen-tab", "Reopen closed tab", "Ctrl+Shift+T"),
    A("next-tab", "Next tab", "Ctrl+Tab"), A("prev-tab", "Previous tab", "Ctrl+Shift+Tab"), A("back", "Back", "Alt+Left", "X"),
    A("forward", "Forward", "Alt+Right"), A("reload", "Reload", "F5"), A("address", "Address bar", "Ctrl+L"), A("find", "Find in page", "Ctrl+F"),
    A("fullscreen", "Full screen", "F11"), A("zoom-in", "Zoom in", "Ctrl+="), A("zoom-out", "Zoom out", "Ctrl+-"),
    A("zoom-reset", "Reset zoom", "Ctrl+0", null, true), A("bookmark", "Bookmark this page", "Ctrl+D", null, true), A("history", "History", "Ctrl+H", null, true),
    A("downloads", "Downloads", downloads, null, true), A("new-window", "New window", "Ctrl+N", null, true), A("private", "Private window", priv, null, true),
    A("home", "Home page", "Alt+Home", null, true),
  ];
  const icon = (hue) => "data:image/svg+xml," + encodeURIComponent(
    `<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 64 64"><circle cx="32" cy="32" r="28" fill="hsl(${hue} 70% 50%)"/><circle cx="32" cy="32" r="12" fill="white" fill-opacity="0.85"/></svg>`);
  const app = (id, name, exes, actions, extra = {}) => ({ id, name, exes, custom: false, pinned: false, installed: true, modified: false, icon: null, actions, ...extra });
  return { apps: [
    app("everywhere", "Everywhere", [], [
      A("vol-up", "Volume up", "VolumeUp"), A("vol-down", "Volume down", "VolumeDown"), A("mute", "Mute", "VolumeMute"),
      A("play-pause", "Play / pause", "MediaPlayPause"), A("next-track", "Next track", "MediaNext"), A("prev-track", "Previous track", "MediaPrev"),
      A("show-desktop", "Show desktop", "Win+D"), A("last-window", "Last window", "Alt+Tab"), A("task-view", "Task view", "Win+Tab"),
      A("maximize", "Maximize window", "Win+Up"), A("snap-left", "Snap left", "Win+Left"), A("snap-right", "Snap right", "Win+Right"),
      A("screenshot", "Save a screenshot", "Win+PrintScreen"), A("enter", "Enter", "Enter"), A("escape", "Escape", "Esc"),
      A("close-window", "Close window", "Alt+F4", null, false, true),
      A("arrow-up", "Arrow up", "Up", "Up", true), A("arrow-down", "Arrow down", "Down", "Down", true),
      A("arrow-left", "Arrow left", "Left", "Left", true), A("arrow-right", "Arrow right", "Right", "Right", true),
      A("copy", "Copy", "Ctrl+C", null, true), A("paste", "Paste", "Ctrl+V", null, true), A("undo", "Undo", "Ctrl+Z", null, true), A("select-all", "Select all", "Ctrl+A", null, true),
    ]),
    app("firefox", "Firefox", ["firefox"], browser("Ctrl+Shift+Y", "Ctrl+Shift+P"), { icon: icon(25) }),
    app("chrome", "Google Chrome", ["chrome"], browser("Ctrl+J", "Ctrl+Shift+N"), { installed: false }),
    app("vlc", "VLC", ["vlc"], [
      A("play-pause", "Play / pause", "Space", "X"), A("fullscreen", "Full screen", "F", "Y"), A("mute", "Mute", "M"),
      A("skip-fwd", "Skip forward 10 s", "Alt+Right"), A("skip-back", "Skip back 10 s", "Alt+Left"), A("next", "Next", "N"), A("prev", "Previous", "P"),
      A("subtitles", "Subtitle track", "V"), A("audio", "Audio track", "B"), A("faster", "Faster", "]"), A("slower", "Slower", "["), A("stop", "Stop", "S"),
      A("playlist", "Playlist", "Ctrl+L"), A("quit", "Quit VLC", "Ctrl+Q", null, true, true),
    ], { icon: icon(30) }),
    app("explorer", "File Explorer", ["explorer"], [
      A("back", "Back", "Alt+Left", "X"), A("forward", "Forward", "Alt+Right"), A("up", "Up a folder", "Alt+Up", "Y"), A("open", "Open", "Enter"),
      A("new-folder", "New folder", "Ctrl+Shift+N"), A("rename", "Rename", "F2"), A("delete", "Delete", "Delete"), A("copy", "Copy", "Ctrl+C"),
      A("paste", "Paste", "Ctrl+V"), A("select-all", "Select all", "Ctrl+A"), A("address", "Address bar", "Alt+D"), A("search", "Search", "Ctrl+E"), A("refresh", "Refresh", "F5"),
    ], { icon: icon(45) }),
    app("spotify", "Spotify", ["spotify"], [A("play-pause", "Play / pause", "Space", "X"), A("next", "Next track", "Ctrl+Right", "Y")], { installed: false }),
    app("plex", "Plex", ["plex"], [
      { ...A("c1a2b3c4", "Play / pause", "Space", "X"), custom: true },
      { ...A("c5d6e7f8", "Full screen", "F"), custom: true },
    ], { custom: true }),
  ] };
})();
const mockActionsPristine = JSON.parse(JSON.stringify(mockActions));
/* Everything that reacts to the Xbox button still on, so Windows and Steam can be walked. */
const mockXboxButton = { gameBar: true, xboxMode: true, steam: true, steamRunning: true, steamBusy: false };

/* Preview only: P opens the Power Wheel over "Firefox", O over an app we have no actions for. */
if (!HOST) window.addEventListener("keydown", (e) => {
  if (inputOpen || overlayMode || keyPick || captureState || e.ctrlKey || e.altKey || e.metaKey) return;
  const over = (title, exe) => handleHostMessage({ type: "overlay", mode: "radial", targetTitle: title, targetProcess: exe, shot: null, windows: mockWindows });
  if (e.key === "p") over("Mozilla Firefox", "firefox");
  if (e.key === "o") over("Untitled - Notepad", "notepad");
});

/* A slice of the host's catalogue, enough to walk the folder wizard and the options lists. */
const mockEmulation = {
  emulators: [
    { id: "e1", name: "RetroArch", exePath: "C:\\RetroArch\\retroarch.exe", args: '-L "{core}" "{rom}" -f', preset: "retroarch", platforms: ["nes", "snes", "n64", "gb", "gba", "ps1", "genesis", "arcade"], detected: true },
    { id: "e2", name: "DuckStation", exePath: "C:\\Emulators\\DuckStation\\duckstation-qt-x64-ReleaseLTCG.exe", args: '-batch -fullscreen -- "{rom}"', preset: "duckstation", platforms: ["ps1"] },
  ],
  romFolders: [
    { id: "f1", path: "D:\\ROMs\\SNES", platformId: "snes", emulatorId: "e1", core: "C:\\RetroArch\\cores\\snes9x_libretro.dll", args: null, extensions: null, recurse: true },
    { id: "f2", path: "D:\\ROMs\\PS1", platformId: "ps1", emulatorId: "e1", core: null, args: null, extensions: null, recurse: true },
    { id: "f3", path: "D:\\ROMs\\Arcade", platformId: "arcade", emulatorId: null, core: null, args: null, extensions: ["zip"], recurse: false },
    { id: "f4", path: "C:\\RetroArch\\playlists\\Nintendo - Game Boy Advance.lpl", platformId: "gba", emulatorId: "e1", core: "C:\\RetroArch\\cores\\mgba_libretro.dll", args: null, extensions: null, recurse: true, playlist: true, detected: true },
  ],
  platforms: [
    { id: "nes", name: "Nintendo Entertainment System", shortName: "NES", extensions: ["nes", "fds", "zip", "7z"], hasCores: true },
    { id: "snes", name: "Super Nintendo", shortName: "SNES", extensions: ["sfc", "smc", "zip", "7z"], hasCores: true },
    { id: "n64", name: "Nintendo 64", shortName: "N64", extensions: ["n64", "z64", "v64", "zip"], hasCores: true },
    { id: "gba", name: "Game Boy Advance", shortName: "GBA", extensions: ["gba", "zip", "7z"], hasCores: true },
    { id: "ps1", name: "PlayStation", shortName: "PS1", extensions: ["cue", "chd", "pbp", "m3u"], hasCores: true },
    { id: "ps2", name: "PlayStation 2", shortName: "PS2", extensions: ["iso", "chd", "cso"], hasCores: true },
    { id: "genesis", name: "Sega Genesis / Mega Drive", shortName: "Genesis", extensions: ["md", "gen", "bin", "zip"], hasCores: true },
    { id: "arcade", name: "Arcade", shortName: "Arcade", extensions: ["zip", "7z", "chd"], hasCores: true },
  ],
};

/* Mods, as Vortex would answer them. Which state a game lands in follows its store, so every
   screen the overlay can show is one tile away: Steam games are ready with a list (Cassette Run
   with an empty one), Epic has no Vortex at all, GOG is known but not set up, Xbox needs a
   restart, Manual is a game Vortex has not found. */
function mockMods(msg) {
  const g = S.games.find(x => x.id === msg.id);
  if (!g) return;
  mockHandle._mods = mockHandle._mods || {};
  const vortex = {
    installed: g.platform !== "Epic", path: "C:\\Users\\couch\\AppData\\Local\\Programs\\Vortex\\Vortex.exe",
    version: "1.13.7", running: true, bridgeReady: g.platform !== "Xbox", needsRestart: g.platform === "Xbox", error: null,
  };
  const reply = (extra) => setTimeout(() => handleHostMessage({
    type: "mods", gameId: g.id, vortex, downloadUrl: "https://www.nexusmods.com/site/mods/1?tab=files",
    prompts: [], notices: [], extensions: [], game: null, mods: [], message: null, ...extra,
  }), 450);
  mockHandle._prompts = mockHandle._prompts || {};
  if (msg.cmd === "modsAnswer") { mockHandle._prompts[g.id] = []; toast(`(preview) answered “${msg.action}”`); }
  const slug = g.id.replace(/\W/g, "");
  if (g.platform === "Epic") return reply({ state: "notInstalled", message: "Vortex, the Nexus Mods manager, is not installed on this PC" });
  if (g.platform === "Xbox") return reply({ state: "needsRestart", message: "Vortex is running but has not loaded the Loungepad bridge yet. Restart Vortex once" });
  if (g.platform === "Manual") return reply({ state: "unsupported",
    message: `Vortex has no extension for ${g.title} yet. An extension is what teaches Vortex a game; these look like they are for it`,
    extensions: [
      { modId: 1234, fileId: 1, name: `${g.title} Support`, gameName: g.title, gameDomain: slug, author: "a modder", version: "1.0.0", exact: true },
      { modId: 1235, fileId: 2, name: `${g.title}: Remastered Support`, gameName: `${g.title}: Remastered`, gameDomain: slug + "remastered", author: "someone else", version: "0.2", exact: false },
    ] });
  const managed = g.platform === "Steam" || !!(mockHandle._managed && mockHandle._managed[g.id]);
  const vg = { id: slug, name: g.title, path: g.installDir || "C:\\Games\\" + g.title, managed, nexusDomain: slug, hidden: false };
  // Salt & Tide is the GOG game Vortex knows by name only; the other GOG games it has located.
  if (g.title === "Salt & Tide" && !managed) return reply({ state: "notDiscovered", game: { ...vg, path: null },
    message: `Vortex has the extension for ${g.title} but has not been told where the game is. Loungepad knows: C:\\Games\\${g.title}` });
  if (!vg.managed) return reply({ state: "notManaged", game: vg,
    message: `Vortex knows ${g.title} but has not been set up for it. That first step -- a folder for the mods, how they are deployed -- is done in Vortex's own window, once` });
  let mods = mockHandle._mods[g.id];
  if (!mods) mods = mockHandle._mods[g.id] = g.title === "Cassette Run" ? [] : [
    { id: "m1", name: "SkyUI", version: "5.2SE", author: "SkyUI Team", category: "42", nexusModId: 12604, nexusDomain: "skyrimspecialedition", state: "installed", enabled: true },
    { id: "m2", name: "Unofficial Patch", version: "4.3.3", author: "Arthmoor", category: null, nexusModId: 266, state: "installed", enabled: true },
    { id: "m0", name: "Hand-installed Fix", version: null, author: null, category: null, nexusModId: null, state: "installed", enabled: true },
    { id: "m3", name: "A Quality World Map", version: "9.0.1", author: "IcePenguin", category: null, nexusModId: 5804, state: "installed", enabled: false },
    { id: "m4", name: "Static Mesh Improvement Mod", version: "2.08", author: "Brumbek", category: null, nexusModId: 659, state: "installed", enabled: true },
    { id: "m5", name: "Immersive Citizens", version: "0.4.0.3", author: "Shurah", category: null, nexusModId: 8429, state: "downloaded", enabled: false },
    { id: "m6", name: "Cathedral Weathers", version: "2.25", author: "JonnyWang", category: null, nexusModId: 24791, state: "installed", enabled: false },
  ];
  if (msg.cmd === "modsToggle") { const m = mods.find(x => x.id === msg.modId); if (m) m.enabled = !!msg.enabled; }
  if (msg.cmd === "modsRemove") { const i = mods.findIndex(x => x.id === msg.modId); if (i >= 0) mods.splice(i, 1); }
  // Hollowmark carries the fallback installer's question (the labels are Vortex's own) and a
  // warning, so the prompt rows and the answer list can be walked in the preview.
  const justManaged = msg.cmd === "modsManage";
  const prompts = mockHandle._prompts[g.id] !== undefined && !justManaged ? mockHandle._prompts[g.id]
    : justManaged ? [{
        id: "dlgDeploy", type: "question", title: "Deployment Method",
        message: "Vortex needs to know how to deploy mods for this game. Hardlink deployment is recommended.",
        actions: ["Cancel", "Hardlink Deployment", "Symlink Deployment", "Move Deployment"], defaultAction: "Hardlink Deployment", answerable: true,
      }]
    : g.title === "Hollowmark: Second Ascent" ? [{
        id: "dlg1", type: "question", title: "You Have Reached The Fallback Installer!",
        message: "The archive does not match any layout the Cyberpunk 2077 extension knows. Install it as it is?",
        actions: ["No, Cancel Installation", "Yes, Install To Staging Anyway", "Yes, Install And Don't Ask Again"],
        defaultAction: null, answerable: true,
      }] : [];
  reply({ state: "ready", game: vg, mods: mods.map(m => ({ ...m })), prompts,
    notices: g.title === "Hollowmark: Second Ascent" ? ["Unsolved conflicts: 2 mods conflict"] : [] });
}

/* The preview's update: a check finds 1.6.0, Install downloads it in a few steps and ends ready,
   so every state of the Settings row can be seen without a release to fetch. */
const mockUpdate = { state: "upToDate", current: "1.5.0", latest: "1.5.0", progress: 0, message: null, userAsked: false };
function mockUpdateStep(patch, delay) {
  setTimeout(() => {
    Object.assign(mockUpdate, patch, { userAsked: true });
    handleHostMessage({ type: "update", status: { ...mockUpdate } });
  }, delay);
}

/* Play sessions and achievements, as the host would answer them. Deterministic, so a reload
   shows the same numbers: sessions over the last two months for the games that have playtime,
   and a list per game for a handful of them -- one complete, one untouched, one a ROM's from
   RetroAchievements with points, one Xbox's with gamerscore. Most unlocks land inside one of the
   game's sessions, the way they do for real, so the Day sheet and the session charts have
   something to join. */
const mockAct = (() => {
  let seed = 7;
  const rnd = () => { seed = (seed * 1103515245 + 12345) & 0x7fffffff; return seed / 0x7fffffff; };
  const ids = {};
  const sessions = [];
  const id = (t, p) => p.toLowerCase() + ":" + t.toLowerCase().replace(/[^a-z]/g, "");
  const plan = [
    ["Hollowmark: Second Ascent", "Steam", 41, true], ["Ridgeline 84", "Epic", 9, false], ["Salt & Tide", "GOG", 30, true],
    ["Foundry Nine", "Manual", 5, false], ["Cassette Run", "Steam", 3, false], ["Nightpost", "Steam", 2, false],
    ["Umber Fields", "Steam", 4, false], ["Marrow", "GOG", 7, false], ["Super Mario World", "Super Nintendo", 6, false],
    ["Forza Horizon 5", "Xbox", 18, true],
  ];
  const now = Date.now();
  plan.forEach(([title, platform, count, rich], gi) => {
    const gameId = id(title, platform);
    ids[title] = gameId;
    for (let i = 0; i < count; i++) {
      const daysAgo = Math.floor(rnd() * 60);
      const start = new Date(now - daysAgo * 86400000 - Math.floor(rnd() * 6) * 3600000 - 3600000 * 2);
      const seconds = Math.round((15 + rnd() * 150) * 60);
      const fps = rich ? Math.round(55 + rnd() * 70) : null;
      sessions.push({
        id: "s" + gi + "_" + i, gameId, start: start.toISOString(), end: new Date(start.getTime() + seconds * 1000).toISOString(),
        seconds, samples: Math.min(720, Math.floor(seconds / 5)),
        avgFps: fps, lowFps: fps ? Math.round(fps * 0.72) : null,
        avgCpu: Math.round(25 + rnd() * 40), avgGpu: Math.round(60 + rnd() * 38), avgRam: Math.round(45 + rnd() * 30), peakRamMb: Math.round(9000 + rnd() * 6000),
        avgCpuTemp: rich ? Math.round(55 + rnd() * 20) : null, maxCpuTemp: rich ? Math.round(78 + rnd() * 12) : null,
        avgGpuTemp: rich ? Math.round(60 + rnd() * 15) : null, maxGpuTemp: rich ? Math.round(76 + rnd() * 10) : null,
        avgCpuPower: rich ? Math.round(60 + rnd() * 50) : null, avgGpuPower: rich ? Math.round(180 + rnd() * 120) : null,
        sources: rich ? "RivaTuner Statistics Server, HWiNFO" : null,
      });
    }
  });
  const sets = {};
  const unlockTime = (gameId) => {
    const mine = sessions.filter(s => s.gameId === gameId);
    if (mine.length && rnd() < 0.75) {
      const s = mine[Math.floor(rnd() * mine.length)];
      return new Date(new Date(s.start).getTime() + rnd() * s.seconds * 1000).toISOString();
    }
    return new Date(now - Math.floor(rnd() * 240) * 86400000 - Math.floor(rnd() * 86400000)).toISOString();
  };
  const makeSet = (title, source, total, unlocked, opts = {}) => {
    const gameId = ids[title] || id(title, opts.platform || "Steam");
    const seedName = title.toLowerCase().replace(/[^a-z]/g, "");
    const items = [];
    for (let i = 0; i < total; i++) {
      const got = i < unlocked;
      const pct = Math.round((got ? 4 + rnd() * 80 : 0.3 + rnd() * 25) * 10) / 10;
      items.push({
        id: seedName + "_" + i, name: `${opts.names ? opts.names[i % opts.names.length] : "Achievement"} ${i + 1}`,
        description: i % 7 === 3 ? null : `A placeholder description for what number ${i + 1} takes, long enough to wrap to a second line on the wider rows.`,
        hidden: i % 6 === 5, unlocked: got,
        unlockedAt: got ? unlockTime(gameId) : null,
        percent: pct, score: source === "xbox" ? (i % 5 === 0 ? 50 : 15) : source === "retro" ? (i % 4 === 0 ? 25 : 5) : source === "epic" ? 10 : null,
        icon: `https://picsum.photos/seed/${seedName}a${i}/128/128`, iconLocked: null, iconFile: null, iconLockedFile: null,
      });
    }
    sets[gameId] = { gameId, source, sourceGameId: null, sourceName: opts.sourceName || null, fetchedAt: new Date(now - 7 * 60000).toISOString(), error: opts.error || null,
      unlocked, total, score: items.filter(a => a.unlocked).reduce((n, a) => n + (a.score || 0), 0), totalScore: items.reduce((n, a) => n + (a.score || 0), 0), items };
  };
  const names = ["First Steps", "Deep Breath", "Untouchable", "Collector", "Night Owl", "Speedrunner", "Pacifist", "Hoarder"];
  makeSet("Hollowmark: Second Ascent", "steam", 63, 41, { names });
  makeSet("Salt & Tide", "gog", 12, 12, { names });
  makeSet("Cassette Run", "steam", 20, 0, { names });
  makeSet("Nightpost", "steam", 30, 5, { names });
  makeSet("Ridgeline 84", "epic", 25, 3, { names });
  makeSet("Forza Horizon 5", "xbox", 50, 20, { names, platform: "Xbox" });
  makeSet("Super Mario World", "retro", 30, 8, { names, platform: "Super Nintendo", sourceName: "Super Mario World" });
  return { sessions, sets, ids };
})();
function mockAchSummaries() {
  const out = {};
  Object.values(mockAct.sets).forEach(s => { if (s.total > 0) out[s.gameId] = { unlocked: s.unlocked, total: s.total, score: s.score, totalScore: s.totalScore, lastUnlock: s.items.filter(a => a.unlockedAt).map(a => a.unlockedAt).sort().pop() || null, source: s.source, fetchedAt: s.fetchedAt, error: s.error }; });
  return out;
}
function mockSamples(session) {
  const out = [];
  const n = Math.min(300, Math.max(12, Math.floor(session.seconds / 5)));
  for (let i = 0; i < n; i++) {
    const t = Math.round(session.seconds * i / (n - 1));
    const wave = Math.sin(i / 9) * 0.5 + Math.sin(i / 3.7) * 0.3;
    out.push({
      t, fps: session.avgFps ? Math.round(session.avgFps + wave * 18 + (i % 23 === 0 ? -25 : 0)) : null,
      cpu: Math.round(session.avgCpu + wave * 14), gameCpu: Math.round(session.avgCpu * 0.6 + wave * 10),
      gpu: Math.round(Math.min(100, session.avgGpu + wave * 12)), ram: Math.round(session.avgRam + i / n * 8), ramMb: session.peakRamMb - 800 + Math.round(i / n * 800),
      cpuTemp: session.avgCpuTemp ? Math.round(session.avgCpuTemp + wave * 6 + i / n * 4) : null, gpuTemp: session.avgGpuTemp ? Math.round(session.avgGpuTemp + wave * 4 + i / n * 6) : null,
      cpuPower: session.avgCpuPower ? Math.round(session.avgCpuPower + wave * 20) : null, gpuPower: session.avgGpuPower ? Math.round(session.avgGpuPower + wave * 40) : null,
    });
  }
  return out;
}
/* One game's dated unlocks, oldest first, as UiBridge.UnlocksFor sends them. */
function mockUnlocks(gameId) {
  const set = mockAct.sets[gameId];
  return set ? set.items.filter(a => a.unlocked && a.unlockedAt).map(a => ({ at: a.unlockedAt, item: a })).sort((a, b) => new Date(a.at) - new Date(b.at)) : [];
}
function mockAchAll() {
  const sets = Object.values(mockAct.sets);
  const title = (id) => (S.games.find(g => g.id === id) || {}).title || id;
  const dated = sets.flatMap(s => s.items.filter(a => a.unlocked && a.unlockedAt).map(a => ({ gameId: s.gameId, title: title(s.gameId), item: a, at: a.unlockedAt })))
    .sort((a, b) => new Date(b.at) - new Date(a.at));
  const since = new Date(); since.setHours(0, 0, 0, 0); since.setDate(since.getDate() - 35);
  const recent = dated.filter((x, i) => i < 60 || new Date(x.at) >= since).slice(0, 1500);
  const unlocksByDay = {};
  dated.forEach(x => { const k = dayKey(new Date(x.at)); unlocksByDay[k] = (unlocksByDay[k] || 0) + 1; });
  const months = [];
  const nowD = new Date();
  for (let i = 11; i >= 0; i--) { const d = new Date(nowD.getFullYear(), nowD.getMonth() - i, 1); months.push({ key: monthKey(d), label: `${d.toLocaleString("en", { month: "short" })} ${String(d.getFullYear()).slice(2)}`, value: 0 }); }
  sets.forEach(s => s.items.forEach(a => { if (!a.unlockedAt) return; const m = months.find(x => x.key === monthKey(new Date(a.unlockedAt))); if (m) m.value++; }));
  return {
    type: "achievementsAll",
    games: sets.map(s => ({ gameId: s.gameId, title: title(s.gameId), summary: mockAchSummaries()[s.gameId] || { unlocked: 0, total: 0, score: 0, totalScore: 0, lastUnlock: null, source: s.source, fetchedAt: s.fetchedAt, error: null } })),
    recent,
    unlocksByDay,
    unlockedPercents: sets.flatMap(s => s.items.filter(a => a.unlocked).map(a => a.percent)),
    unlocksByMonth: months,
    providers: [{ source: "steam", blocked: null, games: 8 }, { source: "xbox", blocked: "Sign in to Xbox under Settings → Library to see achievements", games: 4 },
      { source: "epic", blocked: null, games: 3 }, { source: "gog", blocked: "Sign in to GOG under Settings → Library to see achievements", games: 2 }, { source: "retro", blocked: null, games: 4 }],
  };
}

/* Rest mode in the preview: awake, nothing frozen, and a wake report shaped like this PC's --
   one controller receiver that could be allowed, a Bluetooth radio that is, and the sign-in on. */
const mockRest = {
  phase: "awake", paused: false,
  wake: {
    canSleep: true, modernStandby: false, signInOnWake: true,
    devices: [
      { name: "Xbox Wireless Adapter for Windows", armed: false, kind: "controller" },
      { name: "Intel(R) Wireless Bluetooth(R)", armed: true, kind: "bluetooth" },
    ],
    lastWake: "HID Keyboard Device",
  },
};
function mockRestPayload() { return { phase: mockRest.phase, paused: mockRest.paused, wake: { ...mockRest.wake, devices: mockRest.wake.devices.map(d => ({ ...d })) } }; }
/* The game message as the host sends it after a pause or a thaw. */
function mockGamePush() {
  if (!mockHandle._running) return;
  handleHostMessage({ type: "game", running: true, id: mockHandle._running, since: new Date(Date.now() - 72 * 60000).toISOString(), paused: mockRest.paused });
}

/* Preview only: U shows an unlock card, I opens the in-game menu over a "running" Hollowmark with
   live readings, K closes that game. */
if (!HOST) window.addEventListener("keydown", (e) => {
  if (inputOpen || keyPick || captureState || e.ctrlKey || e.altKey || e.metaKey) return;
  const hollow = mockAct.ids["Hollowmark: Second Ascent"];
  if (e.key === "u") {
    const set = mockAct.sets[hollow];
    handleHostMessage({ type: "achievementsUnlocked", id: hollow, title: "Hollowmark: Second Ascent", items: set.items.slice(41, 43).map(a => ({ ...a, unlocked: true })) });
  }
  if (e.key === "i" && !overlayMode) {
    mockHandle._running = hollow;
    const since = new Date(Date.now() - 72 * 60000).toISOString();
    handleHostMessage({ type: "game", running: true, id: hollow, since });
    handleHostMessage({ type: "overlay", mode: "ingame", targetTitle: "Hollowmark", targetProcess: "hollowmark", targetIsGame: true, shot: null, windows: mockWindows, runningGameId: hollow });
    clearInterval(mockHandle._telemetry);
    let tick = 0;
    const push = () => handleHostMessage({ type: "telemetry", id: hollow, since, sample: { t: 4320 + tick * 2, fps: 118 + Math.round(Math.sin(tick / 2) * 9), cpu: 41 + (tick % 5), gpu: 93 - (tick % 4), ram: 61, gpuTemp: 71, cpuTemp: 64 }, fpsSource: "RivaTuner Statistics Server", sensorSource: "HWiNFO" });
    push();
    mockHandle._telemetry = setInterval(() => { tick++; if (!ingameOpen) { clearInterval(mockHandle._telemetry); return; } push(); }, 2000);
  }
  if (e.key === "k" && mockHandle._running) {
    mockHandle._running = null;
    handleHostMessage({ type: "game", running: false, id: null });
  }
});

function mockHandle(msg) {
  const pushState = () => {
    const seedW = (t) => `https://picsum.photos/seed/${t.toLowerCase().replace(/[^a-z]/g, "")}w/600/340`;
    const seedP = (t) => `https://picsum.photos/seed/${t.toLowerCase().replace(/[^a-z]/g, "")}/400/480`;
    const seedH = (t) => `https://picsum.photos/seed/${t.toLowerCase().replace(/[^a-z]/g, "")}h/1200/390`;
    // Real store trailers (Hollow Knight, Celeste, Stardew Valley, Cyberpunk 2077, at 480p) on a
    // few games only: the rest of the library is what a game with no Steam listing looks like,
    // which is the common case for ROMs and the one most likely to be got wrong.
    const clips = [256679401, 256706951, 256815967, 257082775];
    let clipN = 0;
    const clipUrl = (id) => `https://video.akamai.steamstatic.com/store_trailers/${id}/movie480.mp4`;
    const clip = () => clipUrl(clips[clipN++ % clips.length]);
    // A gallery for two of them: Hollowmark's is what a Steam page gives (its films, then its
    // screenshots, real ones from Hollow Knight's page), Pokemon's is what IGDB gives a ROM.
    const ss = (h) => `https://shared.akamai.steamstatic.com/store_item_assets/steam/apps/367520/ss_${h}?t=1776125684`;
    const shot = (t, n) => `https://picsum.photos/seed/${t}${n}/1920/1080`;
    const mediaHollow = [
      { kind: "video", url: clipUrl(256679401), thumb: ss("5384f9f8b96a0b9934b2bc35a4058376211636d2.600x338.jpg"), name: "Release Trailer" },
      { kind: "video", url: clipUrl(256706951), thumb: ss("d5b6edd94e77ba6db31c44d8a3c09d807ab27751.600x338.jpg"), name: "Gameplay" },
      { kind: "image", url: ss("5384f9f8b96a0b9934b2bc35a4058376211636d2.1920x1080.jpg"), thumb: ss("5384f9f8b96a0b9934b2bc35a4058376211636d2.600x338.jpg") },
      { kind: "image", url: ss("d5b6edd94e77ba6db31c44d8a3c09d807ab27751.1920x1080.jpg"), thumb: ss("d5b6edd94e77ba6db31c44d8a3c09d807ab27751.600x338.jpg") },
      ...[1, 2, 3, 4, 5].map(n => ({ kind: "image", url: shot("hollow", n), thumb: shot("hollow", n) })),
    ];
    const mediaPokemon = [
      { kind: "video", url: "https://www.youtube.com/watch?v=TEXsORWDFNY", thumb: "https://i.ytimg.com/vi/TEXsORWDFNY/hqdefault.jpg", name: "Trailer" },
      ...[1, 2, 3].map(n => ({ kind: "image", url: shot("emerald", n), thumb: shot("emerald", n) })),
    ];
    const g = (title, platform, opts = {}) => ({
      id: platform.toLowerCase() + ":" + title.toLowerCase().replace(/[^a-z]/g, ""),
      title, platform, installed: true, manual: platform === "Manual",
      playtimeMinutes: 0, sessions: 0, lastPlayed: null, sizeBytes: 0,
      favorite: false, hidden: false, preferDirectLaunch: false, args: null, installUri: null,
      coverFile: seedP(title), bannerFile: seedW(title), heroFile: seedH(title),
      // Stand-in metadata, so the preview exercises the detail page's facts row. Individual
      // entries below override it -- including back to nothing, which is what a game we could
      // not fetch looks like and the case most likely to be got wrong.
      description: "A placeholder blurb standing in for the store's own two-sentence pitch, long "
        + "enough to show where a real one wraps and where the clamp takes over.",
      developer: "Northmoor Studio", publisher: "Northmoor",
      genres: ["Action", "Adventure", "Indie"], releaseDate: "Mar 12, 2021",
      criticScore: 82, criticSource: "Metacritic", controllerSupport: "full",
      // Both boards, each with its own reasons, so the Age rating option visibly changes the page.
      esrbRating: "M", esrbDescriptors: ["Blood and Gore", "Intense Violence", "Strong Language"],
      pegiRating: 18, pegiDescriptors: ["Violence", "Bad Language", "In-game purchases"], ...opts,
    });
    const now = Date.now();
    const games = [
      g("Hollowmark: Second Ascent", "Steam", { playtimeMinutes: 4934, sessions: 41, favorite: true, trailerUrl: clip(), media: mediaHollow, lastPlayed: new Date(now - 86400000).toISOString(), sizeBytes: 64.2 * 1024 ** 3, installDir: "C:\\Games\\Steam\\steamapps\\common\\Hollowmark" }),
      // No fetched metadata at all -- the facts row has to fall back to the platform and the
      // description has to collapse rather than leave a gap under the title.
      g("Ridgeline 84", "Epic", { playtimeMinutes: 660, sessions: 9, lastPlayed: new Date(now - 2 * 86400000).toISOString(), sizeBytes: 31 * 1024 ** 3, description: null, developer: null, publisher: null, genres: [], releaseDate: null, criticScore: null, criticSource: null, controllerSupport: null, esrbRating: null, pegiRating: null }),
      g("Salt & Tide", "GOG", { playtimeMinutes: 2820, sessions: 30, favorite: true, trailerUrl: clip(), lastPlayed: new Date(now - 3 * 86400000).toISOString(), sizeBytes: 12 * 1024 ** 3, criticScore: 61, controllerSupport: "partial", esrbRating: "T", esrbDescriptors: ["Fantasy Violence", "Mild Language"], pegiRating: null }),
      g("Foundry Nine", "Manual", { playtimeMinutes: 360, sessions: 5, lastPlayed: new Date(now - 4 * 86400000).toISOString(), sizeBytes: 8 * 1024 ** 3, trailerUrl: "https://www.youtube.com/watch?v=TEXsORWDFNY", criticScore: 38, controllerSupport: null, esrbRating: null, pegiRating: 7, pegiDescriptors: ["Violence"] }),
      g("Cassette Run", "Steam", { playtimeMinutes: 180, trailerUrl: clip(), sessions: 3, lastPlayed: new Date(now - 5 * 86400000).toISOString(), sizeBytes: 4 * 1024 ** 3 }),
      // enough recently-played entries to exercise the Continue carousel
      g("Nightpost", "Steam", { playtimeMinutes: 95, trailerUrl: clip(), sessions: 2, lastPlayed: new Date(now - 6 * 86400000).toISOString() }),
      g("Umber Fields", "Steam", { playtimeMinutes: 210, sessions: 4, lastPlayed: new Date(now - 7 * 86400000).toISOString() }),
      g("The Quiet Shore", "Epic", { playtimeMinutes: 140, sessions: 3, lastPlayed: new Date(now - 8 * 86400000).toISOString() }),
      g("Vector Bloom", "Steam", { playtimeMinutes: 60, sessions: 1, lastPlayed: new Date(now - 9 * 86400000).toISOString() }),
      g("Marrow", "GOG", { playtimeMinutes: 480, sessions: 7, lastPlayed: new Date(now - 10 * 86400000).toISOString() }),
      g("Halden Court", "Manual", { playtimeMinutes: 30, sessions: 1, lastPlayed: new Date(now - 11 * 86400000).toISOString() }),
      g("Tin Orchard", "Epic", { playtimeMinutes: 75, sessions: 2, lastPlayed: new Date(now - 12 * 86400000).toISOString() }),
      g("Paper Lanterns", "Epic"), g("Ninefold", "Manual"),
      g("Bellwether", "GOG"),
      g("Iron Compass", "Steam", { installed: false, sizeBytes: 42 * 1024 ** 3, installUri: "steam://install/1" }),
      g("Low Country", "GOG", { installed: false, installUri: "goggalaxy://openGameView/1" }),
      g("Solaria Drift", "Epic", { installed: false, installUri: "com.epicgames.launcher://apps/Solaria?action=install" }),
      g("Hollow Reef", "Steam", { installed: false, sizeBytes: 27 * 1024 ** 3, installUri: "steam://install/2" }),
      g("Glassmoor", "Manual", { installed: false, sizeBytes: 55 * 1024 ** 3 }),
      g("Tidewrack", "GOG", { installed: false, sizeBytes: 12 * 1024 ** 3 }),
      g("Forza Horizon 5", "Xbox", { playtimeMinutes: 1240, sessions: 18, sizeBytes: 110 * 1024 ** 3 }),
      g("Sea of Thieves", "Xbox", { sizeBytes: 78 * 1024 ** 3 }),
      // The same game in two stores, to exercise the grouping: one tile, launches the installed Xbox copy.
      g("Forza Horizon 5", "Steam", { installed: false, installUri: "steam://install/1551360" }),
      g("Hollow Reef", "Xbox", { installed: false, installUri: "ms-windows-store://pdp/?productid=9XXXXXXXXXXX" }),
      g("Starfield", "Xbox", { installed: false, installUri: "ms-windows-store://pdp/?productid=9NCJSXWZRJPS" }),
      g("Wallpaper Engine", "Steam", { hidden: true, sizeBytes: 2 * 1024 ** 3 }),
      // ROMs: the platform is the system, and they group with nothing.
      g("Super Mario World", "Super Nintendo", { emulated: true, platformId: "snes", romFolderId: "f1", romPath: "D:\\ROMs\\SNES\\Super Mario World (USA).sfc", playtimeMinutes: 420, sessions: 6, lastPlayed: new Date(now - 86400000 * 1.5).toISOString(), sizeBytes: 512 * 1024, releaseDate: "Nov 21, 1990", developer: "Nintendo EAD", publisher: "Nintendo", genres: ["Platform"], criticScore: null, criticSource: null }),
      g("Chrono Trigger", "Super Nintendo", { emulated: true, platformId: "snes", romFolderId: "f1", romPath: "D:\\ROMs\\SNES\\Chrono Trigger (USA).sfc", sizeBytes: 4 * 1024 ** 2, releaseDate: "Mar 11, 1995", genres: ["RPG"], criticScore: null, criticSource: null }),
      g("Doom", "Super Nintendo", { emulated: true, platformId: "snes", romFolderId: "f1", romPath: "D:\\ROMs\\SNES\\Doom (USA).sfc", sizeBytes: 2 * 1024 ** 2, description: null, developer: null, publisher: null, genres: [], releaseDate: null, criticScore: null, criticSource: null, controllerSupport: null, esrbRating: null, pegiRating: null }),
      g("Final Fantasy VII", "PlayStation", { emulated: true, platformId: "ps1", romFolderId: "f2", romPath: "D:\\ROMs\\PS1\\Final Fantasy VII (USA).m3u", sizeBytes: 1.3 * 1024 ** 3, releaseDate: "Jan 31, 1997", genres: ["RPG"] }),
      g("Crash Bandicoot", "PlayStation", { emulated: true, platformId: "ps1", romFolderId: "f2", emulatorId: "e2", romPath: "D:\\ROMs\\PS1\\Crash Bandicoot (USA).chd", sizeBytes: 320 * 1024 ** 2 }),
      g("sf2", "Arcade", { emulated: true, platformId: "arcade", romFolderId: "f3", romPath: "D:\\ROMs\\Arcade\\sf2.zip", sizeBytes: 3 * 1024 ** 2, description: null, developer: null, publisher: null, genres: [], releaseDate: null, criticScore: null, criticSource: null, controllerSupport: null, esrbRating: null, pegiRating: null, coverFile: null, bannerFile: null, heroFile: null }),
      // A ROM: not on Steam, so its trailer is IGDB's YouTube video (this is the real id the
      // service answers for it) and plays through YouTube's player rather than a <video>.
      g("Pokemon: Emerald Version", "Game Boy Advance", { emulated: true, platformId: "gba", romFolderId: "f4", romPath: "C:\\RetroArch\\downloads\\GBA\\Pokemon - Emerald Version (USA, Europe).gba", sizeBytes: 16 * 1024 ** 2, releaseDate: "Sep 16, 2004", genres: ["RPG"], trailerUrl: "https://www.youtube.com/watch?v=TEXsORWDFNY", media: mediaPokemon }),
    ];
    if (mockHandle._titles) games.forEach(x => { if (mockHandle._titles[x.id]) x.title = mockHandle._titles[x.id]; });
    if (mockHandle._emus) games.forEach(x => { if (mockHandle._emus[x.id] !== undefined) x.emulatorId = mockHandle._emus[x.id] || null; });
    if (mockHandle._fav) games.forEach(x => { if (mockHandle._fav[x.id] !== undefined) x.favorite = mockHandle._fav[x.id]; });
    if (mockHandle._running) { S.gameRunning = true; S.runningGameId = mockHandle._running; }
  if (mockHandle._hidden) games.forEach(x => { if (mockHandle._hidden[x.id] !== undefined) x.hidden = mockHandle._hidden[x.id]; });
    handleHostMessage({
      type: "state",
      games,
      collections: mockCollections,
      achievements: mockAchSummaries(),
      sessionStart: mockHandle._running ? new Date(Date.now() - 72 * 60000).toISOString() : null,
      steamAccount: { steamId: "76561198000000000", personaName: "couchplayer", ownedCount: 212,
        fetchedAt: new Date().toISOString(), error: null },
      stores: {
        epic: { signedIn: true, user: "couchplayer", count: 35, fetchedAt: new Date().toISOString(), error: null },
        gog: { signedIn: false, user: null, count: 0, fetchedAt: null, error: null },
        xbox: { signedIn: true, user: "CouchGamer", count: 12, fetchedAt: null, error: "The sign-in has expired. Sign in again" },
        gamePass: { count: 0, fetchedAt: null, error: null },
      },
      emulation: mockEmulation,
      actions: mockActions,
      xboxButton: { ...mockXboxButton },
      mods: { installed: true, path: "C:\\Users\\couch\\AppData\\Local\\Programs\\Vortex\\Vortex.exe", version: "1.13.7", running: true },
      update: mockUpdate,
      settings: S.settings || {
        tvDeviceName: "\\\\.\\DISPLAY2", switchPrimaryOnLaunch: true, repositionGameWindow: true,
        keepFocus: true, launchOnStartup: false, gamepadMouseEnabled: true, gamepadMouseDuringGame: false,
        deadzone: 0.18, sensitivity: 1.0, accelExponent: 1.8, hideCursorSystemWide: false,
        touchpadMouse: true, touchpadSensitivity: 1.0, touchpadTapToClick: true,
        touchpadTapDrag: true, touchpadNaturalScroll: true, touchpadScrollSpeed: 1.0,
        boostButton: "RT", boostMultiplier: 2.5, hideLegend: false, igdbClientId: "", igdbClientSecret: "", steamGridDbKey: "", metadataEndpoint: "",
        steamShowOwned: true, steamApiKey: "", gamePassCatalog: false, xboxClientId: "", detectEmulators: true, autoUpdate: true,
        leftClickButton: "A", rightClickButton: "B",
        minimizeCombo: "LS + RS", menuComboMode: "TapHold", screenshotCombo: "Off", shareButtonScreenshot: true,
        keyboardToggleButton: "Back", keyboardToggleMode: "Press", keyboardToggleHoldMs: 600,
        keyboardApp: "Builtin", keyboardScale: 1.0, keyRepeatDelayMs: 350, keyRepeatIntervalMs: 90,
        keyboardSuggestions: true, keyboardFunctionKeys: false, keyboardNavKeys: false,
        keyboardNumpad: false, keyboardModifiers: false,
        accentColor: "#F0A253", theme: "", cacheTrailers: true, ageRatingBoard: "ESRB",
        activityTracking: true, activityHardware: true, activitySampleSeconds: 5,
        achievementsEnabled: true, achievementNotifications: true, achievementsOnTiles: true,
        retroAchievementsUser: "", retroAchievementsKey: "",
        restAfterMinutes: 60, restDuringGame: true, restPausesGame: true, sleepAfterRestMinutes: -1,
        animationsEnabled: true, animationSpeed: 1.0, themeSettings: {},
        // The first-run setup opens only with ?onboard in the preview's address, so every other
        // screen can still be walked straight away.
        onboardingVersion: /[?&]onboard\b/.test(location.search) ? 0 : 1,
      },
      rest: mockRestPayload(),
      displays: [
        { deviceName: "\\\\.\\DISPLAY1", friendlyName: "Dell U2723QE", x: 0, y: 0, width: 3840, height: 2160, isPrimary: true },
        { deviceName: "\\\\.\\DISPLAY2", friendlyName: "LG C3 OLED", x: 3840, y: 0, width: 3840, height: 2160, isPrimary: false },
      ],
      startupRegistered: false, gameRunning: false, runningGameId: null, scanning: false,
    });
  };

  if (msg.cmd === "ready") {
    setTimeout(pushState, 60);
    // The bundled theme, read off the same server, so the preview's Appearance screen has a theme
    // with options to show and the Loungepad theme can be switched to. The server's root is
    // Loungepad/, which is what makes /themes/loungepad/… reachable at all; a server rooted
    // anywhere else just leaves the list at Shelf.
    fetch("/themes/loungepad/theme.json").then(r => (r.ok ? r.json() : null)).then(t => {
      if (!t || typeof t !== "object") return;
      const v = Date.now();
      handleHostMessage({ type: "themes", themes: [
        { id: "", name: "Shelf" },
        { ...t, id: "loungepad", css: `/themes/loungepad/theme.css?v=${v}`, html: `/themes/loungepad/theme.html?v=${v}` },
      ] });
    }).catch(() => {});
    setTimeout(() => { handleHostMessage({ type: "padConnected", connected: true }); handleHostMessage({ type: "battery", present: true, percent: 62, charging: false, level: 2 }); }, 700);
    setTimeout(() => handleHostMessage({ type: "padLayout", layout: "xbox", name: "Xbox Wireless Controller" }), 750);
    mockScanProgress();
  } else if (msg.cmd === "onboardingProbe") {
    setTimeout(() => {
      handleHostMessage({ type: "onboarding", launchers: { steam: true, epic: true, galaxy: false, xboxApp: true },
        playnite: { found: true, dir: mockPlaynite.dir }, pin: !!mockHandle._pin, vortex: mockVortex().vortex });
      handleHostMessage({ type: "onboardingVortex", busy: false, mode: "peek", ...mockVortex() });
    }, 300);
  } else if (msg.cmd === "onboardingPin") {
    handleHostMessage({ type: "onboardingPin", pin: !!mockHandle._pin });
  } else if (msg.cmd === "onboardingSignInOptions") {
    toast("(preview) Windows Settings would open at Sign-in options, on the TV");
    setTimeout(() => { mockHandle._pin = true; }, 1500);
  } else if (msg.cmd === "onboardingVortex") {
    // Connecting to a running Vortex needs one restart, the way a Vortex that was open before the
    // extension arrived does; the restart then answers with the games.
    handleHostMessage({ type: "onboardingVortex", busy: true, mode: msg.mode });
    if (msg.mode === "connect" && mockHandle._vortex !== "ready") mockHandle._vortex = "needsRestart";
    if (msg.mode === "restart") mockHandle._vortex = "ready";
    setTimeout(() => handleHostMessage({ type: "onboardingVortex", busy: false, mode: msg.mode, ...mockVortex() }), msg.mode === "peek" ? 200 : 1600);
  } else if (msg.cmd === "updateCheck") {
    mockUpdateStep({ state: "checking" }, 0);
    mockUpdateStep({ state: "available", latest: "1.6.0" }, 700);
  } else if (msg.cmd === "updateInstall") {
    if (mockUpdate.state === "ready") { toast("(preview) would restart on " + mockUpdate.latest); return; }
    [0, 30, 65, 100].forEach((p, i) => mockUpdateStep({ state: "downloading", progress: p }, i * 400));
    mockUpdateStep({ state: "ready" }, 1800);
  } else if (msg.cmd === "cacheTrailer") {
    // The host would download the file and answer with its name; here the "file" is the same
    // URL, which is enough to walk the cached path (trailerUrl prefers trailerFile).
    const g = S.games.find(x => x.id === msg.id);
    if (g && g.trailerUrl && !g.trailerFile)
      setTimeout(() => handleHostMessage({ type: "trailerCached", id: msg.id, file: g.trailerUrl }), 1500);
  } else if (msg.cmd === "fetchMedia") {
    // The host would ask Steam or IGDB; here a game with no gallery gets stand-in pictures a
    // moment later, which is what an uninstalled game's page looks like filling in.
    const g = S.games.find(x => x.id === msg.id);
    if (g && !(g.media && g.media.length)) {
      const seed = g.title.toLowerCase().replace(/[^a-z]/g, "");
      const media = [1, 2, 3, 4, 5, 6].map(n => ({ kind: "image", url: `https://picsum.photos/seed/${seed}g${n}/1920/1080`, thumb: `https://picsum.photos/seed/${seed}g${n}/600/338` }));
      setTimeout(() => handleHostMessage({ type: "media", id: msg.id, media }), 900);
    }
  } else if (msg.cmd === "launch") {
    toast("(preview) would launch " + msg.id);
  } else if (msg.cmd === "toggleFavorite") {
    mockHandle._fav = mockHandle._fav || {};
    const cur = (S.games.find(g => g.id === msg.id) || {}).favorite;
    mockHandle._fav[msg.id] = !cur;
    pushState();
  } else if (msg.cmd === "toggleHidden") {
    mockHandle._hidden = mockHandle._hidden || {};
    const cur = (S.games.find(g => g.id === msg.id) || {}).hidden;
    mockHandle._hidden[msg.id] = !cur;
    pushState();
  } else if (msg.cmd === "modsOpen" || msg.cmd === "modsToggle" || msg.cmd === "modsRemove" || msg.cmd === "modsRestartVortex" || msg.cmd === "modsAnswer") {
    mockMods(msg);
  } else if (msg.cmd === "modsGetVortex") {
    toast("(preview) would open the Vortex download page in the browser");
  } else if (msg.cmd === "modsShowVortex") {
    toast("(preview) would bring Vortex to the TV");
  } else if (msg.cmd === "modsManage") {
    // Setting up lands the game in the ready state with Vortex's one question on top.
    mockHandle._managed = mockHandle._managed || {};
    mockHandle._managed[msg.id] = true;
    mockMods(msg);
  } else if (msg.cmd === "modsBrowse") {
    toast(msg.extensionModId ? `(preview) would open the extension's page, site mod #${msg.extensionModId}`
      : msg.site ? "(preview) would open the site's Vortex extensions"
      : msg.nexusModId ? `(preview) would open mod #${msg.nexusModId} on ${msg.nexusDomain || "the game's section"}`
      : "(preview) would open the game's section on nexusmods.com");
  } else if (msg.cmd === "modsExtension") {
    toast(`(preview) would open Vortex's extension browser on #${msg.modId} and bring Vortex to the TV`);
  } else if (msg.cmd === "addManual") {
    toast("(preview) would open the file picker");
  } else if (msg.cmd === "createCollection") {
    mockCollections.push({ id: "c" + (mockCollections.length + 1), name: msg.name, gameIds: msg.gameId ? [msg.gameId] : [] });
    pushState();
  } else if (msg.cmd === "toggleInCollection") {
    const c = mockCollections.find(x => x.id === msg.collectionId);
    if (c) { const i = c.gameIds.indexOf(msg.id); if (i >= 0) c.gameIds.splice(i, 1); else c.gameIds.push(msg.id); }
    pushState();
  } else if (msg.cmd === "deleteCollection") {
    const i = mockCollections.findIndex(x => x.id === msg.id);
    if (i >= 0) mockCollections.splice(i, 1);
    pushState();
  } else if (msg.cmd === "listWindows") {
    handleHostMessage({ type: "windows", windows: mockWindows });
  } else if (msg.cmd === "windowAction") {
    toast("(preview) window " + msg.action);
  } else if (msg.cmd === "shortcut") {
    toast(msg.id === "sleepPc" ? "(preview) the PC would sleep now, with the game frozen first" : "(preview) shortcut " + msg.id);
  } else if (msg.cmd === "power") {
    toast("(preview) power " + msg.action);
  } else if (msg.cmd === "closeOverlay" || msg.cmd === "goHome") {
    /* host-side window juggling; nothing to do in the browser preview */
  } else if (msg.cmd === "resumeGame") {
    // Back to the game thaws it, as on the host.
    if (mockRest.paused) { mockRest.paused = false; mockGamePush(); }
  } else if (msg.cmd === "pauseGame") {
    mockRest.paused = !mockRest.paused;
    mockGamePush();
    toast(mockRest.paused ? "(preview) game frozen" : "(preview) game running again");
  } else if (msg.cmd === "rest") {
    // The screen goes dark for a moment and comes back, as a press would bring it back.
    mockRest.phase = "resting";
    if (mockHandle._running) mockRest.paused = true;
    handleHostMessage({ type: "rest", rest: mockRestPayload() });
    toast("(preview) resting — the screen would be dark now");
    setTimeout(() => { mockRest.phase = "awake"; mockRest.paused = false; handleHostMessage({ type: "rest", rest: mockRestPayload() }); }, 1500);
  } else if (msg.cmd === "sleepPc") {
    toast("(preview) the PC would sleep now, with the game frozen first");
  } else if (msg.cmd === "restRefresh") {
    handleHostMessage({ type: "rest", rest: mockRestPayload() });
  } else if (msg.cmd === "wakeAllow") {
    const d = mockRest.wake.devices.find(x => x.name === msg.name);
    if (d) d.armed = true;
    toast(`(preview) ${msg.name} may wake the PC now`);
    handleHostMessage({ type: "rest", rest: mockRestPayload() });
  } else if (msg.cmd === "signInOnWake") {
    mockRest.wake.signInOnWake = !!msg.on;
    toast(msg.on ? "(preview) sign-in after a wake is back on" : "(preview) no sign-in after a wake");
    handleHostMessage({ type: "rest", rest: mockRestPayload() });
  } else if (msg.cmd === "closeGame") {
    mockHandle._running = null;
    handleHostMessage({ type: "game", running: false, id: null });
    toast("(preview) game closed");
  } else if (msg.cmd === "rescan") {
    handleHostMessage({ type: "scanning", busy: true });
    setTimeout(() => handleHostMessage({ type: "scanning", busy: false }), 1500);
    mockScanProgress();
  } else if (msg.cmd === "setArgs") {
    toast("(preview) args = " + msg.args);
  } else if (msg.cmd === "romFolderPick") {
    setTimeout(() => handleHostMessage({ type: "romFolderPicked", path: "D:\\ROMs\\N64", platformId: "n64" }), 300);
  } else if (msg.cmd === "romFolderAdd") {
    mockEmulation.romFolders.push({ id: "f" + (mockEmulation.romFolders.length + 1), path: msg.path, platformId: msg.platformId, emulatorId: msg.emulatorId || null, core: null, args: null, extensions: null, recurse: true });
    toast(`(preview) added ${msg.platformId} folder ${msg.path}`);
    pushState();
  } else if (msg.cmd === "romFolderUpdate") {
    const f = mockEmulation.romFolders.find(x => x.id === msg.id);
    if (f) {
      if (msg.platformId) f.platformId = msg.platformId;
      if (msg.emulatorId !== undefined) { f.emulatorId = msg.emulatorId || null; f.core = null; }
      if (msg.args !== undefined) f.args = msg.args || null;
      if (msg.extensions !== undefined) f.extensions = msg.extensions ? msg.extensions.split(/[,\s;]+/).filter(Boolean) : null;
    }
    pushState();
  } else if (msg.cmd === "romFolderPickCore") {
    const f = mockEmulation.romFolders.find(x => x.id === msg.id);
    if (f) f.core = "C:\\RetroArch\\cores\\mednafen_psx_hw_libretro.dll";
    toast("(preview) core chosen");
    pushState();
  } else if (msg.cmd === "romFolderRemove") {
    const i = mockEmulation.romFolders.findIndex(x => x.id === msg.id);
    if (i >= 0) mockEmulation.romFolders.splice(i, 1);
    pushState();
  } else if (msg.cmd === "emuAdd") {
    const e = { id: "e" + (mockEmulation.emulators.length + 1), name: "Dolphin", exePath: "C:\\Emulators\\Dolphin\\Dolphin.exe", args: '-b -e "{rom}"', preset: "dolphin", platforms: ["gc", "wii"] };
    mockEmulation.emulators.push(e);
    pushState();
    setTimeout(() => handleHostMessage({ type: "emuAdded", id: e.id }), 200);
  } else if (msg.cmd === "emuUpdate") {
    const e = mockEmulation.emulators.find(x => x.id === msg.id);
    if (e) { if (msg.name) e.name = msg.name; if (msg.args !== undefined) e.args = msg.args; }
    pushState();
  } else if (msg.cmd === "emuPickExe") {
    toast("(preview) would open the file picker");
  } else if (msg.cmd === "emuRemove") {
    const i = mockEmulation.emulators.findIndex(x => x.id === msg.id);
    if (i >= 0) mockEmulation.emulators.splice(i, 1);
    mockEmulation.romFolders.forEach(f => { if (f.emulatorId === msg.id) f.emulatorId = null; });
    pushState();
  } else if (msg.cmd === "setEmulator") {
    mockHandle._emus = mockHandle._emus || {};
    mockHandle._emus[msg.id] = msg.emulatorId || null;
    pushState();
  } else if (msg.cmd === "setTitle") {
    mockHandle._titles = mockHandle._titles || {};
    mockHandle._titles[msg.id] = msg.title;
    pushState();
  } else if (msg.cmd === "actionFire") {
    const app = mockActions.apps.find(a => a.id === msg.appId);
    const a = app && app.actions.find(x => x.id === msg.actionId);
    toast(`(preview) would send ${a ? a.keys : "?"} to ${app ? app.name : "?"}`);
  } else if (msg.cmd === "actionCapture") {
    // What the pad "presses", in turn: a plain button, then the two the host refuses, a chord, a
    // direction, and a stick click, so every path of the dialog gets walked.
    mockHandle._captures = mockHandle._captures || ["Y", "A", "LB + X", "Up", "Guide", "RS"];
    const next = mockHandle._captures.shift();
    mockHandle._captures.push(next);
    clearTimeout(mockHandle._captureTimer);
    mockHandle._captureTimer = setTimeout(() => {
      if (next === "A" || next === "Guide") {
        handleHostMessage({ type: "actionCaptureRejected", button: next });
        mockHandle._captureTimer = setTimeout(() => handleHostMessage({ type: "actionCaptured", combo: "X" }), 1400);
      } else handleHostMessage({ type: "actionCaptured", combo: next });
    }, 900);
  } else if (msg.cmd === "actionCaptureCancel") {
    clearTimeout(mockHandle._captureTimer);
  } else if (msg.cmd === "actionUpdate") {
    const app = mockActions.apps.find(a => a.id === msg.appId);
    if (app) {
      const i = app.actions.findIndex(x => x.id === msg.action.id);
      if (i >= 0) app.actions[i] = { ...app.actions[i], ...msg.action };
      else app.actions.push({ ...msg.action, custom: true });
      if (!app.custom) app.modified = true;
    }
    handleHostMessage({ type: "actions", actions: mockActions });
  } else if (msg.cmd === "actionRemove") {
    const app = mockActions.apps.find(a => a.id === msg.appId);
    if (app) { const i = app.actions.findIndex(x => x.id === msg.actionId); if (i >= 0) app.actions.splice(i, 1); }
    handleHostMessage({ type: "actions", actions: mockActions });
  } else if (msg.cmd === "actionAppReset") {
    const app = mockActions.apps.find(a => a.id === msg.appId);
    const was = mockActionsPristine.apps.find(a => a.id === msg.appId);
    if (app && was) { app.actions = JSON.parse(JSON.stringify(was.actions)); app.modified = false; }
    handleHostMessage({ type: "actions", actions: mockActions });
  } else if (msg.cmd === "actionAppRemove") {
    const i = mockActions.apps.findIndex(a => a.id === msg.appId);
    if (i >= 0) { if (mockActions.apps[i].custom) mockActions.apps.splice(i, 1); else { mockActions.apps[i].installed = false; mockActions.apps[i].pinned = false; } }
    handleHostMessage({ type: "actions", actions: mockActions });
  } else if (msg.cmd === "actionAppRename") {
    const app = mockActions.apps.find(a => a.id === msg.appId);
    if (app) app.name = msg.name;
    handleHostMessage({ type: "actions", actions: mockActions });
  } else if (msg.cmd === "actionAppAdd" || msg.cmd === "actionAppBrowse") {
    const exe = msg.cmd === "actionAppBrowse" ? "notepad" : msg.exe;
    const name = msg.cmd === "actionAppBrowse" ? "Notepad" : (msg.name || exe);
    let app = mockActions.apps.find(a => a.exes.includes(exe));
    const existed = !!(app && app.installed);
    if (app) { app.installed = true; app.pinned = !app.custom; }
    else { app = { id: exe, name, exes: [exe], custom: true, pinned: false, installed: true, modified: false, icon: null, actions: [] }; mockActions.apps.push(app); }
    handleHostMessage({ type: "actions", actions: mockActions });
    setTimeout(() => handleHostMessage({ type: "actionAppAdded", id: app.id, existed }), 150);
  } else if (msg.cmd === "actionListApps") {
    handleHostMessage({ type: "actionApps", open: [
      { title: "Cyberpunk 2077", exe: "cyberpunk2077", listed: false },
      { title: "Mozilla Firefox", exe: "firefox", listed: true, appId: "firefox" },
      { title: "Google Chrome", exe: "chrome", listed: false },
      { title: "Netflix", exe: "netflix", listed: false },
    ] });
  } else if (msg.cmd === "xboxButtonSet" || msg.cmd === "xboxButtonAllOff") {
    // Windows' two switch at once; Steam takes a moment, as its restart does on the host.
    const all = msg.cmd === "xboxButtonAllOff";
    if (all || msg.what === "gameBar") mockXboxButton.gameBar = all ? false : !!msg.on;
    if (all || msg.what === "xboxMode") mockXboxButton.xboxMode = all ? false : !!msg.on;
    if (all || msg.what === "steam") {
      mockXboxButton.steamBusy = true;
      setTimeout(() => { mockXboxButton.steam = all ? false : !!msg.on; mockXboxButton.steamBusy = false; pushState(); toast("(preview) Steam restarted"); }, 1200);
    }
    pushState();
  } else if (msg.cmd === "activityOpen") {
    setTimeout(() => handleHostMessage({ type: "activity", id: msg.id, sessions: mockAct.sessions.filter(s => s.gameId === msg.id), unlocks: mockUnlocks(msg.id) }), 250);
  } else if (msg.cmd === "activitySession") {
    const s = mockAct.sessions.find(x => x.id === msg.id);
    const slack = 2 * 60000;
    const unlocks = s ? mockUnlocks(s.gameId).filter(u => new Date(u.at) >= new Date(s.start).getTime() - slack && new Date(u.at) <= new Date(s.end).getTime() + slack) : [];
    if (s) setTimeout(() => handleHostMessage({ type: "activitySession", id: s.id, session: s, samples: s.samples ? mockSamples(s) : [], unlocks }), 350);
  } else if (msg.cmd === "activityAll") {
    setTimeout(() => handleHostMessage({ type: "activityAll", sessions: mockAct.sessions.slice(), sources: { gpu: true, fps: "RivaTuner Statistics Server", sensors: null } }), 300);
  } else if (msg.cmd === "activityDelete") {
    const i = mockAct.sessions.findIndex(x => x.id === msg.id);
    const gameId = i >= 0 ? mockAct.sessions[i].gameId : null;
    if (i >= 0) mockAct.sessions.splice(i, 1);
    toast("Session removed");
    if (gameId) handleHostMessage({ type: "activity", id: gameId, sessions: mockAct.sessions.filter(s => s.gameId === gameId), unlocks: mockUnlocks(gameId) });
  } else if (msg.cmd === "activityClear") {
    const n = mockAct.sessions.length;
    mockAct.sessions.length = 0;
    toast(`Cleared ${n} sessions`);
    handleHostMessage({ type: "activityAll", sessions: [], sources: null });
  } else if (msg.cmd === "achievementsOpen") {
    const g = S.games.find(x => x.id === msg.id);
    const set = mockAct.sets[msg.id];
    const blocked = g && g.platform === "Xbox" && !set ? "Xbox has not seen this game played on this account yet" : null;
    handleHostMessage({ type: "achievements", id: msg.id, head: { blocked, fetching: !set && !blocked, provider: g && g.platform === "Steam" ? "steam" : "xbox", id: msg.id }, set: set || null });
    if (!set && !blocked) setTimeout(() => handleHostMessage({ type: "achievements", id: msg.id, head: null, set: { gameId: msg.id, source: "steam", fetchedAt: new Date().toISOString(), error: null, unlocked: 0, total: 0, score: 0, totalScore: 0, items: [] } }), 900);
  } else if (msg.cmd === "achievementsClose") {
    /* nothing to release in the preview */
  } else if (msg.cmd === "achievementsRefresh") {
    toast(msg.id ? "(preview) would fetch this game's list again" : "Refreshing achievements in the background");
  } else if (msg.cmd === "achievementsAll") {
    setTimeout(() => handleHostMessage(mockAchAll()), 200);
  } else if (msg.cmd === "actionsRefresh") {
    /* detection is the host's; the preview's list is what it is */
  } else if (msg.cmd === "achievementEdit" || msg.cmd === "achievementUnedit") {
    // AchievementStore.Edit / Unedit, on the mock's list.
    const set = mockAct.sets[msg.id];
    const a = set && set.items.find(x => x.id === msg.achievementId);
    if (!a) return;
    if (msg.cmd === "achievementEdit") {
      if (!a.edited) { a.storeUnlocked = a.unlocked; a.storeUnlockedAt = a.unlockedAt; }
      a.unlocked = msg.unlocked; a.unlockedAt = msg.unlocked ? msg.at : null; a.edited = true;
    } else if (a.edited) {
      a.unlocked = a.storeUnlocked; a.unlockedAt = a.storeUnlockedAt; a.edited = null; a.storeUnlocked = null; a.storeUnlockedAt = null;
    }
    set.unlocked = set.items.filter(x => x.unlocked).length;
    handleHostMessage({ type: "achievementsSummary", id: msg.id, summary: mockAchSummaries()[msg.id] });
    handleHostMessage({ type: "achievements", id: msg.id, head: null, set });
    toast(msg.cmd === "achievementEdit" ? "Achievement saved" : "Back to what the store says");
  } else if (msg.cmd === "activityLog" || msg.cmd === "activityEdit") {
    const start = new Date(msg.start);
    const fields = { start: start.toISOString(), end: new Date(start.getTime() + msg.seconds * 1000).toISOString(), seconds: msg.seconds, counted: msg.counted || null };
    let gameId = msg.id;
    if (msg.cmd === "activityLog") mockAct.sessions.push({ id: "m" + Date.now().toString(16), gameId, samples: 0, origin: "manual", ...fields });
    else { const s = mockAct.sessions.find(x => x.id === msg.id); if (!s) return; Object.assign(s, fields); gameId = s.gameId; }
    handleHostMessage({ type: "activity", id: gameId, sessions: mockAct.sessions.filter(s => s.gameId === gameId), unlocks: mockUnlocks(gameId) });
    toast(msg.cmd === "activityLog" ? "Session logged" : "Session saved");
  } else if (msg.cmd === "playniteScan" || msg.cmd === "playnitePick") {
    handleHostMessage({ type: "playnite", state: "reading", dir: mockPlaynite.dir, record: mockPlaynite.record });
    setTimeout(() => handleHostMessage(mockPlaynite.message("ready", null)), 500);
  } else if (msg.cmd === "playniteImport") {
    const plan = mockPlaynite.plan();
    const outcome = { playtime: 0, flags: 0, collections: 0, games: 0, sessions: 0, achievements: 0, settings: 0 };
    (msg.parts || []).forEach(p => { if (plan[p]) outcome[p] = plan[p].count; });
    outcome.total = Object.values(outcome).reduce((a, b) => a + b, 0);
    (msg.parts || []).forEach(p => mockPlaynite.done.add(p));
    if (outcome.total) mockPlaynite.record = { at: new Date().toISOString(), ...outcome };
    setTimeout(() => handleHostMessage(mockPlaynite.message("done", outcome)), 400);
  } else if (msg.cmd === "playniteUndo") {
    const outcome = { ...(mockPlaynite.record || {}), total: 1 };
    mockPlaynite.record = null;
    mockPlaynite.done.clear();
    setTimeout(() => handleHostMessage(mockPlaynite.message("undone", outcome)), 400);
  }
}

/* The scan as the host reports it, a step at a time (BeginSteps / SetStep), so the first-run
   setup's rail can be watched filling in. GOG is not signed in and Game Pass is off, as in the
   mock's stores, so both are "off". */
function mockScanProgress() {
  const plan = [["emulators", 2], ["steam", 9], ["epic", 5], ["gog", 4], ["xbox", 2], ["roms", 8],
    ["steamOwned", 3], ["epicOwned", 1], ["gogOwned", null], ["xboxOwned", 2], ["gamePass", null], ["metadata", null], ["achievements", null]];
  const off = new Set(["gogOwned", "gamePass"]);
  const steps = plan.map(([id]) => ({ id, state: off.has(id) ? "off" : "pending", count: null }));
  const push = () => handleHostMessage({ type: "scanProgress", steps: steps.map(s => ({ ...s })) });
  clearTimeout(mockScanProgress._t);
  push();
  let i = 0;
  const next = () => {
    while (i < plan.length && steps[i].state === "off") i++;
    if (i >= plan.length) return;
    const st = steps[i], n = plan[i][1];
    st.state = "running";
    push();
    mockScanProgress._t = setTimeout(() => { st.state = "done"; st.count = n; push(); i++; next(); }, st.id === "metadata" ? 6000 : 700);
  };
  mockScanProgress._t = setTimeout(next, 400);
}

/* Vortex as the setup's step would find it: running, and not connected until it has been restarted
   once (see the onboardingVortex branch of mockHandle). */
function mockVortex() {
  const state = mockHandle._vortex || "none";
  const vortex = {
    installed: true, path: "C:\\Users\\couch\\AppData\\Local\\Programs\\Vortex\\Vortex.exe", version: "1.13.7", running: true,
    bridgeReady: state === "ready", needsRestart: state === "needsRestart",
    error: state === "needsRestart" ? "Vortex is running but has not loaded the Loungepad bridge yet. Restart Vortex once" : null,
  };
  const games = state !== "ready" ? [] : [
    { gameId: "steam:hollowmarksecondascent", title: "Hollowmark: Second Ascent", mods: 14, enabled: 11 },
    { gameId: "gog:salttide", title: "Salt & Tide", mods: 3, enabled: 3 },
    { gameId: null, title: "Skyrim Special Edition", mods: 0, enabled: 0 },
  ];
  return { vortex, games, error: null };
}

/* The Playnite import as the host would plan it for a small library: every part has something,
   except that SuccessStory is not installed. What has been imported reads as nothing left. */
const mockPlaynite = {
  dir: "C:\\Users\\you\\AppData\\Roaming\\Playnite",
  record: null,
  done: new Set(),
  plan() {
    const zero = (p, extra) => (this.done.has(p) ? { count: 0, ...extra } : null);
    return {
      playtime: zero("playtime") || { count: 6, minutes: 5230, titles: ["Hollowmark: Second Ascent", "Salt & Tide", "Marrow"] },
      flags: zero("flags") || { count: 3, favorites: 2, hidden: 1 },
      collections: zero("collections") || { count: 2, names: ["Couch co-op", "Backlog (Playnite)"] },
      games: zero("games") || { count: 1, titles: ["Dolphin Test Build"] },
      sessions: zero("sessions", { available: true, games: 0 }) || { count: 48, available: true, games: 5 },
      achievements: { count: 0, available: false, unlocked: 0 },
      settings: zero("settings", { labels: [] }) || { count: 1, labels: ["SteamGridDB key"] },
    };
  },
  message(state, outcome) {
    return { type: "playnite", state, dir: this.dir, version: "10.56", games: 22, matched: 18, parts: this.plan(), outcome, record: this.record };
  },
};

/* ============================== boot ============================== */

fitStage();
tickClock();
renderTabbars();
renderGuide();
renderLibraryLegend();
renderInputHint();
paintButtons(document);
// Show the no-controller state straight away. The host only pushes when something changes, so
// waiting for a message left the corner blank until a pad was plugged in.
updateBattery(null);
switchView("library");
send({ cmd: "ready" });
