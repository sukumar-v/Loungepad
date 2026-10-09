"use strict";

/*
 * Add-ons: community themes and extensions (docs/ADDONS.md).
 *
 * Three pieces, all fed by S.addons (the host's list: every theme and extension installed or
 * listed by the add-ons repository, with where the list came from and what the extension runtime
 * says about each -- see AddonsPayload on the host):
 *
 *   1. Settings → Add-ons. Two tabs, Themes and Extensions (LB / RB, or a click), each a grid of
 *      tiles like the Actions category: what is installed, then what is available. A on a tile
 *      opens the add-on's own page in the same pane -- its facts, the rows that act on it, its
 *      settings -- and B comes back; Y on a tile is the short menu with the same actions.
 *
 *   2. What an extension stored on a game, drawn under the facts on the game's page
 *      (renderDetailExtFacts) from the labels its manifest contributes, and handed to the theme
 *      templates as {{ext.<id>.<key>}} (extView).
 *
 *   3. The install flows: from the repository (a confirm that names what an extension may reach),
 *      or from a zip or a folder through the host's file dialogs.
 *
 * Loaded after actions.js and shares app.js's globals. addonsUi is declared in app.js.
 */

const ADDON_KINDS = [
  { id: "theme", label: "Themes" },
  { id: "extension", label: "Extensions" },
];

/* ---- the data ---- */

function addonsData() { return S.addons || { items: [], catalogue: null, launcher: "", passRunning: false }; }
function addonItems(kind) { return (addonsData().items || []).filter(a => a.kind === kind); }
function addonByKey(key) { return (addonsData().items || []).find(a => a.key === key) || null; }
function addonKindLabel(kind) { return (ADDON_KINDS.find(k => k.id === kind) || ADDON_KINDS[0]).label; }
function addonInUse(a) { return a.kind === "theme" && a.installed && (S.settings ? (S.settings.theme || "") : "") === a.id; }
/* Enabled, installed extensions in the order the host lists them: the ones whose facts the page draws. */
function activeExtensions() { return addonItems("extension").filter(a => a.installed && a.enabled && !a.error); }

/* ---- Settings → Add-ons ---- */

function addonsTabReset() { addonsUi = { level: "grid", kind: addonsUi.kind || "theme", key: null, sort: addonsUi.sort || "az" }; }

/* ---- sorting, and the counts ----
   A to Z, or by what the repository's users did: the most downloaded, the most liked. An add-on
   with no counts (a bundled theme, something installed from a file) sorts as zero, then by name. */
const ADDON_SORTS = [
  { id: "az", label: "A to Z", icon: "sortAsc" },
  { id: "downloads", label: "Most downloaded", icon: "download" },
  { id: "likes", label: "Most liked", icon: "heart" },
];
function addonSortLabel() { return (ADDON_SORTS.find(s => s.id === (addonsUi.sort || "az")) || ADDON_SORTS[0]).label; }
function sortAddons(list) {
  const by = addonsUi.sort || "az";
  const name = (a, b) => String(a.name || "").localeCompare(String(b.name || ""), undefined, { sensitivity: "base" });
  if (by === "az") return [...list].sort(name);
  return [...list].sort((a, b) => ((b[by] || 0) - (a[by] || 0)) || name(a, b));
}
function openAddonSort() {
  openChoice("Sort add-ons", ADDON_SORTS.map(s => ({
    label: s.label, icon: s.icon, checked: s.id === (addonsUi.sort || "az"), radio: true,
    action: () => { addonsUi.sort = s.id; settingsIdx = 0; renderSettings(); pulse($("settingsScroll")); },
  })));
}

/* 1234 → "1.2k": the tile has room for a few characters. */
function fmtCount(n) {
  n = Math.max(0, Number(n) || 0);
  if (n < 1000) return String(n);
  if (n < 10000) return (Math.floor(n / 100) / 10).toString().replace(/\.0$/, "") + "k";
  if (n < 1000000) return Math.floor(n / 1000) + "k";
  return (Math.floor(n / 100000) / 10).toString().replace(/\.0$/, "") + "M";
}
function hasCounts(a) { return typeof a.likes === "number" || typeof a.downloads === "number"; }
/* The heart and the download arrow with their numbers, for a tile and for the add-on's page. */
function addonCountsHtml(a) {
  if (!hasCounts(a)) return "";
  return `<span class="addon-count${a.liked ? " liked" : ""}">${iconSvg("heart")}<b>${fmtCount(a.likes)}</b></span>` +
    `<span class="addon-count">${iconSvg("download")}<b>${fmtCount(a.downloads)}</b></span>`;
}

/* Like it, or take the like back. The heart moves at once; the host keeps the answer on this PC
   and sends the service only +1 or -1, which it counts at most once a day per address. */
function toggleAddonLike(a) {
  if (!a || !a.available) { toast("Only add-ons from the repository can be liked"); return; }
  const on = !a.liked;
  a.liked = on;
  if (typeof a.likes === "number") a.likes = Math.max(0, a.likes + (on ? 1 : -1));
  send({ cmd: "addonLike", key: a.key, on });
  renderSettings();
}
function addonsGridMode() { return settingsTab === "addons" && addonsUi.level === "grid"; }
function addonsLevelTag() { return `|${addonsUi.level}:${addonsUi.kind}:${addonsUi.key}`; }

/* The rows the generic settings renderer draws, by level: the grid (tiles under a tab strip and
   two headings) or one add-on's page (ordinary rows). */
function addonsSettingsRows() {
  if (addonsUi.level === "addon") {
    const a = addonByKey(addonsUi.key);
    if (a) return addonPageRows(a);
    addonsTabReset();
  }
  const kind = addonsUi.kind;
  const items = addonItems(kind);
  const installed = sortAddons(items.filter(a => a.installed));
  const available = sortAddons(items.filter(a => !a.installed));
  const rows = [];
  // A header is a section to everything that counts rows (it is not focusable), drawn by its
  // own builder rather than as a heading.
  rows.push({ section: true, header: true, build: addonsHeaderEl });
  rows.push({ section: "INSTALLED", cat: "addons" });
  installed.forEach(a => rows.push({ tile: true, addon: a, action: () => enterAddon(a.key) }));
  rows.push({ tile: true, add: true, kind, action: () => startAddonFileInstall(kind) });
  rows.push({ section: availableHeading(available), cat: "addons" });
  available.forEach(a => rows.push({ tile: true, addon: a, action: () => enterAddon(a.key) }));
  rows.push({ tile: true, refresh: true, action: () => refreshAddons() });
  return rows;
}

/* The Available heading carries the catalogue's state, because an empty list and a broken fetch
   look identical: nothing to install is a fact, the repository being out of reach is a problem. */
function availableHeading(available) {
  const c = addonsData().catalogue;
  if (c && c.busy) return "AVAILABLE · CHECKING…";
  if (c && c.error && !available.length) return "AVAILABLE · " + c.error.toUpperCase();
  if (!available.length) return c && c.fetchedAt ? "AVAILABLE · NOTHING MORE FOR NOW" : "AVAILABLE";
  return "AVAILABLE";
}

function enterAddon(key) {
  const a = addonByKey(key);
  if (!a) return;
  addonsUi = { level: "addon", kind: a.kind, key, sort: addonsUi.sort };
  settingsPane = "rows";
  settingsIdx = 0;
  renderSettings();
  pulse($("settingsScroll"));
}

/* B inside the category: back from an add-on's page to its tile. False at the grid, where B means
   the categories. */
function addonsBack() {
  if (addonsUi.level !== "addon") return false;
  const key = addonsUi.key;
  addonsTabReset();
  settingsPane = "rows";
  const tiles = addonsSettingsRows().filter(r => !r.section);
  const i = tiles.findIndex(r => r.addon && r.addon.key === key);
  settingsIdx = Math.max(0, i);
  renderSettings();
  pulse($("settingsScroll"));
  return true;
}

function addonsSetKind(kind) {
  if (!ADDON_KINDS.some(k => k.id === kind) || addonsUi.kind === kind) return;
  addonsUi = { level: "grid", kind, key: null, sort: addonsUi.sort };
  settingsPane = "rows";
  settingsIdx = 0;
  renderSettings();
  pulse($("settingsScroll"));
}

function addonsSwitchKind(dir) {
  const i = ADDON_KINDS.findIndex(k => k.id === addonsUi.kind);
  addonsSetKind(ADDON_KINDS[(i + dir + ADDON_KINDS.length) % ADDON_KINDS.length].id);
}

/* The tab strip above the grid. Not focusable: LB and RB turn it, as the action wheel's pages,
   and a click does too. The count is what is installed. */
function addonsHeaderEl() {
  const el = document.createElement("div");
  el.className = "addons-tabs";
  ADDON_KINDS.forEach(k => {
    const tab = document.createElement("div");
    tab.className = "addon-tab" + (k.id === addonsUi.kind ? " active" : "");
    tab.dataset.kind = k.id;
    const n = addonItems(k.id).filter(a => a.installed).length;
    tab.innerHTML = `<span>${esc(k.label)}</span><span class="addon-tab-count">${n}</span>`;
    tab.addEventListener("click", () => addonsSetKind(k.id));
    el.appendChild(tab);
  });
  const hint = document.createElement("div");
  hint.className = "addons-tabs-hint";
  hint.innerHTML = `${slot("LB")}${slot("RB")}`;
  el.appendChild(hint);
  return el;
}

/* ---- the tiles ---- */

function addonTileHtml(row) {
  if (row.add) {
    return `<div class="app-icon app-add">${iconSvg("plus")}</div><div class="app-name">Install from a file</div><div class="app-sub">A zip, or a folder on this PC</div>`;
  }
  if (row.refresh) {
    const c = addonsData().catalogue;
    const sub = c && c.busy ? "Checking…" : c && c.fetchedAt ? `Checked ${fmtAgo(c.fetchedAt)}` : "Read the repository's list";
    return `<div class="app-icon app-glyph">${iconSvg("refresh")}</div><div class="app-name">Refresh</div><div class="app-sub">${esc(sub)}</div>`;
  }
  const a = row.addon;
  const icon = a.icon ? `<div class="app-icon addon-icon" style="background-image:url('${esc(a.icon)}')"></div>`
    : `<div class="app-icon app-mono"><span>${esc((a.name || "?").trim().charAt(0).toUpperCase())}</span></div>`;
  const badge = a.update && a.installable ? `<span class="addon-badge">Update</span>`
    : addonInUse(a) ? `<span class="addon-badge quiet">In use</span>` : "";
  const sub = addonTileSub(a);
  const counts = hasCounts(a) ? `<div class="addon-counts">${addonCountsHtml(a)}</div>` : "";
  return `${icon}${badge}<div class="app-name">${esc(a.name)}</div><div class="app-sub${sub.accent ? " accent" : ""}${sub.muted ? " muted" : ""}">${esc(sub.text)}</div>${counts}`;
}

/* The line under the name: what state the add-on is in, in a few words. */
function addonTileSub(a) {
  if (a.busy === "downloading") return { text: `Downloading ${a.progress || 0}%`, accent: true };
  if (a.busy === "installing") return { text: "Installing…", accent: true };
  if (!a.installed) {
    if (a.needsLauncher) return { text: `Needs Loungepad ${a.needsLauncher}`, muted: true };
    return { text: [a.available ? "v" + a.available : null, a.author ? "by " + a.author : null].filter(Boolean).join(" · ") || "Available" };
  }
  if (a.error) return { text: "Cannot load", accent: true };
  const v = a.version ? ` · v${a.version}` : "";
  if (a.update && a.installable) return { text: `Update to ${a.available}`, accent: true };
  if (a.kind === "theme") return { text: (addonInUse(a) ? "In use" : a.bundled ? "Built in" : "Installed") + v };
  if (!a.enabled) return { text: "Off" + v, muted: true };
  const st = a.status && a.status.state;
  if (st === "error") return { text: "Error" + v, accent: true };
  if (st === "running") return { text: "Running" + v };
  return { text: (st === "starting" ? "Starting" : "Installed") + v };
}

function fmtAgo(iso) {
  const ms = Date.now() - new Date(iso).getTime();
  if (!(ms >= 0)) return "just now";
  const min = Math.round(ms / 60000);
  if (min < 1) return "just now";
  if (min < 60) return `${min} min ago`;
  const h = Math.round(min / 60);
  if (h < 48) return `${h} h ago`;
  return `${Math.round(h / 24)} days ago`;
}

/* ---- one add-on's page ----
   The facts are a header, not rows: nothing in it takes the highlight, so everything the D-pad can
   land on below it does something. The header carries what used to be read-only rows (the
   description, what an extension can reach, whether it is running, an error) beside the icon,
   with the counts and a status pill. */

/** The pill beside the name: where the add-on stands, in a word or two. */
function addonStatusPill(a) {
  const pill = (text, tone) => `<span class="addon-pill${tone ? " " + tone : ""}">${esc(text)}</span>`;
  if (a.busy === "downloading") return pill(`Downloading ${a.progress || 0}%`, "accent");
  if (a.busy === "installing") return pill("Installing…", "accent");
  if (!a.installed) return a.needsLauncher ? pill(`Needs Loungepad ${a.needsLauncher}`, "warn") : pill("Not installed");
  if (a.error) return pill("Cannot load", "danger");
  if (a.kind === "theme") return addonInUse(a) ? pill("In use", "good") : pill(a.bundled ? "Built in" : "Installed");
  if (!a.enabled) return pill("Off");
  const st = (a.status && a.status.state) || "stopped";
  return st === "running" ? pill("Running", "good") : st === "error" ? pill("Error", "danger")
    : st === "starting" ? pill("Starting") : pill("Stopped");
}

function addonHeroEl(a) {
  const el = document.createElement("div");
  el.className = "addon-hero";
  const icon = a.icon ? `<div class="addon-hero-icon" style="background-image:url('${esc(a.icon)}')"></div>`
    : `<div class="addon-hero-icon mono"><span>${esc((a.name || "?").trim().charAt(0).toUpperCase())}</span></div>`;
  const meta = [a.kind === "theme" ? "Theme" : "Extension",
    a.version ? "v" + a.version : a.available ? "v" + a.available : null,
    a.author ? "by " + a.author : null].filter(Boolean).join(" · ");
  const pills = addonStatusPill(a)
    + (a.installed && a.update ? `<span class="addon-pill accent">Update to ${esc(a.available)}</span>` : "");

  const facts = [];
  if (hasCounts(a)) {
    facts.push(`<span class="addon-fact${a.liked ? " liked" : ""}">${iconSvg("heart")}<b>${fmtCount(a.likes)}</b> ${a.likes === 1 ? "like" : "likes"}</span>`);
    facts.push(`<span class="addon-fact">${iconSvg("download")}<b>${fmtCount(a.downloads)}</b> ${a.downloads === 1 ? "download" : "downloads"}</span>`);
  }
  if (a.kind === "extension") {
    const hosts = a.hosts || [];
    facts.push(`<span class="addon-fact">${iconSvg("globe")}${hosts.length ? `Reaches <b>${esc(hosts.join(", "))}</b> only` : "Reaches nothing on the network"}</span>`);
  }
  const st = a.status || {};
  if (a.kind === "extension" && a.installed && a.enabled && st.state === "running" && st.startedAt)
    facts.push(`<span class="addon-fact">${iconSvg("clock")}Running for ${esc(fmtAgo(st.startedAt).replace(" ago", "").replace("just now", "a moment"))}${st.calls ? ` · ${st.calls} ${st.calls === 1 ? "lookup" : "lookups"}` : ""}</span>`);

  const notes = [];
  if (a.error) notes.push(`<div class="addon-hero-warn">${esc(a.error)}. Reinstall it, or remove it below.</div>`);
  else if (a.kind === "extension" && a.enabled && st.state === "error") notes.push(`<div class="addon-hero-warn">${esc(st.error || "It stopped with an error")}</div>`);
  if (!a.installed && a.needsLauncher) notes.push(`<div class="addon-hero-warn">This version needs Loungepad ${esc(a.needsLauncher)}; this is ${esc(addonsData().launcher || "older")}. Update Loungepad first.</div>`);
  if (a.kind === "extension")
    notes.push(`<div class="addon-hero-note">It cannot read your settings, your files or your accounts, start anything, or draw on the screen.</div>`);
  if (a.bundled) notes.push(`<div class="addon-hero-note">Part of Loungepad, and kept up to date with it.</div>`);

  el.innerHTML = icon + `<div class="addon-hero-body">
    <div class="addon-hero-title"><span class="addon-hero-name">${esc(a.name)}</span>${pills}</div>
    <div class="addon-hero-meta">${esc(meta)}</div>
    ${a.description ? `<p class="addon-hero-desc">${esc(a.description)}</p>` : ""}
    ${facts.length ? `<div class="addon-hero-facts">${facts.join("")}</div>` : ""}
    ${notes.join("")}
  </div>`;
  return el;
}

function addonPageRows(a) {
  const rows = [];
  const s = S.settings || {};
  const set = (fn) => { fn(); scheduleSave(); renderSettings(); };
  rows.push({ section: true, header: true, build: () => addonHeroEl(a) });
  if (a.available) rows.push({
    name: a.liked ? "You like this" : "Like", type: "html",
    hint: "Kept on this PC. The add-ons service only counts it",
    valueHtml: `<span class="addon-like-row${a.liked ? " liked" : ""}">${iconSvg("heart")}<span>${a.liked ? "Liked" : "Like"}</span></span>`,
    action: () => toggleAddonLike(a),
  });

  // What to do with it.
  if (!a.installed) {
    rows.push({
      name: "Install", type: "action", label: a.busy ? (a.busy === "downloading" ? `${a.progress || 0}%` : "Installing…") : "Install",
      hint: a.needsLauncher ? "Needs a newer Loungepad" : `From the add-ons repository${a.available ? ", version " + a.available : ""}`,
      action: () => installAddon(a),
    });
  } else if (a.update) {
    rows.push({
      name: `Update to ${a.available}`, type: "action", label: a.busy ? (a.busy === "downloading" ? `${a.progress || 0}%` : "Installing…") : "Update",
      hint: a.installable ? `You have ${a.version || "an older version"}. The old copy is kept in the backups folder`
        : `The new version needs Loungepad ${a.needsLauncher}. Update Loungepad first`,
      action: () => installAddon(a),
    });
  }
  if (a.kind === "theme" && a.installed) {
    const inUse = addonInUse(a);
    rows.push({
      name: inUse ? "In use" : "Use this theme", type: "action", label: inUse ? "Current" : "Use",
      hint: inUse ? "Its colour, hints, animation and options are under Appearance" : "Switches the whole launcher to it. Your accent and options for it are kept from last time",
      action: () => {
        if (inUse) { toast("Already in use"); return; }
        set(() => { s.theme = a.id; });
        applyTheme();
        toast(`Now using ${a.name}`);
      },
    });
    if (inUse) rows.push({
      name: "Theme options", hint: "Accent, hints, animation and what the theme declares for itself, under Appearance",
      type: "action", label: "Open",
      action: () => { settingsTab = "appearance"; settingsPane = "rows"; settingsIdx = 0; renderSettings(); pulse($("settingsScroll")); },
    });
  }
  if (a.kind === "extension" && a.installed && !a.error) {
    rows.push(toggleRow("Enabled", a.enabled ? "On. Off stops it and keeps what it stored" : "Off. On starts it and asks it about your games",
      () => !!a.enabled, v => { a.enabled = v; send({ cmd: "addonEnable", key: a.key, on: v }); renderSettings(); }));
    if (a.enabled && a.contributes && a.contributes.metadata) {
      const lp = a.lastPass;
      const passing = addonsData().passRunning;
      rows.push({
        name: "Fetch now", type: "action", label: passing ? "Running…" : "Fetch",
        hint: (lp ? `Last pass ${fmtAgo(lp.at)}: ${lp.tried} asked, ${lp.found} answered${lp.failed ? `, ${lp.failed} failed` : ""}${lp.error ? ". " + lp.error : ""}. `
          : "Not yet run. ") + "Asks about every game again, installed ones first, and shows what it finds as it goes",
        action: () => { if (passing) { toast("A pass is already running"); return; } send({ cmd: "addonFetchNow", key: a.key }); },
      });
    }
    const defs = a.enabled ? extSettingDefs(a) : [];
    if (defs.length) {
      rows.push({ section: `${a.name.toUpperCase()} OPTIONS`, cat: "addons" });
      const bag = () => extSettingBag(a.id, true);
      const put = (d, v) => set(() => { bag()[d.id] = v; });
      for (const d of defs) {
        const cur = () => extSettingValue(a, d);
        if (d.type === "toggle") rows.push(toggleRow(d.name, d.hint, cur, v => put(d, v)));
        else if (d.type === "select") rows.push(cycleRow(d.name, d.options, cur, v => put(d, v), d.hint, null, d.labels));
        else rows.push(sliderRow(d.name, cur, d.min, d.max, d.step, v => put(d, v), v => fmtThemeValue(d, v), d.hint));
      }
    }
  }

  rows.push({ section: "MANAGE", cat: "addons" });
  if (a.canReload) rows.push({
    name: a.source === "zip" ? "Reload from the zip" : "Reload from the folder",
    hint: "Copies it in again" + (a.kind === "extension" ? " and restarts it. For writing one: edit, reload, look" : ". For writing one: edit, reload, look"),
    type: "action", label: "Reload",
    action: () => send({ cmd: "addonReload", key: a.key }),
  });
  if (a.kind === "extension" && a.installed && a.enabled && !a.error) {
    rows.push({ name: "Restart", hint: "Stops it and starts it again", type: "action", label: "Restart", action: () => send({ cmd: "addonRestart", key: a.key }) });
    rows.push({
      name: "Open developer tools", hint: "The extension's own console, in a window on the TV. For whoever is writing it",
      type: "action", label: "Open",
      action: () => send({ cmd: "addonDevTools", key: a.key }),
    });
  }
  if (a.homepage) rows.push({
    name: "Open the homepage", hint: a.homepage, type: "action", label: "Open",
    action: () => send({ cmd: "addonOpenHomepage", key: a.key }),
  });
  if (a.installed && !a.bundled) rows.push({
    name: "Remove", type: "action", label: "Remove", danger: true,
    hint: a.kind === "theme" ? "The folder goes to the backups folder. Appearance falls back to Loungepad if this is the theme in use"
      : "The folder goes to the backups folder. What it stored on your games stays, and comes back into use if it is installed again",
    action: () => askConfirm({
      title: `Remove ${a.name}?`,
      body: a.kind === "theme" ? "A copy is kept under theme-backups in the Loungepad data folder." : "It stops now. A copy is kept under extension-backups in the Loungepad data folder.",
      yesLabel: "Remove", icon: "trash", danger: true,
      onYes: () => { send({ cmd: "addonRemove", key: a.key }); addonsTabReset(); settingsIdx = 0; },
    }),
  });
  rows.push({
    name: a.kind === "theme" ? "Themes folder" : "Extensions folder", hint: "Opens it in Explorer, for a look at the files",
    type: "action", label: "Open",
    action: () => send({ cmd: "addonsOpenFolder", kind: a.kind }),
  });
  return rows;
}

/* ---- an extension's own options ----
   The manifest declares them in the shape theme.json uses, so the same checker applies
   (themeSettingDefs); the values live under settings.extensionSettings[id], the way a theme's do
   under themeSettings, and reach the running extension through the host after a save. */
function extSettingDefs(a) { return themeSettingDefs({ settings: a.settings }); }

function extSettingBag(id, create) {
  const s = S.settings;
  if (!s) return null;
  if (!s.extensionSettings || typeof s.extensionSettings !== "object") { if (!create) return null; s.extensionSettings = {}; }
  if (!s.extensionSettings[id]) { if (!create) return null; s.extensionSettings[id] = {}; }
  return s.extensionSettings[id];
}

function extSettingValue(a, d) {
  const bag = extSettingBag(a.id, false);
  const v = bag ? bag[d.id] : undefined;
  if (d.type === "toggle") return typeof v === "boolean" ? v : d.default;
  if (d.type === "select") return d.options.includes(v) ? v : d.default;
  return typeof v === "number" && isFinite(v) ? Math.max(d.min, Math.min(d.max, v)) : d.default;
}

/* ---- installing ---- */

/* From the repository. An extension says what it may reach before anything is fetched: the hosts
   are the whole of what it can do beyond storing a few facts per game. */
function installAddon(a) {
  if (a.busy) { toast(a.busy === "downloading" ? "Already downloading" : "Already installing"); return; }
  if (!a.installable) { toast(`Needs Loungepad ${a.needsLauncher}`); return; }
  const go = () => send({ cmd: "addonInstall", key: a.key });
  if (a.kind === "theme") { go(); return; }
  const hosts = a.hosts || [];
  askConfirm({
    title: `${a.installed ? "Update" : "Install"} ${a.name}?`,
    body: (hosts.length
      ? `It will be able to send requests to ${hosts.join(", ")} and nowhere else.`
      : "It sends nothing anywhere.")
      + " It cannot read your settings, your files or your accounts, start anything, or draw on the screen.",
    yesLabel: a.installed ? "Update" : "Install", icon: "download", danger: false,
    onYes: go,
  });
}

/* From this PC: a zip, or a folder, through the host's dialogs. */
function startAddonFileInstall(kind) {
  const what = kind === "theme" ? "theme" : "extension";
  openChoice(`Install a ${what}`, [
    { label: "From a zip file…", icon: "file", sub: `A ${what} downloaded as a zip, with its ${kind === "theme" ? "theme.css" : "manifest.json"} inside`, action: () => send({ cmd: "addonInstallFile", how: "zip" }) },
    { label: "From a folder…", icon: "folder", sub: `A folder on this PC. Edits there can be reloaded from the ${what}'s page`, action: () => send({ cmd: "addonInstallFile", how: "folder" }) },
  ]);
}

function refreshAddons() {
  const c = addonsData().catalogue;
  if (c && c.busy) { toast("Already checking"); return; }
  send({ cmd: "addonsRefresh" });
  toast("Checking the add-ons repository…");
}

/* ---- the short menu (Y on a tile) ---- */

function addonQuickMenu(a) {
  const items = [{ label: "View details", icon: "info", action: () => enterAddon(a.key) }];
  if (a.available) items.push({ label: a.liked ? "Unlike" : "Like", icon: "heart", sub: hasCounts(a) ? `${fmtCount(a.likes)} likes` : null, action: () => toggleAddonLike(a) });
  if (!a.installed) items.push({ label: "Install", icon: "download", sub: a.installable ? (a.available ? "Version " + a.available : null) : `Needs Loungepad ${a.needsLauncher}`, action: () => installAddon(a) });
  else if (a.update) items.push({ label: `Update to ${a.available}`, icon: "download", action: () => installAddon(a) });
  if (a.kind === "theme" && a.installed && !addonInUse(a))
    items.push({ label: "Use this theme", icon: "image", action: () => { if (S.settings) { S.settings.theme = a.id; applyTheme(); scheduleSave(); renderSettings(); toast(`Now using ${a.name}`); } } });
  if (a.kind === "extension" && a.installed && !a.error)
    items.push({ label: a.enabled ? "Turn off" : "Turn on", icon: "power", action: () => send({ cmd: "addonEnable", key: a.key, on: !a.enabled }) });
  if (a.installed && !a.bundled)
    items.push({ label: "Remove", icon: "trash", action: () => askConfirm({
      title: `Remove ${a.name}?`, body: "A copy is kept in the backups folder.", yesLabel: "Remove", icon: "trash", danger: true,
      onYes: () => { send({ cmd: "addonRemove", key: a.key }); addonsTabReset(); settingsIdx = 0; },
    }) });
  openChoice(a.name, items);
}

/* ---- what an extension stored, on the page and in the templates ---- */

/* "12½ h" the way howlongtobeat.com prints a time: to the nearest half hour, the half as a glyph. */
function fmtHours(v) {
  const n = Number(v);
  if (!isFinite(n) || n <= 0) return "";
  if (n < 1) return `${Math.max(5, Math.round(n * 60 / 5) * 5)} min`;
  const half = Math.round(n * 2) / 2;
  const whole = Math.floor(half);
  return `${whole}${half - whole ? "½" : ""} h`;
}

function fmtExtValue(v, format) {
  if (v === null || v === undefined || v === "") return "";
  switch (format) {
    case "hours": return fmtHours(v);
    case "number": { const n = Number(v); return isFinite(n) ? String(Math.round(n * 10) / 10) : ""; }
    case "percent": { const n = Number(v); return isFinite(n) ? `${Math.round(n)}%` : ""; }
    default: return typeof v === "object" ? "" : String(v).slice(0, 80);
  }
}

/* The data an extension stored on a game, or null. */
function extData(g, id) {
  const rec = g && g.ext && g.ext[id];
  return rec && rec.found && rec.data && typeof rec.data === "object" ? rec.data : null;
}

/* The template fields: ext.<id>.<key> as stored, plus ext.<id>.<key>Text as the manifest's label
   formats it, for every active extension with something on this game. Only built for a game that
   has anything, so the grid pays nothing for it. */
function extView(g) {
  if (!g || !g.ext) return {};
  const out = {};
  for (const a of activeExtensions()) {
    const data = extData(g, a.id);
    if (!data) continue;
    const view = { ...data };
    const facts = (a.contributes && a.contributes.gameFacts) || [];
    for (const f of facts) view[f.key + "Text"] = fmtExtValue(data[f.key], f.format);
    out[a.id] = view;
  }
  return out;
}

/* Under the facts on a game's page: one line per extension that has something, the extension's
   name first and then each contributed fact that has a value. Empty stays empty (and collapsed). */
function renderDetailExtFacts(g) {
  const el = $("detailExtFacts");
  if (!el) return;
  const lines = [];
  for (const a of activeExtensions()) {
    const data = extData(g, a.id);
    const facts = (a.contributes && a.contributes.gameFacts) || [];
    if (!data || !facts.length) continue;
    const parts = facts.map(f => ({ label: f.label, text: fmtExtValue(data[f.key], f.format) })).filter(p => p.text);
    if (!parts.length) continue;
    lines.push(`<div class="ext-line"><span class="ext-src">${esc(a.name)}</span>` +
      parts.map(p => `<span class="ext-fact"><b>${esc(p.text)}</b> ${esc(p.label)}</span>`).join(`<span class="fact-dot">·</span>`) + `</div>`);
  }
  el.innerHTML = lines.join("");
}

/* The host's install progress: one field on one tile, never a state push. */
function onAddonProgress(m) {
  const a = addonByKey(m.key);
  if (!a) return;
  a.busy = m.state === "done" || m.state === "failed" ? null : m.state;
  a.progress = m.percent || 0;
  if (view === "settings" && settingsTab === "addons") renderSettings();
}
