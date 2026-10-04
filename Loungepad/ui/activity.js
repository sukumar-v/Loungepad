"use strict";

/*
 * Stats: the play sessions and the achievements, on the page.
 *
 * Five things, all fed by the host (see ActivityService and AchievementService):
 *
 *   1. A game's Achievements sheet: the list with icons, rarity and unlock dates, a filter (X)
 *      and an order (Y), hidden ones revealed with A. Opened from the game's page, its Y menu,
 *      or a row on the Stats screen.
 *
 *   2. A game's Stats sheet: its sessions newest first with each one's averages and what it
 *      unlocked, a bar a day for the last month with the unlocks over it, and a Session sheet
 *      over that with the readings of one sitting drawn as charts, each unlock marked at the
 *      moment it happened.
 *
 *   3. The Stats screen (LB on the library, or Settings → Stats → Open Stats): the whole
 *      library's playtime and achievements in three categories, laid out like Settings. The
 *      overview's chart is a day picker: ◂ ▸ walk the last four weeks and the day's unlocks
 *      are listed under it.
 *
 *   4. A Day sheet: one day as a timeline, each session with the achievements earned during
 *      it, and anything unlocked away from a recorded session at its own time.
 *
 *   5. The small things: the unlocked share on tiles and in the hero text, the "playing for"
 *      readout on the resume card, the live readings under the in-game menu, and the card that
 *      announces what a session unlocked once the game has closed.
 *
 * An unlock belongs to a session when it is the same game and its time falls inside the
 * session, give or take two minutes (UNLOCK_SLACK_MS, the host's SessionUnlockSlack): the
 * store's clock against this PC's, and games that write their stats on the way out.
 *
 * Loaded after app.js, radial.js and actions.js and shares their globals; the state variables
 * (achState, actState, sessionState, dayState, statsUi, statsData) live in app.js because it
 * reads them while it boots. Every chart is inline SVG built here -- nothing is fetched to draw
 * one. The host's names (ActivityService, activity.json, the activity* commands) predate the
 * screen being called Stats and were left alone.
 */

/* ============================== formatting ============================== */

function fmtDuration(seconds) {
  const s = Math.max(0, Math.round(seconds || 0));
  if (s < 60) return `${s}s`;
  const h = Math.floor(s / 3600), m = Math.round((s % 3600) / 60);
  if (h > 0) return m > 0 ? `${h}h ${String(m).padStart(2, "0")}m` : `${h}h`;
  return `${m}m`;
}

function fmtDate(iso) {
  if (!iso) return "";
  const d = new Date(iso);
  if (isNaN(d)) return "";
  return `${d.getDate()} ${d.toLocaleString("en", { month: "short" })} ${d.getFullYear()}`;
}

/* "2 min ago", "3 h ago", then the date: for "updated" lines, where the date alone would read
   as stale the moment it was fetched. */
function fmtAgo(iso) {
  if (!iso) return "";
  const t = new Date(iso);
  if (isNaN(t)) return "";
  const mins = Math.round((Date.now() - t) / 60000);
  if (mins < 1) return "just now";
  if (mins < 60) return `${mins} min ago`;
  const hours = Math.round(mins / 60);
  if (hours < 24) return `${hours} h ago`;
  return fmtDate(iso);
}

function fmtInt(n) { return (n || 0).toLocaleString("en"); }

function fmtClock(d) {
  d = d instanceof Date ? d : new Date(d);
  return isNaN(d) ? "" : `${String(d.getHours()).padStart(2, "0")}:${String(d.getMinutes()).padStart(2, "0")}`;
}

/* "Today", "Yesterday", then "Thursday 25 Sep" -- a day key read back as a day. */
function fmtDayName(key, long) {
  const [y, m, d] = key.split("-").map(Number);
  const date = new Date(y, m - 1, d);
  const today = new Date();
  const diff = Math.round((new Date(today.getFullYear(), today.getMonth(), today.getDate()) - date) / 86400000);
  if (diff === 0) return "Today";
  if (diff === 1) return "Yesterday";
  const name = `${date.toLocaleString("en", { weekday: "long" })} ${date.getDate()} ${date.toLocaleString("en", { month: long ? "long" : "short" })}`;
  return date.getFullYear() === today.getFullYear() ? name : `${name} ${date.getFullYear()}`;
}

/* ============================== unlocks against time ============================== */

const UNLOCK_SLACK_MS = 2 * 60000;

/* Whether an unlock happened during a session: the same game, inside it give or take the slack. */
function unlockInSession(u, s) {
  if (u.gameId !== undefined && u.gameId !== s.gameId) return false;
  const t = new Date(u.at).getTime();
  return t >= new Date(s.start).getTime() - UNLOCK_SLACK_MS && t <= new Date(s.end).getTime() + UNLOCK_SLACK_MS;
}

/* Puts each bucket's unlocks on it as `marks`, from counts per day. Month buckets (key yyyy-mm)
   add up their days. */
function withMarks(buckets, byDay) {
  const index = new Map(buckets.map((b, i) => [b.key, i]));
  const monthly = buckets.length > 0 && buckets[0].key.length === 7;
  buckets.forEach(b => { b.marks = 0; });
  Object.entries(byDay || {}).forEach(([day, n]) => {
    const i = index.get(monthly ? day.slice(0, 7) : day);
    if (i !== undefined) buckets[i].marks += n;
  });
  return buckets;
}

/* A few icons in a row, and "+n" for the rest. */
function unlockIcons(unlocks, max) {
  const shown = unlocks.slice(0, max);
  const more = unlocks.length - shown.length;
  return `<span class="unlock-icons">${shown.map(u => {
    const src = achIconSrc(u.item.iconFile, u.item.icon);
    return `<i class="ach-icon tiny"${achIconStyle(src)}>${src ? "" : iconSvg("trophy")}</i>`;
  }).join("")}${more > 0 ? `<b>+${more}</b>` : ""}</span>`;
}

/* ============================== rarity ==============================
   The bands the chips use, by the share of players who hold the achievement. Ultra rare under
   5%, rare under 10%, uncommon under 30%, common above -- SuccessStory's defaults with the
   ultra-rare step on, which is roughly where Xbox and PlayStation draw theirs too. */
const RARITY_BANDS = [
  { id: "ultra",    label: "Ultra rare", below: 5 },
  { id: "rare",     label: "Rare",       below: 10 },
  { id: "uncommon", label: "Uncommon",   below: 30 },
  { id: "common",   label: "Common",     below: Infinity },
];
function rarityBand(pct) {
  if (typeof pct !== "number" || isNaN(pct)) return null;
  return RARITY_BANDS.find(b => pct < b.below) || RARITY_BANDS[RARITY_BANDS.length - 1];
}
function fmtPct(pct) {
  if (typeof pct !== "number" || isNaN(pct)) return "";
  return pct < 10 ? pct.toFixed(1) + "%" : Math.round(pct) + "%";
}

/* ============================== summaries, tiles, the hero ============================== */

/* What the state push carries per game: { unlocked, total, score, totalScore, lastUnlock, source }. */
function achievementSummary(g) {
  if (!g || !S.achievements) return null;
  const a = S.achievements[g.id];
  return a && a.total > 0 ? a : null;
}

/* The unlocked share as a number for sorting; -1 where there is nothing to sort on. */
function achievementShare(g) {
  const a = achievementSummary(g);
  return a ? a.unlocked / a.total : -1;
}

function achievementsOnTiles() { return !S.settings || S.settings.achievementsOnTiles !== false; }

/* For a theme's tile template. */
function achievementView(g) {
  const a = achievementSummary(g);
  if (!a) return { achievements: false, achUnlocked: 0, achTotal: 0, achPercent: "" };
  return { achievements: achievementsOnTiles(), achUnlocked: a.unlocked, achTotal: a.total, achPercent: String(Math.round(100 * a.unlocked / a.total)) };
}

/* The small cup and share on a Shelf tile's meta line. Only for a game with any, and only while
   the option is on: a wall of "0%" would be a wall of captions. */
function achievementChip(g) {
  const a = achievementSummary(g);
  if (!a || !achievementsOnTiles()) return "";
  const pct = Math.round(100 * a.unlocked / a.total);
  return `<span class="tile-ach${pct >= 100 ? " done" : ""}">${iconSvg("trophy")}${pct}%</span>`;
}

/* "· 23/50 ACHIEVEMENTS" on the hero text under the Loungepad theme. */
function achievementMeta(g) {
  const a = achievementSummary(g);
  if (!a || !achievementsOnTiles()) return "";
  return ` · ${a.unlocked}/${a.total} achievements`;
}

function achievementMenuSub(g) {
  const a = achievementSummary(g);
  if (!a) return "See what the game has, and what you have";
  const pct = Math.round(100 * a.unlocked / a.total);
  return `${a.unlocked} of ${a.total} unlocked · ${pct}%`;
}

/* RetroAchievements' systems, as the host's provider lists them. */
const ACH_RETRO_PLATFORMS = new Set(["genesis", "n64", "snes", "gb", "gba", "gbc", "nes", "tg16", "segacd", "32x", "sms", "ps1", "lynx",
  "ngp", "gg", "gc", "jaguar", "nds", "ps2", "atari2600", "arcade", "neogeo", "vb", "msx", "saturn", "dreamcast", "psp", "3do",
  "coleco", "intv", "atari7800", "ws", "tgcd"]);

/* The same rule as the host's providers: what kind of game could have a list at all. A game with
   a list on record always can, whatever kind it is. */
function canHaveAchievements(g) {
  if (!g) return false;
  if (S.settings && S.settings.achievementsEnabled === false) return false;
  if (achievementSummary(g)) return true;
  if (g.emulated) return !!(g.platformId && ACH_RETRO_PLATFORMS.has(g.platformId));
  if (g.platform === "Steam") return g.id.startsWith("steam:");
  if (g.platform === "Xbox") return !g.id.startsWith("xbox:store:");
  if (g.platform === "Epic") return !!g.epicNamespace;
  if (g.platform === "GOG") return /^gog:\d+$/.test(g.id);
  return false;
}

function onAchievementsSummary(m) {
  if (!m.id || !m.summary) return;
  S.achievements = S.achievements || {};
  if (m.summary.total > 0) S.achievements[m.id] = m.summary; else delete S.achievements[m.id];
  // The one game's tiles and the hero text, patched in place: the background pass lands one of
  // these every second or so for minutes, and a library rebuild for each would throw the
  // highlight around under somebody browsing.
  const g = gameById(m.id);
  if (g) {
    const view_ = achievementView(g);
    document.querySelectorAll(`[data-game-id="${CSS.escape(m.id)}"]`).forEach(tile => {
      const meta = tile.querySelector(".grid-meta");
      if (meta) meta.innerHTML = esc(g.installed ? shortMeta(g) : uninstalledMeta(g)) + achievementChip(g);
      const sub = tile.querySelector(".cont-sub");
      if (sub) sub.innerHTML = esc(shortMeta(g)) + achievementChip(g);
      const chip = tile.querySelector(".tv-ach");
      if (chip) { chip.textContent = view_.achPercent + "%"; chip.hidden = !view_.achievements; }
    });
    if (view === "library" && focusedGame() === g) updateFocusDetail(g);
  }
  if (view === "detail" && detailGameId === m.id) renderDetailStats(gameById(m.id));
  if (statsData.ach) requestStats(false);
}

/* The resume card and the in-game menu: how long the sitting has been going. */
function updatePlayingMeta() {
  const g = S.gameRunning ? gameById(S.runningGameId) : null;
  const el = $("playingMeta");
  if (!el || !g) return;
  const since = S.sessionStart ? new Date(S.sessionStart) : null;
  const dur = since && !isNaN(since) ? ` · ${fmtDuration((Date.now() - since) / 1000)}` : "";
  // No process seen yet (a store client still launching it, or a launch that went nowhere):
  // "starting", and Close game ends the session at once instead of finding nothing to close.
  const state = S.gamePaused ? " · PAUSED" : S.gameStarting ? " · STARTING" : " · RUNNING";
  el.textContent = (g.platform + state + dur).toUpperCase();
}

function renderIngameStats() {
  const el = $("ingameStats");
  if (!el) return;
  const bits = [];
  if (S.gamePaused) bits.push("PAUSED");
  const since = S.sessionStart ? new Date(S.sessionStart) : null;
  if (since && !isNaN(since)) bits.push(`PLAYING FOR ${fmtDuration((Date.now() - since) / 1000)}`);
  const t = S.telemetry && S.telemetry.sample;
  if (t && S.telemetry.id === S.runningGameId) {
    if (t.fps != null) bits.push(`${t.fps} FPS`);
    if (t.cpu != null) bits.push(`CPU ${t.cpu}%`);
    if (t.gpu != null) bits.push(`GPU ${t.gpu}%`);
    if (t.ram != null) bits.push(`RAM ${t.ram}%`);
    if (t.gpuTemp != null) bits.push(`GPU ${t.gpuTemp}°`);
    if (t.cpuTemp != null) bits.push(`CPU ${t.cpuTemp}°`);
  }
  el.textContent = bits.join("  ·  ").toUpperCase();
  el.hidden = bits.length === 0;
}

/* ============================== icons ============================== */

function achIconSrc(file, url) {
  if (file) return HOST ? `https://loungepad.data/achievements/${encodeURIComponent(file)}` : file;
  return url || "";
}

/* The icon as a style attribute, or nothing. The URL is a store's or the metadata service's
   answer being written into markup, so it is escaped like any other text from outside; the host
   has already refused anything but a plain https URL (AchievementService.SafeIconUrl), and this
   is the second lock. `extra` is any other declaration the element needs in the same attribute. */
function achIconStyle(src, extra = "") {
  if (src) return ` style="${extra}background-image:url('${esc(src)}')"`;
  return extra ? ` style="${extra}"` : "";
}
/* The picture for an item as it stands: the store's greyed version when there is one for a
   locked achievement, else the colour icon, which the stylesheet greys. */
function achItemIcon(a) {
  if (a.unlocked) return achIconSrc(a.iconFile, a.icon);
  return achIconSrc(a.iconLockedFile, a.iconLocked) || achIconSrc(a.iconFile, a.icon);
}

/* ============================== the achievements sheet ============================== */

const ACH_FILTERS = [["all", "All"], ["unlocked", "Unlocked"], ["locked", "Locked"]];
const ACH_SORTS = [["default", "Store order"], ["rarity", "Rarest first"], ["date", "Latest unlocked"], ["name", "A – Z"]];

/* `focusId` opens the list on one achievement: the one a Stats row or a day's timeline was on. */
function openAchievements(gameId, from, focusId) {
  achState = { gameId, idx: 0, filter: "all", sort: "default", set: null, head: null, reveal: new Set(), from: from || view, focusId: focusId || null };
  showOverlay("overlay-achievements");
  renderAchievements();
  send({ cmd: "achievementsOpen", id: gameId });
}

function closeAchievements() {
  if (!achState) return;
  achState = null;
  hideOverlay("overlay-achievements");
  send({ cmd: "achievementsClose" });
}

function onAchievementsMessage(m) {
  if (!achState || m.id !== achState.gameId) return;
  if (m.head) achState.head = m.head;
  if (m.set) { achState.set = m.set; if (achState.head) achState.head.fetching = false; }
  renderAchievements();
}

/* The list as filtered and ordered. Hidden ones keep their place: an achievement you cannot
   see yet is still one you have not got. */
function achItems() {
  if (!achState || !achState.set) return [];
  let items = achState.set.items || [];
  if (achState.filter === "unlocked") items = items.filter(a => a.unlocked);
  else if (achState.filter === "locked") items = items.filter(a => !a.unlocked);
  const pct = (a) => (typeof a.percent === "number" ? a.percent : 101);
  const when = (a) => (a.unlockedAt ? new Date(a.unlockedAt).getTime() : 0);
  if (achState.sort === "rarity") items = [...items].sort((a, b) => pct(a) - pct(b) || a.name.localeCompare(b.name));
  else if (achState.sort === "date") items = [...items].sort((a, b) => when(b) - when(a) || pct(a) - pct(b));
  else if (achState.sort === "name") items = [...items].sort((a, b) => a.name.localeCompare(b.name));
  return items;
}

function achScoreLabel(set) {
  if (!set || !set.totalScore) return "";
  const unit = set.source === "xbox" ? "G" : set.source === "retro" || set.source === "playnite" ? "pts" : "XP";
  return `${fmtInt(set.score)} / ${fmtInt(set.totalScore)} ${unit}`;
}

const ACH_SOURCE_NAMES = { steam: "Steam", xbox: "Xbox", epic: "Epic Games", gog: "GOG", retro: "RetroAchievements", playnite: "Playnite" };

function renderAchievements() {
  if (!achState) return;
  const g = gameById(achState.gameId);
  const set = achState.set, head = achState.head || {};
  $("achGame").textContent = g ? g.title : "";
  const src = ACH_SOURCE_NAMES[(set && set.source) || head.provider] || "";
  const srcBits = [src ? src.toUpperCase() : null];
  if (set && set.sourceName && g && set.sourceName !== g.title) srcBits.push(`MATCHED “${set.sourceName.toUpperCase()}”`);
  if (set && set.fetchedAt) srcBits.push(`${set.source === "playnite" ? "IMPORTED" : "UPDATED"} ${fmtAgo(set.fetchedAt).toUpperCase()}`);
  if (head.fetching) srcBits.push("FETCHING…");
  $("achSource").textContent = srcBits.filter(Boolean).join(" · ");

  // The share at the top: the number, a bar, the counts and the score where the store has one.
  const prog = $("achProgress");
  if (set && set.total > 0) {
    const pct = Math.round(100 * set.unlocked / set.total);
    prog.innerHTML = `<div class="ach-pct${pct >= 100 ? " done" : ""}">${pct}%</div>` +
      `<div class="ach-bar"><i style="width:${pct}%"></i></div>` +
      `<div class="ach-counts mono">${set.unlocked} / ${set.total}${set.totalScore ? " · " + esc(achScoreLabel(set)) : ""}</div>`;
    prog.hidden = false;
  } else prog.hidden = true;

  // What stands in the way, or what there is none of.
  const note = $("achNote");
  let noteText = null;
  if (head.blocked) noteText = head.blocked;
  else if (set && set.error && !(set.items && set.items.length)) noteText = set.error;
  else if (set && set.total === 0 && !head.fetching) noteText = set.source === "retro"
    ? "RetroAchievements has no set for this game, or the ROM could not be matched to one"
    : "This game has no achievements";
  else if (!set && head.fetching) noteText = "Fetching the list…";
  else if (!set && !head.fetching && !head.blocked) noteText = "Nothing fetched yet";
  if (set && set.error && set.items && set.items.length) noteText = `${set.error}. Showing the list from ${fmtAgo(set.fetchedAt)}`;
  note.textContent = noteText || "";
  note.hidden = !noteText;

  // Filter and order, as the toolbar reads them.
  const counts = set ? { all: set.total, unlocked: set.unlocked, locked: set.total - set.unlocked } : { all: 0, unlocked: 0, locked: 0 };
  $("achToolbar").innerHTML = ACH_FILTERS.map(([id, label]) =>
      `<span class="ach-tab${achState.filter === id ? " on" : ""}">${esc(label.toUpperCase())} <b>${counts[id]}</b></span>`).join("") +
    `<span class="ach-sort">${esc((ACH_SORTS.find(x => x[0] === achState.sort) || ACH_SORTS[0])[1].toUpperCase())}</span>`;

  const list = $("achList");
  const items = achItems();
  if (achState.focusId && set) {
    const at = items.findIndex(a => a.id === achState.focusId);
    if (at >= 0) achState.idx = at;
    achState.focusId = null;
  }
  const keepTop = list.scrollTop;
  list.innerHTML = "";
  achState.idx = Math.max(0, Math.min(achState.idx, items.length - 1));
  items.forEach((a, i) => {
    const el = document.createElement("div");
    const revealed = a.unlocked || achState.reveal.has(a.id);
    const secret = a.hidden && !revealed;
    el.className = "ach-row" + (a.unlocked ? "" : " locked") + (secret ? " secret" : "");
    el.dataset.focusable = "";
    el.dataset.focusKey = "ach:" + a.id;
    el.dataset.rowIndex = i;
    const band = rarityBand(a.percent);
    const icon = achItemIcon(a);
    const name = secret ? "Hidden achievement" : a.name;
    const desc = secret ? "Its name and what it takes stay hidden until it is unlocked. Press A to reveal"
      : (a.description || (a.hidden ? "The store keeps the description until it is unlocked" : ""));
    const right = [];
    if (band) right.push(`<span class="ach-rarity" data-band="${band.id}">${esc(fmtPct(a.percent))} · ${esc(band.label.toUpperCase())}</span>`);
    if (a.edited) right.push(`<span class="ach-edited">EDITED</span>`);
    if (a.unlocked) right.push(`<span class="ach-when">${esc(a.unlockedAt ? fmtDate(a.unlockedAt) : "Unlocked")}</span>`);
    else right.push(`<span class="ach-when ach-locked">${a.score ? esc(String(a.score)) + (set && set.source === "xbox" ? " G" : "") : "Locked"}</span>`);
    el.innerHTML =
      `<div class="ach-icon${icon ? "" : " none"}"${icon && !secret ? achIconStyle(icon) : ""}>${secret || !icon ? iconSvg("trophy") : ""}</div>` +
      `<div class="ach-text"><div class="ach-name">${esc(name)}</div><div class="ach-desc">${esc(desc)}</div></div>` +
      `<div class="ach-right mono">${right.join("")}</div>`;
    el.addEventListener("mouseenter", () => { if (hoverEnabled() && achState && achState.idx !== i) { achState.idx = i; paintAchFocus(); } });
    el.addEventListener("click", () => { if (!achState) return; achState.idx = i; achActivate(); });
    list.appendChild(el);
  });
  if (items.length === 0 && set && set.total > 0) {
    const empty = document.createElement("div");
    empty.className = "ach-empty";
    empty.textContent = achState.filter === "unlocked" ? "Nothing unlocked yet" : "Everything is unlocked";
    list.appendChild(empty);
  }
  list.scrollTop = keepTop;
  watchOverflow(list);

  renderAchievementsFoot(items);
  paintAchFocus();
}

function paintAchFocus() {
  if (!achState) return;
  const scope = $("overlay-achievements");
  setScopeKey(scope, "ach:" + (achItems()[achState.idx] || {}).id);
  const cur = focusEl(scope);
  const show = focusVisible();
  Nav.focusables(scope).forEach(el => el.classList.toggle("focused", show && el === cur));
  if (cur && show) revealFocus(cur);
}

/* A reveals a hidden one first; on anything you can read, it edits. */
function achActivate() {
  const cur = achItems()[achState.idx];
  if (!cur) return;
  if (cur.hidden && !cur.unlocked && !achState.reveal.has(cur.id)) { achState.reveal.add(cur.id); renderAchievements(); return; }
  openAchievementEditor(achState.gameId, cur);
}

function achInput(btn) {
  const items = achItems();
  switch (btn) {
    case "Up": case "Down":
      if (listMove(btn)) {
        const el = focusEl();
        if (el && el.dataset.rowIndex !== undefined) achState.idx = parseInt(el.dataset.rowIndex, 10);
      }
      renderAchievementsFoot(items);
      break;
    case "A": if (focusVisible()) achActivate(); break;
    case "X": {
      const i = ACH_FILTERS.findIndex(f => f[0] === achState.filter);
      achState.filter = ACH_FILTERS[(i + 1) % ACH_FILTERS.length][0];
      achState.idx = 0;
      renderAchievements();
      break;
    }
    case "Y": {
      const i = ACH_SORTS.findIndex(f => f[0] === achState.sort);
      achState.sort = ACH_SORTS[(i + 1) % ACH_SORTS.length][0];
      achState.idx = 0;
      renderAchievements();
      break;
    }
    case "B": closeAchievements(); break;
  }
}

function renderAchievementsFoot(items) {
  const cur = items[achState.idx];
  const canReveal = cur && cur.hidden && !cur.unlocked && !achState.reveal.has(cur.id);
  $("achFoot").innerHTML = foot(...(canReveal ? [["A", "Reveal"]] : cur ? [["A", "Edit"]] : []), ["X", "Filter"], ["Y", "Order"], ["B", "Back"]);
}

/* ============================== editing by hand ==============================
   Both editors are form sheets (openForm in app.js) over the sheet they were opened from. Their
   dates and times move a day or a quarter of an hour on ◂ ▸ and are typed exactly on A; neither
   can be set to a moment still to come. */

/* A time rounded down to the quarter hour: what the pickers start from and step by. */
function quarterHour(d) {
  const x = new Date(d);
  x.setSeconds(0, 0);
  x.setMinutes(Math.floor(x.getMinutes() / 15) * 15);
  return x;
}
function fmtLongDate(d) {
  return `${d.toLocaleString("en", { weekday: "short" })} ${d.getDate()} ${d.toLocaleString("en", { month: "short" })} ${d.getFullYear()}`;
}

/* The day and time rows over a Date kept in st[key]. */
function dayTimeRows(st, key, dayName, timeName) {
  const notAhead = (x) => { if (x > new Date()) { toast("That is still to come"); return false; } return true; };
  return [
    {
      name: dayName, hint: "◂ ▸ a day at a time, or A to type the date", type: "select",
      value: fmtLongDate(st[key]) + (dayKey(st[key]) === dayKey(new Date()) ? " · today" : ""),
      adjust: (dir) => { const x = new Date(st[key]); x.setDate(x.getDate() + dir); if (x <= new Date()) st[key] = x; },
      action: () => openInput(`${dayName.toUpperCase()} · YYYY-MM-DD`, isoDay(st[key]), v => {
        const day = parseTypedDay(v);
        if (!day) { toast("Type the date as 2026-09-25"); return; }
        const x = new Date(day);
        x.setHours(st[key].getHours(), st[key].getMinutes(), 0, 0);
        if (notAhead(x)) { st[key] = x; renderForm(); }
      }),
    },
    {
      name: timeName, hint: "◂ ▸ a quarter of an hour, or A to type it", type: "select", value: fmtClock(st[key]),
      adjust: (dir) => { const x = new Date(st[key].getTime() + dir * 15 * 60000); if (x <= new Date()) st[key] = x; },
      action: () => openInput(`${timeName.toUpperCase()} · HH:MM`, fmtClock(st[key]), v => {
        const t = parseTypedTime(v);
        if (!t) { toast("Type the time as 21:30"); return; }
        const x = new Date(st[key]);
        x.setHours(t.h, t.min, 0, 0);
        if (notAhead(x)) { st[key] = x; renderForm(); }
      }),
    },
  ];
}

/* One achievement's state, set by hand. The host keeps what the store says aside and carries the
   edit over every later fetch until it is undone here. */
function openAchievementEditor(gameId, a) {
  const set = achState && achState.set;
  const src = ACH_SOURCE_NAMES[set && set.source] || "The store";
  const storeUnlocked = a.edited ? a.storeUnlocked : a.unlocked;
  const storeAt = a.edited ? a.storeUnlockedAt : a.unlockedAt;
  const says = storeUnlocked ? `${src} says unlocked${storeAt ? ` on ${fmtDate(storeAt)} at ${fmtClock(storeAt)}` : ""}` : `${src} says locked`;
  // Opened on a locked one, the likely wish is to mark it unlocked, so that is where it starts.
  const st = { unlocked: true, at: a.unlocked && a.unlockedAt ? new Date(a.unlockedAt) : quarterHour(new Date()) };
  openForm({
    kind: "ach", kicker: "EDIT ACHIEVEMENT", title: a.name, sub: says.toUpperCase(), backLabel: "Cancel",
    rows: () => {
      const rows = [toggleRow("Unlocked", "Off marks it locked, whatever the store says", () => st.unlocked, v => { st.unlocked = v; })];
      if (st.unlocked) rows.push(...dayTimeRows(st, "at", "Unlocked on", "At"));
      rows.push({
        name: "Save", hint: "Kept through every refresh from the store, until you undo it here",
        type: "action", label: "Save",
        action: () => {
          send({ cmd: "achievementEdit", id: gameId, achievementId: a.id, unlocked: st.unlocked, at: st.unlocked ? st.at.toISOString() : null });
          closeForm();
        },
      });
      if (a.edited) rows.push({
        name: "Undo my edit", hint: `Back to what ${src} says`, type: "action", label: "Undo", danger: true,
        action: () => { send({ cmd: "achievementUnedit", id: gameId, achievementId: a.id }); closeForm(); },
      });
      return rows;
    },
  });
}

/* A sitting Loungepad did not see -- another PC, or with the launcher closed -- logged by hand, or
   one logged earlier being changed. `session` null logs a new one. */
function openSessionEditor(gameId, session) {
  const g = gameById(gameId);
  const st = session
    ? { start: new Date(session.start), minutes: Math.max(1, Math.round(session.seconds / 60)), counted: !!session.counted }
    : { start: quarterHour(new Date(Date.now() - 60 * 60000)), minutes: 60, counted: true };
  const end = () => new Date(st.start.getTime() + st.minutes * 60000);
  const endOk = () => end().getTime() <= Date.now() + 60000;
  openForm({
    kind: "session", kicker: session ? "EDIT SESSION" : "LOG A SESSION", title: g ? g.title : gameId, backLabel: "Cancel",
    sub: session ? "LOGGED BY HAND" : "FOR TIME LOUNGEPAD DID NOT SEE: ANOTHER PC, OR WITH THE LAUNCHER CLOSED",
    rows: () => {
      const rows = dayTimeRows(st, "start", "Day", "Started");
      rows.push({
        name: "Length", hint: "◂ ▸ five minutes at a time under an hour and a quarter above, or A to type it (1h 30m)",
        type: "select", value: fmtDuration(st.minutes * 60),
        adjust: (dir) => {
          const step = (dir > 0 ? st.minutes >= 60 : st.minutes > 60) ? 15 : 5;
          st.minutes = Math.max(5, Math.min(24 * 60, st.minutes + dir * step));
        },
        action: () => openInput("LENGTH · 1h 30m, OR MINUTES", fmtDuration(st.minutes * 60), v => {
          const m = parseTypedLength(v);
          if (!m || m < 1 || m > 1440) { toast("Type the length as 1h 30m, or in minutes"); return; }
          st.minutes = m;
          renderForm();
        }),
      });
      rows.push(toggleRow("Count toward playtime",
        "Adds it to the game's total and its session count, as a session Loungepad recorded would be. Off, it is on the timeline only",
        () => st.counted, v => { st.counted = v; }));
      rows.push({
        name: session ? "Save" : "Log it",
        hint: endOk() ? `${fmtLongDate(st.start)}, ${fmtClock(st.start)} – ${fmtClock(end())}` : "It would end in the future: start it earlier or make it shorter",
        type: "action", label: session ? "Save" : "Log", muted: !endOk(),
        action: () => {
          const times = { start: st.start.toISOString(), seconds: st.minutes * 60, counted: st.counted };
          send(session ? { cmd: "activityEdit", id: session.id, ...times } : { cmd: "activityLog", id: gameId, ...times });
          closeForm();
        },
      });
      if (session) rows.push({
        name: "Remove this session", hint: session.counted ? "Its time comes off the game's playtime too" : "The game's playtime is not changed",
        type: "action", label: "Remove", danger: true,
        action: () => askConfirm({
          title: "Remove this session?",
          body: `${fmtDuration(session.seconds)} on ${fmtDate(session.start)}.${session.counted ? " Its time comes off the game's playtime too." : ""}`,
          yesLabel: "Remove", icon: "trash", danger: true,
          onYes: () => { send({ cmd: "activityDelete", id: session.id }); closeForm(); },
        }),
      });
      return rows;
    },
  });
}

/* ============================== the unlock cards ============================== */

const unlockQueue = [];
let unlockShowing = false;
const UNLOCK_CARD_MS = 5200;

function onAchievementsUnlocked(m) {
  if (S.settings && S.settings.achievementNotifications === false) return;
  (m.items || []).forEach(item => unlockQueue.push({ item, title: m.title || "" }));
  drainUnlocks();
}

/* One card at a time, and only with the launcher on the TV: behind a game or under an overlay
   the card would go unseen, so it waits for the game message that says the session is over. */
function drainUnlocks() {
  if (unlockShowing || unlockQueue.length === 0) return;
  if (S.gameRunning || overlayMode || document.visibilityState !== "visible") return;
  const { item, title } = unlockQueue.shift();
  const card = $("unlockCard");
  if (!card) return;
  const icon = $("unlockIcon");
  const src = achIconSrc(item.iconFile, item.icon);
  icon.style.backgroundImage = src ? `url('${src}')` : "none";
  icon.innerHTML = src ? "" : iconSvg("trophy");
  $("unlockName").textContent = item.name || "";
  const band = rarityBand(item.percent);
  $("unlockSub").textContent = [title, band ? `${fmtPct(item.percent)} of players · ${band.label}` : null].filter(Boolean).join(" · ");
  $("unlockKicker").textContent = unlockQueue.length ? `ACHIEVEMENT UNLOCKED · ${unlockQueue.length + 1} NEW` : "ACHIEVEMENT UNLOCKED";
  card.hidden = false;
  card.classList.remove("leaving");
  unlockShowing = true;
  setTimeout(() => {
    card.classList.add("leaving");
    setTimeout(() => { card.hidden = true; card.classList.remove("leaving"); unlockShowing = false; drainUnlocks(); }, Math.max(1, motionMs(card)) + 20);
  }, UNLOCK_CARD_MS);
}
document.addEventListener("visibilitychange", () => setTimeout(drainUnlocks, 500));

/* ============================== a game's activity sheet ============================== */

function openActivity(gameId, from) {
  actState = { gameId, idx: 0, sessions: null, unlocks: [], from: from || view };
  showOverlay("overlay-activity");
  renderActivity();
  send({ cmd: "activityOpen", id: gameId });
}

function closeActivity() {
  if (!actState) return;
  actState = null;
  hideOverlay("overlay-activity");
}

function onActivityMessage(m) {
  if (actState && m.id === actState.gameId) {
    actState.sessions = m.sessions || [];
    actState.unlocks = (m.unlocks || []).map(u => ({ ...u, gameId: m.id }));
    renderActivity();
  }
  // A session removed from the sheet also leaves the screen's copy.
  if (statsData.sessions) statsData.sessions = statsData.sessions.filter(s => s.gameId !== m.id).concat(m.sessions || []);
  if (view === "stats") renderStats();
}

function onActivityRecorded(m) {
  const s = m.session;
  if (!s) return;
  if (actState && actState.gameId === s.gameId && actState.sessions) { actState.sessions.push(s); renderActivity(); }
  if (statsData.sessions) { statsData.sessions.push(s); if (view === "stats") renderStats(); }
}

/* Newest first: the one you just finished is the one you came to look at. */
function actSessions() { return actState && actState.sessions ? [...actState.sessions].sort((a, b) => new Date(b.start) - new Date(a.start)) : []; }

function sessionChips(s) {
  const chips = [];
  if (s.avgFps != null) chips.push(`<span class="act-chip fps">${s.avgFps} FPS${s.lowFps != null && s.lowFps < s.avgFps ? ` <i>low ${s.lowFps}</i>` : ""}</span>`);
  if (s.avgCpu != null) chips.push(`<span class="act-chip">CPU ${s.avgCpu}%</span>`);
  if (s.avgGpu != null) chips.push(`<span class="act-chip">GPU ${s.avgGpu}%</span>`);
  if (s.avgRam != null) chips.push(`<span class="act-chip">RAM ${s.avgRam}%</span>`);
  if (s.avgGpuTemp != null) chips.push(`<span class="act-chip">GPU ${s.avgGpuTemp}°</span>`);
  if (s.avgCpuTemp != null) chips.push(`<span class="act-chip">CPU ${s.avgCpuTemp}°</span>`);
  return chips.join("");
}

function statTile(label, value, sub) {
  return `<div class="stat-tile"><div class="stat-label mono">${esc(label)}</div><div class="stat-tile-value">${esc(value)}</div>${sub ? `<div class="stat-tile-sub">${esc(sub)}</div>` : ""}</div>`;
}

function renderActivity() {
  if (!actState) return;
  const g = gameById(actState.gameId);
  $("actGame").textContent = g ? g.title : "";
  const sessions = actSessions();
  const total = sessions.reduce((n, s) => n + (s.seconds || 0), 0);
  const longest = sessions.reduce((m, s) => Math.max(m, s.seconds || 0), 0);
  const fpsRows = sessions.filter(s => s.avgFps != null);
  const avgFps = fpsRows.length ? Math.round(fpsRows.reduce((n, s) => n + s.avgFps, 0) / fpsRows.length) : null;
  const tiles = [
    statTile("PLAYTIME", actState.sessions ? fmtDuration(total) : "—", g && g.playtimeMinutes && Math.abs(g.playtimeMinutes * 60 - total) > 90 ? `${fmtPlaytime(g.playtimeMinutes)} counted before the log` : null),
    statTile("SESSIONS", actState.sessions ? String(sessions.length) : "—"),
    statTile("AVERAGE", sessions.length ? fmtDuration(total / sessions.length) : "—"),
    statTile("LONGEST", sessions.length ? fmtDuration(longest) : "—"),
  ];
  if (avgFps != null) tiles.push(statTile("AVG FPS", String(avgFps), fpsRows[0].sources || null));
  if (sessions.length) tiles.push(statTile("LAST", fmtLastPlayed(sessions[0].start)));
  $("actSummary").innerHTML = tiles.join("");

  const unlocks = actState.unlocks || [];
  $("actChart").innerHTML = sessions.length
    ? barChart(withUnlocks(dayBuckets(sessions, 28), unlocks), { height: 150, fmt: v => fmtDuration(v), title: "LAST 28 DAYS", marks: true })
    : "";

  const list = $("actList");
  const keepTop = list.scrollTop;
  list.innerHTML = "";
  actState.idx = Math.max(0, Math.min(actState.idx, sessions.length - 1));
  if (actState.sessions && sessions.length === 0) {
    const empty = document.createElement("div");
    empty.className = "ach-empty";
    empty.textContent = "No sessions on record yet. The next time this game runs from Loungepad the sitting lands here -- or press Y to log one it did not see";
    list.appendChild(empty);
  }
  sessions.forEach((s, i) => {
    const el = document.createElement("div");
    el.className = "act-row";
    el.dataset.focusable = "";
    el.dataset.focusKey = "session:" + s.id;
    el.dataset.rowIndex = i;
    const got = unlocks.filter(u => unlockInSession(u, s));
    el.innerHTML =
      `<div class="act-when"><div class="act-date">${esc(fmtLastPlayed(s.start))}</div><div class="act-sub mono">${esc(fmtDate(s.start).toUpperCase())} · ${esc(fmtClock(s.start))}</div></div>` +
      `<div class="act-dur">${esc(fmtDuration(s.seconds))}</div>` +
      `<div class="act-chips">${originChip(s)}${got.length ? `<span class="act-chip ach">${iconSvg("trophy")}${got.length}</span>` : ""}${sessionChips(s) || (got.length || s.origin ? "" : `<span class="act-chip none">${s.samples ? "" : "No readings"}</span>`)}</div>` +
      (got.length ? unlockIcons(got, 3) : "") +
      `<span class="arrow">▸</span>`;
    el.addEventListener("mouseenter", () => { if (hoverEnabled() && actState && actState.idx !== i) { actState.idx = i; paintActFocus(); } });
    el.addEventListener("click", () => { if (!actState) return; actState.idx = i; actActivate(s); });
    list.appendChild(el);
  });
  list.scrollTop = keepTop;
  watchOverflow(list);
  renderActFoot(sessions);
  paintActFocus();
}

/* A hand-logged session has no readings to show, so A edits it instead. */
function actActivate(s) {
  if (s.origin === "manual") openSessionEditor(s.gameId, s);
  else openSession(s, "activity");
}

function renderActFoot(sessions) {
  const s = sessions[actState.idx];
  $("actFoot").innerHTML = foot(...(s ? [["A", s.origin === "manual" ? "Edit" : "Readings"], ["X", "Remove"]] : []), ["Y", "Log a session"], ["B", "Back"]);
}

/* Where a session came from, when it was not recorded here. */
function originChip(s) {
  if (s.origin === "manual") return `<span class="act-chip origin">LOGGED</span>`;
  if (s.origin === "playnite") return `<span class="act-chip origin">PLAYNITE</span>`;
  return "";
}

function paintActFocus() {
  if (!actState) return;
  const scope = $("overlay-activity");
  const s = actSessions()[actState.idx];
  setScopeKey(scope, s ? "session:" + s.id : null);
  const cur = focusEl(scope);
  const show = focusVisible();
  Nav.focusables(scope).forEach(el => el.classList.toggle("focused", show && el === cur));
  if (cur && show) revealFocus(cur);
}

function actInput(btn) {
  const sessions = actSessions();
  switch (btn) {
    case "Up": case "Down":
      if (listMove(btn)) {
        const el = focusEl();
        if (el && el.dataset.rowIndex !== undefined) actState.idx = parseInt(el.dataset.rowIndex, 10);
        renderActFoot(sessions);
      }
      break;
    case "A": if (focusVisible() && sessions[actState.idx]) actActivate(sessions[actState.idx]); break;
    case "Y": openSessionEditor(actState.gameId, null); break;
    case "X": {
      const s = sessions[actState.idx];
      if (!s) break;
      askConfirm({
        title: "Remove this session?",
        body: `${fmtDuration(s.seconds)} on ${fmtDate(s.start)}. ${s.origin === "manual" && s.counted ? "It was logged into the game's playtime, so its time comes off that too." : "The game's playtime total is not changed."}`,
        yesLabel: "Remove", icon: "trash", danger: true,
        onYes: () => send({ cmd: "activityDelete", id: s.id }),
      });
      break;
    }
    case "B": closeActivity(); break;
  }
}

/* ---- day and month buckets ---- */

function dayKey(d) { return `${d.getFullYear()}-${String(d.getMonth() + 1).padStart(2, "0")}-${String(d.getDate()).padStart(2, "0")}`; }
function monthKey(d) { return `${d.getFullYear()}-${String(d.getMonth() + 1).padStart(2, "0")}`; }

/* Seconds per day for the last n days, oldest first. A session is filed under the day it began. */
function dayBuckets(sessions, n) {
  const now = new Date();
  const days = [];
  const index = new Map();
  for (let i = n - 1; i >= 0; i--) {
    const d = new Date(now.getFullYear(), now.getMonth(), now.getDate() - i);
    const key = dayKey(d);
    index.set(key, days.length);
    days.push({ key, label: i === 0 ? "Today" : `${d.getDate()} ${d.toLocaleString("en", { month: "short" })}`, short: String(d.getDate()), value: 0, date: d });
  }
  sessions.forEach(s => {
    const d = new Date(s.start);
    if (isNaN(d)) return;
    const i = index.get(dayKey(d));
    if (i !== undefined) days[i].value += s.seconds || 0;
  });
  return days;
}

function monthBuckets(sessions, n, pick) {
  const now = new Date();
  const months = [];
  const index = new Map();
  for (let i = n - 1; i >= 0; i--) {
    const d = new Date(now.getFullYear(), now.getMonth() - i, 1);
    const key = monthKey(d);
    index.set(key, months.length);
    months.push({ key, label: `${d.toLocaleString("en", { month: "short" })} ${String(d.getFullYear()).slice(2)}`, short: d.toLocaleString("en", { month: "short" })[0], value: 0, date: d });
  }
  sessions.forEach(s => {
    const d = new Date(s.start);
    if (isNaN(d)) return;
    const i = index.get(monthKey(d));
    if (i !== undefined) months[i].value += pick ? pick(s) : (s.seconds || 0);
  });
  return months;
}

/* ============================== charts ==============================
   Inline SVG, sized by the box it lands in (viewBox), every colour a token from the stylesheet
   through a class: .chart-bar, .chart-line, .chart-grid, .chart-text. */

/* Bars of `value`, and with `opts.marks` each bucket's unlocks over its bar: the achievements' own
   icons, rarest first, stacked up from the bar -- as many as the column has room for -- and a "+n"
   tile for the rest. A bucket whose unlocks are only known as a count (`marks` with no `unlocks`:
   a month, or a day older than the full list) gets one tile with the number. The tiles are HTML
   laid over the SVG in percentages of the same box, because an <image> inside a stretched SVG
   would stretch with it; the SVG is given its viewBox height in pixels so the two boxes agree.
   `opts.sel` picks out one bar: the overview's day picker. */
const PIN = 26, PIN_STEP = 17;     // a tile, and how far along the next one sits (they overlap)

function barChart(buckets, opts) {
  const marks = !!opts.marks && buckets.some(b => b.marks > 0);
  const W = 1000, padB = 26;
  const n = buckets.length;
  const gap = n > 40 ? 2 : 6;
  const bw = (W - gap * (n - 1)) / n;
  // A column wide enough for three tiles side by side lays them out in a row; a narrow one
  // (a day of four weeks) stacks them up.
  const across = Math.max(1, Math.floor((bw - PIN) / PIN_STEP) + 1);
  const vertical = across < 3;
  const maxTiles = vertical ? 3 : Math.min(across, 5);
  const stacks = buckets.map(b => (marks && b.marks > 0 ? pinTiles(b, maxTiles) : null));
  const tallest = Math.max(1, ...stacks.map(t => (t ? t.length : 0)));
  const markRoom = !marks ? 0 : (vertical ? PIN + PIN_STEP * (tallest - 1) : PIN) + 10;
  const padT = (opts.title ? 26 : 8) + markRoom;
  const H = (opts.height || 150) + markRoom;
  const max = Math.max(1, ...buckets.map(b => b.value));
  let pins = "";
  const bars = buckets.map((b, i) => {
    const h = b.value > 0 ? Math.max(3, (H - padB - padT) * (b.value / max)) : 0;
    const x = i * (bw + gap);
    const y = H - padB - h;
    const sel = opts.sel === i;
    const label = n <= 14 || i % Math.ceil(n / 14) === 0 || i === n - 1 || sel ? b.short : "";
    const tip = `${b.label}: ${opts.fmt ? opts.fmt(b.value) : String(b.value)}${b.marks ? ` · ${b.marks} unlocked` : ""}`;
    // Every bucket also carries a full-height hit area with its index, so a click anywhere in its
    // column can pick it (the day picker's mouse path).
    let out = `<rect class="chart-hit" data-i="${i}" x="${(x - gap / 2).toFixed(1)}" y="0" width="${(bw + gap).toFixed(1)}" height="${H}"/>`;
    if (sel) out += `<rect class="chart-sel" data-i="${i}" x="${(x - gap / 2).toFixed(1)}" y="${padT - markRoom}" width="${(bw + gap).toFixed(1)}" height="${H - padT + markRoom}" rx="6"/>`;
    out += b.value > 0 ? `<rect class="chart-bar${sel ? " sel" : ""}" data-i="${i}" x="${x.toFixed(1)}" y="${y.toFixed(1)}" width="${bw.toFixed(1)}" height="${h.toFixed(1)}" rx="3"><title>${esc(tip)}</title></rect>`
      : `<rect class="chart-bar empty" data-i="${i}" x="${x.toFixed(1)}" y="${(H - padB - 2).toFixed(1)}" width="${bw.toFixed(1)}" height="2" rx="1"><title>${esc(tip)}</title></rect>`;
    if (stacks[i]) {
      const top = (b.value > 0 ? y : H - padB) - 5;
      pins += `<div class="chart-pin${vertical ? " v" : ""}${sel ? " sel" : ""}" data-i="${i}" title="${esc(tip)}" style="left:${((x + bw / 2) / W * 100).toFixed(2)}%;top:${(top / H * 100).toFixed(2)}%">${stacks[i].join("")}</div>`;
    }
    if (label) out += `<text class="chart-text${sel ? " sel" : ""}" x="${(x + bw / 2).toFixed(1)}" y="${H - 6}" text-anchor="middle">${esc(label)}</text>`;
    return out;
  }).join("");
  let top = "";
  if (opts.title) {
    top = `<text class="chart-text head" x="0" y="14">${esc(opts.title)}</text>` +
      `<text class="chart-text head" x="${W}" y="14" text-anchor="end">PEAK ${esc(opts.fmt ? opts.fmt(max) : String(max))}</text>`;
    // The key for the tiles, after the title. Mono type, so its width is the character count.
    if (marks) {
      const x0 = opts.title.length * 8.9 + 26;
      const total = buckets.reduce((s, b) => s + (b.marks || 0), 0);
      top += `<rect class="chart-mark" x="${(x0 - 5).toFixed(1)}" y="4.5" width="10" height="10" rx="2.5"/>` +
        `<text class="chart-text head mark" x="${(x0 + 11).toFixed(1)}" y="14">${fmtInt(total)} UNLOCKED</text>`;
    }
  }
  const svg = `<svg class="chart" viewBox="0 0 ${W} ${H}" preserveAspectRatio="none" style="height:${H}px">${top}${bars}</svg>`;
  return pins ? `<div class="chart-box">${svg}<div class="chart-pins">${pins}</div></div>` : svg;
}

/* One bucket's tiles, nearest the bar first. The rarest are the ones worth a face: a day of
   twelve common unlocks and one ultra rare shows the ultra rare. */
function pinTiles(b, maxTiles) {
  const rank = (u) => (typeof u.item.percent === "number" ? u.item.percent : 101);
  const known = (b.unlocks || []).slice().sort((x, y) => rank(x) - rank(y));
  const total = Math.max(b.marks || 0, known.length);
  if (!known.length) return [`<b class="pin-tile count">${total > 99 ? "99+" : total}</b>`];
  const fit = total <= maxTiles ? total : maxTiles - 1;
  // Each tile is stacked over the one before it, so the "+n" at the end is never covered.
  // Chrome painted a column-reverse stack the other way round without the explicit order.
  const tiles = known.slice(0, fit).map((u, i) => {
    const src = achIconSrc(u.item.iconFile, u.item.icon);
    return src ? `<i class="pin-tile"${achIconStyle(src, `z-index:${i};`)}></i>` : `<i class="pin-tile none" style="z-index:${i}">${iconSvg("trophy")}</i>`;
  });
  const rest = total - tiles.length;
  if (rest > 0) tiles.push(`<b class="pin-tile more" style="z-index:${tiles.length}">+${rest > 99 ? 99 : rest}</b>`);
  return tiles;
}

/* Puts each bucket's own unlocks on it as `unlocks` (day keys, or yyyy-mm for month buckets),
   raising `marks` to match where the counts came from somewhere shorter. */
function withUnlocks(buckets, unlocks) {
  const monthly = buckets.length > 0 && buckets[0].key.length === 7;
  const by = new Map();
  (unlocks || []).forEach(u => {
    const d = new Date(u.at);
    if (isNaN(d)) return;
    const k = monthly ? monthKey(d) : dayKey(d);
    if (!by.has(k)) by.set(k, []);
    by.get(k).push(u);
  });
  buckets.forEach(b => {
    b.unlocks = by.get(b.key) || [];
    b.marks = Math.max(b.marks || 0, b.unlocks.length);
  });
  return buckets;
}

/* One or two lines over a session's length. Series: [{ name, points: [{t, v}], cls }]. */
function lineChart(series, opts) {
  const W = 1000, H = opts.height || 150, padL = 44, padR = 10, padT = 26, padB = 22;
  const all = series.flatMap(s => s.points);
  if (!all.length) return "";
  const tMax = Math.max(1, ...all.map(p => p.t));
  let vMin = opts.min !== undefined ? opts.min : Math.min(...all.map(p => p.v));
  let vMax = opts.max !== undefined ? opts.max : Math.max(...all.map(p => p.v));
  if (opts.max === undefined) vMax = Math.ceil((vMax + 1) / 10) * 10;
  if (opts.min === undefined) vMin = Math.max(0, Math.floor((vMin - 1) / 10) * 10);
  if (vMax <= vMin) vMax = vMin + 10;
  const sx = (t) => padL + (W - padL - padR) * (t / tMax);
  const sy = (v) => padT + (H - padT - padB) * (1 - (v - vMin) / (vMax - vMin));
  const grid = [0, 0.5, 1].map(f => {
    const v = vMin + (vMax - vMin) * f;
    return `<line class="chart-grid" x1="${padL}" x2="${W - padR}" y1="${sy(v).toFixed(1)}" y2="${sy(v).toFixed(1)}"/>` +
      `<text class="chart-text" x="${padL - 8}" y="${(sy(v) + 4).toFixed(1)}" text-anchor="end">${Math.round(v)}${esc(opts.unit || "")}</text>`;
  }).join("");
  const lines = series.map(s => {
    if (!s.points.length) return "";
    const d = s.points.map((p, i) => `${i ? "L" : "M"}${sx(p.t).toFixed(1)} ${sy(p.v).toFixed(1)}`).join(" ");
    return `<path class="chart-line ${s.cls || ""}" d="${d}"/>`;
  }).join("");
  const ticks = [0, 0.5, 1].map(f => `<text class="chart-text" x="${sx(tMax * f).toFixed(1)}" y="${H - 5}" text-anchor="${f === 0 ? "start" : f === 1 ? "end" : "middle"}">${esc(fmtDuration(tMax * f))}</text>`).join("");
  const legend = series.length > 1 ? series.map((s, i) => `<text class="chart-text ${s.cls || ""}" x="${W - padR - i * 120}" y="14" text-anchor="end">${esc(s.name)}</text>`).join("") : "";
  // Each unlock where it happened, as a gold rule down the chart: a frame-rate dip or a GPU peak
  // next to one is usually the fight it was for. `opts.marks`: [{ t, label }].
  const marks = (opts.marks || []).filter(m => m.t >= 0 && m.t <= tMax).map(m =>
    `<line class="chart-mark-line" x1="${sx(m.t).toFixed(1)}" x2="${sx(m.t).toFixed(1)}" y1="${padT}" y2="${H - padB}"/>` +
    `<circle class="chart-mark" cx="${sx(m.t).toFixed(1)}" cy="${padT}" r="4.5"><title>${esc(m.label)}</title></circle>`).join("");
  return `<svg class="chart" viewBox="0 0 ${W} ${H}" preserveAspectRatio="none">` +
    `<text class="chart-text head" x="${padL}" y="14">${esc(opts.title || "")}</text>${legend}${grid}${marks}${lines}${ticks}</svg>`;
}

/* ============================== a session's readings ============================== */

function openSession(session, from) {
  sessionState = { session, samples: null, unlocks: [], from };
  showOverlay("overlay-session");
  renderSession();
  send({ cmd: "activitySession", id: session.id });
}

function closeSession() {
  if (!sessionState) return;
  sessionState = null;
  hideOverlay("overlay-session");
  if (actState) paintActFocus();
  else if (dayState) paintDayFocus();
  else if (view === "stats") paintNav();
}

function onSessionMessage(m) {
  if (!sessionState || m.id !== sessionState.session.id) return;
  if (m.session) sessionState.session = m.session;
  sessionState.samples = m.samples || [];
  sessionState.unlocks = (m.unlocks || []).map(u => ({ ...u, gameId: sessionState.session.gameId }));
  renderSession();
}

function renderSession() {
  if (!sessionState) return;
  const s = sessionState.session;
  const g = gameById(s.gameId);
  $("sessionGame").textContent = g ? g.title : s.gameId;
  const start = new Date(s.start);
  $("sessionWhen").textContent = `${fmtDate(s.start)} · ${fmtClock(s.start)} – ${fmtClock(s.end)}${s.sources ? " · " + s.sources : ""}`.toUpperCase();

  // What the sitting unlocked, in order, each at its time into the session: the same clock the
  // charts' x axis runs on, and a gold rule on every chart at that point.
  const unlocks = [...(sessionState.unlocks || [])].sort((a, b) => new Date(a.at) - new Date(b.at));
  const into = (u) => Math.max(0, Math.min(s.seconds, (new Date(u.at) - start) / 1000));
  const marks = unlocks.map(u => ({ t: into(u), label: `${u.item.name} · ${fmtClock(u.at)}` }));
  const strip = $("sessionUnlocks");
  if (strip) {
    strip.hidden = !unlocks.length;
    strip.innerHTML = unlocks.length
      ? `<div class="session-unlocks-head mono">${iconSvg("trophy")}UNLOCKED THIS SESSION · ${unlocks.length}</div>` +
        `<div class="session-unlock-list">${unlocks.slice(0, 8).map(u => unlockChip(u, "AT " + fmtDuration(into(u)).toUpperCase())).join("")}` +
        `${unlocks.length > 8 ? `<span class="day-more mono">+${unlocks.length - 8} MORE</span>` : ""}</div>`
      : "";
  }

  const tiles = [statTile("DURATION", fmtDuration(s.seconds))];
  if (unlocks.length) {
    const rarest = unlocks.filter(u => typeof u.item.percent === "number").sort((a, b) => a.item.percent - b.item.percent)[0];
    tiles.push(statTile("UNLOCKED", String(unlocks.length), rarest ? `rarest ${fmtPct(rarest.item.percent)} of players` : null));
  }
  if (s.avgFps != null) tiles.push(statTile("FRAME RATE", `${s.avgFps} avg`, s.lowFps != null ? `${s.lowFps} in the slowest 5%` : null));
  if (s.avgCpu != null) tiles.push(statTile("CPU", `${s.avgCpu}%`, s.avgCpuTemp != null ? `${s.avgCpuTemp}° avg · ${s.maxCpuTemp}° peak` : null));
  if (s.avgGpu != null) tiles.push(statTile("GPU", `${s.avgGpu}%`, s.avgGpuTemp != null ? `${s.avgGpuTemp}° avg · ${s.maxGpuTemp}° peak` : null));
  if (s.avgRam != null) tiles.push(statTile("MEMORY", `${s.avgRam}%`, s.peakRamMb ? `${(s.peakRamMb / 1024).toFixed(1)} GB peak` : null));
  if (s.avgGpuPower != null || s.avgCpuPower != null) tiles.push(statTile("POWER", [s.avgGpuPower != null ? `GPU ${s.avgGpuPower} W` : null, s.avgCpuPower != null ? `CPU ${s.avgCpuPower} W` : null].filter(Boolean).join(" · ")));
  $("sessionSummary").innerHTML = tiles.join("");

  const charts = $("sessionCharts");
  const samples = sessionState.samples;
  if (!samples) charts.innerHTML = `<div class="ach-empty">Loading the readings…</div>`;
  else if (!samples.length) charts.innerHTML = `<div class="ach-empty">${s.origin === "manual" ? "Logged by hand, so there are no readings" : s.origin === "playnite" ? "Brought from Playnite without readings" : "No readings were taken during this session"}</div>`;
  else {
    const pts = (key) => samples.filter(x => x[key] != null).map(x => ({ t: x.t, v: x[key] }));
    const blocks = [];
    const fps = pts("fps");
    if (fps.length) blocks.push(lineChart([{ name: "FPS", points: fps, cls: "fps" }], { title: "FRAME RATE", min: 0, unit: "", marks }));
    const cpu = pts("cpu"), gameCpu = pts("gameCpu");
    if (cpu.length) blocks.push(lineChart([{ name: "System", points: cpu, cls: "cpu" }, ...(gameCpu.length ? [{ name: "Game", points: gameCpu, cls: "game" }] : [])], { title: "CPU", min: 0, max: 100, unit: "%", marks }));
    const gpu = pts("gpu");
    if (gpu.length) blocks.push(lineChart([{ name: "GPU", points: gpu, cls: "gpu" }], { title: "GPU", min: 0, max: 100, unit: "%", marks }));
    const ram = pts("ram");
    if (ram.length) blocks.push(lineChart([{ name: "RAM", points: ram, cls: "ram" }], { title: "MEMORY", min: 0, max: 100, unit: "%", marks }));
    const cpuT = pts("cpuTemp"), gpuT = pts("gpuTemp");
    if (cpuT.length || gpuT.length) blocks.push(lineChart([...(gpuT.length ? [{ name: "GPU", points: gpuT, cls: "gpu" }] : []), ...(cpuT.length ? [{ name: "CPU", points: cpuT, cls: "cpu" }] : [])], { title: "TEMPERATURE", unit: "°", marks }));
    charts.innerHTML = blocks.map(b => `<div class="session-chart">${b}</div>`).join("") || `<div class="ach-empty">The readings carried nothing this can chart</div>`;
  }
  $("sessionFoot").innerHTML = foot(["B", "Back"]);
}

function sessionInput(btn) {
  if (btn === "B") closeSession();
}

/* ============================== the Stats screen ============================== */

const STATS_TABS = [
  { id: "overview",     label: "Overview" },
  { id: "playtime",     label: "Playtime" },
  { id: "achievements", label: "Achievements" },
];
const PERIODS = [["7d", "Last 7 days"], ["30d", "Last 30 days"], ["12m", "Last 12 months"], ["all", "All time"]];
const ACH_GAME_SORTS = [["progress", "By progress"], ["recent", "Recently unlocked"], ["count", "Most unlocked"], ["name", "A – Z"]];

/* The two ways in: LB on the library, and the top row of Settings → Stats. B goes back to the
   one it came from, so a look at the numbers from Settings does not strand you on the library. */
function openStats(from) {
  statsUi.from = from || "library";
  switchView("stats");
}

function leaveStats() {
  if (statsUi.from === "settings") {
    switchView("settings");
    setSettingsTab("stats");
    settingsIdx = 0;
    enterSettingsPane("rows");
  } else switchView("library");
}

/* Back from a game's page keeps the place; any other way in starts on the categories. */
function openStatsView(prev) {
  if (prev !== "detail") {
    statsUi.pane = "nav";
    statsUi.idx = 0;
    statsUi.day = null;
  }
  requestStats(true);
  renderStats();
}

function requestStats(sessionsToo) {
  if (sessionsToo) send({ cmd: "activityAll" });
  send({ cmd: "achievementsAll" });
}

function onActivityAll(m) {
  statsData.sessions = m.sessions || [];
  if (m.sources) statsData.sources = m.sources;
  if (view === "stats") renderStats();
  if (dayState) renderDay();
  if (view === "settings" && settingsTab === "stats") renderSettings();
}

function onAchievementsAll(m) {
  statsData.ach = {
    games: m.games || [], recent: m.recent || [], unlocksByDay: m.unlocksByDay || {},
    unlockedPercents: m.unlockedPercents || [], providers: m.providers || [], unlocksByMonth: m.unlocksByMonth || [],
  };
  if (view === "stats") renderStats();
  if (dayState) renderDay();
}

function periodStart(id) {
  const now = new Date();
  if (id === "7d") return new Date(now.getFullYear(), now.getMonth(), now.getDate() - 6);
  if (id === "30d") return new Date(now.getFullYear(), now.getMonth(), now.getDate() - 29);
  if (id === "12m") return new Date(now.getFullYear(), now.getMonth() - 11, 1);
  return null;
}

function sessionsIn(period) {
  const all = statsData.sessions || [];
  const since = periodStart(period);
  return since ? all.filter(s => new Date(s.start) >= since) : all;
}

function gameTitle(id) { const g = gameById(id); return g ? g.title : id; }
function gamePlatform(id) { const g = gameById(id); return g ? g.platform : "Gone"; }

/* Seconds per game, most first. */
function topGames(sessions) {
  const by = new Map();
  sessions.forEach(s => by.set(s.gameId, (by.get(s.gameId) || 0) + (s.seconds || 0)));
  return [...by.entries()].map(([gameId, seconds]) => ({ gameId, seconds })).sort((a, b) => b.seconds - a.seconds);
}

/* ---- days: sessions and unlocks together ---- */

/* The day picker's span. The host sends every unlock of the last 35 days in full
   (UiBridge.RecentUnlockDays), so each of these days has all of its own; older unlocks come only
   as counts per day, which is all the other charts need. */
const DAY_PICKER_DAYS = 28;

function recentUnlocks() { return (statsData.ach && statsData.ach.recent) || []; }
function unlocksByDay() { return (statsData.ach && statsData.ach.unlocksByDay) || {}; }

/* The first day whose unlocks are all in `recent`: before it, a session's trophies would be
   missing rather than absent, so they are not drawn at all. */
function unlocksKnownSince() {
  const d = new Date();
  return new Date(d.getFullYear(), d.getMonth(), d.getDate() - 34);
}

function dayStartOf(key) {
  const [y, m, d] = key.split("-").map(Number);
  return new Date(y, m - 1, d);
}

/* One day: its sessions oldest first with what each unlocked, and the unlocks that fall in no
   recorded session (another device, or a game Loungepad did not start), at their own time. A
   session is filed under the day it began, so an unlock after midnight in a session begun the
   evening before stays with that session. */
function dayDetail(key) {
  const all = statsData.sessions || [];
  const sessions = all.filter(s => dayKey(new Date(s.start)) === key).sort((a, b) => new Date(a.start) - new Date(b.start));
  const unlocks = recentUnlocks();
  const byTime = (a, b) => new Date(a.at) - new Date(b.at);
  const bySession = new Map(sessions.map(s => [s.id, unlocks.filter(u => unlockInSession(u, s)).sort(byTime)]));
  const loose = unlocks.filter(u => dayKey(new Date(u.at)) === key && !all.some(s => unlockInSession(u, s))).sort(byTime);
  const listed = sessions.flatMap(s => bySession.get(s.id)).concat(loose).sort(byTime);
  const seconds = sessions.reduce((n, s) => n + (s.seconds || 0), 0);
  const games = [...new Set(sessions.map(s => s.gameId).concat(loose.map(u => u.gameId)))];
  return { key, sessions, bySession, loose, listed, seconds, games };
}

/* Days in the picker's span with anything in them, oldest first. */
function activeDays() {
  const since = new Date();
  since.setHours(0, 0, 0, 0);
  since.setDate(since.getDate() - (DAY_PICKER_DAYS - 1));
  const set = new Set();
  (statsData.sessions || []).forEach(s => { const d = new Date(s.start); if (d >= since) set.add(dayKey(d)); });
  recentUnlocks().forEach(u => { const d = new Date(u.at); if (d >= since) set.add(dayKey(d)); });
  return [...set].sort();
}

/* An unlock as a chip: icon, name, and the time with the rarity band. */
function unlockChip(u, sub) {
  const src = achIconSrc(u.item.iconFile, u.item.icon);
  const band = rarityBand(u.item.percent);
  return `<span class="day-unlock"><i class="ach-icon tiny"${achIconStyle(src)}>${src ? "" : iconSvg("trophy")}</i>` +
    `<span class="day-unlock-text"><span class="day-unlock-name">${esc(u.item.name)}</span>` +
    `<span class="day-unlock-sub mono">${esc(sub !== undefined ? sub : fmtClock(u.at))}${band ? " · " + esc(band.label.toUpperCase()) : ""}</span></span></span>`;
}

function gamesLine(ids) {
  if (!ids.length) return "";
  const names = ids.slice(0, 3).map(gameTitle);
  return names.join(", ") + (ids.length > 3 ? ` and ${ids.length - 3} more` : "");
}

/* The overview's chart, as a row: the last four weeks of playtime with each day's unlocks over
   its bar, one day picked out, and under the chart what that day held. ◂ ▸ walk the days and A
   opens the day's timeline. One focusable, so the D-pad passes over it in a single step. */
function dayPickerRow() {
  const days = withUnlocks(withMarks(dayBuckets(statsData.sessions || [], DAY_PICKER_DAYS), unlocksByDay()), recentUnlocks());
  const keys = days.map(d => d.key);
  if (!statsUi.day || !keys.includes(statsUi.day)) {
    const active = days.filter(d => d.value > 0 || d.marks > 0);
    statsUi.day = active.length ? active[active.length - 1].key : keys[keys.length - 1];
  }
  const d = dayDetail(statsUi.day);
  const bits = [];
  if (d.seconds) bits.push(`${fmtDuration(d.seconds)} over ${d.sessions.length} session${d.sessions.length === 1 ? "" : "s"}`);
  if (d.games.length) bits.push(gamesLine(d.games));
  const n = d.listed.length;
  const MAX_CHIPS = 6;
  const chips = d.listed.slice(0, MAX_CHIPS).map(u => unlockChip(u)).join("") +
    (n > MAX_CHIPS ? `<span class="day-more mono">+${n - MAX_CHIPS} MORE</span>` : "");
  const html =
    `<div class="daypick-chart">${barChart(days, { height: 190, fmt: fmtDuration, title: "LAST 28 DAYS", marks: true, sel: keys.indexOf(statsUi.day) })}</div>` +
    `<div class="daypick-head">` +
      `<div class="daypick-text"><div class="set-name">${esc(fmtDayName(statsUi.day, true))}</div>` +
      `<div class="set-hint">${esc(bits.join(" · ") || (n ? "Nothing played from Loungepad" : "Nothing played or unlocked"))}</div></div>` +
      `<div class="daypick-count${n ? "" : " none"}">${iconSvg("trophy")}<b>${n}</b><span class="mono">UNLOCKED</span></div>` +
    `</div>` +
    (n ? `<div class="daypick-unlocks">${chips}</div>` : "");
  const empty = !d.sessions.length && !n;
  return {
    html, cls: "stats-daypick",
    adjust: (dir) => {
      const i = keys.indexOf(statsUi.day);
      const j = Math.max(0, Math.min(keys.length - 1, i + dir));
      if (j !== i) { statsUi.day = keys[j]; renderStats(); }
    },
    action: empty ? null : () => openDay(statsUi.day),
    foot: [["DpadH", "Day"], ...(empty ? [] : [["A", "Timeline"]]), ["B", "Categories"]],
  };
}

/* The rows and blocks of the active category. A block is { kind: "tiles" | "chart" | "section" |
   "note" } and is not focusable; a row is { name, hint, valueHtml, action, adjust } -- or
   { html, cls } for one that draws itself -- and is. */
function statsBlocks() {
  const blocks = [];
  const sessions = statsData.sessions;
  const ach = statsData.ach;
  const loading = !sessions || !ach;
  const byDay = unlocksByDay();
  if (statsUi.tab === "overview") {
    const all = sessions || [];
    const total = all.reduce((n, s) => n + (s.seconds || 0), 0);
    const week = sessionsIn("7d").reduce((n, s) => n + (s.seconds || 0), 0);
    const todayKey = dayKey(new Date());
    const today = all.filter(s => dayKey(new Date(s.start)) === todayKey).reduce((n, s) => n + (s.seconds || 0), 0);
    const games = new Set(all.map(s => s.gameId)).size;
    const unlocked = ach ? ach.games.reduce((n, g) => n + g.summary.unlocked, 0) : 0;
    const totalAch = ach ? ach.games.reduce((n, g) => n + g.summary.total, 0) : 0;
    const weekStart = dayKey(periodStart("7d"));
    const weekUnlocks = Object.entries(byDay).reduce((n, [k, c]) => n + (k >= weekStart ? c : 0), 0);
    const todayUnlocks = byDay[todayKey] || 0;
    blocks.push({ kind: "tiles", tiles: [
      ["PLAYTIME", loading ? "—" : fmtDuration(total), `${all.length} session${all.length === 1 ? "" : "s"} · ${games} game${games === 1 ? "" : "s"}`],
      ["THIS WEEK", loading ? "—" : fmtDuration(week), loading ? null : `${weekUnlocks} unlocked`],
      ["TODAY", loading ? "—" : fmtDuration(today), loading ? null : `${todayUnlocks} unlocked`],
      ["ACHIEVEMENTS", loading ? "—" : `${fmtInt(unlocked)} / ${fmtInt(totalAch)}`, totalAch ? `${Math.round(100 * unlocked / totalAch)}% across ${ach.games.filter(g => g.summary.total > 0).length} games` : null],
    ] });
    blocks.push(dayPickerRow());
    const top = topGames(sessionsIn("30d")).slice(0, 5);
    blocks.push({ kind: "section", text: "MOST PLAYED · LAST 30 DAYS" });
    if (!top.length) blocks.push({ kind: "note", text: loading ? "Loading…" : "No sessions in the last 30 days. Play something from Loungepad and it lands here" });
    const max = top.length ? top[0].seconds : 1;
    const since30 = periodStart("30d");
    top.forEach(t => {
      const got = recentUnlocks().filter(u => u.gameId === t.gameId && new Date(u.at) >= since30).length;
      blocks.push({
        name: gameTitle(t.gameId), hint: `${gamePlatform(t.gameId)}${got ? ` · ${got} unlocked in these 30 days` : ""}`,
        valueHtml: barValue(t.seconds / max, fmtDuration(t.seconds)),
        action: () => { if (gameById(t.gameId)) openDetail(t.gameId, "stats"); },
      });
    });
    blocks.push({ kind: "section", text: "LATEST ACHIEVEMENTS" });
    const recent = recentUnlocks().slice(0, 8);
    if (!recent.length) blocks.push({ kind: "note", text: loading ? "Loading…" : "Nothing unlocked on record yet" });
    recent.forEach(r => blocks.push(achievementRow(r)));
  } else if (statsUi.tab === "playtime") {
    const list = sessionsIn(statsUi.period);
    const total = list.reduce((n, s) => n + (s.seconds || 0), 0);
    const longest = list.reduce((m, s) => Math.max(m, s.seconds || 0), 0);
    const label = (PERIODS.find(p => p[0] === statsUi.period) || PERIODS[1])[1];
    blocks.push({
      name: "Period", hint: "◂ ▸ to change what the numbers below cover",
      valueHtml: `<span class="arrow" data-dir="-1">◂</span><span>${esc(label)}</span><span class="arrow" data-dir="1">▸</span>`,
      adjust: (dir) => { const i = PERIODS.findIndex(p => p[0] === statsUi.period); statsUi.period = PERIODS[(i + dir + PERIODS.length) % PERIODS.length][0]; renderStats(); },
    });
    const days = new Set(list.map(s => dayKey(new Date(s.start)))).size;
    const since = periodStart(statsUi.period);
    const unlocksIn = since ? Object.entries(byDay).reduce((n, [k, c]) => n + (k >= dayKey(since) ? c : 0), 0) : Object.values(byDay).reduce((n, c) => n + c, 0);
    blocks.push({ kind: "tiles", tiles: [
      ["PLAYTIME", fmtDuration(total)],
      ["SESSIONS", String(list.length), list.length ? `${fmtDuration(total / list.length)} on average` : null],
      ["DAYS PLAYED", String(days)],
      ["UNLOCKED", String(unlocksIn), longest ? `longest session ${fmtDuration(longest)}` : null],
    ] });
    const chart = statsUi.period === "7d" ? barChart(withUnlocks(withMarks(dayBuckets(list, 7), byDay), recentUnlocks()), { height: 170, fmt: fmtDuration, title: "BY DAY", marks: true })
      : statsUi.period === "30d" ? barChart(withUnlocks(withMarks(dayBuckets(list, 30), byDay), recentUnlocks()), { height: 170, fmt: fmtDuration, title: "BY DAY", marks: true })
      : barChart(withMarks(monthBuckets(list, statsUi.period === "12m" ? 12 : Math.max(12, monthsSpanned(list))), byDay), { height: 170, fmt: fmtDuration, title: "BY MONTH", marks: true });
    blocks.push({ kind: "chart", html: chart });
    blocks.push({ kind: "section", text: "BY GAME" });
    const top = topGames(list).slice(0, 12);
    if (!top.length) blocks.push({ kind: "note", text: sessions ? "No sessions in this period" : "Loading…" });
    const max = top.length ? top[0].seconds : 1;
    top.forEach(t => blocks.push({
      name: gameTitle(t.gameId), hint: `${gamePlatform(t.gameId)} · ${list.filter(s => s.gameId === t.gameId).length} sessions`,
      valueHtml: barValue(t.seconds / max, `${fmtDuration(t.seconds)} · ${Math.round(100 * t.seconds / Math.max(1, total))}%`),
      action: () => { if (gameById(t.gameId)) openActivity(t.gameId, "stats"); },
    }));
    const plat = new Map();
    list.forEach(s => { const p = gamePlatform(s.gameId); plat.set(p, (plat.get(p) || 0) + (s.seconds || 0)); });
    const plats = [...plat.entries()].sort((a, b) => b[1] - a[1]);
    if (plats.length > 1) {
      blocks.push({ kind: "section", text: "BY STORE" });
      plats.forEach(([p, secs]) => blocks.push({ kind: "note", html: `<span class="stats-plat"><b>${esc(p)}</b>${barValue(secs / Math.max(1, plats[0][1]), `${fmtDuration(secs)} · ${Math.round(100 * secs / Math.max(1, total))}%`)}</span>` }));
    }
    blocks.push({ kind: "section", text: "RECENT SESSIONS" });
    const known = unlocksKnownSince();
    [...list].sort((a, b) => new Date(b.start) - new Date(a.start)).slice(0, 10).forEach(s => {
      const got = new Date(s.start) >= known ? recentUnlocks().filter(u => unlockInSession(u, s)) : [];
      blocks.push({
        name: gameTitle(s.gameId), hint: `${fmtLastPlayed(s.start)} · ${fmtClock(s.start)} · ${fmtDuration(s.seconds)}`,
        valueHtml: `<span class="act-chips">${got.length ? `<span class="act-chip ach">${iconSvg("trophy")}${got.length}</span>` : ""}${sessionChips(s)}</span><span class="arrow">▸</span>`,
        action: () => openSession(s, "stats"),
      });
    });
  } else {
    const games = ach ? ach.games.filter(g => g.summary.total > 0) : [];
    const unlocked = games.reduce((n, g) => n + g.summary.unlocked, 0);
    const total = games.reduce((n, g) => n + g.summary.total, 0);
    const done = games.filter(g => g.summary.unlocked >= g.summary.total).length;
    const bands = { ultra: 0, rare: 0, uncommon: 0, common: 0 };
    (ach ? ach.unlockedPercents : []).forEach(p => { const b = rarityBand(p); if (b) bands[b.id]++; });
    blocks.push({ kind: "tiles", tiles: [
      ["UNLOCKED", loading ? "—" : `${fmtInt(unlocked)} / ${fmtInt(total)}`, total ? `${Math.round(100 * unlocked / total)}% of everything on record` : null],
      ["GAMES", String(games.length), done ? `${done} at 100%` : null],
      ["RARE", loading ? "—" : String(bands.rare + bands.ultra), bands.ultra ? `${bands.ultra} ultra rare` : null],
      ["COMMON", loading ? "—" : String(bands.common + bands.uncommon), bands.uncommon ? `${bands.uncommon} uncommon` : null],
    ] });
    if (ach && ach.unlocksByMonth.length) blocks.push({ kind: "chart", html: barChart(ach.unlocksByMonth.map(m => ({ ...m, short: m.label.slice(0, 3) })), { height: 170, fmt: v => `${v}`, title: "UNLOCKED · LAST 12 MONTHS" }) });
    (ach ? ach.providers : []).filter(p => p.blocked && p.games > 0).forEach(p => blocks.push({
      kind: "note", text: `${ACH_SOURCE_NAMES[p.source] || p.source}: ${p.blocked}`,
    }));
    const sortLabel = (ACH_GAME_SORTS.find(s => s[0] === statsUi.achSort) || ACH_GAME_SORTS[0])[1];
    blocks.push({
      name: "Games", hint: "◂ ▸ to change the order",
      valueHtml: `<span class="arrow" data-dir="-1">◂</span><span>${esc(sortLabel)}</span><span class="arrow" data-dir="1">▸</span>`,
      adjust: (dir) => { const i = ACH_GAME_SORTS.findIndex(s => s[0] === statsUi.achSort); statsUi.achSort = ACH_GAME_SORTS[(i + dir + ACH_GAME_SORTS.length) % ACH_GAME_SORTS.length][0]; renderStats(); },
    });
    const share = (g) => g.summary.unlocked / g.summary.total;
    const when = (g) => (g.summary.lastUnlock ? new Date(g.summary.lastUnlock).getTime() : 0);
    const sorted = [...games].sort(
      statsUi.achSort === "recent" ? (a, b) => when(b) - when(a) || share(b) - share(a)
      : statsUi.achSort === "count" ? (a, b) => b.summary.unlocked - a.summary.unlocked || share(b) - share(a)
      : statsUi.achSort === "name" ? (a, b) => a.title.localeCompare(b.title)
      : (a, b) => share(b) - share(a) || b.summary.unlocked - a.summary.unlocked || a.title.localeCompare(b.title));
    if (!sorted.length) blocks.push({ kind: "note", text: loading ? "Loading…" : "No achievements on record yet. Lists are fetched in the background after each scan, for games that are installed or have been played" });
    sorted.slice(0, 60).forEach(g => {
      const pct = Math.round(100 * share(g));
      blocks.push({
        name: g.title, hint: `${ACH_SOURCE_NAMES[g.summary.source] || g.summary.source}${g.summary.lastUnlock ? " · last unlocked " + fmtDate(g.summary.lastUnlock) : ""}`,
        valueHtml: barValue(share(g), `${g.summary.unlocked} / ${g.summary.total} · ${pct}%`, pct >= 100),
        action: () => { if (gameById(g.gameId)) openAchievements(g.gameId, "stats"); },
      });
    });
  }
  return blocks;
}

function monthsSpanned(list) {
  if (!list.length) return 12;
  const first = list.reduce((m, s) => Math.min(m, new Date(s.start).getTime()), Infinity);
  const d = new Date(first), now = new Date();
  return (now.getFullYear() - d.getFullYear()) * 12 + (now.getMonth() - d.getMonth()) + 1;
}

function barValue(share, text, done) {
  const w = Math.max(0, Math.min(100, Math.round(100 * share)));
  return `<span class="stats-bar${done ? " done" : ""}"><i style="width:${w}%"></i></span><span class="stats-bar-text">${esc(text)}</span>`;
}

function achievementRow(r) {
  const band = rarityBand(r.item.percent);
  const src = achIconSrc(r.item.iconFile, r.item.icon);
  const at = new Date(r.at);
  return {
    name: r.item.name, hint: `${r.title} · ${fmtDayName(dayKey(at))} at ${fmtClock(at)}`,
    iconHtml: `<div class="ach-icon small"${achIconStyle(src)}>${src ? "" : iconSvg("trophy")}</div>`,
    valueHtml: band ? `<span class="ach-rarity" data-band="${band.id}">${esc(fmtPct(r.item.percent))} · ${esc(band.label.toUpperCase())}</span>` : "",
    action: () => { if (gameById(r.gameId)) openAchievements(r.gameId, "stats", r.item.id); },
  };
}

/* Laid out like Settings, updated the same way: the nav is built once, the scroller only when
   its shape changes, and a highlight move repaints classes. */
function renderStats() {
  const nav = $("statsNav");
  if (!nav) return;
  if (nav.children.length !== STATS_TABS.length) {
    nav.innerHTML = "";
    STATS_TABS.forEach(t => {
      const el = document.createElement("div");
      el.className = "set-tab";
      el.dataset.focusable = "";
      el.dataset.focusKey = "statstab:" + t.id;
      el.dataset.statsTab = t.id;
      el.innerHTML = `<span>${esc(t.label)}</span>`;
      el.addEventListener("click", () => { setFocusEl(el); setStatsTab(t.id); });
      el.addEventListener("mouseenter", () => { if (!hoverEnabled()) return; setFocusEl(el); paintNav(); });
      nav.appendChild(el);
    });
  }
  STATS_TABS.forEach((t, i) => nav.children[i].classList.toggle("active", t.id === statsUi.tab));

  const blocks = statsBlocks();
  const scroll = $("statsScroll");
  const rows = blocks.filter(b => !b.kind);
  statsUi.idx = Math.max(0, Math.min(statsUi.idx, rows.length - 1));
  const shape = [statsUi.tab, statsUi.period, statsUi.achSort, statsData.sessions ? statsData.sessions.length : -1, statsData.ach ? statsData.ach.games.length : -1]
    .join("|") + "|" + blocks.map(b => b.kind || (b.cls ? "r:" + b.cls : "r")).join("|");
  if (scroll.__shape !== shape) {
    const keepTop = scroll.scrollTop;
    scroll.innerHTML = "";
    let ri = -1;
    blocks.forEach(b => {
      if (b.kind === "tiles") {
        const el = document.createElement("div");
        el.className = "stats-tiles";
        el.innerHTML = b.tiles.map(([label, value, sub]) => statTile(label, value, sub)).join("");
        scroll.appendChild(el);
      } else if (b.kind === "chart") {
        const el = document.createElement("div");
        el.className = "stats-chart";
        el.innerHTML = b.html;
        scroll.appendChild(el);
      } else if (b.kind === "section") {
        scroll.appendChild(settingsSectionEl(b.text));
      } else if (b.kind === "note") {
        const el = document.createElement("div");
        el.className = "stats-note";
        if (b.html) el.innerHTML = b.html; else el.textContent = b.text;
        scroll.appendChild(el);
      } else {
        ri++;
        scroll.appendChild(statsRowEl(ri));
      }
    });
    scroll.scrollTop = keepTop;
    scroll.__shape = shape;
  }
  let ri = -1;
  blocks.forEach((b, i) => {
    if (b.kind) return;
    ri++;
    const el = scroll.children[i];
    el.dataset.focusKey = "statsrow:" + statsUi.tab + ":" + ri;
    if (b.adjust) el.dataset.navLock = "horizontal"; else delete el.dataset.navLock;
    if (el.__cls !== b.cls) { if (el.__cls) el.classList.remove(el.__cls); if (b.cls) el.classList.add(b.cls); el.__cls = b.cls; }
    const html = b.html || `${b.iconHtml || ""}<div class="set-left"><div class="set-name">${esc(b.name)}</div>${b.hint ? `<div class="set-hint">${esc(b.hint)}</div>` : ""}</div><div class="set-value">${b.valueHtml || ""}</div>`;
    if (el.__html !== html) { el.innerHTML = html; el.__html = html; }
  });

  const scope = $("screen-stats");
  if (statsUi.pane === "rows" && rows.length) setScopeKey(scope, "statsrow:" + statsUi.tab + ":" + statsUi.idx);
  else { statsUi.pane = "nav"; setScopeKey(scope, "statstab:" + statsUi.tab); }

  // The legend names what the row under the highlight does: a row that only opens something
  // has no ◂ ▸, and the day picker says what its own do.
  const cur = statsUi.pane === "rows" ? rows[statsUi.idx] : null;
  $("statsFoot").innerHTML = !cur
    ? foot(["A", "Open"], ["B", "Back"], ["DpadV", "Category"])
    : cur.foot ? foot(...cur.foot)
    : foot(...(cur.action || cur.adjust ? [["A", "Select"]] : []), ...(cur.adjust ? [["DpadH", "Adjust"]] : []), ["B", "Categories"]);

  paintNav();
  const el = focusEl(scope);
  if (el && focusVisible()) revealFocus(el);
}

function statsRowEl(idx) {
  const el = document.createElement("div");
  el.className = "set-row stats-row";
  el.dataset.focusable = "";
  el.dataset.rowIndex = idx;
  el.addEventListener("mouseenter", () => {
    if (!hoverEnabled()) return;
    if (statsUi.idx === idx && statsUi.pane === "rows") return;
    statsUi.idx = idx; statsUi.pane = "rows"; renderStats();
  });
  el.addEventListener("click", (e) => {
    statsUi.idx = idx; statsUi.pane = "rows";
    const row = statsBlocks().filter(b => !b.kind)[idx];
    if (!row) return;
    const arrow = e.target instanceof Element ? e.target.closest(".arrow[data-dir]") : null;
    if (arrow && row.adjust) { row.adjust(parseInt(arrow.dataset.dir, 10) || 1); return; }
    // A click on a bar of the day picker picks that day; anywhere else on it opens the day.
    const hit = e.target instanceof Element ? e.target.closest("[data-i]") : null;
    if (hit && row.cls === "stats-daypick") {
      const key = dayBuckets([], DAY_PICKER_DAYS).map(d => d.key)[parseInt(hit.dataset.i, 10)];
      if (key && key !== statsUi.day) { statsUi.day = key; renderStats(); return; }
    }
    if (row.action) row.action(); else if (row.adjust) row.adjust(1);
  });
  return el;
}

function setStatsTab(id) {
  const changed = statsUi.tab !== id;
  if (changed) { statsUi.tab = id; statsUi.idx = 0; }
  renderStats();
  if (changed) pulse($("statsScroll"));
}

function syncStatsPane() {
  const el = focusEl($("screen-stats"));
  if (!el) return;
  if (el.dataset.statsTab) {
    statsUi.pane = "nav";
    if (el.dataset.statsTab !== statsUi.tab) setStatsTab(el.dataset.statsTab); else renderStats();
  } else if (el.dataset.rowIndex !== undefined) {
    statsUi.pane = "rows";
    statsUi.idx = parseInt(el.dataset.rowIndex, 10);
    renderStats();
  }
}

function statsInput(btn) {
  const scope = $("screen-stats");
  const el = focusEl(scope);
  const rows = statsBlocks().filter(b => !b.kind);
  const row = el && el.dataset.rowIndex !== undefined ? rows[parseInt(el.dataset.rowIndex, 10)] : null;
  const onTab = !!(el && el.dataset.statsTab);
  // As in Settings: inside a category the highlight stays among its rows and comes round at the
  // ends; B is the way back to the categories.
  const inRows = (x) => x.dataset.rowIndex !== undefined;
  const inTabs = (x) => x.dataset.statsTab !== undefined;
  switch (btn) {
    case "Up": case "Down":
      if (row ? paneMove(btn, inRows) : onTab ? paneMove(btn, inTabs) : navMove(btn)) syncStatsPane();
      break;
    case "Left": case "Right":
      if (row && el.dataset.navLock === "horizontal") { row.adjust(btn === "Right" ? 1 : -1); break; }
      if (row ? paneMove(btn, inRows) : navMove(btn)) syncStatsPane();
      break;
    case "A":
      if (!focusVisible()) break;
      if (onTab) { setStatsTab(el.dataset.statsTab); if (rows.length) { statsUi.pane = "rows"; statsUi.idx = 0; renderStats(); } break; }
      if (row) { if (row.action) row.action(); else if (row.adjust) row.adjust(1); }
      break;
    case "B":
      if (onTab) leaveStats();
      else { statsUi.pane = "nav"; renderStats(); }
      break;
    case "LB": leaveStats(); break;
  }
}

/* ============================== a day's timeline ============================== */

function openDay(key) {
  dayState = { key, idx: 0 };
  showOverlay("overlay-day");
  renderDay();
}

/* The overview's picker follows the day the sheet was left on. */
function closeDay() {
  if (!dayState) return;
  const key = dayState.key;
  dayState = null;
  hideOverlay("overlay-day");
  if (view === "stats") { statsUi.day = key; renderStats(); }
}

/* The timeline in order: each session followed by what it unlocked, and the loose unlocks at
   their own time between them. */
function dayEntries(d) {
  const items = [];
  d.sessions.forEach(s => items.push({ kind: "session", at: new Date(s.start).getTime(), s, unlocks: d.bySession.get(s.id) }));
  d.loose.forEach(u => items.push({ kind: "unlock", at: new Date(u.at).getTime(), u, loose: true }));
  items.sort((a, b) => a.at - b.at);
  const out = [];
  items.forEach(it => {
    out.push(it);
    if (it.kind === "session") it.unlocks.forEach(u => out.push({ kind: "unlock", at: new Date(u.at).getTime(), u, loose: false }));
  });
  return out;
}

/* The day as a 24-hour strip: each session a span, and over it the unlocks at the moment they
   happened, as their icons. Unlocks within a few minutes of each other share one stack (a boss
   often gives three at once), with a "+n" when there are more than fit. */
function dayStrip(d) {
  const W = 1000, pinBase = 50, barY = 58, barH = 16, H = 96;
  const start = dayStartOf(d.key).getTime(), len = 86400000;
  const sx = (t) => Math.max(0, Math.min(W, (new Date(t).getTime() - start) / len * W));
  const grid = [0, 3, 6, 9, 12, 15, 18, 21, 24].map(h => {
    const x = (h / 24) * W;
    return `<line class="chart-grid" x1="${x.toFixed(1)}" x2="${x.toFixed(1)}" y1="${barY - 6}" y2="${barY + barH + 4}"/>` +
      (h % 6 === 0 ? `<text class="chart-text" x="${x.toFixed(1)}" y="${H - 3}" text-anchor="${h === 0 ? "start" : h === 24 ? "end" : "middle"}">${String(h % 24).padStart(2, "0")}:00</text>` : "");
  }).join("");
  const spans = d.sessions.map(s => {
    const a = sx(s.start), b = sx(s.end);
    return `<rect class="chart-bar" x="${a.toFixed(1)}" y="${barY}" width="${Math.max(4, b - a).toFixed(1)}" height="${barH}" rx="4"><title>${esc(`${gameTitle(s.gameId)} · ${fmtClock(s.start)} – ${fmtClock(s.end)}`)}</title></rect>`;
  }).join("");
  // Clusters: a stack takes the unlocks that land within a tile's width of its first one.
  const dayEnd = start + len;
  const own = d.listed.filter(u => { const t = new Date(u.at).getTime(); return t >= start && t < dayEnd; });
  const clusters = [];
  own.forEach(u => {
    const x = sx(u.at);
    const last = clusters[clusters.length - 1];
    if (last && x - last.x0 < PIN + 8) last.items.push(u); else clusters.push({ x0: x, items: [u] });
  });
  let pins = "", ticks = "";
  clusters.forEach(c => {
    const x = c.items.reduce((s, u) => s + sx(u.at), 0) / c.items.length;
    ticks += `<line class="chart-mark-line" x1="${x.toFixed(1)}" x2="${x.toFixed(1)}" y1="${pinBase}" y2="${barY + barH}"/>`;
    const tip = c.items.map(u => `${fmtClock(u.at)} ${u.item.name}`).join("\n");
    pins += `<div class="chart-pin" title="${esc(tip)}" style="left:${(x / W * 100).toFixed(2)}%;top:${((pinBase - 3) / H * 100).toFixed(2)}%">${pinTiles({ unlocks: c.items, marks: c.items.length }, 3).join("")}</div>`;
  });
  const head = `<text class="chart-text head" x="0" y="12">THE DAY</text>` +
    (d.listed.length ? `<rect class="chart-mark" x="${W - 131}" y="3" width="10" height="10" rx="2.5"/><text class="chart-text head mark" x="${W - 116}" y="12">${d.listed.length} UNLOCKED</text>` : "");
  const svg = `<svg class="chart" viewBox="0 0 ${W} ${H}" preserveAspectRatio="none" style="height:${H}px">${head}${grid}${ticks}${spans}</svg>`;
  return pins ? `<div class="chart-box">${svg}<div class="chart-pins">${pins}</div></div>` : svg;
}

function renderDay() {
  if (!dayState) return;
  const d = dayDetail(dayState.key);
  const date = dayStartOf(dayState.key);
  $("dayTitle").textContent = fmtDayName(dayState.key, true);
  $("dayWhen").textContent = `${date.toLocaleString("en", { weekday: "long" })} ${date.getDate()} ${date.toLocaleString("en", { month: "long" })} ${date.getFullYear()}`.toUpperCase();

  const rarest = d.listed.filter(u => typeof u.item.percent === "number").sort((a, b) => a.item.percent - b.item.percent)[0];
  $("daySummary").innerHTML = [
    statTile("PLAYED", d.seconds ? fmtDuration(d.seconds) : "—"),
    statTile("SESSIONS", String(d.sessions.length)),
    statTile("GAMES", String(d.games.length), d.games.length ? gamesLine(d.games) : null),
    statTile("UNLOCKED", String(d.listed.length), rarest ? `rarest ${fmtPct(rarest.item.percent)} of players` : null),
  ].join("");
  $("dayChart").innerHTML = dayStrip(d);

  const entries = dayEntries(d);
  const list = $("dayList");
  const keepTop = list.scrollTop;
  list.innerHTML = "";
  dayState.idx = Math.max(0, Math.min(dayState.idx, entries.length - 1));
  if (!entries.length) {
    const empty = document.createElement("div");
    empty.className = "ach-empty";
    empty.textContent = "Nothing was played from Loungepad or unlocked on this day";
    list.appendChild(empty);
  }
  entries.forEach((e, i) => {
    const el = document.createElement("div");
    el.dataset.focusable = "";
    el.dataset.rowIndex = i;
    if (e.kind === "session") {
      const s = e.s;
      el.className = "day-row session";
      el.dataset.focusKey = "day:s:" + s.id;
      el.innerHTML =
        `<div class="day-time mono">${esc(fmtClock(s.start))}<i>${esc(fmtClock(s.end))}</i></div>` +
        `<div class="day-rail"><i></i></div>` +
        `<div class="day-main"><div class="day-name">${esc(gameTitle(s.gameId))}</div>` +
        `<div class="day-sub">${esc(fmtDuration(s.seconds))}${e.unlocks.length ? ` · ${e.unlocks.length} unlocked` : ""}${s.origin === "manual" ? " · logged by hand" : s.origin === "playnite" ? " · from Playnite" : ""}</div></div>` +
        // The frame rate only: the rest of the averages are one A away, and a timeline row with
        // six chips on it stops reading as a timeline.
        (s.avgFps != null ? `<div class="act-chips"><span class="act-chip fps">${s.avgFps} FPS</span></div>` : "") +
        `<span class="arrow">▸</span>`;
    } else {
      const u = e.u;
      const band = rarityBand(u.item.percent);
      const src = achIconSrc(u.item.iconFile, u.item.icon);
      el.className = "day-row unlock" + (e.loose ? " loose" : "");
      el.dataset.focusKey = "day:u:" + u.gameId + ":" + u.item.id;
      const sub = e.loose ? `${gameTitle(u.gameId)} · not during a session Loungepad recorded` : (u.item.description || gameTitle(u.gameId));
      el.innerHTML =
        `<div class="day-time mono">${esc(fmtClock(u.at))}</div>` +
        `<div class="day-rail"><i></i></div>` +
        `<div class="ach-icon small"${achIconStyle(src)}>${src ? "" : iconSvg("trophy")}</div>` +
        `<div class="day-main"><div class="day-name">${esc(u.item.name)}</div><div class="day-sub">${esc(sub)}</div></div>` +
        (band ? `<span class="ach-rarity mono" data-band="${band.id}">${esc(fmtPct(u.item.percent))} · ${esc(band.label.toUpperCase())}</span>` : "");
    }
    el.addEventListener("mouseenter", () => { if (hoverEnabled() && dayState && dayState.idx !== i) { dayState.idx = i; paintDayFocus(); } });
    el.addEventListener("click", () => { if (!dayState) return; dayState.idx = i; dayActivate(entries[i]); });
    list.appendChild(el);
  });
  list.scrollTop = keepTop;
  watchOverflow(list);
  renderDayFoot(entries);
  paintDayFocus();
}

function renderDayFoot(entries) {
  const cur = entries[dayState.idx];
  const days = activeDays();
  $("dayFoot").innerHTML = foot(
    ...(cur ? [["A", cur.kind === "session" ? "Readings" : "Achievements"]] : []),
    ...(days.length > 1 || (days.length === 1 && days[0] !== dayState.key) ? [["DpadH", "Day"]] : []),
    ["B", "Back"]);
}

function paintDayFocus() {
  if (!dayState) return;
  const scope = $("overlay-day");
  const e = dayEntries(dayDetail(dayState.key))[dayState.idx];
  setScopeKey(scope, !e ? null : e.kind === "session" ? "day:s:" + e.s.id : "day:u:" + e.u.gameId + ":" + e.u.item.id);
  const cur = focusEl(scope);
  const show = focusVisible();
  Nav.focusables(scope).forEach(el => el.classList.toggle("focused", show && el === cur));
  if (cur && show) revealFocus(cur);
}

function dayActivate(e) {
  if (!e) return;
  if (e.kind === "session" && e.s.origin === "manual") openSessionEditor(e.s.gameId, e.s);
  else if (e.kind === "session") openSession(e.s, "day");
  else if (gameById(e.u.gameId)) openAchievements(e.u.gameId, "day", e.u.item.id);
}

/* ◂ ▸: the day before or after that has anything in it. */
function stepDay(dir) {
  const days = activeDays();
  const next = dir > 0 ? days.find(k => k > dayState.key) : [...days].reverse().find(k => k < dayState.key);
  if (!next) return;
  dayState.key = next;
  dayState.idx = 0;
  $("dayList").scrollTop = 0;
  renderDay();
  pulse($("dayList"));
}

function dayInput(btn) {
  switch (btn) {
    case "Up": case "Down":
      if (listMove(btn)) {
        const el = focusEl();
        if (el && el.dataset.rowIndex !== undefined) dayState.idx = parseInt(el.dataset.rowIndex, 10);
        renderDayFoot(dayEntries(dayDetail(dayState.key)));
      }
      break;
    case "Left": case "Right": stepDay(btn === "Right" ? 1 : -1); break;
    case "A": if (focusVisible()) dayActivate(dayEntries(dayDetail(dayState.key))[dayState.idx]); break;
    case "B": closeDay(); break;
  }
}

/* ============================== Settings → Stats ============================== */

function activitySourcesHint() {
  const src = statsData.sources;
  if (!src) return "Checking what is on this PC…";
  const bits = [src.gpu ? "Built-in CPU, GPU and memory from Windows" : "Built-in CPU and memory from Windows (this GPU publishes no usage counter)"];
  bits.push(src.fps ? `frame rate from ${src.fps}` : "no frame rate: run RivaTuner Statistics Server, MSI Afterburner or HWiNFO for one");
  bits.push(src.sensors ? `temperatures and power from ${src.sensors}` : "no temperatures: run HWiNFO with Shared Memory Support on, or MSI Afterburner");
  return bits.join(" · ");
}

function activitySettingsRows(s, set) {
  const rows = [];
  // The way in from here comes first: the category holds the switches, the screen the numbers.
  // Its section draws no heading -- "STATS" over "Open Stats" in a category called Stats would say
  // the same word three times.
  rows.push({ section: "STATS", cat: "stats", bare: true });
  rows.push({
    name: "Open Stats", hint: "Playtime day by day, each session's readings and what it unlocked. LB on the library opens it too",
    type: "action", label: "Open", action: () => openStats("settings"),
  });
  rows.push({ section: "PLAY SESSIONS", cat: "stats" });
  rows.push(toggleRow("Track play sessions", "Every sitting is recorded: when, how long, and how the PC did. The totals on each game are kept either way",
    () => s.activityTracking !== false, v => set(() => s.activityTracking = v)));
  if (s.activityTracking !== false) {
    rows.push(toggleRow("Record performance", "A reading every few seconds while a game runs: CPU, GPU and memory from Windows, and the frame rate, temperatures and power from RivaTuner Statistics Server, MSI Afterburner or HWiNFO when one of them is running",
      () => s.activityHardware !== false, v => set(() => s.activityHardware = v)));
    if (s.activityHardware !== false) {
      rows.push(sliderRow("Reading interval", () => s.activitySampleSeconds ?? 5, 2, 30, 1, v => set(() => s.activitySampleSeconds = v), v => `${Math.round(v)} s`,
        "Five seconds is a point every few pixels of a chart and a few kilobytes an hour"));
      rows.push({
        name: "Performance sources", hint: activitySourcesHint(), type: "action", label: "Check",
        action: () => { statsData.sources = null; renderSettings(); send({ cmd: "activityAll" }); },
      });
    }
  }
  const n = (statsData.sessions || []).length;
  rows.push({
    name: "Clear play sessions", hint: "Every session on record, for every game. Playtime totals stay as they are",
    type: "action", label: "Clear", danger: true,
    action: () => askConfirm({
      title: "Clear every play session?",
      body: `${n ? n + " sessions and their readings" : "Every session and its readings"} will be removed. The playtime shown on each game is not changed.`,
      yesLabel: "Clear", icon: "trash", danger: true,
      onYes: () => { send({ cmd: "activityClear" }); statsData.sessions = []; },
    }),
  });

  rows.push({ section: "ACHIEVEMENTS", cat: "stats" });
  rows.push(toggleRow("Fetch achievements", "Each game's list from its store: Steam by app id, Xbox, Epic and GOG through their sign-ins, ROMs through RetroAchievements. Fetched in the background after a scan, again after each session, and when a game's page asks",
    () => s.achievementsEnabled !== false, v => set(() => s.achievementsEnabled = v)));
  if (s.achievementsEnabled !== false) {
    rows.push(toggleRow("Unlock cards", "Once a game closes, a card for each achievement the session unlocked",
      () => s.achievementNotifications !== false, v => set(() => s.achievementNotifications = v)));
    rows.push(toggleRow("Show on tiles", "The unlocked share on library tiles and in the hero text, for games that have any",
      () => s.achievementsOnTiles !== false, v => set(() => s.achievementsOnTiles = v)));
    const withAch = Object.keys(S.achievements || {}).length;
    rows.push({
      name: "Refresh all achievements", hint: `${withAch} game${withAch === 1 ? "" : "s"} with achievements on record. Fetches every game again, paced, in the background`,
      type: "action", label: "Refresh",
      action: () => send({ cmd: "achievementsRefresh" }),
    });
    const a = S.steamAccount;
    const st = S.stores || {};
    const signedIn = (k) => st[k] && st[k].signedIn;
    const toLibrary = () => { setSettingsTab("library"); enterSettingsPane("rows"); };
    rows.push({
      name: "Steam", hint: a && a.steamId
        ? `As ${a.personaName || a.steamId}, read from Steam with your Steam sign-in or your own Web API key, both under Library. Your account's data goes to Steam and nowhere else`
        : "No Steam login was found on this PC. Sign in to Steam once",
      type: "action", label: "Library", action: toLibrary,
    });
    rows.push({ name: "Xbox", hint: signedIn("xbox") ? `Signed in as ${st.xbox.user || "Xbox"}. Games the account has played on any device` : "Sign in under Library to fetch Xbox achievements", type: "action", label: "Library", action: toLibrary });
    rows.push({ name: "Epic Games", hint: signedIn("epic") ? `Signed in as ${st.epic.user || "Epic"}` : "Sign in under Library to fetch Epic achievements", type: "action", label: "Library", action: toLibrary });
    rows.push({ name: "GOG", hint: signedIn("gog") ? `Signed in as ${st.gog.user || "GOG"}` : "Sign in under Library to fetch GOG achievements", type: "action", label: "Library", action: toLibrary });
    rows.push({
      name: "RetroAchievements username", hint: "For emulated games: a ROM is matched by its hash to the site's game, or by title on systems that cannot be hashed. Free at retroachievements.org",
      type: "action", label: s.retroAchievementsUser ? s.retroAchievementsUser : "Not set",
      action: () => openInput("RETROACHIEVEMENTS USERNAME", s.retroAchievementsUser || "", v => set(() => s.retroAchievementsUser = v.trim())),
    });
    rows.push(secretRow("RetroAchievements web API key", s, "From retroachievements.org → Settings → Keys. Kept sealed for your Windows account, like the other keys",
      () => s.retroAchievementsKey, v => set(() => s.retroAchievementsKey = v)));
  }
  return rows;
}

/* The sources line in Settings comes from the same message the screen uses; ask once the
   category is opened, and again whenever Check is pressed. */
setInterval(() => {
  if (view === "settings" && settingsTab === "stats" && statsData.sources === null && !activitySourcesAsked) {
    activitySourcesAsked = true;
    send({ cmd: "activityAll" });
  }
}, 600);
let activitySourcesAsked = false;
