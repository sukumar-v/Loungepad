# Add-ons: community themes and extensions

[README](../README.md) · [User guide](GUIDE.md) · [Themes](THEMES.md) · [Add-ons](ADDONS.md) · [Development](DEVELOPMENT.md)

An add-on is something somebody other than Loungepad made for Loungepad. There are two kinds:

- A **theme** restyles the launcher. It is CSS and markup, and cannot run code. [Themes](THEMES.md)
  says how to write one.
- An **extension** adds something the launcher does not do on its own. The first kind of extension
  is a *metadata source*: a small script that fetches facts about each game from somewhere and
  hands them to the launcher, which stores them with the game and shows them on its page. A theme
  can draw them too. [HowLongToBeat](https://howlongtobeat.com) is the first one.

Both are found, installed, updated and removed from **Settings → Add-ons**, from the sofa, and
both come from one place: the **[loungepad-addons](https://github.com/sukumar-v/loungepad-addons)**
repository, which anyone can contribute to with a pull request.

- [Settings → Add-ons](#settings--add-ons)
- [The add-ons repository](#the-add-ons-repository)
- [Writing an extension](#writing-an-extension)
- [The extension API](#the-extension-api)
- [What an extension can and cannot do](#what-an-extension-can-and-cannot-do)
- [How it works inside](#how-it-works-inside)

## Settings → Add-ons

Two tabs, **Themes** and **Extensions**, switched with LB and RB (or a click). Each shows what is
**installed** first and what is **available** from the repository under it, as tiles like the
Actions grid. A tile says what it is and where it stands: the version, *Update to 1.1.0* when the
repository has a newer one, *Needs Loungepad 1.10* when it wants a newer launcher.

**A** on a tile opens the add-on's own page: its description, author and version, what it is
allowed to reach (an extension's permissions), then the rows that act on it -- **Install**,
**Update**, **Use this theme** or **Enabled**, an extension's own settings, and **Remove**. **Y**
on a tile is the short menu with the same actions. **B** goes back, as everywhere else.

The last tile of each installed section is **Install from a file**: a zip, or a folder on this
PC, for a theme or extension that is not in the repository -- your own while you write it, or
somebody else's. An extension installed from a folder keeps a **Reload from the folder** row, so
editing it is edit, reload, look.

A theme installed by dropping its folder into the themes directory, as [Themes](THEMES.md)
describes, shows up here too.

Nothing is fetched from the repository until Settings → Add-ons is opened or the launcher has been
running for a while; the list is cached for six hours and **Refresh** asks again.

## The add-ons repository

`github.com/sukumar-v/loungepad-addons` holds every community theme and extension, one folder each:

```
loungepad-addons/
  index.json                 what the launcher reads: every add-on, its version, its files
  themes/<id>/               theme.json, theme.css, theme.html, …
  extensions/<id>/           manifest.json, main.js, icon.svg, …
  tools/build-index.mjs      rebuilds index.json from the folders
  .github/workflows/         checks every pull request: manifests valid, index in step
```

To add one, or update one, open a pull request against `main` with the add-on's folder and the
version in its manifest bumped. The maintainer reviews it -- what it reaches, what it does with
what it gets, that it is readable -- and merges it. The merge is the release: the index is
rebuilt on `main`, and every launcher sees the new version the next time it looks.

### index.json

The launcher reads `index.json` from the repository's `main` branch and nothing else. An entry:

```json
{
  "id": "howlongtobeat",
  "kind": "extension",
  "name": "HowLongToBeat",
  "version": "1.0.0",
  "summary": "How long each game takes to beat, on its page",
  "author": "Loungepad",
  "homepage": "https://github.com/sukumar-v/loungepad-addons/tree/main/extensions/howlongtobeat",
  "minLauncher": "1.9.0",
  "permissions": { "hosts": ["howlongtobeat.com"] },
  "icon": "icon.svg",
  "base": "https://raw.githubusercontent.com/sukumar-v/loungepad-addons/<commit>/extensions/howlongtobeat/",
  "files": [
    { "path": "manifest.json", "sha256": "…", "size": 812 },
    { "path": "main.js", "sha256": "…", "size": 6140 },
    { "path": "icon.svg", "sha256": "…", "size": 930 }
  ]
}
```

Installing is downloading each file from `base` and checking its SHA-256 against the index before
anything is written where the launcher reads it. `base` names the commit the index was built from,
so a merge after the index was built cannot change what an install gets; `build-index.mjs` writes
both. There is no zip to build and no release to publish: a merge is enough.

Why a repository of its own and not a folder in Loungepad's: a contribution to an add-on is a
contribution to that add-on, reviewed on its own terms and released the moment it is merged. In
the launcher's repository it would wait for a launcher release, and every launcher release would
carry every add-on's history. The two move at different speeds.

What the repository is trusted for: the maintainer is the one deciding what goes in. The hashes in
the index protect an install against a damaged or substituted download, not against the
repository itself -- which is the same trust the launcher's own updater places in its releases.
What limits the damage a bad extension could do is the sandbox below, not the review.

## Writing an extension

An extension is a folder with a `manifest.json` and a JavaScript module. This is HowLongToBeat's
manifest, which uses most of what there is:

```json
{
  "id": "howlongtobeat",
  "kind": "extension",
  "name": "HowLongToBeat",
  "version": "1.0.0",
  "description": "How long each game takes to beat -- main story, with extras, completionist -- from howlongtobeat.com, on every game's page.",
  "author": "Loungepad",
  "homepage": "https://github.com/sukumar-v/loungepad-addons/tree/main/extensions/howlongtobeat",
  "main": "main.js",
  "icon": "icon.svg",
  "minLauncher": "1.9.0",
  "permissions": { "hosts": ["howlongtobeat.com"] },
  "contributes": {
    "metadata": { "staleAfterDays": 30, "retryAfterDays": 7, "paceMs": 1500 },
    "gameFacts": [
      { "key": "main", "label": "Main story", "format": "hours" },
      { "key": "mainExtra", "label": "Main + extras", "format": "hours" },
      { "key": "completionist", "label": "Completionist", "format": "hours" }
    ]
  },
  "settings": [
    { "id": "match-year", "name": "Match the release year", "type": "toggle", "default": true,
      "hint": "Only accept a result released the same year as the game, when both are known" }
  ]
}
```

| field | |
|---|---|
| `id` | letters, digits and hyphens, starting with a letter, up to 40. The folder name, and the key everything is stored under |
| `kind` | `extension` (a theme's manifest is `theme.json`, see [Themes](THEMES.md)) |
| `name`, `description`, `author`, `homepage` | what Settings shows |
| `version` | `major.minor.patch`. Bump it in every pull request: it is how the launcher knows there is an update |
| `main` | the module to load, relative to the folder |
| `icon` | an image in the folder, for the tile. SVG, PNG or JPEG |
| `minLauncher` | the oldest launcher that has what the extension uses. The launcher refuses to install anything newer than itself knows |
| `permissions.hosts` | every host the extension may send requests to. Exact names, or `*.example.com`. Shown on the add-on's page and in the install confirmation |
| `contributes.metadata` | the extension fills in facts per game (see below). `staleAfterDays`: how long an answer is kept before it is asked for again; `retryAfterDays`: how long a game with no answer waits before it is tried again; `paceMs`: the gap the launcher leaves between two games |
| `contributes.gameFacts` | which of the stored fields the launcher shows on a game's page, with a label and a format: `hours`, `number`, `text` or `percent` |
| `settings` | rows under the add-on's page, exactly as a theme declares them (`toggle`, `select`, `slider`; see [Themes](THEMES.md)). The values reach the extension as `loungepad.settings` |

The module exports the hooks the launcher calls:

```js
// main.js
export async function activate(ctx) {
  // once, after the extension is loaded: ctx.settings, ctx.launcher, ctx.api
}

export async function enrich(game) {
  // one game: return an object to store, or null for "nothing for this one"
  const res = await loungepad.fetch(`https://example.com/search?q=${encodeURIComponent(game.title)}`);
  if (!res.ok) throw new Error(`HTTP ${res.status}`);
  const hit = pick(JSON.parse(res.body), game);
  return hit ? { main: hit.hours } : null;
}
```

What `enrich` gets is a plain description of the game: `id`, `title`, `platform`, `store`
(`steam`, `epic`, `gog`, `xbox`, `manual`, `rom`), `steamAppId`, `platformId` for a ROM,
`releaseDate`, `year`, `developer`, `publisher`, `genres`, `installed`, `playtimeMinutes`,
`lastPlayed`. Nothing that identifies the person: no account ids, no paths.

What it returns is stored on the game under the extension's id, as it is, up to 16 KB, and
reaches a game's page (through `contributes.gameFacts`) and the theme templates (as
`{{ext.<id>.<key>}}`, with `{{ext.<id>.<key>Text}}` already formatted). Return `null` and the
launcher remembers there was nothing, and asks again after `retryAfterDays`. Throw and the game is
left for the next pass.

The launcher runs the pass: after every scan it goes through the games the extension has no fresh
answer for -- installed first, most recently played first -- and calls `enrich` for each, one at
a time, `paceMs` apart, saving as it goes. **Fetch now** on the add-on's page runs it on demand.
An extension never has to schedule anything, and should not: its page is hidden, and a hidden
page's timers are throttled.

**Match carefully.** A wrong answer looks exactly like a right one. The launcher's own rule for
titles is exact-after-normalising -- accents, `&`, punctuation and one trailing edition suffix
folded -- and never a prefix match: "Portal" must not match "Portal 2". HowLongToBeat's `main.js`
has a `titleKey` that does this and is worth copying.

**Test it in a terminal first.** The folder's `test.mjs` runs the extension under Node with a
stand-in `loungepad` whose `fetch` is Node's own, against the live site, and checks a handful of
titles. It is the same module the launcher loads, so what passes there works in the launcher.
Then **Install from a file → a folder** in Settings → Add-ons, **Fetch now**, and **Open developer
tools** on the add-on's page for the console.

## The extension API

The module runs in a page of its own with one global, `loungepad`:

| | |
|---|---|
| `loungepad.id`, `loungepad.version`, `loungepad.launcher`, `loungepad.api` | the extension's id and version, the launcher's version, and the API version (`1`) |
| `loungepad.settings` | the values of the manifest's `settings`, kept current |
| `loungepad.on("settings", fn)` | called when the user changes one |
| `await loungepad.fetch(url, init)` | an HTTPS request to one of `permissions.hosts`. `init` takes `method`, `headers` and `body` (a string). Returns `{ status, ok, url, headers, body }` with `body` as text. 30 seconds, 5 MB, redirects followed only within the allowed hosts, no cookies kept between requests |
| `await loungepad.storage.get(key)`, `.set(key, value)`, `.remove(key)` | a small JSON store of the extension's own, for a cached token or the last thing looked up. 256 KB in all |
| `await loungepad.games.list()` | every game in the library, in the shape `enrich` gets |
| `loungepad.log(message)` | a line in loungepad.log, prefixed with the extension's id |

Requests are paced by the launcher as well: at most four a second to any one host, whatever the
extension asks for. A host that is not in `permissions.hosts` is refused before anything is sent.

## What an extension can and cannot do

An extension's code runs in a hidden WebView2 page on an origin of its own
(`https://<id>.loungepad.ext`), the way the YouTube player runs on one. It is not in the
launcher's page, it cannot reach the launcher's page, and it has no bridge to the host: the only
thing it can say is the API above, which the launcher checks call by call. In particular an
extension cannot:

- send a request anywhere but the hosts its manifest names, which the user sees before installing;
- read settings, credentials, the library file, or anything on disk;
- start a program, write a file, or change a setting;
- draw on the screen, or take input.

A theme cannot run code at all. What an add-on *can* do is what the launcher does with what it
returns: store a few kilobytes per game and show them. That is the whole surface, which is why the
review in the repository is about quality and not a line of defence.

## How it works inside

- **`AddonService`** (`Services/AddonService.cs`) is the catalogue and the installer: it reads the
  index (cached under `%LOCALAPPDATA%\Loungepad\addons`), lists what is installed (themes through
  `ThemeService`, extensions from `%APPDATA%\Loungepad\extensions`), downloads and verifies, unzips
  (every entry path checked; a zip with one top-level folder is unwrapped), copies a folder, backs
  up before replacing, removes. What it knows about each install -- where it came from, when,
  whether it is enabled -- is `%APPDATA%\Loungepad\addons.json`; the folders themselves are the
  truth about what is installed.
- **`ExtensionRuntime`** and **`ExtensionHost`** (`Services/ExtensionHost.cs`) run the extensions:
  one hidden WebView2 controller per enabled extension, sharing the launcher's browser
  environment, navigated to `https://<id>.loungepad.ext/` and served from the extension's folder
  plus the runtime page shipped in the exe (`ui/ext/`). Messages go through
  `chrome.webview.postMessage`, which arrives on that controller alone: the host sees which
  extension spoke from the origin. The runtime also runs the metadata pass (`RunPassAsync`),
  after the launcher's own, and stores answers on `Game.Ext[<id>]` with a stamp, carried across
  rescans by `MergeScanned`.
- **The page** (`ui/addons.js`) draws Settings → Add-ons from the `addons` payload the host pushes
  with every state and after every change, shows an extension's facts under a game's facts
  (`renderDetailExtFacts`), and puts `ext` on the fields a theme template can bind.
- **Settings:** an extension's options live in `AppSettings.ExtensionSettings[<id>]`, kept to plain
  values by the same cleaner as the themes' (`ThemeService.CleanSettingValues`), and reach the
  running extension as a `settings` event. `AddonsIndexUrl` points the launcher at a different
  index, for testing an add-on against a local copy of the repository.
