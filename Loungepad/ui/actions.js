"use strict";

/*
 * Actions: a program's keyboard shortcuts, on the pad.
 *
 * Three pieces, all fed by S.actions (the host's apps, packs merged with the user's changes):
 *
 *   1. The action wheel. The Power Wheel's Actions spoke opens a second wheel with the shortcuts of
 *      whatever was in front when the menu came up -- the host names the exe, this file matches
 *      it to an app -- eight to a page, the Everywhere set after the app's own. A spoke shows
 *      the action, its keys and the pad button it is bound to; A presses it, and a bound button
 *      presses its action directly. Firing hands the foreground back to that window first, so
 *      the keys land where they were meant to.
 *
 *   2. Settings → Actions. A grid of the apps on this PC we have actions for, plus Everywhere and
 *      the user's own; A on a tile drills into its list, A on an action into its editor. Three
 *      levels in the same pane, B going back up one at a time, so it reads as one place rather
 *      than a stack of popups.
 *
 *   3. The two pickers an action needs: "press a button" (the host records the next press or
 *      chord and swallows it) and a keyboard laid out to point at, for the shortcut.
 *
 * Bindings fire on the host, in the pad loop, only while the app is in front and the launcher is
 * not; this file never sends a key. Loaded after radial.js and shares app.js's globals.
 */

const ACTION_SPOKES = 8;
const EVERYWHERE_ID = "everywhere";
const MOD_ORDER = ["Ctrl", "Shift", "Alt", "Win"];

/* ---- the data ---- */

function actionApps() { return (S.actions && S.actions.apps) || []; }
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
/* What the Settings grid shows: Everywhere first, then the rest by name. A pack for a program
   that is not on this PC stays out of the way. */
function shownApps() {
  const apps = actionApps().filter(a => a.installed);
  const ev = apps.filter(a => a.id === EVERYWHERE_ID);
  const rest = apps.filter(a => a.id !== EVERYWHERE_ID).sort((a, b) => a.name.localeCompare(b.name));
  return ev.concat(rest);
}
function visibleActions(app) { return ((app && app.actions) || []).filter(a => !a.hidden); }
function actionById(app, id) { return ((app && app.actions) || []).find(a => a.id === id) || null; }

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
  if (comboEq(combo, s.minimizeCombo))
    issues.push({ level: "block", text: `That is the menu combo, which always wins. Pick another button.` });
  if (s.screenshotCombo && s.screenshotCombo !== "Off" && comboEq(combo, s.screenshotCombo))
    issues.push({ level: "block", text: `That is the screenshot button, which always wins. Pick another button.` });

  const toggle = canonBtn(s.keyboardToggleButton || "RB");
  const hold = (s.keyboardToggleMode || "Press") === "Hold";
  if (single === toggle && !hold)
    issues.push({ level: "warn", text: `${say(toggle)} opens the keyboard on a press, so it wins over this. Change the keyboard button under Keyboard, set it to Hold, or pick another button.` });
  else if (single === toggle && hold)
    issues.push({ level: "note", text: `A tap fires this; holding ${say(toggle)} still opens the keyboard.` });

  const boost = canonBtn(s.boostButton || "");
  if (boost && boost !== "Off" && parts.includes(boost))
    issues.push({ level: "note", text: `${say(boost)} is also the speed boost while it is held.` });

  const left = canonBtn(s.leftClickButton || "A"), right = canonBtn(s.rightClickButton || "B");
  if (single && (single === left || single === right) && !(single === "A" || single === "B"))
    issues.push({ level: "warn", text: `${say(single)} is the ${single === left ? "left" : "right"} click button. In ${esc(app.name)} it fires this instead of clicking.` });

  if (app) {
    const twin = (app.actions || []).find(a => a !== action && a.id !== (action && action.id) && comboEq(a.button, combo));
    if (twin) issues.push({ level: "block", text: `${esc(twin.name)} already uses that button here. Pick another, or clear it there first.` });
    if (app.id !== EVERYWHERE_ID) {
      const ev = everywhereApp();
      const over = ev && (ev.actions || []).find(a => comboEq(a.button, combo));
      if (over) issues.push({ level: "note", text: `Overrides Everywhere's “${esc(over.name)}” while ${esc(app.name)} is in front.` });
    }
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
let actionPages = [];          // [{ app, items }]
let actionPage = 0, actionIdx = 0;

/* The pages: the app in front's visible actions, eight a page, then Everywhere's. With nothing
   for the app there is still a page for it, empty, so the wheel can say so and point at Settings. */
function buildActionPages() {
  const target = appForProcess(overlayTargetProcess);
  const pages = [];
  const chunk = (app) => {
    const list = visibleActions(app);
    for (let i = 0; i < list.length; i += ACTION_SPOKES) pages.push({ app, items: list.slice(i, i + ACTION_SPOKES) });
  };
  if (target) chunk(target);
  const ev = everywhereApp();
  if (ev) chunk(ev);
  if (!pages.length) pages.push({ app: target || ev || { id: "", name: "Actions", actions: [] }, items: [] });
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
    (it.button ? `<span class="spoke-btn">${comboHtml(it.button)}</span>` : "");
}

function renderActionWheel(rebuild) {
  const page = actionPages[actionPage] || { app: { name: "Actions" }, items: [] };
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

  const total = actionPages.length;
  $("actionsTitle").textContent = "ACTIONS" + (total > 1 ? ` · ${actionPage + 1} / ${total}` : "");
  const appIcon = page.app.id === EVERYWHERE_ID ? iconSvg("globe")
    : page.app.icon ? `<span class="app-icon-sm" style="background-image:url('${page.app.icon}')"></span>`
    : iconSvg("bolt");
  $("actionsApp").innerHTML = appIcon + `<span>${esc(String(page.app.name || "").toUpperCase())}</span>`;
  const sel = items[actionIdx];
  if (sel) {
    $("actionsSelName").textContent = sel.name;
    $("actionsKeys").innerHTML = keysHtml(sel.keys, "big") + (sel.button ? `<span class="actions-btn">${comboHtml(sel.button)}</span>` : "");
  } else {
    $("actionsSelName").textContent = "Nothing here yet";
    $("actionsKeys").innerHTML = `<span class="actions-hint">Add actions under Settings › Actions</span>`;
  }
  const target = appForProcess(overlayTargetProcess);
  const targetLine = overlayTargetProcess
    ? (overlayTargetTitle || overlayTargetProcess).toUpperCase() + (target ? "" : " · NO ACTIONS FOR THIS APP YET")
    : "NO APP IN FRONT";
  $("actionsTarget").textContent = targetLine;
  // The pager is its own row between the ring and the legend, so it never lands on a spoke: the
  // shoulders either side say how to turn the page, and each is clickable like a legend entry.
  const pager = $("actionsPager");
  pager.hidden = total < 2;
  pager.innerHTML = total > 1
    ? `<div class="legend-item" data-press="LB">${slot("LB")}</div>` +
      `<div class="actions-pages">${actionPages.map((p, i) =>
        `<i class="${i === actionPage ? "on" : ""}${p.app.id === EVERYWHERE_ID ? " ev" : ""}"></i>`).join("")}</div>` +
      `<div class="legend-item" data-press="RB">${slot("RB")}</div>`
    : "";
  $("actionsFoot").innerHTML = foot(["A", "Press"], ["B", "Back"]);
}

function actionPageStep(dir) {
  const total = actionPages.length;
  if (total < 2) return;
  actionPage = (actionPage + dir + total) % total;
  actionIdx = 0;
  renderActionWheel(true);
}

/* An action bound to a button, for the app in front first and Everywhere second: what a press
   of that button does on the wheel, hidden or not. */
function boundAction(btn) {
  const target = appForProcess(overlayTargetProcess);
  for (const app of [target, everywhereApp()]) {
    const hit = app && (app.actions || []).find(a => a.button && comboEq(a.button, btn));
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
  actionPages = buildActionPages();
  actionPage = Math.min(actionPage, actionPages.length - 1);
  renderActionWheel(true);
}

/* ============================== Settings → Actions ============================== */

/* Which level of the category is showing, and of what. Reset to the grid whenever the category
   is entered afresh (setSettingsTab). */
let actionsUi = { level: "apps", appId: null, actionId: null };

function actionsTabReset() { actionsUi = { level: "apps", appId: null, actionId: null }; }
function actionsGridMode() { return settingsTab === "actions" && actionsUi.level === "apps"; }

/* The rows the generic settings renderer draws for this category, by level. At the grid level
   each "row" is a tile; renderSettings draws those differently but drives them the same way. */
function actionsSettingsRows() {
  if (actionsUi.level === "app") {
    const app = actionApp(actionsUi.appId);
    if (app) return actionsAppRows(app);
    actionsTabReset();
  }
  if (actionsUi.level === "action") {
    const app = actionApp(actionsUi.appId);
    const a = actionById(app, actionsUi.actionId);
    if (app && a) return actionsEditorRows(app, a);
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

function enterActionsApp(appId, actionIdx) {
  actionsUi = { level: "app", appId, actionId: null };
  settingsPane = "rows";
  settingsIdx = actionIdx || 0;
  renderSettings();
  pulse($("settingsScroll"));
}

function enterActionsEditor(appId, actionId, rowIdx) {
  actionsUi = { level: "action", appId, actionId };
  settingsPane = "rows";
  settingsIdx = rowIdx || 0;
  renderSettings();
  pulse($("settingsScroll"));
}

/* B inside the category: up one level, landing on what was opened. False at the grid, where B
   means the categories. */
function actionsBack() {
  if (actionsUi.level === "action") {
    const app = actionApp(actionsUi.appId);
    const i = app ? (app.actions || []).findIndex(a => a.id === actionsUi.actionId) : -1;
    enterActionsApp(actionsUi.appId, Math.max(0, i));
    return true;
  }
  if (actionsUi.level === "app") {
    const i = shownApps().findIndex(a => a.id === actionsUi.appId);
    actionsTabReset();
    settingsPane = "rows";
    settingsIdx = Math.max(0, i);
    renderSettings();
    pulse($("settingsScroll"));
    return true;
  }
  return false;
}

/* One app's list: every action as a row showing its keys and its button, then the management
   rows. Hidden actions stay in the list, dimmed, so they can be brought back. */
function actionsAppRows(app) {
  const rows = [];
  const list = app.actions || [];
  const bound = list.filter(a => a.button).length;
  const bits = [`${list.length} action${list.length === 1 ? "" : "s"}`];
  if (bound) bits.push(`${bound} on a button`);
  if (app.id === EVERYWHERE_ID) bits.push("in every app");
  rows.push({ section: `${app.name.toUpperCase()} · ${bits.join(" · ").toUpperCase()}`, cat: "actions" });
  if (!list.length)
    rows.push({ name: "No actions yet", hint: "Add one below: a shortcut the program understands, a name, and a button if you want one", type: "html", valueHtml: "", action: () => startAddAction(app) });
  list.forEach((a, i) => {
    const notes = [];
    if (a.hidden) notes.push("Hidden from the wheel");
    if (!a.keys) notes.push("No shortcut yet");
    const warn = bindingWarn(app, a);
    rows.push({
      name: a.name, hint: notes.join(" · ") || null, warn, muted: a.hidden,
      type: "html",
      valueHtml: `${keysHtml(a.keys)}${a.button ? `<span class="set-btn">${comboHtml(a.button)}</span>` : ""}<span class="arrow">▸</span>`,
      action: () => enterActionsEditor(app.id, a.id, 0),
    });
  });
  rows.push({ section: "MANAGE", cat: "actions" });
  rows.push({
    name: "Add an action", hint: "A name, then the shortcut, then a button if you want one",
    type: "action", label: "Add",
    action: () => startAddAction(app),
  });
  if (app.custom) rows.push({
    name: "Rename", hint: app.name, type: "action", label: "Rename",
    action: () => openInput("APP NAME", app.name, v => { if (v) { app.name = v; send({ cmd: "actionAppRename", appId: app.id, name: v }); renderSettings(); } }),
  });
  if (!app.custom && app.modified) rows.push({
    name: "Reset to defaults", hint: "Every change and every action you added here goes; the pack comes back as shipped",
    type: "action", label: "Reset",
    action: () => askConfirm({
      title: `Reset ${app.name}?`, body: "Its actions go back to the defaults. Your own actions here are removed.",
      yesLabel: "Reset", icon: "refresh", danger: true,
      onYes: () => { send({ cmd: "actionAppReset", appId: app.id }); settingsIdx = 0; },
    }),
  });
  if (app.custom || app.pinned) rows.push({
    name: "Remove this app", hint: app.custom ? "Its actions go with it" : "The program stays on this PC; it comes back if you add it again",
    type: "action", label: "Remove", danger: true,
    action: () => askConfirm({
      title: `Remove ${app.name}?`, body: app.custom ? "Every action you set up for it is deleted." : "Its actions and buttons go back to the defaults, and it leaves the grid.",
      yesLabel: "Remove", icon: "trash", danger: true,
      onYes: () => { send({ cmd: "actionAppRemove", appId: app.id }); actionsTabReset(); settingsIdx = 0; },
    }),
  });
  return rows;
}

/* One action's editor. Every change is saved at once: the row it changes is the row you are on. */
function actionsEditorRows(app, a) {
  const rows = [];
  const save = (patch) => saveAction(app, Object.assign({}, a, patch));
  rows.push({ section: `${app.name.toUpperCase()} › ${a.name.toUpperCase()}`, cat: "actions" });
  rows.push({
    name: "Name", type: "html",
    valueHtml: `<span class="set-text">${esc(a.name)}</span><span class="arrow">▸</span>`,
    action: () => openInput("ACTION NAME", a.name, v => { if (v) save({ name: v }); }),
  });
  rows.push({
    name: "Keyboard shortcut",
    hint: "What the program itself answers to. Sent exactly as if typed on a keyboard",
    type: "html",
    valueHtml: `${keysHtml(a.keys)}<span class="arrow">▸</span>`,
    action: () => openKeyPick(a.keys, keys => save({ keys }), `SHORTCUT FOR ${a.name.toUpperCase()}`),
  });
  const notes = bindingNotes(app, a);
  rows.push({
    name: "Controller button",
    hint: (a.button
      ? `Fires “${a.name}” while ${app.id === EVERYWHERE_ID ? "any app" : app.name} is in front and Loungepad is not`
      : "Optional. Press it in the app, with Loungepad out of the way, and the shortcut is sent")
      + (notes.length ? ". " + notes.join(" ") : ""),
    warn: bindingWarn(app, a),
    type: "html",
    valueHtml: `${a.button ? comboHtml(a.button) : `<span class="set-none">None</span>`}<span class="arrow">▸</span>`,
    action: () => openCapture(app, a, combo => save({ button: combo })),
  });
  if (a.button) rows.push({
    name: "Clear the button", type: "action", label: "Clear",
    action: () => save({ button: null }),
  });
  rows.push({
    name: "Show on the wheel", hint: "Off keeps it out of the wheel. A button still works either way",
    type: "toggle", value: !a.hidden,
    adjust: () => save({ hidden: !a.hidden }),
    action: () => save({ hidden: !a.hidden }),
  });
  if (a.custom) rows.push({
    name: "Delete this action", type: "action", label: "Delete", danger: true,
    action: () => askConfirm({
      title: `Delete “${a.name}”?`, body: "This cannot be undone.", yesLabel: "Delete", icon: "trash", danger: true,
      onYes: () => {
        const i = (app.actions || []).findIndex(x => x.id === a.id);
        if (i >= 0) app.actions.splice(i, 1);
        send({ cmd: "actionRemove", appId: app.id, actionId: a.id });
        enterActionsApp(app.id, Math.max(0, i - 1));
      },
    }),
  });
  return rows;
}

/* Into the local copy first, so the row shows the change before the host answers; the host's
   actions push then replaces the list with what it stored. */
function saveAction(app, next) {
  const list = app.actions || (app.actions = []);
  const i = list.findIndex(x => x.id === next.id);
  if (i >= 0) list[i] = next; else list.push(next);
  send({ cmd: "actionUpdate", appId: app.id, action: next });
  renderSettings();
}

function newActionId() {
  return "c" + Math.random().toString(16).slice(2, 10);
}

/* Name, then shortcut, then the editor with the button row lit. Backing out of either prompt
   makes nothing. */
function startAddAction(app) {
  openInput("ACTION NAME", "", name => {
    if (!name) return;
    openKeyPick("", keys => {
      const a = { id: newActionId(), name, keys, button: null, hidden: false, custom: true, danger: false };
      (app.actions || (app.actions = [])).push(a);
      send({ cmd: "actionUpdate", appId: app.id, action: a });
      enterActionsEditor(app.id, a.id, 2);
    }, `SHORTCUT FOR ${name.toUpperCase()}`);
  });
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
  openChoice("Add an app", items);
}

/* The host added it (or found it already there): open its list. Null is a cancelled dialog. */
function onActionAppAdded(m) {
  if (!m.id) return;
  if (m.existed) toast("Already listed");
  settingsTab = "actions";
  enterActionsApp(m.id, 0);
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
    const row = settingsRows()[idx];
    if (row && row.action) row.action();
  });
  return el;
}

function actionsTileHtml(row) {
  if (row.add) {
    return `<div class="app-icon app-add">${iconSvg("plus")}</div><div class="app-name">Add an app</div><div class="app-sub">Open now, or browse</div>`;
  }
  const app = row.app;
  const list = app.actions || [];
  const bound = list.filter(a => a.button).length;
  const icon = app.id === EVERYWHERE_ID ? `<div class="app-icon app-glyph">${iconSvg("globe")}</div>`
    : app.icon ? `<div class="app-icon" style="background-image:url('${app.icon}')"></div>`
    : `<div class="app-icon app-mono"><span>${esc((app.name || "?").trim().charAt(0).toUpperCase())}</span></div>`;
  const sub = app.id === EVERYWHERE_ID ? "In every app"
    : `${list.length} action${list.length === 1 ? "" : "s"}${bound ? ` · ${bound} on a button` : ""}`;
  return `${icon}<div class="app-name">${esc(app.name)}</div><div class="app-sub">${esc(sub)}</div>`;
}

/* ============================== record a button ============================== */

const CAPTURE_TIMEOUT_MS = 15000;

function openCapture(app, action, onDone) {
  captureState = { app, action, onDone, timer: null, note: null };
  renderCapture();
  showOverlay("overlay-capture");
  send({ cmd: "actionCapture" });
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
  $("captureBody").innerHTML =
    `Hold two or more for a combination, then let go. ${slot("B", true)} on its own cancels; ` +
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
  const block = bindingBlock(captureState.app, captureState.action, combo);
  if (block) {
    captureState.note = block;
    renderCapture();
    send({ cmd: "actionCapture" });
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
      if (navMove(btn)) paintNav();
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
