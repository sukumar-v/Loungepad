# Loungepad — working notes

## Commit messages

- **Never add a `Co-Authored-By:` trailer.** Not for Claude, not for any model. No
  "Generated with" footers either.
- Subject line: one line, imperative or descriptive, no trailing period.
- Body: **short bullet points only.** Never paragraphs of prose.
- One idea per bullet. Wrap at ~80 columns; indent continuation lines two spaces.
- Say what changed and, where it is not obvious, why — a bullet may carry a root
  cause or a measurement, but keep it to a sentence or two.
- Skip the body entirely when the subject already says everything.

```
Stop the scrolled grid from clipping through the All games header

- The scroller's -18px top margin more than ate the 16px section gap: its top
  edge, where overflow gets clipped, sat 3px above the header's label text
- Margin is now -8px, and the top edge fades over 26px when content is above
- The fade is off at scrollTop 0, so the first row keeps its focus-glow padding
```

## Verifying changes

- The `ui-preview` server renders the same HTML but **not** the WPF/WebView2
  hosting, so it cannot see host-level input, focus, cursor or window bugs. It is
  fine for layout and UI logic only.
- It serves `Loungepad/`, so the app is at `/ui/index.html` and a theme's files are
  reachable at `/themes/<id>/…` — which is what makes a theme previewable at all.
  Push one in by hand: a `{type:"themes", themes:[…]}` message with those URLs,
  then `S.settings.theme = "<id>"` and `applyTheme()`.
- The preview runs `requestAnimationFrame` **once and then never again**, so CSS
  transitions freeze at their start value. Not a bug in the page. To check an animated end
  state, disable transitions (`* { transition: none !important }`) and measure. The scroll
  animator's watchdog snaps to the target after 150 ms without a frame, so scroll-follow can be
  checked in the preview by waiting ~400 ms and reading `scrollTop`.
- The preview has the Loungepad theme in `S.themes` already: `S.settings.theme = "loungepad";
  applyTheme()` switches to it, and `""` goes back to Shelf.
- The published app takes keyboard input now (the WebView gets focus on `Activated`). To drive
  it, **check `GetForegroundWindow() == launcher` before every key you send**: a plain
  `SetForegroundWindow` from PowerShell is often refused, and the keys then go to whatever the
  user has open -- a held-arrow test once scrolled the user's browser. Take the foreground with
  `AttachThreadInput` + `BringWindowToTop` + `SetForegroundWindow`, verify, and abort if lost.
- For anything touching input, focus, the cursor or window behaviour, run the
  published app: `publish\Loungepad.exe --windowed` gives a 1280x720
  non-topmost window. Drive it with real `SendInput` and screen captures.
- The WPF window sets `ShowInTaskbar=false`, so `Process.MainWindowHandle` is 0 —
  find the window by enumerating top-level windows for the pid.
- **Never send clicks** while testing: a stray click once launched a real game.
  Only kill launcher instances started within the session.
- A PowerShell 5.1 driver for that (`Start-Process --windowed`, find the window by pid, take the
  foreground, `SendInput` keys, `CopyFromScreen`) has two traps: `Add-Type` compiles with the C# 5
  compiler, so no `out _`; and the parameter type is `[uint16]`, not `[ushort]`. Both failed AFTER
  the app had been started, which is how a test instance got left running. Put the whole run in
  `try/finally` with the close in the `finally`: WM_CLOSE to the window and a wait, and
  `Stop-Process` only if it has not gone (a kill mid-save can truncate library.json; see the
  first-run setup notes).
- **The real app can be read without sending it a single key**: start it with
  `WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS=--remote-debugging-port=9333` in its environment (set it
  in the launching shell only) and talk CDP to `http://127.0.0.1:9333/json` -- `Runtime.evaluate`
  for the page's state, `Page.captureScreenshot` for a picture,
  `Page.addScriptToEvaluateOnNewDocument` + `Page.reload` to trace boot order. It works on a
  Release build with `AreDevToolsEnabled = false` (Oct 6 2026). The window still opens in front of
  the user, so their mouse or pad still reaches it: a test screenshot once showed a tile the
  cursor was resting on.

## Architecture

- WPF (.NET 8) shell hosting the design's HTML/CSS/JS in WebView2, bridged by
  JSON messages (`UiBridge`): UI → host `{cmd}`, host → UI `{type}`.
- The launcher window must stay **opaque**. `AllowsTransparency` makes WPF host it
  as a layered window and the WebView2 then receives no mouse or wheel messages at
  all. Overlay menus paint their own dim over a screen capture instead.

## One file: what ships inside the exe

- **A release is `Loungepad.exe` and nothing else** (Oct 6 2026, the user's ask: no zip to unpack).
  `ui\`, `themes\` and `vortex-bridge\` are `EmbeddedResource`s named `shipped\<path>` (plus
  `shipped\LICENSE` and `shipped\NOTICE`), read back by `ShippedFiles` with forward-slash paths.
  Nothing is copied beside the exe any more; `bin\` and `publish\` still hold stale copies from
  older builds, which nothing reads. `WithCulture="false"` is on the item so a name like `x.de.js`
  cannot be taken for a satellite assembly.
- `.NET`'s own `IncludeAllContentForSelfExtract` was the wrong tool: it unpacks to `%TEMP%` at
  start and moves `AppContext.BaseDirectory` there, so the updater would install into the temp
  folder and `Assembly.Location` stops being empty (which is how a build knows it may update).
- `MainWindow.ServeShipped` answers `https://loungepad.ui/*` and `https://loungepad.player/*` from
  the resources through `WebResourceRequested`. **It has to use the overload with source kinds**
  (`CoreWebView2WebResourceRequestSourceKinds.Document`): with the plain two-argument filter the
  cross-site player frame's document arrived and its own `player.js` was never seen by the handler
  (scratch WebView2, runtime 154). **Every handler hears every filtered request**, so each checks
  its host first -- the data host's used to answer everything with a 404. Responses are
  `no-store`, so an update is never shown a page kept from the version before.
- **The page says `ready` on `DOMContentLoaded`, not from inside app.js.** The host answers it with
  the whole state at once, and app.js came before radial/actions/activity/playnite/onboarding: off
  disk those always finished loading first, but served from the exe (each request a trip through
  the UI thread, busy building that state) the state landed first, the handler threw on
  `withActionIcons`, and the first highlight stayed on the search box. Traced with CDP: state at
  59 ms, DOMContentLoaded at 66.
- `package.ps1` checks what it can no longer see in the bundle: that the publish folder holds only
  `Loungepad.exe` (anything else is something the release would go without), and that every file
  under the three folders is named in the Release `obj\...\<rid>\Loungepad.dll`'s metadata.

## Trust boundaries (the security pass of Oct 3 2026)

- **The page at `https://loungepad.ui` is the trust root**: anything running in it can post any
  bridge command. So nothing from the network runs there. `index.html` carries a CSP with
  `script-src 'self'` (no inline script anywhere in the UI, and a theme template's `<script>` is
  refused too), `UiBridge.OnWebMessageReceived` drops a message whose `e.Source` is not that origin,
  and `MainWindow` cancels any top-level navigation off it and swallows `NewWindowRequested`.
- **YouTube's player runs in `ui/player/player.html` on a second host, `https://loungepad.player`**
  (served out of the exe with no `Access-Control-Allow-Origin`, so the page can frame it and
  neither origin can fetch the other -- what `DenyCors` gave when it was a folder mapping; and
  `ui/player/` is NOT served on `loungepad.ui`, so YouTube's script can never be framed on the
  page's own origin), and `youTubeBackend` in app.js drives it through postMessage: `{cmd}` in, `{ev}`
  out, with a 250 ms `time` event because position and duration are read synchronously by the
  viewer. Both sides check `e.source` and `e.origin` on every message; the page's origin travels
  to the frame as `?o=`. The embed is on `youtube-nocookie.com`. In the preview the frame is the
  same origin (`player/player.html`), which still exercises the whole protocol; the headless
  harness in the scratchpad (`yt-relay.js`, puppeteer-core with `--autoplay-policy`) is how it
  was checked, because the pane is a hidden document and YouTube will not start a film in one.
- **The data host serves an allow-list of folders** (`ServedFolders`: covers, themes; `DataRoots`:
  trailers, achievements) with `Access-Control-Allow-Origin: https://loungepad.ui`. It used to
  answer for the whole data folder with `*`, which put settings.json one `fetch` away from any
  frame in the WebView.
- **Credentials never reach the page.** The four keys (IGDB secret, SteamGridDB, Steam Web API,
  RetroAchievements) are DPAPI-sealed in settings.json through `ProtectedStringConverter`
  (`"dpapi:<base64>"`; a plain value from an older file still reads and is sealed on the next save),
  `PushState` sends `AppSettings.ForPage()`, which replaces a set key with the marker `dpapi:set`,
  and `CopySettings` keeps the stored key when the marker comes back (`Secret(...)`). On the page a
  `secretRow` says "Set" or "Not set" and opens the prompt EMPTY: a new value replaces, empty
  keeps, "clear" removes. A key is never displayed again once entered.
- **Remote strings are escaped before they are markup.** Achievement icon URLs come from the stores
  and the metadata service and were written raw into `style="background-image:url('…')"`, which is
  attribute injection and therefore script in the trust root. `achIconStyle` escapes them and the
  host hands the page only `AchievementService.SafeIconUrl` (https, plain URL characters). The same
  rule for anything else from outside: `esc()` it, or set it through the DOM.
- **Nothing identifying goes to the metadata service.** `/v1/owned` and `/v1/achievements` are gone
  from the worker and from the launcher: both took a SteamID, and the worker let anyone with the
  URL read any account through its Steam key. Owned games and achievements come from Steam
  directly, with the user's own Web API key or the Steam sign-in (the token, `access_token=`, for
  GetOwnedGames; the ISteamUserStats calls do not take it at all -- see "Steam achievements" under
  Play sessions). A launcher with neither fetches no Steam achievements. The worker
  also requires `X-Loungepad-Client: 1` (a speed bump, not a secret), hashes the client IP for its
  rate limit, fails open when a cache write or the limiter fails, caches only 200 and 404, and only steps
  down an age-rating shape when IGDB's 400 names `age_ratings`. `invocation_logs` is off.
  **Deploying the worker breaks every build older than this one** (they send no header): ship the
  build first, or in the same sitting.

## Art and metadata

- Five shapes, and they are not interchangeable. Putting the wrong one in a slot is what
  made tiles look like they had the wrong game's art:
  - `CoverFile` portrait 2:3 (600x900) — portrait grid tiles
  - `BannerFile` 1.75:1 (616x353, Steam's capsule) — landscape tiles, the continue row,
    the now-playing card
  - `HeroFile` ~3.1:1 (1920x620, 3840x1240 at 2x) — full-screen backdrops and the detail page
  - `BackdropFile` 16:9 key art — only for a game with no hero at all; see the note below
    about why it is no longer preferred
  - `LogoFile` transparent wordmark, 1.2:1 or wider — the title as art on the detail page
- `bannerUrl` falls back to the cover, never to the hero. `backdropUrls` is a list —
  hero, then backdrop, then the tile — and never reaches the portrait cover. A 3:1 hero
  centre-cropped into a 16:9 tile throws away 43% of the width and what is left is
  background; a 2:3 cover hung across a screen is a column of box art.
- `MetadataService` fills the rest in after the scan, from Steam only: the CDN
  (`cdn.cloudflare.steamstatic.com/steam/apps/<appid>/…`) for art and the undocumented
  `store.steampowered.com/api/appdetails` for the description, developer, genres, release
  date, controller support and the Metacritic score. No key, no account, and keyed by app
  id so there is no title matching and so no chance of attaching the wrong game's art.
  Roughly 200 requests per 5 minutes per IP, hence the 1.5s gap between store calls.
- Every metadata field is cosmetic and every failure is swallowed. Offline, the launcher
  keeps whatever the scanner copied out of Steam's local cache (which is half-size: the
  cached "library_600x900" is really 300x450).
- Art the user picks by hand is written as `custom_<id>[_<slot>].<ext>`. That prefix is the only
  thing keeping it — nothing else ever writes that name, so neither a rescan nor an
  enrich can overwrite the file or point the game away from it. The cover has no suffix (it was
  the only pickable slot once and the name is on disk in everyone's install); the tile is
  `_tile`. The guard in `Assign` covers every slot, and `CustomSlots` seeds `filled` so a picked
  slot is not even downloaded for. `HasFetchedArt` has to accept a custom name too, or a game
  with hand-picked tile art reads as "no art" and rejoins the queue on every single start.
- Non-Steam games have only a title to match on, so both keyed providers go through
  `TitleMatch`, which accepts nothing short of exact-after-normalising (accents, `&`,
  apostrophes and punctuation folded; one trailing edition suffix discounted). "Portal"
  does not match "Portal 2" and never should — a wrong cover is worse than a missing
  one, because nothing about it looks wrong.
- Epic's manifest folder is not a games list: it also holds Unreal Engine, Quixel Bridge
  and Fab plugins, and the engine entries have a launch executable, so they pass the
  "has an exe" test. `IsEpicGame` reads Epic's own `AppCategories` instead — a game
  carries "games", an engine carries "engines".
- Credentials for IGDB and SteamGridDB live in settings.json in plain text. A rejection
  is latched per pass, so bad keys produce one log line rather than one per game, and
  games skipped because of it are left unstamped so corrected keys retry at once.
- Metadata is tiered so that almost nothing reaches a keyed source. Steam entries go by
  app id; non-Steam entries are first resolved against Steam's keyless
  `storesearch` endpoint (most games sold on Epic/GOG/Xbox are also on Steam); only
  what is genuinely not on Steam reaches the proxy in `proxy/`, which holds the IGDB
  and SteamGridDB credentials. A user's own keys, if set, take priority over the proxy.
- Newer Steam apps have stopped publishing to `cdn/steam/apps/<id>/<name>`. Forza
  Horizon 6 serves only `library_hero.jpg` there and 404s for the capsule and header.
  `appdetails` always names a working `header_image` under `store_item_assets`, so
  facts are fetched *before* art and that URL is the tile's last resort.
- The proxy's title matching is a courtesy; the launcher re-checks every response
  against `TitleMatch` itself. A proxy that is wrong, stale or replaced still cannot put
  another game's art on a tile.
- **Bumping `SCHEMA` does nothing on its own — the worker has to be deployed.** PEGI was added
  to `proxy/src/worker.js` and never shipped, so the live service kept answering without the
  field at all and no amount of launcher-side work could show a rating. `curl
  <endpoint>/v1/facts?title=<something-nobody-has-asked-for>` and look for `X-Cache: MISS`:
  a fresh answer that is still missing a field means the deploy, not the cache.
- `MetadataService.FetchVersion` is the other half of that. A build that learns a new field
  has to be able to go back and ask for it, and the 14-day freshness window would otherwise
  hold a library on the old answers. Bump it whenever a field is added or a picture starts
  being picked differently; everything stamped older is fetched again on the next pass.
- SteamGridDB's landscape grids are **920x430 and 460x215, and both are 2.14:1** — it has no
  1.75:1 shape at all. Steam's `capsule_616x353` is the only source of one, so the capsule is
  asked for *before* the service (`PreferSteamCapsuleAsync`) and only that one picture is. Ask
  the service first for the tile and every game in the library grows a blurred bed, because
  every tile is suddenly the one shape the box is not cut to. A game with no capsule --
  REANIMAL, Forza Horizon 6, Shotgun Cop Man all 404 for it -- still falls through to the
  service, whose 920x430 is twice the header's resolution in the same shape.
- Both keyed clients now ask by Steam app id when there is one, as the proxy always has: IGDB
  via `external_games.category = 1`, SteamGridDB via `/games/steam/<appid>`. They used to
  search by title regardless, so a user who supplied their own credentials was getting the
  weaker path — the opposite of what supplying them is for.
- `JsonElement.TryGetInt32` and friends **throw** on a JSON `null` rather than returning
  false — they return false only for a number that will not fit. A `"criticScore": null`,
  which is most games, took out that game's whole enrichment silently. Read numbers
  through `JsonNum.Int/Long/Double`, which check `ValueKind` first.
- IGDB carries several entries with byte-identical titles: two named exactly "DOOM"
  (1993 and 2016), more than one named "Fortnite". `TitleMatch` cannot separate those, so
  the tie is broken on popularity (`follows`, then `total_rating_count`) and entries with
  a `version_parent` are dropped. Without it, Fortnite came back as the delisted Chinese
  version, developer "Tencent Games".
- The worker's cache key carries a `SCHEMA` constant. Bump it whenever a fetcher's shape
  or picking rules change, or the old answers are served for another 30 days.
- **The worker's cache is a D1 table, not KV** (`loungepad-metadata-cache`, Oct 8 2026). KV's free
  tier is 1,000 writes a day, and a miss cost two (the answer and the KV rate counter), so ~250 new
  games spent the day. On Oct 7-8 one user's pass over a ROM collection several thousand strong
  did it in six minutes and ran on for five hours keeping nothing; worse, the counter failed open
  when KV refused it, so the limit was off too. Worked out from the KV keys alone: an entry's
  expiration minus its TTL is when it was written, and the first write after the midnight-UTC reset
  at 00:00:01 showed the pass had been running across it. Now: one row per miss (`WITHOUT ROWID`),
  the limit is Cloudflare's `[[ratelimits]]` binding (no storage), a daily cron deletes expired
  rows, and KV is read as a fallback until its entries expire (Nov 8 2026; then drop the binding).
  The account is on Workers Paid since the same day. `node tools\proxy-harness.mjs` is the
  worker's regression check (node:sqlite as D1, fake KV, limiter and upstreams; 27 checks), and
  `npx wrangler dev --local --test-scheduled` in `proxy\` checks what it cannot: D1's own SQL (its
  `?1` placeholders, which node:sqlite reads as names) and the real bindings.
- Tile art is never cover-cropped. It has the game's name burnt into it, close to the
  edges: a 2.14:1 header.jpg in a 16:9 box lost 18% of its width and REANIMAL lost the
  end of its own name. Polish's tile is cut to 1.75:1 (Steam's capsule exactly) and uses
  `contain`; the default theme decides per image in `artFit`, and only for landscape
  boxes — portrait box art is drawn to be cropped and still is.
- The hero is fetched at 2x (`library_hero_2x.jpg`, 3840x1240) where it exists. `.bd` is
  inset -80px, so on a 1920x1080 stage it covers 2080x1240 — from the 1x hero that was a
  2x upscale showing 54% of the width, which is what "zoomed in and blurry" was.
- **An art file's name has to carry the SOURCE as well as the slot.** It used to be slot plus
  extension — `_hdtile` + `.jpg` — so Steam's 616x353 capsule and SteamGridDB's 920x430 grid,
  which is also served as .jpg, resolved to the same path. The service overwrote the capsule in
  place; every later pass found a file *named* like a capsule, skipped the download because it
  already existed, and pointed the tile at 2.14:1 art. Twenty tiles with a blurred mat under
  them and not one line of code that said why. Names are now `<id>_st_<slot>.<ext>` and
  `<id>_sv_<slot>.<ext>`, so two sources can hold the same slot on disk and which one a game
  uses is decided by the code that runs, not by whoever wrote last. Renaming a suffix orphans
  the old files, which is a cache and harmless — but bump `FetchVersion` with it.
- Every download is shape-checked against the slot it is going into (`Fits` / `Bounds`), and a
  picture of the wrong shape is deleted rather than assigned. IGDB's `artworks` are whatever
  somebody uploaded: taking the first one for the backdrop put a 1080x1080 square behind
  DREDGE's screen and an 810x1080 portrait behind Hollow Knight's, and because the backdrop
  follows the highlight, walking along a row changed the shape of the picture every few tiles.
  The bounds are wide on purpose — they reject the wrong KIND of picture, not one a few percent
  off. Art we cannot decode (SteamGridDB serves .webp) is accepted rather than discarded.
- **`Fits` can tell that an artwork is 16:9 and cannot tell that it is any good.** Persona 3's
  first IGDB artwork passed the gate at 1920x1080 and is a blue diagonal, two floating leaves and
  31 KB of JPEG — against 873 KB of key art in Steam's hero. So `backdropUrls` puts the **hero
  first** and the 16:9 backdrop second, and the enrich only fills the Backdrop slot when nothing
  filled Hero. `library_hero` is curated and is the picture the store itself shows; the
  band-over-a-blurred-bed treatment handles its 3.1:1 perfectly well. A side effect worth having:
  every backdrop in the library is now the same shape, so the picture no longer changes proportion
  as the highlight moves.
- `setBackdrop` walks a LIST of candidates and drops to the next on a load failure. A name in the
  library can outlive the file it points at — a download rejected for being the wrong shape is
  deleted, and only a *successful* download ever replaces a name — so one stale entry used to
  cost the whole backdrop, permanently, through any number of refreshes. `Unassign` now clears the
  field when the file it names is the one being rejected, and the UI falls through regardless.
- `library_hero_2x.jpg` 404s for a lot of older apps (Celeste, Hollow Knight, TUNIC, Aseprite,
  Henry Stickmin all only have the 1x). That is what SteamGridDB's hero is for, and it is why the
  order is Steam 2x → service → Steam 1x rather than just "Steam".
- `heroUrl` stops at the tile. It must never fall back to the portrait cover: 2:3 art hung
  across a screen at its own aspect is a tall column of box art, and a flat colour is the
  better answer for a game with no wide art at all.
- **One priority order for art, best source first, and the first to fill a slot keeps it.** Steam's
  fixed-size library assets lead (`SteamPreferred`: capsule 616x353, library_600x900_2x,
  library_hero_2x), then the service for what Steam has nothing for and for every non-Steam game,
  then Steam's leftovers (`SteamFallback`: header.jpg, the 1x hero, logo.png). Facts still come
  from the service first — this is only about pictures.
- **`StoreRemoteAsync` has to honour `filled`.** It did not, while the Steam CDN loop did, so
  "the capsule gets first refusal" was true for exactly the two lines until the service ran and
  overwrote it. A rule that only some callers obey is not a rule; put the gate in the one place
  every caller goes through.
- **Three separate places key off the art naming scheme, and a rename has to move all of them:**
  `MetadataService` (writes the names), `MetadataService.HasFetchedArt`, and
  `LibraryStore.KeepBest`. KeepBest went on testing for the retired `_hd` after the rename, so it
  stopped recognising fetched art and every scan reverted the whole library to Steam's local
  half-size cache — which is what "the artwork keeps changing" was on restart, as opposed to the
  overwrite bug that caused it within a pass.
- `MergeScanned` has to list every fetched field, exactly like `CopySettings`, and it fails the
  same silent way. `BackdropFile`, `PegiRating` and `MetadataVersion` were all missing: the
  backdrop was dropped and refetched on every scan, a PEGI rating would have been wiped the
  moment the proxy started sending one, and the version stamp resetting to 0 made every game look
  stale so the whole library refetched on every single start.
- `setBackdrop` keys on the game id **and the resolved URL**. On the id alone it skipped the
  repaint whenever the highlight had not moved — including the push right after a metadata pass
  swapped the art out from under it, which left the element pointing at a file that no longer
  existed and the screen black until you moved.
- The shared service is asked **first**, Steam second as the fallback. What makes that safe
  is that both are asked by Steam app id when there is one: IGDB via
  `external_games.category = 1`, SteamGridDB via `/games/steam/<appid>`. An id lookup
  cannot answer with a different game, so proxy-first does not reintroduce the title
  matching that produced the wrong Fortnite. The title is still sent as the fallback for
  games the upstream does not index under that id.
- "Fallback" means fills gaps, not overwrites. Steam skips any art slot the service
  filled, and skips the text fields entirely when the service answered — keyed on whether
  the service answered, not on whether a field is empty, because a value left over from a
  previous run is also non-empty and testing emptiness would make stale data
  uncorrectable. Controller support is always Steam's: IGDB has no equivalent.

## Settings, themes and the keyboard

- A bundled theme is installed once and then kept up to date by `ThemeService.SyncBuiltIn`, which
  is keyed on a **hash of the shipped files** (`.shipped` beside the installed copy), not on the
  `version` in theme.json. It was keyed on the version, and the version takes a human to bump:
  a whole day of stylesheet changes shipped under the same "4.0" (Sept 2026), nobody's install
  was refreshed, and the launcher went on loading a copy made that morning -- "the option is
  gone" and "the still shows for a split second" were both reports against a stylesheet that no
  longer existed in the repo. Before that it skipped any folder that already existed, which was
  the same failure one step earlier. The folder is copied to `theme-backups` first, so an edited
  theme is recoverable rather than gone. Bump `version` anyway; it is what Settings shows.
- `CopySettings` has to list every setting. `HideCursorSystemWide` was wired through the UI, the
  host and the CSS but never copied, so the toggle moved on screen and was gone again on the next
  state push -- a whole feature that silently did nothing.
- Restore-defaults keeps `TvDeviceName`. Which screen is the television is a fact about the room,
  not a preference, and clearing it moves the launcher off the screen the user is looking at.
- The keyboard toggle is evaluated BEFORE the `serviceActive` gate, like the menu combo and the
  screenshot key: inside a focused game the rest of the pad is silent, and the keyboard is most
  useful exactly there. `KeyboardInGame` decides whether it may fire; closing a keyboard that is
  already up is always allowed, or an opted-out player could strand one on screen.
- In Press mode the toggle button is consumed on the press (`toggleFired` is set there), so it
  never also reaches the UI. That is why Start is a bad choice for it -- Start is the Menu button
  -- and why the Settings row warns about exactly that. Hold leaves the tap free, which is the
  point of having both.
- **The defaults are the Loungepad keyboard on View** (`KeyboardApp` "Builtin", `KeyboardToggleButton`
  "Back"; the user's call, Sept 30 2026). View needs no warning on Press: the library claims it for
  search, which raises the keyboard anyway. The "Opens on" row warns instead when the toggle is part
  of the menu or screenshot combo, since the press fires the keyboard on the way into it. The
  touch keyboard's Gamepad-layout tip is gone from the couch guide and the setup's sign-in step with
  it; the TabTip warning under "Keyboard app" still says it, for anyone who picks TabTip.

## Controllers and the button glyphs

- Non-XInput pads (DualSense, DualShock 4, Switch Pro, generic) come in through **Raw Input on the
  main window** (`HidGamepadReader`, registered in `OnSourceInitialized` with `RIDEV_INPUTSINK`)
  and are parsed with hid.dll into `XINPUT_GAMEPAD`, so `GamepadService` runs one loop for every
  pad. INPUTSINK is the whole reason it is Raw Input and not Windows.Gaming.Input: the menu combo,
  keyboard toggle and screenshot key all have to work with a game in front.
- Xbox pads show up in Raw Input too, with `IG_` in their device path. They are skipped there and
  left to XInput, which has the Guide button and the battery; reading both doubles every press.
- Through hid.dll, only the report whose id matches the X axis's caps is parsed. **Sony pads are
  read by hand instead** (`ParseSony`, `SonyLayout`): the touchpad lives in the vendor bytes after
  the described part, which hid.dll cannot name. The DualSense's USB report is 0x01/64 bytes with
  the body at offset 1; its Bluetooth full report is 0x31/78 bytes with the same body at offset 2.
  Touch points are at body+32 and +36: a contact byte whose top bit is SET while nothing touches,
  then x (12 bits) and y (12 bits) packed into three bytes. Verified on USB: an idle pad logs
  `96 73 57 15 80 00 00 00`, i.e. no finger, last position x=1907 y=341. The DualShock 4 layout
  (body at 1 on USB, 3 on Bluetooth report 0x11; touch at +34) is unverified.
- A DualSense on Bluetooth sends a 10-byte 0x01 with no touch data until something reads its
  calibration feature report 0x05, which flips it to 0x31 for good. Steam does that read, which
  used to leave the pad unreadable here; now `EnableFullReports` does it ourselves on open
  (read/write handle, `HidD_GetFeature`), and 0x31 is parsed natively. Unverified on hardware:
  this PC's DualSense was on the cable. The short report still goes through hid.dll if the switch
  is refused, so the pad works either way, minus the touchpad. A Switch Pro on USB sends nothing
  at all without Nintendo's handshake; Bluetooth is the way.
- Touch travel is only counted while the same numbered contact continues, and the reader
  accumulates it under its lock; `Snapshot()` drains it, so a poll that skips the touchpad branch
  (menu up, service inactive) simply drops that travel. A touchpad press is a real `SendInput`
  click, held while the pad is held so a drag works; two fingers make it a right click.
  `ReleaseTouch` runs on every path that stops reading the pad as a mouse, so a press can never
  outlive its context as a stuck button.
- **The pointer moved when the pad was pressed**, because a finger rocks a few units as it works
  the switch. Three things hold it still: a rest deadband (`RestDeadband`, 14 units, ~0.4 mm) that
  a stopped finger has to cross before the pointer follows it again, with that travel dropped
  rather than replayed; a freeze on movement for 160 ms after a press and 120 ms after a release;
  and the deadband accumulator being leaky (x0.9 per poll), so a resting thumb's jitter never adds
  up to a false start while a slow deliberate push still gets through. Gain follows speed
  (0.45x to 2.2x of `TouchpadSensitivity`), which is what made "too sensitive" go away without
  making a flick slow.
- **All touchpad behaviour lives in `TouchpadGestures`**, one state machine per touch: pointer,
  press, tap (deferred 230 ms so it can become a double click or a drag), tap-and-drag with a
  350 ms drag lock across lifts, two-finger tap, two-finger scroll with rails and coasting, and
  pinch as Ctrl+wheel. `GamepadService` only feeds it a `TouchFrame` per poll and calls `Reset()`
  on every path that stops reading the pad as a mouse.
- The reader reports the fingers' **average** travel over the contacts that carried on from the
  last report, plus the change in distance between two fingers (the spread). A finger landing or
  lifting never shows up as travel.
- **Test gestures with the harness, not by hand**: `Output` and `MoveCursor` on the class are
  replaceable, and a scratch console project that compiles `TouchpadGestures.cs`, `AppSettings.cs`
  and `NativeMethods.cs` can replay scripted finger traces at 8 ms and record what would be sent,
  without a single real click. Apply finger noise **per poll**; noise drawn once per segment is a
  steady slow slide, which correctly moves the pointer and made half the first run look broken.
  21 cases pass under 8 noise seeds.
- Scroll rails: a two-finger swipe that starts with one axis over twice the other is locked to it
  for the gesture. Without them the fingers' wobble scrolled a page sideways while reading down it.
  A coast stops below 250 wheel units a second: slower than that it only dribbles out a step every
  tenth of a second, which reads as stutter.
- Tap-to-click measures the touch's NET travel (start to end), not the sum of its deltas: at
  250 reports a second a still finger's jitter sums to more than a tap's allowance in 200 ms,
  and a tap that never registers looks like a broken pad. A tap is refused if the pad was pressed
  down during the touch, so a click never doubles.
- Every DualSense touch contact carries an incrementing id; the log's touch bytes from two runs on
  one evening went from id 22 to ids 109 and 88 with different positions, which is the pad being
  used between them. A cheap check that the decode is tracking real fingers.
- **A touchpad click reaches the page as a real mousedown**, which is exactly what the page reads
  as "the mouse is in use" to swap the legend to key caps. The host raises `TouchClick` just
  before sending it, the bridge pushes `padClick`, and the page both stamps the moment and puts
  the pad family back -- the two can arrive in either order, and that handles both.
- Button maps are by HID button number. Sony's order (Square, Cross, Circle, Triangle, L1, R1, L2,
  R2, Share, Options, L3, R3, PS) is verified against the DualSense's own descriptor and is also
  the generic default. The Switch map (B, A, Y, X, L, R, ZL, ZR, −, +, LS, RS, Home) is from the
  reverse-engineering notes for report 0x3F and is **unverified on hardware**; it maps by position,
  so Nintendo's B is the launcher's A. Right stick is Z/Rz when both exist, else Rx/Ry; triggers
  are Rx/Ry only in the first case.
- **The pad's own screenshot button takes a screenshot over a focused game** (`ShareButtonScreenshot`,
  on by default; the user's ask, Oct 2 2026): Create on a DualSense (Share on a DualShock 4), Capture
  on a Switch Pro. The Xbox Share button never reaches an application -- Windows and Steam answer it
  themselves -- so nothing is done for an Xbox pad and the combo (`ScreenshotCombo`) is still its
  way. Capture is `XINPUT_GAMEPAD_SHARE` (0x0800, the one bit XInput leaves unused; `PadButton.Share`,
  the Switch map's 14th), masked out of combos, bindings and recordings (`BindableButtons`). Create
  is `Back` because it is also the pad's View button, so `ShareMask` keys on the family: "playstation"
  → Back, "switch" → Share, nothing else. The press is skipped when the keyboard toggle just spent
  it (View on Press with "Show while a game is running" on) and when View is part of the menu or
  screenshot combo; the Settings row warns for both. **Which key is the game's**: `ScreenshotRequested`
  goes to `MainWindow.TakeScreenshot`, F12 (the Steam overlay's key) for a focused `steam:` game and
  Win+PrintScreen for everything else, the combo included -- it used to send F12 regardless, which in
  a browser opens the developer tools. **Verified by the user on a DualSense over a game** (Oct 6
  2026, "working as expected"); the Switch Pro's Capture is still untried on hardware.
- "Which pad is driving" is decided by movement against an **anchor** (`StickNoise`, 5% of
  travel), not the previous tick: a DualSense streams a report every 4 ms and its sticks rest a
  few percent off centre, so a per-tick delta never crossed the threshold on a slow push and a
  zero anchor announced an untouched pad at startup. The first reading seeds the anchor.
- `PadUsed` fires on a change of pad *and* when a pad is picked up after 1.5 s of silence. The
  page switches its legend to the keyboard on any keypress or mouse click, and that event is what
  switches it back. Mouse **movement** never switches the legend: the left stick moves the real
  Windows pointer.
- The page keeps two families: `inputFamily` (what the legends draw) and `padFamily` (the last
  gamepad seen). Settings rows that name gamepad buttons draw `padFamily` through `[data-pad]`
  slots, or "Left click button: Enter" would appear. Every drawn button is a `[data-btn]` slot and
  `paintButtons` repaints them all in place; nothing is re-rendered for a family change except
  Settings, whose hints name buttons in words.
- Stored button names stay XInput's (`A`, `RB`, `Start`, `Back`, `Guide`); `canonBtn` folds
  Start/Back to Menu/View for drawing. Only the picture changes with the pad.
- The published app now gives the WebView keyboard focus on `Activated` and after navigation, and
  `AreBrowserAcceleratorKeysEnabled` is off so F5 cannot reload the launcher. That is what made
  keydown fire at all.
- The browser preview's `key` action for "Return" arrives with an empty `code` **and** an empty
  `key`; use "Enter". Every other key arrives with `key` set and `code` empty, which is why
  `KEYMAP_BY_KEY` exists beside `KEYMAP`.
- **On the library, Tab is Settings, ` is Stats and Ctrl is a game's options; Esc is Back** (the
  user's calls, Oct 1 2026: first Tab/Esc for Stats/Settings because [ and M "felt random", then the
  same day Esc wanted back as the one cancel key). `LIBRARY_KEYS` applies only while
  `libraryTakesKeys()` -- the library with nothing over it and no search open. Y, M and [ still work.
  The hint bar draws those keys through `LIBRARY_KEYCAPS`: a slot's `data-kb` is the keycap a
  keyboard sees in place of the button's usual one (`slotIcon`), so the same slot still draws Y, LB
  and Menu for a pad. Only the library: the key picker's own legend has X as Ctrl and Y as Shift.
- **Ctrl fires on the release, and only if nothing else happened while it was held** (`ctrlTap`):
  another key, a click, a wheel (the touchpad's pinch is Ctrl plus the wheel) or a blur cancels it.
  On the press it would open the game menu on the way into Ctrl+Shift+Esc or any Ctrl chord.
- A DualSense on the cable reports "connected, charge unknown" (a pad icon with an empty bar):
  neither WinRT nor XInput can see it, and the Bluetooth lookup is only asked about the HID pad's
  own container, so an Xbox pad on the same PC cannot answer for it.

## Scrolling and held keys

- **Nothing uses `scrollTo({behavior:"smooth"})`.** Each call restarts Chrome's eased animation from
  standstill, so a run of steps lurched and stalled, and a held key could not keep up at all.
  `animateScroll` is a critically damped spring (`SCROLL_OMEGA` 22, settles ~250 ms, no overshoot)
  that is retargeted mid-flight without losing velocity; simulated at 60 fps it never drops below
  ~1300 px/s during a held D-pad run and trails the highlight by under one row. A scroll it did not
  make (wheel, stick, drag) is detected by `scrollTop` differing from what it last wrote and wins.
- `revealOffset` measures against `scrollTarget` (where the list is heading), not `scrollTop`:
  mid-glide the two differ, and the current position asked for the same scroll twice.
- The backdrop is deferred during a fast run (`scheduleBackdrop`, 170 ms): each change decodes a
  hero and re-blurs a screen-sized layer, the most expensive thing in the frame. Direct
  `setBackdrop` calls (detail page, Settings) cancel a pending one.
- Held keys are paced to one step per 85 ms and the repeats between are **dropped, not queued**.
  Windows repeats at ~30 Hz; handling every one outran the paint and the screen froze until the
  key was released. Measured: 31 repeats in a second became 11 steps; a step costs ~3 ms with
  313 tiles.
- **The right stick does not send wheel notches while the launcher is in front.** A notch is a
  100 px jump and they came up to 18 a second on whichever poll crossed the line, so the list
  lurched however smoothly the stick was held. `GamepadService` sends the stick's speed instead
  (`UiScroll`, notches per second; pushed on change at most every 16 ms, re-sent every 100 ms,
  0 on release), and `onStickScroll` moves the list by speed x frame time on every frame, easing
  the speed over 80 ms. A speed older than 250 ms counts as zero, so a lost stop cannot leave it
  running. Outside the launcher, and with the on-screen keyboard driving, it is still wheel
  notches -- that is what Windows apps expect. Horizontal is still HWHEEL: the carousel steps
  focus per notch, which is what it should do.
- `nextFrame` is `requestAnimationFrame` raced against a 50 ms timeout, so a per-frame loop keeps
  going in a hidden window and in the preview (20 fps there). Measure per-frame motion by wrapping
  `window.nextFrame`; sampling `scrollTop` on a timer aliases against those frames.
- `mousemove` is ignored unless `screenX/Y` changed: the browser raises it when content scrolls
  under a parked cursor, and that flipped the page to pointer mode mid-run.
- **The recents are the top of the page, not a frozen row** (the user's call, Oct 1 2026: the grid
  had under half the screen). In Shelf `.lib-body` is the scroller and the grid is as tall as its
  rows (`.lib-body .grid-scroll`), so the recents scroll away as the highlight walks down; a theme
  that slots the regions out of `.lib-body` keeps the grid as its scroller. `libraryScroller()` asks
  the CSS which it is, and everything that used to name `#gridScroll` as THE scroller (wheel routing,
  B's scroll to the top, `renderLibrary`'s kept position) goes through it. `revealOffset` brings a
  section's heading back with its first row (`reach`), or walking up onto Continue or All games
  left the heading cut under the top edge.
- **Loungepad's library is one page too** (`.tv-view` is the scroller; theme 4.5, the user's call,
  Oct 6 2026: "treat the recents like any other row"). It used to be a fixed stack slid up by
  `translateY` when `data-focus-region` became "grid", with the grid scrolling inside it -- and only
  a D-pad step changed the region, so a wheel or the right stick scrolled the grid inside the 40px
  peek while the recents sat still above it. The browsing look (art dimmed, title faded, shelf at
  full opacity) now keys on `.tv-view.scrolled` (`watchScrolled`), so every way of scrolling gets
  it. Three things make the page behave: the recents carry `scroll-margin-top: var(--view-h)`, which
  `revealOffset` honours, so walking up onto ANY of them lands on the top of the page (the
  first-item snap alone missed the rest of the row, and the Playing card is first while a game
  runs); the end snaps count only drawn focusables and apply to the whole first row, and only when
  it is in view at 0 (the hidden Playing card had been "the first" all along, and with nothing
  played the first tile sits in the peek); and `revealSearchResults` scrolls the grid's top to
  the top of the view when a search opens below the fold, with the shelf kept `min-height:
  var(--view-h)` while searching so a short result list can get there. B also scrolls the page
  home when the highlight is already on the recents but the stick moved the page.
- **The browsing look changes with the motion, and only by fading** (theme 4.6; "a small flicker
  when we scroll up", the user's report, Oct 6 2026). Two causes, both fixed. `watchScrolled` marked
  by `scrollTop`, so a glide back to the top dropped `.scrolled` on its last frame and the whole
  look changed on a screen that had stopped; it marks by `scrollTarget` now, so the drop is on the
  first frame (checked: 770 of 842, the D-pad's and the wheel's glides alike). And the browsing
  wash was `#backdrop::after` with a different background, and a gradient does not transition into
  another gradient: the scrim swapped in one frame while the art took 380ms to blur. The resting
  scrim now only fades (opacity), and the wash is `.tv::before` (z-index -1 inside the isolated
  screen), fading in against it. The edge mask still toggles with `.scrolled`: leaving it on at
  rest would make `.tv-view` a backdrop root and take the blur out of the Playing chip.
- **The page never rests between the art and the grid** (the user's calls, Oct 6 2026: first the
  jump down, then the same snap on the way up). A move down from above the grid lands on
  `gridJumpStop`; a move up that would end above it lands on 0. `libraryNav` jumps down on a step
  from outside the grid into it (its step back onto the recents already lands on 0, through their
  scroll-margin); the wheel router and the stick loop jump both ways (`gridJumpFor(sc, dir, by)`,
  `by` being how far the move would go, and the stick's coast counting in the direction it moves).
  A wheel or stick push the same way as a running jump is held off (`gridJumpRunning(sc, dir)`), or
  the animator would hand over to it halfway; the other way is answered. Never in Shelf: its grid
  starts on screen, and `gridJumpStop` is null for it.
- **`gridJumpStop` is the section top less `REVEAL_MARGIN`, not the grid's top edge.** It is exactly
  where `revealOffset` puts the page for a highlight on the first row. Landing on the edge (872)
  left the reveal's 30px to be made by the first Right along the row (842), which read as the row
  settling a moment after it had landed (the user's report). Search reveals to the same stop.
  Checked in the preview: Right x5 along the top row stays at 842, the held stick goes 1250 → 846
  → 0 without resting in between, wheel-up deep in the grid scrolls natively until a notch would
  cross the stop.
- **Rows scrolled out of a list still have real rects.** In Loungepad they used to sit behind the
  recents row, which parked over the top of the grid; it is a row of the same page now. Holding Up, the scroll's glide trails the
  highlight; 22-40 px of lag (stage px) made a recents tile score nearer than the grid row above,
  so the highlight hopped to the recents for one step (the row slid down) and the next Up dived
  into a hidden row behind them (it slid back up). Only a fast run did it; spaced presses never
  did. `navMove` now walks a scroller to its end before leaving it, and never lands on something
  wholly scrolled out of a scroller it is not already in (`scrollerLookup`, memoised because the
  grid is hundreds of tiles). Reproduced in the windowed build with a held key (a key-down every
  115 ms, the host's D-pad repeat) and frame captures; in the preview by setting the grid's
  `scrollTop` 30 px past the settled reveal and calling `navMove("Up")`.

## Screens and the switcher

- Library is the only top-level screen. Collections became a filter category (`F.collections`,
  a set of collection ids) and Settings is reached with the Menu button from the library —
  `TAB_DEFS` holds one entry and there is no section cycling, so LB/RB are free.
  Collections themselves still exist and are still made from the game menu.
- A collection can be deleted while it is still being filtered on, so stale ids are pruned
  when state arrives. Left alone the filter matches nothing and the library looks empty for
  no visible reason.
- **The Power Wheel's Close spoke is "Close game" over the running game** (the user's ask, Oct 2 2026).
  `PushOverlay` carries `targetIsGame` (`_overlayTargetLive && _launcher.OwnsWindow(_overlayTarget)`),
  the page keeps it in `overlayTargetIsGame` (declared in app.js, like the other overlay variables),
  and `radialLabel` / `radialDesc` read it. Activating it sends `closeGame`, the in-game menu's own
  route, not `windowAction close`: `closeGame` thaws a paused game first (a frozen process never
  reads a WM_CLOSE) and lands on the library, and the overlay is dropped the way the in-game menu
  drops it, without a `closeOverlay` that would hand the foreground back to a game being asked to
  quit. Opened from the in-game menu's Power Wheel tile there is no new push, so the flag from the
  `ingame` push carries over.
- `renderMenu` paints its highlight onto `listEl.closest("[data-focus-scope]")`. An overlay
  without that attribute gets no highlight at all — which is what was wrong with the power
  wheel's submenus. It also only paints rows that are focusable, and nothing inside a hidden
  overlay is, so an overlay must be made `.active` *before* it is rendered.
- `repaintFocus` must have a branch for every overlay that can own input, ordered as in
  `handleInput`. A missing branch repaints the screen underneath and leaves the visible menu
  unhighlighted.
- The window switcher follows the Alt+Tab rules, and the one that matters is DWM's cloak flag:
  a suspended Store app stays "visible" in the old sense, which is why ApplicationFrameHost and
  TextInputHost appeared as if they were programs. Do **not** blocklist ApplicationFrameHost —
  it owns the frame window of every Store app, so blocking it hides Settings and Windows
  Security too. Cloaking already separates the ghosts from the real ones.
- A minimized window reports a 160x28 rect wherever Windows parks it. Any "too small to be
  real" test has to ask `IsIconic` first, or it eats exactly the windows the switcher is for.
- Thumbnails come from `PrintWindow` with `PW_RENDERFULLCONTENT`, which is the flag that makes
  it work for DirectComposition and UWP surfaces; without it browsers and Store apps come back
  blank. They are captured off the UI thread and pushed one at a time *after* the list, because
  PrintWindow waits on the target's message loop and the switcher is often opened precisely
  because something is stuck.
- An overlay must be made `.active` **before** it is rendered, everywhere — not just the power
  wheel. `openFilter`, `openGameMenu`, `openCollect`, `openManage` and the confirm all rendered
  first, so the first row was never highlighted until something moved.
- Hover handlers must not rebuild the list they are on. `renderSettingsNav` replaces every tab
  node, and a node destroyed between mousedown and mouseup never raises a click — which is why
  the settings categories could not be clicked at all while the option rows could. Set focus and
  `paintNav()`; the rows already did exactly that via a guard.
- `TAB_DEFS` is empty. The top bars still render for the clock and title count, they just have
  no tabs in them.
- A minimized window cannot be photographed: PrintWindow answers true and hands back an empty
  bitmap. `WindowService` keeps the last picture of each window (`_thumbs`, pruned of dead
  handles on every list) and falls back to the window's icon, flagged as `IsIcon` so the UI
  draws it inside the box rather than cover-cropping a 32px square into a smear. Alt+Tab shows
  a real picture because DWM keeps the last composed frame; there is no public way to read that.
- Tile art is flush to the tile; only the corners are rounded. Insetting it to keep its corners
  clear of the radius did stop the clipping and left a surface-coloured mat round every tile, so
  each read as a picture pasted onto a rounded card. A radius only ever takes the four corners --
  the sides were being lost to a crop, and the fix for that is the box being the shape of the
  picture, not a border around it.
- Art that still does not fill its box gets a bed, not black bars: a blurred, dimmed copy of the
  same picture behind the fitted one. A uniform grid cannot give each tile the shape of its own
  art, and the two shapes in play are 1.75:1 and 2.14:1, so a few tiles will always have a strip
  left over. Polish builds the bed in its template (`.tv-bed`); everything going through
  `applyArt` gets `.art-bed` + `.art-top` built on demand, and ONLY when the fit is `contain` --
  so the great majority of tiles carry no extra layer and no blur.
- Both layers are children. A child always paints above its parent's background and never below
  it, so the bed cannot be the element's own background; and a `filter` on the element would blur
  the sharp layer along with the bed. The bed is also scaled ~1.15, because a blur feathers its
  own edges and a feathered edge inside a rounded box reads as a halo.
- `applyArt` takes an optional `fit`. Scenery must always `cover` — a full-bleed backdrop has no
  edges of its own to protect, and `artFit` letterboxed the detail page's 3:1 hero inside its
  16:9 box, which is where the black bars came from. Only tiles are worth fitting.
- Full-width art is cut to the art, not to a number. `applyArt` measures what loaded and writes
  `--art-aspect` on the element; the detail hero and Polish's backdrop are `aspect-ratio:
  var(--art-aspect, 3.1)` with `max-height: 100%`. A fixed height only ever suited one source:
  62% of a 16:9 stage is 2.87:1, so Steam's 3.1:1 hero lost the sides and IGDB's 16:9 artwork lost
  42% of its height.
- **A height floor on the backdrop cannot buy height without buying width.** `.bd` has `left` and
  `right` pinned and an `aspect-ratio`, so `min-height: 72%` grew the *element* to 1605px inside a
  1280px screen and `#backdrop`'s `overflow: hidden` threw away everything past the right edge — a
  fifth of the picture, off one side only, and on a 4K panel the fifth that survived was being
  upscaled 1.25x to get there. Both halves of "cropped and blurry", from one number. `--bd-fill`
  now defaults to 0 and `max-width: 100%` means raising it costs the bottom of the picture rather
  than the sides, which is the cheaper edge: key art puts its logo across the middle and the
  element is anchored `top`. The sharp layer hangs at its own height and the blurred bed carries
  the rest of the screen, which is what the bed is for.
- A mask has to fade to nothing at the element's own edge. Polish's stopped at 99% of a band that
  was only 57% tall, so the art was still clearly visible where it ended -- which is what read as
  cut rather than dissolved.
- The rest of the screen is the same picture, blurred and scaled past the edges --
  `#backdrop::before`, fed by `--bd-image`, which `setBackdrop` writes alongside the art. It is
  the trick tvOS and Plex both use, and it is the only way to have the art fill a screen and stay
  sharp: a 3.1:1 hero stretched over 16:9 shows the middle 57% of itself and nothing else. The
  bed is blurred, so its resolution never matters. Polish's bottom scrim had to come off full
  opacity to let it through -- it was written when there was nothing behind it to show.
- `BackdropFile` is 16:9 key art and is what `backdropUrl` prefers for anything filling a whole
  screen, falling back to the 3.1:1 hero. Only IGDB publishes art of that shape; Steam's
  `page_bg_generated_v6b` is 16:9 but 28-63 KB of auto-generated blur. IGDB's artwork used to be
  written into the Hero slot, which is what made a backdrop's shape unpredictable -- the same slot
  held 3.1:1 for Steam games and 16:9 for everything else.
- `artFit` must measure the CONTENT box, not `clientWidth/clientHeight`. `background-origin:
  content-box` -- what holds tile art clear of the rounded corners -- makes `cover` size against
  the content box, so measuring the padding box read the continue row as 1.79:1 when it was 1.86:1;
  a 1.75:1 capsule then looked like a perfect fit and lost a strip off each side. That was the
  "the sides are cut off, not just the corners" report, and rounding was not the cause of it.
- Every landscape box is 1.75:1, which is Steam's capsule exactly: `.cont-art` 300x172 (but see
  below: Shelf's recents now widen to their picture), `.playing-art` 366 wide against the card's
  210, Polish's tile `--tile-w / 1.745`. The grid tile
  is 174x261, which is 2:3 -- box art's own shape -- and nine of them plus eight 24px gaps is the
  same 1760 run eight 199px ones made. `GRID_COLS` and `.grid-item` have to move together.
- **The critic score is Metacritic's or nothing** (the user's call, Sept 2026). Metacritic has no
  free API; the only free source is the `metacritic` block in Steam's `appdetails`, which
  `FetchSteamFactsAsync` takes whenever Steam answers -- a score or null, so a dropped one goes
  too. IGDB's `aggregated_rating` used to fill the gaps labelled "IGDB critics", and because the
  service runs first it even beat Steam's real Metascore on installed games; it is no longer asked
  for. `LibraryStore.Load` drops any score whose `CriticSource` is not "Metacritic". Coverage,
  checked live: 9 of 12 big Steam games carry it; Black Myth: Wukong, Monster Hunter Wilds and
  EA FC 25 do not. Wikidata holds ~11,000 Metacritic scores (P444 by Q150248) but had none of
  those three, so it was not added. ROMs have none: Steam is never asked for one.
- The score and the age mark sit together on the right, level with the stats hairline: the corner
  of a game's box, which is where an age mark has been printed for thirty years. The age mark is
  last, so it is what sits in the corner. Positioned absolutely out of the column: the stats are
  anchored to the bottom of the page and the ratings are not always there, so in flow a game with
  a score would put its Play button somewhere different from a game without one.
- The wordmark gets a pool of shade of its own (`.detail-titleblock::before`, only when the logo
  is showing). A logo is whatever colour its designer chose and the key art behind it is the same
  palette -- Cyberpunk's yellow on yellow, and the reason a screen-wide scrim is not the answer:
  anything heavy enough to separate them flattens the picture everywhere else.
- IGDB moved age ratings from numeric enums (`category`/`rating`) to references
  (`organization`/`rating_category`), and APIcalypse fails the WHOLE query with a 400 for one
  unknown field -- so guessing wrong costs the description and the score as well as the rating.
  Both the worker and `IgdbClient` try the shapes in order, step down only on a 400, and remember
  what worked. The last shape asks for no age fields at all, so there is always a query that runs.
- A game carries ratings from several boards at once and their enums overlap: ESRB's rating 4 and
  PEGI's rating 4 are different things. Identify the board before reading the number.
- Only an ISO release date is reformatted, and its three numbers are read out of the string rather
  than through `Date`. Parsing "2024-02-02T00:00:00Z" reads UTC and printing reads back local, so
  west of Greenwich every release came out a day early. Steam's own "2 Feb, 2024", "Q1 2024" and
  bare years are passed through: reformatting them means guessing a day.
- `.detail-main` is anchored with `margin-top: auto`. A game with no metadata has a much shorter
  column than one with everything, and centring put the Play button in a different place on each.
- The detail page uses `LogoFile` when there is one, falling back to the text title. Both stay in
  the DOM; `renderDetail` toggles `hidden`.
- `revealOffset` snaps to the ends for the scroller's first and last focusable. Nothing focusable
  above the first row means the space above it is not slack, it is that row's section heading --
  clearing REVEAL_MARGIN for the focus glow scrolled the heading off the moment you walked back up,
  which is why the first category in every Settings tab kept vanishing.
- A on a settings category reads the category off the element, not off `settingsTab`. Hovering a
  category highlights it without selecting it, so A opened whichever one had last been activated:
  the highlight said Keyboard and Controller's rows appeared, which reads as A doing nothing.

## The first-run setup

- **`OnboardingVersion` decides whether it opens**, and the page owns the number
  (`ONBOARDING_VERSION` in onboarding.js). 0 is a new install. `SettingsStore.Load` reads a
  settings.json that has no `OnboardingVersion` key as 1: that install predates the setup and was set
  up by hand. Raise `ONBOARDING_VERSION` only for a step worth showing to existing installs; they see
  it once. `CopySettings` carries the field and "Restore default settings" keeps it, or a restore
  would bring the welcome back.
- It is a view (`view === "onboarding"`, `#screen-onboarding`) over the blurred library, like
  Settings, and its rows are Settings' own builders: `storeAccountRows`, `emulationRows`,
  `menuComboRows`, `xboxButtonRows` were split out of `allSettingsRows` for it. A change to one of
  those rows is a change in both places, which is the point. `onbSet` is its `set`.
- The Look step sets `body[data-onb-step="look"]`: the box moves right, the rail goes, and the library
  is NOT blurred, because the library behind is the preview. The accent picked there is the app-wide
  `accentColor` with the current theme's bag accent deleted, so it holds under every theme.
- **The scan reports its steps** for the rail: `BeginSteps` lists every step of a non-quiet scan up
  front (a switched-off source is "off"), `SetStep` moves one, `FailOpenSteps` marks the rest failed
  when the scan throws. `scanProgress` rides every state push too. `LibraryScanner.ScanAll` takes the
  reporter; nothing else about the scan changed. The page names the steps (`onbScanLines`).
- Playnite's and Vortex's steps appear only when the host's `onboarding` answer
  (`ProbeFirstRunAsync`) says Playnite has a library or Vortex is installed; it arrives while the
  welcome is still up. The Vortex step's background look is `ModService.SummaryAsync("peek")`, which
  changes nothing: no start, no waiting, and the extension is not copied into a Vortex that never had
  it (`VortexBackend.PeekAsync` only writes this run's token for an install connected before).
  "Connect" and "Restart" are the Mods screen's own `EnsureBridgeAsync` / `RestartAsync`, on a press.
- **The PIN check is `KeyCredentialManager.IsSupportedAsync()`**, true only once a Windows Hello PIN
  exists. Verified true on this PC, matching `...\LogonUI\NgcPin\Credentials\<SID>`; the false case
  was not seen here. The step polls it every 4 s (`onboardingPin`) so a PIN set up in Windows Settings
  shows the moment Loungepad is back. "Set up a PIN" parks the launcher, opens
  `ms-settings:signinoptions`, and moves the first ApplicationFrameHost/SystemSettings window that
  takes the foreground within 8 s onto the TV.
- The preview opens the setup only with `?onboard` in the address (`/ui/index.html?onboard`); the
  mock's scan steps, launchers, Playnite, Vortex (connect → needs a restart → connected) and PIN
  (set after "Set up a PIN") are all simulated. In the real app it is Settings → General → First-time
  setup: from the library, M, Right, then Up three times (Up off the first row wraps to Exit).
- Verified in the windowed Debug build with keys (Sept 30 2026): every step renders over the real
  bridge with this PC's launchers, store counts, RetroArch playlists, PIN and Vortex 2.7. **Close a
  test instance with WM_CLOSE, not Stop-Process**: `LibraryStore.Save` is a plain WriteAllText, and a
  kill during the metadata pass's 20 s checkpoint can truncate library.json. Back it up first.

## Running two copies

- `App.OnStartup` takes a `Loungepad_SingleInstance` mutex, and a second copy used to just
  `Shutdown()`. That happens **before** `Log.Info("---- Loungepad <version> starting ----")`, so launching
  the exe while a copy was already running did nothing at all: no window, no error, and not one
  line in the log to say a start had been attempted. Whatever the running copy happened to be
  showing then got blamed on the build. A second copy now signals `Loungepad_ShowExisting` and the
  running one calls `Unpark()` and logs that it did.
- So: **never leave a test instance running.** The user launches
  `publish\Loungepad.exe` by hand and from the HKCU Run key, and a copy left behind after a test
  silently swallows every launch of theirs. Kill it in the same turn it is finished with.
- A gap in the log where a start should be is the signature of this. If the user reports something
  and the log has no `---- Loungepad <version> starting ----` for it, they were looking at an instance
  somebody else started.

## The website

- `website/` is loungepad.app: a plain static site (index.html, site.css, site.js, glyphs.js, img/)
  built from the Claude Design handoff in `website/design/` (`HANDOFF.md` says how to read the
  `.dc.html` files). The design stays as the source of truth for copy and layout; the site is built
  from it by hand. **All the copy is in index.html**, including each feature's long description (a
  hidden span in its tile that the viewer reads), so search engines and no-JS visitors get it;
  site.js only moves it. `glyphs.js` is the Glyph component's drawings, which are `ui/glyphs.js`'s --
  keep the two in step. Every glyph is pre-rendered as Xbox in the HTML and repainted for the
  family in hand.
- `img/` is made by `tools/website-images.js` from the trailer project's stills
  (`trailer/out/screens/*.png`, 2560x1440): five widths in AVIF, WebP and JPEG, named
  `<key>-<w>.<ext>`. The widths live in three places -- that script, index.html's srcsets and
  `WIDTHS` in site.js -- and move together. Run it from `trailer/` after `npm install --no-save
  sharp`, since that folder has the `.npmrc` with `os=win32`.
- Hosting is a Cloudflare Worker serving the folder as static assets (Workers Builds from GitHub:
  root `website`, deploy command `npx wrangler deploy`). `website/wrangler.toml` has `name` (must
  match the Worker in the dashboard) and `[assets] directory = "."`, so `.assetsignore` keeps
  `design/` and the repo files out of the upload. Headers (CSP, caching) are in `_headers`. It began
  as a Pages config and failed: Workers Builds runs `wrangler deploy`, which has no use for
  `pages_build_output_dir`.
- **Never put a backtick inside a double-quoted bash string.** A README edit written that way ran
  `npx wrangler deploy` for real (Oct 2 2026) and published the site to the `loungepad` Worker.
  Write docs with the editor tools or a quoted heredoc (`<<'EOF'`).
- **The viewer's image needs `width: auto; height: auto`**: it carries `width="1920" height="1080"`
  for the aspect-ratio hint, and with both set the two `max-*` limits squash it instead of fitting
  it (a 390px phone showed the screenshot squeezed to portrait).
- **The site's gamepad support needs nothing installed** -- it is the browser's Gamepad API, and
  the headers do not restrict it -- but on a PC with Loungepad (or Steam Input's desktop layout)
  running, every press arrived TWICE: the Gamepad API edge, and the translation Loungepad sends for
  the desktop (the Everywhere pack's hidden D-pad → arrow-key bindings, A as a real click). The
  highlight moved two tiles a press and the stage flipped to Keyboard and back, which read as "does
  not work with a gamepad" (Oct 2 2026; the launcher log showed `Action: Up → Up (Arrow up) in no
  app` lines while the user was on the site). `site.js` now dedupes per action and source
  (`fresh(src, action)`, 250 ms, in either order since the key can reach the page before or after
  the edge), swallows the trusted mousedown/mouseup/click that follow a pad A, leaves Enter/Space to
  the browser but tracks them, keeps the pad's family for a key arriving within 2 s of pad input,
  and reads a hat on axis 9 for a pad with no standard mapping. A pad A while the pointer was last
  in charge only brings the highlight back, as in the launcher. Checked with a scratchpad
  puppeteer-core harness (20 cases: both orders, held repeats, the swallowed click, the hat).
- The `website` entry in `.claude/launch.json` serves the folder on 8931. The preview pane freezes
  transitions AND smooth scrolling (use `scrollTo({behavior:"instant"})`), and its screenshots time
  out when batched. What worked (Oct 2 2026): puppeteer-core in the scratchpad driving headless
  Chrome, with `navigator.getGamepads` stubbed to a fake DualSense so the D-pad, repeat, Menu, A and
  B paths run for real; section screenshots stitched into contact sheets with sharp. Lighthouse
  (desktop, local server) 99/100/100/100; the text-compression and bfcache audits it lists are
  the local server's no-cache headers, not the site.
- **The copy says "your Windows PC", never Windows 11** (the user's call, Oct 6 2026): the site, the
  README, the docs, the repo description, the trailer's `TAGLINE` and end card, and the share images
  cut from its poster (`website/img/og.jpg`, GitHub's social preview). Windows 10 is untested, so
  nothing claims it either: the "Also runs on Windows 10" pill went with the "Windows 11" one.
  Technical notes that are about Windows 11 itself (the shell reading Xbox pads, build numbers)
  stay as they are.
- **Cloudflare Web Analytics is on for the zone** and injects its beacon into every page at the
  edge; the CSP refused it from the day the site went up, so nothing was counted before Oct 6 2026.
  The script is `static.cloudflareinsights.com` (allowed in `script-src`); the reports go to the
  site's own `/cdn-cgi/rum` (204), so `connect-src 'self'` already covers them. **This PC's
  ProtonVPN (NetShield) answers NXDOMAIN for `static.cloudflareinsights.com`**, the 1.1.1.1 lookup
  included, which reads as the site being broken: resolve it over DoH and start Chrome with
  `--host-resolver-rules=MAP static.cloudflareinsights.com <ip>` to check the beacon.
- **The trailer is on R2, not in the site** (Oct 7 2026): the film is
  `https://media.loungepad.app/loungepad-trailer.mp4` (bucket `loungepad-media`, 1080p30 from
  `trailer/out/loungepad-trailer-1080p.mp4`, kept locally as `artifacts/loungepad-trailer-web.mp4`);
  only the `.jpg` poster is in `website/video/`. Two reasons it cannot be a site file: `.gitignore`
  keeps every `*.mp4` out of git, so **every push's Workers Build redeploys the site without it**
  (a `wrangler deploy` from this folder put it up and the next push took it down again within
  minutes), and Workers static assets ignore `Range` (a range request answers 200 with the whole
  file), so the film could not be scrubbed -- skipping restarted it from 0. `media-src` in the CSP
  names the bucket's domain. "Watch the trailer" under the hero's buttons opens `#film`, the
  viewer's frame around a `<video preload="none">`, so nothing is fetched until play; it is a
  fourth overlay state (`S.film`) and has to be named wherever the others are: `hideOverlay`, the
  cycle's guard, `back`, `nav`'s scope, `openWheel`. A on the video plays/pauses (a scripted
  `click()` on a video does neither), Left/Right skip 5 s, B/Esc close and pause, `/#trailer`
  opens it (the README links there). `play()` can be refused for a pad press or a fresh visit, so
  it falls back to muted. Checked with a puppeteer-core harness and a stubbed DualSense (21 checks),
  and on Oct 8 2026 against the bucket itself: plays, a seek to 0:45 is one 206 range request, no
  CSP violation. The bucket's custom domain was added with `wrangler r2 bucket domain add`, which
  needs the zone id (`dbdf24653a1b53aed225f39e02db9d1e` for loungepad.app). **To replace the film**,
  upload under a NEW name and change the `<source>`: the object is served `max-age=604800`, so a
  new cut under the old name can take a week to reach a visitor. `npx wrangler r2 object put
  loungepad-media/<name>.mp4 --file <mp4> --content-type video/mp4 --cache-control "public,
  max-age=604800" --remote`.
- **`wrangler dev` in `website/` never settles**: it watches the assets folder, writes `.wrangler/`
  into it, sees the change and reloads, forever. To check the site under its real CSP, serve the
  folder with a few lines of Node that send `_headers`' `/*` block (the launch.json server sends
  none of it).
- **The README's video is a GitHub upload** (`github.com/user-attachments/assets/0aa19943-…`, the
  under-10 MB 720p encode). That raw link answers 404 to anyone signed out, which looks broken and
  is not: GitHub renders it for visitors through a signed `private-user-images` link, which streams.
- **A direct `wrangler deploy` from `website/` is undone by the next push** (Workers Builds deploys
  whatever git has). Only use it for something that is also committed. Run it with
  `npm_config_os=win32` (the user-level `.npmrc` says linux). `.wrangler` is in `.assetsignore`
  because the first direct deploy published wrangler's own temp file from inside the folder.
- **`/feedback` is `feedback.html`, a Tally form** (`tally.so/r/WOvqlL`, the user's; Oct 6 2026)
  embedded on a light card, because Tally draws dark text. Tally's own snippet is inline script,
  which the CSP refuses, so `feedback.js` is that snippet as a file; `embed.js` sizes the frame
  (iframe-resizer). `tally.so` is in the ONE site-wide policy as `script-src` and `frame-src`: two
  `_headers` rules setting the CSP are joined into two policies, so a looser rule for one path
  cannot work. The README, the footer, Discord and the issue chooser all link to `/feedback`, so a
  new poll is a new form id in one place. The pane cannot see into the cross-origin frame and a
  full-page headless capture leaves it blank below the fold; check it with a meta-tag CSP copy of
  the page and a viewport screenshot scrolled to the form's end.

## The old names

- The app was Couch Launcher, then Consolify up to 1.4. **Every migration from those names is
  gone** (Sept 2026, at the user's request: nobody else was on the old builds): no folder move
  from `%APPDATA%\Consolify`, no old mutex or wake event, no Run-value rename, no old Vortex
  plugin folder cleanup. Do not bring any of it back. `%APPDATA%\Consolify`,
  `%LOCALAPPDATA%\Consolify`, the `Consolify` Run value and `%APPDATA%\Vortex\plugins\
  consolify-bridge` are the user's to delete by hand; nothing in the app touches them now.
- The metadata worker is still `consolify-metadata`: the worker's name is its URL, and a renamed
  worker is a new one with no secrets. See the note in `proxy/wrangler.toml`. That is a fact about
  the deployment, not a migration.

## Updates and the tray icon

- `UpdateService` reads `api.github.com/repos/sukumar-v/Loungepad/releases/latest`, picks the asset
  named exactly `Loungepad.exe` (`AssetName`), checks its size and the `sha256:` digest GitHub
  publishes, **and that the exe's version resource is the tag's** (`BuiltVersion`, which is
  `FileVersionInfo`): an exe built with the previous number would install, restart, see the
  release as newer than itself and install again on every start. It is staged as
  `%LOCALAPPDATA%\Loungepad\updates\<version>\<the name this copy runs as>`, since a person may
  have renamed theirs and the swap has to replace the file that runs. **The repo name is in the
  code** (`UpdateService.Repo`): rename the repo and every shipped build stops finding updates,
  because GitHub redirects old names to new ones but not the other way.
- **Builds up to 1.6.3 only know the zip**: they take the asset ending `-win-x64.zip` and refuse
  one without `Loungepad.exe` and `ui\index.html` in it. So every release still carries
  `Loungepad-v<version>-win-x64.zip` -- the same exe, the page's `index.html`, README, LICENSE and
  NOTICE -- until nobody can be on 1.6.3. Drop it from `package.ps1` and nothing breaks for anyone
  newer. The new exe's first start removes the zip's leftovers (`RemoveZipLeftovers`: `ui\` if its
  index.html has Loungepad's title, `vortex-bridge\` if its info.json names the bridge, each
  bundled theme folder under `themes\` and `themes\` itself if that empties it; docs and README are
  left alone). Checked on a 1.6.3 install updated by a replay of 1.6.3's own install step.
- Installing renames each file the release carries to `<name>.loungepad-old`, copies the new one
  in, starts the new exe with `--updated-from <v> --wait-for <pid>` and exits. Windows lets a
  running single-file exe be renamed but not deleted (verified with a throwaway single-file app),
  so the next start's `FinishPreviousUpdate` deletes the set-aside files -- **top level only**: the
  exe can live in Downloads or on the Desktop now, and walking everything under one of those at
  every start is a slow start. A failure halfway puts every file back.
- The new copy waits for the old pid, then for WebView2's `EBWebView\lockfile` to be free: a
  browser process outlives its host briefly, and a second environment on a locked profile fails.
  It then takes the mutex with `WaitOne`, since the old one may still be holding it.
- Automatic updates download in the background and install at the NEXT start (`App.OnStartup`,
  before any window), because installing is a restart and the launcher is what is on the TV.
  Settings and the tray install at once, and refuse while a game is running.
- Only a single-file build updates itself (`Assembly.Location` is empty there). A `dotnet build`
  or the multi-file `publish\` build says "development build" and never replaces itself with the
  release, so testing the updater needs `tools\package.ps1` output in a folder of its own.
- **A folder of its own is not a data folder of its own.** Every build reads and writes the same
  `%APPDATA%\Loungepad`, and an older build drops every library.json field it has no property for
  when it saves. A 1.5.0 updater test on Sept 28 2026 wiped TrailerUrl, TrailerFile, Media and the
  content descriptors from all 954 games while keeping MetadataVersion, so 1.6.0 never fetched them
  again -- which read as "trailers do not play anywhere". FetchVersion 11 refills them, and
  `Game.Unknown` (`[JsonExtensionData]`, carried by `MergeScanned`) keeps unknown fields from here
  on -- which only protects against builds that have it, not against 1.6.0 or older.
- **Releases are signed with Azure Artifact Signing** (formerly Trusted Signing). `package.ps1`
  signs `Loungepad.exe` before it is copied to `dist\` and into the zip, when `tools\signing.json`
  exists (gitignored; shape in `signing.example.json`), through the `ArtifactSigning` PowerShell
  module, authenticated by whatever `DefaultAzureCredential` finds (`az login`). The certificate
  lasts three days, so the timestamp is mandatory and the script fails without one. GitHub's digest
  is of the uploaded, already signed file, so the updater's sha256 check is unaffected. **First
  signed release: 1.7.0 (Oct 6 2026)**, signed in under 4 s; the subject is the user's legal name
  under "Microsoft ID Verified CS EOC CA 03". The module installs signtool and its client into
  `%LOCALAPPDATA%\ArtifactSigning` on first use. **`az` has to be on PATH** for the CLI
  credential: a shell opened before the Azure CLI was installed does not have
  `C:\Program Files\Microsoft SDKs\Azure\CLI2\wbin`, and signing then has no credential. README
  and GUIDE say releases are signed and SmartScreen may still warn until the identity has
  reputation; the website's step 03 ("If Windows shows a warning") stays for the same reason.
- `TrayIcon` is WinForms' NotifyIcon (`UseWindowsForms`, with its global usings removed in the
  csproj so `Application`/`MessageBox` stay WPF's). Left click shows the launcher, the menu has
  Show or Minimize (whichever it is not doing as the menu opens), the update step and Quit. It is
  disposed on `Closed`, hidden first, or it lingers as a ghost icon until the pointer passes over it.
- **"Minimize Loungepad", never "Hide"** (the user's call, Oct 7 2026): Hide is what a game's menu
  does to a game. H on any screen, the library legend's last entry and the tray all go to
  `MainWindow.HideLauncher`, which dismisses a host overlay the way the combo does and parks. The
  legend entry (`minimizeLegendItem`) carries both drawings, H for a keyboard and the menu combo
  for a pad ("Hold" in front in tap-and-hold), and the CSS shows the one for `body[data-input]`.
  A pad gets no entry while the combo is Off or a game runs (the hold is then the in-game menu).
  KeepFocus's refocus waits while the tray menu is open (`TrayIcon.MenuOpen`): the menu takes the
  foreground to open, and taking it back 350 ms later would shut it before anything could be
  chosen. Reasoned, not seen: no clicks in tests.
- **The Power Wheel over Loungepad itself has Exit Loungepad as its Close spoke** (a Moonlight
  user's report via the user, Oct 7 2026: quitting was only at the end of Settings → General).
  `PushOverlay` carries `overLauncher` (`!_overlayTargetLive`) because the target title is NOT
  cleared when the launcher is in front -- it still names the last window acted on. No confirm,
  like Settings' Exit: the spoke is never where the highlight starts (`RADIAL_HOME`).
- **Topmost has to let go when nothing will take the focus back.** With KeepFocus off (or a game
  running) Alt+Tab did activate the chosen app, but the launcher stayed `HWND_TOPMOST` over it,
  which read as "Keep launcher focused off does nothing" (Oct 7 2026). `StepBehindForeground`
  puts the launcher just below the new foreground window -- plain NOTOPMOST would put it above
  every normal window, the one just picked included -- and `BackOnTop` restores topmost on
  `Activated`. Skipped for the shell's own windows and ours (the tray menu), under a host overlay,
  and in `--windowed`, so it was not checked in the windowed build: it needs a fullscreen run with
  KeepFocus off.
- **A Power Wheel shortcut or Switch window parks the launcher** (Oct 8 2026; a handheld user's
  report: the Volume Mixer opened behind Loungepad). Opened over Loungepad itself,
  `_overlayWasMinimized` is false, so `CloseOverlay` left the launcher up: topmost over the new
  window, and with KeepFocus on it took the foreground back 350 ms later. `MainWindow.OpenShortcut`
  runs the shortcut, `AllowSetForegroundWindow`s the started process while the launcher is still
  the foreground process, parks, waits up to 8 s for the program's window
  (`WindowService.WaitForShortcutWindow`: a new one, or after 1.5 s an existing single-instance one
  -- never an old Explorer window, since Explorer always opens another), moves it to the TV and
  `ForceForeground`s it (the input-queue join, moved here from `GameLaunchService`, which now calls
  it). `SwitchTo` gives the picked window the foreground first, then parks. Lock still parks
  nothing. **This bug cannot be seen in `--windowed`**, which turns topmost and KeepFocus off.
  Checked with a full-screen Debug build driven only through CDP (`openRadial`,
  `openRadialSub('shortcuts')`, the item's own `action()`; no keys, no clicks) and Win32 reads of
  the foreground and of `WindowFromPoint` at the new window's centre: before, the launcher was
  drawn over the mixer, Display Settings, Explorer and a switched-to window in every sample to
  3 s; after, the launcher was hidden and each window was in front. Task Manager was left out: it
  auto-elevates, and a test cannot close it again. Explorer pre-creates a hidden CabinetWClass
  window, so a test that only closes handles that did not exist before leaves the new one open.
- `System.Drawing.Icon.ToBitmap` in Windows PowerShell (.NET Framework) cannot read PNG-compressed
  icon frames and returns noise, for the old icon as much as the new one. Check an .ico with WPF's
  `IconBitmapDecoder` (WIC), which is what the shell and the window use.
- **The icon is drawn, not cut out** (Oct 1 2026): `tools\make-icon.ps1` draws a dark armchair-gamepad
  silhouette on an Ember tile with GDI+ shapes at each of its nine sizes. The old one was the master's
  badge scaled down -- a thin glowing outline on a dark tile -- and at tray sizes the line was under a
  pixel and the tile vanished on a dark taskbar ("not legible"). **It is the master badge's own
  outline, made solid**, and that outline is what reads as both: each leg straight down on its
  OUTER side and slanting in on its inner side to a flat seat bottom (the controller), a backrest
  and one seam over the arms and across a gently curved cushion (the sofa). Two redraws that
  departed from it failed: upright arms with no slant read as an armchair only, and arms tilted
  whole with a round arch cut between them read as neither, the arch looking pasted on (the user's
  words). Below 32 px the seam goes and the silhouette alone is the badge. The README header is
  `assets\loungepad-logo-dark.png` / `-light.png` (a `<picture>` per GitHub colour scheme), rendered
  with the new icon by the local, gitignored `trailer\` project (`npm run stills`), which also makes
  `assets\screens\`; `make-icon.ps1` only draws the icon now. Explorer may show an exe's old icon
  until its icon cache catches up.

## PEGI

- **Steam carries the age boards itself** — `appdetails` returns a `ratings` object with one entry
  per board (`pegi`, `esrb`, `usk`, `cero`, …), keyed by app id, keyless, no title matching. That
  makes it a strictly better source than IGDB for anything on Steam, and it is one word in the
  filter list: `ratings`. The filter list is exhaustive, so leaving the word out drops the whole
  block silently, which is what "PEGI never shows up" was for a long time.
- The rating arrives as a **string**, and some boards put letters in it ("m", "r18", "z"), so only
  PEGI's own five numbers are accepted. A game with no `pegi` entry has no European rating —
  most indies do not. 4 of 20 in the test library have one, and that is correct, not a failure.
- The proxy's IGDB age extraction is **separately still broken**: `/v1/facts` returns `pegi: null`
  for everything, including Cyberpunk 2077 and The Witcher 3, on a verified fresh `X-Cache: MISS`.
  The likely cause is `AGE_SHAPES` stepping down to the last entry (which asks for no age fields
  at all) and `ageShape` then sticking for the life of the isolate — and a 400 from an unrelated
  field, such as `external_games.category`, would be misattributed to the age fields and trigger
  exactly that. It only matters now for games that are not on Steam at all.

## Fitted art and the bed

- **A bed must be stretched to the box, not centre-cropped.** `background-size: cover` showed a
  zoomed MIDDLE of the picture behind a strip that sits directly above and below that same
  picture's own top and bottom edges: two pieces of one image that do not line up, dimmed to 55%.
  The eye reads that as what it looks like — a bar. `background-size: 100% 100%` matches the sharp
  layer's horizontal scale, so the strip is the art's own colours carrying on past its edge and
  stops registering as a border at all. Both `.art-bed` and Polish's `.tv-bed`.
- The small `transform: scale(1.08)` is only there to keep the blur's feathered edge outside the
  rounded box. It used to be 1.14, which was fighting the misalignment rather than the feather.
- **Do not solve leftover strips by cropping.** A 2.14:1 tile cropped to 1.75:1 loses 18% of its
  width: REANIMAL and Forza Horizon 6 survive that (centred logos) and Shotgun Cop Man does not —
  its title runs vertically down the left edge and is gone. Edge-detail heuristics do not separate
  the two cases reliably (measured: 0.79 vs 0.48 and 0.60 of centre std-dev, n=3). The bed is the
  answer; "Change tile art" in Manage is the escape hatch for a tile somebody dislikes.
- **Where the box can take the picture's shape, it does, and there is no bed.** On a 1.75:1 box a
  2.14:1 header leaves 16px of bed above and below, and blurred light art is a pale band with a hard
  line where the sharp picture starts: "white bars on top and bottom" on Shelf's recents (the user's
  report, Oct 6 2026; Denshattack!, Look Outside and Total Overdose, all SteamGridDB 920x430 or
  460x215). A row can hold tiles of different widths where a grid cannot, so `.cont-art` is 172 tall
  and `172 x clamp(1.745, --art-aspect, 2.2)` wide: 300 or 368. The carousel measures real positions
  for it (`contOffset`, `contPerView(start)`, `contMaxScroll` walking starts) instead of index x
  pitch, `.cont-meta` is `width: 0; min-width: 100%` so a title spans its own tile and never widens
  it, and `applyArt` remembers each URL's natural size (`artSizes`) so a re-render draws the tile at
  its final width at once. Loungepad's dock and grid are fixed columns and keep the bed.

## The detail page's rating marks

- **Nothing is ever cropped off a tile.** `contain` shows 100% of the picture; the leftover strip
  is the bed. A crop was measured and rejected — see the note under "Fitted art" — so a title
  running down an edge, like Shotgun Cop Man's, is always whole.
- The score is drawn as Metacritic draws it (`.metascore`): a rounded square in the band colour
  metacritic.com uses (`#00ce7a` / `#ffbd3f` / `#ff6b73`, read off the live site Sept 2026) with
  `#262626` digits, and METACRITIC under it. The tile's proportions are the site's (36px digits
  and a 6px corner in a 64px box). `--rating-h` on `.detail-ratings` sizes both marks: the age
  mark stands that tall, the tile is 14px less (62px, PEGI's coloured square at that height) and
  the name is trimmed to its capitals (`text-box`) so it lands on the age mark's bottom edge.
  Measured in the preview: tops and bottoms match to the pixel against ESRB and PEGI.
- **The age rating is the board's real logo** (`ui/ratings/*.svg`, the user's call, Sept 2026),
  not a badge drawn in the page's materials. The files are Wikimedia Commons' `PEGI_<age>.svg`,
  `ESRB_Everyone.svg`, `ESRB_Everyone_10+.svg`, `ESRB_Teen.svg`, `ESRB_Mature_17+.svg`,
  `ESRB_Adults_Only_18+.svg` and `ESRB_RP.svg` -- public domain as text logos, still the boards'
  trademarks. **Commons' ESRB files have no `viewBox`**; an `<img>` then draws them at 215x300
  whatever box it is given and crops the rest, so one was added to each. Re-add it if a file is
  ever replaced. upload.wikimedia.org answers 429 to a quick run of downloads: space them out.
- **Settings → Library → Age rating** (`AgeRatingBoard`, "ESRB" or "PEGI", default ESRB) picks the
  board; a game rated only by the other board shows that one's (`ageRating` on the page). The mark
  is the board's own logo, so falling back cannot be read as the board asked for.
- Content descriptors ("Blood and Gore", "Mild Lyrics") are kept per board (`EsrbDescriptors`,
  `PegiDescriptors`; `FetchVersion` 9) and the page prints the ones from the same board as the
  mark: PEGI's "Bad Language" under an ESRB M would credit one board's judgement to the other.
  They sit under the description. ESRB writes them as a sentence, so the last one arrives as "and
  Strong Language" and the leading "and " has to come off.
- **Steam's `logo.png` is a wordmark, 1.78:1 or wider, every time. SteamGridDB's logos are
  whatever somebody drew** — 0.92:1 for DREDGE, 1.11:1 for Henry Stickmin, 7.34:1 for ULTRAKILL.
  The detail page hangs this where the title goes, so a square one lands as a small blob in the
  corner of a box cut for a wordmark. Steam's now gets first refusal, and the Logo slot's `Bounds`
  reject anything squarer than 1.2:1 — falling back to the text title, which is better than a blob.

## Filters

- `F.hidden` is a separate VIEW, not "show hidden as well": the reason to ask is to look over what
  you put away and take something back out, and mixing them into 200 tiles is not that. The row
  carries the count, because an empty hidden view and a broken filter look identical.

## The Steam account

- **A Steam sign-in reads a private profile with no key** (`SteamWebSession`, Oct 2026). The user
  signs in on store.steampowered.com in the usual sign-in window; the page is then asked, from
  inside itself, for `/pointssummary/ajaxgetasyncconfig`, whose `data.webapi_token` (a JWT, `sub` =
  SteamID, `exp` about a day) goes to `GetOwnedGames` as `access_token=`. That is the owner's
  token, so privacy does not apply. Signed out, the endpoint answers `{"success":1,"data":[]}`, an
  array where the object would be; the Web API rejects a bad token with a 401 (both checked with
  curl). A renewal runs the same window HIDDEN on the store's account page, on the UI thread, so
  Steam's own script refreshes the session from its long-lived cookie; landing on /login means the
  session has gone. When signed in it goes before the proxy and the key, and signing in turns
  `SteamShowOwned` on. **Not yet verified against a signed-in account**: the token's acceptance by
  GetOwnedGames for a private profile is the one link not seen working here.
- Uninstalled Steam games come from the Web API's `GetOwnedGames`, which needs a key: the shared
  proxy's `STEAM_API_KEY` (`/v1/owned`) for profiles whose game details are public, the user's own
  key (`SteamApiKey`) for private ones, or the sign-in above. **There is no anonymous route.** The community
  `games?tab=all` page and the older `?xml=1` feed both redirect to a login for every profile,
  public or not (checked Sept 2026), and nothing local lists the library with names: the
  per-account `userdata/<id>/config/librarycache/*.json` are achievement pointers, `licensecache`
  is encrypted, and `appcache/librarycache` is shared between every account that has logged in on
  the PC, so it mixes libraries.
- The account is read from `config/loginusers.vdf`. `MostRecent` is missing on some installs, so
  the newest `Timestamp` is the fallback.
- The answer is cached in `steam-owned.json`, keyed by SteamID, and held for six hours; a manual
  Rescan forces it. A failed fetch serves the cache, so an offline start keeps the library. With the
  toggle off the next scan drops the uninstalled entries, because `MergeScanned` only keeps what was
  scanned plus manual entries -- that is the intended way to remove them.
- `StateFlags` bit 4 is "fully installed". A download that has just begun already has a manifest
  and a folder, so `Directory.Exists` alone showed it as installed for the whole download.
- A `FileSystemWatcher` on every steamapps folder triggers a *quiet* scan 5s after the last
  manifest write: no spinner, and `PushState` only when the (id, installed) signature changed. A
  download rewrites its manifest every few seconds, and a full repaint each time throws the
  highlight around under somebody browsing.
- `install` parks the launcher (`Park()`) before opening `steam://install/<appid>`: the launcher is
  topmost on the TV and Steam's dialog would open behind it. The UI's confirm says how to come back
  (the minimize combo, or starting the exe again when the combo is Off).
- The proxy route has to be **deployed with the secret set**, or every fetch answers 501 and the
  Settings row says to add a key. Same trap as PEGI: the code being in `worker.js` proves nothing.

## Store accounts: Epic, GOG, Xbox and Game Pass

- Epic, GOG and Xbox libraries come from signing in to each store, the way Playnite does it: a
  `StoreLoginWindow` (a WebView2 with a profile folder per store under
  `%LOCALAPPDATA%\Loungepad\webview2-accounts`) shows the store's own page, a probe runs after every
  navigation and closes the window the moment it has what it came for. Tokens are DPAPI-encrypted
  in `%APPDATA%\Loungepad\accounts\<store>.bin`; the last library answer is plain JSON beside it,
  held six hours and served on any failure. Sign-out deletes all three.
- **GOG Galaxy's database was tried first and rejected**: it lists every connected store's library
  locally with no login, but only for people who have Galaxy, and a store disconnected in Galaxy
  keeps a stale list forever. The user asked for the Playnite route instead.
- **Epic** signs in as the Epic Games Launcher's own OAuth client (`launcherAppClient2`, the one
  Legendary, Heroic and Playnite all use). The library host is
  `library-service.live.use1a.on.epicgames.com`; the `library-service.prod.epicgames.com` that
  Playnite names no longer resolves. The catalogue is one request per item, so its answers are kept
  in the cache file by id and only new items are looked up.
- **GOG** is a cookie session. The Galaxy client credentials every open-source client carries
  (`46899977096215655` and its secret) are answered `invalid_client` now, for a bad code and a bad
  refresh token alike, so there is no OAuth route left. The sign-in window's `gog-al` cookie and
  friends are copied out of WebView2's CookieManager and replayed by HttpClient with
  `AllowAutoRedirect = false`: a 302 from the account page means the session is gone. Box art is
  `api.gog.com/v2/games/<id>` (`_links.boxArtImage`), public and per game.
- **Xbox** has no owned-games list; it has the title history (played games, any device), which is
  what Playnite imports. Four tokens in a chain: Microsoft OAuth → user.auth.xboxlive.com →
  xsts.auth.xboxlive.com (which carries the xuid and gamertag) → titlehub. The default sign-in
  client is the Xbox app's own (`0000000048093EE3`, implicit flow, `MBI_SSL` scope), whose RPS
  ticket prefix is tried as `t=`, bare, then `d=` on a 400. `XboxClientId` in Settings switches to
  the code flow of an Azure registration of one's own (`d=` only), which is what Playnite ships
  with. Neither could be verified end to end here: there was no account to sign in with.
- **The PC Game Pass catalogue is keyless**: `catalog.gamepass.com/sigls/v2?id=<sigl>` (the "All PC
  Games" list) answers with product ids, `displaycatalog.mp.microsoft.com/v7.0/products?bigIds=`
  turns twenty at a time into titles, package family names and art (`Poster` is 2:3,
  `SuperHeroArt` 16:9). Each answer carries every screenshot, so the whole list is ~30 MB; it is
  held for a week in `gamepass.json`. The Store suffixes PC builds with " - Windows" or "(PC)",
  which `CleanTitle` strips or the strict Steam title match never succeeds.
- Ids: Xbox title history is `xbox:pfn:<pfn>`, the catalogue `xbox:store:<productId>`, an installed
  Xbox game `xbox:<identity>`. All three carry `PackageFamilyName`, and `NotAlreadyFound` dedupes
  on it -- owned against installed, and owned against owned, since a PFN is unique where a title
  is not (Steam sells two games named exactly "DOOM").
- **Every uninstalled game gets the lite metadata pass**, not just Steam's: cover and tile only,
  facts from Steam by title, never the shared service. Five hundred catalogue games through the
  proxy would be a thousand cache writes for games nobody is playing. The store's own
  art (`RemoteCoverUrl`/`RemoteBackdropUrl`, written as `_pf_`) is the last fallback, and it is the
  third name that `KeepBest`, `HasFetchedArt` and `MetadataService` all have to know.
- `HasFetchedArt` accepts the cover alone for an uninstalled game. Judged on the tile, a Game Pass
  game that is not on Steam has "no art", rejoins the queue on every start, and costs a paced Steam
  search each time for the same nothing.
- Only Steam has a manifest to watch. After any Install the library is re-read once a minute for up
  to three hours (`BeginInstallPolling`), quietly, until the game is on disk.
- `Game.InstallUri` is written by the service that listed the game and is the only thing the
  Install button keys off; the host checks it against `InstallSchemes` before starting anything, so
  a hand-edited library.json cannot turn Install into "run this". GOG's is `goggalaxy://` when
  Galaxy is installed and the game's gog.com page otherwise.
- The sign-in window is driven like the rest of the desktop: stick as mouse, keyboard toggle for the
  on-screen keyboard (it types into the focused window), a Cancel button because B does not reach a
  window that is not the launcher. `BeginModalDialog` around it, or it opens behind the launcher.
- **The Xbox app's own client id (0000000048093EE3) is dead for user tokens**: sign-in succeeds,
  then user.auth.xboxlive.com answers 403 to its ticket with every prefix. Verified on a real
  account, Sept 2026. The way Playnite works is an Azure app registration of its own (client id in
  its source, scopes `Xboxlive.signin Xboxlive.offline_access`, `d=` ticket) -- the "Let this app
  access your info?" prompt is that registration's consent screen. `DefaultClientId` in
  `XboxAccountClient` is the slot for a Loungepad registration; until one exists the fallback is
  `000000004C12AE6F` with `t=`, which @xboxreplay/xboxlive-auth reports working and which can go
  the same way. The ticket loop tries every prefix on 400/401/403 and logs each answer.

## Editions, the confirm dialog and search

- **One game in several stores is one tile.** Library entries stay separate on the host (install
  state, launch route, playtime and art are per copy, and everything is keyed by id); the page
  groups them by `titleKey` in `buildEditions`, rebuilt on every state push. Never two copies from
  the same store in one group: Steam sells two games named exactly "DOOM".
- The tile stands for `rankEditions(...)[0]`: the copy picked under Manage → Launch with
  (`Game.PreferredEdition`, carried through `MergeScanned`), then an installed copy, then Steam,
  Epic, GOG, Xbox, then playtime. The game menu offers "Play on X" for the other installed copies,
  and "Install" / "Install on X" **only while no copy is installed** (the user's call, Sept 30 2026:
  Silksong installed on Steam offered "Install on Xbox" one row from Play). A second store's copy is
  installed from the game's page: Manage → Install from another store.
- The platform filter runs BEFORE grouping, so filtering to Xbox shows the Xbox copy; every other
  filter (favourite, collection, installed, search) is asked of the game as a whole.
- Hide is sent as `setHidden` for every copy. Hiding one copy only brought the other out from behind
  it as a tile of its own.
- `titleKey` must stay in step with `TitleMatch` on the host, plus the Microsoft Store's " - Windows"
  and "(Game Preview)" labels.
- The confirm overlay is a dialog, not a menu: heading, body, and A / B chips that are also what a
  mouse clicks. Nothing in it is focusable, so `confirmInput` takes A without `focusVisible()`.
  Titles are sentence case now; the old all-caps title belonged to the menu-card style.
- A menu row's subtitle sits under its label (`.two-line`). Side by side, a long subtitle ellipsised
  the label down to "In…".
- Search lives in the library top bar because both themes slot the top bar and Polish hides every
  section heading. View opens it (LB/RB are minimize-combo options). View is also the keyboard
  toggle by default; the claim below is what lets both live on one button.
  Typing refilters live; Enter/A/Down keeps it and lands on the first result; Esc/B clears it.
- B on the library goes back to how it opened (the user's call, Sept 30 2026): from anywhere below
  the top it lands on the first recent and scrolls the grid to its first row (in Loungepad the
  recents slide back down), and at the top it clears a standing search. With no recents the top
  is the first tile (`libraryTop` / `atLibraryTop`). The legend shows B only while a search
  stands, as "Back" or "Clear search".
- The default sort puts installed games first, then A to Z. The label says so.
- A text field needs the HOST to hand keyboard focus to the WebView (`focusPage` →
  `MainWindow.FocusPage` → `WebView.Focus()`, which is what calls the controller's MoveFocus).
  DOM `focus()` alone gives a field with no caret that the on-screen keyboard types into nothing.
  `openSearch` and `openInput` both send it.
- The search box is a focusable in the library scope (`data-focus-key="search"`,
  `data-action="search"`, `data-nav-skip`), so View lights it -- but the D-pad never walks to it:
  `navMove` leaves `data-nav-skip` elements out of its candidates. View or the Filter menu only. While a search is
  open or standing, `paintNav` publishes the focus region as "grid" so Polish shows the results.
- `renderMenu` rebuilds its list on every step; it must restore `scrollTop` after emptying it, or a
  menu longer than its box restarts its smooth reveal from 0 on each press and the last rows go
  unseen (Persona 3 Reload's Manage). Overflowing menus fade the edge with more beyond it; the fade
  is 28px, inside REVEAL_MARGIN, so it never dims the highlighted row.
- Manage → Launch with lists stores in `EDITION_ORDER`, never rank order: ranked, the chosen store
  jumped to the top and the row under the highlight changed.
- After a vertical wheel (the right stick), the next D-pad step lands on the first item in view
  when the highlight has been scrolled out of sight (`resumeInView`). Keyed on the wheel event,
  not on visibility alone: during a fast run of presses the smooth reveal leaves the highlight half
  out of view on every step.
- **The page claims View on the library** (`publishClaims` → `claimButtons` →
  `GamepadService.UiClaimedButtons`). When the keyboard toggle is bound to View, Press mode used to
  spend the press before the UI saw it, so View raised the keyboard and search never opened. A
  claimed press is delivered to the UI and marked spent for the toggle; the claim is dropped under
  any overlay, while searching and on other screens, so the toggle keeps View everywhere else.
- **Wheels are routed** to the list the user is in when they land on nothing scrollable
  (`wheelHome`). The right stick sends real wheel events, which go to whatever is under the hidden
  cursor; Classic's grid is only the bottom half of the screen, so the stick usually scrolled
  nothing there. A wheel over a real scroller is left to the browser.

## Over a game: the foreground and the exit

- **Windows refuses SetForegroundWindow from a program that did not receive the last input, and
  XInput is not input.** With a game in front, the Guide menu and the Power Wheel were shown
  (topmost) but never became foreground, so `launcherFg` was false and the pad went nowhere until a
  mouse click. `TakeForeground` joins the foreground window's input queue for the call
  (`AttachThreadInput`), and while `_overlayActive` the pad service treats the launcher as in front
  and the game as not focused regardless, so a refused foreground can no longer freeze a menu.
- **A process the launcher starts directly opened unfocused** -- an emulator, a GOG or manual exe.
  `GameStarted` parks the launcher by hiding it, the foreground passes to whatever was underneath,
  and when the game's window appears two seconds later Windows refuses it the foreground: the
  rule is "started by the CURRENT foreground process", and the hidden launcher is not it any more.
  Steam games never showed this because exclusive fullscreen takes the foreground by force. Two
  layers now: `AllowSetForegroundWindow(pid)` right after `Process.Start`, BEFORE the park (it is
  only honoured while the caller is the foreground process), and `FocusGameWindowAsync`, which
  waits for the game's first real window and brings it to the front once through the same
  `AttachThreadInput` join `TakeForeground` uses -- unless the game already has the foreground or
  the launcher does (an overlay is up).
- **A session used to wait a flat 15 s after every exit** for a successor process from the install
  dir. Now: anything of the game still running at that instant carries the session on (launchers
  hand over before they exit); a close Loungepad asked for waits for nothing (`_closeRequested`);
  a process that lived under 90 s (a pre-launcher) keeps the 15 s window; anything longer gets 1 s,
  for a game that restarts itself. The poll is every 250 ms, not 1.5 s.
- **A close that finds nothing of the game ends the session** (`GameLaunchService.Abandon`, Oct 3
  2026, the user's ask). A store launch that goes nowhere -- 1000xRESIST on Oct 3: no process in
  its folder ever appeared -- sat in `WaitForProcessFromDir`'s two-minute wait with the card saying
  RUNNING NOW, "Close game" answering "No game window to close" and a swap refusing with "Could not
  find the running game to close", for the whole two minutes plus the 20 s "assuming it exited"
  delay. Every wait the session makes now takes a per-session token; `RequestClose(out foundAny)`
  counts the processes it matched, and when it matched none it cancels the token, so the finally
  runs at once and the library is free to launch the same game again or another. `CloseRunningGame`
  on the bridge returns (closed, found): processes with no window to close are still reported as
  such and still waited out by a swap; nothing at all reads as "X was not running". The page gets
  `starting` on the `game` message and `gameStarting` on the state push (`Starting`, true until the
  first process is tracked; `ProcessTracked` pushes the state), and shows STARTING on the card and
  the meta, with a sub-line under Close game saying it only clears. Scratch harness over a scratch
  copy of cmd.exe: 12 checks, the abandoned close ending the session in 62 ms where the grace was
  15 s. The 120 s URI wait itself is the same cancellable wait and was not exercised (no URI
  launch without a store client).

## Emulators and ROMs

- **The folder is the platform.** Nothing about a file says whether a `.bin` is Genesis, Atari or a
  PlayStation track, and every ROM collection is already one folder per system, so a `RomFolderDef`
  names its system and the system (`EmulatedPlatforms`) supplies the extensions. Disc systems leave
  `.bin` out on purpose; a `.cue`'s `FILE` lines and an `.m3u`'s entries are "parts", skipped so a
  five-track game is one tile. The folder can override the extension list.
- A ROM's id is `rom:<16 hex of SHA-1(lower-cased full path)>`, so playtime, favourites and custom
  art follow the file and survive a rescan; `ScanEmulated` dedupes ids, because two overlapping
  folders would otherwise put the same id in the list twice and `MergeScanned`'s `ToDictionary`
  throws on the NEXT scan, not this one.
- **A ROM never reaches Steam's search.** Steam sells "DOOM" and "DOOM (1993)", and the exact-title
  rule hands the SNES cartridge the 2016 capsule with nothing about it looking wrong. ROMs go to the
  service (IGDB + SteamGridDB) only, with the system's IGDB platform ids as `platform=` on
  `/v1/facts` and `where platforms = (…)` in `IgdbClient`. `(a, b)` in APIcalypse is "released on any
  of these".
- **The worker has to be deployed for the platform hint to do anything.** An undeployed worker
  ignores the parameter: verified Sept 2026, `/v1/facts?title=Doom&platform=19,58` answered DOOM
  (2016) on a fresh `X-Cache: MISS`. The cache key carries `:p<ids>` only when the parameter is
  present, so no `SCHEMA` bump is needed. Deploy with `npx wrangler deploy` in `proxy/`.
- Emulated games never group as editions (`buildEditions` gives each its own group): Doom on the
  SNES and DOOM on Steam are different games, and so are Sonic on the Genesis and on the Master
  System. The platform string is the system's NAME ("Super Nintendo"), which is what the filter
  lists under an EMULATED heading; the original Xbox is "Original Xbox" because "Xbox" is already
  the store, and a filter that could mean either would be no filter.
- **Emulators and ROM folders live in library.json**, beside the collections, and are changed only
  through the `emu*` / `romFolder*` bridge commands. They are deliberately NOT in `CopySettings`:
  the page never sends them back, so a settings push cannot wipe them and "Restore default
  settings" leaves them alone. The page's `S.emulation` is read-only state from `PushState`.
- A game's `EmulatorId` is an OVERRIDE and null is the normal case: launch resolves
  `game.EmulatorId ?? folder.EmulatorId` (`LibraryStore.EmulatorFor`), so changing the folder's
  emulator moves every game that was not given one of its own. `MergeScanned` carries the override
  and `TitleEdited` across, like `Args`.
- **The session folder for a ROM is the emulator's folder**, not the ROM's (`SessionDir` in
  `GameLaunchService`): the emulator is the process that runs, gets moved to the TV and is closed
  by the in-game menu. Which is why a stand-in emulator for a test must NOT be a System32 program:
  `PidBelongsToGame` would claim every svchost and the session would never end. The harness copies
  cmd.exe into a scratch folder instead.
- Arguments are a template with `{rom}`, `{romdir}`, `{romname}`, `{romfile}`, `{core}`, `{emudir}`,
  three layers that REPLACE each other (game > folder > emulator), and quoting is the template's
  job so MAME's unquoted set name is possible. A template with no `{rom` gets `"{rom}"` appended,
  because an emulator started with no game looks like a launch that did nothing. RetroArch's
  `{core}` is per folder (`RomFolderDef.Core`); `SuggestCore` takes the first of the platform's
  listed cores that exists under `<emudir>\cores`, and only then is a file dialog shown.
- `NeedsFetch` lets an emulated game wait out the freshness window even with no art: a folder of a
  thousand arcade sets with nothing on SteamGridDB would otherwise cost a thousand proxy calls per
  start. It is still re-fetched if a picture it did have has gone from disk.
- `emuAdded` is pushed with `id: null` when the file dialog is cancelled. The page's folder wizard
  waits on it, and without the null a cancelled dialog left `pendingEmuPick` armed, so the next
  unrelated "Add an emulator" would have added the folder with that emulator.
- The preview's `mockHandle` answers every emulation command and ships a slice of the catalogue
  (`mockEmulation`), so the wizard, the options lists and the Manage rows can be walked in the
  browser. Keys: X opens the filter, M opens Settings, Enter is A.

## Mods and Vortex

- **Vortex has no external API at all.** It is Electron, its state is a LevelDB only it can open,
  and the one way in is an extension running inside it. `Loungepad/vortex-bridge/index.js` is that
  extension: an HTTP server on `127.0.0.1:47391` that turns JSON requests into Vortex API calls.
  `VortexBackend.SyncPlugin` copies it into `%APPDATA%\Vortex\plugins\loungepad-bridge`, keyed on
  the version in `info.json` like the bundled themes -- bump it whenever the extension changes.
- **Vortex loads extensions at startup only.** A Vortex that was already running when the folder
  appeared answers nothing, which is the `needsRestart` state and the Restart Vortex row; the
  restart is a WM_CLOSE to its main window and a `--start-minimized` start, never a kill. The
  same state is raised when the bridge that answers `/status` is an older version than the one
  shipped: an updated extension on disk is not the one running, and a route the new build asks
  for would otherwise come back 404 and read as an empty answer. Bump `info.json` and
  `BRIDGE_VERSION` together whenever the extension changes.
- **"Restart Vortex once" that never helps is a port conflict.** The old `consolify-bridge` (the same
  extension under the app's former name, left in `%APPDATA%\Vortex\plugins` since its cleanup was
  removed) loads first by folder order and binds 47391; vortex.log then shows
  `[loungepad-bridge] could not listen ... EADDRINUSE` on every start, and our pings meet a bridge
  that rejects our token. `VortexBackend.ForeignBridges` finds any other plugin whose index.js names
  the port, and the status says to remove it in Vortex's Extensions instead of to restart. Seen on
  this PC, Oct 1 2026. It is a diagnosis, not a migration: nothing deletes the other extension.
- The token is new on every Loungepad start and written to `bridge.json` beside the extension,
  which re-reads the file on EVERY request. That is what lets a Loungepad restart not strand Vortex
  on a stale token. The Host header must be loopback (DNS rebinding from a browser tab).
- **Downloads and installs never go through the bridge.** `Vortex.exe --install <url>` is Vortex's
  own command line, and a second instance forwards its argv to the running one (`src/main/src/
  ipcHandlers.ts`), so it works with or without the extension. The Nexus browse window cancels
  `LaunchingExternalUriScheme` for the `nxm://` link and hands it there; that is what stops a
  protocol prompt from appearing on a desktop nobody is looking at.
- What the extension calls, taken from Vortex's source at master in Sept 2026: the
  `setModsEnabled(api, profileId, modIds, enabled)` helper, the `deploy-mods` event with an error
  callback, `remove-mod(gameId, modId, cb)`, and `setNextProfile(profileId)` for switching. Every
  write goes through `ensureActive` first, because enabling and deploying only act on the active
  profile.
- **Never raise `activate-game` for a game with no profile.** Vortex's handler answers it with a
  "Choose profile" dialog listing the game's profiles -- none -- whose Activate button then does
  nothing (`user selected profile {}` in vortex.log). That is what "Set it up in Vortex does
  nothing" was, on Silksong. Vortex's own Manage button does `manageGameDiscovered`: dispatch
  `setProfile({ id: shortid(), gameId, name: "Default", modState: {} })`, then `setNextProfile`,
  and the switch itself makes the staging folder and asks the deployment question. `/activate` does
  exactly that now; the switch's wait ends on a question raised BY the switch (dialogs that were
  already open do not count) so the launcher can show it. Verified live: Silksong went from
  located to managed and active in one call, with no question. State is read by raw path (`persistent.mods[game]`, `persistent.profiles`,
  `settings.profiles.activeProfileId`, `session.gameMode.known`, `settings.gameMode.discovered`)
  rather than through selectors, so the harness can stub the store without stubbing selectors.
  **The first cut read `persistent.profile.profiles`, which does not exist**: every game came
  back unmanaged and the active profile id pointed at nothing, against a real Vortex 2.7 with
  Cyberpunk managed. `GET /state-keys?path=persistent` on the bridge lists the keys and types at
  any point of the tree (never values) and is how to check a path before trusting it.
- **Verified against Vortex 2.7.0 on this PC (Sept 2026)**, with Cyberpunk 2077 (GOG) managed: the
  extension loads and listens, `/games` and `/mods` read the real state, `--install-archive`
  installs, `/answer` presses a dialog's button, `/enable` deploys and undeploys (checked on the
  file in the game folder), `/remove` takes the mod out. Two things were wrong on the first
  contact and are the pattern to expect from anything else taken from the source: a state path
  (`persistent.profiles`) and a function's signature (`setModsEnabled(api, profileId, modIds,
  enabled)` is a helper that dispatches itself and returns a promise, NOT an action creator --
  called the old way it fails with "api.getState is not a function"). The harness models both the
  real way. `node tools\vortex-bridge-harness.js` is the extension's regression check; a scratch
  console project referencing `bin\Release\...\Loungepad.dll` and calling `ModService` directly is
  how the host side was run without the launcher window (the single-instance mutex stops a second
  copy while the user's is up).
- Vortex 2.7's all-users install is `C:\Program Files\Vortex\Vortex.exe`, with an uninstall entry
  whose `InstallLocation` is empty and whose `DisplayIcon` carries the path -- so the registry
  walk has to read DisplayIcon, and the plain Program Files path is a candidate of its own.
- **A Vortex started `--start-minimized` has no visible window**, so `Process.CloseMainWindow`
  closes nothing and neither does a search for a visible one. WM_CLOSE posted to its hidden
  top-level window titled "Vortex" exits it cleanly within a second (`VortexBackend.CloseWindows`);
  that is what the Restart Vortex row does.
- **Reinstalling an archive whose name is already in Vortex's downloads raises "File exists"**,
  because removing a mod keeps its archive on purpose. Every such question is a dialog in
  `session.notifications.dialogs` with its buttons as labels, and `api.closeDialog(id, label)`
  presses one; that is the whole of `/answer`. Answering a dialog's "don't ask again" button is
  a permanent choice in Vortex: a test once pressed "Yes, Install And Don't Ask Again" on the
  fallback installer's question by picking the last button, and the question has not come back
  since. Pick a button by its meaning, never by position.
- Games are matched to Vortex's by **install path only** (`ModService.Match`): equal first, then
  one nested in the other. Vortex's names and the stores' names differ, and a title match would
  list another game's mods with nothing about it looking wrong.
- Every answer to the page is one `mods` message carrying the whole screen (`ModsView`): a state,
  the sentence explaining it, the list. `modsState.view` on the page is that message and nothing
  else, and an answer for a game other than the one on screen is dropped. Opening the screen may
  start Vortex minimized; `_modsCts` cancels the previous request so a slow start cannot answer
  for a screen that has moved on.
- The preview lands each store on a different state so every screen is one tile away: Steam ready
  (Cassette Run with an empty list, Hollowmark with a Vortex warning), Epic not installed, GOG not
  set up, Xbox needs a restart, Manual unsupported.
- Vortex deploys with hardlinks into the game folder, so nothing about the launch changes. Its
  per-game "primary tool" (SKSE and the like) is deliberately NOT honoured yet -- it would change
  how a game starts for anyone who has Vortex without ever opening the Mods screen.
- **The browse window has no title bar.** A stick-click on the title bar's X -- the one control
  in that window Windows drew rather than we did -- left the user's pad doing nothing until a
  real mouse closed the window, while a click on Done was fine. WM_CLOSE posted to the window
  (what the X sends) reproduced nothing: the launcher got the foreground back every time. The
  difference is somewhere in how an injected click lands on a caption button, and the fix that
  needs no theory is to have no caption: `WindowStyle.None`, and Back, Forward, Keyboard and Done
  as buttons in the window's own bar, each drawn with the pad button that also does it.
- **B is Back, X is Forward and Y is Done in the browse window, not LB and RB.** RB was the keyboard
  toggle by default when this was written (View is now) and the keyboard is how anything gets typed
  into the site; B is only a right click on
  the desktop, and context menus are off in that WebView anyway. `GamepadService.ModalButtonHandler`
  is the hook: set while the window is open, asked about each face or shoulder press while the
  launcher is not in front, and a button it takes is not also a click. It checks that the window
  is the foreground one itself, because the same pad drives whatever else is on the desktop.
- Y on a mod row opens THAT mod's page: `attributes.modId` and `attributes.downloadGame` (the
  Nexus section it was fetched from, which can differ from the game's own) travel as
  `nexusModId`/`nexusDomain`, and `ModService.ModUrl` refuses a section that is not plain letters
  and digits rather than put it in a URL. A mod installed from a file has neither and Y falls
  back to the game's section.
- **The browse window's bar is a second WebView**, a page built in `StoreLoginWindow.BarHtml` with
  `ui/glyphs.js` inlined into it. glyphs.js is the icons and the button pictures split out of
  app.js (loaded before it; the two family variables live there and app.js assigns them), so the
  B and X on Back and Forward are the launcher's own drawings and follow the pad in hand through
  `MainWindow.WatchPadFamily`. Drawing them again in WPF would have been a second set to keep in
  step. The bar talks to the host with `chrome.webview.postMessage({cmd})` and is told
  `{family, canBack, canForward}` back.
- **Locating a game in Vortex without its folder dialog**: `/discover` dispatches the same
  `addDiscoveredGame` (new) or `setGamePath` (re-pointed) that Vortex's own "manually set
  location" does, after checking every `requiredFiles` entry is in the folder. The launcher
  reaches it from `ManageAsync` when the game is known to Vortex only by name -- the one place a
  title is matched, exactly via `TitleMatch.IsConfident`, and only on the user's press of the row
  that says what it will do (the `notDiscovered` state).
- **Installing a game extension is not on Vortex's API.** `state.session.extensions.available`
  is Vortex's catalogue (fetched by Vortex, needs the network; `type === "game"` entries carry
  `gameName`/`gameDomain`); `show-extension-page(modId)` opens Vortex's own extension browser on
  one; the install itself is a click there, or an `nxm://site/...` link from the extension's page
  on Nexus Mods, which Vortex routes through `install-extension-from-download`. The bridge's
  `/extensions?query=` matches loosely on the game's name and flags the exact match; the user
  picks from names, so a near miss costs a glance.

## Finding emulators and ROMs on their own

- `EmulatorDetection.Run` goes first in every scan (`DetectEmulators`, default on) and ADDS to the
  library store from the scan thread; the scan that follows picks the new folders up in the same
  pass. Only exes the presets name are ever taken, so a folder of exes cannot produce a "video
  player" emulator. Sources: one level under Program Files, `%LOCALAPPDATA%` (+`Programs`), the
  profile folders and every drive root; a second level only under a folder whose name says
  emulator; `Emulation\emulators`; Steam's `common\RetroArch`; uninstall entries (`DisplayIcon`,
  `InstallLocation`); Start Menu and Desktop `.lnk` files whose name contains a preset name,
  resolved through `WScript.Shell`. Measured on this PC: emulators 246 ms, ROM sources 39 ms.
- **RetroArch's playlists are the best ROM source and come first.** One `.lpl` per system under
  `<retroarch>\playlists` (or `%APPDATA%\RetroArch\playlists`, or `playlist_directory` in
  retroarch.cfg): JSON since 1.7.5, six plain lines per entry before; `content_*.lpl` are history
  and favourites, not systems. An entry carries the ROM path (`game.zip#game.gba` for an archive
  -- the archive is the file), the database's No-Intro label, and `core_path` or "DETECT". A
  playlist becomes a `RomFolderDef` with `Playlist = true` whose `Path` is the .lpl, its platform
  from the file name (`RetroArchPlaylists.PlatformFor` = a few specials, then `Guess`), and its
  core the most-named existing one, else `SuggestCore`. Directories that playlist entries live in
  are then NOT added as folders on top, or every game would have two rows.
- The trade-off of playlists first: a ROM dropped into the folder later is not seen until
  RetroArch rescans it, or the folder is added by hand. Said in the README.
- `EmulatedPlatforms.Guess` decides a contained match by LENGTH, and "Super Nintendo Entertainment
  System" contains the NES's full name -- so the SNES's full name is in the list, or every SNES
  playlist filed itself under NES. The harness has both names.
- Labels are not file names: `RomTitles.FromLabel` exists because `GetFileNameWithoutExtension`
  on "Dr. Mario (USA)" is "Dr".
- Detected folders get an emulator from `PickEmulator`: the source's own program (a PCSX2 ini →
  PCSX2), else the standalone emulator made for the system with the FEWEST platforms (mGBA over an
  everything-emulator), else a RetroArch that has a core for it installed.
- Removing an emulator or folder puts its path on `IgnoredEmulatorPaths` / `IgnoredRomFolderPaths`
  in library.json, however it got there; adding by hand takes it off. Without that, detection put
  back whatever was removed on the next start, which reads as "remove does nothing".
- The harness's detection section runs against the REAL machine, read-only (`FindEmulators` and
  `FindRomFolders` with empty lists, nothing saved). It asserts what this PC has -- RetroArch at
  `C:\RetroArch-Win64`, PCSX2 at `C:\PCSX2`, a GBA and a DS playlist -- so it will need its
  expectations changed on another machine.

## Trailers

- **Steam's `appdetails` `movies` no longer names a playable file.** It used to carry `webm`/`mp4`
  at `480`/`max`; since 2025 it carries only `dash_av1`, `dash_h264` and `hls_h264` manifests, which a
  plain `<video>` cannot play. The progressive files are still on the CDN at
  `video.akamai.steamstatic.com/store_trailers/<MOVIE id>/movie_max.mp4` (and `movie480.mp4`) --
  keyed by the movie's id, not the app's. Checked Sept 2026 on nine games from Portal to Black Myth:
  Wukong. A missing file is a **200 with an empty body** (Portal 2's oldest movie has only the 480),
  so `SteamTrailerAsync` probes each URL with a one-byte range request and believes only a 206 with
  a `video/*` type. The legacy keys are still read first if an entry has them. IGDB has YouTube ids
  only, so a game not on Steam has no trailer.
- `FetchVersion` is 7 for this (6 was Steam's trailer, 7 added IGDB's). `MergeScanned` lists `TrailerUrl` and `TrailerFile`; a changed URL
  clears the file name, because the cache name carries a hash of the URL it came from.
- **`appdetails` is not reliably keyed by the app id asked for.** With `basic` in the filter list (or
  no filter at all), an app that has DLC answers keyed by one of its DLC ids -- Hollow Knight under
  `916000`, Portal 2 under `323180`, Wukong under `3288260` -- with the right `steam_appid` inside.
  Verified Sept 2026 with curl, and it predates the trailer work. `TryGetProperty(appId)` therefore
  read every game with DLC as "not found": no description, no rating, no trailer, silently.
  `FetchSteamFactsAsync` now takes the single entry whatever it is keyed by and checks
  `steam_appid` inside instead. Nothing else calls appdetails.
- **The pass saves at checkpoints now** (`EnrichAsync`'s `checkpoint` callback, every 20 s while
  there is unsaved work; the bridge saves and pushes state) and runs installed games first, most
  recently played first. It used to save once at the very end: at store pace a 954-game library is
  half an hour, the launcher was closed before that every time, and nothing -- not one trailer URL,
  not one stamp -- ever reached disk. That was "trailers are not playing" on the first build. The
  stamps are also saved when nothing changed; unsaved, the same two games were fetched on every start.
- **The data host (`ServeDataFolder`) answers Range requests now** -- 206 with `Content-Range`, a
  `SliceStream` over a shared-delete `FileStream` -- and streams anything over 8 MB instead of
  reading it whole. Covers still go through `ReadAllBytes`. `/trailers/...` is routed to
  `%LOCALAPPDATA%\Loungepad\trailers` (`DataRoots`); everything else stays under `DataDir`. A
  scratch harness exercised `ParseRange` and `SliceStream` by reflection (15 checks).
- `TrailerCache`: the page streams the URL and sends `cacheTrailer` at the same moment; one download
  at a time and only the latest request waits, the file lands as `<id>_<10 hex of sha1(url)>.mp4`,
  the game is pointed at it, and the page gets a `trailerCached` message (one field on one game --
  NOT a state push, which would rebuild the library under somebody browsing). 4 GB cap, oldest write
  time out first; a play touches the file's write time. `Prune()` at bridge start clears names whose
  file is gone and deletes stray `.part` files. `CacheTrailers` in `AppSettings` (and `CopySettings`).
- `--autoplay-policy=no-user-gesture-required` is passed to the WebView2 environment: a pad press
  arrives as a bridge message, which is not a user gesture, so an unmuted `play()` would otherwise be
  refused and "Trailer sound" would do nothing.
- **IGDB is the fallback source, and IGDB keeps no video files** -- only YouTube ids
  (`videos.video_id`). The worker (`SCHEMA` v6, deployed Sept 2026) and `IgdbClient` both ask for
  `videos.name, videos.video_id` and pick the one named "trailer", else the first; it travels as
  `video` in `/v1/facts` and `IgdbGame.VideoId`, and lands in `TrailerUrl` as a
  `youtube.com/watch?v=` URL only when Steam has nothing (`EnrichAsync`, after the Steam step).
  Steam's "none" clears only a Steam-shaped URL (`IsSteamTrailer`), and IGDB's "none" clears only a
  YouTube one, and only when IGDB actually answered. `TrailerCache.IsFile` keeps YouTube URLs out of
  the cache. **Nothing streams through the proxy**: the id is eleven characters in an answer it
  already makes; the video goes YouTube → page, and Steam's mp4 goes Steam CDN → page. The user was
  explicit about not wanting video traffic on the worker.
- On the page, one player interface with two backends (`videoBackend` over `<video>`,
  `youTubeBackend` over YouTube's IFrame API, loaded lazily off youtube.com on the first YouTube
  trailer and never for a Steam-only library). The YouTube player replaces `#bdYtHost` /
  `#detailYtHost` with an iframe inside `.trailer-yt`, sized to cover with `max(100%, 177.78vh)` ×
  `max(100%, 56.25vw)` because an iframe cannot `object-fit`. Every theme rule that styles the film
  has to name both: `#backdrop :is(video, .trailer-yt)`.
- **YouTube's chrome cannot be switched off by an embed, so it is worked around** (the user wants
  the film to read as a film, not as YouTube): the fade-in waits `YT_SHOW_DELAY_MS` (900) after
  PLAYING so the big play button and the centre bezel are gone before the frame shows; the
  captions module is unloaded (`player.unloadModule("captions")`, undocumented but the one thing
  that keeps captions off) on ready and on every PLAYING; the frame is made 28% TALLER than 16:9
  at the cover width (`--yt-pad` on `.trailer-yt`) so the player letterboxes the picture to
  exactly the box and pins its title bar, cards button and bottom bar into the black bars the
  wrapper clips off -- an 18% overscan did the same job first and cut 8% off every edge, which
  the user saw as "zoomed in"; and a 250 ms watch cuts the film `YT_END_MARGIN_S` (1.2 s) before
  its end so the suggested-videos end screen is drawn behind a wrapper already at zero. A YouTube
  stop mutes rather than pauses -- a paused embed shows the play button again -- and `stopVideo()`
  comes after the fade. `rel: 0` only limits suggestions to the same channel these days and
  `modestbranding` is ignored; neither is the answer.
- `syncTrailers()` is the one decision point and is idempotent: called from `paintNav`,
  `renderDetail`, `switchView`, `showOverlay`/`hideOverlay` (a tick later, because the overlay flag
  is set around the call in either order), `setOverlayMode` and `visibilitychange`. It stops
  everything when a search is being typed, the Mods screen or the guide is up, a game is running,
  the window is hidden, or the option says so. **Ordinary menus do not stop it** (Y, the filter, the
  collection and manage sheets, a confirm): the user asked for the film to keep running under them.
  The library film additionally needs `--trailers` on `#backdrop` to not be `none`, which is how a
  theme allows it at all (Loungepad) or turns it off per state without script.
- **The film is not handed across screens; the page is drawn over it.** A handoff was built first
  (the page's own player started at the library player's position) and the user could see the
  still for the beat before the page's first frame. Now Loungepad's page is transparent, its
  `.detail-art` is off, and it declares `--trailer-surface: backdrop`; `syncTrailers` then arms the
  LIBRARY player with the page's game, which is a no-op when it is the game already showing, so
  the film in `#backdrop` never stops. The library's tiles fade out under the page
  (`body[data-view="detail"] #screen-library.under { opacity: 0 }`, app.css) and the theme's
  grid-state dimming carries `body:not([data-view="detail"])` so a page opened from the grid is
  not drawn over a blurred, darkened backdrop. Shelf's page is still an opaque sheet with its own
  player and its own 3 s clock.
- **Shelf's library has no film** (the user's call: Shelf stays as it was). `--trailers: none` is
  the app's default on `#backdrop`; Loungepad sets `auto`. `libraryCanHostTrailers()` reads it,
  `trailerModes()` drops "Library and details" from the Trailers row while it is none, and
  `lookTrailers()` folds a stored "all" to "detail" so a bag written under Loungepad reads sensibly
  under Shelf.
- `makeTrailerPlayer` arms at 1.2 s (source assigned, buffering starts) and plays at 3 s; `ended`
  and an unrecoverable error are remembered per game so a finished or unplayable film does not
  restart on every repaint; a cached file that errors falls back to the stream once. Sound is on by
  default at 0.7 (`TRAILER_VOLUME`); "Trailer sound" off mutes.
- The trailer options are look settings in the theme's bag (`LOOK_IDS.trailers` = "all" | "detail" |
  "off", `LOOK_IDS.trailerSound`, default on), listed first under "<THEME> OPTIONS" for every theme,
  with "Achievement progress" after them. A new look setting needs `LOOK_IDS`, its `look*()`
  reader, and a row; `RESERVED_IDS` follows.
- A "change the trailer" option (a link or a file, under Manage) was built and then removed at the
  user's request the same day, in favour of the gallery below. Do not bring it back unasked.

## The gallery

- `Game.Media` is the store page's films and pictures (`MediaItem`: kind, url, thumb, name),
  fetched for **installed games only** -- the list rides in every state push and a Game Pass
  catalogue would make it most of the payload. Steam: every movie (`SteamMoviesAsync`, each
  resolved and probed the way the trailer is, at most `MaxMovies`) then the screenshots
  (`path_full` 1920x1080, `path_thumbnail` 600x338, at most `MaxScreenshots`); the lite pass
  resolves the highlight movie only. IGDB: `videos` and `screenshots.image_id` at `t_1080p`
  (worker `SCHEMA` v7, deployed Sept 2026; `IgdbGame.Videos`/`Screenshots`), the poster for a
  YouTube video being `i.ytimg.com/vi/<id>/hqdefault.jpg`. IGDB's list is set first and Steam's
  replaces it when Steam has anything. `MergeScanned` carries it; `FetchVersion` is 8.
- **Any game's page fills its gallery on demand.** The page sends `fetchMedia` once per game per
  session when a page opens with an empty strip (`requestMedia`); the bridge runs
  `MetadataService.FetchMediaAsync` for that one game off the UI thread (Steam by app id or the
  exact-title search, never for a ROM; IGDB otherwise; `_mediaFetching` stops a double ask), saves,
  and answers with one `media` message that the page writes into the game in place and re-renders
  the strip from. That is how an uninstalled game gets a full gallery without every catalogue
  game carrying one. `SteamAppDetailsAsync` is the one appdetails call (the DLC-keyed root fix
  lives there) and the facts fetch goes through it too.
- The strip signals "more this way" on the tiles, not with an edge fade: the outermost visible
  tile on a side that has more beyond it gets a shade over its outer half (`.edge-left` /
  `.edge-right`, set by `updateMediaScroll`), and the scroll keeps the highlight a tile in from
  such an edge so the shade never sits on the tile you are on. The earlier right-edge mask was
  also what cut the first tile's ring: a mask stops at the element's box, and the lifted tile
  reaches past it.
- On the page, `detailMediaItems(g)` puts the trailer first (`trailerUrl(g)`, so the cached copy
  when there is one, with the matching movie's poster) and skips the movie that IS the trailer.
  The strip (`#detailMedia`, built by `renderDetailMedia`) is ordinary focusables in the detail
  scope, so the spatial nav walks onto it from Play with Up; `updateMediaScroll` slides the track
  like the continue row (`MEDIA_ITEM_W`/`MEDIA_GAP`/`MEDIA_STRIP_W` must match `.media-item` and
  `.media-strip`).
- **The highlight drives the picture and the film.** `paintNav` on the detail view reads
  `focusedMediaOverride()` (a highlighted screenshot) and hands it to `scheduleBackdrop(g,
  override)` / `setBackdrop(g, override)` (the override leads the candidate list) and to
  `updateDetailArt` (Shelf's own art, applied only on a change). `syncTrailers` arms a highlighted
  film with `GALLERY_DELAY_MS` (700) rather than the 3 s, plays nothing for a highlighted picture,
  and plays the trailer again once the highlight leaves the strip.
- Players are keyed on `filmKey(g, url)` -- "trailer:<remote>" for the game's own trailer whether
  read from the store or the cache, the URL for anything else -- so a cache landing never restarts
  a playing trailer and a different film always does. `arm(g, url, { delay, again })`; only
  `trailer:` films are sent to `cacheTrailer`.
- **The viewer** (`#overlay-media`, `mediaView`, its own `viewerTrailer` player): the item whole
  on black, `contain`-fitted; A pause/resume (on a picture: next), X/Y seek ∓10 s, Left/Right step
  (and move the strip's focus with them, so B lands where you were), B closes. `tickMediaView`
  every 250 ms drives the progress line and the A label. It is quiet for the page's own films
  (`mediaView` in `quiet`) but exempt from the Trailers option: its film was asked for. A ended or
  failed film replays on A through `again`. The YouTube backend's `pause()` is a real pause (the
  button it brings back is the price of asking); `quiet()` is what a stop does (mute, run out
  under the fade, stop after). `handleInput` routes to `mediaViewInput` before the confirm;
  `repaintFocus` has a branch; `overlayOpen()` includes it.
- Loungepad's resting scrim was rebuilt so the film stays crisp: a light band under the chrome, a
  flat heavy band from where the recents row starts (40% up) to the bottom, and an ellipse of shade
  behind the title at the bottom left instead of a wall down the whole side. The detail page lost
  its right-edge vignette for the same reason; the ratings sit inside the bottom shade anyway.
- `--continue-max` on `.continue-row` is read by `continueMax()` to decide how many recents the
  row lists (capped at `CONTINUE_MAX`, 12). Loungepad sets it to its column count so the row is
  exactly full and never scrolls; Shelf has none set and keeps the carousel. It follows the "Tiles
  across" option on the next render (the settings save's state push).

## The theme names

- The bundled theme is **Loungepad** (folder `loungepad`); the built-in look with no theme applied
  is **Shelf** (id `""`). `ThemeService.Renamed` maps `marquee` and `polish` to `loungepad`, which
  moves an installed old folder to `theme-backups` and rewrites settings.json on the next start.
  Nothing else keys on the display name.
- The recents row lost its frosted tray on purpose: the tray was what made the layout read as
  somebody else's television. The tiles sit straight on the art with the scrim under them. It is a
  row of the page now and scrolls off the top like the others (theme 4.5; 4.4 slid it off and
  faded it), so the rule that used to separate it from the grid (theme 4.3) is gone. Corner
  radius is 10px.
- `SyncBuiltIn` ignores a shipped theme whose id is a key of `Renamed`. It was written when the
  themes were copied next to the exe and build output is never cleaned (`bin\Release\themes\polish`
  is still there on this PC): the retired theme was reinstalled one line after being retired. The
  themes ship inside the exe now, so only a retired folder put back into the project could do it.

## Motion, and a theme's own options

- Every duration in `app.css` is `calc(<base> * var(--motion))` through the `--t-*` tokens on
  `:root`. `applyMotion` writes `--motion` from the Animations toggle and speed slider (1/speed, 0
  for off) plus `body.no-motion` for the one thing 0 cannot stop, the Play button's infinite pulse.
  A literal `180ms` anywhere in app.css or a bundled theme is a bug: it ignores the setting.
- Screens and overlays have a two-class lifecycle: `.active` owns input, `.closing` is only still
  drawn while the exit runs (`motionEnter` / `motionLeave`, `showOverlay` / `hideOverlay`).
  `Nav.activeScope` looks at `.active` only, so a closing menu never takes the D-pad. How long
  `.closing` stays is read off the computed animation of the element and its direct children, delay
  included, so a theme that lengthens an exit is not cut off, and at 0 the class comes off in the
  same call. `hideOverlay` on something not active is a no-op: adding `.closing` to a hidden
  overlay would flash it up to fade it.
- Entrances are `from`-only keyframes with `backwards` fill. A `to { transform: none }` held by
  `forwards` overrides the element's own `.focused` scale for good; letting the animation end on
  whatever the element's style says is what stops anything snapping. Exits name their end state
  and hold it.
- Anything rebuilt on every highlight move (menu rows, in-game tiles) must not carry an entrance
  animation, or every step replays it. `renderRadial` builds its spokes once and updates them in
  place for exactly this reason; `--i`, `--dx`, `--dy` on each spoke are the stagger and the way
  back to the centre.
- **The library never leaves.** `switchView` marks it `.under` (drawn, `pointer-events: none`) while
  the detail page or Settings is up, and brings it back with `data-motion="none"` so it gets no
  entrance. That is what keeps the backdrop still across a game's page: `renderDetail` no longer
  clears it, `focusedGame` under Settings answers with the library's own highlight, and Polish keys
  its backdrop rules on `#screen-library:is(.active, .closing, .under)`. On `.active` alone the
  sharp hero snapped to Classic's blurred wash the moment a page opened and transitioned back from
  it on return -- "the background goes up and settles back down".
- **Every `.screen` is `isolation: isolate`**, because a library that stays drawn underneath needs
  its z-indexes kept in. The focused tile lifts to z-index 3 and painted over the detail sheet,
  which has none -- but only from the grid's first rows, since the scroller's top-edge fade is a
  mask and turns on once it scrolls, and the mask was the one thing containing the tile. A fix that
  only holds while some unrelated mask is on is not a fix; screens now stack in document order.
- Settings is a box (`.settings-box`) over the blurred library: `body[data-view="settings"]` blurs
  `#screen-library` and `#backdrop`, transitioned. The detail page is an opaque sheet that fades
  with no scale, because its art sits exactly where the library's backdrop hangs the same picture.
- `renderSettings` and `renderSettingsNav` update in place and only rebuild when the list's shape
  changes (`__shape`). Emptying the scroller on every move snapped scrollTop to 0 and the reveal
  then glided back down from the top each step -- the "jittery" settings rows. A rebuild keeps
  scrollTop, like renderMenu, and so does `renderLibrary` now: a state push mid-browse (end of a
  scan or a metadata pass, a favourite toggled) used to drop the grid to the top and glide it back.
  **Any function that empties a scroller has to put scrollTop back before it returns.**
- Accent, hints and the animation settings are per theme: the same bag as the theme's own options,
  under reserved ids (`LOOK_IDS`: accent, hide-hints, animations, animation-speed) that
  `themeSettingDefs` refuses. `AppSettings.AccentColor/HideLegend/AnimationsEnabled/AnimationSpeed`
  are only the fallback a theme with nothing set reads -- which is what keeps a pre-existing accent
  -- and "Restore <theme>'s defaults" deletes that theme's bag and nothing else.
- A theme's options (`settings` in theme.json) are stored in `AppSettings.ThemeSettings` keyed by
  theme id, validated on the page (`themeSettingDefs`; a bad entry drops that one row), and reach
  CSS as the option's `token` on the root and `data-theme-<id>` on `<html>`. The host only cleans
  the values (`ThemeService.CleanSettingValues`: bool, number or short string under a well-formed
  id) -- it never models the definitions, which is why `ThemeInfo.Settings` is a `JsonElement`.
  Three places have to know a new app-level appearance setting: `AppSettings`, `CopySettings` and
  the mock's settings object; a theme option needs none of them.
- The preview fetches `/themes/polish/theme.json` off the same server and pushes a `themes`
  message, so Polish and its rows are in the mock without the hand-pushed message the note under
  "Verifying changes" describes. The mock still starts on Classic.
- Polish's grid is `repeat(var(--cols), 1fr)` with `--tile-w` derived from the count and the dock
  height derived from the tile, so the column-count option moves the hero with it. Its own
  transitions multiply by `var(--motion, 1)`. (The "Row slide" option, `--tv-slide`, went with
  the slide in theme 4.5: the page moves on the app's scroll spring.)
- Bumped Polish to 3.5 for this. Anything that changes a bundled theme has to bump it or nobody
  gets the change (see `SyncBuiltIn` above).

## The menu combo: tap and hold

- **Tap opens the Power Wheel, hold (500 ms) shows or hides Loungepad** (`MenuComboMode`
  "TapHold", the default since Sept 2026, at the user's request: the double tap was finicky and
  opened the wrong thing). "DoubleTap" keeps the old shape. The tap fires on the release; the
  hold fires while still held and its release does nothing. A tap with any overlay up dismisses
  it (`OnWheelTap`); the hold is the old `OnComboTap`, so the in-game menu while a game runs.
- The gesture is `ComboGesture`, fed once a poll, so the harness replays traces against it with
  a fake clock. Its mode is learned on the first update: when it defaulted to tap-and-hold, a
  double-tap user's first poll counted as a mode switch and ate a press already down. A real
  switch mid-press still spends that press.
- **Windows' long press of the Xbox button (Task View) belongs to Xbox mode**, not to Game Bar:
  this PC had Game Bar's button setting (`HKCU\Software\Microsoft\GameBar`,
  `UseNexusForGameBarEnabled` = 0) and `AppCaptureEnabled` already off and still got Task View.
  Xbox mode is `HKCU\...\CurrentVersion\GamingConfiguration`, `GamingHomeApp`: absent or an app id
  is on, `""` is off, and it only exists from builds 26100.8328 / 26200.8328 / 28000.2179. The
  evidence that turning it off stops the long press is Microsoft's docs listing the long press
  under Xbox mode plus one user report; it was not tested here (no pad to hold). Disabling
  GameInputSvc also stops it but breaks Steam Input, so it is not offered.
- **Settings → Controller → Windows and Steam** switches the three things that also react to the
  Xbox button, each a toggle showing the live state, plus "Give Loungepad the Xbox button" for all
  three (`xboxButtonSet`, `xboxButtonAllOff`). The state rides every push as `xboxButton`
  { gameBar, xboxMode, steam, steamRunning, steamBusy }; xboxMode and steam are null where there is
  no such thing, and their rows are left out.
- `WindowsGuide`: Game Bar is ONE switch over two DWORDs under HKCU\Software\Microsoft\GameBar,
  `UseNexusForGameBarEnabled` (the button opens Game Bar) and `GamepadNexusChordEnabled` (View +
  Menu stands in for the Xbox button in apps, which fights a View + Menu combo); both default on when
  absent. Xbox mode is `GamingHomeApp`, and "" as off is confirmed: it is what Windows' own Settings
  toggle wrote on this PC. Turning either back ON deletes the values, so Windows' default comes back
  rather than a guessed 1. The Controller Bar option lives inside Game Bar's app, not the registry,
  and is covered by the button being off.
- `SteamGuide`: `Controller_CheckGuideButton` ("Guide Button Focuses Steam") and
  `SteamController_Enable_Chord` (Guide button chords), top level of the signed-in account's
  `userdata\<id>\config\localconfig.vdf`, on when absent. Matched to their settings by the names in
  SteamUI.dll's settings table (`controller_guide_button_focus_steam`, `controller_enable_chord`),
  not by forum posts. **Steam rewrites that file when it exits**, so `Set` runs `steam.exe
  -shutdown`, waits (30 s cap, and changes nothing if Steam will not close), edits, keeps
  `.loungepad-bak`, and restarts with `-silent` if it had been running. Refused while a game runs:
  closing Steam ends a Steam game. The editor only touches depth-1 value lines, adds missing ones
  before the root's close, and leaves every other byte alone (the harness checks this on a copy of
  the real file; the real one is never written by a test).
- **The same switch empties Steam's desktop layout** (`SteamDesktopLayout`, Oct 3 2026, the user's
  ask). With Steam running and a pad it has configuration support for, Steam Input applies a
  "desktop layout" whenever no game is in front, and its default (`controller_base\desktop_*.vdf`)
  binds the **left stick** and the D-pad to the arrow keys, the right stick to the mouse, A to Enter
  and the triggers to clicks -- every desktop job Loungepad already gives the pad, done a second
  time. The way out is a layout grown from `controller_base\empty.vdf`, which Steam autosaves as
  `steamapps\common\Steam Controller Configs\<account id>\config\413080\controller_<type>.vdf`
  (413080 is the Desktop pseudo-app; 443510 is the Guide chord layout) and selects through
  `configset_controller_<type>.vdf` → `"413080" { "autosave" "1" }`. Off writes that layout for
  six types (xbox360, xboxone, ps4, ps5, switch_pro, generic), keeping the Share/Capture button as
  a Steam screenshot the way the user's own file did, and leaves whatever was there as
  `.loungepad-bak` (zero bytes when there was nothing); on moves the backups back or deletes what
  it created. Done inside `SteamGuide.Set`'s Steam-closed window, since Steam rewrites the
  configsets on exit. `SteamGuide.Read()` answers "on" while either half still has the pad, so an
  install with the keys off but Steam's layout in force still shows the row ON. Scratch harness:
  49 checks on a copy of the real folder, the real one hash-checked unchanged. Observed in
  `logs\controller_ui.txt`: Steam applied the one Xbox autosave to a DualSense too ("Loaded Config
  for Local Override Path for App ID 413080 ... empty.vdf" on the pad's connect), so one file may
  be enough on some installs; the per-type files are what the folder's naming asks for.
- **The Windows 11 shell reads Xbox pads itself, and nothing turns that off.** On this PC
  `explorer.exe` has `xinput1_4.dll` and `Windows.Gaming.Input.dll` loaded (`tasklist /m`), and its
  XAML surfaces -- the taskbar's right-click menu, Explorer's new context menu, Start, Settings --
  treat the left stick and D-pad as focus movement, A as activate and B as back when they have the
  focus. That was "the highlighter moves when I move the stick" (Oct 2 2026): not Steam, whose
  desktop layout was already empty for both pads in its log, and not Loungepad, which sends nothing
  for the stick but pointer movement. WinRT cannot see a DualSense, so only Xbox-class pads get
  it. The fixes people cite were checked here: `GameDVR\AppCaptureEnabled` was already 0 and it
  still happens; Game Bar's button and chord were already off; disabling the GameInput service is
  what XInput and Steam Input now go through, so it is not offered; HidHide is a third-party filter
  driver and documents Xbox pads staying visible. Over such a menu, A fires twice: Windows invokes
  the highlighted item and Loungepad clicks under the pointer.
- The combo's legend sits below the wheel's 880px wrap (`bottom: -46px`) for both wheels, so the
  action wheel's pager can have the strip under the bottom spoke and the legend does not move
  between the two.

## Actions: an app's shortcuts on the pad

- **The wheel acts on the window that was in front when the Power Wheel opened.** `ShowOverlay`
  records `_overlayTargetLive` (the foreground was not the launcher) and `PushOverlay` carries
  `targetProcess`, the exe name behind that window, which the page matches to an app in
  `S.actions`. With the launcher itself in front there is no app: the wheel shows Everywhere only
  and `FireAction` sends nothing but a system-wide shortcut (media keys, any Win chord) — an
  Alt+F4 meant for a browser must never close Loungepad. The Power Wheel's `_overlayTarget` is
  deliberately NOT cleared in that case (Close window has always named the last target); only the
  live flag decides.
- `FireAction` is `CloseOverlay(true)` (park, hand the foreground back), a wait of up to 500 ms for
  the target to actually be foreground, 60 ms more to settle, then `SendKeyCombo`. If the
  foreground is still ours and the shortcut is not system-wide, it is refused with a toast rather
  than sent to the launcher.
- **Bindings live in the pad loop's desktop branch** (`ActionService.Evaluate`, 125 Hz): launcher
  not in front, service active, keyboard not driving. The loop resets them on every other path.
  The foreground app is cached per hwnd; Store apps resolve through the `Windows.UI.Core.CoreWindow`
  child because the frame host owns the top-level window. Chords: the longest binding a poll's
  presses complete fires, so Y does not fire on the press that completed LB + Y. A button a
  binding took is added to `modalTaken` so it is not also a click.
- **A chord fires once, and the rules are about buttons, not bindings** (Oct 8 2026, the user's
  report: Show desktop on LB + RB "switches apps too quickly"). The held state used to be a set of
  per-app binding keys, cleared on every foreground change -- and Win+D changes the foreground,
  so a held chord fired again in the window it brought up, and again on the way back: the desktop
  and the windows flipping for as long as it was held. Now: whatever fired is `_latched` by mask
  until every one of its buttons is up, across any foreground change, and nothing that uses one of
  them fires meanwhile (a one-poll bounce cannot refire it). A binding that is the start of a
  longer one, or of the menu or screenshot combo (LB alone in Firefox when Everywhere has
  LB + RB), waits `ChordWindowMs` (100) for the rest and fires on the release if let go sooner;
  any other binding still fires on its press. Whatever is held when the pad comes back to the
  desktop (`ResetBindings`) is latched, not fired. Masks are `GamepadService.ChordMask` /
  `HeldMask`, with the triggers as bits 16 and 17: the old 16-bit masks dropped them, so a menu
  combo of LT + RT + LB + RB reserved plain LB + RB too. Scratch harness (Loungepad.dll by
  reflection, `Foreground`/`ExeOfWindow`/`SendKeys` replaced, a fake 8 ms clock, the real log held
  shut): 32 traces plus 16 randomised runs of 50 chords; the old code failed 7 of the first 14.
- **The system wins over a binding, always**: a press the keyboard toggle spent (`toggleFired` on
  this press), the menu combo and the screenshot combo (`ReservedCombos`) never reach a binding,
  and A, B and Guide on their own are never one (`IsReservedButton`; the page refuses them at
  capture with a note). The Settings row explains each collision (`bindingIssues` on the page):
  `block` is refused at capture, `warn` shows under the row, `note` goes in the hint.
- **Recording a button is a capture mode in `GamepadService`** (`BeginCapture`), evaluated before
  the menu combo so nothing else fires: the chord is the union of what was pressed between the
  first NEW press and everything being let go. Whatever was held when recording began is
  ignored until released — the A that chose the row is usually still down. B alone cancels
  (`Captured(null)`); A and Guide alone are `CaptureRejected` and recording carries on. The page
  times out after 15 s.
- `ButtonMask` knows Up/Down/Left/Right now, for bindings only; `ComboName` writes a chord in one
  fixed order (`ActionService.ButtonOrder`, LT RT LB RB LS RS View Menu Guide A B X Y then the
  D-pad) and `CleanButton` normalises whatever the page sends to the same, so "Y + LB" and
  "LB + Y" are one binding. The page's `normCombo` does the same.
- **`actions.json` holds only what differs from the packs**: a pack app's entry lists the actions
  the user touched (full copies, by id) and their own; untouched defaults come from
  `ActionPacks.All` at every load, so a new default arrives on the next build. `Reset` deletes the
  entry. A custom app is any entry whose id is not a pack's; a pack added by hand is `Pinned`,
  so it shows whether or not detection finds the program. Ids are exe names, lower case, no
  extension, so a hand-added chrome.exe IS the Chrome pack.
- Detection (`Detect`, off the UI thread after `ready` and on every add) is App Paths, the uninstall
  entries (`EmulatorDetection.RegistryExes`, made internal for it), a few known folders, the
  Store's execution aliases, the library's emulators, and the running processes. Icons come from
  `PrivateExtractIcons` at 96px (ExtractAssociatedIcon stops at 32) through
  `WindowService.EncodeIcon`. They ride only on the `actions` message; a state push leaves them
  out and the page carries them over by id (`withActionIcons`), or every favourite toggle would
  cost 100 KB of PNG.
- **Shortcuts are text** (`ShortcutKeys`): modifiers by name then one key. Letters and digits are
  their own VKs; a single punctuation character goes through `VkKeyScanW` at send time so it
  follows the layout; named keys are a table. `Parts` treats a trailing "+" as the key itself.
  Every pack shortcut has to parse (the harness checks).
- On the page, `actionWheelOpen`, `captureState`, `keyPick` and `overlayTargetProcess` are declared
  in app.js, not actions.js: app.js reads them while it boots, and a `let` in a later script is
  in its temporal dead zone until that script runs — `typeof` does not save you from that.
- The Settings category is three levels in one pane (`actionsUi.level`: apps grid, an app's list,
  an action's editor). The grid is the same scroller with `.apps-grid`; tiles are rows with
  `tile: true` so `settingsInput`, `syncSettingsPane` and the highlight keys work unchanged. The
  level is part of the scroller's `shape`, or a drill-down with the same row count would not
  rebuild. B climbs one level (`actionsBack`) before it reaches the categories.
- The key picker takes a real keyboard too (capture-phase keydown in actions.js, before app.js's
  own key map turns X into a pad button). A bare letter is the key; bare Esc/Enter/arrows/Space
  still navigate. In the browser preview, **P** opens the Power Wheel over "Firefox" and **O**
  over an app with no pack.
- The harness for the host side is a scratch console project referencing
  `bin\Debug\...\Loungepad.dll` and reaching the internal types by reflection, with `SendKeys`
  replaced: 64 checks over the parser, the packs, the chord logic and the file round trip
  (Sept 2026). It writes the real `actions.json` and puts it back.

## The Loungepad keyboard: blocks, suggestions, options

- **The board is data** (`KeyboardLayout`): keys placed by row and column on a canvas, a column being
  one key plus its gap, blocks half a column apart. Row 0 is always the suggestion bar; the keys
  start on row 1. The main 12-column block is the old skeleton unchanged; F1-F12 go above it (one
  per column, with PrtSc/ScrLk/Pause over the navigation block when both are on), the navigation
  block and a real number pad (two-row + and Enter) to its right, Ctrl/Win/Alt on its bottom row
  taking room from Space. Every block on is 20 columns; `FitToDisplay` shrinks the keys to 96% of
  the display's width and 70% of its height.
- **The keyboard's sizes are DIPs, so the display is read in DIPs too** (Oct 8 2026). `DisplayInfo`
  is real pixels (`dmPelsHeight`) and a key used to be 5.8% of THAT, drawn as DIPs that Windows
  then multiplied by the display's scaling: at 150% the board was half as big again as at 100%,
  and at 300% (a 4K television's usual setting) every size setting ran into `FitToDisplay`'s 70%
  and the slider did nothing. `SizeKeys` and `FitToDisplay` now divide the display by
  `DisplayScale` (`GetDpiForMonitor` on the monitor under its middle; replaceable for the harness)
  and `FitToDisplay` measures `Root` in DIPs rather than the window rect in pixels, so it no longer
  depends on which monitor the window was on before `Place` moved it. Nothing changes at 100%.
- **Navigation is by position, not index.** Up/Down go to the key under the column centre the
  highlight is keeping (`_wantX`, sticky); a row with nothing within half a key of it is stepped
  over (the navigation block's empty row, the space above the number pad) -- except the bar, which
  always takes you. A two-row key is in both rows, so the highlight remembers which row it is in.
- **Suggestions are Windows' own** (`Windows.Data.Text.TextPredictionGenerator`, the touch keyboard's
  engine). It works unpackaged, keyless and offline, answers in 4-26 ms, and learns nothing -- the
  keyboard stores nothing typed. Asked for `Predictions` only: "hel" → hello/help, "recieve" →
  receive, and with previous words "see you" → tomorrow/soon. `Corrections` is for touch screens and
  offers the NEIGHBOURS of what was typed ("hel" → yep, gel), so only its apostrophe fixes are taken
  (dont → don't, im → I'm), which Predictions never offers. `None` throws 0x87B20803. The language
  is the first Windows language in Latin script, since that is what the keys type.
- The keyboard cannot read the field, so `KeyboardText` tracks what IT typed since it last lost track;
  the word is the letters at the end. It loses track on caret/navigation keys, Enter, Tab, Esc, any
  Ctrl/Win/Alt combo, a pad click (`PointerClick` from `GamepadService`), and `CheckTarget`: the
  foreground's focused control (`GetGUIThreadInfo`) changed, or its system caret is not where it
  settled 250 ms after the last key. **A reset only ever makes a suggestion insert instead of
  replace**, so when in doubt it resets: replacing on a stale word deletes somebody's text. Games
  have no system caret and are judged on focus alone.
- Taking a suggestion retypes only what differs ("hel" → "lo ") and carries the typed capitals
  ("Hel" → Hello, "HEL" → HELLO); a latched Shift capitalises it. Punctuation straight after a
  suggestion's space goes in front of it, and keeps doing so, so "..." and "?!" work.
- **RS takes the first suggestion, not RT**: RT is the pointer's boost button by default, and holding
  it to cross the screen would type a word.
- Keys other than characters go through `SendKeyCombo` with their scan code: a DirectInput or raw
  input game sees only that, and those are the programs that bind F5 or Num 8. Pause and Num Lock
  share 0x45, so Pause (and Print Screen, whose mapping varies) go by virtual key alone. A character
  with Ctrl/Win/Alt latched is sent as the key that types it (`VkKeyScanW`), or Ctrl then C would
  not be Ctrl+C.
- **The gear** at the bar's right end swaps the keys for the five switches (`KeyboardLayout.Options`,
  same size as the keys so nothing jumps), and its bar carries − Size n% + beside the gear: the size
  is there because a sixth switch row would make the page taller than the plain keyboard. The steps
  are a tenth at a time over the Settings slider's 0.3-1.6, raise `ScaleChanged`, and are saved and
  pushed in `keyboardOptions` like the switches (`keyboardScale` rides in it). The harness checks
  them off-screen: options page → Up reaches −, the highlight survives the rebuild, the ends stop. B and Menu go back to the keys there, X/Y/LB/RB/RS type
  nothing. A switch raises `OptionsChanged`; `MainWindow` writes settings.json and the bridge pushes
  `keyboardOptions`, which the page MERGES into `S.settings` rather than replacing it, or an unsaved
  change on the page would be lost. The settings are `Keyboard{Suggestions,FunctionKeys,NavKeys,
  Numpad,Modifiers}` in AppSettings, CopySettings and the mock.
- A B badge on the gear sat on top of the icon (the bar is shorter than a key) and was removed.
- **Small screens** (Oct 8 2026; an AYN Odin 3, 6 inches, streaming the PC through Artemis and
  VibePollo: "too big even at 60%"). The bottom of the size range is 30% (`ScaleMin`, which the
  Settings slider, `MainWindow`, `CopySettings` and the keyboard's own steps all use), and the
  smallest key is 16 DIPs (`MinKeySize`): the old floor of 28 was what 60% already came to on a
  720p display, so going lower there would have changed nothing. The frame's padding and corner
  follow the keys (`min(18, key × 0.28)`), or a small board was mostly border. Measured in the
  harness, plain keyboard, 1080p at 100%: 28% × 28% of the screen at 60% before, 14% × 12% at 30%
  now; 720p 32% × 32% before, 18% × 15% now. At high Windows scaling the 16-DIP floor is what
  stops it (a 1080p display at 250%: 29% × 25%).
- **With word suggestions off there is no bar**: it was a strip as tall as most of a key holding
  nothing but the gear. Row 0 is still row 0 to `KeyboardLayout` and to navigation, but the window
  draws it in the grab bar's line (`BarInGrip`): the grip stops where the bar's keys start
  (`Grip.Margin.Right`), the board is pulled up by the grip's height (`Board.Margin.Top`), and on the
  options page the grip's title becomes "Keyboard options". Both pages do it, so opening the options
  still never changes the height. About 11% of the height comes back.
- **Hover is MouseMove on the root, not each key's MouseEnter.** WPF raises MouseEnter for a key
  BUILT under a pointer that has not moved, so every rebuild dragged the D-pad's highlight to wherever
  the hidden cursor was parked. MouseMove has the position guard.
- **Test it with the harness**: a scratch WPF project that compiles `KeyboardWindow.xaml(.cs)`,
  `KeyboardLayout.cs`, `Services/KeyboardText.cs`, `Services/WordPredictor.cs`,
  `Interop/NativeMethods.cs` and `Models/AppSettings.cs` beside a stub `Log` and `DisplayInfo`.
  `KeyboardWindow.Output` and `Probe` are replaceable, so it records strokes instead of typing into
  whatever is in front, and a `DisplayInfo` at -6000,-6000 keeps the window off every real screen;
  `RenderTargetBitmap` of `kb.Content` gives the pictures. Do not reference Loungepad.dll instead:
  its `Log` writes the user's real loungepad.log. 83 checks pass (Sept 2026).

## Play sessions and achievements

- **The session is the launcher's, not the recorder's.** `GameLaunchService` raises
  `SessionStarted` right after `GameStarted` and `SessionEnded(game, start, end, counted)` from its
  `finally`, and `ActivityService` records exactly what those say. `counted` is the same
  `MinSessionMinutes` (0.5) test that adds to `PlaytimeMinutes` and `Sessions`, so the three can
  never disagree. The pid cache is cleared before `SessionEnded` fires: readings are taken during
  the session (`TrackedPids`), never after it.
- `activity.json` holds the rows with their averages; the readings are one file per session under
  `activity\`, thinned to 720 points by averaging neighbours (`ActivityStore.Thin`), and only ever
  opened for the session sheet. `LowFps` is the 5th percentile, which is what a "1% low" stands in
  for at five-second sampling. The store takes its folder as a constructor argument so the harness
  never touches the real one.
- **`HardwareMonitor` needs nothing installed for CPU, RAM and GPU.** CPU is `GetSystemTimes`
  deltas (the first reading only primes it and is thrown away); RAM is `GlobalMemoryStatusEx`; the
  GPU is PDH's `\GPU Engine(*)\Utilization Percentage`, summed per engine type and then the busiest
  type taken, which is Task Manager's number. The PDH item array is walked by hand: 24 bytes an
  item on x64, the name pointer at 0, the status at +8, the double at +16. Verified on this PC by
  the harness (own process's working set and a GPU percentage came back).
- The three tools are read from their shared memory, opened lazily and retried every 30 s, and a
  reader that throws is dropped and reopened rather than allowed to end the sampling loop.
  RTSS `RTSSSharedMemoryV2`: header DWORDs, app entry size at 8, array offset at 12, count at 16;
  in an entry pid at 0, tick0/tick1 at 268/272, frames at 276, frame time (µs) at 280, ticks on
  `GetTickCount`'s clock. HWiNFO `Global\HWiNFO_SENS_SM2` is `pack(1)`: reading offsets 32/36/40
  for the readings section, a reading's type at 0, labels at 12 and 140, unit at 268, value at 284.
  Afterburner `MAHMSharedMemory`: the float sits at 1300 (five 260-byte strings first) or at 544
  on the 2.0 layout, `FLT_MAX` is "no reading". None of the three was running on this PC, so
  those three readers are unverified on hardware; the layouts are from the vendors' headers.
- HWiNFO readings are matched by label (`CpuTempLabels` and friends), which is a heuristic on
  purpose: the labels are the same on every PC and the sensor ids are not.
- **Achievements are asked for by the store's own id, like the metadata**: Steam by app id,
  Xbox by `XboxTitleId` (from the title history's `titleId`, matched to an installed game by PFN),
  Epic by `EpicNamespace` (the manifest's `CatalogNamespace`, or the library service's
  `namespace`), GOG by the numeric product id. Both new fields are carried in `MergeScanned` with
  `??=`, since the manifest scan knows Epic's and not Xbox's. RetroAchievements is the one title
  match, and only where the ROM's hash finds nothing (`RetroHash`; disc systems are never hashed).
- **Steam achievements: the list keyless, the unlocks by key or by the Community page** (Oct 6
  2026). The list, rarity included, is `IPlayerService/GetGameAchievements` (no key, no token;
  `{"response":{}}` is a game with none, the one answer that may say so; icons are file names under
  `shared.akamai.steamstatic.com/community_assets/images/apps/<appid>/`). With a key the unlocks are
  `GetPlayerAchievements`; with only the sign-in they are the account's Community stats page,
  `steamcommunity.com/profiles/<id>/stats/<appid>/achievements/?xml=1`, which carries every
  unlock's time and needs nothing for a profile whose game details are public (this PC's is).
  **The ISteamUserStats calls do not take the sign-in's `access_token`**: they answer 400 "Required
  parameter 'key' is missing" (GetOwnedGames answers a bad token with 401), and that 400 was read as
  "no stats", so signing in saved 0/0 over every Steam game's list. Answers from the Community
  page: XML; an HTML profile page for a game with no stats for this account (owned and never
  started -- Half-Life 2 here -- or achievement-less), which is "nothing unlocked" unless the
  record had unlocks, when the record is kept; `<response><error>` for a profile Steam will not
  load. Ids are the same api names as before, compared case-blind (the page lower-cases them).
  A private profile with only the sign-in needs the community cookies, which are not read yet:
  the row says to make game details public or add a key. Harness (20 checks, real endpoints, the
  real log held shut): Hollow Knight 42/63 with times, TFC none, HL2 0/69, unowned Cyberpunk 0/57.
  `GameAchievements.Version` (`CurrentVersion` 1) makes every list from before this stale, and
  `NewlyUnlocked` announces nothing against an empty list, or refilling the emptied ones would
  have carded every old unlock.
- **Steam used to go through the proxy's `/v1/achievements`** (gone in 1.6.2; verified on this PC's
  account: Hollow Knight 42/63 with rarity and icons) or the user's own key. **`GetPlayerAchievements`
  answers 403 "Profile is not public" for an app the account has never started or does not own,
  on a public profile too** (3DMark did, on this account, while Hollow Knight answered with its
  unlocks). The worker asks for the schema first and answers an empty list without touching the
  account when there is none; on a 403 it checks the profile's game-details visibility once
  (`detailsPublic`, one bit in KV for an hour) and reads the 403 as "no unlocks" when the details
  are readable. The direct route with the user's own key reads every 403 that way. The schema
  and percentages are cached in KV per app; the unlocks never are.
- **GOG's account page hands a bearer token to a signed-in browser session**
  (`menu.gog.com/v1/account/basic`: `accessToken`, `accessTokenExpires`, `userId`) and
  `gameplay.gog.com/clients/<GAME id>/users/<userId>/achievements` takes it -- "clients" is the
  game, not an OAuth client. That is Galaxy's route without Galaxy's dead credentials. Kept on the
  session (`GogSession`) and re-read near expiry. **Its `page_token` never runs out**: the last page
  carries one too and answers the same list again, so walking tokens to the 20-page cap stored
  Cyberpunk's 57 achievements 20 times (1140, 100 unlocked; Oct 6 2026). Ids are taken once and a
  page with nothing new ends the walk, and `AchievementStore.Load` drops duplicate ids from any
  list kept from before. The live paging fix is unverified (it needs the GOG session); the
  stored list loads as 5/57. Epic is two GraphQL queries on
  `launcher.store.epicgames.com/graphql` with `Authorization: bearer <eg1 token>`; Xbox is
  `achievements.xboxlive.com` with contract version 2. All three are unverified live (no fetch was
  made against a signed-in account here); the shapes are the ones Playnite's SuccessStory and
  its CommonPluginsStores send.
- A failed fetch keeps the last list and puts the reason in `Error`; a fetch that is blocked by
  the account (not signed in, no key) is reported to the page and never written. The background
  pass (`RefreshAllAsync`) runs after every non-quiet scan, newest-played first, paced 1.2 s, capped
  at 400, and stops for a provider that has gone `Unavailable`. Freshness: 6 h for a page open,
  24 h in the pass, 7 days for a game never played here and not installed.
- **A first fetch announces nothing.** `NewlyUnlocked(before, now)` with `before == null` is an
  empty list, or every achievement earned years ago would arrive as a card. The post-session fetch
  waits 5 s for the store to hear from the client, and the cards wait for the `game` message with
  `running: false` -- behind a game or under an overlay they would go unseen.
- Icons are cached under `%LOCALAPPDATA%\Loungepad\achievements` by a hash of the URL and served
  as `https://loungepad.data/achievements/<file>` (`DataRoots`); the page prefers the file when the
  DTO names one. The set on disk never stores file names -- `IconFileIfCached` checks at push time.
- **The feature is called Stats on screen** (the user's call, Sept 2026); the host's names
  (`ActivityService`, `activity.json`, the `activity*` bridge commands, `ActivityTracking` and
  friends in settings.json) predate that and were left alone, as was the page's `activity.js`.
  It is not on the Power Wheel (it read as out of place there): the ways in are LB on the library
  and **Open Stats**, the first row of Settings → Stats, whose section is `bare` (no heading, see
  `settingsSectionEl`). `statsUi.from` remembers which, and B/LB on the categories go back to it.
- **On the page, `achState`, `actState`, `sessionState`, `dayState`, `statsUi` and `statsData`
  are declared in app.js**, not activity.js (the same temporal-dead-zone reason as the actions
  variables). The library claims LB alongside View (`publishClaims`) so a keyboard toggle bound to
  LB still opens the Stats screen. The screen is a view (`data-view="stats"`) on Settings' frame;
  the sheets are overlays on the quick-card frame, so both themes get them for free -- Loungepad
  only rounds them. `#overlay-day` comes BEFORE the other sheets in index.html: overlays stack in
  document order, and the session and achievements sheets it opens have to paint over it.
- **Unlocks and sessions are joined on the page, by time**: an unlock belongs to a session when it
  is the same game and inside it give or take two minutes (`UNLOCK_SLACK_MS`, the host's
  `SessionUnlockSlack`). `achievementsAll` carries `unlocksByDay` (every unlock on record as a
  count per local day, for the pips on every playtime chart) and `recent` (every unlock of the
  last 35 days in full, never fewer than 60, capped at 1500: `RecentUnlockDays`), which is why the
  overview's day picker spans 28 days and a session's trophies are only drawn when it began inside
  that window -- older ones would be missing, not absent. The `activity` message carries the
  game's dated unlocks and `activitySession` the session's own.
- **Unlock markers are near-white (`--trophy`), not gold and not the accent.** Gold was the first
  choice and sat next to Ember, the default accent, so pips on bars read as more bar; the accent
  presets go all the way round the wheel, so no hue is safe for everyone.
- **Inside a Settings or Stats category Up and Down never leave the rows** (`paneMove`): Up off the
  first row comes round to the last, Down off the last to the first (nearest column in the Actions
  grid). `navMove(dir, within)` takes a filter for this. On the Stats screen Left/Right still adjust
  a value and B is the way back.
- **Every list comes round at its ends** (the user's call, Oct 1 2026): `listMove` (= `paneMove` over
  the whole scope) for Up/Down in every overlay menu (`menuStep`), the achievements, stats and day
  sheets, the key picker and the first-run setup; the Settings and Stats categories wrap among
  themselves; the Power Wheel's submenus and the in-game bar wrap their index. The library grid and
  a game's page do NOT: they are screens, and nav.js's "no vertical wrap" is about exactly them. A
  new list should step with `listMove`, never bare `navMove`.
- **In Settings, Left and Right cross between the panes and never change a value** (the user's
  call, Sept 30 2026): Right or A from a category into its rows at the last-highlighted row, Left
  from any row back to the category. Only the Actions grid walks its tiles sideways first
  (`tileBeside`, since navMove would wrap the line rather than report its end). A value is changed
  from a list: a row with a fixed set of values carries `choices` (`{ value, label, html?, swatch? }`),
  `current` and `pick`, and A opens them all through `openChoice` (`openSettingChoice`), ticked and
  scrolled to the one in force. `sliderRow` lists every step (rounded to the step's decimals, so
  0.05 + 3 x 0.01 is stored as 0.08), `buttonRow` draws combos as buttons (`html`), the accent lists
  its presets with a `swatch` plus a `more` row for a hex code. Toggles still flip on A. The ◂ ▸
  are gone from Settings rows (`settingsRowHtml(r, true)`); the form sheet still nudges sideways and
  keeps them, which is the only reason rows still carry `adjust`.
- The preview's `key` action with `repeat: N` is dropped by the page's 85 ms pacing (only one press
  lands): send separate presses. Its screenshots also lag the input it just sent -- read the state
  with `javascript_tool` before concluding a key did nothing. `pulse()`'s riseIn and the row
  transitions freeze there, so inject `* { transition: none; animation: none }` before a screenshot.
- Rarity bands: ultra rare under 5%, rare under 10%, uncommon under 30% (SuccessStory's 30/10 with
  its ultra-rare step on). The `ach` sort in the filter menu sorts by the unlocked share.
- Loungepad theme 4.2: `.tv-ach` on the focused tile, from the `achievements` / `achPercent`
  template fields, which the app leaves false while the theme's "Achievement progress" is off.
- **"Achievement progress" is per theme** (`LOOK_IDS.achievements`, "achievement-progress";
  `lookAchievements`; the user's ask, Oct 6 2026, for both themes). It covers everything the
  library draws of it: Shelf's tile chips (`achievementChip`), Loungepad's `.tv-ach` and its
  "23/50 achievements" hero line (`achievementMeta`); a game's page and Stats always show it. It
  was one switch under Stats → Achievements ("Show on tiles"), now gone; `AchievementsOnTiles`
  in settings.json is only the fallback a theme with nothing set reads, like `AccentColor`.
- The harness is a scratch console project referencing `bin\Debug\...\Loungepad.dll`: hash rules,
  thinning and summaries, the diff, the built-in readers, a session through `ActivityService`'s
  handlers by reflection (a real launch would go through `LibraryStore.Save` and rewrite the real
  library.json), and the Steam provider against the deployed service.
- **Unlock icons on the charts are HTML over the SVG** (`.chart-pins`, positioned in percentages
  of the same box), because an `<image>` in a `preserveAspectRatio="none"` SVG stretches with it.
  `barChart` therefore gives its SVG an inline pixel height equal to its viewBox height, so the two
  boxes agree; a CSS height on `.chart` for a bar chart would drift them apart. Narrow columns stack
  tiles upward (`column-reverse`), wide ones line them up; **each tile carries an explicit
  z-index** because Chrome painted the column-reverse stack with the earlier sibling on top, which
  cut the "+n" tile's number in half. Icons come from `recent` (35 days), so a month bucket or an
  older day falls back to one count tile.
- **Hand edits to achievements live on the item** (`Edited`, `StoreUnlocked`, `StoreUnlockedAt`):
  `Unlocked`/`UnlockedAt` are always what everything reads, and `AchievementStore.Put` carries every
  edit onto a fresh fetch in place (reference checks skip a failed fetch, which hands back the
  previous set itself). An edit equal to the store's answer is dropped. Hand-logged sessions are
  `Origin = "manual"` and, when `Counted`, move the game's totals on log, edit and remove; recorded
  sessions never do on removal, and imported ones never enter the totals.
- **The form sheet** (`openForm` in app.js, `#overlay-form`) is Settings' rows on the quick-card
  frame, for the editors and the import. It sits after the sheets and before the confirm and the
  text field in index.html (overlays stack in document order) and before `sessionState` in
  `handleInput`. Dates and times are ◂ ▸ to nudge, A on the row to type (`parseTypedDay`,
  `parseTypedTime`, `parseTypedLength`); nothing can be set in the future.

## The Playnite import

- **Fill, never overwrite** (the user's rule, Sept 2026): the import only writes where Loungepad
  has nothing. Playtime, play count and last played each fill only an empty field -- a bigger
  Playnite number does NOT replace ours. Categories become new collections only; a name the user
  already has gets " (Playnite)", and a collection the import made earlier is added to (the record
  says which). SuccessStory lists only for games with no list and no working provider (`canFetch`
  is provider present and not blocked), because a list Loungepad fetches is the store's answer and
  is never edited by the import. Sessions overlapping a recorded one for the same game are
  skipped. Settings only fill empty ones: the SteamGridDB key; Playnite's start-with-Windows is
  NOT brought, since Loungepad's switch has a value even when off. Apply re-checks each change
  against the data at that moment.
- Playnite 10 keeps a **LiteDB v4** database (format byte 7, one collection per file under
  `library\`, or wherever `config.json`'s `DatabasePath` points -- `%AppData%` style or
  `{PlayniteDir}`). LiteDB 4.1.4 carries a critical advisory (GHSA-3x49-g6rc-c284), so the app uses
  **LiteDB 5.0.21 with `Upgrade = true` on a copy in %TEMP%**; the source files are copied with
  `FileShare.ReadWrite` (Playnite may be running) and never opened. Documents are read as
  `BsonDocument` by field name -- no typed mapping. LiteDB returns dates in UTC.
- Games match by the store's id (Steam `cb91dfc9…` → `steam:<GameId>`, GOG `aebe8b7c…`, Epic
  `00000002-dbd1…` → `epic:<AppName>`, Xbox `7e4fbb5e…` by package family name), then a hand-added
  game imported before (`manual:pn-<guid>`), then an exact title with exactly one hit. Hand-added
  Playnite games come in only with a plain File action whose exe exists (`{InstallDir}` expanded).
- GameActivity and SuccessStory keep one JSON per game under `ExtensionsData\<plugin>\GameActivity\`
  and `...\SuccessStory\`, found by folder name. Neither is installed on this PC, so both readers
  were written from the extensions' formats and checked against synthetic files only.
  Playnite's Steam key is encrypted in the Steam plugin's `keys.dat` and is not read.
- Every change goes into `playnite-import.json` (cumulative over imports); Undo walks it back where
  it still stands, taking playtime down by the amount added rather than to the old value.
- `LibraryStore` takes an optional file path now, for the harness: `harness2` in a scratch folder
  reads the real Playnite data (checking its files' timestamps are unchanged) and runs plan,
  apply, a second apply (nothing), and undo against scratch stores. 44 checks (Sept 2026).

## Rest mode and pausing a game

- **A program cannot make a controller wake a sleeping PC.** That is the device asking the bus to
  wake the machine, and whether it may is a per-device driver switch ("Allow this device to wake
  the computer"). On this PC (S3 only, no Modern Standby, Sept 2026) `powercfg /devicequery
  wake_programmable` lists keyboards, mice and the two network cards and NOT the Intel Bluetooth
  radio, the Xbox pad (Bluetooth LE) or the DualSense (Bluetooth), so nothing on the pad can wake
  it from sleep, whatever the app does -- until the user plugged in an **Xbox Wireless Adapter**
  (Oct 1 2026), which IS listed, unarmed, and gets an Allow row. So rest mode is two steps: **Resting** (screen off via
  `SC_MONITORPOWER`, the game frozen, the pad inert; wakes on any pad because it is the launcher
  reading the pad) and **Asleep** (`SetSuspendState`; wakes on whatever the hardware allows). The
  Settings rows read the two powercfg lists and say which case the PC is; `WakeInfo.Classify` picks
  out controller-shaped names and must NOT match "HID-compliant system controller", which is a
  keyboard's power-keys collection.
- `DevicePowerEnumDevices` (the API powercfg wraps) answered ERROR_WMI_INSTANCE_NOT_FOUND and no
  devices from an ordinary process here, while `powercfg /devicequery` worked unelevated. Spawn the
  tool. `powercfg /deviceenablewake` and `/setacvalueindex` need elevation: `Verb = "runas"`, one UAC
  prompt each, Win32 error 1223 is the prompt declined.
- "Require sign-in on wake" is the active scheme's CONSOLELOCK (`0e796bdb-…` under the no-subgroup
  guid): `PowerReadACValueIndex` reads it unelevated (this PC: 1, on), `PowerWriteACValueIndex`
  is access denied (rc 5) unelevated. With it on, an S3 wake lands on the lock screen, so
  `RestService.Wake` defers while `WM_WTSSESSION_CHANGE` says the session is locked
  (`WTSRegisterSessionNotification` on the main window) and wakes on the unlock.
- **Pausing is `NtSuspendProcess` on every pid `TrackedPids()` returns** (`GameLaunchService.Pause`),
  handles kept open until `Resume`. Verified on a scratch copy of cmd.exe: the count stops and
  restarts. Two things hang on a frozen process and are guarded: `SetWindowPos` in the window
  monitor (`EnforceOnTv` is skipped while paused) and `PrintWindow` for the switcher's thumbnails
  (`StartThumbnails` skips the game's windows). `RequestClose` thaws first, because a frozen process
  never reads the WM_CLOSE. The session's `finally` and the window's `Closed` both thaw, so a game
  is never left frozen behind an exiting launcher.
- `RestService` is one thread (the UI thread: a 500 ms DispatcherTimer, WndProc's
  WM_POWERBROADCAST and the pad's `WakeRequested` via the dispatcher) and every outside effect is
  a delegate in `Ports`, so the scratch harness drives it with a fake clock. Idle is the minimum of
  `GetLastInputInfo`'s age, the pad service's `PadInputAgeMs` (Windows counts no XInput or Raw
  Input read as input), and **the time since the last wake**: a wake by the power button or by
  Windows' own resume moves neither stamp, and without `_wokeAt` the idle timer read the hours
  asleep as hours idle and rested again on the very next tick. The harness found that one.
- **The launcher starting is a wake** (`_wokeAt` is set in the constructor). Every input stamp
  (`UserInputWatch`, `PadInputAgeMs`) starts at "never", and so did `_wokeAt`, so the first tick
  read the idle time as forever: from 1.6.0 on, the log has `Rest: no input for 60 min` one to
  four seconds after nearly every `starting` line (fixed Oct 6 2026). A fake-clock harness with no
  input at all: awake at 2 s and at 58 min, warned at 59, resting at 60.
- `SetSuspendState` blocks until the machine is back, so it runs on a worker (`Task.Run`) and its
  `true` arrives after the resume; a `false` within moments is a refusal. A sleep that is asked for
  but never followed by `PBT_APMSUSPEND` within 20 s is treated as refused too. After an automatic
  resume (`PBT_APMRESUMEAUTOMATIC` with no `PBT_APMRESUMESUSPEND`) the service rests on and sleeps
  again after 3 min of nothing, so a maintenance wake does not leave the PC on all night.
- Chromium holds `ES_DISPLAY_REQUIRED` while the launcher's own trailer plays, so the "somebody is
  watching a video" check (`SomethingHoldsDisplay`) only counts when another window is in front.
- The harness is a scratch console project referencing `bin\Debug\...\Loungepad.dll`: the state
  machine through `Ports` with a fake clock, `WakeInfo.Read()` live, and one real
  session through `GameLaunchService` with the scratch cmd.exe for Pause/Resume/RequestClose (62
  checks, Sept 2026). The live session writes the user's real loungepad.log, like any session. Its
  `Rig` class has to come AFTER the top-level statements, and cannot see their `const`s.
- **Never `SendMessageTimeout` a broadcast on the UI thread.** The screen-off used to be
  `SendMessageTimeout(HWND_BROADCAST, WM_SYSCOMMAND, SC_MONITORPOWER, …, 1000 ms)`, which waits the
  full second for every top-level window that does not pump messages -- suspended Store apps, a
  frozen game, anything flagged hung -- and this PC has 326 top-level windows. The user's log showed
  "displays off" 10 to 70 s after "Rest:", with the UI thread blocked the whole time: no dim, no
  tick (so no sleep ever), and whatever was pressed meanwhile woke it the moment the screen went
  dark. That was the whole "finicky" report of Oct 1 2026. `WindowService.MonitorPower` now sends
  the message to our own window (DefWindowProc makes it system-wide, hidden or not; 0-3 ms
  measured), and the log carries the time it took. The one-minute warning is `min(60 s, limit/2)`
  so the 1 and 5 minute test timers still re-arm. `SetSuspendState`'s result and error, and
  `powercfg /lastwake` after every resume, are logged, so a PC that wakes itself names the device.
- **A gamepad Raw Input registration with INPUTSINK makes Windows count the pad's reports as user
  input.** A DualSense streams one every 4 ms whether or not anyone touches it, so with the
  launcher open and a Sony pad attached `GetLastInputInfo` is pinned at zero (measured Oct 1 2026:
  1249 WM_INPUT in 5 s; zero the moment a probe registered, climbing the moment it unregistered;
  XInput polling is innocent), Windows' own display and sleep timers never run, and a display
  turned off with the registration held was turned back on by Windows within **one millisecond**.
  That was "it comes back on instantly": the rest's wake check tripped exactly at its 1.5 s grace.
  So: `HidGamepadReader.SetQuiet(true)` while resting drops the registration (RIDEV_REMOVE) and
  reads each pad through a plain `ReadFile` on its device path from a thread of its own (a file
  read is not input to Windows; the HID class driver gives every reader its own copy of the
  reports), feeding the same `Parse` so the loop's wake-on-press is unchanged; `SetQuiet(false)`
  re-registers. `CancelSynchronousIo` on the thread (`OpenThread` with THREAD_TERMINATE) breaks the
  blocking read. And rest mode no longer reads `GetLastInputInfo` at all: `UserInputWatch` keeps its
  own stamps off mouse and keyboard Raw Input -- a key or button down wakes, the wheel and movement
  past 24 counts in a second only count as activity for the idle timer, and a sensor's jitter counts
  as nothing (the user's rule: clicks wake, movement never). `RestService.OnDisplayState` (the
  GUID_CONSOLE_DISPLAY_STATE notification) dims the screen again two seconds after Windows lights it
  for movement, at most once in ten seconds. The one WM_INPUT read is in `MainWindow.WndProc` and
  is handed to the pad reader or the input watch by type.
- **Sleep after rest is OFF by default** (`SleepAfterRestMinutes` -1; the user's call, Oct 1 2026): a
  fresh install only ever rests, and sleep is something the user turns on once they know what can
  wake their PC. A pad that cannot wake a sleeping PC would send them to the desk for the mouse,
  which is the one thing the launcher exists to prevent. The page's fallback for a settings file
  without the key, the mock's defaults and the harness's sleep case all say -1 / set it explicitly.

## Controller input on UAC and the sign-in screen: the input service (1.8.0)

- **The feature is on the branch `secure-desktop-input`** (Oct 8 2026), off main at 75c1660; main has
  none of it. `docs/INPUT-SERVICE-HANDOFF.md` is the running log and `docs/SECURE-INPUT.md` the design;
  read both first. Three projects: `Loungepad.Input` (shared, compiles NativeMethods, HidNative,
  HidGamepadReader and StickPointer from the launcher's tree by source -- the launcher `Compile Remove`s
  them and references the library), `Loungepad.Service` (LocalSystem, session 0, starts the agent in
  the console session) and `Loungepad.InputAgent` (SYSTEM with uiAccess, one worker process per
  desktop). `tests\Loungepad.Input.Tests` is the suite (64 checks) plus opt-in probes;
  `node --test tests\input-service-ui.test.cjs` is the page's.
- **The agent moves the pointer on every desktop, from one formula** (`StickPointer`; the user's
  report of Oct 8 2026: "very jittery, slow on the main desktop and fast on the admin prompts"). It
  used to be two: the launcher curved the deflection and moved along the stick's direction, the agent
  curved each axis (41% faster on a diagonal). And on Default every move went `GetCursorPos` + dx
  through the pipe as an absolute target, so a tick that read the pointer before the last move had
  landed aimed from a stale base and threw that move away -- slow and uneven for as long as a round
  trip took, on the one desktop that used the pipe. Now the launcher sends only its policy for the
  tick (`MovePointer`, `ScrollWheel` on the request, from the mouse branch's `SetPointerPolicy`, read
  as off once 50 ms old so every `continue` path stops the agent unnamed) and the worker's own 8 ms
  tick moves the pointer from a fresh capture (`SecureMapper.MovePointer`; touch travel left
  undrained for the reply). Buttons, keyboard, combos, bindings and the touchpad stay the launcher's.
- **Protocol fields are optional so the two sides update apart.** The reply's `PointerOwner` is the
  agent claiming the pointer; an older agent never sends it and the launcher moves the pointer through
  the pipe as before; an older launcher never asks. A version bump instead would have put the old
  worker's background mapper and the new launcher's local mapper on the pointer at once. The settings
  row says the installed service is older while a connected agent does not claim the pointer.
- **Anything that moves the pointer by a delta goes through `NativeMethods.MoveCursorBy`**: it aims
  from where the last move was heading while the pointer still sits where it was seen before that
  move went out (within 100 ms), and keeps the aim inside the virtual screen -- an aim run off the
  edge by a push would have held the pointer there until the stick brought it all the way back.
  `TouchpadGestures.MoveCursor` is this; the touchpad still crosses the pipe.
- **The navigation hook (`GamepadNavigationFilter`, WH_KEYBOARD_LL dropping VK 0xC3-0xDA) is on a
  thread of its own.** A low-level hook runs on its installing thread within `LowLevelHooksTimeout`,
  and the worker's dispatcher is what shows the secure keyboard. `--navigation-hook-stall-probe`
  (opt-in: it injects VK 0xDA, an inert gamepad key, a few times) showed on 26200 that a hook on a
  thread blocked 1.5 s survived but missed the keys sent during the stall -- Windows navigation
  would have acted on those -- while the filter on its own thread caught 6 of 6.
- **To check the installed worker's hook without a pad**: read `SuppressedNavigationEvents` out of
  `HKLM\SOFTWARE\Loungepad\Input`'s `AgentHealth`, `SendInput` one VK 0xDA down/up with something
  inert in front, wait for the next heartbeat (1 s), read again: a live hook counts 2 (checked Oct 8
  2026 on worker 131320, 0 → 2). XInputUWPFix is the same hook, which is the evidence that Windows'
  controller-to-VK events pass through the chain at all. The counter is not proof of behaviour.
- **A DualSense on the cable while it is paired is two HID pads to Windows, and the reader read
  both** (Oct 8 2026, after the agent took the pointer: still "very jittery on both desktops").
  `--hid-probe` saw the Bluetooth instance at ~380 reports/s and the USB one at ~150/s, the same
  stick on each, and `_last` flipping 653 times in 3 s; the radio's copy lags the cable's, so a
  push read as a sawtooth. `HidGamepadReader.Reconcile` shadows a Bluetooth instance while a wired
  instance of the same pad (`HidPad.SamePhysicalPad`: vendor, product, and not two different
  serials) is present: parsed, never the reading, never touch travel. The dispatcher tick was
  cleared first (`--dispatcher-cadence-probe`: 8.00 ms mean, max 8.5, at every priority). The
  launcher's log had said it all along: two `reading full reports` lines for one pad, 0x31 and 0x01.
  The agent's pad selection (`SecureMapper.SelectPad`) measures movement against an anchor past
  `StickPointer.StickNoise` (1600, shared with GamepadService's `Moved`), not reading to reading,
  or a resting Xbox pad's wobble steals the pointer from the DualSense in hand.
- **An absolute mouse move lands on pixel floor(n * width / 65536), so aim at
  (x * 65536 + 32768) / width.** `MoveCursorTo` used `(x * 65535 + 32767) / (width - 1)` and
  landed 1186 of 2560 columns and 689 of 1440 rows a pixel off, plus the row a pixel off on every
  horizontal move (`--absolute-mapping-probe`, Oct 8 2026, 2560x1440). Read back the next tick, that
  was 16% lost motion and a vertical wobble at 125 Hz: "jumps like a mouse on a VDI" once the agent
  moved the pointer. `--pointer-sweep` drives the real pointer from the shared mover at 8 ms through
  the absolute move, a relative move and SetCursorPos and compares the steps; after the fix all
  three are 11-12 px, sd 0.66. Measure injection with those two before suspecting a loop.
- **Packaging needs the dist launcher closed** (`package.ps1` deletes `dist\v1.8.0`, which it runs
  from) and the two scripts sequential (`package-input-service.ps1`, then `package.ps1`; both sign).
  The service install needs UAC -- `install-input-service.ps1 -SourcePath <package>` in an admin
  PowerShell keeps the enabled flag and profile -- and the in-app flow only installs from a published
  release. A `dotnet build` launcher cannot authenticate the installer. The Xbox pad here is paired
  over Bluetooth LE and shows in no XInput slot unless powered on; the DualSense on USB is what the
  worker's "controller present" was.
