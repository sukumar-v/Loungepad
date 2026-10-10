// Settings → Add-ons and the extension facts (Loungepad/ui/addons.js), under Node with the page's
// globals stubbed: `node --test tests\addons-ui.test.cjs`.
const assert = require('node:assert/strict');
// Arrays made inside the vm context have another realm's prototype, which strict deep equality
// rejects; the shapes are what matter here.
const deepEqual = (a, b, m) => assert.equal(JSON.stringify(a), JSON.stringify(b), m);
const fs = require('node:fs');
const vm = require('node:vm');
const path = require('node:path');

const source = fs.readFileSync(path.join(__dirname, '../Loungepad/ui/addons.js'), 'utf8');
const appJs = fs.readFileSync(path.join(__dirname, '../Loungepad/ui/app.js'), 'utf8');
// The theme-option checker is app.js's; the add-ons reuse it as it is.
const defsStart = appJs.indexOf('const THEME_SETTING_TYPES');
const defsEnd = appJs.indexOf('/** The value in force for one of a theme\'s options', defsStart);
assert(defsStart > 0 && defsEnd > defsStart);
const fmtStart = appJs.indexOf('function fmtThemeValue(d, v)');
const fmtEnd = appJs.indexOf('\n}\n', fmtStart) + 3;

const sent = [];
let confirm = null, choice = null, rendered = 0, toasts = [];
const ctx = {
  S: { addons: null, settings: { theme: '', extensionSettings: {} }, games: [] },
  settingsTab: 'addons', settingsPane: 'nav', settingsIdx: 0, view: 'settings',
  addonsUi: { level: 'grid', kind: 'theme', key: null },
  send: m => sent.push(m), esc: s => String(s), iconSvg: n => `<i>${n}</i>`, slot: b => `[${b}]`,
  renderSettings: () => rendered++, pulse: () => {}, $: () => ({}), toast: m => toasts.push(m),
  askConfirm: st => { confirm = st; }, openChoice: (title, items) => { choice = { title, items }; },
  scheduleSave: () => {}, applyTheme: () => {}, RESERVED_IDS: new Set(),
  toggleRow: (name, hint, get, setV) => ({ name, hint, type: 'toggle', value: get(), action: () => setV(!get()) }),
  cycleRow: (name, options, get, setV, hint) => ({ name, hint, type: 'select', value: get(), choices: options, pick: setV }),
  sliderRow: (name, get, min, max, step, setV, fmt, hint) => ({ name, hint, type: 'slider', value: get(), pick: setV }),
  document: { createElement: () => ({ className: '', dataset: {}, innerHTML: '', appendChild() {}, addEventListener() {} }) },
  Date, Math, Number, String, Object, Array, isFinite, console,
};
vm.createContext(ctx);
vm.runInContext(appJs.slice(defsStart, defsEnd) + '\n' + appJs.slice(fmtStart, fmtEnd) + '\n' + source, ctx);

const hltb = {
  key: 'extension:howlongtobeat', id: 'howlongtobeat', kind: 'extension', name: 'HowLongToBeat', installed: true, enabled: true,
  version: '1.0.0', available: '1.0.0', update: false, installable: true, hosts: ['howlongtobeat.com'],
  contributes: { metadata: { staleAfterDays: 30 }, gameFacts: [
    { key: 'main', label: 'Main story', format: 'hours' }, { key: 'mainExtra', label: 'Main + extras', format: 'hours' }, { key: 'score', label: 'Score', format: 'percent' }] },
  settings: [{ id: 'match-year', name: 'Match the release year', type: 'toggle', default: false }, { id: 'bad id', type: 'toggle' }],
  status: { state: 'running', calls: 3 }, lastPass: { at: new Date().toISOString(), tried: 10, found: 8, failed: 1 },
};
const items = [
  { key: 'theme:loungepad', id: 'loungepad', kind: 'theme', name: 'Loungepad', installed: true, bundled: true, enabled: true, version: '4.6' },
  { key: 'theme:night', id: 'night', kind: 'theme', name: 'Night', installed: true, enabled: true, version: '1.0.0', available: '1.1.0', update: true, installable: true, canReload: true, source: 'folder' },
  { key: 'theme:arcade', id: 'arcade', kind: 'theme', name: 'Arcade', installed: false, available: '0.9.0', installable: true, author: 'pixelgrid' },
  hltb,
  { key: 'extension:protondb', id: 'protondb', kind: 'extension', name: 'ProtonDB', installed: false, available: '1.1.0', installable: false, needsLauncher: '1.10.0', hosts: ['www.protondb.com'] },
];
ctx.S.addons = { items, catalogue: { fetchedAt: new Date(Date.now() - 7200000).toISOString(), error: null, busy: false }, launcher: '1.9.0', passRunning: false };

// The grid: a header, Installed (with the file tile), Available (with the refresh tile).
let rows = ctx.addonsSettingsRows();
assert(rows[0].header && rows[0].section, 'the tab strip is a header that counts as a section');
let tiles = rows.filter(r => !r.section);
deepEqual(tiles.map(t => t.addon ? t.addon.id : t.add ? 'add' : 'refresh'), ['loungepad', 'night', 'add', 'arcade', 'refresh']);
deepEqual(rows.filter(r => typeof r.section === 'string').map(r => r.section), ['INSTALLED', 'AVAILABLE']);
ctx.addonsSwitchKind(1);
assert.equal(ctx.addonsUi.kind, 'extension');
tiles = ctx.addonsSettingsRows().filter(r => !r.section);
deepEqual(tiles.map(t => t.addon ? t.addon.id : t.add ? 'add' : 'refresh'), ['howlongtobeat', 'add', 'protondb', 'refresh']);
ctx.addonsSwitchKind(1);
assert.equal(ctx.addonsUi.kind, 'theme', 'the tabs come round');

// The tile line.
deepEqual(ctx.addonTileSub(items[0]), { text: 'Built in · v4.6' });
deepEqual(ctx.addonTileSub(items[1]), { text: 'Update to 1.1.0', accent: true });
deepEqual(ctx.addonTileSub(items[2]), { text: 'v0.9.0 · by pixelgrid' });
deepEqual(ctx.addonTileSub(hltb), { text: 'Running · v1.0.0' });
deepEqual(ctx.addonTileSub(items[4]), { text: 'Needs Loungepad 1.10.0', muted: true });
deepEqual(ctx.addonTileSub({ ...hltb, busy: 'downloading', progress: 40 }), { text: 'Downloading 40%', accent: true });
deepEqual(ctx.addonTileSub({ ...hltb, enabled: false }), { text: 'Off · v1.0.0', muted: true });
deepEqual(ctx.addonTileSub({ ...hltb, status: { state: 'error' } }), { text: 'Error · v1.0.0', accent: true });
ctx.S.settings.theme = 'night';
assert.match(ctx.addonTileHtml({ addon: items[1] }), /addon-badge">Update/);
ctx.S.settings.theme = 'loungepad';
assert.match(ctx.addonTileHtml({ addon: items[0] }), /In use/);
assert.equal(ctx.availableHeading([]), 'AVAILABLE · NOTHING MORE FOR NOW');
ctx.S.addons.catalogue = { fetchedAt: null, error: 'Could not reach the add-ons repository', busy: false };
assert.equal(ctx.availableHeading([]), 'AVAILABLE · COULD NOT REACH THE ADD-ONS REPOSITORY');
assert.equal(ctx.availableHeading([items[2]]), 'AVAILABLE', 'an error with a cached list shows the list');
ctx.S.addons.catalogue = { fetchedAt: new Date().toISOString(), error: null, busy: false };

// An extension's page: facts, the switch, fetch, its options (the bad one dropped), manage.
ctx.enterAddon('extension:howlongtobeat');
assert.equal(ctx.addonsUi.level, 'addon');
rows = ctx.addonsSettingsRows();
const names = rows.filter(r => !r.section).map(r => r.name);
// Only rows that do something: the facts are the header, which is not focusable.
deepEqual(names, ['Like', 'Enabled', 'Fetch now', 'Match the release year', 'Restart', 'Open developer tools', 'Remove', 'Extensions folder']);
const hero = rows.find(r => r.header);
assert(hero && hero.section, 'the header is a section to the counting, so never highlighted');
const heroHtml = hero.build().innerHTML;
assert.match(heroHtml, /addon-hero-name">HowLongToBeat</);
assert.match(heroHtml, /addon-pill good">Running</);
assert.match(heroHtml, /Reaches <b>howlongtobeat\.com<\/b> only/);
assert.match(heroHtml, /cannot read your settings/);
assert.match(heroHtml, /Extension · v1\.0\.0/);
assert.match(ctx.addonStatusPill({ ...hltb, status: { state: 'error' } }), /danger">Error/);
assert.match(ctx.addonStatusPill({ ...hltb, enabled: false }), />Off</);
assert.match(ctx.addonStatusPill({ installed: false, needsLauncher: '2.0.0', kind: 'extension' }), /warn">Needs Loungepad 2\.0\.0/);
assert.match(ctx.addonHeroEl({ ...hltb, error: 'manifest.json is not valid' }).innerHTML, /addon-hero-warn">manifest\.json is not valid/);
assert.match(ctx.addonHeroEl({ ...hltb, name: '<b>x</b>' }).innerHTML, /&lt;b&gt;|\<b\>x/, 'the name goes through esc');
assert.match(rows.find(r => r.name === 'Fetch now').hint, /10 asked, 8 answered, 1 failed/);
rows.find(r => r.name === 'Enabled').action();
deepEqual(sent.pop(), { cmd: 'addonEnable', key: 'extension:howlongtobeat', on: false });
hltb.enabled = true;
rows.find(r => r.name === 'Fetch now').action();
deepEqual(sent.pop(), { cmd: 'addonFetchNow', key: 'extension:howlongtobeat' });
const opt = ctx.addonsSettingsRows().find(r => r.name === 'Match the release year');
assert.equal(opt.value, false);
opt.action();
assert.equal(ctx.S.settings.extensionSettings.howlongtobeat['match-year'], true, 'the option lands in extensionSettings');
rows.find(r => r.name === 'Remove').action();
assert.equal(confirm.yesLabel, 'Remove');
confirm.onYes();
deepEqual(sent.pop(), { cmd: 'addonRemove', key: 'extension:howlongtobeat' });
assert.equal(ctx.addonsUi.level, 'grid', 'removing goes back to the grid');

// Installing an extension asks first and names the hosts; a theme does not ask.
ctx.installAddon(items[4]);
assert.equal(sent.length, 0);
assert.match(toasts.pop(), /Needs Loungepad 1.10.0/);
ctx.installAddon({ ...items[4], installable: true, needsLauncher: null });
assert.match(confirm.body, /www\.protondb\.com and nowhere else/);
confirm.onYes();
deepEqual(sent.pop(), { cmd: 'addonInstall', key: 'extension:protondb' });
ctx.installAddon(items[2]);
deepEqual(sent.pop(), { cmd: 'addonInstall', key: 'theme:arcade' });
ctx.startAddonFileInstall('theme');
assert.equal(choice.items.length, 2);
choice.items[1].action();
deepEqual(sent.pop(), { cmd: 'addonInstallFile', how: 'folder' });

// The short menu.
ctx.S.settings.theme = 'loungepad';
ctx.addonQuickMenu(items[1]);
deepEqual(choice.items.map(i => i.label), ['View details', 'Like', 'Update to 1.1.0', 'Use this theme', 'Remove']);
assert.equal(choice.items[0].sub, undefined, 'View details has no sub text');
ctx.addonQuickMenu(items[2]);
deepEqual(choice.items.map(i => i.label), ['View details', 'Like', 'Install']);
ctx.addonQuickMenu(items[0]);
deepEqual(choice.items.map(i => i.label), ['View details'], 'a bundled theme in use: only View details');

// The back path lands on the tile that was opened.
ctx.addonsUi = { level: 'addon', kind: 'theme', key: 'theme:night' };
assert.equal(ctx.addonsBack(), true);
assert.equal(ctx.settingsIdx, 1);
assert.equal(ctx.addonsBack(), false, 'B at the grid means the categories');

// Hours as the site prints them, and the facts under a game's page.
assert.equal(ctx.fmtHours(27), '27 h');
assert.equal(ctx.fmtHours(41.5), '41½ h');
assert.equal(ctx.fmtHours(26.74), '26½ h');
assert.equal(ctx.fmtHours(0.3), '20 min');
assert.equal(ctx.fmtHours(0), '');
assert.equal(ctx.fmtExtValue(91, 'percent'), '91%');
assert.equal(ctx.fmtExtValue(3.14159, 'number'), '3.1');
assert.equal(ctx.fmtExtValue({ a: 1 }, 'text'), '', 'an object prints nothing');
const game = { id: 'steam:1', ext: { howlongtobeat: { at: '', ext: '1.0.0', found: true, data: { main: 27, mainExtra: 41.5, score: 91, extra: 'x' } } } };
deepEqual(ctx.extView(game), { howlongtobeat: { main: 27, mainExtra: 41.5, score: 91, extra: 'x', mainText: '27 h', mainExtraText: '41½ h', scoreText: '91%' } });
deepEqual(ctx.extView({ id: 'x' }), {});
deepEqual(ctx.extView({ id: 'x', ext: { howlongtobeat: { found: false } } }), {}, 'a "nothing" record draws nothing');
hltb.enabled = false;
deepEqual(ctx.extView(game), {}, 'a disabled extension draws nothing');
hltb.enabled = true;
let html = '';
ctx.$ = () => ({ set innerHTML(v) { html = v; } });
ctx.renderDetailExtFacts(game);
assert.match(html, /ext-src">HowLongToBeat</);
assert.match(html, /<b>27 h<\/b> Main story/);
assert.match(html, /<b>91%<\/b> Score/);
ctx.renderDetailExtFacts({ id: 'none' });
assert.equal(html, '');

// The progress message patches one tile.
ctx.onAddonProgress({ key: 'theme:arcade', state: 'downloading', percent: 55 });
assert.equal(items[2].busy, 'downloading');
assert.equal(items[2].progress, 55);
ctx.onAddonProgress({ key: 'theme:arcade', state: 'done', percent: 100 });
assert.equal(items[2].busy, null);

// Counts, sorting and likes.
assert.equal(ctx.fmtCount(0), '0');
assert.equal(ctx.fmtCount(999), '999');
assert.equal(ctx.fmtCount(1234), '1.2k');
assert.equal(ctx.fmtCount(5000), '5k');
assert.equal(ctx.fmtCount(48500), '48k');
assert.equal(ctx.fmtCount(2350000), '2.3M');
const pool = [
  { key: 'extension:b', name: 'Bravo', downloads: 10, likes: 5 },
  { key: 'extension:a', name: 'alpha', downloads: 10, likes: 9 },
  { key: 'extension:c', name: 'Charlie', downloads: 99 },
  { key: 'extension:d', name: 'Delta' },
];
ctx.addonsUi.sort = 'az';
deepEqual(ctx.sortAddons(pool).map(a => a.name), ['alpha', 'Bravo', 'Charlie', 'Delta']);
ctx.addonsUi.sort = 'downloads';
deepEqual(ctx.sortAddons(pool).map(a => a.name), ['Charlie', 'alpha', 'Bravo', 'Delta'], 'ties by name, no counts last');
ctx.addonsUi.sort = 'likes';
deepEqual(ctx.sortAddons(pool).map(a => a.name), ['alpha', 'Bravo', 'Charlie', 'Delta']);
ctx.addonsUi.sort = 'az';
const liked = { key: 'extension:x', name: 'X', available: '1.0.0', likes: 4, downloads: 9, liked: false };
ctx.toggleAddonLike(liked);
deepEqual(sent.pop(), { cmd: 'addonLike', key: 'extension:x', on: true });
assert.equal(liked.liked, true); assert.equal(liked.likes, 5);
ctx.toggleAddonLike(liked);
deepEqual(sent.pop(), { cmd: 'addonLike', key: 'extension:x', on: false });
assert.equal(liked.likes, 4);
ctx.toggleAddonLike({ key: 'theme:loungepad', name: 'Loungepad', bundled: true });
assert.equal(sent.length, 0, 'an add-on the repository does not list cannot be liked');
assert.match(toasts.pop(), /Only add-ons from the repository/);
assert.match(ctx.addonCountsHtml(liked), /<b>4<\/b>/);
assert.equal(ctx.addonCountsHtml({ key: 'theme:loungepad' }), '', 'no counts, nothing drawn');
ctx.enterAddon('theme:arcade');
assert(ctx.addonsSettingsRows().some(r => r.name === 'Like'), 'a repository add-on has a Like row');
ctx.addonsUi = { level: 'addon', kind: 'theme', key: 'theme:loungepad', sort: 'az' };
assert(!ctx.addonsSettingsRows().some(r => r.name === 'Like'), 'a bundled theme has none');
ctx.addonsUi = { level: 'grid', kind: 'theme', key: null, sort: 'az' };

// Wiring in app.js and index.html.
assert.match(appJs, /\{ id: "addons",\s+label: "Add-ons" \}/);
assert.match(appJs, /if \(settingsTab === "addons"\) return addonsSettingsRows\(\);/);
// The game model lives in themeview.js now.
assert.match(fs.readFileSync(path.join(__dirname, '../Loungepad/ui/themeview.js'), 'utf8'), /ext: extView\(g\)/);
assert.match(appJs, /renderDetailExtFacts\(g\);/);
assert.match(appJs, /case "addonProgress":/);
const indexHtml = fs.readFileSync(path.join(__dirname, '../Loungepad/ui/index.html'), 'utf8');
assert(indexHtml.indexOf('<script src="actions.js">') < indexHtml.indexOf('<script src="addons.js">'), 'addons.js loads after actions.js');
assert.match(indexHtml, /id="detailExtFacts"/);
console.log('PASS: the add-ons grid, an extension\'s page, the install flows, the short menu, the facts and the wiring');
