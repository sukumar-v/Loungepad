# Loungepad user guide

[README](../README.md) · [User guide](GUIDE.md) · [Themes](THEMES.md) · [Development](DEVELOPMENT.md)

Everything Loungepad does, in detail. The [README](../README.md) is the short version.

- [Install, first run and updates](#install)
- [No keyboard. No mouse.](#no-keyboard-no-mouse)
- [Your library, found automatically](#your-library-found-automatically)
- [Emulators and ROMs](#emulators-and-roms)
- [Mods](#mods)

## Install

**[Download the latest release](https://github.com/sukumar-v/Loungepad/releases/latest)**, unzip it
anywhere, and run `Loungepad.exe`.

Nothing else to install: the .NET runtime is bundled. Windows 11 already has the one thing that is
not — the **Edge WebView2 Runtime** — and on Windows 10 the app will tell you where to get it.
Keep `Loungepad.exe` and the `ui` folder together; the UI is loaded off disk at startup.

The build is not code-signed yet, so Windows SmartScreen will warn the first time you run it:
choose **More info → Run anyway**.

On first run Loungepad opens a short setup, all of it driven with the controller: which screen is
the TV, the look, your store accounts (for the games you own but have not installed), the
emulators and ROMs it found, freeing the Xbox button from Game Bar, Xbox mode and Steam, a PIN so
the lock screen can be passed with the controller, and starting with Windows. When Playnite or
Vortex is on the PC, it offers to bring Playnite's library over and to connect Vortex. While you
answer, it is already scanning Steam, Epic, GOG, the Xbox app and every emulator and ROM folder it
can find, and the left of the screen shows what it has found so far. Every answer is saved as it is
given, **X** skips the rest, and **Settings → General → First-time setup** runs it again. Settings,
library and cover art live in `%APPDATA%\Loungepad`; uninstalling is deleting the folder you
unzipped.

`Loungepad.exe --windowed` opens a 1280×720 window instead, which is easier to poke at from a desk.

While it runs, Loungepad has an icon in the notification area (the `^` by the clock, until you
pin it): click it to bring the launcher back, or right-click to check for updates or quit.

### Updates

Loungepad updates itself from this repository's releases. With **Settings → General → Update
automatically** on, which is the default, it checks every few hours, downloads a new version in
the background, and installs it the next time it starts. The **Loungepad** row above that toggle
checks on demand and installs straight away (it restarts the launcher, so not while a game is
running). Turn the toggle off and nothing is fetched until you ask.

An update replaces the files in the folder you unzipped into, so that folder has to be one your
account can write to — anywhere under your user folder is; `Program Files` is not.

## No keyboard. No mouse.

Most couch launchers get you as far as starting a game, then leave you stranded the moment you
need to do anything else — dismiss an update prompt, log into a store, close a window that opened
on the wrong screen. You end up walking to the desk for the mouse anyway.

Loungepad is built so that never happens. **The controller is a complete input device**, not just
a menu remote:

- **The left stick is the mouse.** Deadzone, sensitivity and an acceleration curve — slow near
  the centre for precision, fast at full deflection to cross a 4K screen — are all sliders in
  Settings. The right stick scrolls. A and B are left and right click, live across the whole
  desktop the moment the launcher isn't in front.
- **The keyboard comes to you.** Press View (Create on a DualSense) to raise an on-screen
  keyboard, so you can type a search, a password or a message without getting up. Text fields
  inside the launcher raise it on their own. The Loungepad Keyboard never takes focus from the game
  underneath, suggests words as you type (from Windows' own dictionary; the right stick's click
  takes the first), and can add function keys, a number pad, navigation keys and Ctrl/Win/Alt, or
  grow and shrink — from Settings, or from the gear on the keyboard itself. The Windows touch
  keyboard is there too.
- **The Power Wheel runs Windows.** One tap of the Xbox button, from anywhere — including
  mid-game; hold it instead to bring Loungepad back — and you can switch to any open window (it gets dragged onto the TV with you), fire
  a saved shortcut, summon the keyboard, re-centre a lost pointer, close the window in front of
  you, rest (the TV dark and the game paused until you press a button again) or put the PC to
  sleep.
- **It rests like a console.** Put the pad down and after an hour with nothing pressed (Settings →
  General → Rest and sleep, from ten minutes to never) the TV goes dark and the game is paused
  where it stands — its processes frozen the way PlayState freezes them, drawing and computing
  nothing — and a button on any controller, a key or a mouse click brings both back exactly where
  they were; a nudged mouse or a drifting stick never does. Left
  resting, the PC can go to sleep as well (off unless you turn it on; straight away, or after
  any number of minutes), which is where the electricity goes. Whether the controller can wake it from *sleep* is up to its
  receiver's driver rather than any program: the same Settings rows list every controller device
  that could, allow it with one press, say plainly when none can (a Bluetooth pad on most
  desktops: a keyboard, mouse or the power button wakes it then), and turn off the sign-in Windows
  would otherwise ask for on the way back. The in-game menu has a Pause tile for the same freeze on
  demand — a game with no pause of its own can be walked away from.
- **Actions put an app's shortcuts on the pad.** The wheel's first spoke opens a second wheel
  with the shortcuts of whatever was in front: new tab, close tab, back, reload and full screen
  in a browser; play, pause and subtitles in VLC; save and load state in RetroArch; volume, media
  keys and Alt+Tab everywhere. Point at one and press A, or give an action a button of its own —
  **Y** for a new tab, say — and it works in that app whenever Loungepad is out of the way,
  without the wheel. Settings → Actions is where the packs live, where any shortcut, name or
  button can be changed, and where a program we have no pack for gets one of its own.
- **The pointer knows when to disappear.** Touch the D-pad and the cursor hides and stops
  stealing focus; nudge the stick or a real mouse and it comes straight back. Optionally
  system-wide, so it stays hidden out on the desktop too.
- **Any controller.** Xbox pads through XInput; a DualSense, a DualShock 4, a Switch Pro
  controller (over Bluetooth) or a generic pad through Raw Input, so they keep working while a
  game is in front. The button hints along the bottom, in every menu and in Settings are drawn as
  the buttons of the pad in your hand: the green A becomes a cross the moment you pick up a
  DualSense, and turns into a key cap when you touch the keyboard. A DualSense's touchpad works
  like a laptop's: the pointer, tap and press to click, tap-and-drag, two-finger scroll and
  right-click, and pinch to zoom.
- **A keyboard and mouse work too.** Arrows move, Enter selects, Esc goes back, X and Y are
  themselves and `/` searches; on the library, Tab opens Settings, `` ` `` opens Stats and Ctrl
  opens a game's options. The mouse hovers and clicks anywhere; a right
  click on a game opens its menu and a right click anywhere else goes back, a click on the dimmed
  screen closes a menu, and the hint bar is itself clickable.

The result is a machine you genuinely never have to walk over to. Games are the reason you sit
down; everything else stops being a reason to stand up.

## Your library, found automatically

Loungepad scans **Steam**, **Epic**, **GOG** and the **Xbox app** from their local install data —
no accounts, no API keys, nothing phoned anywhere. It picks up real cover art, tracks playtime and
sessions locally, and rescans every time it starts, so a game installed yesterday is simply there.
Anything the scanners drag in that isn't a game (benchmarks, wallpaper tools, redistributables)
gets hidden with one button.

Turn on **Show games you own but haven't installed** under Settings → Library and your whole Steam
library comes in too, the way Playnite's Steam integration does it. The account is read off the
Steam client's own login, so there is nothing to sign into; anything not on disk sits greyed out
in the grid, and pressing A on one hands it to Steam to install — the tile turns playable the
moment the download finishes. Steam's default privacy settings are enough. For a profile that
keeps its game details private, or one you would rather not make public, use **Steam sign-in**
under the toggle: you sign in on Steam's own page, and the library is read with that sign-in, so
nothing has to be made public and Loungepad never sees your password. A free
[Steam Web API key](https://steamcommunity.com/dev/apikey) in the row below does the same job
without signing in.

**Epic, GOG and Xbox** work the way they do in Playnite: sign in to each store once, from
Settings → Library, and its library is listed here. The sign-in is the store's own web page in a
window of its own — the stick is the mouse, the keyboard toggle raises the on-screen keyboard —
and Loungepad keeps only the resulting token, encrypted for your Windows account. Pressing A on a
game you own but do not have opens the right store ready to install: the Epic Games Launcher, GOG
Galaxy (or the game's gog.com page when Galaxy is not installed), or the Microsoft Store. The
Xbox list is your profile's title history, which is what Xbox Live exposes; **Show the PC Game
Pass catalogue** adds every game included with PC Game Pass, from Microsoft's public catalogue,
with no sign-in at all.

**Trailers.** In the Loungepad theme, rest the highlight on a game for three seconds and its
trailer plays across the screen, with sound, behind the tiles; open the game's page and the same
film simply carries on behind the page, and a menu opened over it leaves it running. Shelf keeps
its library as it always was and plays trailers only on a game's page, after the page has been
open for three seconds. Steam's trailers come first, keyed by app id like the
rest of the art, so any game Steam sells has one and nothing is matched by title: the file streams
from Steam the first time and is kept under `%LOCALAPPDATA%\Loungepad\trailers` for the next, up
to 4 GB with the oldest going first (**Keep trailers on this PC**, under Settings → Library, turns
the keeping off). A game Steam has nothing for — a ROM, a store exclusive — gets IGDB's trailer,
which is a YouTube video and plays through YouTube's own player straight from YouTube; nothing is
kept for those, and the player's own dressing (the play button, captions, the title bar, the
suggestions at the end) is kept off the screen. Neither kind passes through the metadata service,
which only ever hands back the video's id. **Trailers** and **Trailer sound** sit under the theme's own options in Settings →
Appearance: Trailers can be limited to the game's page or turned off (Shelf offers only those
two), and the sound can be muted.

**The gallery.** Under the blurb on a game's page sits a row of the store page's films and
screenshots, the trailer first. Walking along it changes the picture behind the page: a film plays
after a short beat, a screenshot hangs where the hero would. A opens the highlighted one whole, in
a viewer with playback under the pad: A pauses and resumes a film (on a picture, it goes to the
next), X and Y skip ten seconds back and ahead, Left and Right step through the gallery, B returns
to the page. Steam's screenshots and every movie are used where a game has a Steam page; IGDB's
screenshots and videos otherwise. Installed games get theirs in the background; any other game's
fills in the first time its page is opened. A shade over the outer half of the last tile says
there is more that way. Nothing from the gallery is kept on disk except the trailer.

### The Xbox sign-in needs an app registration

Xbox Live only issues tokens to programs Microsoft knows about. Playnite works because its author
registered Playnite as an application with Microsoft — the "Let this app access your info?"
prompt you see there is the consent screen for that registration. The old trick of signing in as
one of Microsoft's own first-party clients is being withdrawn: the Xbox app's own id is now
refused outright (a 403 from the user-token service), and the one Loungepad falls back to can
stop working the same way at any time.

Registering one takes five minutes and costs nothing:

1. Sign in at https://portal.azure.com with any Microsoft account, open **Microsoft Entra ID →
   App registrations → New registration**.
2. Name it (say, "Loungepad"). Under **Supported account types** choose **Personal Microsoft
   accounts only**.
3. Under **Redirect URI** pick the platform **Public client/native (mobile & desktop)** and enter
   `https://login.live.com/oauth20_desktop.srf`. Register.
4. On the app's **Authentication** page set **Allow public client flows** to **Yes** and save. No
   client secret is needed, or wanted.
5. Copy the **Application (client) ID** from the Overview page.

Paste it into **Settings → Library → Xbox sign-in app id** and sign in again; the consent prompt
will now name your registration. If you build Loungepad yourself, put the same id into
`DefaultClientId` in `XboxAccountClient.cs` and everybody who runs your build gets the Xbox sign-in
with nothing to set up — which is exactly what Playnite ships.

The TV is treated as a first-class display: the launcher places itself there pixel-exactly, makes
it the Windows primary before a game starts so the game opens on the right screen, and puts your
old primary back when you quit.

## Emulators and ROMs

Loungepad runs emulated games the way Playnite and LaunchBox do: you tell it where the ROMs
are and what runs them, and the games take their place in the library like anything else —
cover art and box art, a description, a release date and a score, playtime, favourites,
collections, and a tile you press A on.

Most of it sets itself up. Every scan looks for emulators the way it looks for games: RetroArch
in a portable folder at a drive root or from Steam, anything installed under Program Files or
`%LOCALAPPDATA%`, a folder in Downloads or Desktop, an `Emulators` collection, the registry's
uninstall entries and the Start Menu's shortcuts. Games are found from **RetroArch's playlists**
first, which already say which system each ROM is and which core plays it, then from the game
folders PCSX2 and DuckStation keep in their settings, then from any folder named after a system
that holds a file of that system's kind: an EmuDeck-style `Emulation\roms\snes`, a `D:\ROMs\PS1`,
RetroArch's own `downloads\GBA`. Everything found gets a row under **Settings → Library →
Emulators & ROM folders**, marked *found automatically*, and anything you remove there stays
removed. **Find emulators and ROMs automatically** turns the whole thing off.

For anything the scan does not find, the same section adds it by hand, entirely from the gamepad:

1. **Add an emulator.** Point to its `.exe`. RetroArch, Dolphin, PCSX2, DuckStation, PPSSPP,
   Cemu, yuzu, Ryujinx, Citra/Azahar, melonDS, mGBA, Snes9x, bsnes, Mesen, Project64, ares,
   Flycast, Redream, MAME, xemu, Xenia, Mednafen, BlastEm, Kega Fusion, BizHawk and a few more
   are recognised from the file name and set up with the right command line. Anything else is
   started with the ROM's path and can be given arguments from its own row (`{rom}` is the file,
   `{romdir}`, `{romname}`, `{romfile}`, `{core}` and `{emudir}` also expand).
2. **Add a ROM folder.** Pick the folder, say which system it is for (the folder's name is the
   first guess — `SNES`, `psx`, `MegaDrive` all land on the right one) and which emulator runs
   it. One folder per system, which is how every ROM collection is organised anyway; the
   system decides which file extensions count as games, so save files, manuals and box scans
   beside the ROMs are left alone. A RetroArch folder also needs a core, and the first of that
   system's usual cores that is installed is chosen for you.

The folder is scanned on every start, like the stores are. Titles are read off the file names
with the No-Intro and GoodTools tags stripped — `Legend of Zelda, The - A Link to the Past (USA)
(Rev 1).sfc` becomes *The Legend of Zelda: A Link to the Past* — and that title is what the
metadata lookup runs on, so a game the file name does not describe (a MAME set called `sf2`)
can be renamed from its Manage menu and fetches again. Multi-disc games listed in an `.m3u`
are one game; `.bin` tracks named by a `.cue` are not listed twice.

Every system is a platform in the library's filter, under its own **Emulated** heading, so a
shelf of SNES games is one press away. An emulated game never merges with a store copy of the
same name: *Doom* on the SNES and DOOM on Steam are two games. Art and facts come from the same
shared service as every other non-Steam game, with the system passed along so the lookup asks
about the right *Doom*; Steam is never consulted for a ROM.

Playing one starts the emulator with the ROM, and the launcher treats the emulator as the game:
its window goes to the TV, the in-game menu closes it, and playtime is recorded against the ROM.
Per game, Manage offers **Rename**, **Run with** (a different emulator for this one game) and
its own launch arguments.

## Mods

Loungepad does not manage mods itself. It drives **[Vortex](https://www.nexusmods.com/site/mods/1)**,
Nexus Mods' free mod manager, which knows how to mod over 250 PC games — the Bethesda games,
Cyberpunk 2077, Baldur's Gate 3, The Witcher 3, Stardew Valley, Elden Ring, The Sims 4 and the
rest — and puts the parts of it you want from a sofa on the TV. Press **Y** on any installed game
and choose **Mods**:

- **Switch mods on and off.** Each row is one mod, with its version and author; A toggles it and
  Vortex deploys the change straight away.
- **Remove a mod.** X on the row. Vortex takes it out of the game and keeps the downloaded
  archive, so it can be put back from there.
- **Find mods.** *Find mods on Nexus Mods* opens the game's section of nexusmods.com in a window
  over the launcher; **Y** on a mod that came from Nexus opens that mod's own page. The window
  is driven the same way the store sign-ins are: the stick is the mouse, and its bar has
  **Back (B)**, **Forward (X)**, **Keyboard** (the same button as the keyboard toggle) and
  **Done (Y)**, each drawn with the button of the pad in hand. Choose **Mod manager download** on a mod and it goes straight to Vortex, which
  downloads and installs it in the background; it appears in the list when you come back.
- **Teach Vortex a game it does not know.** Vortex learns each game through an extension. For a
  game it has none for, the Mods screen lists the extensions in Vortex's catalogue that look
  like they are for it, exact match first: choosing one opens Vortex's own extension browser on
  it, on the TV, where Install is one click; or the extension's page on Nexus Mods, where **Mod
  manager download** installs it. After a restart of Vortex the game is known to it but not yet
  located, and **Set it up in Vortex** does the rest: Loungepad hands Vortex the game's folder
  (Vortex checks the game's files are in it) and Vortex opens on the game for its first-time
  questions, which are answered from the Mods screen like any other.
- **Answer Vortex's questions.** When Vortex stops to ask something — "this archive is not a
  layout I know, install it anyway?", "this file exists, replace it?" — the question appears at
  the top of the list with its own buttons, so it is answered from the sofa. A question that
  wants more than a button, such as a folder, still needs Vortex's window.
- **Open Vortex.** For load order, conflicts, mod options and anything else the list does not
  do, Vortex's own window is brought to the TV. Come back to the launcher the way you do after a
  game (hold the menu combo, Guide by default).

The first time, two one-off steps happen in Vortex's own window: Vortex has to be restarted
once so it loads the small extension Loungepad installs into it (the screen says so and offers
to do it), and each game has to be *managed* in Vortex once — a folder for the mods, how they
are deployed. **Set it up in Vortex** on the Mods screen starts that and puts Vortex on the TV.
Opening the Mods screen starts Vortex minimized if it is not already running.

**If Vortex is not installed**, the Mods screen says so and explains how to get it: the
installer is on the Files tab of [nexusmods.com/site/mods/1](https://www.nexusmods.com/site/mods/1),
it installs for your own Windows account with no administrator step, and a free Nexus Mods
account (signed into inside Vortex) is only needed to download mods. **Open the download page**
opens it in your browser on the desktop. The same row lives under **Settings → Library → Mods**,
with an optional **Vortex location** for a portable copy or one on another drive.

How it works: Vortex has no way in from outside, so Loungepad ships a tiny Vortex extension
(`vortex-bridge`) that it copies into `%APPDATA%\Vortex\plugins\loungepad-bridge`. The extension
listens on the local machine only (`127.0.0.1`, a fresh secret per run) and turns a handful of
requests into calls on Vortex's own API — list, enable, disable, remove, deploy, switch game.
Downloads go through Vortex's own command line (`Vortex.exe --install <link>`). Vortex deploys
mods into the game folder itself, so the game launches exactly as it did before; nothing about
the launch changes. Mods for emulated games, and mod managers other than Vortex, are not
supported.
