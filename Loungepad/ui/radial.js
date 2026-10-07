"use strict";

/*
 * Power Wheel (the radial) and the in-game menu.
 *
 * Loaded after app.js and shares its globals ($, send, esc, iconSvg, foot, renderMenu,
 * focusVisible, hoverEnabled, setOverlayMode, switchView, gameById, S).
 */

let radialOpen = false, radialIdx = 0;
let radialSub = null, radialSubIdx = 0;     // null | "windows" | "shortcuts"
let ingameOpen = false, ingameIdx = 0;
let overlayTargetTitle = "";
let hostWindows = [];

/* Spokes are laid out clockwise from the top and the ring divides 360 by however many there
   are, so the count is free to change. */
const RADIAL_ITEMS = [
  { id: "close",       label: "Close window",  icon: "x",        danger: true,   // top
    desc: "Ask the window behind this menu to quit" },
  /* The home spoke: the shortcuts of whatever is behind the menu, on the pad. See actions.js. */
  { id: "actions",     label: "Actions",       icon: "bolt",
    desc: "Shortcuts for the app in front, and for everywhere" },
  { id: "windows",     label: "Switch window", icon: "viewBtn",
    desc: "Pick another open window and bring it to the TV" },
  { id: "shortcuts",   label: "Shortcuts",     icon: "apps",
    desc: "Task Manager, Explorer, Settings and friends" },
  { id: "keyboard",    label: "Keyboard",      icon: "keyboard",
    desc: "Show or hide the on-screen keyboard" },
  { id: "centerMouse", label: "Center mouse",  icon: "pointer",
    desc: "Park the pointer in the middle of the TV" },
  /* Label and description are rewritten from the live setting in renderRadial: this one is a
     toggle, and a spoke that cannot say which way it is currently set is a coin flip. */
  { id: "mouseInGame", label: "Mouse in game", icon: "controller" },
  /* Rest mode (see RestService on the host): the screen off, the game frozen, and one press on the
     pad brings both back. Sleeping the PC itself is under Shortcuts, because whether the pad can
     wake it from THAT depends on the hardware. */
  { id: "rest",        label: "Rest",          icon: "moon",
    desc: "Screen off and the game paused — any button brings both back" },
];

/* Where the highlight sits before the stick has been pushed anywhere. Deliberately NOT spoke
   0: that one closes a window, and A on a menu you have only just opened should not be able
   to destroy something by default. Spoke 1 is Actions, the one most worth a single press. */
const RADIAL_HOME = 1;

const SHORTCUTS = [
  { id: "taskManager",     label: "Task Manager",     icon: "bars" },
  { id: "explorer",        label: "File Explorer",    icon: "folder" },
  { id: "settings",        label: "Windows Settings", icon: "gear" },
  { id: "displaySettings", label: "Display Settings", icon: "monitor" },
  { id: "volume",          label: "Volume Mixer",     icon: "volume" },
  // Distinct from Rest, which only blanks the screen: this hands the session to the Windows
  // lock screen, which the pad cannot drive at all.
  { id: "lock",            label: "Lock PC",          icon: "lock" },
  // Rest first (the game frozen, the pad parked), then Windows' own sleep. The host answers
  // this id itself rather than running a program; what can wake the PC afterwards is in
  // Settings → General → Rest and sleep.
  { id: "sleepPc",         label: "Sleep PC",         icon: "power" },
];

/** Close every transient menu so an overlay never stacks on a stale one. */
function closeAllMenus() {
  filterOpen = false; gameMenu = null; collectOpen = false;
  manageOpen = false; confirmState = null; guideOpen = false;
  ["overlay-filter", "overlay-gamemenu", "overlay-collect",
   "overlay-manage", "overlay-confirm", "overlay-guide"]
    .forEach(hideOverlay);
  // The Actions pickers too: a shortcut half-chosen under a Power Wheel is not a state.
  closeKeyPick();
  cancelCapture(null);
}

/* ============================== radial ============================== */

function openRadial(targetTitle) {
  radialOpen = true; radialIdx = RADIAL_HOME; radialSub = null;
  overlayTargetTitle = targetTitle || "";
  setOverlayMode(true);
  closeAllMenus();
  renderRadial();
  showOverlay("overlay-radial");
}

function closeRadial(refocus) {
  radialOpen = false; radialSub = null;
  hideOverlay("overlay-radial");
  hideOverlay("overlay-radialsub");
  setOverlayMode(false);
  send({ cmd: "closeOverlay", refocus: refocus !== false });
}

/* The stick-as-mouse is off inside a focused game by default, so games with native pad support
   never see phantom input. For the ones that have none it has to be switchable from where you
   are -- in the game -- rather than from a settings screen you cannot reach without leaving. */
function mouseInGameOn() { return !!(S.settings && S.settings.gamepadMouseDuringGame); }
/* Distinct icon from "Center mouse", which also uses the pointer, and it flips with the state so
   the spoke reads at a glance from across the room. */
function radialIcon(it) {
  if (it.id !== "mouseInGame") return it.icon;
  return mouseInGameOn() ? "controller" : "controllerOff";
}
function radialLabel(it) {
  if (it.id === "mouseInGame") return mouseInGameOn() ? "Mouse in game: On" : "Mouse in game: Off";
  // Opened over the running game, the window behind the menu IS the game, and the spoke should
  // say so: "Close window" over a game reads as something milder than it is.
  if (it.id === "close" && overlayTargetIsGame) return "Close game";
  // Opened over Loungepad, Loungepad is the window behind it. Quitting it was only at the end of
  // Settings → General, which is a long way to go for someone who starts it for one evening of
  // play over Moonlight (a user's report, Oct 7 2026).
  if (it.id === "close" && overlayOverLauncher) return "Exit Loungepad";
  return it.label;
}
function radialDesc(it) {
  if (it.id === "mouseInGame") return mouseInGameDesc();
  if (it.id === "close" && overlayTargetIsGame) return "Ask the game to quit and come back to the library";
  if (it.id === "close" && overlayOverLauncher) return "Close Loungepad. Start it again whenever you want it back";
  return it.desc || "";
}
function mouseInGameDesc() {
  return mouseInGameOn()
    ? "Stick keeps moving the pointer while a game is focused — press A to turn it off"
    : "Force the stick to move the pointer even while a game is focused";
}

function renderRadial() {
  const ring = $("radialRing");
  const R = 310, cx = 440, cy = 440;
  // Built once and updated in place from then on. The spokes carry the wheel's opening animation
  // (see .radial-item in app.css), and rebuilding them on every move would replay it each step.
  if (ring.children.length !== RADIAL_ITEMS.length) {
    ring.innerHTML = "";
    RADIAL_ITEMS.forEach((it, i) => {
      const ang = (-90 + i * (360 / RADIAL_ITEMS.length)) * Math.PI / 180;
      const el = document.createElement("div");
      el.style.left = (cx + R * Math.cos(ang)) + "px";
      el.style.top = (cy + R * Math.sin(ang)) + "px";
      // For the bloom: its turn in the sequence, and the way back to the centre it starts from.
      el.style.setProperty("--i", i);
      el.style.setProperty("--dx", (-R * 0.55 * Math.cos(ang)).toFixed(1) + "px");
      el.style.setProperty("--dy", (-R * 0.55 * Math.sin(ang)).toFixed(1) + "px");
      el.addEventListener("mouseenter", () => {
        if (hoverEnabled() && radialIdx !== i) { radialIdx = i; renderRadial(); }
      });
      el.addEventListener("click", () => { radialIdx = i; radialActivate(); });
      ring.appendChild(el);
    });
  }
  RADIAL_ITEMS.forEach((it, i) => {
    const el = ring.children[i];
    el.className = "radial-item"
      + (i === radialIdx && focusVisible() ? " focused" : "")
      + (it.danger ? " danger" : "");
    // Only the mouse-in-game spoke ever changes what it says; the rest are written once.
    const html = iconSvg(radialIcon(it)) + "<span>" + esc(radialLabel(it)) + "</span>";
    if (el.__html !== html) { el.innerHTML = html; el.__html = html; }
  });
  const sel = RADIAL_ITEMS[radialIdx];
  $("radialSelName").textContent = radialLabel(sel);
  $("radialDesc").textContent = radialDesc(sel);
  // Close is the only spoke that acts on the window behind the menu, so that is the only one
  // that needs to name it. Over Loungepad the label already does, and the title the host sends
  // is the last window the wheel acted on, not this one.
  $("radialTarget").textContent =
    sel.id !== "close" || overlayOverLauncher ? ""
    : overlayTargetTitle ? overlayTargetTitle.toUpperCase() : "NO WINDOW";
  $("radialFoot").innerHTML = foot(["A", "Select"], ["B", "Close"]);
}

function radialActivate() {
  const it = RADIAL_ITEMS[radialIdx];
  switch (it.id) {
    case "close":
      // Over the running game this is the in-game menu's Close game: the host thaws a paused game
      // first (a frozen process never reads a WM_CLOSE) and lands on the library itself, so the
      // overlay is dropped here the way the in-game menu drops it, without a closeOverlay that
      // would hand the foreground back to a game that is being asked to quit.
      if (overlayTargetIsGame) {
        radialOpen = false; radialSub = null;
        hideOverlay("overlay-radial");
        hideOverlay("overlay-radialsub");
        setOverlayMode(false);
        switchView("library");
        send({ cmd: "closeGame" });
      } else if (overlayOverLauncher) {
        // No confirm, like Exit in Settings: the spoke is never where the highlight starts.
        send({ cmd: "exitApp" });
      } else {
        send({ cmd: "windowAction", action: "close" });
        closeRadial(false);
      }
      break;
    case "keyboard":  send({ cmd: "toggleKeyboard" });                   closeRadial(true);  break;
    // Blanks the TV and parks the pad; any button brings it back, so it needs no confirm step.
    case "rest":      send({ cmd: "rest" });                             closeRadial(false); break;
    // Stays open so the flipped label is visible; the toast alone would be gone with the menu.
    case "mouseInGame":
      if (S.settings) S.settings.gamepadMouseDuringGame = !mouseInGameOn();
      send({ cmd: "mouseInGame", on: mouseInGameOn() });
      renderRadial();
      break;
    case "centerMouse":
      // host re-centres the pointer and closes the overlay; do not refocus the old window
      send({ cmd: "centerMouse" });
      radialOpen = false; radialSub = null;
      hideOverlay("overlay-radial");
      hideOverlay("overlay-radialsub");
      setOverlayMode(false);
      setInputMode("pointer");    // tells the host too, so its copy stays in step
      break;
    case "windows":   openRadialSub("windows"); break;
    case "shortcuts": openRadialSub("shortcuts"); break;
    case "actions":   openActionWheel(); break;
  }
}

function radialInput(btn) {
  const n = RADIAL_ITEMS.length;
  switch (btn) {
    case "Right": case "Down": radialIdx = (radialIdx + 1) % n; renderRadial(); break;
    case "Left":  case "Up":   radialIdx = (radialIdx - 1 + n) % n; renderRadial(); break;
    case "A": if (focusVisible()) radialActivate(); break;
    case "B": closeRadial(true); break;
  }
}

/* ---- submenus ---- */

function radialSubItems() {
  if (radialSub === "windows") {
    if (!hostWindows.length) return [{ label: "No open windows", icon: "info", action: () => {} }];
    return hostWindows.map(w => ({
      label: w.title, icon: "folder", sub: w.processName,
      thumb: w.thumb, thumbIsIcon: w.thumbIsIcon,
      // The host restores the window, drags it onto the TV and focuses it -- switching to a
      // window you cannot see would be pointless from the couch.
      action: () => { send({ cmd: "windowAction", action: "focus", handle: w.handle }); closeRadial(false); },
    }));
  }
  if (radialSub === "shortcuts") {
    return SHORTCUTS.map(s => ({
      label: s.label, icon: s.icon,
      action: () => { send({ cmd: "shortcut", id: s.id }); closeRadial(false); },
    }));
  }
  return [];
}

function openRadialSub(kind) {
  radialSub = kind; radialSubIdx = 0;
  // Shown before it is filled. renderMenu paints the highlight onto whichever row is focusable,
  // and nothing inside a hidden overlay is: rendering first left the first row unhighlighted
  // until something moved.
  showOverlay("overlay-radialsub");
  if (kind === "windows") send({ cmd: "listWindows" });
  renderRadialSub();
}

function renderRadialSub() {
  const items = radialSubItems();
  radialSubIdx = Math.max(0, Math.min(radialSubIdx, items.length - 1));
  $("radialSubTitle").textContent = radialSub === "windows" ? "SWITCH WINDOW" : "SHORTCUTS";
  renderMenu($("radialSubList"), $("radialSubFoot"), items, radialSubIdx,
    foot(["A", "Select"], ["B", "Back"]),
    (i) => { if (radialSubIdx !== i) { radialSubIdx = i; renderRadialSub(); } },
    (i) => items[i] && items[i].action());
}

function radialSubInput(btn) {
  const items = radialSubItems();
  switch (btn) {
    // Round at the ends, like every list (listMove in app.js).
    case "Up": if (items.length) { radialSubIdx = (radialSubIdx - 1 + items.length) % items.length; renderRadialSub(); } break;
    case "Down": if (items.length) { radialSubIdx = (radialSubIdx + 1) % items.length; renderRadialSub(); } break;
    case "A": if (focusVisible() && items[radialSubIdx]) items[radialSubIdx].action(); break;
    case "B": radialSub = null; hideOverlay("overlay-radialsub"); renderRadial(); break;
  }
}

/* ============================== in-game menu ============================== */

function openIngame() {
  ingameOpen = true; ingameIdx = 0;
  setOverlayMode(true);
  closeAllMenus();
  renderIngame();
  showOverlay("overlay-ingame");
}

function hideIngame() {
  ingameOpen = false;
  hideOverlay("overlay-ingame");
  setOverlayMode(false);
}

function ingameItems() {
  const g = gameById(S.runningGameId);
  const paused = !!S.gamePaused;
  return [
    // Going back to the game always means a running game: a frozen one is thawed on the way.
    { label: "Resume", icon: "play", desc: paused ? "Unpause the game and go back to it" : "Back to what you were playing",
      action: () => { hideIngame(); send({ cmd: "resumeGame" }); } },
    { label: "Home", icon: "home", desc: paused ? "Leave the game paused and open the library" : "Leave the game running and open the library",
      action: () => { hideIngame(); setOverlayMode(false); switchView("library"); send({ cmd: "goHome" }); } },
    /* PlayState's trick: the game's processes frozen where they stand, so a game with no pause
       of its own can be walked away from, and nothing is drawn or computed until you are back.
       The menu stays up so the tile flips and says which way it is. */
    { label: paused ? "Unpause" : "Pause game", icon: paused ? "play" : "pause",
      desc: paused ? "The game is frozen where it stands; unpause it and stay here"
                   : "Freeze the game where it stands, so it draws and computes nothing until you are back",
      action: () => send({ cmd: "pauseGame" }) },
    { label: "Power Wheel", icon: "apps", desc: "Switch windows, keyboard, rest",
      action: () => { hideIngame(); send({ cmd: "setRadialActive", active: true }); openRadial(g ? g.title : ""); } },
    // No confirm step: drop the overlay and land back on the library. Leaving the overlay up
    // over a closing game looks like nothing happened at all.
    { label: "Close game", icon: "x", danger: true, desc: "Ask the game to quit and return here",
      action: () => { hideIngame(); setOverlayMode(false); switchView("library"); send({ cmd: "closeGame" }); } },
  ];
}

function renderIngame() {
  const g = gameById(S.runningGameId);
  $("ingameTitle").textContent = (g ? g.title : "PLAYING").toUpperCase();

  const items = ingameItems();
  ingameIdx = Math.max(0, Math.min(ingameIdx, items.length - 1));
  $("ingameDesc").textContent = (items[ingameIdx] && items[ingameIdx].desc) || "";
  renderIngameStats();

  const row = $("ingameList");
  row.innerHTML = "";
  items.forEach((it, i) => {
    const el = document.createElement("div");
    el.className = "ingame-tile"
      + (i === ingameIdx && focusVisible() ? " focused" : "")
      + (it.danger ? " danger" : "");
    el.innerHTML = iconSvg(it.icon) + "<span>" + esc(it.label) + "</span>";
    el.addEventListener("mouseenter", () => {
      if (hoverEnabled() && ingameIdx !== i) { ingameIdx = i; renderIngame(); }
    });
    el.addEventListener("click", () => { ingameIdx = i; it.action(); });
    row.appendChild(el);
  });

  $("ingameFoot").innerHTML = foot(["A", "Select"], ["B", "Resume"]);
}

function ingameInput(btn) {
  const items = ingameItems();
  switch (btn) {
    // A row, so it reads left and right, and comes round at its ends like every list. Up/Down are
    // deliberately inert: on a row they would read as the highlight jumping for no reason.
    case "Left":  if (items.length) { ingameIdx = (ingameIdx - 1 + items.length) % items.length; renderIngame(); } break;
    case "Right": if (items.length) { ingameIdx = (ingameIdx + 1) % items.length; renderIngame(); } break;
    case "A": if (focusVisible() && items[ingameIdx]) items[ingameIdx].action(); break;
    case "B": hideIngame(); send({ cmd: "resumeGame" }); break;
  }
}
/** Host asked us to tear down any overlay menu (e.g. the combo was tapped while one was open). */
function dismissOverlays() {
  radialOpen = false; radialSub = null; ingameOpen = false; actionWheelOpen = false;
  ["overlay-radial", "overlay-radialsub", "overlay-actions", "overlay-ingame"]
    .forEach(hideOverlay);
  closeAllMenus();
  setOverlayMode(false);
}
