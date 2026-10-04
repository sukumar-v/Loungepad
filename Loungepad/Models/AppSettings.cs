using System.Text.Json;

namespace Loungepad.Models;

public class AppSettings
{
    // Appearance
    /// <summary>
    /// The one colour the UI is built around -- focus rings, active tabs, sliders. "#RRGGBB";
    /// the UI overrides its --accent token with it and derives every tint from there.
    /// </summary>
    public string AccentColor { get; set; } = "#F0A253";
    /// <summary>Folder name under %APPDATA%\Loungepad\themes, or "" for the built-in look.
    /// A theme that has been deleted falls back to the built-in look rather than failing. The
    /// Loungepad theme ships with the app, so a fresh install opens on it rather than on Shelf.</summary>
    public string Theme { get; set; } = "loungepad";
    /// <summary>Hide the button-hint bar along the bottom of every screen. Off by default: it is
    /// the only thing telling a new player what A and Y do, so it is opt-out, not opt-in.</summary>
    public bool HideLegend { get; set; }
    /// <summary>Screens, menus and the Power Wheel move into place rather than appearing. Off
    /// makes every change instant: the page does it with one CSS multiplier (--motion), so
    /// nothing here has to know which animation is which.</summary>
    public bool AnimationsEnabled { get; set; } = true;
    /// <summary>Multiplier on the speed of every animation, 0.5 .. 2.0. 1.0 is the design's own timing.</summary>
    public double AnimationSpeed { get; set; } = 1.0;
    /// <summary>
    /// The themes' own options, keyed by theme id and then by option id: the values behind the
    /// rows a theme declares in its theme.json (see the README's Themes section). Kept here rather
    /// than in the theme's folder so updating a bundled theme, which replaces the folder, keeps
    /// them, and per theme so switching away and back finds them as they were. Only the page reads
    /// them, as CSS, and it checks each value against the theme's definition first; the host keeps
    /// them to plain values (ThemeService.CleanSettingValues) and nothing more.
    /// </summary>
    public Dictionary<string, Dictionary<string, JsonElement>> ThemeSettings { get; set; } = new();

    // Metadata providers
    // Nothing here needs setting. Steam games are keyed by app id, non-Steam games are looked up
    // against Steam by title, and anything left over goes through the shared metadata service --
    // none of which asks the user for anything.
    //
    // The three below are an override for people who would rather use their own credentials than
    // someone else's server. When set they take priority over the service. Stored in plain text in
    // settings.json, which is what Playnite does too, but worth knowing before pasting a secret in.
    /// <summary>Twitch application client id, for IGDB. Free, non-commercial use only.</summary>
    public string IgdbClientId { get; set; } = "";
    /// <summary>Twitch application client secret, for IGDB.</summary>
    public string IgdbClientSecret { get; set; } = "";
    /// <summary>SteamGridDB API key. Art only, and the best source of it for non-Steam games.</summary>
    public string SteamGridDbKey { get; set; } = "";
    /// <summary>Overrides the shipped metadata service endpoint. Empty means use the built-in one;
    /// this exists for self-hosting and for testing, not as something anyone need ever set.</summary>
    public string MetadataEndpoint { get; set; } = "";
    /// <summary>Keep a copy of each trailer the page plays, so the second play comes off disk
    /// rather than off Steam. The cache lives under %LOCALAPPDATA% and is capped (see
    /// TrailerCache.CapBytes), oldest out first. Off streams every time and keeps nothing.</summary>
    public bool CacheTrailers { get; set; } = true;
    /// <summary>Whose age rating a game's page shows: "ESRB" or "PEGI". A game rated only by the
    /// other board shows that one's instead -- the mark is the board's own logo, so it cannot be
    /// read as the one asked for.</summary>
    public string AgeRatingBoard { get; set; } = "ESRB";

    // Steam account
    /// <summary>
    /// Bring in the whole Steam library, not just the part on disk. The account is read off the
    /// Steam client's own login file, so there is nothing to sign into; the list comes from the
    /// Web API through the shared service, or through the key below. Off by default because it is
    /// the one feature that sends something identifying -- the SteamID -- anywhere, and the README
    /// promises "nothing phoned anywhere" for the plain scan.
    /// </summary>
    public bool SteamShowOwned { get; set; }
    /// <summary>The user's own Steam Web API key, free from steamcommunity.com/dev/apikey. Only
    /// needed when the profile keeps its game details private, which the shared key cannot read;
    /// a key issued to the profile's owner can. Plain text in settings.json, like the rest.</summary>
    public string SteamApiKey { get; set; } = "";

    // Other stores
    // Epic, GOG and Xbox are not settings at all: each is a sign-in, kept encrypted under
    // %APPDATA%\Loungepad\accounts, and being signed in is what turns the store's library on.
    /// <summary>Every game included with PC Game Pass, from Microsoft's public catalogue, each
    /// installable from here. A catalogue rather than a library, hence its own switch.</summary>
    public bool GamePassCatalog { get; set; }
    /// <summary>Optional. The client id of an Azure app registration of the user's own, for the
    /// Xbox sign-in. Empty means the Xbox app's own client, which needs nothing; this is the way
    /// out if Microsoft ever stops accepting that. See the README.</summary>
    public string XboxClientId { get; set; } = "";

    // Mods
    /// <summary>Optional. Where Vortex is, for a portable copy or one installed somewhere its
    /// installer does not put it. Empty means look in the usual places: the per-user Programs
    /// folder, the uninstall entries, Program Files.</summary>
    public string VortexPath { get; set; } = "";

    // Activity and achievements
    /// <summary>Record every sitting with a game -- when, how long -- as the activity log behind the
    /// playtime numbers. Off keeps the totals and records nothing more.</summary>
    public bool ActivityTracking { get; set; } = true;
    /// <summary>Take a hardware reading every few seconds while a game runs: CPU, GPU and memory
    /// from Windows, frame rate and temperatures from RivaTuner, Afterburner or HWiNFO where one of
    /// them is running. See HardwareMonitor.</summary>
    public bool ActivityHardware { get; set; } = true;
    /// <summary>Seconds between readings, 2 .. 30. Five is a reading every few frames of a chart
    /// and a few kilobytes an hour.</summary>
    public int ActivitySampleSeconds { get; set; } = 5;
    /// <summary>Fetch each game's achievements from its store: Steam by app id through the metadata
    /// service or the key below, Xbox, Epic and GOG through their sign-ins, ROMs through
    /// RetroAchievements. Off fetches nothing and hides what was fetched.</summary>
    public bool AchievementsEnabled { get; set; } = true;
    /// <summary>A card for each achievement a session unlocked, shown once the game has closed.</summary>
    public bool AchievementNotifications { get; set; } = true;
    /// <summary>The unlocked share on library tiles and in the hero text, for games that have any.</summary>
    public bool AchievementsOnTiles { get; set; } = true;
    /// <summary>RetroAchievements username, for the achievements of emulated games. With the key
    /// below, free from retroachievements.org/settings. Plain text in settings.json, like the rest.</summary>
    public string RetroAchievementsUser { get; set; } = "";
    public string RetroAchievementsKey { get; set; } = "";

    // Emulation
    /// <summary>Look for installed emulators and for ROMs on every scan -- RetroArch's playlists,
    /// an Emulation\roms layout, folders named after a system -- and add what is found. On by
    /// default for the same reason the store scans are: nobody wants to go and set up what the
    /// machine already knows. Everything found can be removed, and stays removed.</summary>
    public bool DetectEmulators { get; set; } = true;

    // Display
    public string? TvDeviceName { get; set; }          // e.g. @"\\.\DISPLAY2"
    public bool SwitchPrimaryOnLaunch { get; set; } = true;
    public bool RepositionGameWindow { get; set; } = true;
    public bool KeepFocus { get; set; } = true;        // pull focus back when the desktop steals it
    /// <summary>On by default: a launcher you have to go and find on the desktop is not a
    /// launcher anyone uses from a sofa.</summary>
    public bool LaunchOnStartup { get; set; } = true;
    /// <summary>Check GitHub for a new release every few hours, download it in the background and
    /// install it the next time the app starts. Off, nothing is fetched until Settings or the tray
    /// asks. See UpdateService.</summary>
    public bool AutoUpdate { get; set; } = true;
    /// <summary>The newest first-run setup this install has been through, finished or skipped. 0 is
    /// a new install and the page opens its setup screens over the library; a later setup that
    /// adds a step raises the page's own number (ONBOARDING_VERSION in onboarding.js), and only
    /// then does it show again. A settings file from before the setup existed is read as 1 by
    /// SettingsStore.Load: that install was set up by hand, and nobody wants a welcome screen on
    /// the hundredth start.</summary>
    public int OnboardingVersion { get; set; }

    // Rest and sleep (see RestService)
    /// <summary>Minutes without a touch on the pad, the keyboard or the mouse before the launcher
    /// rests: the TV goes dark, the game is frozen, and one press on the pad brings both back.
    /// 0 never rests on its own; the Power Wheel still can. An hour is what both consoles ship
    /// with, long enough for a cutscene.</summary>
    public int RestAfterMinutes { get; set; } = 60;
    /// <summary>Whether the idle timer runs while a game is running. Off for anyone who leaves a
    /// game grinding by itself; on, like a console, for everyone else.</summary>
    public bool RestDuringGame { get; set; } = true;
    /// <summary>Freeze the game's processes while resting (the way PlayState does), so it draws
    /// nothing and computes nothing until the wake. Off leaves it running to a dark screen -- for
    /// an online game, or one whose anti-cheat objects to being frozen.</summary>
    public bool RestPausesGame { get; set; } = true;
    /// <summary>Once resting, how long before the PC itself is put to sleep: -1 never (screen off
    /// only, any pad wakes it), 0 straight away, otherwise minutes. Sleep is where the electricity
    /// goes; what can wake it from there is the hardware's business, and Settings says which. Off
    /// by default (the user's call, Oct 2026): most pads cannot wake a sleeping PC, and a default
    /// that sends somebody to the desk for the mouse is the wrong default for a couch.</summary>
    public int SleepAfterRestMinutes { get; set; } = -1;

    // Gamepad → mouse
    public bool GamepadMouseEnabled { get; set; } = true;
    public bool GamepadMouseDuringGame { get; set; }   // off by default so it never fights native pad support
    public double Deadzone { get; set; } = 0.18;       // 0.05 .. 0.40
    public double Sensitivity { get; set; } = 1.0;     // 0.2 .. 3.0 (multiplier on max cursor speed)
    public double AccelExponent { get; set; } = 1.8;   // 1.0 linear .. 3.0 strongly curved
    /// <summary>Held to move the cursor and scroll faster. "Off" disables the boost.</summary>
    public string BoostButton { get; set; } = "RT";
    public double BoostMultiplier { get; set; } = 2.5; // 1.5 .. 5.0
    /// <summary>Replace Windows' cursors with blank ones while the D-pad drives navigation.
    /// Global state, so off by default — see CursorService.</summary>
    public bool HideCursorSystemWide { get; set; }
    /// <summary>The touchpad on a DualSense or DualShock 4 as a trackpad: swipe to move the
    /// pointer, press the pad to click, press with two fingers for a right click.</summary>
    public bool TouchpadMouse { get; set; } = true;
    /// <summary>Scales the touchpad's gain. The gain itself follows the finger's speed -- see the
    /// touchpad section of GamepadService -- and 1.0 is a laptop-like feel.</summary>
    public double TouchpadSensitivity { get; set; } = 1.0;   // 0.25 .. 4.0
    /// <summary>A short, still touch is a click, and a two-finger one a right click. The pad's own
    /// press always clicks regardless.</summary>
    public bool TouchpadTapToClick { get; set; } = true;
    /// <summary>Tap, then touch and hold: the left button stays down while the finger moves, to drag a
    /// window or select text. Costs every tap a short wait, which is why it can be turned off.</summary>
    public bool TouchpadTapDrag { get; set; } = true;
    /// <summary>Two-finger scrolling moves the content with the fingers, as Windows' touchpads do by default.</summary>
    public bool TouchpadNaturalScroll { get; set; } = true;
    /// <summary>Scales two-finger scrolling. 1.0 scrolls about twelve notches over the pad's height.</summary>
    public double TouchpadScrollSpeed { get; set; } = 1.0;   // 0.25 .. 4.0

    // Button bindings (used outside the launcher UI; inside it A/B/Y/X/MENU follow the on-screen legend)
    public string LeftClickButton { get; set; } = "A";
    public string RightClickButton { get; set; } = "B";
    /// <summary>Gamepad combo that minimizes/restores the launcher, e.g. "LS + RS". "Off" disables
    /// it. Guide is the button a console player already reaches for, so that is the default --
    /// Windows and Steam both grab it, and the Settings row says so and how to free it.</summary>
    public string MinimizeCombo { get; set; } = "Guide";
    /// <summary>
    /// What the menu combo does. "TapHold": a tap opens the Power Wheel and a hold shows or hides
    /// Loungepad (the in-game menu while a game runs). "DoubleTap": a tap shows or hides Loungepad
    /// and a double tap opens the Power Wheel -- the original, kept for anyone whose Windows still
    /// takes a long press of the Xbox button. See ComboGesture.
    /// </summary>
    public string MenuComboMode { get; set; } = "TapHold";
    /// <summary>Gamepad button or combo that taps the screenshot key. "Off" disables it.
    /// Evaluated even inside a focused game, which is the only place it is any use.</summary>
    public string ScreenshotCombo { get; set; } = "Off";
    /// <summary>
    /// The pad's own screenshot button takes one while a game is focused: Create on a DualSense
    /// (Share on a DualShock 4), Capture on a Switch Pro. F12 in a Steam game, the Steam overlay's
    /// key, so the picture lands with the game's Steam screenshots; Win+PrintScreen in anything
    /// else, which Windows saves to Pictures\Screenshots. The Xbox Share button never reaches an
    /// application, so Windows and Steam keep answering that one themselves.
    /// </summary>
    public bool ShareButtonScreenshot { get; set; } = true;

    // On-screen keyboard
    /// <summary>How long a D-pad direction must be held on the on-screen keyboard before the
    /// highlight starts repeating.</summary>
    public int KeyRepeatDelayMs { get; set; } = 350;
    /// <summary>Gap between repeats once it is moving; smaller is faster.</summary>
    public int KeyRepeatIntervalMs { get; set; } = 90;
    /// <summary>Shows and hides the on-screen keyboard. View ("Back"; Create on a DualSense), the
    /// button a console's own keyboard sits nearest. On the library the page claims View for search
    /// (GamepadService.UiClaimedButtons), and search raises the keyboard anyway, so the one place View
    /// already had a job loses nothing. Not Start: that is the launcher's Menu button, and in Press
    /// mode the keyboard would take the press outright.</summary>
    public string KeyboardToggleButton { get; set; } = "Back";
    /// <summary>"Press" (a tap) or "Hold". A tap is the quicker of the two and is the default;
    /// Hold is for anyone whose toggle button also has a job inside the launcher.</summary>
    public string KeyboardToggleMode { get; set; } = "Press";
    /// <summary>How long the button is held in Hold mode. Not used in Press mode.</summary>
    public int KeyboardToggleHoldMs { get; set; } = 400;
    /// <summary>
    /// Whether the toggle button reaches the keyboard while a game holds the foreground.
    ///
    /// Off by default: inside a game every button belongs to the game, and a keyboard sliding up
    /// over one mid-fight is a surprise nobody asked for. Closing a keyboard that is already up is
    /// always allowed regardless, so this can never strand one on screen.
    /// </summary>
    public bool KeyboardInGame { get; set; }
    /// <summary>Builtin (the Loungepad keyboard) | TabTip | Osk. The Loungepad keyboard by default:
    /// it is driven by the pad out of the box and never takes the foreground, where TabTip answers a
    /// pad only on a layout picked by hand in its own settings, and Osk not at all.</summary>
    public string KeyboardApp { get; set; } = "Builtin";
    /// <summary>Multiplier on the Loungepad keyboard's key size, 0.6 .. 1.6. The base size is a
    /// fraction of the display height, so this only nudges it away from that.</summary>
    public double KeyboardScale { get; set; } = 1.0;
    /// <summary>The bar of word suggestions above the Loungepad keyboard's keys. On by default: it
    /// is the one addition that saves presses on every word, and it costs a strip of height.</summary>
    public bool KeyboardSuggestions { get; set; } = true;
    // The Loungepad keyboard's extra blocks. Off by default: letters and symbols are what most
    // typing needs, and every block widens the keyboard over whatever it is covering. Also
    // switched from the keyboard itself, behind the gear on its suggestion bar.
    /// <summary>F1 to F12 across the top, with Print Screen, Scroll Lock and Pause over the
    /// navigation block when that is on too.</summary>
    public bool KeyboardFunctionKeys { get; set; }
    /// <summary>Insert, Delete, Home, End, Page Up, Page Down and four arrows, right of the letters.</summary>
    public bool KeyboardNavKeys { get; set; }
    /// <summary>A number pad as on a full-size keyboard, sent as real number-pad keys.</summary>
    public bool KeyboardNumpad { get; set; }
    /// <summary>Ctrl, Win and Alt on the bottom row, each latching for the next key like Shift.</summary>
    public bool KeyboardModifiers { get; set; }
}
