"use strict";

/*
 * The first-run setup: what a new install opens on, over the library, before anything else.
 *
 * A handful of steps, each a few of Settings' own rows -- the same builders, so a switch here is
 * the switch there -- with the scan that started on `ready` carrying on underneath. The rail on
 * the left says where you are and what has been found so far: the host reports the scan source by
 * source (scanProgress; see BeginSteps in UiBridge), and the library fills in behind the box as it
 * goes. The Look step moves the box aside, because the library behind it is the preview.
 *
 * Nothing waits and nothing is asked twice: every answer is saved as it is given (scheduleSave),
 * so quitting halfway loses nothing, and Skip leaves every default as it was. Finishing or
 * skipping stamps settings.onboardingVersion. A later setup with steps worth showing to existing
 * installs raises ONBOARDING_VERSION, and it shows again once. Settings → General → First-time
 * setup runs it again at any time.
 *
 * Two steps only appear when there is something for them: Playnite when it has a library on this
 * PC, Vortex when it is installed. The host's `onboarding` answer (ProbeFirstRunAsync) says which,
 * a moment after the setup opens -- while the welcome is still up.
 *
 * Loaded last. onboardState is declared in app.js, which reads it while it boots.
 */

const ONBOARDING_VERSION = 1;
const ONB_PIN_POLL_MS = 4000;

function needsOnboarding() {
  return !!S.settings && (S.settings.onboardingVersion || 0) < ONBOARDING_VERSION;
}

function onbInfo() { return (onboardState && onboardState.info) || {}; }

/* The steps in order, each shown only when `show` says so. The welcome and the last step are not
   counted in "STEP n OF m": they ask nothing. */
const ONB_STEPS = [
  { id: "welcome",   name: () => "Welcome",        show: () => true },
  { id: "tv",        name: () => "Your TV",        show: () => (S.displays || []).length > 1 },
  { id: "look",      name: () => "The look",       show: () => true },
  { id: "stores",    name: () => "Your stores",    show: () => true },
  { id: "emulation", name: () => "Emulators",      show: () => true },
  { id: "xbox",      name: () => guideTitle(),     show: () => true },
  { id: "signin",    name: () => "Signing in",     show: () => true },
  { id: "startup",   name: () => "Startup",        show: () => true },
  { id: "playnite",  name: () => "Playnite",       show: () => !!(onbInfo().playnite && onbInfo().playnite.found) },
  { id: "vortex",    name: () => "Vortex",         show: () => !!(onbInfo().vortex && onbInfo().vortex.installed) },
  { id: "done",      name: () => "All set",        show: () => true },
];

function onboardSteps() { return ONB_STEPS.filter(s => s.show()); }

/* What the pad in hand calls its middle button. */
function guideTitle() {
  return { xbox: "The Xbox button", playstation: "The PS button" }[padFamily] || "The Home button";
}

const PAD_FAMILY_NAMES = { xbox: "Xbox controller", playstation: "PlayStation controller", switch: "Switch controller", generic: "Controller" };

/* How to get back to Loungepad once it has stepped aside, in the words of the combo in force. */
function comeBackText() {
  const s = S.settings || {};
  const combo = s.minimizeCombo && s.minimizeCombo !== "Off" ? s.minimizeCombo : null;
  if (!combo) return "start Loungepad again to come back";
  return `${s.menuComboMode === "DoubleTap" ? "press" : "hold"} ${comboName(combo, padFamily)} to come back`;
}

/* ---- opening, moving, finishing ---- */

function openOnboarding(from) {
  onboardState = {
    step: "welcome", from: from || "library",
    // A second run keeps what the host said last time until it answers again.
    info: onboardState ? onboardState.info : null,
    vortex: null, playnite: null, pinTimer: null, entered: {},
  };
  send({ cmd: "onboardingProbe" });
  clearFocus($("screen-onboarding"));
  switchView("onboarding");
}

function finishOnboarding() {
  if (!onboardState) return;
  stopPinPoll();
  if (S.settings) {
    S.settings.onboardingVersion = Math.max(S.settings.onboardingVersion || 0, ONBOARDING_VERSION);
    // Now rather than in 350 ms: the next thing may well be a game, or closing the launcher.
    clearTimeout(saveTimer);
    send({ cmd: "saveSettings", settings: S.settings });
  }
  onboardState = null;
  switchView("library");
}

function askSkipOnboarding() {
  askConfirm({
    title: "Skip the setup?",
    body: "Whatever you have chosen so far is kept, and everything else stays at its default. The games Loungepad finds on its own still arrive. The setup is in Settings → General whenever you want it.",
    yesLabel: "Skip setup", icon: "x", danger: false,
    onYes: finishOnboarding,
  });
}

function onbGo(dir) {
  const steps = onboardSteps();
  const i = Math.max(0, steps.findIndex(s => s.id === onboardState.step));
  const j = i + dir;
  if (j < 0) return;
  if (j >= steps.length) { finishOnboarding(); return; }
  onbEnter(steps[j].id);
}

function onbEnter(id) {
  onboardState.step = id;
  const scope = $("screen-onboarding");
  clearFocus(scope);
  const rows = $("onbRows");
  stopScroll(rows, "y");
  rows.scrollTop = 0;
  if (id === "signin") startPinPoll(); else stopPinPoll();
  // Vortex is peeked at when the setup opens; a second look on arrival catches a Vortex started
  // since. Only a peek: connecting is the row's to do.
  if (id === "vortex" && !(onboardState.vortex && onboardState.vortex.busy)) send({ cmd: "onboardingVortex", mode: "peek" });
  onboardState.entered[id] = true;
  renderOnboarding();
  pulse($("onbRows"));
  pulse($("screen-onboarding").querySelector(".onb-head"));
}

function startPinPoll() {
  stopPinPoll();
  send({ cmd: "onboardingPin" });
  onboardState.pinTimer = setInterval(() => {
    if (view === "onboarding" && onboardState && onboardState.step === "signin" && document.visibilityState === "visible") send({ cmd: "onboardingPin" });
  }, ONB_PIN_POLL_MS);
}
function stopPinPoll() {
  if (onboardState && onboardState.pinTimer) { clearInterval(onboardState.pinTimer); onboardState.pinTimer = null; }
}

/* ---- the host's answers ---- */

function onOnboardingInfo(m) {
  if (!onboardState) return;
  onboardState.info = m;
  if (view === "onboarding") renderOnboarding();
}

function onOnboardingPin(m) {
  if (!onboardState) return;
  onboardState.info = { ...onbInfo(), pin: m.pin };
  if (view === "onboarding") renderOnboarding();
}

function onOnboardingVortex(m) {
  if (!onboardState) return;
  // A busy note keeps the last answer on screen under it rather than blanking the step.
  onboardState.vortex = m.busy ? { ...(onboardState.vortex || {}), busy: true, mode: m.mode } : m;
  if (m.vortex) onboardState.info = { ...onbInfo(), vortex: m.vortex };
  if (view === "onboarding") renderOnboarding();
}

function onOnboardingPlaynite(m) {
  if (!onboardState) return;
  if (m.state === "done" && m.outcome) onboardState.playnite = m.outcome;
  if (m.state === "undone") onboardState.playnite = null;
  renderOnboarding();
}

/* ---- the steps ----
   Each returns { title, lede, items, next }. `lede` may name buttons as [[A]]. Items are Settings
   rows, or one of: { section }, { note, tone }, { status: [...] }, { stats: [...] }, { keys: [[btn,
   label]] }, and { tiles: [...] } -- a row of big choices, each { key, title, sub, swatch, icon,
   chosen, pick }. */

/** Change a setting from a step: applied, saved, redrawn -- the Settings rows' own `set`. */
function onbSet(fn) { fn(); applyTheme(); scheduleSave(); renderOnboarding(); }

const ONB_PAGES = {
  welcome() {
    const connected = !!(lastBatteryMsg && lastBatteryMsg.present !== false) || S.padConnected;
    const name = S.padName || PAD_FAMILY_NAMES[padFamily] || "Controller";
    const pct = lastBatteryMsg && typeof lastBatteryMsg.percent === "number" ? ` · ${lastBatteryMsg.percent}%` : "";
    return {
      title: "Welcome to Loungepad",
      lede: "Every game on this PC, from every store, on the TV. A few questions and you are on the sofa. It takes a couple of minutes, and Loungepad is already looking for your games while you answer.",
      items: [
        { status: [connected
          ? { icon: "controller", name, value: "Connected" + pct, ok: true }
          : { icon: "controllerOff", name: "No controller yet", value: "Connect one now, or use the arrow keys and Enter" }] },
        { note: "Everything here can be changed later in Settings. [[X]] skips the rest of the setup." },
      ],
      next: "Get started",
    };
  },

  tv() {
    const s = S.settings;
    return {
      title: "Which screen is your TV?",
      lede: "Loungepad opens there, and games are moved onto it when they start. It moves as soon as you choose.",
      items: [
        { tiles: S.displays.map(d => ({
          key: "d:" + d.deviceName, icon: "monitor",
          title: d.friendlyName || d.deviceName,
          sub: `${d.width}×${d.height}${d.isPrimary ? " · primary" : ""} · Display ${d.deviceName.replace(/\D/g, "")}`,
          chosen: s.tvDeviceName === d.deviceName,
          pick: () => onbSet(() => { s.tvDeviceName = d.deviceName; }),
        })), cls: "wide" },
        toggleRow("Switch primary display on launch", "Games default to the primary display, so the TV becomes primary while a game runs",
          () => s.switchPrimaryOnLaunch, v => onbSet(() => s.switchPrimaryOnLaunch = v)),
      ],
    };
  },

  look() {
    const s = S.settings;
    const themes = S.themes && S.themes.length ? S.themes : [{ id: "", name: "Shelf" }];
    const about = (t) => t.description || (t.id === "" ? "The built-in look: recently played along the top, every game in a grid under it" : "");
    return {
      title: "Pick a look",
      lede: "The library beside this box is the preview: it changes as you choose. Both can be changed later, with more options, in Settings → Appearance.",
      items: [
        { section: "THEME" },
        { tiles: themes.map(t => ({
          key: "t:" + t.id, icon: t.id === "" ? "apps" : "image",
          title: t.name || t.id, sub: about(t),
          chosen: (s.theme || "") === t.id,
          pick: () => onbSet(() => { s.theme = t.id; }),
        })), cls: "wide" },
        { section: "ACCENT COLOUR" },
        // The app-wide accent, and the chosen theme's own taken off: a setup's answer should hold
        // under every theme, not only the one that happened to be showing when it was given.
        { tiles: ACCENTS.map(a => ({
          key: "a:" + a.hex, swatch: a.hex, title: a.name,
          chosen: lookAccent() === a.hex.toUpperCase(),
          pick: () => onbSet(() => {
            s.accentColor = a.hex;
            const bag = lookBag(false);
            if (bag) delete bag[LOOK_IDS.accent];
          }),
        })), cls: "swatches" },
      ],
    };
  },

  stores() {
    const s = S.settings;
    const L = onbInfo().launchers || null;
    const running = (id) => (S.scanProgress || []).some(x => x.id === id && (x.state === "running" || x.state === "pending"));
    const installed = (platform) => S.games.filter(g => g.platform === platform && g.installed).length;
    const line = (icon, name, has, platform, stepId) => {
      const n = installed(platform);
      const value = running(stepId) ? "Looking…" : n ? `${n} game${n === 1 ? "" : "s"} on this PC` : has ? "No games installed" : "Not installed";
      return { icon, name, value, ok: has === true || n > 0, dim: !has && !n };
    };
    const kb = btnName(s.keyboardToggleButton || "Back", padFamily);
    return {
      title: "Your stores",
      lede: "Games already installed are found on their own. Sign in to a store as well and the games you own there but have not installed join the library too, greyed out, and install from here.",
      items: [
        { status: [
          line("steam", "Steam", L ? L.steam : null, "Steam", "steam"),
          line("epic", "Epic Games Launcher", L ? L.epic : null, "Epic", "epic"),
          line("gog", "GOG Galaxy", L ? L.galaxy : null, "GOG", "gog"),
          line("xbox", "Xbox app", L ? L.xboxApp : null, "Xbox", "xbox"),
        ] },
        { section: "YOUR ACCOUNTS" },
        ...storeAccountRows(s, onbSet),
        { note: `A sign-in opens the store's own page in a window. The left stick moves the pointer, and ${kb} brings up the keyboard for typing.` },
      ],
    };
  },

  emulation() {
    const s = S.settings;
    const em = S.emulation || { emulators: [], romFolders: [] };
    const roms = S.games.filter(g => g.emulated).length;
    const looking = (S.scanProgress || []).some(x => (x.id === "emulators" || x.id === "roms") && (x.state === "running" || x.state === "pending"));
    const found = (em.emulators || []).length + (em.romFolders || []).length;
    return {
      title: "Emulators and ROMs",
      lede: found
        ? `Found ${(em.emulators || []).length} emulator${(em.emulators || []).length === 1 ? "" : "s"} and ${roms} game${roms === 1 ? "" : "s"} in ${(em.romFolders || []).length} ROM folder${(em.romFolders || []).length === 1 ? "" : "s"}. Check that each system runs with the emulator you want; anything you remove stays removed.`
        : looking ? "Looking in the usual places for emulators, and for ROMs in RetroArch's playlists and folders named after a system…"
        : "No emulators were found in the usual places. If you have some somewhere else, add them here, or skip this: it is under Settings → Library too.",
      items: emulationRows(s, onbSet),
    };
  },

  xbox() {
    const s = S.settings;
    const xb = S.xboxButton || {};
    const guideCombo = /\bGuide\b/.test(s.minimizeCombo || "");
    const tapHold = s.menuComboMode !== "DoubleTap";
    const combo = s.minimizeCombo && s.minimizeCombo !== "Off" ? `[[${s.minimizeCombo.split("+").map(p => p.trim()).join("]] + [[")}]]` : null;
    const lede = !combo ? "The menu combo is off, so nothing brings the Power Wheel up from inside a game. Pick one below."
      : (tapHold ? `Tap ${combo} for the Power Wheel, from anywhere, a game included. Hold it to show or hide Loungepad.`
                 : `Press ${combo} to show or hide Loungepad, from anywhere, a game included. Double-tap it for the Power Wheel.`)
        + (guideCombo ? " Windows and Steam listen for the same button. Turn them off here, so one press does one thing." : "");
    // Always listed, whatever the combo: even with the menu on other buttons, Steam or Game Bar
    // opening over a game on a stray press of the Xbox button is the thing this step is for.
    const items = [{ section: "WINDOWS AND STEAM" }];
    const others = xboxButtonRows();
    items.push(...others);
    if (!others.some(r => r.type === "toggle" && r.value) && !xb.steamBusy)
      items.push({ note: "Nothing else reacts to the Xbox button now.", tone: "ok" });
    items.push({ section: "THE COMBO" });
    items.push(...menuComboRows(s, onbSet, "above"));
    if (combo) items.push({ note: tapHold ? `Try it: tap ${combo} to open the Power Wheel, and tap again to close it.` : `Try it: double-tap ${combo} to open the Power Wheel.` });
    return { title: guideTitle(), lede, items };
  },

  signin() {
    const pin = onbInfo().pin;
    const status = pin === true ? { icon: "lock", name: "Windows Hello PIN", value: "Set up", ok: true }
      : pin === false ? { icon: "lock", name: "Windows Hello PIN", value: "Not set up on this account" }
      : { icon: "lock", name: "Windows Hello PIN", value: onboardState.info ? "Windows did not say" : "Checking…" };
    return {
      title: "Sign in with the controller",
      lede: "After a restart or a wake, Windows asks you to sign in before Loungepad can come up. A password needs a keyboard; a PIN can be typed with the controller.",
      items: [
        { status: [status] },
        {
          name: pin === true ? "Change your PIN" : "Set up a PIN",
          hint: pin === true
            ? "This account already signs in with a PIN. Opens Windows Settings at Sign-in options, on the TV, to change it"
            : "Opens Windows Settings at Sign-in options, on the TV. Choose PIN (Windows Hello), then Set up. Windows asks for your account's password once to do it",
          type: "action", label: "Open",
          action: () => askConfirm({
            title: pin === true ? "Open sign-in options?" : "Set up a PIN?",
            body: `Windows Settings opens at Sign-in options. Choose PIN (Windows Hello), then ${pin === true ? "Change PIN" : "Set up"}. The left stick is the pointer and ${btnName(S.settings.keyboardToggleButton || "Back", padFamily)} brings up the keyboard. Loungepad steps aside while you do; ${comeBackText()}.`,
            yesLabel: "Open Settings", icon: "lock", danger: false,
            onYes: () => send({ cmd: "onboardingSignInOptions" }),
          }),
        },
        {
          name: "More couch tips", hint: "Signing in without a PIN at all, waking the PC with the controller, starting Loungepad on its own",
          type: "action", label: "Open guide",
          action: () => { guideOpen = true; showOverlay("overlay-guide"); },
        },
      ],
    };
  },

  startup() {
    const s = S.settings;
    return {
      title: "When the PC starts",
      lede: "Loungepad can be the first thing on the TV after a restart or a wake, and keep itself up to date.",
      items: [
        toggleRow("Launch Loungepad at login", "Recommended. The TV lands straight on the library, with the controller working, after every restart",
          () => !!s.launchOnStartup, v => onbSet(() => s.launchOnStartup = v)),
        toggleRow("Update automatically",
          "Downloads new versions in the background and installs them the next time Loungepad starts, so an update never interrupts a game",
          () => s.autoUpdate !== false, v => onbSet(() => s.autoUpdate = v)),
      ],
    };
  },

  playnite() {
    const p = onbInfo().playnite || {};
    const done = onboardState.playnite;
    const items = [];
    if (done) items.push({ note: done.total > 0 ? `Imported ${playniteOutcomeText(done)}.` : "Nothing was left to bring.", tone: "ok" });
    items.push(S.scanning
      ? { name: "Review and import", hint: "Waiting for the library scan to finish, so Playnite's games can be matched to what is here", type: "action", label: "Scanning…", muted: true, action: () => {} }
      : { name: done ? "Review the import again" : "Review and import",
          hint: "Shows what each part would bring, and imports only the parts you leave on", type: "action", label: "Open",
          action: () => openPlayniteImport() });
    return {
      title: "Your Playnite library",
      lede: `Playnite is on this PC${p.dir ? ` (${p.dir})` : ""}. Its playtime, favourites, categories, games added by hand and play sessions can come over. Only what Loungepad has nothing for is filled in, nothing here is overwritten, and the import can be undone.`,
      items,
    };
  },

  vortex() {
    const base = onbInfo().vortex || {};
    const v = onboardState.vortex || {};
    const st = v.vortex || base;
    const items = [];
    items.push({ status: [{ icon: "download", name: "Vortex", value: [st.version ? "v" + st.version : null, st.running ? "running" : "not running"].filter(Boolean).join(" · "), ok: !!st.bridgeReady }] });
    if (v.busy) {
      items.push({ note: v.mode === "restart" ? "Restarting Vortex. It comes back minimized in a few seconds…"
        : v.mode === "connect" ? "Connecting to Vortex. A Vortex that was not running starts minimized, which takes a moment the first time…"
        : "Asking Vortex…" });
    } else if (st.bridgeReady) {
      const games = v.games || [];
      items.push({ note: games.length ? "Connected. Each game's mods are under its Options → Mods." : "Connected. Vortex is not set up for any of your games yet: a game's Options → Mods does that.", tone: "ok" });
      if (games.length) {
        items.push({ section: "MANAGED IN VORTEX" });
        items.push({ status: games.slice(0, 8).map(g => ({
          icon: "cartridge", name: g.title,
          value: g.mods ? `${g.mods} mod${g.mods === 1 ? "" : "s"} · ${g.enabled} on` : "No mods yet",
          dim: !g.gameId,
        })) });
        if (games.length > 8) items.push({ note: `And ${games.length - 8} more.` });
      }
    } else if (st.needsRestart) {
      items.push({
        name: "Restart Vortex", type: "action", label: "Restart",
        hint: "Vortex loads extensions when it starts, and it was already open. It closes and opens again minimized; nothing about your mods changes",
        action: () => askConfirm({
          title: "Restart Vortex?",
          body: "Vortex closes and opens again minimized, with Loungepad's extension loaded. Anything it is downloading picks up again after.",
          yesLabel: "Restart Vortex", icon: "refresh", danger: false,
          onYes: () => send({ cmd: "onboardingVortex", mode: "restart" }),
        }),
      });
    } else {
      items.push({
        name: "Connect Loungepad to Vortex", type: "action", label: "Connect",
        hint: "Puts Loungepad's extension in Vortex's plugins folder and, if Vortex is not running, starts it minimized. Nothing about your mods changes",
        action: () => send({ cmd: "onboardingVortex", mode: "connect" }),
      });
    }
    const error = v.error || (!v.busy && !st.bridgeReady ? st.error : null);
    if (error) items.push({ note: error, tone: "warn" });
    return {
      title: "Mods, through Vortex",
      lede: "Vortex, Nexus Mods' manager, is on this PC. Loungepad can list each game's mods, switch them on and off and fetch new ones from the couch, by talking to Vortex through a small extension of its own.",
      items,
    };
  },

  done() {
    const games = visibleGames();
    const installed = games.filter(g => g.installed && !g.emulated).length;
    const owned = games.filter(g => !g.installed).length;
    const roms = games.filter(g => g.emulated).length;
    const stores = new Set(games.filter(g => !g.emulated && g.platform !== "Manual").map(g => g.platform)).size;
    const stats = [{ n: installed, label: installed === 1 ? "game installed" : "games installed" }];
    if (owned) stats.push({ n: owned, label: "more you own" });
    if (roms) stats.push({ n: roms, label: roms === 1 ? "ROM" : "ROMs" });
    stats.push({ n: stores, label: stores === 1 ? "store" : "stores" });
    const busy = S.scanning || (S.scanProgress || []).some(x => x.state === "running" || x.state === "pending");
    return {
      title: "You're all set",
      lede: busy
        ? "This is what Loungepad has found so far. The rest is still arriving, along with artwork and details; the library fills in as it does."
        : "This is what Loungepad found. Artwork and details keep arriving in the background, and the library fills in as they do.",
      items: [
        { stats },
        { section: "ON THE LIBRARY" },
        { keys: [["Y", "A game's options"], ["X", "Filter and sort"], ["View", "Search"], ["LB", "Stats"], ["Menu", "Settings"],
          ...(/\bGuide\b/.test(S.settings.minimizeCombo || "") ? [["Guide", "The Power Wheel, anywhere"]] : [])] },
      ],
      next: "Start playing",
    };
  },
};

/* ---- drawing ---- */

function renderOnboarding() {
  if (!onboardState || !S.settings || view !== "onboarding") return;
  const steps = onboardSteps();
  if (!steps.some(s => s.id === onboardState.step)) onboardState.step = steps[0].id;
  const idx = steps.findIndex(s => s.id === onboardState.step);
  const page = ONB_PAGES[onboardState.step]();
  onboardState.page = page;
  document.body.dataset.onbStep = onboardState.step;

  renderOnbRail(steps, idx);
  const counted = steps.length - 2;
  $("onbKicker").textContent = idx === 0 ? "FIRST-TIME SETUP" : idx === steps.length - 1 ? "DONE" : `STEP ${idx} OF ${counted}`;
  $("onbTitle").textContent = page.title;
  $("onbLede").innerHTML = hintHtml(page.lede || "");
  renderOnbItems(page.items || []);
  $("onbNextLabel").textContent = page.next || "Continue";

  // A step lands on its first choice, or on Continue when it has none. A step whose rows arrive a
  // moment later (Vortex answering) moves there once they do -- unless the highlight has been
  // moved by hand since, which clears `autoFocus`.
  const scope = $("screen-onboarding");
  const cur0 = focusEl(scope);
  if (!cur0 || (onboardState.autoFocus && cur0.id === "onbNext")) {
    const rows = $("onbRows");
    const first = rows.querySelector(".onb-tile.chosen[data-focusable]") || rows.querySelector("[data-focusable]");
    const bare = onboardState.step === "welcome" || onboardState.step === "done" || !first;
    if (!cur0 || !bare) setFocusEl(bare ? $("onbNext") : first);
    onboardState.autoFocus = bare && onboardState.step !== "welcome" && onboardState.step !== "done";
  }
  paintNav();
  renderOnbLegend();
  const cur = focusEl(scope);
  if (cur && focusVisible()) revealFocus(cur);
}

function renderOnbLegend() {
  if (!onboardState) return;
  const el = focusEl($("screen-onboarding"));
  const it = el && el.__onbItem;
  const page = onboardState.page || {};
  const aLabel = !el || el.id === "onbNext" ? (page.next || $("onbNextLabel").textContent)
    : it && it.tile ? "Choose"
    : it && it.choices ? "Change" : it && it.type === "toggle" ? "Switch" : "Select";
  const first = onboardState.step === "welcome", last = onboardState.step === "done";
  const pairs = [["A", aLabel]];
  if (!first) pairs.push(["B", "Back"]);
  if (!first && !last) pairs.push(["Menu", "Next"]);
  if (!last) pairs.push(["X", "Skip setup"]);
  const html = foot(...pairs);
  const f = $("onbFoot");
  if (f.__html !== html) { f.innerHTML = html; f.__html = html; }
}

function renderOnbRail(steps, idx) {
  const html = steps.map((s, i) => {
    const cls = i < idx ? "done" : i === idx ? "current" : "";
    const dot = i < idx ? "✓" : i === steps.length - 1 ? "★" : String(i === 0 ? "" : i);
    return `<div class="onb-step ${cls}"><span class="onb-dot">${dot}</span><span>${esc(s.name())}</span></div>`;
  }).join("");
  const el = $("onbSteps");
  if (el.__html !== html) { el.innerHTML = html; el.__html = html; }
  renderOnbBackground();
}

/* The scan's steps as the rail lists them: a store's owned library is folded into its line, and
   anything switched off is left out. */
function onbScanLines() {
  const by = Object.fromEntries((S.scanProgress || []).map(x => [x.id, x]));
  const on = (id) => by[id] && by[id].state !== "off";
  const merge = (...ids) => {
    const xs = ids.map(i => by[i]).filter(x => x && x.state !== "off");
    if (!xs.length) return null;
    const state = xs.some(x => x.state === "running") ? "running"
      : xs.some(x => x.state === "pending") ? (xs.some(x => x.state === "done") ? "running" : "pending")
      : xs.some(x => x.state === "failed") && !xs.some(x => x.state === "done") ? "failed" : "done";
    return { state, parts: xs };
  };
  const count = (x) => (x && typeof x.count === "number" ? x.count : null);
  const store = (label, inst, own) => {
    const m = merge(inst, own);
    if (!m) return null;
    const n = count(by[inst]), o = count(by[own]);
    const bits = [];
    if (n !== null) bits.push(n ? `${n} installed` : "none installed");
    if (o) bits.push(`+${o} owned`);
    return { label, state: m.state, value: bits.join(" · ") };
  };
  const lines = [];
  if (on("emulators")) lines.push({ label: "Emulators", state: by.emulators.state, value: count(by.emulators) !== null ? String(count(by.emulators)) : "" });
  lines.push(store("Steam", "steam", "steamOwned"), store("Epic Games", "epic", "epicOwned"), store("GOG", "gog", "gogOwned"), store("Xbox", "xbox", "xboxOwned"));
  if (on("roms")) lines.push({ label: "ROMs", state: by.roms.state, value: count(by.roms) !== null ? String(count(by.roms)) : "" });
  if (on("gamePass")) lines.push({ label: "Game Pass", state: by.gamePass.state, value: count(by.gamePass) !== null ? `${count(by.gamePass)} games` : "" });
  if (on("metadata")) {
    const total = S.games.length, fetched = S.games.filter(g => g.metadataFetched).length;
    lines.push({ label: "Artwork and details", state: by.metadata.state, value: by.metadata.state === "done" ? "" : total ? `${fetched} of ${total}` : "" });
  }
  if (on("achievements")) lines.push({ label: "Achievements", state: by.achievements.state, value: "" });
  return lines.filter(Boolean);
}

function renderOnbBackground() {
  const lines = onbScanLines();
  const busy = lines.some(l => l.state === "running" || l.state === "pending");
  const mark = (st) => st === "done" ? `<span class="onb-bg-mark ok">✓</span>`
    : st === "failed" ? `<span class="onb-bg-mark warn">!</span>`
    : st === "running" ? `<span class="onb-bg-mark spin"></span>`
    : `<span class="onb-bg-mark"></span>`;
  const html = !lines.length ? "" :
    `<div class="onb-bg-title mono">${busy ? "FINDING YOUR GAMES" : "FOUND"}</div>` +
    lines.map(l => `<div class="onb-bg-row ${l.state}">${mark(l.state)}<span class="onb-bg-name">${esc(l.label)}</span><span class="onb-bg-val mono">${esc(l.value || "")}</span></div>`).join("");
  const el = $("onbBg");
  if (el.__html !== html) { el.innerHTML = html; el.__html = html; }
}

/* The step's items, updated in place like Settings' rows and rebuilt only when their shape
   changes -- a new step, a toggle revealing a row -- so a highlight move never snaps the list. */
function renderOnbItems(items) {
  const list = $("onbRows");
  const step = onboardState.step;
  const kind = (it) => it.section ? "s" : it.note !== undefined ? "n" : it.status ? "st" + it.status.length
    : it.stats ? "k" + it.stats.length : it.keys ? "b" : it.tiles ? "t" + it.tiles.map(t => t.key).join(",") : "r";
  const shape = step + "|" + items.map(kind).join("|");
  if (list.__shape !== shape) {
    const keepTop = list.scrollTop;
    list.innerHTML = "";
    let ri = 0;
    items.forEach(it => {
      if (it.tiles) {
        const g = document.createElement("div");
        g.className = "onb-tiles" + (it.cls ? " " + it.cls : "");
        it.tiles.forEach(t => g.appendChild(onbFocusable("onb-tile", `onb:${step}:${t.key}`)));
        list.appendChild(g);
      } else if (it.section || it.note !== undefined || it.status || it.stats || it.keys) {
        const d = document.createElement("div");
        list.appendChild(d);
      } else {
        list.appendChild(onbFocusable("set-row", `onb:${step}:r${ri++}`));
      }
    });
    list.scrollTop = keepTop;
    list.__shape = shape;
  }
  items.forEach((it, i) => {
    const el = list.children[i];
    if (it.tiles) {
      it.tiles.forEach((t, j) => {
        const tile = el.children[j];
        tile.__onbItem = { tile: t };
        tile.classList.toggle("chosen", !!t.chosen);
        const lead = t.swatch ? `<span class="onb-swatch" style="background:${esc(t.swatch)}"></span>` : t.icon ? `<span class="onb-tile-icon">${iconSvg(t.icon)}</span>` : "";
        const html = `${lead}<span class="onb-tile-text"><span class="onb-tile-title">${esc(t.title)}</span>${t.sub ? `<span class="onb-tile-sub">${esc(t.sub)}</span>` : ""}</span><span class="onb-tile-check">✓</span>`;
        if (tile.__html !== html) { tile.innerHTML = html; tile.__html = html; }
      });
      return;
    }
    let html, cls;
    if (it.section) { cls = "set-section"; html = esc(it.section); }
    else if (it.note !== undefined) { cls = "onb-note" + (it.tone ? " " + it.tone : ""); html = hintHtml(it.note); }
    else if (it.status) {
      cls = "onb-status";
      html = it.status.map(x => `<div class="onb-status-row${x.ok ? " ok" : ""}${x.dim ? " dim" : ""}">${x.icon ? `<span class="onb-status-icon">${iconSvg(x.icon)}</span>` : ""}<span class="onb-status-name">${esc(x.name)}</span><span class="onb-status-val">${esc(x.value || "")}</span></div>`).join("");
    } else if (it.stats) {
      cls = "onb-stats";
      html = it.stats.map(x => `<div class="onb-stat"><span class="onb-stat-n">${esc(String(x.n))}</span><span class="onb-stat-label">${esc(x.label)}</span></div>`).join("");
    } else if (it.keys) {
      // Drawn for the pad in hand, like every legend: the slots repaint when it changes.
      cls = "onb-keys";
      // These are the library's buttons, so a keyboard sees the library's keys (Ctrl, Tab, `).
      html = it.keys.map(([b, label]) => `<div class="onb-key">${slot(b, false, LIBRARY_KEYCAPS[b])}<span>${esc(label)}</span></div>`).join("");
    } else {
      el.__onbItem = it;
      el.classList.toggle("muted", !!it.muted);
      html = settingsRowHtml(it, true);
      if (el.__html !== html) { el.innerHTML = html; el.__html = html; }
      return;
    }
    if (el.className !== cls) el.className = cls;
    if (el.__html !== html) { el.innerHTML = html; el.__html = html; }
  });
  watchOverflow(list);
}

/** A focusable made once per shape. Its handlers read the item off the element, which every
    render refreshes, so they stay right while the content is updated under them. */
function onbFocusable(cls, key) {
  const el = document.createElement("div");
  el.className = cls;
  el.dataset.focusable = "";
  el.dataset.focusKey = key;
  el.addEventListener("mouseenter", () => {
    if (!hoverEnabled() || view !== "onboarding") return;
    onboardState.autoFocus = false;
    setFocusEl(el);
    paintNav();
    renderOnbLegend();
  });
  el.addEventListener("click", () => {
    if (view !== "onboarding") return;
    onboardState.autoFocus = false;
    setFocusEl(el);
    paintNav();
    onbActivate(el);
  });
  return el;
}

$("onbNext").addEventListener("click", () => { if (view === "onboarding" && onboardState) onbGo(1); });
$("onbNext").addEventListener("mouseenter", () => {
  if (!hoverEnabled() || view !== "onboarding") return;
  setFocusEl($("onbNext"));
  paintNav();
  renderOnbLegend();
});

function onbActivate(el) {
  if (!el || !onboardState) return;
  if (el.id === "onbNext") { onbGo(1); return; }
  const it = el.__onbItem;
  if (!it) return;
  if (it.tile) { it.tile.pick(); return; }
  if (it.muted) return;
  activateSettingRow(it);
}

/* ---- the pad ---- */

function onboardingInput(btn) {
  if (!onboardState) return;
  const scope = $("screen-onboarding");
  switch (btn) {
    case "Up": case "Down": case "Left": case "Right":
      onboardState.autoFocus = false;
      if (listMove(btn)) renderOnbLegend();
      break;
    case "A":
      // The welcome asks nothing: any A starts, wherever the highlight is.
      if (onboardState.step === "welcome") { onbGo(1); break; }
      if (!focusVisible()) break;
      onbActivate(focusEl(scope) || $("onbNext"));
      break;
    case "Menu": onbGo(1); break;
    case "B": onbGo(-1); break;
    case "X": if (onboardState.step !== "done") askSkipOnboarding(); break;
  }
}
