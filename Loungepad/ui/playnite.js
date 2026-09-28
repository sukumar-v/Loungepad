"use strict";

/*
 * Settings → General → Import from Playnite: a form sheet (openForm in app.js) fed by the host's
 * `playnite` message (see PlayniteImport and UiBridge.PushPlaynite).
 *
 * The host reads Playnite from a copy and plans the import against Loungepad's data as it stands;
 * the sheet shows each part with what it would bring and imports only the parts left on. The rule
 * the sheet repeats is the rule the host applies: it only fills what Loungepad has nothing for,
 * and never overwrites anything here. After an import every count is planned afresh, so the sheet
 * reads what is left (normally nothing), and "Undo the import" takes back exactly what it did.
 *
 * Loaded last; nothing in app.js touches these variables while it boots.
 */

/* The parts, in the order the sheet lists them. `hint` says what a part would bring, from the
   host's counts; `none` says why a part has nothing. */
const PLAYNITE_PARTS = [
  {
    id: "playtime", name: "Playtime and play counts",
    hint: (p) => `${p.count} game${p.count === 1 ? "" : "s"} with no playtime here yet get Playnite's${p.minutes > 0 ? ` (${fmtPlaytime(p.minutes)} in all)` : ""}${p.titles && p.titles.length ? ": " + listTitles(p.titles, p.count) : ""}`,
    none: "Every game Playnite played already has its own playtime here, or Playnite has none",
  },
  {
    id: "flags", name: "Favourites and hidden games",
    hint: (p) => [p.favorites ? `${p.favorites} favourite${p.favorites === 1 ? "" : "s"}` : null, p.hidden ? `${p.hidden} hidden` : null].filter(Boolean).join(" and ") + ", on games not already marked",
    none: "Nothing marked in Playnite that is not marked here already",
  },
  {
    id: "collections", name: "Categories as collections",
    hint: (p) => `${p.count} new collection${p.count === 1 ? "" : "s"}: ${listTitles(p.names, p.count)}. Your own collections are left alone; a name you already use gets “(Playnite)”`,
    none: "Playnite has no categories with games Loungepad has",
  },
  {
    id: "games", name: "Games added by hand",
    hint: (p) => `${p.count} program${p.count === 1 ? "" : "s"} you added to Playnite yourself: ${listTitles(p.titles, p.count)}`,
    none: "Nothing added to Playnite by hand that is not here already",
  },
  {
    id: "sessions", name: "Play sessions",
    hint: (p) => `${p.count} session${p.count === 1 ? "" : "s"} from GameActivity across ${p.games} game${p.games === 1 ? "" : "s"}, on the Stats timeline. Any that overlap a session Loungepad recorded are left out`,
    none: (p) => (p.available ? "Every GameActivity session is here already, or overlaps one Loungepad recorded" : "GameActivity is not installed in Playnite"),
  },
  {
    id: "achievements", name: "Achievements",
    hint: (p) => `SuccessStory's lists for ${p.count} game${p.count === 1 ? "" : "s"} Loungepad cannot fetch a list for itself (${p.unlocked} unlocked). Lists Loungepad fetches are never changed`,
    none: (p) => (p.available ? "Loungepad can fetch every list SuccessStory has, or already has them" : "SuccessStory is not installed in Playnite"),
  },
  {
    id: "settings", name: "Settings",
    hint: (p) => `${p.labels.join(", ")} -- only where yours is empty`,
    none: "Nothing that maps to an empty setting here",
  },
];

let playniteView = null;                 // the last `playnite` message
const playniteOff = new Set();           // parts switched off on the sheet; everything else is on

function listTitles(titles, count) {
  const shown = (titles || []).slice(0, 3);
  return shown.join(", ") + (count > shown.length ? ` and ${count - shown.length} more` : "");
}

function openPlayniteImport() {
  playniteView = { state: "reading" };
  openForm({
    kind: "playnite", kicker: "IMPORT FROM PLAYNITE",
    title: () => (playniteView && playniteView.version ? `Playnite ${playniteView.version}` : "Playnite"),
    sub: () => (playniteView && playniteView.dir ? playniteView.dir.toUpperCase() : ""),
    rows: playniteRows,
    onClose: () => { playniteView = null; },
  });
  send({ cmd: "playniteScan" });
}

function onPlayniteMessage(m) {
  playniteView = m;
  if (formState && formState.kind === "playnite") renderForm();
}

function playniteParts() { return (playniteView && playniteView.parts) || {}; }
function playniteChosen() { return PLAYNITE_PARTS.filter(p => !playniteOff.has(p.id) && (playniteParts()[p.id] || {}).count > 0); }

function playniteRows() {
  const v = playniteView || { state: "reading" };
  const rows = [];
  const pick = { name: "Choose Playnite's folder", hint: "For a portable Playnite, or a library kept somewhere else", type: "action", label: "Choose", action: () => send({ cmd: "playnitePick" }) };
  if (v.state === "reading") {
    rows.push({ note: "Reading Playnite's library. Its files are copied first and only the copy is opened; Playnite itself is never changed." });
    return rows;
  }
  if (v.state === "missing" || v.state === "error") {
    rows.push({ note: v.state === "missing" ? "Playnite's library was not found on this PC." : `Playnite's library could not be read: ${v.error}. If Playnite is open, close it and try again.` });
    rows.push(pick);
    if (v.record) rows.push(...playniteUndoRows(v));
    return rows;
  }

  const parts = playniteParts();
  const lines = [`${v.matched} of the ${v.games} games in Playnite are in Loungepad too.`,
    "Only what Loungepad has nothing for is brought in. Nothing already here is overwritten."];
  if (v.state === "done" && v.outcome) lines.unshift(v.outcome.total > 0 ? `Imported ${playniteOutcomeText(v.outcome)}.` : "Nothing was left to bring.");
  if (v.state === "undone" && v.outcome) lines.unshift(`The import was taken back: ${playniteOutcomeText(v.outcome)}.`);
  rows.push({ note: lines.join(" ") });

  rows.push({ section: "WHAT TO BRING" });
  PLAYNITE_PARTS.forEach(part => {
    const p = parts[part.id] || { count: 0 };
    if (!(p.count > 0)) {
      rows.push({ name: part.name, hint: typeof part.none === "function" ? part.none(p) : part.none, type: "html", valueHtml: `<span class="set-toggle-off">NOTHING</span>`, muted: true });
      return;
    }
    rows.push(toggleRow(part.name, part.hint(p), () => !playniteOff.has(part.id), on => { if (on) playniteOff.delete(part.id); else playniteOff.add(part.id); }));
  });
  const chosen = playniteChosen();
  rows.push({
    name: "Import", type: "action", label: "Import",
    hint: chosen.length ? `${chosen.map(p => p.name.toLowerCase()).join(", ")}` : "Nothing to import",
    muted: !chosen.length,
    action: () => {
      playniteView = { ...playniteView, state: "reading" };
      send({ cmd: "playniteImport", parts: playniteChosen().map(p => p.id) });
    },
  });
  if (v.record) rows.push(...playniteUndoRows(v));
  rows.push({ section: "ELSEWHERE" });
  rows.push(pick);
  return rows;
}

function playniteUndoRows(v) {
  const r = v.record;
  const when = r.at ? fmtAgo(r.at) : "earlier";
  return [
    { section: "UNDO" },
    {
      name: "Undo the import", type: "action", label: "Undo", danger: true,
      hint: `Takes back what importing brought in (${when}). Anything you have changed since is kept, and time played since stays on the games`,
      action: () => askConfirm({
        title: "Undo the Playnite import?",
        body: "Everything the import brought in is taken back out: imported sessions, games, collections, lists and marks, and the playtime it filled in. Your own data is not touched.",
        yesLabel: "Undo import", icon: "refresh", danger: true,
        onYes: () => { playniteView = { ...playniteView, state: "reading" }; renderForm(); send({ cmd: "playniteUndo" }); },
      }),
    },
  ];
}

function playniteOutcomeText(o) {
  const bits = [];
  const add = (n, one, many) => { if (n > 0) bits.push(`${n} ${n === 1 ? one : many}`); };
  add(o.playtime, "game's playtime", "games' playtime");
  add(o.flags, "mark", "marks");
  add(o.collections, "collection", "collections");
  add(o.games, "game", "games");
  add(o.sessions, "session", "sessions");
  add(o.achievements, "achievement list", "achievement lists");
  add(o.settings, "setting", "settings");
  return bits.length ? bits.join(", ") : "nothing";
}
