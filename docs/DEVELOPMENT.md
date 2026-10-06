# Development

[README](../README.md) · [User guide](GUIDE.md) · [Themes](THEMES.md) · [Development](DEVELOPMENT.md)

## Build & run

Requirements: **.NET 8 SDK**, **WebView2 Runtime** (preinstalled on Windows 11).

```bash
dotnet build Loungepad.sln
```

Run `Loungepad\bin\Debug\net8.0-windows\Loungepad.exe`, or open `Loungepad.sln`
in Visual Studio 2022 and F5. Pass `--windowed` for a 1280×720 debug window (no always-on-top,
no focus guarding) instead of the full-screen TV mode.

The UI can also be previewed in a plain browser (it self-mocks sample data when not hosted in
WebView2): serve `Loungepad/ui/` with any static server and open `index.html`.

To build what goes on a release:

```powershell
.\tools\package.ps1            # or -Version 1.1.0 to stamp the tag you are about to push
```

It publishes self-contained and single-file into `artifacts\`, checks that every file under `ui\`,
`themes\` and `vortex-bridge\` was embedded (they ship inside the exe and are read back through
`ShippedFiles`), signs the exe when `tools\signing.json` exists, and writes two files to
`dist\v<version>\` with their SHA-256:

- `Loungepad.exe`, what people download. The updater in this and later builds looks for an asset
  of exactly that name on the latest release of `sukumar-v/Loungepad`, with a tag it can read as
  a version (`v1.5.0`), and refuses an exe whose built version is not the tag's.
- `Loungepad-v<version>-win-x64.zip`, only for installs up to 1.6.3, whose updater reads nothing
  but a zip with `ui\index.html` in it. It holds the same exe.

Attach both exactly as the script names them. A build from `dotnet build` never updates itself;
only the single-file build this script makes does.

`.\tools\make-icon.ps1` draws the app icon (`Loungepad\Loungepad.ico`, every size from 16 to
256 px, and `loungepad-icon.png`) as shapes, so each size is drawn at its own resolution and stays
legible in the tray. The README's header lockup (`assets\loungepad-logo-dark.png` and
`-light.png`, one per GitHub colour scheme) and the screenshots in `assets\screens\` are made
outside this repository, by the project that cuts the trailer from screen recordings of the app;
the screenshots are frames of those recordings, scaled to 1600 px wide.

## Architecture

A native WPF shell (.NET 8) hosting the UI as an HTML/CSS/JS app in WebView2, bridged by JSON
messages, with every system-level feature behind Win32 P/Invoke.

```
Loungepad/
  MainWindow.xaml(.cs)    Borderless, topmost, taskbar-less window; hosts WebView2 on the TV display
  UiBridge.cs             JSON message bridge: web UI <-> native services
  ui/                     The design-faithful UI (index.html, app.css, app.js), 1920x1080 stage
                          scaled to the display; embedded in the exe and served as loungepad.ui
  Services/
    DisplayService.cs     Monitor enumeration + primary-display switching (ChangeDisplaySettingsEx)
    GameLaunchService.cs  Launch orchestration, process tracking, window repositioning, playtime
    LibraryScanner.cs     Steam (ACF/VDF + librarycache art), Epic (.item manifests),
                          GOG (registry), Xbox (MicrosoftGame.config + AppModel repository),
                          ROM folders (one system per folder, see Models/Emulation.cs)
    RomTitles.cs          A game's title out of a ROM's file name (No-Intro / GoodTools tags)
    EmulatorLaunch.cs     The emulator command line for a ROM: {rom}, {core} and friends
    EmulatorDetection.cs  Finds installed emulators (folders, registry, Start Menu, Steam) and
                          ROM sources (RetroArch playlists, PCSX2/DuckStation game lists,
                          folders named after a system)
    RetroArchPlaylists.cs Reads RetroArch's .lpl playlists: ROM path, database label, core
    GamepadService.cs     Gamepad polling: UI navigation events + gamepad-mouse (SendInput/SetCursorPos),
                          merging XInput with the HID reader; publishes which pad family is in use
    HidGamepadReader.cs   Raw Input + hid.dll: DualSense, Switch Pro and generic pads, in XInput's shape
    VirtualKeyboardService.cs  Touch keyboard (TabTip) via ITipInvocation COM
    CursorService.cs      Optional system-wide pointer hiding while the D-pad drives
    StartupService.cs     HKCU Run key registration
    UpdateService.cs      Checks GitHub releases, downloads the exe, swaps it in place, restarts
    ShippedFiles.cs       Reads ui/, themes/ and vortex-bridge/ back out of the exe, where they ship    TrailerCache.cs       Keeps a copy of each trailer the page plays, capped, oldest out first
    ActivityStore.cs      The play sessions: activity.json, plus one readings file per session
    ActivityService.cs    Records each sitting and samples the hardware while the game runs
    HardwareMonitor.cs    CPU, GPU and memory from Windows; frame rate, temperatures and power
                          from RivaTuner Statistics Server, MSI Afterburner or HWiNFO when running
    AchievementStore.cs   The achievement lists, one JSON per game under achievements\
    AchievementService.cs Fetches lists, refreshes after each session, announces the difference
    AchievementProviders.cs  Steam, Xbox, Epic, GOG and RetroAchievements, each asked by its own id
    RetroHash.cs          The hash RetroAchievements identifies a ROM by (header rules per system)
    Storage.cs            JSON persistence in %APPDATA%\Loungepad (settings, library, log, covers)
  TrayIcon.cs             The notification-area icon: show, update, quit
  Interop/NativeMethods.cs   All P/Invoke declarations
  Interop/HidNative.cs       Raw Input and hid.dll, for the HID gamepad reader
```

## Feature notes

- **Appearance** — Settings → Appearance: the theme, then its look (accent colour, button hints),
  its animations (on or off, and how quick) and the options the theme declares for itself. All of
  it is kept per theme, so each theme has its own, and a restore row puts one theme back. Settings
  itself is a box over the library, which stays visible and blurred round it. See
  [Themes](THEMES.md).
- **TV display targeting** — pick the TV in Settings → Display. The launcher window is placed
  there with `SetWindowPos` (pixel-exact, per-monitor DPI aware). Before a game launches the TV
  is made the Windows *primary* display (most games open on the primary), and the previous
  primary is restored when the game exits. As a fallback, for ~30 s after launch any visible
  game window that opened on another monitor is moved onto the TV.
- **Process tracking** — Steam/Epic launches go through their store URI, so the real game
  process is found by matching process image paths against the game's install directory
  (`QueryFullProcessImageName`); direct exe launches are tracked directly. Playtime, session
  count and last-played are recorded locally on exit.
- **Process tracking follows launcher chains** — sessions stay alive as long as *any* process
  from the game's install directory runs, so pre-launchers (REDprelauncher → REDlauncher →
  Cyberpunk2077.exe) don't end the session early. Per-game **launch arguments**
  (e.g. `--launcher-skip` for Cyberpunk) and a **direct-exe override** that bypasses the store
  launcher are available under Detail → Manage.
- **Game options menu** — **Y** on any game opens a context menu: View game (the full detail
  page), favorite, add to collection, change cover art, and **Hide**. Hidden entries drop out of
  the library entirely and collect under the **Hidden** collection, which is how you get rid of
  non-games that the platform scanners pick up (benchmarks, wallpaper tools, redistributables).
- **Library organisation** — Favorites, automatic per-platform collections (Steam, Epic, GOG,
  Xbox, Manual), custom collections, and Hidden. The **X** overlay has two dropdowns: a
  categorised **multi-select Filter** (platform / status / favorites) and a single-select
  **Sort** (A–Z, Z–A, recently played, most played, largest, smallest); **Y** resets both.
  LB/RB switches between Library, Collections and Settings.
- **Actions** — Settings → Actions is a grid of the programs on this PC that we ship a pack of
  shortcuts for (the six big browsers, VLC, Spotify, Discord, File Explorer, RetroArch, PCSX2,
  Dolphin), plus **Everywhere** (volume, media keys, Show desktop, Alt+Tab, snapping, a
  screenshot, Enter, Escape, Close window) and anything added by hand from the list of open
  windows or by browsing for an .exe. An action is a keyboard shortcut the program itself
  understands, a name, an optional pad button or chord, and whether it sits on the wheel. The
  button is recorded by pressing it (hold several for a chord); the shortcut is picked on an
  on-screen keyboard, or pressed on a real one. Bindings fire only while that program is in
  front and Loungepad is not — never inside the launcher, never inside a focused game unless
  "Stay active while a game is focused" is on — and an app's binding beats Everywhere's for
  the same button. The keyboard button, the menu combo and the screenshot button always win
  over a binding, and the row says so. Everywhere's arrow keys are bound to the D-pad out of
  the box, so the D-pad is arrow keys on the desktop. A pack's defaults are never overwritten:
  `actions.json` holds only what differs, so a new default arrives with the next build and
  **Reset to defaults** puts one app back.
- **Stats** — every sitting with a game is recorded: when, how long, and how the PC did.
  While a game runs a reading is taken every few seconds (Settings → Stats): CPU, GPU and
  memory from Windows itself, and the frame rate, temperatures and power from RivaTuner
  Statistics Server, MSI Afterburner or HWiNFO when one of them is running -- nothing to set up,
  whichever is there is used. A game's page has a **Stats** button (and its Y menu an entry)
  with its sessions, a bar a day for the last month and each session's averages; **A** on a
  session draws its readings as charts. **LB** on the library, or **Open Stats** at the top of
  Settings → Stats, opens the Stats screen: playtime today, this week and overall, by day or
  month, by game and by store over a chosen period, the latest sessions and the latest
  achievements. The in-game menu shows how long the sitting has been going and the live
  readings. Sessions live in `activity.json` with the readings under `activity\`; a session can
  be removed from its sheet and every session cleared from Settings. Playtime totals on the games
  are the launcher's own count and are not changed by any of this.
- **Achievements on the timeline** — every chart of playtime carries the day's unlocks over its
  bar as the achievements' own icons, rarest first, with a **+n** tile when there are more than
  the column has room for (a month, or a day older than the last five weeks, shows the count).
  The day timeline puts them at the moment they happened. An achievement is tied to the session
  it was earned in (same game, inside the session give or take two minutes). The Stats
  overview's chart is a day picker: **◂ ▸** walk the last four weeks and the picked day's
  unlocks are listed under it with their times; **A** opens the day as a timeline -- each
  session with what it unlocked under it, anything unlocked away from a recorded session at its
  own time, **◂ ▸** to the next day with anything in it. A game's session rows show what each
  unlocked, and a session's charts mark every unlock at the moment it happened.
- **Achievements** — each game's list from its own store, asked by the id the library already
  has, never by title: Steam by app id straight from Steam, with the Steam sign-in or your own
  Web API key under Library (never through the metadata service), Xbox by title id, Epic by
  catalogue namespace and GOG by product id through their sign-ins, and ROMs through
  RetroAchievements (username and web API key under Settings → Stats), matched by the ROM's
  hash where the system allows it and by title otherwise. Lists are fetched in the background
  after a scan, again a few seconds after a session ends -- the difference is what the unlock
  cards announce once the game has closed -- and whenever a game's page opens with a stale one.
  The page shows the unlocked share as a stat, the **Achievements** button opens the list with
  icons, descriptions, unlock dates and rarity (ultra rare under 5%, rare under 10%, uncommon
  under 30%), filtered with **X** and ordered with **Y**; hidden ones stay hidden until they are
  unlocked, or until **A** reveals one. Tiles carry the share (Settings → Stats → Show on
  tiles), the filter menu can sort by it, and the Stats screen's Achievements category has the
  totals, the rarity breakdown, unlocks by month and every game by progress. Lists are kept under
  `achievements\`, icons are cached under `%LOCALAPPDATA%\Loungepad\achievements\`.
- **Editing by hand** — on a game's Achievements sheet **A** edits the highlighted one: unlocked
  or locked, and when (◂ ▸ a day or a quarter hour, **A** on the row to type it). The store's
  own answer is kept, every refresh keeps the edit, and **Undo my edit** puts it back; an edited
  one is marked EDITED. On a game's Stats sheet **Y** logs a session Loungepad did not see --
  another PC, or the launcher closed -- with its day, start and length, counted toward the
  game's playtime unless switched off; **A** edits a logged one and **X** removes it (taking its
  time back off the total). Every game's page has the Stats button for this.
- **Import from Playnite** (Settings → General) — reads Playnite's library from a copy (Playnite
  itself is never changed) and brings in playtime and play counts, favourites and hidden games,
  categories as collections, games added to Playnite by hand, GameActivity's sessions with their
  readings, SuccessStory's lists and the SteamGridDB key. **It only fills what Loungepad has
  nothing for**: a playtime, collection, list or setting Loungepad already has is left exactly
  as it is, a category whose name you already use comes in as "Name (Playnite)", and a session
  that overlaps one Loungepad recorded is skipped. The sheet shows what each part would bring
  first; a second import brings nothing the first did, and **Undo the import** takes back
  exactly what it brought, keeping anything changed since. Needs LiteDB 5, which reads
  Playnite's v4 database by upgrading the copy.
- **Adding games** — a **+ Add game** tile sits at the end of the library grid, and the same
  action lives under Settings → Library. File dialogs drop always-on-top while open, otherwise
  they open *behind* the full-screen launcher and appear to do nothing.
- **Pointer / D-pad input modes** — the mouse pointer hides and hover stops stealing focus as
  soon as the D-pad drives; moving the stick or a real mouse brings it straight back. Inside the
  launcher this is free; Settings → "Hide pointer system-wide" extends it to the rest of Windows
  by swapping the system cursors (restored on exit, on crash and on process exit).
- **Gamepad** (XInput pad 0, plus any HID pad):
  - Xbox and XInput-compatible pads are read through XInput. Everything else -- Sony's
    DualShock 4 and DualSense, Nintendo's Switch Pro controller, generic HID pads -- is read
    through Raw Input (`RIDEV_INPUTSINK`, so input arrives even with a game in front) and parsed
    with hid.dll from the pad's own descriptor. Sony pads are mapped by their well-known button
    order; the Switch Pro is mapped by *position* (its B is the bottom button, so it does what
    A does on an Xbox pad, and the legend draws a B next to "Select"); anything unrecognised
    gets the Sony order, which most generic pads follow. Two pads can be attached at once: the
    one that moved last is the one driving.
  - The **on-screen hints follow the pad in hand**: Xbox letters, PlayStation shapes, Switch
    letters, a four-button diamond for a generic pad, and key caps once a keyboard or mouse is
    used. Settings rows that name gamepad buttons always draw the last gamepad used.
  - **The touchpad on a DualSense (and DualShock 4) is a precision touchpad**, with a laptop's
    gestures:

    | Gesture | Does |
    |---|---|
    | One finger | Moves the pointer, faster for a flick and slower for a nudge |
    | Tap, or press the pad | Left click (a press is held for as long as it is held, so it drags) |
    | Tap twice | Double click |
    | Tap, then touch and hold | Holds the left button while the finger moves: drag a window, select text. Lift and touch again quickly to carry on; tap to let go |
    | Two-finger tap, or press with two fingers down | Right click |
    | Two-finger swipe | Scrolls, up and down or sideways, and coasts after a flick |
    | Pinch or spread | Zooms (Ctrl + wheel) |

    The pad reports two touch points, so three- and four-finger gestures are not possible. The
    pointer holds still around a press, so clicking does not nudge it. Sensitivity, tap-to-click,
    tap-and-drag, scroll direction and scroll speed are under Settings → Controller. The touch data sits in the vendor part of
    the pad's report, so Sony pads are read from their full report directly; on Bluetooth the
    launcher asks the pad for that report the way Steam does.
  - Known limits: a Switch Pro controller over USB sends nothing until it has been through
    Nintendo's handshake, so connect it over Bluetooth. Battery is shown for Bluetooth pads
    only, and for a pad on the cable it is whatever Windows last saw over Bluetooth.
  - Launcher focused → D-pad/A/B/X/Y drive the UI exactly as the on-screen legend shows;
    the left stick moves the mouse cursor (hover focuses, so stick and D-pad stay in sync),
    right stick scrolls.
  - Launcher not focused, no game running (e.g. you tabbed to the desktop) → the configured
    buttons (defaults: A = left click, B = right click) send real mouse clicks.
  - Game running → the whole service idles so games with native controller support never see
    phantom input (opt back in with Settings → "Stay active while a game runs").
  - **The menu combo** (the Xbox button by default; Settings → Controller) opens the Power Wheel
    on a tap and shows or hides the launcher on a half-second hold, which is the in-game menu
    while a game runs. The old tap-for-launcher, double-tap-for-wheel shape is still there as
    **Combo gesture → Double tap**.
  - **Settings → Controller → Windows and Steam** shows, and switches, the three other things
    that react to the Xbox button, with **Give Loungepad the Xbox button** to turn all of them
    off in one press:
    - **Xbox Game Bar on the controller**: Game Bar opening on the Xbox button, and View + Menu
      standing in for the Xbox button in apps. Win + G still opens Game Bar.
    - **Windows Xbox mode**: while it is on, holding the Xbox button opens Task View. Win + Tab
      still does.
    - **Steam on the controller**: "Guide Button Focuses Steam", Steam's Guide button
      shortcuts, and Steam's desktop layout. That layout's default turns the left stick and
      the D-pad into arrow keys, A into Enter and the right stick into a mouse whenever no game
      is running, on top of what Loungepad does with the same pad; off writes an empty layout
      for every kind of pad (keeping the Share button as a Steam screenshot) and on puts back
      whatever was there. Steam rewrites its settings when it closes, so changing this closes
      Steam, edits them, and starts it again in the tray; it is refused while a game is running.
    The two Windows ones are per-user registry values and may need a sign-out to reach a
    running Game Bar. Turning one back on restores Windows' own default.
  - Deadzone, sensitivity and the acceleration exponent (slow near center, fast at full
    deflection) are sliders in Settings.
  - The header shows a controller **battery gauge** — only for wireless pads that actually
    report a battery; wired controllers show nothing.
- **Keyboard and mouse** — the page has keyboard focus whenever the launcher is the active
  window. Arrows navigate, Enter (or Space) is A, Esc (or Backspace) is B, X and Y are X and Y,
  `[` and `]` are the shoulders, `/` opens search and M opens Settings. On the library itself the
  keys follow its hint bar: Enter launches, X filters, **Ctrl** opens a game's options, `/`
  searches, **Tab** opens Settings and **`` ` ``** opens Stats; Esc is Back there as everywhere
  (Y, M and `[` still work). With the mouse, hovering
  highlights and clicking selects; a right click on a game opens its options and a right click
  elsewhere is Back; clicking the dimmed screen around a menu closes it; the arrows on a settings
  row step its value; and every entry in a hint bar can be clicked to press that button.
- **Virtual keyboard** — the Loungepad Keyboard by default: a WPF window that never activates, so
  keystrokes land in whatever had focus. View toggles it (button, press or hold, and hold time
  configurable); text inputs in the UI (collection names, launch arguments) raise it
  automatically, and on the library View opens search, which raises it too. Settings → Keyboard can
  switch to the Windows *touch* keyboard (TabTip, via the ITipInvocation COM interface) or osk.exe;
  TabTip only takes a gamepad on its own "Gamepad" layout, which no API can select.
- **Rest and sleep** — Settings → General → Rest and sleep. After **Rest after** (an hour by
  default; one and five minutes are there for trying it) with nothing touched on the pad, keyboard or mouse, the TV is blanked
  (`SC_MONITORPOWER`), the game's processes are frozen (`NtSuspendProcess`, what PlayState does;
  **Pause the game while resting** turns that off for online games or an anti-cheat that objects)
  and the pad goes inert; a button on any controller, a key or a mouse button wakes the screen and
  thaws the game. Movement never does: the mouse and keyboard are watched through Raw Input
  (`UserInputWatch`), and while resting the pads are read directly rather than through Raw Input,
  because that registration makes Windows count a DualSense's report stream as input and relight
  the display at once (`HidGamepadReader.SetQuiet`). A display Windows lights for movement is
  dimmed again two seconds later. **Also while a game is running** is the console behaviour and the default.
  After **Then sleep the PC after** (off by default; straight away, or minutes to hours) the PC is put to sleep with
  `SetSuspendState`, so hybrid sleep and hibernate-after apply as Windows has them. Windows going
  to sleep on its own (its timer, the power button) is treated as the same rest: the game is frozen
  for it and thawed when somebody is back, and a wake that lands on the lock screen waits for the
  sign-in. Something else holding the display awake — a video in a browser — counts as somebody
  watching; the launcher's own trailers do not. The Power Wheel's **Rest** spoke and **Shortcuts →
  Sleep PC** do both on demand, as do **Rest now** and **Sleep the PC now** in the same Settings
  section, and the in-game menu's **Pause game** is the freeze alone.
  - **Waking from sleep** is the one part no program can supply: a controller wakes a sleeping PC
    only if its driver offers "Allow this device to wake the computer", which on a desktop in S3
    is typically the keyboard, mouse and network card and not the Bluetooth radio. The rows read
    `powercfg /devicequery wake_programmable` and `wake_armed`, list every controller-shaped device
    (Xbox Wireless Adapter, a wired pad, a Bluetooth radio) with **Allow** for each that could
    (an elevated `powercfg /deviceenablewake`, one UAC prompt), and say which case this PC is. On a
    Modern Standby board a paired Bluetooth pad usually can. **Sign-in after waking** reads the
    active power scheme's `CONSOLELOCK` and turns it off the same way, so a wake lands back where it
    was rather than on a lock screen the pad cannot type into.
- **Lock screen & startup** — Settings → "Launch Loungepad at login" (HKCU Run key),
  plus a step-by-step in-app guide: a Windows Hello PIN for couch-friendly sign-in, rest mode and
  what wakes the PC, and auto-starting the launcher.
- **Focus guarding** — no taskbar button; if the desktop steals focus while no game runs, the
  launcher re-activates itself (Settings → "Keep launcher focused"). Alt-Tab still works.

Data lives in `%APPDATA%\Loungepad\` (`settings.json`, `library.json`, `actions.json`, `covers\`,
`activity.json` and `activity\`, `achievements\`, `playnite-import.json` after an import,
`loungepad.log`). Delete `library.json` to force
a clean rescan. Trailers and achievement icons are kept apart, under `%LOCALAPPDATA%\Loungepad\`
(`trailers\`, `achievements\`), and both folders can be deleted at any time.

## Design → native mapping (flagged deviations)

The imported design is the source of truth for palette, type, spacing, focus motion, backdrop
behaviour and screen structure. Things that could not map 1:1 to local desktop reality:

1. **ACHIEVEMENTS stat** (detail screen) — now real: fetched from each store by the game's own
   id (see Achievements above). It sits beside **SESSIONS**, which is still counted locally, and
   is left out for a game whose store has no list rather than shown as a dash.
2. **"Y Screenshots & saves"** legend on the detail screen — no portable local source for
   screenshots/cloud-save state. Replaced by the Manage menu (cover art, launch arguments,
   direct-exe override).
3. **Continue-row progress bars** — the design implies completion progress, which no platform
   exposes locally. The bar shows playtime relative to your most-played recent title.
4. **Game descriptions** ("A hand-drawn descent through…") — not available locally; the detail
   screen shows platform / launch route / install path instead.
5. **Genre filter and Metacritic sort** — no offline data source (store metadata needs
   authenticated web APIs), so the Filter overlay offers platform/favorites/installed filters
   and A–Z / Z–A / recency / playtime / size sorts instead.
6. **Not-installed titles** — the design dims games that are owned but not installed. Steam's
   library is imported through its Web API, and Epic, GOG and Xbox libraries through a sign-in to
   each store, when set up (see above). Without them, "not installed" only shows for entries whose
   files have been removed since scanning.
7. **Sample imagery** — the design's placeholder photos are replaced by real cover art
   (Steam caches both portrait covers and landscape banners; Xbox supplies square store logos;
   manual entries use user-picked images) with a procedural gradient-and-initials placeholder
   when art is unavailable (Epic/GOG have no local art cache).
8. **Fonts** — Manrope / IBM Plex Mono load from Google Fonts when online; otherwise the UI
   falls back to Segoe UI / Consolas.

## Explicitly out of scope

No input injection at the Windows lock screen / Secure Desktop (OS restriction; handled outside
this app). The in-app wake guide recommends automatic sign-in for a couch-only setup.

Making a controller wake a *sleeping* PC when its driver does not offer it. That is a bus-level
wake the device has to ask for; the app arms every device that can be armed and says which cannot.
Rest mode's screen-off half needs none of it, which is why it exists as a separate step.
