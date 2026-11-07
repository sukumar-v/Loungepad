"use strict";

/*
 * Actions: a program's keyboard shortcuts, on the pad.
 *
 * Three pieces, all fed by S.actions (the host's apps, packs merged with the user's changes, each
 * app's actions on tabs of up to eight):
 *
 *   1. The action wheel. The Power Wheel's Actions spoke opens a second wheel with the shortcuts of
 *      whatever was in front when the menu came up -- the host names the exe, this file matches
 *      it to an app -- one tab to a wheel, the app's own tabs first and Everywhere's after, LB and
 *      RB turning between them. A spoke shows the action, its keys and the pad button it is bound
 *      to; A presses it, and a bound button presses its action directly. Firing hands the
 *      foreground back to that window first, so the keys land where they were meant to.
 *
 *   2. Settings → Actions. A grid of the apps on this PC we have actions for, plus Everywhere and
 *      the user's own; A on a tile opens that app's wheel in the wheel editor (wheeledit.js), the
 *      same one Settings → Shortcuts is: A edits an action (the form sheet, over the wheel), the +
 *      spoke adds one, X moves one round the ring or to another tab, Y is the options card: the
 *      tab's, the ways to bring actions in, and the app's own (save, restore, rename, remove).
 *      Bringing actions in ends on the import's own screen (acImportHtml). B goes back to the grid.
 *
 *   3. The two pickers an action needs: "press a button" (the host records the next press or
 *      chord and swallows it) and a keyboard laid out to point at, for the shortcut.
 *
 * Bindings fire on the host, in the pad loop, only while the app is in front and the launcher is
 * not; this file never sends a key. Loaded after radial.js and wheeledit.js and shares app.js's
 * globals.
 */

const EVERYWHERE_ID = "everywhere";
const MOD_ORDER = ["Ctrl", "Shift", "Alt", "Win"];

/* ---- the data ---- */

function actionApps() { return (S.actions && S.actions.apps) || []; }
function actionsMaxItems() { return (S.actions && S.actions.maxItems) || 8; }
function actionsMaxTabs() { return (S.actions && S.actions.maxTabs) || 12; }
/* The next list with the icons of the last one where it has none of its own: a state push
   leaves them out, and an app's icon never changes between two messages anyway. */
function withActionIcons(next, prev) {
  if (!next) return prev || null;
  const had = new Map(((prev && prev.apps) || []).map(a => [a.id, a.icon]));
  (next.apps || []).forEach(a => { if (!a.icon && had.get(a.id)) a.icon = had.get(a.id); });
  return next;
}
function actionApp(id) { return actionApps().find(a => a.id === id) || null; }
function everywhereApp() { return actionApp(EVERYWHERE_ID); }
function appForProcess(exe) {
  exe = String(exe || "").toLowerCase();
  if (!exe) return null;
  return actionApps().find(a => a.id !== EVERYWHERE_ID && (a.exes || []).some(e => String(e).toLowerCase() === exe)) || null;
}
/* An app's picture at a small size, for the wheel's centre and its pager: the globe for Everywhere,
   the program's own icon, or its first letter in a rounded square when there is no icon. */
function actionAppIconHtml(app, cls) {
  if (!app || app.id === EVERYWHERE_ID) return iconSvg("globe");
  if (app.icon) return `<span class="${cls}" style="background-image:url('${scCssUrl(app.icon)}')"></span>`;
  return `<span class="${cls} app-mono-sm">${esc((app.name || "?").trim().charAt(0).toUpperCase())}</span>`;
}
/* What the Settings grid shows: Everywhere first, then the rest by name. A pack for a program
   that is not on this PC stays out of the way. */
function shownApps() {
  const apps = actionApps().filter(a => a.installed);
  const ev = apps.filter(a => a.id === EVERYWHERE_ID);
  const rest = apps.filter(a => a.id !== EVERYWHERE_ID).sort((a, b) => a.name.localeCompare(b.name));
  return ev.concat(rest);
}
/* Every action on an app's wheel, tab by tab: what its buttons and the checks read. */
function appActions(app) { return ((app && app.tabs) || []).flatMap(t => t.items || []); }
function actionById(app, id) { return appActions(app).find(a => a.id === id) || null; }
function actionNewId() { return "c" + Math.random().toString(16).slice(2, 10).padEnd(8, "0"); }

/* ---- keys, as text and as caps ---- */

const KEY_LABELS = {
  PageUp: "PgUp", PageDown: "PgDn", PrintScreen: "PrtSc", ScrollLock: "ScrLk", CapsLock: "Caps",
  NumLock: "NumLk", Escape: "Esc", Backspace: "Bksp", Delete: "Del", Insert: "Ins",
  Up: "↑", Down: "↓", Left: "←", Right: "→", Apps: "Menu",
  VolumeUp: "Vol +", VolumeDown: "Vol −", VolumeMute: "Mute",
  MediaPlayPause: "Play/Pause", MediaNext: "Next", MediaPrev: "Prev", MediaStop: "Stop",
  BrowserBack: "Back", BrowserForward: "Fwd", BrowserRefresh: "Refresh", BrowserHome: "Home ⌂",
  BrowserSearch: "Search", BrowserFavorites: "Favs",
  NumAdd: "Num +", NumSubtract: "Num −", NumMultiply: "Num *", NumDivide: "Num /", NumDecimal: "Num .", NumEnter: "Num ⏎",
};
function keyLabel(k) {
  if (KEY_LABELS[k]) return KEY_LABELS[k];
  if (/^Num\d$/.test(k)) return "Num " + k[3];
  return k;
}

/* "Ctrl+Shift+T" → ["Ctrl", "Shift", "T"]. A "+" at the end is the key itself. */
function splitKeys(keys) {
  const s = String(keys || "").trim();
  if (!s) return [];
  const out = [];
  let i = 0;
  while (i < s.length) {
    const j = s.indexOf("+", i);
    if (j < 0 || j === s.length - 1) { out.push(s.slice(i).trim()); break; }
    const part = s.slice(i, j).trim();
    out.push(part.length ? part : "+");
    i = j + 1;
  }
  return out.filter(Boolean);
}

/* Key caps for a shortcut, joined with "+". Empty says so in words rather than showing nothing. */
function keysHtml(keys, cls) {
  const parts = splitKeys(keys);
  if (!parts.length) return `<span class="keys-none">No shortcut</span>`;
  return `<span class="keys${cls ? " " + cls : ""}">` +
    parts.map(p => keycap(keyLabel(p))).join(`<span class="combo-plus">+</span>`) + `</span>`;
}

/* ---- buttons: the same combo written two ways is one combo ---- */

const BUTTON_ORDER = ["LT", "RT", "LB", "RB", "LS", "RS", "View", "Menu", "Guide", "A", "B", "X", "Y", "Up", "Down", "Left", "Right"];
function comboParts(combo) {
  return String(combo || "").split("+").map(p => canonBtn(p.trim())).filter(p => BUTTON_ORDER.includes(p));
}
function normCombo(combo) {
  const parts = comboParts(combo);
  return BUTTON_ORDER.filter(b => parts.includes(b)).join(" + ");
}
function comboEq(a, b) { return !!a && !!b && normCombo(a) === normCombo(b); }

/*
 * Why a binding would not do what it says. The host evaluates the keyboard toggle and the two
 * combos before any binding and lets them win, so a binding on the same button is dead; the
 * Settings row says so rather than letting anyone find out by pressing. `block` is refused at
 * capture time, `warn` is shown under the row, `note` goes in the hint.
 */
function bindingIssues(app, action, combo) {
  const s = S.settings || {};
  const issues = [];
  const parts = comboParts(combo);
  if (!parts.length) return issues;
  const single = parts.length === 1 ? parts[0] : null;
  const say = (b) => `[[${b}]]`;

  if (single === "A" || single === "B")
    issues.push({ level: "block", text: `${say(single)} on its own is a click button and cannot be an action. Hold another button with it, or pick a different one.` });
  if (single === "Guide")
    issues.push({ level: "block", text: `Windows keeps ${say("Guide")} for itself; it never reaches an action.` });
  if (!comboOff(s.wheelCombo) && comboEq(combo, s.wheelCombo))
    issues.push({ level: "block", text: `That is the Power Wheel button, which always wins. Pick another button.` });
  if (!comboOff(s.launcherCombo) && comboEq(combo, s.launcherCombo))
    issues.push({ level: "block", text: `That is the Loungepad button, which always wins. Pick another button.` });
  if (s.screenshotCombo && s.screenshotCombo !== "Off" && comboEq(combo, s.screenshotCombo))
    issues.push({ level: "block", text: `That is the screenshot button, which always wins. Pick another button.` });

  const sayAll = (c) => comboParts(c).map(say).join(" + ");
  const toggle = comboOff(s.keyboardToggleButton) ? null : (s.keyboardToggleButton || "Back");
  const hold = (s.keyboardToggleMode || "Press") === "Hold";
  if (toggle && comboEq(combo, toggle) && !hold)
    issues.push({ level: "warn", text: `${sayAll(toggle)} opens the keyboard on a press, so it wins over this. Change the keyboard button under Keyboard, set it to Hold, or pick another button.` });
  else if (toggle && comboEq(combo, toggle) && hold)
    issues.push({ level: "note", text: `A tap fires this; holding ${sayAll(toggle)} still opens the keyboard.` });

  const boost = comboOff(s.boostButton) ? [] : comboParts(s.boostButton);
  if (boost.length && boost.every(b => parts.includes(b)))
    issues.push({ level: "note", text: `${sayAll(s.boostButton)} is also the speed boost while it is held.` });

  const left = comboOff(s.leftClickButton) ? null : canonBtn(s.leftClickButton || "A");
  const right = comboOff(s.rightClickButton) ? null : canonBtn(s.rightClickButton || "B");
  if (single && (single === left || single === right) && !(single === "A" || single === "B"))
    issues.push({ level: "warn", text: `${say(single)} is the ${single === left ? "left" : "right"} click button. ${app ? `In ${app.name} it` : "It"} fires this instead of clicking.` });

  if (app) {
    const twin = appActions(app).find(a => a !== action && a.id !== (action && action.id) && comboEq(a.button, combo));
    if (twin) issues.push({ level: "block", text: `${twin.name} already uses that button here. Pick another, or clear it there first.` });
    if (app.id !== EVERYWHERE_ID) {
      const over = appActions(everywhereApp()).find(a => comboEq(a.button, combo));
      if (over) issues.push({ level: "note", text: `Overrides Everywhere's “${over.name}” while ${app.name} is in front.` });
    }
    // The Power Wheel's shortcuts answer in every app, as Everywhere does (shortcuts.js): the two
    // never share a button, and an app's own action wins over a shortcut while the app is in front.
    const sc = scAllItems().find(x => comboEq(x.button, combo));
    if (sc && app.id === EVERYWHERE_ID)
      issues.push({ level: "block", text: `The shortcut “${sc.name}” already uses that button (Settings › Shortcuts). Pick another.` });
    else if (sc)
      issues.push({ level: "note", text: `Overrides the shortcut “${sc.name}” while ${app.name} is in front.` });
  }
  return issues;
}
function bindingBlock(app, action, combo) { const i = bindingIssues(app, action, combo).find(x => x.level === "block"); return i ? i.text : null; }
function bindingWarn(app, action) {
  const i = bindingIssues(app, action, action.button).find(x => x.level === "warn" || x.level === "block");
  return i ? i.text : null;
}
function bindingNotes(app, action) {
  return bindingIssues(app, action, action.button).filter(x => x.level === "note").map(x => x.text);
}

/* ============================== the wheel ============================== */

/* actionWheelOpen, captureState, keyPick and overlayTargetProcess are declared in app.js with the
   other overlay flags: app.js reads them, and a `let` in a later script is out of reach while
   app.js boots. */
let actionPages = [];          // [{ app, tab, items }]
let actionPage = 0, actionIdx = 0;

/* The pages: the app in front's tabs, then Everywhere's, one wheel each. An empty tab is left out:
   on the Power Wheel it would only be a stop between two that have something on them. With
   nothing at all there is still one page, empty, so the wheel can say so and point at Settings. */
function buildActionPages() {
  const target = appForProcess(overlayTargetProcess);
  const pages = [];
  const add = (app) => ((app && app.tabs) || []).forEach(tab => {
    if ((tab.items || []).length) pages.push({ app, tab, items: tab.items });
  });
  add(target);
  add(everywhereApp());
  if (!pages.length) pages.push({ app: target || everywhereApp() || { id: "", name: "Actions", tabs: [] }, tab: null, items: [] });
  return pages;
}

function openActionWheel() {
  actionWheelOpen = true;
  radialSub = null;
  actionPages = buildActionPages();
  actionPage = 0; actionIdx = 0;
  hideOverlay("overlay-radial");
  renderActionWheel(true);
  showOverlay("overlay-actions");
}

/* Back to the Power Wheel, or away entirely (the host closes the overlay on a fire). */
function closeActionWheel(backToRadial) {
  actionWheelOpen = false;
  hideOverlay("overlay-actions");
  if (backToRadial) { renderRadial(); showOverlay("overlay-radial"); }
}

function actionSpokeHtml(it) {
  return `<span class="spoke-name">${esc(it.name)}</span>` +
    `<span class="spoke-keys">${keysHtml(it.keys)}</span>` +
    (it.button ? spokeBtnHtml(it.button) : "");
}

/* The button a spoke is bound to (an action's here, a shortcut's in shortcuts.js): a row along the
   bottom of the spoke, inside it. The pictures get smaller as the chord grows, so a five-button
   chord still fits the box; it used to be a badge hung off the spoke's corner, and a long chord
   ran across its neighbours. */
function spokeBtnHtml(combo) {
  const n = Math.max(1, String(combo).split("+").filter(p => p.trim()).length);
  const h = [26, 26, 24, 21, 19, 17][Math.min(n, 5)];
  return `<span class="spoke-btn" style="--btn-h:${h}px">${comboHtml(combo)}</span>`;
}

/* A row of page names can be wider than the pager: the one in force is kept in view, and the edge
   with more beyond it fades rather than cutting a name in half. Both wheels' pagers. */
function centerPager(pages) {
  const on = pages && pages.querySelector(".on");
  if (!on) return;
  pages.scrollLeft = Math.max(0, on.offsetLeft - (pages.clientWidth - on.offsetWidth) / 2);
  pages.classList.toggle("more-left", pages.scrollLeft > 1);
  pages.classList.toggle("more-right", pages.scrollLeft + pages.clientWidth < pages.scrollWidth - 1);
}

function renderActionWheel(rebuild) {
  const page = actionPages[actionPage] || { app: { name: "Actions" }, tab: null, items: [] };
  const items = page.items;
  const n = items.length;
  actionIdx = n ? Math.max(0, Math.min(actionIdx, n - 1)) : 0;
  const ring = $("actionsRing");
  const R = 310, cx = 440, cy = 440;
  // Built once per page. A spoke carries the wheel's opening bloom (see .radial-item), and
  // rebuilding them on every move would replay it each step.
  if (rebuild || ring.children.length !== n) {
    ring.innerHTML = "";
    items.forEach((it, i) => {
      const ang = (-90 + i * (360 / Math.max(n, 1))) * Math.PI / 180;
      const el = document.createElement("div");
      el.style.left = (cx + R * Math.cos(ang)) + "px";
      el.style.top = (cy + R * Math.sin(ang)) + "px";
      el.style.setProperty("--i", i);
      el.style.setProperty("--dx", (-R * 0.55 * Math.cos(ang)).toFixed(1) + "px");
      el.style.setProperty("--dy", (-R * 0.55 * Math.sin(ang)).toFixed(1) + "px");
      el.innerHTML = actionSpokeHtml(it);
      el.addEventListener("mouseenter", () => {
        if (hoverEnabled() && actionIdx !== i) { actionIdx = i; renderActionWheel(); }
      });
      el.addEventListener("click", () => { actionIdx = i; fireAction(page.app, items[i]); });
      ring.appendChild(el);
    });
  }
  items.forEach((it, i) => {
    ring.children[i].className = "radial-item action-spoke"
      + (i === actionIdx && focusVisible() ? " focused" : "")
      + (it.danger ? " danger" : "")
      + (it.keys ? "" : " no-keys");
  });

  $("actionsTitle").textContent = "ACTIONS";
  const appIcon = actionAppIconHtml(page.app, "app-icon-sm");
  $("actionsApp").innerHTML = appIcon + `<span>${esc(String(page.app.name || "").toUpperCase())}</span>`;
  const sel = items[actionIdx];
  if (sel) {
    $("actionsSelName").textContent = sel.name;
    // Two lines at most: the pack's own always fit, one typed in Settings is cut with an ellipsis.
    $("actionsDesc").innerHTML = sel.desc ? `<span>${esc(sel.desc)}</span>` : "";
    $("actionsKeys").innerHTML = keysHtml(sel.keys, "big") + (sel.button ? `<span class="actions-btn">${comboHtml(sel.button)}</span>` : "");
  } else {
    $("actionsSelName").textContent = "Nothing here yet";
    $("actionsDesc").innerHTML = "";
    $("actionsKeys").innerHTML = `<span class="actions-hint">Add actions under Settings › Actions</span>`;
  }
  const target = appForProcess(overlayTargetProcess);
  const targetLine = overlayTargetProcess
    ? (overlayTargetTitle || overlayTargetProcess).toUpperCase() + (target ? "" : " · NO ACTIONS FOR THIS APP YET")
    : "NO APP IN FRONT";
  $("actionsTarget").textContent = targetLine;
  // The pager is its own row between the ring and the legend, so it never lands on a spoke: the
  // tabs by name between the shoulders, each clickable, each led by its app's icon (the globe for
  // Everywhere's), and a rule where the app's own end and Everywhere's begin.
  const pager = $("actionsPager");
  const total = actionPages.length;
  pager.hidden = total < 2;
  pager.innerHTML = total > 1
    ? `<div class="legend-item" data-press="LB">${slot("LB")}</div>` +
      `<div class="sc-pages actions-pages">${actionPages.map((p, i) =>
        (i > 0 && p.app.id !== actionPages[i - 1].app.id ? `<span class="actions-pages-div"></span>` : "") +
        `<span class="sc-page${i === actionPage ? " on" : ""}" data-action-page="${i}">${actionAppIconHtml(p.app, "page-icon")}<span>${esc(p.tab ? p.tab.name : p.app.name)}</span></span>`).join("")}</div>` +
      `<div class="legend-item" data-press="RB">${slot("RB")}</div>`
    : "";
  centerPager(pager.querySelector(".actions-pages"));
  $("actionsFoot").innerHTML = foot(["A", "Press"], ["B", "Back"]);
}

$("actionsPager").addEventListener("click", (e) => {
  const page = e.target instanceof Element ? e.target.closest("[data-action-page]") : null;
  if (!page || !actionWheelOpen) return;
  actionPage = parseInt(page.dataset.actionPage, 10) || 0;
  actionIdx = 0;
  renderActionWheel(true);
});

function actionPageStep(dir) {
  const total = actionPages.length;
  if (total < 2) return;
  actionPage = (actionPage + dir + total) % total;
  actionIdx = 0;
  renderActionWheel(true);
}

/* An action bound to a button, for the app in front first and Everywhere second: what a press of
   that button does on the wheel, whichever tab it is on. */
function boundAction(btn) {
  const target = appForProcess(overlayTargetProcess);
  for (const app of [target, everywhereApp()]) {
    const hit = app && appActions(app).find(a => a.button && comboEq(a.button, btn));
    if (hit) return { app, action: hit };
  }
  return null;
}

/* The host closes the overlay itself (it has to park the launcher and hand the foreground back
   before it presses anything), so this only takes the page's menus down. */
function fireAction(app, action) {
  if (!app || !action) return;
  send({ cmd: "actionFire", appId: app.id, actionId: action.id });
  actionWheelOpen = false; radialOpen = false; radialSub = null;
  hideOverlay("overlay-actions");
  hideOverlay("overlay-radial");
  setOverlayMode(false);
}

function actionWheelInput(btn) {
  const page = actionPages[actionPage];
  const n = page ? page.items.length : 0;
  switch (btn) {
    case "Right": case "Down": if (n) { actionIdx = (actionIdx + 1) % n; renderActionWheel(); } break;
    case "Left": case "Up": if (n) { actionIdx = (actionIdx - 1 + n) % n; renderActionWheel(); } break;
    case "LB": actionPageStep(-1); break;
    case "RB": actionPageStep(1); break;
    case "A": if (focusVisible() && page && page.items[actionIdx]) fireAction(page.app, page.items[actionIdx]); break;
    case "B": closeActionWheel(true); break;
    // A bound button presses its action from the wheel too: the badge on the spoke says it will.
    case "X": case "Y": case "View": case "Menu": {
      const hit = boundAction(btn);
      if (hit) fireAction(hit.app, hit.action);
      break;
    }
  }
}

/* The stick points at a spoke, as on the Power Wheel. Called from the host's stick message. */
function actionWheelStick(x, y) {
  const page = actionPages[actionPage];
  const n = page ? page.items.length : 0;
  if (!n) return;
  const i = stickToSpoke(x, y, n);
  if (i !== actionIdx) { actionIdx = i; renderActionWheel(); }
}

/* The list changed under an open wheel (a state push): keep the page and the highlight where they
   were if they still exist. */
function refreshActionWheel() {
  if (!actionWheelOpen) return;
  const was = actionPages[actionPage];
  actionPages = buildActionPages();
  const same = was && was.tab ? actionPages.findIndex(p => p.app.id === was.app.id && p.tab && p.tab.id === was.tab.id) : -1;
  actionPage = same >= 0 ? same : Math.min(actionPage, actionPages.length - 1);
  renderActionWheel(true);
}

/* ============================== Settings → Actions ============================== */

/* Which level of the category is showing: the grid of apps, or one app's wheel. Back to the grid
   whenever the category is entered afresh (setSettingsTab). acUi, the wheel editor's own state for
   the app on screen, is declared in app.js with scUi. */
let actionsUi = { level: "apps", appId: null };

function actionsTabReset() {
  weReset(acModel);
  actionsUi = { level: "apps", appId: null };
}
function actionsGridMode() { return settingsTab === "actions" && actionsUi.level === "apps"; }

/* The rows the generic settings renderer counts. At the grid level each "row" is a tile, drawn
   differently but driven the same way; an app's level is its wheel, one row per spoke. */
function actionsSettingsRows() {
  if (actionsUi.level === "app") {
    if (actionApp(actionsUi.appId)) return weRows(acModel);
    actionsTabReset();
  }
  const rows = [];
  shownApps().forEach(app => rows.push({
    tile: true, app,
    action: () => enterActionsApp(app.id),
  }));
  rows.push({ tile: true, add: true, action: () => startAddApp() });
  return rows;
}

function enterActionsApp(appId) {
  weReset(acModel);
  actionsUi = { level: "app", appId };
  settingsPane = "rows";
  settingsIdx = 0;
  renderSettings();
  pulse($("settingsScroll"));
}

/* B on an app's wheel: back to the grid, landing on its tile. False at the grid, where B means
   the categories. */
function actionsBack() {
  if (actionsUi.level !== "app") return false;
  const i = shownApps().findIndex(a => a.id === actionsUi.appId);
  weReset(acModel);
  actionsUi = { level: "apps", appId: null };
  settingsPane = "rows";
  settingsIdx = Math.max(0, i);
  renderSettings();
  pulse($("settingsScroll"));
  return true;
}

function acApp() { return actionsUi.level === "app" ? actionApp(actionsUi.appId) : null; }
function acTabs() { const app = acApp(); return (app && app.tabs) || []; }
function acFind(id) { return weFind(acModel, id); }
function acActive() { return view === "settings" && settingsTab === "actions" && actionsUi.level === "app"; }
function acWhere(app) { return app.id === EVERYWHERE_ID ? "whatever app is in front" : app.name; }

/* The app's wheel in the wheel editor: what is on it, and what each edit tells the host. */
const acModel = {
  key: "actions",
  focusPrefix: "setrow:actwheel:",
  addLabel: "Add an action",
  backLabel: "Apps",
  get ui() { return acUi; },
  tabs: () => acTabs(),
  setTabs: (tabs) => { const app = acApp(); if (app) app.tabs = tabs; },
  maxItems: () => actionsMaxItems(),
  maxTabs: () => actionsMaxTabs(),
  shape: () => "actions|" + actionsUi.appId,
  spokeHtml: (it) => it ? actionSpokeHtml(it) : `<div class="sc-glyph">${iconSvg("plus")}</div><span class="spoke-name">Add an action</span>`,
  spokeClass: (it) => "ac-spoke" + (it.danger ? " danger" : "") + (it.keys ? "" : " no-keys"),
  cardHtml: (tab, sel, onAdd) => acCardHtml(tab, sel, onAdd),
  movingIconHtml: (it) => it ? keysHtml(it.keys, "big") : "",
  headHtml: () => acHeadHtml(),
  edit: (id) => acEdit(id),
  add: (tab) => acStartAdd(tab),
  rename: (it) => openInput("ACTION NAME", it.name, v => { if (v) acSave({ ...it, name: v }); }),
  remove: (it) => acConfirmRemove(it),
  back: () => actionsBack(),
  applyPending: () => acApplyPending(),
  newTabId: () => "t" + Math.random().toString(16).slice(2, 10).padEnd(8, "0"),
  deleteTabText: (t, n) => {
    const app = acApp();
    const own = (t.items || []).filter(a => !a.custom).length;
    return {
      sub: n ? `Its ${n === 1 ? "action goes" : n + " actions go"} with it` : "It is empty",
      body: !n ? "It has nothing on it."
        : own && app && !app.custom ? `${app.name}'s own actions on it can be added back later; any you made yourself are deleted.`
        : "The actions on it are deleted.",
    };
  },
  optionsHead: () => acOptionsHead(),
  optionSections: () => acOptionSections(),
  send: {
    move: (id, tabId, index) => send({ cmd: "actionMove", appId: actionsUi.appId, actionId: id, tabId, index }),
    tabAdd: (id, name, index) => send({ cmd: "actionTabAdd", appId: actionsUi.appId, id, name, index }),
    tabRename: (id, name) => send({ cmd: "actionTabRename", appId: actionsUi.appId, id, name }),
    tabMove: (id, index) => send({ cmd: "actionTabMove", appId: actionsUi.appId, id, index }),
    tabRemove: (id) => send({ cmd: "actionTabRemove", appId: actionsUi.appId, id }),
  },
};

/* Which app's wheel this is, at the start of the tab strip. */
function acHeadHtml() {
  const app = acApp();
  if (!app) return "";
  const icon = app.id === EVERYWHERE_ID ? iconSvg("globe")
    : app.icon ? `<span class="app-icon-sm" style="background-image:url('${scCssUrl(app.icon)}')"></span>`
    : `<span class="ac-head-mono">${esc((app.name || "?").trim().charAt(0).toUpperCase())}</span>`;
  return `<div class="ac-head">${icon}<span>${esc(app.name)}</span></div>`;
}

function acCardHtml(tab, sel, onAdd) {
  const app = acApp();
  if (!tab || !app) return "";
  if (onAdd || !sel) {
    const left = actionsMaxItems() - (tab.items || []).length;
    const spare = (app.spare || []).length;
    return `<div class="sc-card-icon add">${iconSvg("plus")}</div>` +
      `<div class="sc-card-kind mono">${esc(tab.name.toUpperCase())}</div>` +
      `<div class="sc-card-name">Add an action</div>` +
      `<div class="sc-card-what">${app.id === EVERYWHERE_ID ? "A keyboard shortcut for whatever app is in front" : `A keyboard shortcut ${esc(app.name)} understands`}, with a name and, if you like, a button.</div>` +
      (spare ? `<div class="sc-card-note">${spare === 1 ? `One of ${esc(app.name)}'s own is off the wheel` : `${spare} of ${esc(app.name)}'s own are off the wheel`}, and offered first</div>` : "") +
      `<div class="sc-card-note">${left === 1 ? "Room for one more on this tab" : `Room for ${left} more on this tab`}</div>` +
      (app.id !== EVERYWHERE_ID && !appActions(app).length
        ? `<div class="sc-card-note">${hintHtml("Or start from a community layout, another app's wheel, or a file: [[Y]] Options")}</div>` : "");
  }
  const notes = [];
  const warn = sel.button ? bindingWarn(app, sel) : null;
  if (warn) notes.push(`<div class="sc-card-warn">${hintHtml(warn)}</div>`);
  else if (sel.button) bindingNotes(app, sel).forEach(n => notes.push(`<div class="sc-card-note">${hintHtml(n)}</div>`));
  if (!sel.keys) notes.push(`<div class="sc-card-warn">No shortcut yet, so it does nothing. A, then Keyboard shortcut.</div>`);
  const kind = sel.custom ? "YOUR OWN" : "BUILT IN" + (sel.edited ? " · CHANGED" : "");
  return `<div class="sc-card-icon ac-card-keys">${keysHtml(sel.keys, "big")}</div>` +
    `<div class="sc-card-kind mono">${esc(kind)}</div>` +
    `<div class="sc-card-name">${esc(sel.name)}</div>` +
    `<div class="sc-card-what">${esc(sel.desc || acSentTo(app))}</div>` +
    (sel.desc ? `<div class="sc-card-note">${esc(acSentTo(app))}</div>` : "") +
    `<div class="sc-card-row"><span>Button</span><span>${sel.button ? comboHtml(sel.button) : `<span class="set-none">None</span>`}</span></div>` +
    notes.join("");
}

/* The options card (Y; wheeledit.js draws it): its heading, and the app's parts after "This tab".
   Bringing actions in is a part of its own, three tiles for the three ways in, so it is never
   mistaken for something done to the tab; then the app itself. On an empty wheel the ways in come
   first: they are the likeliest thing wanted there. */
function acOptionsHead() {
  const app = acApp(), tab = acTabs()[acUi.tab];
  if (!app) return { title: "Options" };
  const iconHtml = app.id === EVERYWHERE_ID ? `<span class="panel-app-icon">${iconSvg("globe")}</span>`
    : app.icon ? `<span class="panel-app-icon" style="background-image:url('${scCssUrl(app.icon)}')"></span>`
    : `<span class="panel-app-icon">${esc((app.name || "?").trim().charAt(0).toUpperCase())}</span>`;
  return { kicker: "OPTIONS", title: app.name, iconHtml,
    sub: tab ? `${tab.name} tab · ${(tab.items || []).length} of ${actionsMaxItems()} actions` : "" };
}

function acOptionSections() {
  const app = acApp();
  if (!app) return [];
  const out = [];
  if (app.id !== EVERYWHERE_ID) {
    const n = app.communityLayouts || 0;
    out.push({
      title: `Bring actions into ${app.name}`, tiles: true, first: !appActions(app).length,
      note: "Each one shows you what it would bring in first. Nothing changes until you say so.",
      items: [
        { key: "in:community", icon: "globe", label: "Community layouts",
          sub: n ? `${n === 1 ? "One wheel" : n + " wheels"} other people made for ${app.name}` : `Wheels other people made and shared`,
          hint: `Wheels other people made for ${app.name}: pick one to see what it would bring in`, action: () => acOpenCommunity(app) },
        { key: "in:app", icon: "apps", label: "Another app's wheel", sub: "Copy all of its tabs, or one of them",
          hint: `Copy actions from another app in your list, like one browser's into another`, action: () => acStartCopy() },
        { key: "in:file", icon: "download", label: "A file", sub: "A wheel saved with Save to a file",
          hint: "Open a file of actions, saved on this PC or sent by someone else", action: () => acImportFromFile(app) },
      ],
    });
  }
  const own = [];
  if (app.id !== EVERYWHERE_ID && appActions(app).some(a => a.keys)) own.push({
    key: "app:save", icon: "file", label: "Save to a file",
    hint: `Save ${app.name}'s wheel as a file: to keep, to move to another PC, or to share`,
    action: () => send({ cmd: "actionsExport", appId: app.id }),
  });
  if (app.canUndoImport) own.push({
    key: "app:undo", icon: "restart", label: "Undo the import",
    hint: `${app.importedFrom ? `${app.importedKind === "app" ? "Copied" : "Brought in"} from ${app.importedFrom}. ` : ""}Puts the wheel back to how it was just before, until anything here is changed`,
    action: () => askConfirm({
      title: "Undo the import?", body: `${app.name}'s wheel goes back to how it was just before it.`,
      yesLabel: "Undo", icon: "restart",
      onYes: () => { send({ cmd: "actionsUndoImport", appId: app.id }); acUi.tab = 0; settingsIdx = 0; },
    }),
  });
  if (!app.custom && app.modified) own.push({
    key: "app:restore", icon: "refresh", label: "Restore defaults", danger: true,
    hint: `Back to ${app.name}'s wheel as Loungepad ships it: every tab, change and action of your own here goes`,
    action: () => askConfirm({
      title: `Restore ${app.name}'s defaults?`, body: "Its tabs and actions go back to how Loungepad ships them. Actions you made yourself are deleted.",
      yesLabel: "Restore", icon: "refresh", danger: true,
      onYes: () => { send({ cmd: "actionAppReset", appId: app.id }); acUi.tab = 0; settingsIdx = 0; },
    }),
  });
  if (app.custom) own.push({
    key: "app:rename", icon: "edit", label: "Rename app", hint: `Call ${app.name} something else in this list`,
    action: () => openInput("APP NAME", app.name, v => { if (v) { app.name = v; send({ cmd: "actionAppRename", appId: app.id, name: v }); renderSettings(); } }),
  });
  if (app.custom || app.pinned) own.push({
    key: "app:remove", icon: "trash", label: "Remove app", danger: true,
    hint: app.custom ? `Take ${app.name} out of the list, with every action set up for it` : "Take it out of the list. The program stays on this PC, and comes back if you add it again",
    action: () => askConfirm({
      title: `Remove ${app.name}?`, body: app.custom ? "Every action you set up for it is deleted." : "Its actions and buttons go back to the defaults, and it leaves the grid.",
      yesLabel: "Remove", icon: "trash", danger: true,
      onYes: () => { send({ cmd: "actionAppRemove", appId: app.id }); actionsBack(); },
    }),
  });
  if (own.length) out.push({ title: app.id === EVERYWHERE_ID ? "Everywhere" : app.name, items: own });
  return out;
}

/* ---- bringing actions in: a copy from another app (docs/ACTION-SHARING.md) ----
   The host builds the layout, cleans it and plans the import; the page shows the plan and answers
   with the token and two choices, never with actions of its own. Everywhere is never a source or
   a target: its actions are on every app's wheel already, and its buttons answer in every app. */

function acCount(n, word) { return `${n === 1 ? "one" : n} ${word}${n === 1 ? "" : "s"}`; }

/* The apps a wheel can be copied from: any in the grid with something to send, but not this one. */
function acCopySources(app) {
  return shownApps().filter(a => a.id !== EVERYWHERE_ID && a.id !== app.id && appActions(a).some(x => x.keys));
}

/* A file of actions, into this app whatever program it was made for (the preview says so). */
function acImportFromFile(app) {
  acImport = { waiting: app.id };
  send({ cmd: "actionsImportFile", target: app.id });
}

/* ---- the community's layouts (loungepad-addons' actions/) ----
   The repository's list is cached by the host; the count on the tile comes from that cache, and
   the list is read again (if it is stale) when it is opened. A layout is previewed like a file:
   downloaded and checked against the list's hash on the host, nothing of it written to disk. */

let acCommunity = null;   // { waiting: appId } while the host reads the list

function acOpenCommunity(app, refresh) {
  acCommunity = { waiting: app.id };
  send({ cmd: "actionsCatalogue", appId: app.id, refresh: !!refresh });
  openChoice(`For ${app.name}`, [{ label: "Reading the list…", icon: "refresh", sub: "From the add-ons repository", action: () => acOpenCommunity(app) }]);
}

function onActionsCatalogue(m) {
  if (!acCommunity || acCommunity.waiting !== m.appId) return;
  acCommunity = null;
  const app = actionApp(m.appId);
  // Only over the placeholder: a list closed meanwhile stays closed.
  if (!app || !choiceState || choiceState.title !== `For ${app.name}`) return;
  const layouts = m.layouts || [];
  const items = layouts.map(l => {
    const size = `${acCount(l.actions, "action")} on ${acCount(l.tabs, "tab")}${l.buttons ? `, ${acCount(l.buttons, "button")}` : ""}`;
    const nums = [l.likes != null ? `${l.likes} ♥` : null, l.downloads != null ? `${l.downloads} brought in` : null, l.liked ? "you like it" : null].filter(Boolean);
    return {
      label: l.name, icon: l.needs ? "info" : "globe",
      sub: l.needs ? `Needs Loungepad ${l.needs}` : [l.author ? `By ${l.author}` : null, size, ...nums].filter(Boolean).join(" · "),
      action: () => {
        if (l.needs) { toast(`${l.name} needs Loungepad ${l.needs}. Settings › General has the update`); return; }
        acAskPreview(app, { kind: "catalogue", id: l.id });
      },
    };
  });
  if (!layouts.length) items.push(m.error
    ? { label: "Could not read the list", icon: "info", sub: m.error, action: () => acOpenCommunity(app, true) }
    : { label: `None for ${app.name} yet`, icon: "info", sub: "Yours could be the first: Save to a file, then see the add-ons repository", action: () => toast("github.com/sukumar-v/loungepad-addons says how to send one in") });
  items.push({ label: "Read the list again", icon: "refresh", sub: "It is kept for a few hours", action: () => acOpenCommunity(app, true) });
  openChoice(`For ${app.name}`, items);
}

/* "A file of actions…" from the grid: the host reads the file and previews it into the program
   it names, after asking here to add that program when the grid does not have it yet. */
function acImportFileFromGrid() {
  acImport = { waiting: "*" };
  send({ cmd: "actionsImportFile", target: null });
}

function onImportAddApp(m) {
  if (!acImport || acImport.waiting !== "*") return;
  askConfirm({
    title: `Add ${m.name}?`,
    body: `“${m.label}” is for ${m.name} (${m.exe}.exe), which is not in your list yet. It is added, then you see what the file would bring in.`,
    yesLabel: "Add", icon: "plus",
    onYes: () => { acImport = { waiting: "*" }; send({ cmd: "actionsImportAddApp", token: m.token }); },
  });
}

function acStartCopy() {
  const app = acApp();
  if (!app) return;
  const from = acCopySources(app);
  if (!from.length) { toast("No other app has actions to copy yet"); return; }
  openChoice(`Copy into ${app.name}`, from.map(src => {
    const tabs = (src.tabs || []).filter(t => (t.items || []).length).length;
    return { label: src.name, icon: "apps", sub: `${acCount(appActions(src).length, "action")} on ${acCount(tabs, "tab")}`, action: () => acPickCopyTab(app, src) };
  }));
}

/* The whole wheel, or one of its tabs; straight to the preview when there is only one. */
function acPickCopyTab(app, src) {
  const tabs = (src.tabs || []).filter(t => (t.items || []).length);
  const go = (tabId) => acAskPreview(app, { kind: "app", appId: src.id, tabId: tabId || null });
  if (tabs.length < 2) { go(null); return; }
  openChoice(`From ${src.name}`, [
    { label: "The whole wheel", icon: "apps", sub: `${acCount(appActions(src).length, "action")} on ${acCount(tabs.length, "tab")}`, action: () => go(null) },
    { cat: "ONE TAB" },
    ...tabs.map(t => ({ label: t.name, icon: "bolt", sub: acCount(t.items.length, "action"), action: () => go(t.id) })),
  ], { onBack: () => acStartCopy() });
}

/* The preview on screen: the host's message plus the two choices. While the host plans, only the
   app it was asked for, so an answer for another (or for a sheet since closed) is dropped. */
let acImport = null;

function acAskPreview(app, source) {
  acImport = { waiting: app.id };
  send({ cmd: "actionsImportPreview", target: app.id, source });
}

function onImportPreview(m) {
  const id = m.target && m.target.id;
  if (!acImport || (acImport.waiting !== id && acImport.waiting !== "*")) return;
  if (view !== "settings" || settingsTab !== "actions") return;
  // From the grid, the app the file is for: its wheel opens under the sheet.
  if (actionsUi.level !== "app" || actionsUi.appId !== id) {
    if (acImport.waiting !== "*" || !actionApp(id)) return;
    enterActionsApp(id);
  }
  acImport = { ...m, replace: !!m.empty, buttons: false, tab: 0 };
  openPanel({
    kind: "import", cls: "import-card",
    start: () => acImportCounts().placed ? "import" : "cancel",
    render: () => acImportHtml(),
    act: (key) => acImportAct(key),
    key: (btn) => acImportKey(btn),
    foot: (key) => acImportFoot(key),
    onClose: () => { if (acImport && acImport.token === m.token) acImport = null; },
  });
}

function acImportPlan() {
  const im = acImport;
  return im && im.plans ? im.plans[(im.replace ? "replace" : "add") + (im.buttons ? "Buttons" : "")] : null;
}
function acPlaced(i) { return i.status === "new" || i.status === "pack"; }

function acImportCounts() {
  const p = acImportPlan();
  const all = p ? p.tabs.flatMap(t => t.items) : [];
  return {
    placed: all.filter(acPlaced).length,
    have: all.filter(i => i.status === "have").length,
    left: all.filter(i => i.status === "twice" || i.status === "noroom").length,
    tabs: p ? p.tabs.filter(t => t.fits).length : 0,
    dropped: all.filter(i => acPlaced(i) && i.dropped).length,
  };
}

function acComboHint(combo) { return String(combo || "").split("+").map(b => `[[${b.trim()}]]`).join(" + "); }

/*
 * The import's screen, top to bottom (the user's layout, Oct 13 2026): who made it and its
 * numbers, with Like; what its maker said; the wheel as it would come in, a tab at a time, with
 * what happens to each tab in sentences beside it; the two choices; Cancel and the import itself.
 * Only the buttons are buttons: the wheel is a picture (flat names, no boxes, no highlight), the
 * sentences are text, and a tab chip is a button only when there is more than one tab to show.
 */
function acImportHtml() {
  const im = acImport, p = acImportPlan();
  if (!im || !p) return "";
  const c = acImportCounts();
  const tabs = p.tabs;
  im.tab = Math.max(0, Math.min(im.tab || 0, tabs.length - 1));
  const t = tabs[im.tab];
  const app = im.target.name;
  const total = tabs.reduce((n, x) => n + x.items.length, 0);
  const replacing = p.replace && !im.empty;

  // ---- who made it, and its numbers ----
  const meta = [];
  if (im.kind === "app") meta.push(`<span>A copy of another app's wheel</span>`);
  if (im.kind === "file") meta.push(`<span>From a file</span>`);
  if (im.author) meta.push(`<span>By <b>${esc(im.author)}</b></span>`);
  if (im.likes != null) meta.push(`<span class="imp-stat">${iconSvg("heart")}${im.likes} ${im.likes === 1 ? "like" : "likes"}</span>`);
  if (im.downloads != null) meta.push(`<span class="imp-stat">${iconSvg("download")}${im.downloads} brought in</span>`);
  meta.push(`<span>${acCount(total, "action")} on ${acCount(tabs.length, "tab")}</span>`);
  const like = im.key ? panelBtn("like", `${iconSvg("heart")}<span>${im.liked ? "Liked" : "Like"}</span>`, { cls: "pbtn-sm", on: im.liked }) : "";
  const head = `<div class="panel-head imp-head"><div class="panel-head-text">` +
    `<div class="panel-kicker mono">${esc(`BRING ACTIONS INTO ${app.toUpperCase()}`)}</div>` +
    `<div class="panel-title">${esc(im.label)}</div>` +
    `<div class="imp-meta">${meta.join(`<span class="imp-dot">·</span>`)}</div></div>${like}</div>`;

  // ---- what its maker said, and what the host noticed ----
  const notes = [...(im.notes || [])];
  if (im.madeFor && c.placed) notes.unshift(`These are ${im.madeFor}'s shortcuts. Most programs share the common ones; check any that matter do the same in ${app}.`);
  const about = im.summary || notes.length
    ? `<div class="imp-about">${im.summary ? `<p class="imp-summary">${esc(im.summary)}</p>` : ""}${notes.map(n => `<p class="imp-note">${esc(n)}</p>`).join("")}</div>` : "";

  // ---- the wheel, a tab at a time ----
  const chips = tabs.length > 1
    ? `<div class="imp-tabs">${slot("LB")}${tabs.map((x, i) => panelBtn("tab:" + i,
        `<span>${esc(x.name)}</span><span class="imp-tab-n">${x.items.length}</span>`,
        { cls: "imp-tab" + (x.fits ? "" : " nofit"), on: i === im.tab })).join("")}${slot("RB")}</div>`
    : "";
  const placedHere = t.items.filter(acPlaced).length;
  const wheel = `<div class="imp-wheel">` +
    t.items.map((it, i) => {
      const pos = scSpokeXY(i, t.items.length, 140, 230, 175);
      return `<div class="imp-spoke ${it.status}${it.danger && acPlaced(it) ? " danger" : ""}" style="left:${pos.x.toFixed(1)}px;top:${pos.y.toFixed(1)}px">${acImportSpokeHtml(it)}</div>`;
    }).join("") +
    `<div class="imp-hub"><div class="imp-hub-name mono">${esc(t.name.toUpperCase())}</div>` +
    `<div class="imp-hub-count">${placedHere ? `${placedHere} coming in` : "Nothing new"}</div></div></div>`;
  const body = `<div class="imp-body">${wheel}<div class="imp-tabcard">${acImportTabCard(t, im, p)}</div></div>`;

  // ---- the two choices ----
  const missed = p.replace ? 0 : tabs.flatMap(x => x.items).filter(i => i.status === "have" && i.layoutButton).length;
  const ownHere = tabs.some(x => x.items.some(i => i.status === "pack"));
  const how = `<div class="imp-opt"><div class="panel-label mono">HOW</div>` + (im.empty
    ? `<div class="imp-opt-note">${esc(app)}'s wheel is empty, so these become its tabs.</div>`
    : `<div class="pseg">${panelBtn("mode:add", "Add as new tabs", { on: !im.replace })}${panelBtn("mode:replace", "Replace my wheel", { on: im.replace, danger: true })}</div>` +
      `<div class="imp-opt-note">${im.replace ? "These become the wheel, and what is on it now goes. Undo can bring it back." : "These go on new tabs after yours. Nothing of yours changes."}</div>`) + `</div>`;
  const buttons = `<div class="imp-opt"><div class="panel-label mono">CONTROLLER BUTTONS</div>` + (im.hasButtons
    ? panelBtn("buttons", `<span class="pswitch"></span><span>Bring its button mappings</span>`, { on: im.buttons }) +
      `<div class="imp-opt-note">${esc(im.buttons
        ? (c.dropped ? `${c.dropped === 1 ? "One is" : c.dropped + " are"} left off, because something here has that button already: the tabs say which. ` : "Each fires its action without the wheel. ")
          + (missed ? `${missed === 1 ? "One is" : missed + " are"} for actions already on your wheel, which adding leaves alone: Replace takes ${missed === 1 ? "it" : "them"} too.` : "")
        : `Off: what comes in has no buttons${ownHere ? `, and ${app}'s own actions keep theirs` : ""}.`)}</div>`
    : `<div class="imp-opt-note">It has no button mappings.</div>`) + `</div>`;

  // ---- go ----
  const label = !c.placed ? null : replacing ? `Replace with ${acCount(c.placed, "action")}` : `Add ${acCount(c.placed, "action")}`;
  const actions = `<div class="panel-actions">${panelBtn("cancel", "Cancel")}` +
    (label ? panelBtn("import", `${iconSvg(replacing ? "refresh" : "download")}<span>${esc(label)}</span>`, { primary: true, danger: replacing })
      : `<div class="pbtn-off">Nothing new to bring in</div>`) + `</div>`;

  return head + `<div class="panel-scroll">${about}${chips}${body}<div class="imp-options">${how}${buttons}</div></div>` +
    actions + `<div class="panel-legend ov-foot"></div>`;
}

/* One spoke of the picture: what it is (a tag), its name, its keys and the button it would have. */
function acImportSpokeHtml(it) {
  const tag = { new: "NEW", pack: "BUILT IN", have: "ON YOUR WHEEL", twice: "REPEATED", noroom: "NO ROOM" }[it.status] || "";
  const btn = it.button ? `<span class="imp-btn">${comboHtml(it.button)}</span>`
    : acPlaced(it) && it.dropped && it.asked ? `<span class="imp-btn dropped">${comboHtml(it.asked)}</span>` : "";
  return `<span class="imp-tag mono">${tag}${acPlaced(it) && it.win ? " · WINDOWS" : ""}</span>` +
    `<span class="imp-name">${esc(it.name)}</span><span class="imp-keys">${keysHtml(it.keys)}</span>${btn}`;
}

/* Beside the wheel: what happens to the tab on screen, in sentences. Plain text; names quoted. */
function acImportTabCard(t, im, p) {
  const app = im.target.name;
  const names = (list) => {
    const q = list.map(i => `“${i.name}”`);
    return q.length > 4 ? `${q.slice(0, 3).join(", ")} and ${q.length - 3} more` : q.length > 1 ? `${q.slice(0, -1).join(", ")} and ${q[q.length - 1]}` : q[0];
  };
  const of = (s) => t.items.filter(i => i.status === s);
  const is = (list, one, many) => list.length === 1 ? one : many;
  const lines = [];
  const placed = t.items.filter(acPlaced);
  if (t.items.some(i => i.status === "noroom")) lines.push({ text: `There is no room for this tab: an app holds ${actionsMaxTabs()} tabs.`, warn: true });
  const nw = of("new"), pk = of("pack"), hv = of("have"), tw = of("twice");
  if (nw.length) lines.push({ text: `${names(nw)} ${is(nw, "is", "are")} new to ${app}.` });
  if (pk.length) lines.push({ text: `${names(pk)} ${is(pk, "is", "are")} ${app}'s own${p.replace ? "" : ", put back on the wheel"}, under ${is(pk, "its", "their")} own name.` });
  if (hv.length) lines.push({ text: `${names(hv)} ${is(hv, "is", "are")} on your wheel already, and ${is(hv, "stays as it is", "stay as they are")}.` });
  if (tw.length) lines.push({ text: `${names(tw)} ${is(tw, "repeats a shortcut", "repeat shortcuts")} from earlier, so ${is(tw, "it is", "they are")} left out.` });
  placed.filter(i => i.dropped && i.asked).forEach(i => lines.push({ text: `“${i.name}”: its button ${acComboHint(i.asked)} stays off. ${i.dropped}.` }));
  placed.filter(i => i.danger).forEach(i => lines.push({ text: `“${i.name}” closes or quits something, so it is red on the wheel.`, warn: true }));
  placed.filter(i => i.win).forEach(i => lines.push({ text: `“${i.name}” acts on Windows itself, not on ${app}.`, warn: true }));
  return `<div class="imp-tabcard-title mono">${esc(t.name.toUpperCase())}</div>` +
    `<div class="imp-tabcard-count">${placed.length ? `${acCount(placed.length, "action")} coming in` : "Nothing new on this tab"}</div>` +
    `<ul class="imp-lines">${lines.map(l => `<li${l.warn ? ` class="warn"` : ""}>${hintHtml(l.text)}</li>`).join("")}</ul>`;
}

function acImportAct(key) {
  const im = acImport;
  if (!im) return;
  if (key === "import") { acDoImport(); return; }
  if (key === "cancel") { closePanel(); return; }
  if (key === "like") {
    // The heart is kept on this PC; the service only hears +1 or -1 (docs/ADDONS.md).
    im.liked = !im.liked;
    if (im.likes != null) im.likes = Math.max(0, im.likes + (im.liked ? 1 : -1));
    send({ cmd: "addonLike", key: im.key, on: im.liked });
    return;
  }
  if (key === "mode:add") im.replace = false;
  else if (key === "mode:replace") im.replace = true;
  else if (key === "buttons") im.buttons = !im.buttons;
  else if (key.startsWith("tab:")) im.tab = parseInt(key.slice(4), 10) || 0;
}

/* LB and RB turn the wheel's tab, wherever the highlight is. */
function acImportKey(btn) {
  const p = acImportPlan();
  if (!p || (btn !== "LB" && btn !== "RB")) return false;
  if (p.tabs.length > 1) acImport.tab = (acImport.tab + (btn === "LB" ? -1 : 1) + p.tabs.length) % p.tabs.length;
  return true;
}

function acImportFoot(key) {
  const p = acImportPlan(), im = acImport;
  const a = key === "import" ? "Bring them in" : key === "cancel" ? "Cancel" : key === "like" ? (im && im.liked ? "Unlike" : "Like")
    : key === "buttons" ? "Switch" : key && key.startsWith("tab:") ? "Show this tab" : "Choose";
  return [["A", a], ...(p && p.tabs.length > 1 ? [["LB", "Tab before"], ["RB", "Tab after"]] : []), ["B", "Cancel"]];
}

function acDoImport() {
  const im = acImport;
  if (!im || !acImportCounts().placed) return;
  send({ cmd: "actionsImport", token: im.token, mode: im.replace ? "replace" : "add", buttons: !!im.buttons });
  closePanel();
}

/* The host's answer to an import: its actions push came first, so the wheel is already redrawn;
   this turns it to the first tab the import made. */
function onImported(m) {
  const n = (m.added || 0) + (m.restored || 0);
  toast(`${acCount(n, "action")} brought in${m.skipped ? `, ${m.skipped} already here or left out` : ""}. Undo is under Y, Options`);
  if (!acActive() || actionsUi.appId !== m.appId) return;
  const i = acTabs().findIndex(t => t.id === m.tabId);
  if (i >= 0) acUi.tab = i;
  settingsPane = "rows";
  settingsIdx = 0;
  renderSettings();
  pulse(document.querySelector(".sc-ring"));
}

/* ---- editing one ---- */

/* Into the page's copy first, so the wheel shows the change before the host answers; its actions
   push then replaces the list with what it stored. */
function acSave(next) {
  const f = acFind(next.id);
  if (!f) return;
  f.tab.items[f.index] = { ...f.item, ...next };
  send({ cmd: "actionUpdate", appId: actionsUi.appId, action: acWire(f.tab.items[f.index]) });
  if (acActive()) renderSettings();
}

function acWire(a) { return { id: a.id, name: a.name, keys: a.keys || "", button: a.button || null, desc: a.desc || null }; }

/* Where an action's keys go, in words: the card's line under its description. */
function acSentTo(app) { return `Sent to ${acWhere(app)}${app.id === EVERYWHERE_ID ? "" : " while it is in front"}`; }

let acEditingId = null;

function acEdit(id, idx) {
  acEditingId = id;
  const item = () => (acFind(id) || {}).item;
  openForm({
    kind: "action",
    kicker: () => { const f = acFind(id), app = acApp(); return f && app ? `${app.name.toUpperCase()} · ${f.tab.name.toUpperCase()}` : ""; },
    title: () => (item() || {}).name || "",
    sub: () => { const app = acApp(); return app ? `Sent to ${acWhere(app)}` : ""; },
    rows: () => acEditRows(id),
    backLabel: "Done",
    onClose: () => { if (acEditingId === id) acEditingId = null; },
  });
  if (idx) { formState.idx = idx; renderForm(); }
}

/* The edit sheet's row a new action opens on: the button, the one thing still to set. */
const AC_ROW_BUTTON = 3;

function acEditRows(id) {
  const f = acFind(id), app = acApp();
  if (!f || !app) return [{ note: "This action is no longer on the wheel." }];
  const a = f.item;
  const save = (patch) => acSave({ ...a, ...patch });
  const rows = [];
  rows.push({
    name: "Name", type: "html", aLabel: "Rename",
    valueHtml: `<span class="set-text">${esc(a.name)}</span>`,
    action: () => openInput("ACTION NAME", a.name, v => { if (v) save({ name: v }); }),
  });
  // What it does, in a sentence: shown in the wheel's centre as the action is highlighted. A
  // pack's own comes back when the line is emptied.
  rows.push({
    name: "Description", hint: a.desc || "None yet. A sentence on what it does helps whoever is still learning the shortcut",
    type: "html", aLabel: a.desc ? "Rewrite" : "Write", valueHtml: "",
    action: () => openInput("WHAT IT DOES", a.desc || "", v => save({ desc: v || null })),
  });
  rows.push({
    name: "Keyboard shortcut", hint: "What the program itself answers to, sent exactly as if typed on a keyboard",
    type: "html", aLabel: "Change",
    valueHtml: keysHtml(a.keys),
    action: () => openKeyPick(a.keys, keys => save({ keys }), `SHORTCUT FOR ${a.name.toUpperCase()}`),
  });
  const notes = a.button ? bindingNotes(app, a) : [];
  rows.push({
    name: "Controller button",
    hint: (a.button
      ? `Fires “${a.name}” while ${app.id === EVERYWHERE_ID ? "any app" : app.name} is in front and Loungepad is not`
      : "Optional. Press it in the app, with Loungepad out of the way, and the shortcut is sent")
      + notes.map(n => ". " + n).join(""),
    warn: a.button ? bindingWarn(app, a) : null,
    type: "html", aLabel: "Record",
    valueHtml: a.button ? comboHtml(a.button) : `<span class="set-none">None</span>`,
    action: () => openCapture(app, a, combo => save({ button: combo })),
  });
  if (a.button) rows.push({ name: "Clear the button", type: "action", label: "Clear", action: () => save({ button: null }) });
  if (a.edited) rows.push({
    name: `Restore ${app.name}'s own`, hint: "The name, shortcut, description and button it came with",
    type: "action", label: "Restore",
    action: () => send({ cmd: "actionRestore", appId: app.id, actionId: a.id }),
  });
  if (acTabs().length > 1) rows.push({
    name: "Move to another tab", hint: `On ${f.tab.name} now`, type: "action", label: "Move",
    action: () => acFromForm(back => acPickTab(a, back)),
  });
  rows.push({
    name: a.custom ? "Delete this action" : "Take it off the wheel",
    hint: a.custom ? null : "It can be added back with the + spoke",
    type: "action", label: a.custom ? "Delete" : "Remove", danger: true,
    action: () => acConfirmRemove(a),
  });
  return rows;
}

/* The choice sheet is drawn UNDER the form sheet (overlays stack in document order), so the form
   steps aside while a list is open and comes back on the same row afterwards. */
function acFromForm(open) {
  const id = acEditingId, idx = formState ? formState.idx : 0;
  closeForm();
  open(() => { if (acFind(id)) acEdit(id, idx); });
}

function acConfirmRemove(a) {
  const app = acApp();
  askConfirm(a.custom
    ? { title: `Delete “${a.name}”?`, body: "It is an action you made, so it is gone for good.", yesLabel: "Delete", icon: "trash", danger: true, onYes: () => acRemove(a.id) }
    : { title: `Take “${a.name}” off the wheel?`, body: `${a.button ? "Its button stops working too. " : ""}It can be added back with the + spoke${app ? `, under ${app.name}'s own` : ""}.`, yesLabel: "Remove", icon: "trash", danger: true, onYes: () => acRemove(a.id) });
}

function acRemove(id) {
  const f = acFind(id), app = acApp();
  if (!f || !app) return;
  if (formState && formState.kind === "action") closeForm();
  f.tab.items.splice(f.index, 1);
  if (!f.item.custom) (app.spare || (app.spare = [])).push(f.item);
  send({ cmd: "actionRemove", appId: app.id, actionId: id });
  settingsIdx = Math.max(0, f.index - (f.index >= f.tab.items.length ? 1 : 0));
  if (acActive()) renderSettings();
}

function acPickTab(a, back) {
  const here = acFind(a.id);
  const max = actionsMaxItems();
  const items = acTabs().filter(t => !here || t.id !== here.tab.id).map(t => {
    const full = (t.items || []).length >= max;
    return {
      label: t.name, icon: "apps", sub: full ? "Full" : `${(t.items || []).length} of ${max}`,
      action: () => {
        if (full) { toast(`${t.name} is full: a tab holds ${max}`); back(); return; }
        const f = acFind(a.id);
        if (!f) return;
        f.tab.items.splice(f.index, 1);
        t.items = t.items || [];
        t.items.push(f.item);
        acUi.tab = acTabs().indexOf(t);
        settingsIdx = t.items.length - 1;
        send({ cmd: "actionMove", appId: actionsUi.appId, actionId: a.id, tabId: t.id, index: t.items.length - 1 });
        if (acActive()) renderSettings();
        back();
      },
    };
  });
  openChoice("Move to", items, { onBack: back });
}

/* ---- adding one ---- */

/* The + spoke. With the pack's own actions off the wheel there is a list -- a new one, or one of
   those back -- else straight to the name. */
function acStartAdd(tab) {
  const app = acApp();
  if (!app || !tab) return;
  if ((tab.items || []).length >= actionsMaxItems()) { toast(`A tab holds ${actionsMaxItems()}. Y opens the options, to add another tab`); return; }
  const spare = app.spare || [];
  if (!spare.length) { acNewAction(tab, null); return; }
  const again = () => acStartAdd(tab);
  openChoice("Add an action", [
    { label: "A new action", icon: "plus", sub: "A name, then the keyboard shortcut to send", action: () => acNewAction(tab, again) },
    { cat: `${app.name.toUpperCase()}'S OWN, OFF THE WHEEL` },
    ...spare.map(a => ({
      label: a.name, icon: a.danger ? "trash" : "bolt",
      sub: (a.keys ? splitKeys(a.keys).map(keyLabel).join(" + ") : "No shortcut") + (a.desc ? " · " + a.desc : ""),
      action: () => acPlace(tab, { ...a }),
    })),
  ]);
}

/* Name, then shortcut, then the edit sheet on the button row. Backing out of either prompt makes
   nothing (and goes back to the list it came from, if any). */
function acNewAction(tab, back) {
  openInput("ACTION NAME", "", name => {
    if (!name) { if (back) back(); return; }
    openKeyPick("", keys => {
      const a = { id: actionNewId(), name, keys, button: null, custom: true, danger: false, edited: false };
      acPlace(tab, a);
      acEdit(a.id, AC_ROW_BUTTON);
    }, `SHORTCUT FOR ${name.toUpperCase()}`);
  });
}

/* At the + spoke's place (the end of the tab), in the page's copy first. */
function acPlace(tab, a) {
  const app = acApp();
  if (!app) return;
  if ((tab.items || []).length >= actionsMaxItems()) { toast(`${tab.name} is full: a tab holds ${actionsMaxItems()}`); return; }
  tab.items = tab.items || [];
  tab.items.push(a);
  app.spare = (app.spare || []).filter(x => x.id !== a.id);
  send({ cmd: "actionUpdate", appId: app.id, action: acWire(a), tabId: tab.id, index: tab.items.length - 1 });
  if (acActive()) {
    settingsPane = "rows";
    settingsIdx = tab.items.length - 1;
    renderSettings();
    pulse(document.querySelector(".sc-ring"));
  }
}

/* ---- the host's messages ---- */

/* The actions list, from an actions message or a state push. One that lands while an action is
   picked up waits until it is put down, or it would put the wheel back under the hand. `quiet` is
   the state push, which repaints everything itself. */
function onActionsData(data, quiet) {
  if (!data) return;
  if (acUi.moving) { acUi.pending = withActionIcons(data, acUi.pending || S.actions); return; }
  S.actions = withActionIcons(data, S.actions);
  if (quiet) return;
  if (view === "settings" && settingsTab === "actions") renderSettings();
  if (formState && formState.kind === "action") renderForm();
  refreshActionWheel();
}

function acApplyPending() {
  if (!acUi.pending) return;
  S.actions = acUi.pending;
  acUi.pending = null;
}

/* "Add an app": the windows open right now, by program, then the file dialog. The host answers
   with the list; the pick goes back as the exe, and the app that results is opened. */
let pendingAppAdd = false;
function startAddApp() {
  pendingAppAdd = true;
  send({ cmd: "actionListApps" });
}

function onActionApps(m) {
  if (!pendingAppAdd) return;
  pendingAppAdd = false;
  const items = [];
  const open = (m.open || []).filter(w => !w.listed);
  if (open.length) {
    items.push({ cat: "OPEN NOW" });
    open.forEach(w => items.push({
      label: w.title, icon: "monitor", sub: w.exe + ".exe",
      action: () => send({ cmd: "actionAppAdd", exe: w.exe, name: w.title }),
    }));
  } else {
    items.push({ cat: "OPEN NOW" });
    items.push({ label: "Nothing open that is not already listed", icon: "info", sub: "Open the program first and it shows up here", action: () => startAddApp() });
  }
  items.push({ cat: "OR" });
  items.push({ label: "Browse for a program…", icon: "folder", sub: "Pick its .exe", action: () => send({ cmd: "actionAppBrowse" }) });
  items.push({ label: "A file of actions…", icon: "download", sub: "A wheel someone saved: the program it is for is added with it", action: () => acImportFileFromGrid() });
  openChoice("Add an app", items);
}

/* The host added it (or found it already there): open its wheel. Null is a cancelled dialog. */
function onActionAppAdded(m) {
  if (!m.id) return;
  if (m.existed) toast("Already listed");
  settingsTab = "actions";
  enterActionsApp(m.id);
}

function askConfirm(state) {
  confirmState = state;
  confirmIdx = 0;
  showOverlay("overlay-confirm");
  renderConfirm();
}

/* The grid tiles: the icon (or a monogram when there is none), the name, and what is set. */
function actionsTileEl(idx) {
  const el = document.createElement("div");
  el.className = "app-tile";
  el.dataset.focusable = "";
  el.dataset.rowIndex = idx;
  el.addEventListener("mouseenter", () => {
    if (!hoverEnabled()) return;
    if (settingsIdx === idx && settingsPane === "rows") return;
    settingsIdx = idx; settingsPane = "rows"; renderSettings();
  });
  el.addEventListener("click", () => {
    settingsIdx = idx; settingsPane = "rows";
    // By focusable index, as settingsRowEl does: the Add-ons grid has headings between its tiles.
    const row = settingsRows().filter(x => !x.section)[idx];
    if (row && row.action) row.action();
  });
  return el;
}

function actionsTileHtml(row) {
  if (row.add) {
    return `<div class="app-icon app-add">${iconSvg("plus")}</div><div class="app-name">Add an app</div><div class="app-sub">Open now, browse, or a file</div>`;
  }
  const app = row.app;
  const list = appActions(app);
  const bound = list.filter(a => a.button).length;
  const tabs = (app.tabs || []).length;
  const icon = app.id === EVERYWHERE_ID ? `<div class="app-icon app-glyph">${iconSvg("globe")}</div>`
    : app.icon ? `<div class="app-icon" style="background-image:url('${app.icon}')"></div>`
    : `<div class="app-icon app-mono"><span>${esc((app.name || "?").trim().charAt(0).toUpperCase())}</span></div>`;
  const sub = app.id === EVERYWHERE_ID ? `In every app · ${tabs} tab${tabs === 1 ? "" : "s"}`
    : `${list.length} action${list.length === 1 ? "" : "s"}${bound ? ` · ${bound} on a button` : ""}`;
  return `${icon}<div class="app-name">${esc(app.name)}</div><div class="app-sub">${esc(sub)}</div>`;
}

/* ============================== record a button ============================== */

const CAPTURE_TIMEOUT_MS = 15000;

/* `check` is what refuses a combo, when the caller has its own rules (a Power Wheel shortcut's
   button, shortcuts.js); the default is the action's own bindingBlock. */
function openCapture(app, action, onDone, check, anyButton) {
  captureState = { app, action, onDone, check: check || null, timer: null, note: null, anyButton: !!anyButton };
  renderCapture();
  showOverlay("overlay-capture");
  send({ cmd: "actionCapture", anyButton: !!anyButton });
  armCaptureTimeout();
}

function armCaptureTimeout() {
  if (!captureState) return;
  clearTimeout(captureState.timer);
  captureState.timer = setTimeout(() => cancelCapture("Nothing was pressed"), CAPTURE_TIMEOUT_MS);
}

function renderCapture() {
  if (!captureState) return;
  $("captureIcon").innerHTML = iconSvg("controller");
  $("captureTitle").textContent = `Press a button for “${captureState.action.name}”`;
  $("captureBody").innerHTML = captureState.anyButton
    ? `Hold two or more for a combination, then let go. ${slot("B", true)} on its own cancels.`
    : `Hold two or more for a combination, then let go. ${slot("B", true)} on its own cancels; ` +
      `${slot("A", true)} on its own is the click button and cannot be used alone.`;
  const note = $("captureNote");
  note.hidden = !captureState.note;
  note.innerHTML = captureState.note ? hintHtml(captureState.note) : "";
}

function cancelCapture(reason) {
  if (!captureState) return;
  clearTimeout(captureState.timer);
  captureState = null;
  hideOverlay("overlay-capture");
  send({ cmd: "actionCaptureCancel" });
  if (reason) toast(reason);
}

/* The host's answer: a combo, or null for B. A combo that could never fire (see bindingIssues)
   is explained in the dialog and recording starts again, so nothing dead is ever stored. */
function onActionCaptured(combo) {
  if (!captureState) return;
  if (!combo) { cancelCapture(null); return; }
  const block = captureState.check ? captureState.check(combo) : bindingBlock(captureState.app, captureState.action, combo);
  if (block) {
    captureState.note = block;
    renderCapture();
    send({ cmd: "actionCapture", anyButton: captureState.anyButton });
    armCaptureTimeout();
    return;
  }
  clearTimeout(captureState.timer);
  const st = captureState;
  captureState = null;
  hideOverlay("overlay-capture");
  st.onDone(normCombo(combo));
}

function onActionCaptureRejected(button) {
  if (!captureState) return;
  captureState.note = button === "Guide"
    ? "Windows keeps [[Guide]] for itself. Try another button."
    : `[[${button}]] on its own is a click button. Hold another button with it, or pick a different one.`;
  renderCapture();
  armCaptureTimeout();
}

/* Only B gets here: the host swallows the pad while it records, so this is Esc, a click on the
   dimmed screen, or the Cancel chip. */
function captureInput(btn) {
  if (btn === "B") cancelCapture(null);
}

$("captureCancel").addEventListener("click", () => cancelCapture(null));

/* ============================== pick a shortcut ============================== */

/* The board, as [label, key, width in units]. A row is main keys, a gap, the navigation block,
   a gap, the number pad; "" is a spacer. The shape is a keyboard's so the eye finds a key where
   it expects it, and the nav engine walks it by geometry. */
const KP_ROWS = [
  [["Esc", "Esc", 1.3], ["F1", "F1"], ["F2", "F2"], ["F3", "F3"], ["F4", "F4"], ["F5", "F5"], ["F6", "F6"], ["F7", "F7"], ["F8", "F8"], ["F9", "F9"], ["F10", "F10"], ["F11", "F11"], ["F12", "F12"], ["", "", 1.7],
    "|", ["PrtSc", "PrintScreen"], ["ScrLk", "ScrollLock"], ["Pause", "Pause"], "|", ["", "", 4.3]],
  [["`", "`"], ["1", "1"], ["2", "2"], ["3", "3"], ["4", "4"], ["5", "5"], ["6", "6"], ["7", "7"], ["8", "8"], ["9", "9"], ["0", "0"], ["-", "-"], ["=", "="], ["Bksp", "Backspace", 2],
    "|", ["Ins", "Insert"], ["Home", "Home"], ["PgUp", "PageUp"], "|", ["NumLk", "NumLock"], ["/", "NumDivide"], ["*", "NumMultiply"], ["−", "NumSubtract"]],
  [["Tab", "Tab", 1.5], ["Q", "Q"], ["W", "W"], ["E", "E"], ["R", "R"], ["T", "T"], ["Y", "Y"], ["U", "U"], ["I", "I"], ["O", "O"], ["P", "P"], ["[", "["], ["]", "]"], ["\\", "\\", 1.5],
    "|", ["Del", "Delete"], ["End", "End"], ["PgDn", "PageDown"], "|", ["7", "Num7"], ["8", "Num8"], ["9", "Num9"], ["+", "NumAdd"]],
  [["Caps", "CapsLock", 1.8], ["A", "A"], ["S", "S"], ["D", "D"], ["F", "F"], ["G", "G"], ["H", "H"], ["J", "J"], ["K", "K"], ["L", "L"], [";", ";"], ["'", "'"], ["Enter", "Enter", 2.2],
    "|", ["", "", 3.3], "|", ["4", "Num4"], ["5", "Num5"], ["6", "Num6"], ["", "", 1]],
  [["", "", 2.3], ["Z", "Z"], ["X", "X"], ["C", "C"], ["V", "V"], ["B", "B"], ["N", "N"], ["M", "M"], [",", ","], [".", "."], ["/", "/"], ["", "", 1.7],
    "|", ["", "", 1.1], ["↑", "Up"], ["", "", 1.1], "|", ["1", "Num1"], ["2", "Num2"], ["3", "Num3"], ["⏎", "NumEnter"]],
  [["", "", 3.3], ["Space", "Space", 7], ["Menu", "Apps", 1.4], ["", "", 3.3],
    "|", ["←", "Left"], ["↓", "Down"], ["→", "Right"], "|", ["0", "Num0", 2.1], [".", "NumDecimal"], ["", "", 1]],
  [["Vol −", "VolumeDown", 1.5], ["Vol +", "VolumeUp", 1.5], ["Mute", "VolumeMute", 1.5], ["", "", 0.5],
    ["Prev", "MediaPrev", 1.5], ["Play/Pause", "MediaPlayPause", 2], ["Next", "MediaNext", 1.5], ["Stop", "MediaStop", 1.5], ["", "", 0.5],
    ["Back", "BrowserBack", 1.5], ["Fwd", "BrowserForward", 1.5], ["Refresh", "BrowserRefresh", 1.6], ["Home ⌂", "BrowserHome", 1.6]],
];

function buildKeyPickBoard() {
  const board = $("keypickBoard");
  if (board.children.length) return;
  KP_ROWS.forEach(row => {
    const r = document.createElement("div");
    r.className = "kp-row";
    row.forEach(cell => {
      const el = document.createElement("div");
      if (cell === "|") { el.className = "kp-block-gap"; r.appendChild(el); return; }
      const [label, key, units] = cell;
      el.style.setProperty("--u", units || 1);
      if (!key) { el.className = "kp-gap"; r.appendChild(el); return; }
      el.className = "kp-key";
      el.dataset.focusable = "";
      el.dataset.focusKey = "kp:key:" + key;
      el.dataset.key = key;
      el.textContent = label;
      el.addEventListener("mouseenter", () => { if (hoverEnabled()) { setFocusEl(el); paintNav(); } });
      el.addEventListener("click", () => chooseKey(key));
      r.appendChild(el);
    });
    board.appendChild(r);
  });
  const mods = $("keypickMods");
  MOD_ORDER.forEach(m => {
    const el = document.createElement("div");
    el.className = "kp-chip";
    el.dataset.focusable = "";
    el.dataset.focusKey = "kp:mod:" + m;
    el.dataset.mod = m;
    el.innerHTML = `<span class="kp-chip-check"></span><span>${m}</span>`;
    el.addEventListener("mouseenter", () => { if (hoverEnabled()) { setFocusEl(el); paintNav(); } });
    el.addEventListener("click", () => toggleMod(m));
    mods.appendChild(el);
  });
}

function openKeyPick(current, onDone, title) {
  buildKeyPickBoard();
  const parts = splitKeys(current);
  keyPick = { onDone, mods: new Set(parts.slice(0, -1).filter(p => MOD_ORDER.includes(p))) };
  const key = parts.length ? parts[parts.length - 1] : null;
  $("keypickTitle").textContent = title || "SHORTCUT";
  showOverlay("overlay-keypick");
  const scope = $("overlay-keypick");
  const land = (key && scope.querySelector(`.kp-key[data-key="${CSS.escape(key)}"]`)) || scope.querySelector('.kp-key[data-key="T"]');
  setScopeKey(scope, land ? Nav.keyOf(land) : "kp:mod:Ctrl");
  renderKeyPick();
}

function closeKeyPick() {
  if (!keyPick) return;
  keyPick = null;
  hideOverlay("overlay-keypick");
}

function renderKeyPick() {
  if (!keyPick) return;
  const mods = MOD_ORDER.filter(m => keyPick.mods.has(m));
  $("keypickPreview").innerHTML = mods.length
    ? `<span class="keys big">${mods.map(m => keycap(m)).join(`<span class="combo-plus">+</span>`)}<span class="combo-plus">+</span><span class="kp-slot">?</span></span>`
    : `<span class="kp-slot-hint">Toggle Ctrl, Shift, Alt or Win, then choose the key</span>`;
  $("keypickMods").querySelectorAll(".kp-chip").forEach(el => el.classList.toggle("on", keyPick.mods.has(el.dataset.mod)));
  $("keypickFoot").innerHTML = foot(["A", "Choose"], ["B", "Cancel"], ["X", "Ctrl"], ["Y", "Shift"]) +
    `<span class="kp-tip">or press the shortcut on a keyboard</span>`;
  paintNav();
}

function toggleMod(m) {
  if (!keyPick) return;
  if (keyPick.mods.has(m)) keyPick.mods.delete(m); else keyPick.mods.add(m);
  renderKeyPick();
}

function chooseKey(key) {
  if (!keyPick) return;
  const keys = MOD_ORDER.filter(m => keyPick.mods.has(m)).concat([key]).join("+");
  const st = keyPick;
  closeKeyPick();
  st.onDone(keys);
}

function keyPickInput(btn) {
  const scope = $("overlay-keypick");
  switch (btn) {
    case "Up": case "Down": case "Left": case "Right":
      if (listMove(btn)) paintNav();
      break;
    case "A": {
      if (!focusVisible()) break;
      const el = focusEl(scope);
      if (!el) break;
      if (el.dataset.mod) toggleMod(el.dataset.mod);
      else if (el.dataset.key) chooseKey(el.dataset.key);
      break;
    }
    case "X": toggleMod("Ctrl"); break;
    case "Y": toggleMod("Shift"); break;
    case "B": closeKeyPick(); break;
  }
}

/* A real keyboard, when there is one: the shortcut as pressed. Bare navigation keys still walk
   the board; anything else, with or without modifiers, is the answer. In the capture phase so it
   runs before the page's own key map turns X into a pad button. */
const KP_CODE_NAMES = {
  Minus: "-", Equal: "=", BracketLeft: "[", BracketRight: "]", Backslash: "\\", Semicolon: ";", Quote: "'",
  Comma: ",", Period: ".", Slash: "/", Backquote: "`", ContextMenu: "Apps", Escape: "Esc",
  NumpadAdd: "NumAdd", NumpadSubtract: "NumSubtract", NumpadMultiply: "NumMultiply", NumpadDivide: "NumDivide",
  NumpadDecimal: "NumDecimal", NumpadEnter: "NumEnter", ArrowUp: "Up", ArrowDown: "Down", ArrowLeft: "Left", ArrowRight: "Right",
  AudioVolumeUp: "VolumeUp", AudioVolumeDown: "VolumeDown", AudioVolumeMute: "VolumeMute",
  MediaPlayPause: "MediaPlayPause", MediaTrackNext: "MediaNext", MediaTrackPrevious: "MediaPrev", MediaStop: "MediaStop",
  BrowserBack: "BrowserBack", BrowserForward: "BrowserForward", BrowserRefresh: "BrowserRefresh", BrowserHome: "BrowserHome",
};
function keyNameFromEvent(e) {
  const c = e.code || "";
  if (KP_CODE_NAMES[c]) return KP_CODE_NAMES[c];
  let m;
  if ((m = /^Key([A-Z])$/.exec(c))) return m[1];
  if ((m = /^Digit(\d)$/.exec(c))) return m[1];
  if ((m = /^Numpad(\d)$/.exec(c))) return "Num" + m[1];
  if (/^F\d{1,2}$/.test(c)) return c;
  if (["Tab", "Enter", "Space", "Backspace", "Delete", "Insert", "Home", "End", "PageUp", "PageDown", "CapsLock", "NumLock", "ScrollLock", "Pause", "PrintScreen"].includes(c)) return c;
  return null;
}
window.addEventListener("keydown", (e) => {
  if (!keyPick) return;
  if (["Control", "Shift", "Alt", "Meta"].includes(e.key)) return;
  const mods = e.ctrlKey || e.altKey || e.metaKey || e.shiftKey;
  const nav = ["Escape", "Enter", "ArrowUp", "ArrowDown", "ArrowLeft", "ArrowRight", "Space"].includes(e.code) || e.key === " ";
  if (nav && !mods) return;   // walks the board, or Enter picks, or Esc backs out
  const key = keyNameFromEvent(e);
  if (!key) return;
  e.preventDefault();
  e.stopPropagation();
  if (e.ctrlKey) keyPick.mods.add("Ctrl");
  if (e.shiftKey) keyPick.mods.add("Shift");
  if (e.altKey) keyPick.mods.add("Alt");
  if (e.metaKey) keyPick.mods.add("Win");
  chooseKey(key);
}, true);
