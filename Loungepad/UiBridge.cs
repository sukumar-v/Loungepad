using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Loungepad.Models;
using Loungepad.Services;
using Microsoft.Web.WebView2.Core;

namespace Loungepad;

/// <summary>
/// JSON message bridge between the WebView2 UI and the native shell.
/// UI -> host: { cmd: "...", ... }   host -> UI: { type: "...", ... }
/// </summary>
public class UiBridge
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly MainWindow _window;
    private readonly CoreWebView2 _core;
    private readonly SettingsStore _settings;
    private readonly LibraryStore _library;
    private readonly DisplayService _displays;
    private readonly LibraryScanner _scanner;
    private readonly GameLaunchService _launcher;
    private readonly VirtualKeyboardService _keyboard;
    private readonly WindowService _windows;
    private readonly ThemeService _themes;
    private readonly UpdateService _updates;
    private readonly MetadataService _metadata = new();
    /// <summary>The community server's invite, the same one the README, the website and the issue
    /// chooser link to.</summary>
    private const string DiscordInvite = "https://discord.gg/a6gngxS9b4";
    /// <summary>A Steam sign-in, for a profile whose games are private. Kept apart from _accounts:
    /// Steam's owned games already come in through _steam and OwnedSteamGames, and the sign-in is
    /// only a better way for _steam to ask.</summary>
    private readonly SteamWebSession _steamWeb = new();
    private readonly SteamAccountService _steam;
    private readonly GamePassCatalogService _gamePass = new();
    /// <summary>The stores one signs in to, keyed as the page names them. Filled in the
    /// constructor because Xbox reads a setting.</summary>
    private readonly Dictionary<string, IStoreAccount> _accounts = new();
    /// <summary>The mod manager side: Vortex, through the bridge extension. Built here because it
    /// reads the Vortex path setting.</summary>
    private readonly ModService _mods;
    /// <summary>Trailers kept on disk after their first play. See TrailerCache.</summary>
    private readonly TrailerCache _trailers;
    /// <summary>The Mods screen's request in flight. Opening another game's list cancels the last
    /// one, so a slow Vortex start cannot answer for a screen that has since moved on.</summary>
    private CancellationTokenSource? _modsCts;
    private bool _signingIn;
    private System.Threading.Timer? _installPoll;
    private string? _pendingInstall;
    private DateTime _pollUntil;
    private readonly List<FileSystemWatcher> _manifestWatchers = new();
    private System.Threading.Timer? _manifestTimer;
    private bool _scanning;
    private bool _enriching;
    /// <summary>Games whose gallery is being fetched on demand, so a page reopened while one is in
    /// flight does not ask twice.</summary>
    private readonly HashSet<string> _mediaFetching = new();
    // Square pictures a theme asked for (fetchSquares): one at a time, in the order asked.
    private readonly Queue<string> _squareQueue = new();
    private readonly HashSet<string> _squareQueued = new();
    private bool _squareRunning;
    /// <summary>The play sessions and their readings, and the service that records them.</summary>
    private readonly ActivityStore _activityStore;
    private readonly ActivityService _activity;
    /// <summary>The achievement lists and the service that fetches them.</summary>
    private readonly AchievementStore _achievementStore;
    private readonly AchievementService _achievements;
    /// <summary>The game whose achievements the page has open, so a fetch that lands while it is
    /// up is pushed to it and one for a page since closed is not.</summary>
    private string? _achievementsOpenId;

    /// <summary>Community themes and extensions (docs/ADDONS.md).</summary>
    private readonly AddonService _addons;
    private readonly ExtensionRuntime _extensions;

    public UiBridge(MainWindow window, CoreWebView2 core, SettingsStore settings, LibraryStore library,
        DisplayService displays, LibraryScanner scanner, GameLaunchService launcher, VirtualKeyboardService keyboard, WindowService windows, ThemeService themes,
        UpdateService updates, AddonService addons, ExtensionRuntime extensions)
    {
        _updates = updates;
        _addons = addons;
        _extensions = extensions;
        // The list the page draws, pushed on its own after any change (never a state push, which
        // would rebuild the library); the pass's checkpoints push the games, since they changed.
        _addons.Changed += () => _window.Dispatcher.BeginInvoke(PushAddons);
        _addons.Progress += p => _window.Dispatcher.BeginInvoke(() =>
            Push(new { type = "addonProgress", key = p.Key, state = p.State, percent = p.Percent, error = p.Error }));
        _extensions.Changed += () => _window.Dispatcher.BeginInvoke(PushAddons);
        _extensions.Checkpoint += () => _window.Dispatcher.BeginInvoke(PushState);
        _window = window;
        _core = core;
        _settings = settings;
        _library = library;
        _displays = displays;
        _scanner = scanner;
        _launcher = launcher;
        _keyboard = keyboard;
        _windows = windows;
        _themes = themes;
        _steam = new SteamAccountService(_steamWeb);
        foreach (var account in new IStoreAccount[]
                 {
                     new EpicAccountClient(),
                     new GogAccountClient(),
                     new XboxAccountClient(() => _settings.Settings.XboxClientId),
                 })
            _accounts[account.Store] = account;
        _mods = new ModService(() => _settings.Settings.VortexPath);
        _trailers = new TrailerCache(_library, () => _settings.Settings.CacheTrailers);
        // A lighter message than a state push: the page swaps one field on one game and the
        // library is not rebuilt under somebody who is browsing it.
        _trailers.Cached += g => _window.Dispatcher.BeginInvoke(() =>
            Push(new { type = "trailerCached", id = g.Id, file = g.TrailerFile }));
        _trailers.Prune();
        StartInstallWatcher();

        _activityStore = new ActivityStore();
        _activityStore.Load();
        _activityStore.Prune();
        _activity = new ActivityService(_activityStore, _launcher, () => _settings.Settings);
        // A session landed: one message with the row, not a state push -- the exit already
        // pushed the playtime, and the library is not rebuilt for a row nothing on screen shows.
        _activity.SessionRecorded += s => _window.Dispatcher.BeginInvoke(() => Push(new { type = "activityRecorded", session = s }));
        // The live readout on the in-game menu, and nowhere else: pushed only while an overlay is
        // up over a running game, so the page is never woken every few seconds for nothing.
        _activity.Sampled += live =>
        {
            if (_window.OverlayActive && _launcher.GameRunning) _window.Dispatcher.BeginInvoke(() => PushTelemetry(live));
        };

        _achievementStore = new AchievementStore();
        _achievementStore.Load();
        _achievements = new AchievementService(_achievementStore, _library, () => _settings.Settings,
            new IAchievementProvider[]
            {
                new SteamAchievementProvider(() => _settings.Settings, _steamWeb),
                new XboxAchievementProvider((XboxAccountClient)_accounts["xbox"]),
                new EpicAchievementProvider((EpicAccountClient)_accounts["epic"]),
                new GogAchievementProvider((GogAccountClient)_accounts["gog"]),
                new RetroAchievementProvider(() => _settings.Settings, _achievementStore.Dir),
            }, _launcher);
        _achievements.Fetched += set => _window.Dispatcher.BeginInvoke(() => OnAchievementsFetched(set));
        _achievements.Unlocked += (game, items) => _window.Dispatcher.BeginInvoke(() =>
            Push(new { type = "achievementsUnlocked", id = game.Id, title = game.Title, items = items.Select(AchievementDto) }));
        _achievements.IconsCached += id => _window.Dispatcher.BeginInvoke(() =>
        {
            if (_achievementsOpenId == id && _achievements.Get(id) is { } set) PushAchievements(set, null);
        });
    }

    /// <summary>The window is closing: stop the sampling loop and any pass in flight.</summary>
    public void Shutdown()
    {
        _extensions.CancelPass();
        _activity.Dispose();
        _achievements.Dispose();
    }

    /// <summary>Where a bridge command may come from. Every command runs with the host's
    /// authority, so only the launcher's own page is answered -- never a frame inside it (the
    /// YouTube player) and never a top frame that somehow ended up somewhere else.</summary>
    private const string PageOrigin = "https://loungepad.ui/";

    public void OnWebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        if (!e.Source.StartsWith(PageOrigin, StringComparison.OrdinalIgnoreCase))
        {
            Log.Info($"Ignored a bridge message from {e.Source}");
            return;
        }
        JsonNode? msg;
        try { msg = JsonNode.Parse(e.WebMessageAsJson); }
        catch { return; }
        var cmd = msg?["cmd"]?.GetValue<string>();
        if (cmd is null) return;

        try { Handle(cmd, msg!); }
        catch (Exception ex)
        {
            Log.Info($"Bridge command '{cmd}' failed: {ex}");
            Push(new { type = "toast", message = $"Something went wrong: {ex.Message}" });
        }
    }

    private void Handle(string cmd, JsonNode msg)
    {
        switch (cmd)
        {
            case "ready":
                PushState();
                _window.PushPadState();
                // The notes say which version this is; the toast is for a build that has none.
                if (!PushWhatsNew() && App.UpdatedFrom is not null)
                    PushToast($"Updated to Loungepad {UpdateService.Format(UpdateService.Current)}");
                App.UpdatedFrom = null;
                // After the notes: the page asks one question at a time, in the order it hears them.
                PushSecureInputUpdate();
                // Scan on every start, not just an empty library: games get installed and
                // uninstalled between sessions, and nobody on a couch wants to go looking for
                // Settings to find out. The merge keeps playtime, favourites, manual entries and
                // every per-game override, so re-running it costs nothing.
                StartScan();
                DetectApps();
                RefreshWake();
                // The extensions start once the page is up, in hidden WebViews beside it, and the
                // repository's list is read in the background (cached for hours; see AddonService).
                _extensions.Attach(_core.Environment, _window.WindowHandle);
                _ = Task.Run(() => _addons.RefreshCatalogueAsync(force: false));
                break;

            case "launch":
            {
                var id = msg["id"]?.GetValue<string>();
                var game = id is null ? null : _library.Find(id);
                if (game is null) break;
                if (!game.Installed || (game.ExePath is not null && !File.Exists(game.ExePath) && game.Platform is "GOG" or "Manual"))
                {
                    Push(new { type = "toast", message = $"{game.Title} is not installed" });
                    break;
                }
                // Everything that can be wrong with an emulated launch is known before anything
                // starts -- no emulator, its exe gone, the ROM gone, a core never chosen -- and
                // each is something the person can fix, so it is said here rather than logged.
                if (game.Emulated && _launcher.ResolveEmulated(game, out var problem) is null)
                {
                    Push(new { type = "toast", message = problem ?? "This game cannot be started" });
                    break;
                }
                if (_launcher.GameRunning)
                {
                    // The UI has already asked whether to swap; `replace` is that answer.
                    if (msg["replace"]?.GetValue<bool>() == true) _ = SwapRunningGame(game);
                    else Push(new { type = "toast", message = "A game is already running" });
                    break;
                }
                _launcher.Launch(game);
                break;
            }

            case "rescan":
                StartScan(force: true);
                break;

            case "storeSignIn":
                if (msg["store"]?.GetValue<string>() == SteamWebSession.Store) _ = SignInSteamAsync();
                else if (msg["store"]?.GetValue<string>() is { } signIn && _accounts.TryGetValue(signIn, out var accountIn))
                    _ = SignInAsync(accountIn);
                break;

            case "storeSignOut":
                if (msg["store"]?.GetValue<string>() == SteamWebSession.Store)
                {
                    // Back to the public route (or the user's key): the next scan asks that way,
                    // and a private profile's uninstalled games then leave the library.
                    _steamWeb.SignOut();
                    Push(new { type = "toast", message = "Signed out of Steam" });
                    PushState();
                    StartScan(force: true);
                }
                else if (msg["store"]?.GetValue<string>() is { } signOut && _accounts.TryGetValue(signOut, out var accountOut))
                {
                    accountOut.SignOut();
                    Push(new { type = "toast", message = $"Signed out of {accountOut.DisplayName}" });
                    PushState();
                    // The next scan is what takes the store's games out of the library.
                    StartScan(force: true);
                }
                break;

            // The store's own install flow: Steam and Galaxy put up a dialog with the size and the
            // drive, Epic's launcher and the Microsoft Store open the game's page with an Install
            // button. The manifest watcher or the poll below turns the tile playable afterwards.
            case "install":
            {
                var id = msg["id"]?.GetValue<string>();
                var game = id is null ? null : _library.Find(id);
                if (game is null || game.Installed) break;
                // The URI was written by whichever service listed the game, never by the page --
                // the page only names the game. The scheme check keeps a hand-edited library.json
                // from turning this into "run anything".
                if (game.InstallUri is not { } uri
                    || !InstallSchemes.Any(s => uri.StartsWith(s, StringComparison.OrdinalIgnoreCase)))
                {
                    Push(new { type = "toast", message = $"{game.Title} has to be installed from {game.Platform}" });
                    break;
                }
                try
                {
                    // The launcher is topmost on the TV, so the store's window would open behind
                    // it and look like nothing happened. Step aside the way a launch does; the
                    // minimize combo brings the launcher back.
                    _window.Park();
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(uri) { UseShellExecute = true });
                    Log.Info($"Install requested for {game.Title} ({uri})");
                    BeginInstallPolling(game.Id);
                }
                catch (Exception ex)
                {
                    _window.Unpark();
                    Push(new { type = "toast", message = $"Could not ask {game.Platform} to install: {ex.Message}" });
                }
                break;
            }

            // Credentials can be added long after a game was first looked up and written off, and
            // a title that matched nothing today may match tomorrow. Clearing the timestamps is
            // what makes the next pass reconsider everything rather than honouring the fortnight.
            case "refreshMetadata":
                foreach (var g in _library.Games) g.MetadataFetched = null;
                _ = EnrichMetadata();
                break;

            // Explorer on the themes folder: "put a folder here" is the whole install story,
            // so the launcher may as well open the place you put it.
            case "openThemesFolder":
                try
                {
                    Directory.CreateDirectory(Paths.ThemesDir);
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(Paths.ThemesDir) { UseShellExecute = true });
                }
                catch (Exception ex) { Push(new { type = "toast", message = $"Could not open the themes folder: {ex.Message}" }); }
                break;

            // ---- add-ons (docs/ADDONS.md) ----
            case "addonsRefresh":
                _ = Task.Run(() => _addons.RefreshCatalogueAsync(force: true));
                break;
            // Settings → Add-ons came on screen: the counts, if the last read is over five minutes old.
            case "addonsOpened":
                _ = Task.Run(() => _addons.RefreshCountsAsync(force: false));
                break;
            case "addonLike":
            {
                var key = msg["key"]?.GetValue<string>() ?? "";
                var on = msg["on"]?.GetValue<bool>() ?? true;
                _ = Task.Run(async () =>
                {
                    try { await _addons.SetLikedAsync(key, on); }
                    catch (Exception ex) { _window.Dispatcher.Invoke(() => PushToast(ex.Message)); }
                });
                break;
            }
            case "addonInstall":
                _ = InstallAddonAsync(msg["key"]?.GetValue<string>() ?? "");
                break;
            case "addonInstallFile":
                InstallAddonFromFile(msg["how"]?.GetValue<string>() == "folder");
                break;
            case "addonReload":
                _ = ReloadAddonAsync(msg["key"]?.GetValue<string>() ?? "");
                break;
            case "addonRemove":
                RemoveAddon(msg["key"]?.GetValue<string>() ?? "");
                break;
            case "addonEnable":
                _ = EnableAddonAsync(msg["key"]?.GetValue<string>() ?? "", msg["on"]?.GetValue<bool>() ?? true);
                break;
            case "addonRestart":
                _ = _extensions.RestartAsync(msg["key"]?.GetValue<string>() ?? "");
                break;
            case "addonDevTools":
                try { _extensions.OpenDevTools(msg["key"]?.GetValue<string>() ?? ""); }
                catch (Exception ex) { PushToast(ex.Message); }
                break;
            case "addonFetchNow":
            {
                var key = msg["key"]?.GetValue<string>() ?? "";
                if (_extensions.PassRunning) { PushToast("An extension pass is already running"); break; }
                PushToast("Fetching…");
                _ = Task.Run(() => _extensions.RunPassAsync(key, force: true));
                break;
            }
            case "addonOpenHomepage":
            {
                // Only a homepage the list carries: the page never names a URL of its own.
                var key = msg["key"]?.GetValue<string>() ?? "";
                var url = _addons.Describe().FirstOrDefault(d => d.Key == key)?.Homepage;
                if (url is null) { PushToast("This add-on has no homepage"); break; }
                try
                {
                    _window.Park();
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true });
                }
                catch (Exception ex) { _window.Unpark(); PushToast($"Could not open the browser: {ex.Message}"); }
                break;
            }
            case "addonsOpenFolder":
                try
                {
                    var dir = msg["kind"]?.GetValue<string>() == AddonKind.Extension ? Paths.ExtensionsDir : Paths.ThemesDir;
                    Directory.CreateDirectory(dir);
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(dir) { UseShellExecute = true });
                }
                catch (Exception ex) { PushToast($"Could not open the folder: {ex.Message}"); }
                break;

            case "addManual":
                AddManualGame();
                break;

            case "removeGame":
            {
                var id = msg["id"]?.GetValue<string>();
                if (id is not null && _library.Find(id) is { Manual: true })
                {
                    _library.Remove(id);
                    PushState();
                }
                break;
            }

            // "slot" says which picture: the portrait cover, or the landscape tile the library
            // grid is made of. Absent means the cover, which is what the only caller used to mean.
            case "pickCover":
            {
                var id = msg["id"]?.GetValue<string>();
                if (id is not null) PickArt(id, msg["slot"]?.GetValue<string>() ?? "cover");
                break;
            }

            case "resetArt":
            {
                var id = msg["id"]?.GetValue<string>();
                if (id is not null) ResetArt(id, msg["slot"]?.GetValue<string>() ?? "cover");
                break;
            }

            case "saveSettings":
            {
                var incoming = msg["settings"].Deserialize<AppSettings>(JsonOpts);
                if (incoming is null) break;
                var displayChanged = incoming.TvDeviceName != _settings.Settings.TvDeviceName;
                var startupChanged = incoming.LaunchOnStartup != StartupService.IsRegistered();
                // Flipping the Steam library on should show the games now, not on the next start,
                // and flipping it off should take them away just as promptly. A new key is a new
                // route to the same answer, so it re-asks too.
                var cur = _settings.Settings;
                var storesChanged = incoming.SteamShowOwned != cur.SteamShowOwned
                                    || Secret(incoming.SteamApiKey, cur.SteamApiKey) != cur.SteamApiKey
                                    || incoming.GamePassCatalog != cur.GamePassCatalog
                                    || incoming.DetectEmulators != cur.DetectEmulators;
                // Turned on: look now, rather than leave the row saying nothing for six hours.
                var autoUpdateOn = incoming.AutoUpdate && !cur.AutoUpdate;
                // A key added, or achievements switched on: ask now rather than on the next scan.
                var achievementsChanged = (incoming.AchievementsEnabled && !cur.AchievementsEnabled)
                                          || (incoming.RetroAchievementsUser ?? "").Trim() != cur.RetroAchievementsUser
                                          || Secret(incoming.RetroAchievementsKey, cur.RetroAchievementsKey) != cur.RetroAchievementsKey;
                // Which extensions' options changed, so each running one is told (docs/ADDONS.md).
                var extChanged = ChangedExtensionSettings(incoming.ExtensionSettings, cur.ExtensionSettings);
                CopySettings(incoming);
                _settings.Save();
                foreach (var id in extChanged) _extensions.NotifySettings(AddonService.KeyOf(AddonKind.Extension, id));
                if (storesChanged) StartScan(force: true);
                if (autoUpdateOn) _ = _updates.CheckAsync(userAsked: false);
                if (achievementsChanged && !storesChanged) _ = _achievements.RefreshAllAsync(force: false);
                if (startupChanged)
                {
                    try { StartupService.SetRegistered(incoming.LaunchOnStartup); }
                    catch (Exception ex) { Push(new { type = "toast", message = $"Startup registration failed: {ex.Message}" }); }
                }
                if (displayChanged) _window.PositionOnTargetDisplay();
                // The size slider is only worth having if it moves the keyboard you are looking at.
                _window.RefreshBuiltinKeyboard();
                PushState();
                break;
            }

            case "setSecureInput":
                _ = SetSecureInputAsync(msg["feature"]?.GetValue<string>() ?? "", msg["enabled"]?.GetValue<bool>() == true, msg["installConfirmed"]?.GetValue<bool>() == true);
                break;
            case "uninstallSecureInput":
                if (msg["confirmed"]?.GetValue<bool>() == true) _ = UninstallSecureInputAsync();
                break;
            case "updateSecureInput":
                _ = UpdateSecureInputAsync();
                break;

            // Every setting back to the value a fresh install would have. Deliberately goes
            // through the same path as a save rather than writing the file directly, so the
            // startup entry and the on-screen keyboard both follow it -- resetting "Launch at
            // login" to false has to actually unregister it.
            //
            // The one thing kept is the TV display. Which screen is the television is a fact
            // about the room rather than a preference, and clearing it moves the launcher off
            // the screen the user is looking at -- a restore they would have to undo blind. The
            // first-run setup's mark is kept too: a restore is not a new install, and the welcome
            // screen coming back after it would read as the launcher having been wiped.
            case "resetSettings":
            {
                var defaults = new AppSettings
                {
                    TvDeviceName = _settings.Settings.TvDeviceName,
                    OnboardingVersion = _settings.Settings.OnboardingVersion,
                };
                var startupChanged = defaults.LaunchOnStartup != StartupService.IsRegistered();
                CopySettings(defaults);
                _settings.Save();
                if (startupChanged)
                {
                    try { StartupService.SetRegistered(defaults.LaunchOnStartup); }
                    catch (Exception ex) { Push(new { type = "toast", message = $"Startup registration failed: {ex.Message}" }); }
                }
                _window.RefreshBuiltinKeyboard();
                PushState();
                Push(new { type = "toast", message = "Settings restored to defaults" });
                break;
            }

            // ---- Power Wheel / in-game menu ----

            case "listWindows":
            {
                var open = _windows.ListWindows();
                Push(new { type = "windows", windows = open, displays = _displays.GetDisplays() });
                StartThumbnails(open);
                break;
            }

            case "windowAction":
            {
                var act = msg["action"]?.GetValue<string>();
                var handle = msg["handle"]?.GetValue<long>() ?? (long)_window.OverlayTarget;
                var h = new IntPtr(handle);
                switch (act)
                {
                    case "close": _windows.Close(h); break;
                    case "focus":
                        // Switching to a window brings it to the TV with it — the point of picking
                        // one from the couch is to look at it. A window already there is left
                        // alone rather than being re-centred for no reason.
                        _window.SwitchTo(h);
                        break;
                }
                Push(new { type = "toast", message = ActionToast(act) });
                break;
            }

            case "shortcut":
                if (msg["id"]?.GetValue<string>() is { } sid)
                {
                    // Sleep sits in the shortcuts list but is rest mode's: the game is frozen and
                    // the pad parked first, so the machine comes back the way it went.
                    if (sid == "sleepPc") { _ = _window.Rest.Sleep("the Power Wheel"); break; }
                    _ = _window.OpenShortcut(sid);
                }
                break;

            // ---- rest mode (see RestService) ----

            case "rest":
                _window.Rest.Rest("asked for");
                break;

            // Settings → General → Rest and sleep → Sleep the PC now (the page confirms first).
            case "sleepPc":
                _ = _window.Rest.Sleep("asked for");
                break;

            // The in-game menu's pause: frozen where it stands, or thawed. Going back to the game
            // by any other route (Resume, the resume card) thaws it too.
            case "pauseGame":
            {
                if (!_launcher.GameRunning) break;
                if (_launcher.Paused) _launcher.Resume();
                else if (_launcher.Pause() == 0) PushToast("Nothing of the game could be paused");
                PushGameState();
                break;
            }

            case "restRefresh":
                RefreshWake(force: msg["force"]?.GetValue<bool>() ?? false);
                break;

            // Settings → General → Rest and sleep: allow a controller device to wake the PC.
            // Elevated, so one UAC prompt; the answer is re-read afterwards either way.
            case "wakeAllow":
            {
                var name = msg["name"]?.GetValue<string>();
                if (string.IsNullOrWhiteSpace(name)) break;
                _ = WakeInfo.EnableWakeAsync(name).ContinueWith(t => _window.Dispatcher.BeginInvoke(() =>
                {
                    PushToast(t.IsCompletedSuccessfully && t.Result
                        ? $"{name} may wake the PC now"
                        : "Windows did not allow it, or the prompt was declined");
                    RefreshWake(force: true);
                }));
                break;
            }

            case "signInOnWake":
            {
                var on = msg["on"]?.GetValue<bool>() ?? false;
                _ = WakeInfo.SetSignInOnWakeAsync(on).ContinueWith(t => _window.Dispatcher.BeginInvoke(() =>
                {
                    PushToast(t.IsCompletedSuccessfully && t.Result
                        ? (on ? "Windows asks for a sign-in after a wake again" : "No sign-in after a wake: the pad lands straight back where it was")
                        : "Windows did not accept the change, or the prompt was declined");
                    RefreshWake(force: true);
                }));
                break;
            }

            // ---- actions: the wheel and Settings → Actions ----

            case "actionFire":
            {
                var app = _window.Actions.Find(msg["appId"]?.GetValue<string>() ?? "");
                var action = app?.Actions.FirstOrDefault(a => a.Id == (msg["actionId"]?.GetValue<string>() ?? ""));
                // The page has taken its menus down already; the host's overlay state has to follow
                // whether or not there is anything to press.
                if (app is null || action is null)
                {
                    _window.CloseOverlay(true);
                    Push(new { type = "toast", message = "That action is gone" });
                    break;
                }
                _window.FireAction(app, action);
                break;
            }

            case "actionCapture":
                _window.BeginActionCapture();
                break;

            case "actionCaptureCancel":
                _window.CancelActionCapture();
                break;

            case "actionUpdate":
            {
                var appId = msg["appId"]?.GetValue<string>() ?? "";
                var incoming = msg["action"].Deserialize<ActionDef>(JsonOpts);
                if (incoming is null) break;
                var stored = _window.Actions.Update(appId, incoming);
                if (stored is null) Push(new { type = "toast", message = "That action could not be saved" });
                PushActions();
                break;
            }

            case "actionRemove":
                _window.Actions.Remove(msg["appId"]?.GetValue<string>() ?? "", msg["actionId"]?.GetValue<string>() ?? "");
                PushActions();
                break;

            case "actionAppReset":
                _window.Actions.Reset(msg["appId"]?.GetValue<string>() ?? "");
                PushActions();
                break;

            case "actionAppRemove":
                _window.Actions.RemoveApp(msg["appId"]?.GetValue<string>() ?? "");
                PushActions();
                break;

            case "actionAppRename":
                _window.Actions.RenameApp(msg["appId"]?.GetValue<string>() ?? "", msg["name"]?.GetValue<string>() ?? "");
                PushActions();
                break;

            // An app from the list of open windows: the exe name is enough, and the icon follows
            // once detection has looked at the running process.
            case "actionAppAdd":
            {
                var exe = msg["exe"]?.GetValue<string>() ?? "";
                if (exe.Length == 0) break;
                var (id, existed) = _window.Actions.AddApp(exe, msg["name"]?.GetValue<string>());
                PushActions();
                Push(new { type = "actionAppAdded", id, existed });
                DetectApps();
                break;
            }

            case "actionAppBrowse":
                BrowseForApp();
                break;

            case "actionsRefresh":
                DetectApps();
                break;

            // What "Add an app" offers: the windows open right now, by program. Store apps come
            // through as their own exe, not the frame host's.
            case "actionListApps":
            {
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var open = new List<object>();
                var ours = ActionService.ExeName(Environment.ProcessPath ?? "loungepad");
                foreach (var w in _windows.ListWindows())
                {
                    var exe = ActionService.ExeOf(new IntPtr(w.Handle));
                    if (exe.Length == 0 || exe == ours || !seen.Add(exe)) continue;
                    var known = _window.Actions.ForExe(exe);
                    open.Add(new { title = w.Title, exe, listed = known is not null && _window.Actions.Installed(known), appId = known?.Id });
                }
                Push(new { type = "actionApps", open });
                break;
            }

            case "closeOverlay":
                _window.CloseOverlay(msg["refocus"]?.GetValue<bool>() ?? true);
                break;

            case "centerMouse":
                if (_settings.Settings.TvDeviceName is { } tvc) _windows.CenterCursorOn(tvc);
                _window.CloseOverlay(false);
                break;

            case "mouseInGame":
            {
                // Toggled from the Power Wheel rather than only from Settings: the moment you need
                // it is mid-game, when walking to Settings means leaving the game to do it.
                var st = _settings.Settings;
                st.GamepadMouseDuringGame = msg["on"]?.GetValue<bool>() ?? !st.GamepadMouseDuringGame;
                _settings.Save();
                PushState();
                Push(new { type = "toast", message = st.GamepadMouseDuringGame
                    ? "Gamepad mouse forced on while a game runs"
                    : "Gamepad mouse off while a game runs" });
                break;
            }

            // Settings → Controller → Windows and Steam: one of the things that also act on the
            // Xbox button, switched on or off. See WindowsGuide and SteamGuide for what each writes.
            case "xboxButtonSet":
            {
                var what = msg["what"]?.GetValue<string>();
                var on = msg["on"]?.GetValue<bool>() ?? false;
                if (what == "steam") { _ = SetSteamGuideAsync(on); break; }
                bool ok = what switch
                {
                    "gameBar" => WindowsGuide.SetGameBar(on),
                    "xboxMode" => WindowsGuide.SetXboxMode(on),
                    _ => false,
                };
                PushState();
                Push(new { type = "toast", message = !ok ? "Windows did not accept the change"
                    : what == "gameBar" ? (on ? "Game Bar opens on the Xbox button again" : "Game Bar no longer takes the Xbox button")
                    : on ? "Xbox mode is back on" : "Xbox mode is off. If a hold still opens Task View, sign out of Windows and back in" });
                break;
            }

            // "Turn all off": everything that still takes the button, in one press.
            case "xboxButtonAllOff":
            {
                var w = WindowsGuide.Read();
                if (w.GameBar) WindowsGuide.SetGameBar(false);
                if (w.XboxMode == true) WindowsGuide.SetXboxMode(false);
                PushState();
                if (SteamGuide.Read() == true) _ = SetSteamGuideAsync(false);
                else Push(new { type = "toast", message = "The Xbox button is Loungepad's now. If Windows still reacts to it, sign out and back in" });
                break;
            }

            case "setRadialActive":
                _window.SetRadialActive(msg["active"]?.GetValue<bool>() ?? false);
                break;

            case "inputMode":
                if (msg["mode"]?.GetValue<string>() is { } im) _window.SetInputMode(im);
                break;

            case "goHome":
                _window.GoHome();
                break;

            // H, or the library hint bar's Minimize Loungepad: the keyboard's and the mouse's way
            // off the screen.
            case "hideLauncher":
                _window.HideLauncher();
                break;

            case "closeGame":
            {
                // Land on the library first. Asked from the in-game menu, the launcher is a
                // transparent overlay over the game — leaving it up while the game tears down
                // shows the user nothing at all, which reads as "it froze".
                _window.GoHome();

                var title = _launcher.RunningGameId is { } rid ? _library.Find(rid)?.Title : null;
                var (closed, found) = CloseRunningGame();
                // Nothing found means the session has been ended (GameLaunchService.Abandon): the
                // game never came up, so it reads as closed rather than as an error about windows.
                Push(new { type = "toast", message = closed > 0 ? "Closing game…"
                    : found ? "The game has no window to close yet"
                    : $"{title ?? "The game"} was not running" });
                break;
            }

            case "resumeGame":
                // Back to the game means a running game: a frozen one is thawed on the way.
                _launcher.Resume();
                _window.CloseOverlay(true);
                break;

            case "toggleKeyboard":
                _keyboard.Toggle();
                break;

            // Buttons the page wants delivered to it rather than spent on something else first --
            // View on the library, where it opens search even when it is also the keyboard toggle.
            case "claimButtons":
                _window.SetUiClaimedButtons(msg["buttons"]?.AsArray()
                    .Select(n => n?.GetValue<string>()).Where(x => !string.IsNullOrEmpty(x)).Select(x => x!)
                    .ToArray() ?? Array.Empty<string>());
                break;

            // Keyboard focus into the page, for a text field about to be typed into. Without it the
            // launcher has no focused element at all and keystrokes -- the on-screen keyboard's
            // included -- go nowhere.
            case "focusPage":
                _window.FocusPage();
                break;

            case "showKeyboard":
                _keyboard.Show();
                break;

            case "hideKeyboard":
                _keyboard.Hide();
                break;

            case "toggleHidden":
            {
                var id = msg["id"]?.GetValue<string>();
                var game = id is null ? null : _library.Find(id);
                if (game is null) break;
                game.Hidden = !game.Hidden;
                _library.Save();
                PushState();
                Push(new { type = "toast", message = game.Hidden ? $"{game.Title} hidden" : $"{game.Title} restored to the library" });
                break;
            }

            // A game in several stores is hidden as a whole: the page sends every copy, so hiding
            // the Steam one does not just bring the Xbox one out from behind it.
            case "setHidden":
            {
                var hide = msg["hidden"]?.GetValue<bool>() ?? true;
                var ids = msg["ids"]?.AsArray().Select(n => n?.GetValue<string>()).Where(x => x is not null).ToList() ?? new();
                var changed = ids.Select(i => _library.Find(i!)).Where(x => x is not null).ToList();
                if (changed.Count == 0) break;
                foreach (var x in changed) x!.Hidden = hide;
                _library.Save();
                PushState();
                var name = changed[0]!.Title;
                Push(new { type = "toast", message = hide ? $"{name} hidden" : $"{name} restored to the library" });
                break;
            }

            // Which store a game in several of them launches from. One flag across the copies,
            // so the others are cleared in the same save.
            case "preferEdition":
            {
                var id = msg["id"]?.GetValue<string>();
                var chosen = id is null ? null : _library.Find(id);
                if (chosen is null) break;
                foreach (var sib in msg["siblings"]?.AsArray() ?? new JsonArray())
                    if (sib?.GetValue<string>() is { } sibId && _library.Find(sibId) is { } other) other.PreferredEdition = false;
                chosen.PreferredEdition = true;
                _library.Save();
                PushState();
                break;
            }

            case "toggleFavorite":
            {
                var id = msg["id"]?.GetValue<string>();
                var game = id is null ? null : _library.Find(id);
                if (game is null) break;
                game.Favorite = !game.Favorite;
                _library.Save();
                PushState();
                break;
            }

            // The page is about to play a trailer. Nothing is answered here: it streams from the
            // URL it already has, and the copy that lands on disk is announced as trailerCached.
            case "cacheTrailer":
            {
                var id = msg["id"]?.GetValue<string>();
                var game = id is null ? null : _library.Find(id);
                if (game is not null) _trailers.Request(game);
                break;
            }

            // A game's page opened with nothing in its gallery. Fetched for that one game, in the
            // background, and answered as one `media` message -- not a state push, which would
            // rebuild the library under a page that is up.
            case "fetchMedia":
            {
                var id = msg["id"]?.GetValue<string>();
                var game = id is null ? null : _library.Find(id);
                if (game is null) break;
                if (game.Media.Count > 0) { Push(new { type = "media", id = game.Id, media = game.Media }); break; }
                lock (_mediaFetching) { if (!_mediaFetching.Add(game.Id)) break; }
                _ = Task.Run(async () =>
                {
                    try
                    {
                        var found = await _metadata.FetchMediaAsync(game, _settings.Settings);
                        if (!found) return;
                        _library.Save();
                        _ = _window.Dispatcher.BeginInvoke(() => Push(new { type = "media", id = game.Id, media = game.Media }));
                    }
                    catch (Exception ex) { Log.Info($"Metadata: gallery for {game.Title} failed: {ex.Message}"); }
                    finally { lock (_mediaFetching) _mediaFetching.Remove(game.Id); }
                });
                break;
            }

            // A theme that draws games as icons asks for squares for the games it draws (themeview.js).
            // Queued, one at a time, a little apart; a game with one on disk, or looked for and found
            // to have none this month, is not asked about.
            case "fetchSquares":
            {
                if (msg["ids"] is not JsonArray ids) break;
                lock (_squareQueue)
                {
                    foreach (var node in ids)
                    {
                        var game = node?.GetValue<string>() is { } gid ? _library.Find(gid) : null;
                        if (game is null || _squareQueued.Contains(game.Id)) continue;
                        if (game.SquareFile is { } sq && File.Exists(Path.Combine(Paths.CoversDir, sq))) continue;
                        if (game.SquareCheckedAt is { } at && DateTime.UtcNow - at < TimeSpan.FromDays(30)) continue;
                        _squareQueue.Enqueue(game.Id);
                        _squareQueued.Add(game.Id);
                    }
                    if (_squareRunning || _squareQueue.Count == 0) break;
                    _squareRunning = true;
                }
                _ = Task.Run(FetchSquaresAsync);
                break;
            }

            case "createCollection":
            {
                var name = msg["name"]?.GetValue<string>()?.Trim();
                if (string.IsNullOrEmpty(name)) break;
                var colId = _library.CreateCollection(name);
                if (msg["gameId"]?.GetValue<string>() is { } gid)
                    _library.ToggleInCollection(colId, gid);
                PushState();
                Push(new { type = "toast", message = $"Created collection “{name}”" });
                break;
            }

            case "toggleInCollection":
            {
                var colId = msg["collectionId"]?.GetValue<string>();
                var gid = msg["id"]?.GetValue<string>();
                if (colId is null || gid is null) break;
                _library.ToggleInCollection(colId, gid);
                PushState();
                break;
            }

            case "deleteCollection":
            {
                var colId = msg["id"]?.GetValue<string>();
                if (colId is null) break;
                _library.DeleteCollection(colId);
                PushState();
                break;
            }

            case "setArgs":
            {
                var id = msg["id"]?.GetValue<string>();
                var game = id is null ? null : _library.Find(id);
                if (game is null) break;
                game.Args = msg["args"]?.GetValue<string>()?.Trim() is { Length: > 0 } a ? a : null;
                _library.Save();
                PushState();
                Push(new { type = "toast", message = game.Args is null ? "Launch arguments cleared" : $"Launch arguments set: {game.Args}" });
                break;
            }

            case "pickExe":
            {
                var id = msg["id"]?.GetValue<string>();
                if (id is not null) PickExe(id);
                break;
            }

            case "launchViaStore":
            {
                var id = msg["id"]?.GetValue<string>();
                var game = id is null ? null : _library.Find(id);
                if (game is null) break;
                game.PreferDirectLaunch = false;
                _library.Save();
                PushState();
                Push(new { type = "toast", message = $"{game.Title} will launch through {game.Platform} again" });
                break;
            }

            case "updateCheck":
                _window.CheckForUpdates();
                break;

            case "updateInstall":
                _window.InstallUpdate();
                break;

            case "exitApp":
                _window.ExitApp();
                break;

            // ---- activity: the play sessions ----

            // A game's sessions, newest last, for its Stats sheet, with every dated unlock of its
            // list so the sheet can put each one on the day and the session it happened in. The
            // rows carry the averages; a session's readings are asked for on their own below.
            case "activityOpen":
            {
                var id = msg["id"]?.GetValue<string>();
                if (id is null) break;
                PushGameActivity(id);
                break;
            }

            case "activitySession":
            {
                var id = msg["id"]?.GetValue<string>();
                if (id is null) break;
                var session = _activityStore.Find(id);
                // What the sitting unlocked, for the markers on its charts and the strip above them.
                var unlocks = session is null ? [] : UnlocksFor(session.GameId)
                    .Where(u => u.At >= session.Start - SessionUnlockSlack && u.At <= session.End + SessionUnlockSlack)
                    .Select(u => u.Dto).ToList();
                _ = Task.Run(() =>
                {
                    var samples = _activityStore.LoadSamples(id);
                    _ = _window.Dispatcher.BeginInvoke(() => Push(new { type = "activitySession", id, session, samples, unlocks }));
                });
                break;
            }

            // Everything, for the Stats screen. Probing the tools opens their shared memory,
            // which is quick but is still done off the UI thread.
            case "activityAll":
                _ = Task.Run(() =>
                {
                    var sessions = _activityStore.All();
                    HardwareMonitor.Sources? sources = null;
                    try { sources = _activity.Sources(); } catch (Exception ex) { Log.Info($"Activity: probe failed ({ex.Message})"); }
                    _ = _window.Dispatcher.BeginInvoke(() => Push(new { type = "activityAll", sessions, sources }));
                });
                break;

            case "activityDelete":
            {
                var id = msg["id"]?.GetValue<string>();
                var session = id is null ? null : _activityStore.Find(id);
                if (session is null) break;
                _activityStore.Remove(session.Id);
                if (session is { Origin: "manual", Counted: true } && _library.Find(session.GameId) is { } owner)
                {
                    AddToTotals(owner, session, -1);
                    _library.Save();
                    PushState();
                }
                PushGameActivity(session.GameId);
                Push(new { type = "toast", message = "Session removed" });
                break;
            }

            // A sitting the launcher did not see -- another PC, or Loungepad not running -- logged
            // by hand on the game's Stats sheet. Counted (the default) puts it in the game's totals
            // the way a recorded one is; editing or removing it later moves them back.
            case "activityLog":
            {
                var id = msg["id"]?.GetValue<string>();
                var game = id is null ? null : _library.Find(id);
                if (game is null || !ReadSessionTimes(msg, out var start, out var seconds)) break;
                var session = new PlaySession
                {
                    Id = ActivityStore.NewId(), GameId = game.Id, Start = start, End = start.AddSeconds(seconds), Seconds = seconds,
                    Origin = "manual", Counted = msg["counted"]?.GetValue<bool>() ?? true,
                };
                _activityStore.Add(session, null);
                if (session.Counted)
                {
                    AddToTotals(game, session, +1);
                    _library.Save();
                    PushState();
                }
                PushGameActivity(game.Id);
                Push(new { type = "toast", message = "Session logged" });
                break;
            }

            // Only a hand-logged session can be edited: a recorded one's times are what its readings
            // were taken against.
            case "activityEdit":
            {
                var old = msg["id"]?.GetValue<string>() is { } editId ? _activityStore.Find(editId) : null;
                if (old is not { Origin: "manual" } || !ReadSessionTimes(msg, out var start, out var seconds)) break;
                var edited = new PlaySession
                {
                    Id = old.Id, GameId = old.GameId, Start = start, End = start.AddSeconds(seconds), Seconds = seconds,
                    Origin = "manual", Counted = msg["counted"]?.GetValue<bool>() ?? old.Counted,
                };
                _activityStore.Update(edited);
                if ((old.Counted || edited.Counted) && _library.Find(old.GameId) is { } owner)
                {
                    if (old.Counted) AddToTotals(owner, old, -1);
                    if (edited.Counted) AddToTotals(owner, edited, +1);
                    _library.Save();
                    PushState();
                }
                PushGameActivity(old.GameId);
                Push(new { type = "toast", message = "Session saved" });
                break;
            }

            // Every session of one game, or of every game. The playtime totals on the games are
            // left as they are: they are the launcher's own count and were never derived from this.
            case "activityClear":
            {
                var id = msg["id"]?.GetValue<string>();
                var n = _activityStore.Clear(id);
                Push(new { type = "toast", message = n == 0 ? "Nothing to clear" : $"Cleared {n} session{(n == 1 ? "" : "s")}" });
                if (id is not null) PushGameActivity(id);
                else Push(new { type = "activityAll", sessions = _activityStore.All(), sources = (HardwareMonitor.Sources?)null });
                break;
            }

            // ---- achievements ----

            // A game's list. What is on record goes at once, with why nothing can be fetched when
            // that is the case; a stale or missing list is fetched behind it and pushed when it
            // lands (see OnAchievementsFetched), then its icons are cached for next time.
            case "achievementsOpen":
            {
                var id = msg["id"]?.GetValue<string>();
                var game = id is null ? null : _library.Find(id);
                if (game is null) break;
                _achievementsOpenId = game.Id;
                var set = _achievements.Get(game.Id);
                var blocked = _achievements.Enabled ? _achievements.Blocked(game) : "Achievements are turned off under Settings → Stats";
                // Only a game with a provider can be fetching: a list brought from Playnite for a
                // game no store here serves would otherwise say FETCHING… for good.
                var fetching = _achievements.Enabled && _achievements.ProviderFor(game) is not null && blocked is null && !AchievementService.IsFresh(set);
                PushAchievements(set, new { blocked, fetching, provider = _achievements.ProviderFor(game)?.Source, id = game.Id });
                if (fetching) _ = _achievements.RefreshAsync(game, force: false);
                if (set is not null) _ = _achievements.CacheIconsAsync(set);
                break;
            }

            case "achievementsClose":
                _achievementsOpenId = null;
                break;

            // A few of a game's achievements for a theme's trophy card (themeview.js): from what
            // is on record only. Nothing is fetched; the background pass keeps the lists fresh.
            case "achievementsPeek":
            {
                var id = msg["id"]?.GetValue<string>();
                if (id is null) break;
                var set = _achievements.Get(id);
                Push(new { type = "achievementsPeek", id, set = set is null ? null : AchievementPeekDto(set) });
                break;
            }

            // Fetch again now: one game, or the whole library in the background pass.
            case "achievementsRefresh":
            {
                var id = msg["id"]?.GetValue<string>();
                if (!_achievements.Enabled) { Push(new { type = "toast", message = "Achievements are turned off under Settings → Stats" }); break; }
                if (id is not null)
                {
                    var game = _library.Find(id);
                    if (game is null) break;
                    if (_achievements.Blocked(game) is { } why) { Push(new { type = "toast", message = why }); break; }
                    _ = _achievements.RefreshAsync(game, force: true);
                }
                else
                {
                    Push(new { type = "toast", message = "Refreshing achievements in the background" });
                    _ = _achievements.RefreshAllAsync(force: true);
                }
                break;
            }

            // Every list's summary and the unlocks by day across the library, for the Stats screen.
            case "achievementsAll":
                Push(AchievementsAllPayload());
                break;

            // An achievement's state set by hand on the game's Achievements sheet: what the store
            // says is kept aside, and every later fetch carries the edit over (AchievementStore.Put).
            case "achievementEdit":
            {
                var id = msg["id"]?.GetValue<string>();
                var achId = msg["achievementId"]?.GetValue<string>();
                if (id is null || achId is null) break;
                var unlocked = msg["unlocked"]?.GetValue<bool>() ?? true;
                var at = LocalTime(msg["at"]) ?? DateTime.Now;
                if (unlocked && at > DateTime.Now.AddMinutes(1)) { Push(new { type = "toast", message = "That date is still to come" }); break; }
                if (!_achievementStore.Edit(id, achId, unlocked, unlocked ? at : null)) break;
                if (_achievementStore.Get(id) is { } set) OnAchievementsFetched(set);
                Push(new { type = "toast", message = "Achievement saved" });
                break;
            }

            case "achievementUnedit":
            {
                var id = msg["id"]?.GetValue<string>();
                var achId = msg["achievementId"]?.GetValue<string>();
                if (id is null || achId is null || !_achievementStore.Unedit(id, achId)) break;
                if (_achievementStore.Get(id) is { } set) OnAchievementsFetched(set);
                Push(new { type = "toast", message = "Back to what the store says" });
                break;
            }

            // ---- importing from Playnite (see PlayniteImport) ----

            // Where Playnite is and what the import would bring; read off the UI thread, since it
            // copies and opens a database, then planned against the stores here.
            case "playniteScan":
                ScanPlaynite(msg["dir"]?.GetValue<string>());
                break;

            case "playnitePick":
            {
                var dlg = new Microsoft.Win32.OpenFolderDialog { Title = "Choose Playnite's folder -- the one with library inside it" };
                if (!ShowDialog(dlg)) break;
                var dir = PlayniteImport.NormaliseDataDir(dlg.FolderName);
                if (dir is null) { Push(new { type = "toast", message = "There is no Playnite library in that folder" }); break; }
                ScanPlaynite(dir);
                break;
            }

            // The plan is made again at this moment rather than reused, and Apply checks every change
            // once more as it goes: only what Loungepad still has nothing for is filled.
            case "playniteImport":
            {
                if (_playniteData is null) break;
                var parts = (msg["parts"] as JsonArray)?.Select(n => n?.GetValue<string>()).OfType<string>().ToHashSet() ?? new HashSet<string>();
                var outcome = PlayniteImport.Apply(MakePlaynitePlan(_playniteData), parts, _library, _activityStore, _achievementStore, _settings.Settings);
                if (outcome.Settings > 0) _settings.Save();
                foreach (var gid in outcome.AchievementGames)
                    if (_achievementStore.Get(gid) is { } set) OnAchievementsFetched(set);
                Log.Info($"Playnite import from {_playniteData.Dir}: {outcome.Total} change(s) -- playtime {outcome.Playtime}, marks {outcome.Flags}, collections {outcome.Collections}, games {outcome.Games}, sessions {outcome.Sessions}, lists {outcome.Achievements}, settings {outcome.Settings}");
                PushState();
                PushPlaynite("done", outcome);
                break;
            }

            case "playniteUndo":
            {
                var outcome = PlayniteImport.Undo(_library, _activityStore, _achievementStore, _settings.Settings);
                if (outcome.Settings > 0) _settings.Save();
                foreach (var gid in outcome.AchievementGames)
                    Push(new { type = "achievementsSummary", id = gid, summary = new AchievementSummary() });
                Log.Info($"Playnite import undone: {outcome.Total} change(s) taken back");
                PushState();
                PushPlaynite("undone", outcome);
                break;
            }

            case "log":
                Log.Info($"UI: {msg["msg"]?.GetValue<string>()}");
                break;

            // ---- The first-run setup ----
            // Its screens ask about the PC here. Nothing in them is a setting of its own except
            // OnboardingVersion, which goes through saveSettings like everything else; every change
            // they make is one the Settings rows make, through the same commands.

            case "onboardingProbe":
                _ = ProbeFirstRunAsync();
                break;

            // Asked again every few seconds while the sign-in step is up, so a PIN set up in
            // Windows' own Settings shows as done the moment Loungepad is back.
            case "onboardingPin":
                _ = PushPinAsync();
                break;

            case "onboardingVortex":
            {
                var asked = msg["mode"]?.GetValue<string>();
                _ = OnboardVortexAsync(asked is "connect" or "restart" ? asked : "peek");
                break;
            }

            case "onboardingSignInOptions":
                _ = OpenSignInOptionsAsync();
                break;

            // ---- Mods ----
            // Every answer is one "mods" push carrying the whole screen for one game: which state
            // it is in (Vortex missing, needs a restart, game not set up, ready…), the sentence
            // that explains it, and the list. The page never has to assemble it from pieces.

            case "modsOpen":
                if (ModsGame(msg) is { } gOpen) _ = ModsRunAsync(gOpen, ct => _mods.OpenAsync(gOpen, ct));
                break;

            case "modsToggle":
            {
                var modId = msg["modId"]?.GetValue<string>();
                var enabled = msg["enabled"]?.GetValue<bool>() ?? true;
                if (ModsGame(msg) is { } gTog && modId is not null)
                    _ = ModsRunAsync(gTog, ct => _mods.SetEnabledAsync(gTog, modId, enabled, ct));
                break;
            }

            case "modsRemove":
            {
                var modId = msg["modId"]?.GetValue<string>();
                if (ModsGame(msg) is { } gRem && modId is not null)
                    _ = ModsRunAsync(gRem, ct => _mods.RemoveAsync(gRem, modId, ct));
                break;
            }

            // One of the buttons on a question Vortex is showing -- "install this anyway?" is the
            // usual one -- pressed from the sofa instead of walking to the desk.
            case "modsAnswer":
            {
                var dialogId = msg["dialogId"]?.GetValue<string>();
                var action = msg["action"]?.GetValue<string>();
                if (ModsGame(msg) is { } gAns && dialogId is not null && action is not null)
                    _ = ModsRunAsync(gAns, ct => _mods.AnswerAsync(gAns, dialogId, action, ct));
                break;
            }

            // Vortex's own window, on the TV: for the one-time setup of a game, and for anything
            // Vortex is asking about that a list across the room cannot answer.
            case "modsManage":
                if (ModsGame(msg) is { } gMan) _ = ModsManageAsync(gMan);
                break;

            case "modsShowVortex":
                _ = ShowVortexAsync();
                break;

            case "modsRestartVortex":
                if (ModsGame(msg) is { } gRes) _ = ModsRestartAsync(gRes);
                break;

            // Settings → General → Join the Loungepad Discord. The invite is the host's, so the page
            // can ask for exactly this and nothing else; parked first, or the browser would open
            // behind the launcher on the TV.
            case "openDiscord":
                try
                {
                    _window.Park();
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(DiscordInvite) { UseShellExecute = true });
                    Log.Info("Opened the Discord invite");
                }
                catch (Exception ex)
                {
                    _window.Unpark();
                    Push(new { type = "toast", message = $"Could not open the browser: {ex.Message}" });
                }
                break;

            // The page where the installer is, in the default browser. Not downloaded and run
            // from here: an installer is the one thing worth a mouse in hand.
            case "modsGetVortex":
                try
                {
                    _window.Park();
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(VortexBackend.DownloadUrl) { UseShellExecute = true });
                    Log.Info("Opened the Vortex download page");
                }
                catch (Exception ex)
                {
                    _window.Unpark();
                    Push(new { type = "toast", message = $"Could not open the browser: {ex.Message}" });
                }
                break;

            // Where on nexusmods.com to open: a mod's own page when the page names one, an
            // extension's page for a game Vortex cannot see yet, else the game's section.
            case "modsBrowse":
            {
                if (ModsGame(msg) is not { } gBr) break;
                var vg = _mods.Cached(gBr);
                string? url = null;
                if (LongOf(msg["extensionModId"]) is > 0 and var ext) url = VortexBackend.ExtensionUrl(ext);
                else if (msg["site"]?.GetValue<bool>() == true) url = "https://www.nexusmods.com/site/mods/";
                else if (LongOf(msg["nexusModId"]) is > 0 and var nexusId)
                    url = ModService.ModUrl(vg, nexusId, msg["nexusDomain"]?.GetValue<string>());
                _ = BrowseModsAsync(gBr, url ?? ModService.NexusUrl(vg));
                break;
            }

            // Vortex's extension browser, on the extension that would teach it this game, and
            // Vortex on the TV so its Install button can be pressed.
            case "modsExtension":
                if (ModsGame(msg) is { } gExt && LongOf(msg["modId"]) is > 0 and var extId)
                    _ = ModsExtensionAsync(gExt, extId);
                break;

            // ---- Emulators and ROM folders ----
            // The host owns these lists outright: every change comes through one of the commands
            // below and is saved into library.json, and the page never sends them back. They are
            // deliberately NOT part of saveSettings, so a settings push from an older page cannot
            // wipe them and "Restore default settings" leaves them alone.

            case "emuAdd":
                AddEmulator();
                break;

            case "emuPickExe":
                if (msg["id"]?.GetValue<string>() is { } emuExeId) PickEmulatorExe(emuExeId);
                break;

            case "emuUpdate":
            {
                var emu = _library.FindEmulator(msg["id"]?.GetValue<string>());
                if (emu is null) break;
                if (msg["name"]?.GetValue<string>()?.Trim() is { Length: > 0 } name) emu.Name = name;
                // Arguments may legitimately be emptied: that is "just the ROM", which Resolve
                // supplies. The property holds the template; an empty one is the default.
                if (msg["args"] is { } argsNode) emu.Args = argsNode.GetValue<string>()?.Trim() ?? "";
                _library.Save();
                PushState();
                break;
            }

            case "emuRemove":
            {
                var emu = _library.FindEmulator(msg["id"]?.GetValue<string>());
                if (emu is null) break;
                _library.RemoveEmulator(emu.Id);
                PushState();
                Push(new { type = "toast", message = $"Removed {emu.Name}. Folders that used it need a new emulator" });
                break;
            }

            case "romFolderPick":
                PickRomFolder();
                break;

            case "romFolderAdd":
                AddRomFolder(msg["path"]?.GetValue<string>(), msg["platformId"]?.GetValue<string>(),
                    msg["emulatorId"]?.GetValue<string>());
                break;

            case "romFolderUpdate":
            {
                var folder = _library.FindRomFolder(msg["id"]?.GetValue<string>());
                if (folder is null) break;
                var rescan = false;
                if (EmulatedPlatforms.Find(msg["platformId"]?.GetValue<string>()) is { } platform
                    && platform.Id != folder.PlatformId)
                {
                    folder.PlatformId = platform.Id;
                    folder.Core = null;   // a core is for one system; the new one picks its own
                    rescan = true;
                }
                if (msg["emulatorId"] is { } emuNode)
                {
                    var emuId = emuNode.GetValue<string>();
                    folder.EmulatorId = _library.FindEmulator(emuId)?.Id;
                    // A new emulator may want a core, and the old core was for the old one.
                    folder.Core = null;
                    if (_library.FindEmulator(folder.EmulatorId) is { } newEmu
                        && EmulatedPlatforms.Find(folder.PlatformId) is { } p)
                        folder.Core = EmulatorLaunch.SuggestCore(newEmu, p);
                }
                if (msg["args"] is { } fArgs) folder.Args = fArgs.GetValue<string>()?.Trim() is { Length: > 0 } a ? a : null;
                if (msg["extensions"] is { } extNode)
                {
                    var list = (extNode.GetValue<string>() ?? "")
                        .Split(new[] { ',', ' ', ';' }, StringSplitOptions.RemoveEmptyEntries)
                        .Select(e => e.Trim().TrimStart('.').ToLowerInvariant())
                        .Where(e => e.Length > 0 && e.All(c => char.IsAsciiLetterOrDigit(c)))
                        .Distinct().ToList();
                    folder.Extensions = list.Count > 0 ? list : null;
                    rescan = true;
                }
                _library.Save();
                PushState();
                if (rescan) StartScan();
                break;
            }

            case "romFolderPickCore":
                if (msg["id"]?.GetValue<string>() is { } coreFolderId) PickCore(coreFolderId);
                break;

            case "romFolderRemove":
            {
                var folder = _library.FindRomFolder(msg["id"]?.GetValue<string>());
                if (folder is null) break;
                _library.RemoveRomFolder(folder.Id);
                PushState();
                Push(new { type = "toast", message = $"Removed {folder.Path}. Its games leave the library on this scan" });
                StartScan();
                break;
            }

            // A ROM's title is a guess from its file name, and the guess is what the metadata
            // lookup runs on -- so a rename is also the way to make a wrongly-matched (or
            // unmatched) game fetch again, which is why the stamp is cleared.
            case "setTitle":
            {
                var game = _library.Find(msg["id"]?.GetValue<string>() ?? "");
                var title = msg["title"]?.GetValue<string>()?.Trim();
                if (game is null || string.IsNullOrEmpty(title) || title.Length > 200) break;
                game.Title = title;
                game.TitleEdited = true;
                game.MetadataFetched = null;
                _library.Save();
                PushState();
                Push(new { type = "toast", message = $"Renamed to {title}. Fetching its details again" });
                _ = EnrichMetadata();
                break;
            }

            // Which emulator runs this one game. An empty id puts it back on its folder's.
            case "setEmulator":
            {
                var game = _library.Find(msg["id"]?.GetValue<string>() ?? "");
                if (game is null || !game.Emulated) break;
                var emu = _library.FindEmulator(msg["emulatorId"]?.GetValue<string>());
                game.EmulatorId = emu?.Id;
                _library.Save();
                PushState();
                var now = _library.EmulatorFor(game);
                Push(new { type = "toast", message = now is null
                    ? $"{game.Title} has no emulator to run with"
                    : $"{game.Title} now runs with {now.Name}" });
                break;
            }
        }
    }

    // ---- Mods ----

    /// <summary>A whole number off the page, or null for anything else -- including JSON null,
    /// which GetValue throws on.</summary>
    private static long? LongOf(JsonNode? node)
    {
        try { return node?.GetValue<long>(); }
        catch { return null; }
    }

    private Game? ModsGame(JsonNode msg)
    {
        var game = _library.Find(msg["id"]?.GetValue<string>() ?? "");
        if (game is null) return null;
        if (!ModService.Eligible(game))
        {
            Push(new { type = "toast", message = game.Installed ? "Mods are for games on this PC's disk" : $"{game.Title} is not installed" });
            return null;
        }
        return game;
    }

    /// <summary>Run one Mods request off the UI thread and push its screen. A newer request for
    /// any game cancels this one; the page ignores an answer for a game it is no longer showing.</summary>
    private async Task ModsRunAsync(Game game, Func<CancellationToken, Task<ModsView>> work)
    {
        _modsCts?.Cancel();
        var cts = _modsCts = new CancellationTokenSource();
        try
        {
            var view = await Task.Run(() => work(cts.Token), cts.Token);
            if (!cts.IsCancellationRequested) PushMods(view);
        }
        catch (OperationCanceledException) { /* superseded */ }
        catch (Exception ex)
        {
            Log.Info($"Mods: {ex}");
            if (!cts.IsCancellationRequested)
                PushMods(new ModsView { GameId = game.Id, State = "error", Message = ex.Message, Vortex = _mods.Status() });
        }
    }

    private void PushMods(ModsView v) => Push(new
    {
        type = "mods",
        gameId = v.GameId, state = v.State, message = v.Message,
        vortex = v.Vortex, game = v.Game, mods = v.Mods, prompts = v.Prompts, notices = v.Notices,
        extensions = v.Extensions, downloadUrl = v.DownloadUrl,
    });

    /// <summary>
    /// Set the game up in Vortex and switch to it. Vortex's first-time questions come back as
    /// prompt rows on the Mods screen, so its window is only brought to the TV when it stopped
    /// on something the screen cannot carry.
    /// </summary>
    private async Task ModsManageAsync(Game game)
    {
        _modsCts?.Cancel();
        var cts = _modsCts = new CancellationTokenSource();
        try
        {
            var (view, needsWindow) = await Task.Run(() => _mods.ManageAsync(game, cts.Token), cts.Token);
            if (cts.IsCancellationRequested) return;
            PushMods(view);
            if (needsWindow) await ShowVortexAsync();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Log.Info($"Mods manage: {ex}");
            if (!cts.IsCancellationRequested)
                PushMods(new ModsView { GameId = game.Id, State = "error", Message = ex.Message, Vortex = _mods.Status() });
        }
    }

    /// <summary>
    /// Vortex's window to the TV and the foreground, with the launcher parked behind it the way it
    /// is for a store's install dialog. A Vortex sitting in the tray has no window to find, so it
    /// is started again -- its second instance only shows the first -- and given a moment.
    /// </summary>
    private async Task ShowVortexAsync()
    {
        if (_mods.Vortex.ExePath is null && _mods.Vortex.Locate() is null)
        {
            Push(new { type = "toast", message = "Vortex is not installed" });
            return;
        }
        var hwnd = VortexBackend.MainWindow();
        if (hwnd == IntPtr.Zero)
        {
            _mods.Vortex.Start("");
            var until = DateTime.UtcNow + TimeSpan.FromSeconds(20);
            while (hwnd == IntPtr.Zero && DateTime.UtcNow < until)
            {
                await Task.Delay(500);
                hwnd = VortexBackend.MainWindow();
            }
        }
        if (hwnd == IntPtr.Zero)
        {
            Push(new { type = "toast", message = "Vortex's window did not appear" });
            return;
        }
        _window.Park();
        if (_settings.Settings.TvDeviceName is { } tv && _windows.DisplayOf(hwnd) != tv) _windows.MoveToDisplay(hwnd, tv);
        _windows.Focus(hwnd);
        Log.Info("Brought Vortex to the TV");
    }

    private async Task ModsRestartAsync(Game game)
    {
        Push(new { type = "toast", message = "Restarting Vortex…" });
        var ok = await Task.Run(() => _mods.Vortex.RestartAsync(CancellationToken.None));
        if (!ok) Push(new { type = "toast", message = "Vortex did not close. Close it yourself, then try again" });
        await ModsRunAsync(game, ct => _mods.OpenAsync(game, ct));
    }

    /// <summary>
    /// nexusmods.com for the game, in a window of its own over the launcher, driven the way the
    /// store sign-ins are: the stick is the mouse, the keyboard toggle types. A "Mod manager
    /// download" click on the site is an nxm:// link, which the window hands to Vortex instead of
    /// letting Windows put up a protocol prompt; Vortex downloads and installs it, and the list is
    /// re-read when the window closes.
    /// </summary>
    private async Task BrowseModsAsync(Game game, string url)
    {
        if (_signingIn) return;
        if (_mods.Vortex.ExePath is null && _mods.Vortex.Locate() is null)
        {
            Push(new { type = "toast", message = "Install Vortex first: it is what downloads and installs the mods" });
            return;
        }
        _signingIn = true;
        _window.BeginModalDialog();
        var sent = 0;
        try
        {
            var s = _settings.Settings;
            await StoreLoginWindow.RunAsync(_window, $"Nexus Mods — {game.Title}", AccountStore.ProfileDir("nexus"), url,
                _ => Task.FromResult(false), visible: true, timeout: TimeSpan.FromHours(4),
                browse: new BrowseOptions
                {
                    Hint = "Mod manager download on a mod's Files tab sends it straight to Vortex. Done when you have what you want.",
                    CloseLabel = "Done",
                    OnExternalUri = uri =>
                    {
                        if (_mods.Vortex.Install(uri)) { sent++; Push(new { type = "toast", message = "Sent to Vortex. It downloads and installs in the background" }); }
                        else Push(new { type = "toast", message = "That link is not one Vortex can take" });
                    },
                    ShowKeyboard = () => _keyboard.Toggle(),
                    KeyboardButton = s.KeyboardToggleButton,
                    KeyboardHold = string.Equals(s.KeyboardToggleMode, "Hold", StringComparison.OrdinalIgnoreCase),
                    RegisterPadHandler = h => _window.SetModalPadHandler(h),
                    PadFamily = _window.PadFamily,
                    WatchPadFamily = w => _window.WatchPadFamily(w),
                });
        }
        catch (Exception ex) { Log.Info($"Nexus browse: {ex}"); }
        finally
        {
            _window.SetModalPadHandler(null);
            _window.WatchPadFamily(null);
            _window.EndModalDialog();
            _signingIn = false;
        }
        Log.Info($"Nexus browse closed; {sent} link(s) handed to Vortex; foreground is now {NativeMethodsForeground()}");
        await ModsRunAsync(game, ct => _mods.OpenAsync(game, ct));
    }

    /// <summary>For the log line after a browse window closes: which window Windows handed the
    /// foreground to, since "the pad stopped working" reports come down to exactly that.</summary>
    private static string NativeMethodsForeground()
    {
        var fg = Loungepad.Interop.NativeMethods.GetForegroundWindow();
        if (fg == IntPtr.Zero) return "nothing";
        Loungepad.Interop.NativeMethods.GetWindowThreadProcessId(fg, out var pid);
        return pid == (uint)Environment.ProcessId ? "the launcher" : $"another process (pid {pid})";
    }

    private async Task ModsExtensionAsync(Game game, long modId)
    {
        Push(new { type = "toast", message = "Asking Vortex…" });
        try
        {
            var ok = await Task.Run(() => _mods.ShowExtensionAsync(modId, CancellationToken.None));
            if (!ok) { Push(new { type = "toast", message = "Vortex is not answering; try again in a moment" }); return; }
            await ShowVortexAsync();
        }
        catch (Exception ex)
        {
            Log.Info($"Mods extension: {ex}");
            Push(new { type = "toast", message = $"Vortex could not be asked: {ex.Message}" });
        }
    }

    // ---- The first-run setup ----

    /// <summary>
    /// What the setup screens need to know before they offer anything: the launchers on this PC,
    /// whether Playnite has a library here, whether Windows has a PIN, and where Vortex stands. All
    /// of it read-only. Vortex is then peeked at in the background -- only a Vortex that is running
    /// and was connected before can answer that, and nothing is started for it.
    /// </summary>
    private async Task ProbeFirstRunAsync()
    {
        var (launchers, playnite, vortex) = await Task.Run(() =>
            (FirstRun.Launchers(), PlayniteImport.FindDataDir(), _mods.Status(refresh: true)));
        var pin = await FirstRun.PinSetUpAsync();
        Push(new
        {
            type = "onboarding",
            launchers = new { steam = launchers.Steam, epic = launchers.Epic, galaxy = launchers.Galaxy, xboxApp = launchers.XboxApp },
            playnite = new { found = playnite is not null, dir = playnite },
            pin,
            vortex,
        });
        if (vortex.Installed) await OnboardVortexAsync("peek");
    }

    private async Task PushPinAsync() => Push(new { type = "onboardingPin", pin = await FirstRun.PinSetUpAsync() });

    private CancellationTokenSource? _onboardVortexCts;

    /// <summary>Vortex and the games it manages, for the setup's Vortex step. See ModService.SummaryAsync
    /// for what each mode may do; a newer request cancels the one before it.</summary>
    private async Task OnboardVortexAsync(string mode)
    {
        _onboardVortexCts?.Cancel();
        var cts = _onboardVortexCts = new CancellationTokenSource();
        Push(new { type = "onboardingVortex", busy = true, mode });
        var library = _library.Games.ToList();
        VortexSummary sum;
        try { sum = await Task.Run(() => _mods.SummaryAsync(library, mode, cts.Token), cts.Token); }
        catch (OperationCanceledException) { return; }
        catch (Exception ex)
        {
            Log.Info($"First run: Vortex: {ex}");
            sum = new VortexSummary { Vortex = _mods.Status(), Error = ex.Message };
        }
        if (cts.IsCancellationRequested) return;
        if (mode != "peek")
        {
            var why = sum.Error ?? sum.Vortex.Error;
            Log.Info($"First run: Vortex {mode}: bridge {(sum.Vortex.BridgeReady ? "ready" : "not ready")}, {sum.Games.Count} managed game(s){(why is null ? "" : " -- " + why)}");
        }
        Push(new { type = "onboardingVortex", busy = false, mode, vortex = sum.Vortex, games = sum.Games, error = sum.Error });
    }

    /// <summary>
    /// Windows' sign-in options, where a PIN is set up, with Loungepad out of the way: it is topmost
    /// on the TV and Settings would open behind it. Settings also opens wherever Windows last put
    /// it, which is usually the desk monitor, so its window is moved to the TV once it has the
    /// foreground. Only a frame window that turns up in the first few seconds is moved -- the
    /// Settings app is hosted by ApplicationFrameHost like every Store app, and a window that
    /// arrives later could be anything.
    /// </summary>
    private async Task OpenSignInOptionsAsync()
    {
        _window.Park();
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("ms-settings:signinoptions") { UseShellExecute = true }); }
        catch (Exception ex)
        {
            _window.Unpark();
            Push(new { type = "toast", message = $"Could not open Windows' sign-in options: {ex.Message}" });
            return;
        }
        Log.Info("First run: opened Windows' sign-in options");
        if (_settings.Settings.TvDeviceName is not { } tv) return;
        var until = DateTime.UtcNow + TimeSpan.FromSeconds(8);
        while (DateTime.UtcNow < until)
        {
            await Task.Delay(250);
            var fg = Loungepad.Interop.NativeMethods.GetForegroundWindow();
            if (fg == IntPtr.Zero) continue;
            Loungepad.Interop.NativeMethods.GetWindowThreadProcessId(fg, out var pid);
            string name;
            try { using var p = System.Diagnostics.Process.GetProcessById((int)pid); name = p.ProcessName; }
            catch { continue; }
            if (!name.Equals("ApplicationFrameHost", StringComparison.OrdinalIgnoreCase)
                && !name.Equals("SystemSettings", StringComparison.OrdinalIgnoreCase)) continue;
            if (_windows.DisplayOf(fg) != tv) _windows.MoveToDisplay(fg, tv);
            return;
        }
    }

    // ---- The Xbox button: who else takes it ----

    /// <summary>Set while Steam is being closed, edited and reopened, so the row can say so and a
    /// second press does not start a second restart.</summary>
    private bool _steamGuideBusy;

    private object XboxButtonState()
    {
        var w = WindowsGuide.Read();
        return new
        {
            gameBar = w.GameBar,
            xboxMode = w.XboxMode,
            steam = SteamGuide.Read(),
            steamRunning = SteamGuide.IsRunning(),
            steamBusy = _steamGuideBusy,
        };
    }

    private async Task SetSteamGuideAsync(bool on)
    {
        if (_steamGuideBusy) return;
        // Closing Steam ends a Steam game with it, and this process is what tracks the session.
        if (_launcher.GameRunning)
        {
            Push(new { type = "toast", message = "Close the game first: Steam has to restart for this" });
            return;
        }
        bool running = SteamGuide.IsRunning();
        _steamGuideBusy = true;
        PushState();
        if (running) Push(new { type = "toast", message = "Restarting Steam to change this…" });
        string? error;
        try { error = await Task.Run(() => SteamGuide.Set(on)); }
        catch (Exception ex) { error = ex.Message; }
        _steamGuideBusy = false;
        PushState();
        Push(new { type = "toast", message = error
            ?? (on ? "Steam opens on the Xbox button again, and its desktop layout is back"
                   : "Steam no longer takes the Xbox button, and its desktop layout is empty") });
    }

    // ---- Actions: a program chosen from disk ----

    private void BrowseForApp()
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Choose the program to add actions for",
            Filter = "Programs (*.exe)|*.exe|All files (*.*)|*.*"
        };
        if (!ShowDialog(dlg)) { Push(new { type = "actionAppAdded", id = (string?)null, existed = false }); return; }
        try
        {
            var (id, existed) = _window.Actions.AddApp(dlg.FileName, null);
            PushActions();
            Push(new { type = "actionAppAdded", id, existed });
            DetectApps();
        }
        catch (Exception ex)
        {
            Push(new { type = "toast", message = $"Could not add that program: {ex.Message}" });
            Push(new { type = "actionAppAdded", id = (string?)null, existed = false });
        }
    }

    // ---- Emulators and ROM folders ----

    private void AddEmulator()
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Choose the emulator's program (retroarch.exe, Dolphin.exe, pcsx2-qt.exe…)",
            Filter = "Programs (*.exe)|*.exe|All files (*.*)|*.*"
        };
        // The page may be in the middle of adding a ROM folder and waiting on this; a cancel
        // has to be reported too, or it waits forever with nothing on screen.
        if (!ShowDialog(dlg)) { Push(new { type = "emuAdded", id = (string?)null }); return; }

        var exe = dlg.FileName;
        if (_library.Emulators.FirstOrDefault(e => string.Equals(e.ExePath, exe, StringComparison.OrdinalIgnoreCase)) is { } dup)
        {
            Push(new { type = "toast", message = $"{dup.Name} is already set up" });
            Push(new { type = "emuAdded", id = dup.Id });
            return;
        }

        // A known exe brings its name, its command line and the systems it runs; anything else
        // is named after its file and started with the ROM's path and nothing more.
        var preset = EmulatorPresets.Detect(exe);
        var emu = new EmulatorDef
        {
            Id = Guid.NewGuid().ToString("N")[..8],
            Name = preset?.Name ?? Path.GetFileNameWithoutExtension(exe),
            ExePath = exe,
            Args = preset?.Args ?? "\"{rom}\"",
            Preset = preset?.Key,
            Platforms = preset?.Platforms.ToList() ?? new List<string>(),
        };
        _library.AddEmulator(emu);
        PushState();
        Push(new { type = "emuAdded", id = emu.Id });
        Push(new { type = "toast", message = preset is null
            ? $"Added {emu.Name}. Check its launch arguments under Settings → Library"
            : $"Added {emu.Name}" });
    }

    private void PickEmulatorExe(string id)
    {
        var emu = _library.FindEmulator(id);
        if (emu is null) return;
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = $"Choose the program for {emu.Name}",
            Filter = "Programs (*.exe)|*.exe|All files (*.*)|*.*"
        };
        if (Path.GetDirectoryName(emu.ExePath) is { } dir && Directory.Exists(dir)) dlg.InitialDirectory = dir;
        if (!ShowDialog(dlg)) return;
        emu.ExePath = dlg.FileName;
        _library.Save();
        PushState();
        Push(new { type = "toast", message = $"{emu.Name} now runs {Path.GetFileName(dlg.FileName)}" });
    }

    /// <summary>The first step of adding a ROM folder. The rest -- which system, which emulator --
    /// is asked on the page, where a gamepad can answer; the folder's name seeds the system.</summary>
    private void PickRomFolder()
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog { Title = "Choose a folder of ROMs for one system" };
        if (!ShowDialog(dlg)) return;
        var path = dlg.FolderName;
        if (_library.RomFolders.Any(f => string.Equals(f.Path, path, StringComparison.OrdinalIgnoreCase)))
        {
            Push(new { type = "toast", message = "That folder is already in the library" });
            return;
        }
        Push(new { type = "romFolderPicked", path, platformId = EmulatedPlatforms.Guess(Path.GetFileName(path.TrimEnd('\\', '/'))) });
    }

    private void AddRomFolder(string? path, string? platformId, string? emulatorId)
    {
        var platform = EmulatedPlatforms.Find(platformId);
        if (path is null || !Directory.Exists(path) || platform is null)
        {
            Push(new { type = "toast", message = "That folder could not be added" });
            return;
        }
        var emu = _library.FindEmulator(emulatorId);
        var folder = new RomFolderDef
        {
            Id = Guid.NewGuid().ToString("N")[..8],
            Path = path,
            PlatformId = platform.Id,
            EmulatorId = emu?.Id,
        };

        // RetroArch needs a core per system. Take the first of the platform's known cores that
        // is installed, and only open a dialog when none of them is.
        var needsCore = emu is not null && emu.Args.Contains("{core}", StringComparison.OrdinalIgnoreCase);
        if (needsCore)
        {
            folder.Core = EmulatorLaunch.SuggestCore(emu!, platform) ?? PickCoreDialog(emu!, platform);
        }

        _library.AddRomFolder(folder);
        PushState();
        var core = folder.Core is null ? "" : $" with {Path.GetFileNameWithoutExtension(folder.Core)}";
        Push(new { type = "toast", message = needsCore && folder.Core is null
            ? $"Added {platform.Name}. Choose a core for it under Settings → Library before playing"
            : $"Added {platform.Name}{core}. Scanning for games…" });
        StartScan();
    }

    private void PickCore(string folderId)
    {
        var folder = _library.FindRomFolder(folderId);
        var emu = _library.FindEmulator(folder?.EmulatorId);
        var platform = EmulatedPlatforms.Find(folder?.PlatformId);
        if (folder is null || emu is null || platform is null) return;
        var core = PickCoreDialog(emu, platform);
        if (core is null) return;
        folder.Core = core;
        _library.Save();
        PushState();
        Push(new { type = "toast", message = $"{platform.Name} now runs on {Path.GetFileNameWithoutExtension(core)}" });
    }

    private string? PickCoreDialog(EmulatorDef emu, EmulatedPlatforms.Def platform)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = $"Choose the {emu.Name} core for {platform.Name}",
            Filter = "Cores (*_libretro.dll)|*_libretro.dll|Libraries (*.dll)|*.dll|All files (*.*)|*.*"
        };
        if (EmulatorLaunch.CoresDir(emu) is { } cores) dlg.InitialDirectory = cores;
        return ShowDialog(dlg) ? dlg.FileName : null;
    }

    /// <summary>What a credential the page sent back should be stored as. The page is only ever
    /// shown the "set" marker for a key (AppSettings.ForPage), and sends it back untouched unless
    /// the user typed a new one -- so the marker means "keep what is there".</summary>
    private static string Secret(string? incoming, string current) =>
        AppSettings.IsRedacted(incoming) ? current : (incoming ?? "").Trim();

    private void CopySettings(AppSettings s)
    {
        var t = _settings.Settings;
        // The accent is written straight into a CSS custom property, so only a hex colour may
        // get through; anything else keeps whatever was already there.
        if (SettingsStore.IsHexColor(s.AccentColor)) t.AccentColor = s.AccentColor.ToUpperInvariant();
        t.Theme = s.Theme ?? "";
        t.HideLegend = s.HideLegend;
        t.AnimationsEnabled = s.AnimationsEnabled;
        t.AnimationSpeed = Math.Clamp(s.AnimationSpeed, 0.5, 2.0);
        t.ThemeSettings = ThemeService.CleanSettingValues(s.ThemeSettings);
        t.ExtensionSettings = ThemeService.CleanSettingValues(s.ExtensionSettings);
        t.AddonsIndexUrl = (s.AddonsIndexUrl ?? "").Trim();
        t.IgdbClientId = s.IgdbClientId.Trim();
        t.IgdbClientSecret = Secret(s.IgdbClientSecret, t.IgdbClientSecret);
        t.SteamGridDbKey = Secret(s.SteamGridDbKey, t.SteamGridDbKey);
        t.MetadataEndpoint = s.MetadataEndpoint.Trim();
        t.CacheTrailers = s.CacheTrailers;
        t.AgeRatingBoard = s.AgeRatingBoard == "PEGI" ? "PEGI" : "ESRB";
        t.SteamShowOwned = s.SteamShowOwned;
        t.SteamApiKey = Secret(s.SteamApiKey, t.SteamApiKey);
        t.GamePassCatalog = s.GamePassCatalog;
        t.XboxClientId = (s.XboxClientId ?? "").Trim();
        t.DetectEmulators = s.DetectEmulators;
        t.VortexPath = (s.VortexPath ?? "").Trim();
        t.TvDeviceName = s.TvDeviceName;
        t.SwitchPrimaryOnLaunch = s.SwitchPrimaryOnLaunch;
        t.RepositionGameWindow = s.RepositionGameWindow;
        t.KeepFocus = s.KeepFocus;
        t.LaunchOnStartup = s.LaunchOnStartup;
        t.AutoUpdate = s.AutoUpdate;
        t.OnboardingVersion = Math.Max(0, s.OnboardingVersion);
        t.RestAfterMinutes = Math.Clamp(s.RestAfterMinutes, 0, 1440);
        t.RestDuringGame = s.RestDuringGame;
        t.RestPausesGame = s.RestPausesGame;
        t.SleepAfterRestMinutes = Math.Clamp(s.SleepAfterRestMinutes, -1, 1440);
        t.GamepadMouseEnabled = s.GamepadMouseEnabled;
        t.GamepadMouseDuringGame = s.GamepadMouseDuringGame;
        t.SteamOwnsPad = s.SteamOwnsPad;
        t.Deadzone = Math.Clamp(s.Deadzone, 0.05, 0.40);
        t.Sensitivity = Math.Clamp(s.Sensitivity, 0.2, 3.0);
        t.AccelExponent = Math.Clamp(s.AccelExponent, 1.0, 3.0);
        t.BoostButton = s.BoostButton;
        t.BoostMultiplier = Math.Clamp(s.BoostMultiplier, 1.5, 5.0);
        t.HideCursorSystemWide = s.HideCursorSystemWide;
        t.TouchpadMouse = s.TouchpadMouse;
        t.TouchpadSensitivity = Math.Clamp(s.TouchpadSensitivity, 0.25, 4.0);
        t.TouchpadTapToClick = s.TouchpadTapToClick;
        t.TouchpadTapDrag = s.TouchpadTapDrag;
        t.TouchpadNaturalScroll = s.TouchpadNaturalScroll;
        t.TouchpadScrollSpeed = Math.Clamp(s.TouchpadScrollSpeed, 0.25, 4.0);
        t.LeftClickButton = s.LeftClickButton;
        t.RightClickButton = s.RightClickButton;
        t.MinimizeCombo = s.MinimizeCombo;
        t.MenuComboMode = s.MenuComboMode == "DoubleTap" ? "DoubleTap" : "TapHold";
        t.ScreenshotCombo = s.ScreenshotCombo;
        t.ShareButtonScreenshot = s.ShareButtonScreenshot;
        t.KeyRepeatDelayMs = Math.Clamp(s.KeyRepeatDelayMs, 120, 900);
        t.KeyRepeatIntervalMs = Math.Clamp(s.KeyRepeatIntervalMs, 20, 300);
        t.KeyboardToggleButton = s.KeyboardToggleButton;
        t.KeyboardToggleMode = s.KeyboardToggleMode == "Hold" ? "Hold" : "Press";
        t.KeyboardToggleHoldMs = Math.Clamp(s.KeyboardToggleHoldMs, 200, 2000);
        t.KeyboardInGame = s.KeyboardInGame;
        t.KeyboardApp = s.KeyboardApp;
        t.KeyboardScale = Math.Clamp(s.KeyboardScale, KeyboardWindow.ScaleMin, KeyboardWindow.ScaleMax);
        t.KeyboardSuggestions = s.KeyboardSuggestions;
        t.KeyboardFunctionKeys = s.KeyboardFunctionKeys;
        t.KeyboardNavKeys = s.KeyboardNavKeys;
        t.KeyboardNumpad = s.KeyboardNumpad;
        t.KeyboardModifiers = s.KeyboardModifiers;
        t.ActivityTracking = s.ActivityTracking;
        t.ActivityHardware = s.ActivityHardware;
        t.ActivitySampleSeconds = Math.Clamp(s.ActivitySampleSeconds <= 0 ? 5 : s.ActivitySampleSeconds, 2, 30);
        t.AchievementsEnabled = s.AchievementsEnabled;
        t.AchievementNotifications = s.AchievementNotifications;
        t.AchievementsOnTiles = s.AchievementsOnTiles;
        t.RetroAchievementsUser = (s.RetroAchievementsUser ?? "").Trim();
        t.RetroAchievementsKey = Secret(s.RetroAchievementsKey, t.RetroAchievementsKey);
    }


    /// <summary>
    /// Pictures of the open windows, pushed one at a time as they are taken.
    ///
    /// Deliberately not part of the list itself. PrintWindow asks a window to paint, which means
    /// waiting on that window's message loop -- one busy or hung program would otherwise hold up
    /// the whole switcher, and the switcher is the thing you open *because* something is stuck.
    /// The menu draws immediately with titles and fills the pictures in underneath.
    /// </summary>
    private void StartThumbnails(IReadOnlyList<WindowInfo> open)
    {
        if (open.Count == 0) return;
        Task.Run(() =>
        {
            foreach (var w in open.Take(MaxThumbnails))
            {
                // PrintWindow waits on the window's thread, and a frozen game's never answers:
                // the switcher would hang here until the game was thawed.
                if (_launcher.Paused && _launcher.OwnsWindow(new IntPtr(w.Handle))) continue;
                WindowShot shot;
                try { shot = _windows.CaptureWindow(new IntPtr(w.Handle)); }
                catch (Exception ex) { Log.Info($"Thumbnail for '{w.Title}' failed: {ex.Message}"); continue; }
                if (shot.Image is null) continue;

                var (image, icon) = (shot.Image, shot.IsIcon);
                _ = _window.Dispatcher.BeginInvoke(() =>
                    Push(new { type = "windowThumb", handle = w.Handle, image, icon }));
            }
        });
    }

    /// <summary>Past this many the list is scrolling anyway, and each picture costs a round trip
    /// through another program's message loop.</summary>
    private const int MaxThumbnails = 16;

    /// <summary>
    /// <paramref name="force"/> re-asks Steam for the account's library even when the last answer
    /// is recent, for a rescan the user asked for by hand. <paramref name="quiet"/> is the manifest
    /// watcher's mode: no spinner, and the UI is only repainted when a game's installed state
    /// actually changed, because a download rewrites its manifest every few seconds and a full
    /// repaint each time would throw the highlight around under somebody browsing.
    /// </summary>
    private void StartScan(bool force = false, bool quiet = false)
    {
        if (_scanning)
        {
            // A manifest that changed while a scan was already reading the folder may have been
            // read before or after the change; asking again once this one is done settles it.
            if (quiet) ScheduleQuietScan();
            return;
        }
        _scanning = true;
        if (!quiet) Push(new { type = "scanning", busy = true });
        var settings = _settings.Settings;
        // Only a scan somebody can see reports its steps: a quiet one is a manifest write mid-download.
        if (!quiet) BeginSteps(settings);
        Action<string, string, int?> step = quiet ? (_, _, _) => { } : SetStep;
        Task.Run(async () =>
        {
            var before = InstallSignature();
            try
            {
                // Emulators and ROM folders first, so anything found is scanned in the same pass.
                if (settings.DetectEmulators) step("emulators", "running", null);
                var detected = settings.DetectEmulators ? EmulatorDetection.Run(_library) : null;
                if (settings.DetectEmulators) step("emulators", "done", _library.Emulators.Count);
                if (!quiet && detected is { } d && (d.Emulators.Count > 0 || d.Folders.Count > 0))
                    _ = _window.Dispatcher.BeginInvoke(() => Push(new { type = "toast", message = DetectionToast(d) }));

                var found = _scanner.ScanAll(step);
                step("roms", "running", null);
                var roms = _scanner.ScanEmulated(_library.RomFolders.ToList());
                step("roms", "done", roms.Count);
                found.AddRange(roms);
                // What the accounts own but the disk does not have. Each source is independent
                // and each is optional; NotAlreadyFound keeps an installed game from appearing
                // a second time as an owned one.
                var owned = new List<Game>();
                if (settings.SteamShowOwned)
                {
                    step("steamOwned", "running", null);
                    var steamOwned = _scanner.OwnedSteamGames(await _steam.GetOwnedAsync(settings, force), found);
                    owned.AddRange(steamOwned);
                    step("steamOwned", _steam.Status.Error is null ? "done" : "failed", steamOwned.Count);
                }
                foreach (var account in _accounts.Values)
                    if (account.Status.SignedIn)
                    {
                        step(account.Store + "Owned", "running", null);
                        var theirs = await account.GetOwnedAsync(force);
                        owned.AddRange(theirs);
                        step(account.Store + "Owned", account.Status.Error is null ? "done" : "failed", theirs.Count);
                    }
                if (settings.GamePassCatalog)
                {
                    step("gamePass", "running", null);
                    var pass = await _gamePass.GetAsync(force);
                    owned.AddRange(pass);
                    step("gamePass", _gamePass.Status.Error is null ? "done" : "failed", pass.Count);
                }
                found.AddRange(LibraryScanner.NotAlreadyFound(found, owned));
                _library.MergeScanned(found);
            }
            catch (Exception ex)
            {
                Log.Info($"Scan failed: {ex.Message}");
                // Whatever the scan had not reached is not coming: say so rather than spin forever.
                if (!quiet) FailOpenSteps();
            }
            finally
            {
                _scanning = false;
                var changed = !quiet || InstallSignature() != before;
                _ = _window.Dispatcher.BeginInvoke(() =>
                {
                    if (!quiet) Push(new { type = "scanning", busy = false });
                    if (changed) PushState();
                });
            }

            await EnrichMetadata();
            // The achievement lists, after the art: the pass is paced and the library is already
            // on screen, so nothing waits on it. A quiet scan is a manifest write mid-download,
            // which is no reason to walk the library again.
            if (!quiet)
            {
                if (settings.AchievementsEnabled) step("achievements", "running", null);
                try { await _achievements.RefreshAllAsync(force: false); }
                finally { if (settings.AchievementsEnabled) step("achievements", "done", null); }
                // The extensions' own facts, last: they are the most optional thing in the pass,
                // and the games are long on screen by now.
                await _extensions.RunPassAsync();
            }
        });
    }

    // ---- add-ons (docs/ADDONS.md) ----

    /// <summary>The whole of Settings → Add-ons: every theme and extension, installed or listed by
    /// the repository, with where the list came from and what the runtime says of each.</summary>
    private object AddonsPayload() => new
    {
        items = _addons.Describe(a => _extensions.StatusOf(a)),
        catalogue = _addons.CatalogueStatus(),
        launcher = UpdateService.Format(UpdateService.Current),
        passRunning = _extensions.PassRunning,
    };

    public void PushAddons() => Push(new { type = "addons", addons = AddonsPayload() });

    /// <summary>The ids whose option bags differ between what the page sent and what is stored.</summary>
    private static List<string> ChangedExtensionSettings(Dictionary<string, Dictionary<string, JsonElement>>? incoming,
        Dictionary<string, Dictionary<string, JsonElement>>? current)
    {
        var a = ThemeService.CleanSettingValues(incoming);
        var b = ThemeService.CleanSettingValues(current);
        var ids = new List<string>();
        foreach (var id in a.Keys.Union(b.Keys))
        {
            var x = a.TryGetValue(id, out var xa) ? JsonSerializer.Serialize(xa) : "";
            var y = b.TryGetValue(id, out var yb) ? JsonSerializer.Serialize(yb) : "";
            if (x != y) ids.Add(id);
        }
        return ids;
    }

    private async Task InstallAddonAsync(string key)
    {
        try
        {
            var installed = await _addons.InstallFromCatalogueAsync(key);
            await AfterInstallAsync(installed);
        }
        catch (Exception ex) { PushToast($"Could not install: {ex.Message}"); }
    }

    /// <summary>A zip or a folder on this PC, through the file dialogs.</summary>
    private void InstallAddonFromFile(bool folder)
    {
        string? path;
        if (folder)
        {
            var dlg = new Microsoft.Win32.OpenFolderDialog { Title = "Choose the theme's or extension's folder" };
            path = ShowDialog(dlg) ? dlg.FolderName : null;
        }
        else
        {
            var dlg = new Microsoft.Win32.OpenFileDialog { Title = "Choose the theme's or extension's zip", Filter = "Zip archives (*.zip)|*.zip" };
            path = ShowDialog(dlg) ? dlg.FileName : null;
        }
        if (path is null) return;
        _ = InstallAddonFromPathAsync(path, folder);
    }

    private async Task InstallAddonFromPathAsync(string path, bool folder)
    {
        try
        {
            var installed = folder ? await _addons.InstallFromFolderAsync(path) : await _addons.InstallFromZipAsync(path);
            await AfterInstallAsync(installed);
        }
        catch (Exception ex) { PushToast($"Could not install: {ex.Message}"); }
    }

    private async Task ReloadAddonAsync(string key)
    {
        try
        {
            var installed = await _addons.ReloadFromSourceAsync(key);
            await AfterInstallAsync(installed, reloaded: true);
        }
        catch (Exception ex) { PushToast($"Could not reload: {ex.Message}"); }
    }

    /// <summary>An add-on is on disk: a theme reloads through the themes watcher; an extension is
    /// started (or restarted) and asked about the library straight away.</summary>
    private async Task AfterInstallAsync(InstalledAddon a, bool reloaded = false)
    {
        var what = $"{a.Manifest.Name}{(string.IsNullOrEmpty(a.Manifest.Version) ? "" : " " + a.Manifest.Version)}";
        PushToast(reloaded ? $"Reloaded {what}" : $"Installed {what}");
        if (a.Kind == AddonKind.Theme)
        {
            PushThemes();
            PushState();
            return;
        }
        await _extensions.SyncAsync();
        if (a.Enabled) _ = Task.Run(() => _extensions.RunPassAsync(a.Key));
    }

    private void RemoveAddon(string key)
    {
        try
        {
            var gone = _addons.Remove(key);
            if (gone.Kind == AddonKind.Theme)
            {
                // The theme in use just went: back to the bundled one, or Shelf without it.
                if (_settings.Settings.Theme == gone.Id)
                {
                    var fallback = new AppSettings().Theme;
                    _settings.Settings.Theme = Directory.Exists(Path.Combine(Paths.ThemesDir, fallback)) ? fallback : "";
                    _settings.Save();
                }
                PushThemes();
                PushState();
            }
            else
            {
                _ = _extensions.SyncAsync();
                // What it stored stays with the games: a reinstall picks it up again, and a theme
                // that reads the field keeps working. Nothing else reads it once the extension has gone.
            }
            PushToast($"Removed {gone.Manifest.Name}");
        }
        catch (Exception ex) { PushToast($"Could not remove: {ex.Message}"); }
    }

    private async Task EnableAddonAsync(string key, bool on)
    {
        try
        {
            _addons.SetEnabled(key, on);
            await _extensions.SyncAsync();
            if (on) _ = Task.Run(() => _extensions.RunPassAsync(key));
        }
        catch (Exception ex) { PushToast(ex.Message); }
    }

    // ---- the scan, step by step ----
    // What the first-run setup draws while it asks its questions: each source of games in the last
    // scan somebody could see, then the art and the achievement passes that follow it. The page
    // names the steps; the host only says where each one stands.

    private sealed record ScanStep(string Id, string State, int? Count);
    private readonly object _stepsLock = new();
    private readonly List<ScanStep> _steps = new();

    /// <summary>Every step the coming scan will take, up front, so the page can list what is still
    /// to come rather than have the list grow. A source that is switched off, or an account that
    /// is not signed in, is "off" and says so.</summary>
    private void BeginSteps(AppSettings s)
    {
        lock (_stepsLock)
        {
            _steps.Clear();
            void Add(string id, bool on) => _steps.Add(new ScanStep(id, on ? "pending" : "off", null));
            Add("emulators", s.DetectEmulators);
            foreach (var id in new[] { "steam", "epic", "gog", "xbox", "roms" }) Add(id, true);
            Add("steamOwned", s.SteamShowOwned);
            foreach (var account in _accounts.Values) Add(account.Store + "Owned", account.Status.SignedIn);
            Add("gamePass", s.GamePassCatalog);
            // A pass already running from the last scan carries on; this scan's call joins it.
            _steps.Add(new ScanStep("metadata", _enriching ? "running" : "pending", null));
            Add("achievements", s.AchievementsEnabled);
        }
        PushScanProgress();
    }

    /// <summary>From any thread. The push goes through the dispatcher, like every other.</summary>
    private void SetStep(string id, string state, int? count)
    {
        lock (_stepsLock)
        {
            var i = _steps.FindIndex(x => x.Id == id);
            var next = new ScanStep(id, state, count);
            if (i >= 0) _steps[i] = next; else _steps.Add(next);
        }
        _ = _window.Dispatcher.BeginInvoke(PushScanProgress);
    }

    /// <summary>The scan's own steps that were still to come when it failed. Not the passes after
    /// it, which still run.</summary>
    private void FailOpenSteps()
    {
        lock (_stepsLock)
            for (var i = 0; i < _steps.Count; i++)
                if (_steps[i] is { State: "pending" or "running" } open && open.Id is not ("metadata" or "achievements"))
                    _steps[i] = open with { State = "failed" };
        _ = _window.Dispatcher.BeginInvoke(PushScanProgress);
    }

    private object ScanStepsPayload()
    {
        lock (_stepsLock) return _steps.Select(x => new { id = x.Id, state = x.State, count = x.Count }).ToList();
    }

    private void PushScanProgress() => Push(new { type = "scanProgress", steps = ScanStepsPayload() });

    /// <summary>"Found RetroArch and PCSX2; added Game Boy Advance and Nintendo DS ROMs".</summary>
    private static string DetectionToast(DetectionSummary d)
    {
        static string Join(IEnumerable<string> names)
        {
            var list = names.ToList();
            return list.Count switch
            {
                0 => "",
                1 => list[0],
                2 => $"{list[0]} and {list[1]}",
                _ => string.Join(", ", list.Take(list.Count - 1)) + " and " + list[^1],
            };
        }
        var bits = new List<string>();
        if (d.Emulators.Count > 0) bits.Add("found " + Join(d.Emulators.Select(e => e.Name)));
        if (d.Folders.Count > 0)
            bits.Add("added " + Join(d.Folders.Select(f => EmulatedPlatforms.Find(f.PlatformId)?.Name ?? f.PlatformId).Distinct()) + " ROMs");
        var text = string.Join("; ", bits);
        return char.ToUpperInvariant(text[0]) + text[1..];
    }

    /// <summary>Which games exist and which are on disk: the two things a manifest change can alter,
    /// and the only two a quiet scan repaints for.</summary>
    private string InstallSignature() =>
        string.Join("\n", _library.Games.Select(g => g.Installed ? g.Id + "+" : g.Id)
            .OrderBy(x => x, StringComparer.Ordinal));

    /// <summary>
    /// A Steam install started from here -- or from the Steam client, or from a phone -- shows up
    /// as an appmanifest arriving and, some minutes later, its StateFlags gaining the "fully
    /// installed" bit. Watching for that is what lets a tile turn from grey to playable while you
    /// are looking at it rather than on the next start. Debounced, because a download rewrites
    /// its manifest every few seconds; and the scan it triggers is the quiet kind.
    /// </summary>
    private void StartInstallWatcher()
    {
        _manifestTimer = new System.Threading.Timer(
            _ => _window.Dispatcher.BeginInvoke(() => StartScan(quiet: true)),
            null, Timeout.Infinite, Timeout.Infinite);

        foreach (var root in LibraryScanner.SteamLibraryRoots())
        {
            try
            {
                var w = new FileSystemWatcher(root, "appmanifest_*.acf")
                {
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
                    IncludeSubdirectories = false,
                };
                w.Created += (_, _) => ScheduleQuietScan();
                w.Changed += (_, _) => ScheduleQuietScan();
                w.Deleted += (_, _) => ScheduleQuietScan();
                w.Renamed += (_, _) => ScheduleQuietScan();
                w.Error += (_, e) => Log.Info($"Manifest watcher on {root}: {e.GetException().Message}");
                w.EnableRaisingEvents = true;
                _manifestWatchers.Add(w);
            }
            catch (Exception ex) { Log.Info($"Cannot watch {root} for installs: {ex.Message}"); }
        }
    }

    private void ScheduleQuietScan() =>
        _manifestTimer?.Change(TimeSpan.FromSeconds(5), Timeout.InfiniteTimeSpan);

    /// <summary>The only things an Install may start. Each is a store's own registered scheme, and
    /// each opens that store's client rather than running a file.</summary>
    private static readonly string[] InstallSchemes =
    {
        "steam://install/", "com.epicgames.launcher://apps/", "goggalaxy://openGameView/", "ms-windows-store://pdp/",
        // GOG without Galaxy: the game's own page on gog.com, where the installer is.
        "https://www.gog.com/",
    };

    /// <summary>
    /// The store's own sign-in page, in a window over the launcher. Always-on-top is dropped
    /// around it the way it is around a file dialog, or the window would open behind the
    /// launcher and look like nothing happened; and one sign-in at a time, because two windows
    /// fighting over the foreground is not something a gamepad can sort out.
    /// </summary>
    private async Task SignInAsync(IStoreAccount account)
    {
        if (_signingIn) return;
        _signingIn = true;
        _window.BeginModalDialog();
        var ok = false;
        try { ok = await account.SignInAsync(_window); }
        catch (Exception ex) { Log.Info($"{account.Store}: sign-in failed ({ex})"); }
        finally
        {
            _window.EndModalDialog();
            _signingIn = false;
        }

        PushState();
        if (ok)
        {
            var who = account.Status.User is { Length: > 0 } u ? $" as {u}" : "";
            Push(new { type = "toast", message = $"Signed in to {account.DisplayName}{who}" });
            StartScan(force: true);
        }
        else
        {
            Push(new { type = "toast", message = account.Status.Error ?? $"{account.DisplayName} sign-in cancelled" });
        }
    }

    /// <summary>
    /// Steam's sign-in, the same window and the same one-at-a-time rule as the other stores'. Being
    /// signed in is the opt-in for the rest of the library, as it is for Epic, GOG and Xbox, so a
    /// successful sign-in turns "show games you own" on and rescans.
    /// </summary>
    private async Task SignInSteamAsync()
    {
        if (_signingIn) return;
        _signingIn = true;
        _window.BeginModalDialog();
        var ok = false;
        try { ok = await _steamWeb.SignInAsync(_window); }
        catch (Exception ex) { Log.Info($"steam: sign-in failed ({ex})"); }
        finally
        {
            _window.EndModalDialog();
            _signingIn = false;
        }
        if (ok && !_settings.Settings.SteamShowOwned)
        {
            _settings.Settings.SteamShowOwned = true;
            _settings.Save();
        }
        PushState();
        Push(new { type = "toast", message = ok ? "Signed in to Steam" : "Steam sign-in cancelled" });
        if (ok) StartScan(force: true);
    }

    /// <summary>
    /// Only Steam writes a manifest the watcher can see. Epic, GOG and the Microsoft Store install
    /// wherever the user pointed them, so after asking one of them to install, the library is
    /// re-read once a minute until the game is on disk or three hours have passed -- quietly, so
    /// the tile simply turns playable.
    /// </summary>
    private void BeginInstallPolling(string id)
    {
        _pendingInstall = id;
        _pollUntil = DateTime.UtcNow.AddHours(3);
        _installPoll ??= new System.Threading.Timer(
            _ => _window.Dispatcher.BeginInvoke(PollInstall), null, Timeout.Infinite, Timeout.Infinite);
        _installPoll.Change(TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));
    }

    private void PollInstall()
    {
        var done = _pendingInstall is null
                   || DateTime.UtcNow > _pollUntil
                   || _library.Find(_pendingInstall)?.Installed == true;
        if (done)
        {
            _installPoll?.Change(Timeout.Infinite, Timeout.Infinite);
            _pendingInstall = null;
            return;
        }
        StartScan(quiet: true);
    }

    /// <summary>
    /// Runs after the library is already on screen, and never blocks it. Fetching art and facts is
    /// network-bound and takes a while on a big library, but nothing here is needed to browse or
    /// to start a game -- so the scan spinner is already down by the time this begins, and the
    /// only visible effect is that art sharpens and the detail page fills in a moment later.
    /// </summary>
    private async Task EnrichMetadata()
    {
        if (_enriching) return;
        _enriching = true;
        SetStep("metadata", "running", null);
        try
        {
            // A snapshot: a rescan may replace the library while this is in flight, and its merge
            // carries across whatever has been written by then. Every twenty seconds the pass hands
            // over what it has so far: saved, so closing the launcher mid-pass keeps it, and pushed,
            // so the tiles fill in as it goes rather than all at once half an hour later.
            void Checkpoint()
            {
                _library.Save();
                _ = _window.Dispatcher.BeginInvoke(PushState);
            }
            var (changed, stamped) = await _metadata.EnrichAsync(_library.Games.ToList(), _settings.Settings, Checkpoint);
            // The stamps are saved even when nothing new was found: unsaved, a game with no
            // metadata anywhere was looked up again on every single start.
            if (changed == 0 && stamped == 0) return;
            _library.Save();
            if (changed > 0) _ = _window.Dispatcher.BeginInvoke(PushState);
        }
        catch (Exception ex) { Log.Info($"Metadata pass failed: {ex.Message}"); }
        finally
        {
            _enriching = false;
            SetStep("metadata", "done", null);
        }
    }

    private void AddManualGame()
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Choose the game executable",
            Filter = "Programs (*.exe)|*.exe|All files (*.*)|*.*"
        };
        if (!ShowDialog(dlg)) return;

        var exe = dlg.FileName;
        var game = new Game
        {
            Id = $"manual:{Guid.NewGuid():N}",
            Title = Path.GetFileNameWithoutExtension(exe),
            Platform = "Manual",
            Manual = true,
            ExePath = exe,
            InstallDir = Path.GetDirectoryName(exe),
            Installed = true
        };
        try { game.SizeBytes = new FileInfo(exe).Length; } catch { }

        // Optional cover art
        var art = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Choose cover art (optional — press Cancel to skip)",
            Filter = "Images (*.jpg;*.jpeg;*.png;*.webp)|*.jpg;*.jpeg;*.png;*.webp"
        };
        if (ShowDialog(art))
            game.CoverFile = CopyArt(art.FileName, game.Id, "");

        _library.AddManual(game);
        PushState();
        Push(new { type = "toast", message = $"Added {game.Title}" });
    }

    /// <summary>Show a modal dialog with always-on-top suspended, so it can't open behind the launcher.</summary>
    private bool ShowDialog(Microsoft.Win32.CommonDialog dlg)
    {
        _window.BeginModalDialog();
        try { return dlg.ShowDialog(_window) == true; }
        finally { _window.EndModalDialog(); }
    }

    private void PickExe(string id)
    {
        var game = _library.Find(id);
        if (game is null) return;
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = $"Choose the executable to launch for {game.Title}",
            Filter = "Programs (*.exe)|*.exe|All files (*.*)|*.*",
        };
        if (game.InstallDir is not null && Directory.Exists(game.InstallDir))
            dlg.InitialDirectory = game.InstallDir;
        if (!ShowDialog(dlg)) return;

        game.ExePath = dlg.FileName;
        if (game.Platform is "Steam" or "Epic") game.PreferDirectLaunch = true;
        _library.Save();
        PushState();
        Push(new { type = "toast", message = $"{game.Title} now launches {Path.GetFileName(dlg.FileName)} directly" });
    }

    /// <summary>
    /// The two pictures worth letting somebody replace by hand, and why they are separate.
    ///
    /// They are not the same shape and they are not in the same places. The cover is portrait box
    /// art, 2:3; the tile is the 1.75:1 landscape sheet the library grid and the recents dock are
    /// made of. One dialog that set both would put whichever file was chosen into a slot it was
    /// the wrong shape for, which is the exact mistake that made tiles look like they carried
    /// another game's art in the first place.
    /// </summary>
    private static readonly Dictionary<string, (string Label, string Suffix)> ArtSlots = new()
    {
        ["cover"] = ("cover art", ""),
        ["tile"] = ("tile art", "_tile"),
    };

    private void PickArt(string id, string slot)
    {
        var game = _library.Find(id);
        if (game is null || !ArtSlots.TryGetValue(slot, out var def)) return;

        var art = new Microsoft.Win32.OpenFileDialog
        {
            Title = $"Choose {def.Label} for {game.Title}",
            Filter = "Images (*.jpg;*.jpeg;*.png;*.webp)|*.jpg;*.jpeg;*.png;*.webp"
        };
        if (!ShowDialog(art)) return;

        var name = CopyArt(art.FileName, game.Id, def.Suffix);
        if (name is null) { Push(new { type = "toast", message = "That image could not be copied" }); return; }

        if (slot == "tile") game.BannerFile = name; else game.CoverFile = name;
        _library.Save();
        PushState();
    }

    /// <summary>
    /// Back to whatever the last fetch found. Clearing the field rather than deleting the file:
    /// the covers folder is a cache and an orphan in it costs nothing, where a delete on a path
    /// built from a game id is the kind of thing that only has to be wrong once. The next enrich
    /// refills the slot -- the stamp is cleared so it does not wait a fortnight to do it.
    /// </summary>
    private void ResetArt(string id, string slot)
    {
        var game = _library.Find(id);
        if (game is null || !ArtSlots.ContainsKey(slot)) return;

        if (slot == "tile") game.BannerFile = null; else game.CoverFile = null;
        game.MetadataFetched = null;
        _library.Save();
        PushState();
        Push(new { type = "toast", message = "Artwork will be fetched again on the next scan" });
    }

    /// <summary>
    /// Art the user chose by hand. The "custom_" prefix is what keeps it: it is the one name
    /// neither the scanner nor MetadataService ever writes, so a rescan cannot overwrite the file
    /// and an enrich cannot point the game away from it. The suffix keeps one slot's choice from
    /// landing on top of another's.
    /// </summary>
    private static string? CopyArt(string source, string gameId, string suffix)
    {
        try
        {
            // Every character that is not plainly a name goes, not just the colon. A game id is
            // ours -- the scanner writes "steam:1091500" and "manual:<guid>" -- but this one
            // arrives from the page and ends up in a file path, and "ours" is an argument about
            // where it came from rather than a property of the string in hand.
            var safe = Regex.Replace(gameId, "[^A-Za-z0-9_-]", "_");
            var ext = Path.GetExtension(source).ToLowerInvariant();
            var dest = Path.Combine(Paths.CoversDir, $"custom_{safe}{suffix}{ext}");
            File.Copy(source, dest, overwrite: true);

            // A second pick with a different extension would otherwise leave the old file behind
            // and, since the name is what the UI loads, leave it showing until a restart.
            foreach (var stale in Directory.GetFiles(Paths.CoversDir, $"custom_{safe}{suffix}.*"))
                if (!string.Equals(stale, dest, StringComparison.OrdinalIgnoreCase))
                    try { File.Delete(stale); } catch { /* a cache file; not worth failing over */ }

            return Path.GetFileName(dest);
        }
        catch { return null; }
    }

    // ---- host -> UI pushes ----

    private bool _secureInputBusy;
    private async Task SetSecureInputAsync(string feature, bool enabled, bool installConfirmed)
    {
        if (_secureInputBusy) return;
        _secureInputBusy = true;
        try { await _window.SetSecureInput(feature, enabled, installConfirmed); PushToast($"Controller input for {(feature == "uac" ? "UAC" : "sign-in")} {(enabled ? "enabled" : "disabled")}; the service remains installed"); }
        catch (InputServiceInstallRequiredException) { Push(new { type = "secureInputInstallRequired", feature }); }
        catch (OperationCanceledException) { PushToast("Input service setup cancelled"); }
        catch (Exception ex) { PushToast($"Secure input: {ex.Message}"); }
        finally { _secureInputBusy = false; PushSecureInput(); }
    }

    private async Task UninstallSecureInputAsync()
    {
        if (_secureInputBusy) return;
        _secureInputBusy = true;
        try { await _window.UninstallSecureInput(); PushToast("Loungepad input service uninstalled"); }
        catch (OperationCanceledException) { PushToast("Input service uninstall cancelled"); }
        catch (Exception ex) { PushToast($"Input service: {ex.Message}"); }
        finally { _secureInputBusy = false; PushSecureInput(); }
    }

    private async Task UpdateSecureInputAsync()
    {
        if (_secureInputBusy) return;
        _secureInputBusy = true;
        try { PushToast($"Loungepad input service updated to {await _window.UpdateSecureInput()}"); }
        catch (OperationCanceledException) { PushToast("Input service update cancelled"); }
        catch (Exception ex) { PushToast($"Input service: {ex.Message}"); }
        finally { _secureInputBusy = false; PushSecureInput(); }
    }

    public void PushSecureInput() => Push(new { type = "secureInput", status = _window.SecureInputStatus });

    /// <summary>
    /// The input service is older than this launcher: ask once per version whether to update it,
    /// on the start after Loungepad itself updated. Updating needs Windows' administrator approval,
    /// so it cannot happen on its own the way the launcher's update does. Only asked while one of
    /// its switches is on (somebody who turned both off is not using it); Settings → Advanced
    /// offers it either way. Marked as asked when sent, so Later holds until the next version.
    /// </summary>
    private void PushSecureInputUpdate()
    {
        if (_window.PendingSecureInputUpdate() is not { } due || !due.InUse) return;
        var target = UpdateService.Format(due.Target);
        if (_settings.Settings.SecureInputUpdateAsked == target) return;
        _settings.Settings.SecureInputUpdateAsked = target;
        _settings.Save();
        Push(new { type = "secureInputUpdate", installed = UpdateService.Format(due.Installed), version = target });
        Log.Info($"Input service: {UpdateService.Format(due.Installed)} installed, offered {target}");
    }

    /// <summary>
    /// This version's release notes, once: on the first start of a version this install has not
    /// shown the notes for, whether the updater installed it or a new exe was put in place by hand.
    /// Never on a new install, which opens on the first-run setup, and never for a version older
    /// than notes already shown (a newer build was run, then this one). The notes are
    /// release-notes\v&lt;version&gt;.md, built in as whatsnew.md; the page leaves out the download
    /// steps. Marked as seen when sent, so a launcher closed before reading them does not show
    /// them again. Returns whether anything was sent.
    /// </summary>
    private bool PushWhatsNew()
    {
        var s = _settings.Settings;
        var current = UpdateService.Format(UpdateService.Current);
        if (s.WhatsNewSeen == current) return false;
        if (Version.TryParse(s.WhatsNewSeen, out var seen) && seen > UpdateService.Current) return false;
        bool newInstall = s.OnboardingVersion == 0;
        s.WhatsNewSeen = current;
        _settings.Save();
        if (newInstall) return false;
        var notes = ShippedFiles.ReadAllText("whatsnew.md");
        if (string.IsNullOrWhiteSpace(notes)) return false;
        Push(new { type = "whatsNew", version = current, notes });
        Log.Info($"What's new: showed the notes for {current}");
        return true;
    }

    public void PushState()
    {
        var s = _settings.Settings;
        Push(new
        {
            type = "state",
            games = _library.Games,
            collections = _library.Collections,
            // The keys stay on this side; the page sees only that each is set or not.
            settings = s.ForPage(),
            secureInput = _window.SecureInputStatus,
            displays = _displays.GetDisplays(),
            startupRegistered = StartupService.IsRegistered(),
            update = _updates.Status,
            themes = _themes.List(),
            addons = AddonsPayload(),
            gameRunning = _launcher.GameRunning,
            runningGameId = _launcher.RunningGameId,
            gameStarting = _launcher.Starting,
            // When the running game was started, for the "playing for" readout.
            sessionStart = _activity.Current?.Start,
            // Per game: unlocked, total, score, the last unlock. Enough for a tile and a stats
            // row; the lists themselves are asked for by page.
            achievements = _achievements.Summaries(),
            scanning = _scanning,
            // Where the last scan and its passes stand, source by source (see BeginSteps), so a
            // page that opens its first-run setup mid-scan has the progress strip at once.
            scanProgress = ScanStepsPayload(),
            steamAccount = _steam.Status,
            actions = ActionsPayload(icons: false),
            // Who else acts on the Xbox button, for Settings → Controller → Windows and Steam and
            // the menu combo's warning. Two registry reads and one cached file read.
            xboxButton = XboxButtonState(),
            // Rest mode's phase, whether the game is frozen, and what can wake this PC (the last
            // answer; see RefreshWake).
            rest = RestPayload(),
            stores = new
            {
                steam = _steamWeb.Status,
                epic = _accounts["epic"].Status,
                gog = _accounts["gog"].Status,
                xbox = _accounts["xbox"].Status,
                gamePass = _gamePass.Status,
            },
            // Whether Vortex is here and where, for the Settings row. The Mods screen itself asks
            // afresh through modsOpen, which is what may start Vortex.
            mods = _mods.Status(),
            // The page draws the emulator and ROM folder rows from these, and the system picker
            // from the catalogue; it never sends any of it back (see the emu* commands).
            emulation = new
            {
                emulators = _library.Emulators,
                romFolders = _library.RomFolders,
                platforms = EmulatedPlatforms.All.Select(p => new
                {
                    id = p.Id, name = p.Name, shortName = p.Short,
                    extensions = p.Extensions, hasCores = p.Cores.Length > 0,
                }),
            }
        });
    }

    public void PushBattery(BatteryState b) =>
        Push(new { type = "battery", present = b.Present, percent = b.Percent, charging = b.Charging, level = b.CoarseLevel });

    /// <summary>
    /// Politely close every visible window the running game owns, then ask its processes
    /// directly — a fullscreen game may own no window we can enumerate. Returns how many things
    /// were asked, so the caller can tell "closing…" from "there was nothing to close".
    /// </summary>
    /// <summary>Close the running game's windows and ask its processes to quit. <c>Found</c> is
    /// false when nothing of the game was running at all, in which case the launcher has already
    /// ended the session (GameLaunchService.Abandon) and the library is free.</summary>
    private (int Closed, bool Found) CloseRunningGame()
    {
        int n = 0;
        foreach (var w in _windows.ListWindows())
        {
            var h = new IntPtr(w.Handle);
            if (!_launcher.OwnsWindow(h)) continue;
            _windows.Close(h);
            n++;
        }
        n += _launcher.RequestClose(out bool found);
        return (n, found || n > 0);
    }

    /// <summary>
    /// Close whatever is running and start <paramref name="next"/> once it has actually gone.
    /// Launching straight away would race the old game's teardown — it still owns the display
    /// mode we are about to change and the foreground we are about to take.
    /// </summary>
    private async Task SwapRunningGame(Game next)
    {
        var outgoing = _launcher.RunningGameId is { } id ? _library.Find(id)?.Title : null;
        Log.Info($"Swapping {outgoing ?? "running game"} -> {next.Title}");

        var (closed, found) = CloseRunningGame();
        // Nothing of the old game running: the launcher has ended that session itself, so the
        // wait below returns at once and the new game starts. Processes with no window to close
        // are the one case that still has to be waited out.
        if (closed == 0 && found)
        {
            Push(new { type = "toast", message = $"{outgoing ?? "The game"} has no window to close yet" });
            return;
        }

        // Generous: a game that prompts to save, or a launcher-chained title, can take a while.
        if (!await _launcher.WaitForExitAsync(TimeSpan.FromSeconds(25)))
        {
            Push(new { type = "toast", message = $"{outgoing ?? "The game"} didn't close — {next.Title} not started" });
            return;
        }

        Push(new { type = "toast", message = $"Launching {next.Title}…" });
        _launcher.Launch(next);
    }

    private static string ActionToast(string? act) => act switch
    {
        "close" => "Closing window…",
        "focus" => "Brought to the TV",
        _ => "Done"
    };

    public void PushOverlay(string mode, string targetTitle, string? shot, string targetProcess, bool targetIsGame, bool overLauncher) =>
        Push(new
        {
            type = "overlay",
            mode,
            targetTitle,
            // The exe name behind the window the menu opened over ("firefox"), which is what the
            // action wheel matches its apps on; empty when the launcher itself was in front.
            targetProcess,
            // That window is the running game's: the Power Wheel's Close spoke then says "Close
            // game" and goes through closeGame, which thaws a paused game before asking it.
            targetIsGame,
            // Opened over Loungepad itself: the Close spoke is "Exit Loungepad". The target title
            // is still the last window it acted on, which is not what is behind the menu now.
            overLauncher,
            shot,
            windows = _windows.ListWindows(),
            displays = _displays.GetDisplays(),
            runningGameId = _launcher.RunningGameId
        });

    // ---- actions ----

    /// <summary>Every app and its actions, as the Settings grid and the wheel draw them. Pushed
    /// on its own after an edit rather than as a state push, which would rebuild the library.</summary>
    public void PushActions() => Push(new { type = "actions", actions = ActionsPayload(icons: true) });

    /// <summary>The icons are a hundred kilobytes of PNG and only this message carries them; a
    /// state push leaves them out and the page keeps the ones it has.</summary>
    private object ActionsPayload(bool icons)
    {
        var acts = _window.Actions;
        return new
        {
            apps = acts.Apps.Select(a => new
            {
                id = a.Id, name = a.Name, exes = a.Exes, custom = a.Custom, pinned = a.Pinned,
                installed = acts.Installed(a), modified = acts.Modified(a.Id), icon = icons ? acts.Icon(a) : null,
                actions = a.Actions.Select(x => new
                {
                    id = x.Id, name = x.Name, keys = x.Keys, button = x.Button,
                    hidden = x.Hidden, custom = x.Custom, danger = x.Danger,
                }),
            }),
        };
    }

    public void PushActionCaptured(string? combo) => Push(new { type = "actionCaptured", combo });
    public void PushActionCaptureRejected(string button) => Push(new { type = "actionCaptureRejected", button });

    /// <summary>Look for the packs' programs and their icons, off the UI thread, then tell the page.</summary>
    private void DetectApps()
    {
        Task.Run(() =>
        {
            try { _window.Actions.Detect(); }
            catch (Exception ex) { Log.Info($"Actions: detection failed: {ex.Message}"); }
            _window.Dispatcher.BeginInvoke(PushActions);
        });
    }

    public void PushStick(double x, double y) => Push(new { type = "stick", x, y });

    /// <summary>Tell the UI to tear down whatever overlay menu it has open.</summary>
    public void PushDismiss() => Push(new { type = "dismiss" });

    public void PushInputMode(string mode) => Push(new { type = "inputMode", mode });

    /// <summary>Re-send the theme list after a change on disk, so an edit reloads live.</summary>
    public void PushThemes() => Push(new { type = "themes", themes = _themes.List() });

    public void PushUpdate(UpdateStatus status) => Push(new { type = "update", status });

    public void PushToast(string message) => Push(new { type = "toast", message });

    public void PushGameState() =>
        Push(new { type = "game", running = _launcher.GameRunning, id = _launcher.RunningGameId, since = _activity.Current?.Start, paused = _launcher.Paused,
            // No process of the game seen yet: the page says "starting" rather than "running".
            starting = _launcher.Starting });

    // ---- rest mode ----

    private WakeReport? _wake;
    private DateTime _wakeAt;
    private bool _wakeBusy;

    /// <summary>
    /// What can wake this PC, for Settings → General → Rest and sleep. Read off the UI thread --
    /// it spawns powercfg -- and pushed on its own message when it lands; a state push carries
    /// the last answer. Re-read at most every half minute unless forced, which an elevated change
    /// does.
    /// </summary>
    private void RefreshWake(bool force = false)
    {
        if (_wakeBusy || (!force && _wake is not null && DateTime.UtcNow - _wakeAt < TimeSpan.FromSeconds(30))) return;
        _wakeBusy = true;
        Task.Run(WakeInfo.Read).ContinueWith(t => _window.Dispatcher.BeginInvoke(() =>
        {
            _wakeBusy = false;
            if (t.IsCompletedSuccessfully) { _wake = t.Result; _wakeAt = DateTime.UtcNow; }
            else Log.Info($"Wake: reading what can wake the PC failed: {t.Exception?.GetBaseException().Message}");
            PushRest();
        }));
    }

    private object RestPayload() => new
    {
        phase = _window.Rest.Current.ToString().ToLowerInvariant(),
        paused = _launcher.Paused,
        wake = _wake is null ? null : new
        {
            canSleep = _wake.CanSleep, modernStandby = _wake.ModernStandby, signInOnWake = _wake.SignInOnWake,
            devices = _wake.Devices, lastWake = _wake.LastWake,
        },
    };

    /// <summary>The rest phase or the frozen game changed, or the wake facts were re-read.</summary>
    public void PushRest() => Push(new { type = "rest", rest = RestPayload() });

    /// <summary>The latest reading during a session, for the in-game menu's readout.</summary>
    private void PushTelemetry(ActivityService.Live live) =>
        Push(new
        {
            type = "telemetry", id = live.GameId, since = live.Start, sample = live.Sample,
            fpsSource = live.Sources.Fps, sensorSource = live.Sources.Sensors,
        });

    /// <summary>One game's list as the page reads it: the set's numbers, then every item with
    /// its icon URLs and, where one is on disk, the cached file's name.</summary>
    private void PushAchievements(GameAchievements? set, object? head)
    {
        Push(new
        {
            type = "achievements",
            id = set?.GameId ?? _achievementsOpenId,
            head,
            set = set is null ? null : AchievementSetDto(set),
        });
    }

    private static object AchievementSetDto(GameAchievements set) => new
    {
        gameId = set.GameId, source = set.Source, sourceGameId = set.SourceGameId, sourceName = set.SourceName,
        fetchedAt = set.FetchedAt == default ? (DateTime?)null : set.FetchedAt, error = set.Error,
        unlocked = set.Unlocked, total = set.Total, score = set.Score, totalScore = set.TotalScore,
        items = set.Items.Select(AchievementDto),
    };

    /// <summary>Works through the square queue. A service that answers "slow down" or cannot be
    /// reached is waited out once for a minute, then the rest is left for the next session.
    /// Saves every twenty games and at the end; each find goes to the page as one field.</summary>
    private async Task FetchSquaresAsync()
    {
        int found = 0, none = 0, dirty = 0;
        var retried = false;
        try
        {
            while (true)
            {
                string? id;
                lock (_squareQueue)
                {
                    if (!_squareQueue.TryDequeue(out id)) { _squareRunning = false; break; }
                }
                var game = _library.Find(id);
                if (game is null) continue;
                var result = await _metadata.FetchSquareAsync(game, _settings.Settings);
                if (result == MetadataService.SquareResult.Unavailable)
                {
                    if (retried)
                    {
                        lock (_squareQueue) { _squareQueue.Clear(); _squareQueued.Clear(); _squareRunning = false; }
                        Log.Info("Squares: the service is not answering; the rest are left for later");
                        break;
                    }
                    retried = true;
                    lock (_squareQueue) { _squareQueue.Enqueue(game.Id); }
                    await Task.Delay(TimeSpan.FromSeconds(65));
                    continue;
                }
                retried = false;
                if (result == MetadataService.SquareResult.Found)
                {
                    found++;
                    var file = game.SquareFile;
                    _ = _window.Dispatcher.BeginInvoke(() => Push(new { type = "square", id = game.Id, file }));
                }
                else { none++; game.SquareCheckedAt = DateTime.UtcNow; }
                if (++dirty >= 20) { _library.Save(); dirty = 0; }
                await Task.Delay(300);
            }
        }
        catch (Exception ex)
        {
            Log.Info($"Squares: {ex.Message}");
            lock (_squareQueue) { _squareRunning = false; }
        }
        finally
        {
            if (dirty > 0) _library.Save();
            if (found + none > 0) Log.Info($"Squares: {found} found, {none} with none");
        }
    }

    /// <summary>Eight each of the latest unlocks, the rarest unlocks and the locked ones most
    /// players have -- never a hidden one, which would give it away -- and up to forty in one
    /// row. The preview's mock answers with the same rules.</summary>
    private static object AchievementPeekDto(GameAchievements set)
    {
        const int n = 8, row = 40;
        var unlocked = set.Items.Where(a => a.Unlocked).ToList();
        // The row holds both kinds: at least half of it for the locked ones when there are that
        // many, so a game with forty unlocks still shows what is left to get.
        var byDate = unlocked.OrderByDescending(a => a.UnlockedAt ?? DateTime.MinValue).ToList();
        var locked = set.Items.Where(a => !a.Unlocked && !a.Hidden).OrderByDescending(a => a.Percent ?? 101)
            .Concat(set.Items.Where(a => !a.Unlocked && a.Hidden)).ToList();
        var nLocked = Math.Min(locked.Count, Math.Max(row / 2, row - byDate.Count));
        var nUnlocked = Math.Min(byDate.Count, row - nLocked);
        return new
        {
            recent = unlocked.OrderByDescending(a => a.UnlockedAt ?? DateTime.MinValue).Take(n).Select(AchievementDto),
            rarest = unlocked.Where(a => a.Percent is not null).OrderBy(a => a.Percent).Take(n).Select(AchievementDto),
            next = set.Items.Where(a => !a.Unlocked && !a.Hidden).OrderByDescending(a => a.Percent ?? 101).Take(n).Select(AchievementDto),
            // A row of them for a theme's Activities: the unlocked, latest first, then the locked
            // ones most players have, then the hidden ones (the page names those "Hidden").
            list = byDate.Take(nUnlocked).Concat(locked.Take(nLocked)).Select(AchievementDto),
        };
    }

    private static object AchievementDto(Achievement a) => new
    {
        a.Id, a.Name, a.Description, a.Hidden, a.Unlocked, a.UnlockedAt, a.Percent, a.Score,
        // An edit by hand, and what the store said before it, for the sheet to show both.
        edited = a.Edited ? true : (bool?)null,
        storeUnlocked = a.Edited ? a.StoreUnlocked : (bool?)null,
        storeUnlockedAt = a.Edited ? a.StoreUnlockedAt : null,
        // Only an https URL the page can safely put in markup; a store's answer is data, not
        // something to trust with the page's own authority.
        icon = AchievementService.SafeIconUrl(a.IconUrl), iconLocked = AchievementService.SafeIconUrl(a.IconLockedUrl),
        iconFile = AchievementService.IconFileIfCached(a.IconUrl),
        iconLockedFile = AchievementService.IconFileIfCached(a.IconLockedUrl),
    };

    /// <summary>A fetch landed: the summary goes to everyone (tiles, the stats screen), the list
    /// only to a page that has this game open.</summary>
    private void OnAchievementsFetched(GameAchievements set)
    {
        Push(new { type = "achievementsSummary", id = set.GameId, summary = AchievementSummary.Of(set) });
        if (_achievementsOpenId == set.GameId) PushAchievements(set, null);
    }

    /// <summary>An unlock can be stamped a little either side of the session it happened in: the
    /// store's clock against this PC's, and a game that writes its stats on the way out.</summary>
    private static readonly TimeSpan SessionUnlockSlack = TimeSpan.FromMinutes(2);

    /// <summary>How far back the Stats screen's overview gets every unlock in full. Older ones
    /// arrive only as counts per day (<c>unlocksByDay</c>), which is what the charts need.</summary>
    private const int RecentUnlockDays = 35;
    private const int RecentUnlockCap = 1500;

    private record DatedUnlock(DateTime At, object Dto);

    /// <summary>One game's dated unlocks, oldest first, as the page reads them.</summary>
    private List<DatedUnlock> UnlocksFor(string gameId)
    {
        var set = _achievements.Get(gameId);
        if (set is null) return [];
        return set.Items.Where(a => a.Unlocked && a.UnlockedAt is not null)
            .OrderBy(a => a.UnlockedAt)
            .Select(a => new DatedUnlock(a.UnlockedAt!.Value, new { at = a.UnlockedAt, item = AchievementDto(a) }))
            .ToList();
    }

    /// <summary>A hand-logged session's time put into (+1) or taken out of (-1) the game's totals.</summary>
    private static void AddToTotals(Game game, PlaySession s, int sign)
    {
        game.PlaytimeMinutes = Math.Max(0, game.PlaytimeMinutes + sign * s.Seconds / 60.0);
        game.Sessions = Math.Max(0, game.Sessions + sign);
        if (sign > 0 && (game.LastPlayed is null || s.End > game.LastPlayed)) game.LastPlayed = s.End;
    }

    /// <summary>A time from the page: ISO, usually with a Z. Local time out.</summary>
    private static DateTime? LocalTime(JsonNode? n)
    {
        var s = n is JsonValue v && v.TryGetValue<string>(out var str) ? str : null;
        if (s is null || !DateTime.TryParse(s, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind, out var d)) return null;
        return d.Kind == DateTimeKind.Utc ? d.ToLocalTime() : DateTime.SpecifyKind(d, DateTimeKind.Local);
    }

    /// <summary>A logged session's start and length, checked: a minute to a day long, and begun
    /// by now. Says why on the page when they are not.</summary>
    private bool ReadSessionTimes(JsonNode msg, out DateTime start, out int seconds)
    {
        start = LocalTime(msg["start"]) ?? default;
        var secs = msg["seconds"] is JsonValue v && v.TryGetValue<double>(out var d) ? d : 0;
        seconds = (int)Math.Round(secs);
        if (start == default || seconds < 60 || seconds > 24 * 3600) { Push(new { type = "toast", message = "A session is between a minute and a day long" }); return false; }
        if (start > DateTime.Now) { Push(new { type = "toast", message = "That session starts in the future" }); return false; }
        return true;
    }

    // ---- the Playnite import ----

    private PlayniteImport.PnData? _playniteData;

    private void ScanPlaynite(string? dir)
    {
        dir ??= _playniteData?.Dir ?? PlayniteImport.FindDataDir();
        if (dir is null) { Push(new { type = "playnite", state = "missing", record = PlayniteRecordDto() }); return; }
        Push(new { type = "playnite", state = "reading", dir, record = PlayniteRecordDto() });
        _ = Task.Run(() =>
        {
            try
            {
                var data = PlayniteImport.Read(dir);
                _ = _window.Dispatcher.BeginInvoke(() => { _playniteData = data; PushPlaynite("ready", null); });
            }
            catch (Exception ex)
            {
                Log.Info($"Playnite import: reading {dir} failed: {ex.Message}");
                _ = _window.Dispatcher.BeginInvoke(() => Push(new { type = "playnite", state = "error", dir, error = ex.Message, record = PlayniteRecordDto() }));
            }
        });
    }

    private PlayniteImport.Plan MakePlaynitePlan(PlayniteImport.PnData data) =>
        PlayniteImport.MakePlan(data, _library.Games, _library.Collections, _activityStore.All(), _achievementStore, _settings.Settings,
            g => _achievements.ProviderFor(g) is not null && _achievements.Blocked(g) is null, PlayniteImport.LoadRecord());

    /// <summary>The import sheet's whole state: where Playnite is, what each part would bring
    /// (planned afresh, so after an import every count reads what is left), what the last run
    /// did, and whether there is an import to undo.</summary>
    private void PushPlaynite(string state, PlayniteImport.Outcome? outcome)
    {
        var data = _playniteData;
        object? parts = null;
        var matched = 0;
        if (data is not null)
        {
            var plan = MakePlaynitePlan(data);
            matched = plan.Matched;
            string Title(string id) => _library.Find(id)?.Title ?? plan.Games.FirstOrDefault(g => g.Id == id)?.Title ?? id;
            parts = new
            {
                playtime = new { count = plan.Playtime.Count, minutes = Math.Round(plan.Playtime.Sum(c => c.MinutesAfter - c.MinutesBefore)), titles = plan.Playtime.Take(3).Select(c => Title(c.GameId)) },
                flags = new { count = plan.Flags.Count, favorites = plan.Flags.Count(f => f.Favorite), hidden = plan.Flags.Count(f => f.Hidden) },
                collections = new { count = plan.Collections.Count, names = plan.Collections.Select(c => c.Name) },
                games = new { count = plan.Games.Count, titles = plan.Games.Take(3).Select(g => g.Title) },
                sessions = new { count = plan.Sessions.Count, available = data.HasGameActivity, games = plan.Sessions.Select(s => s.Session.GameId).Distinct().Count() },
                achievements = new { count = plan.Achievements.Count, available = data.HasSuccessStory, unlocked = plan.Achievements.Sum(a => a.Items.Count(i => i.Unlocked)) },
                settings = new { count = plan.Settings.Count, labels = plan.Settings.Select(s => s.Label) },
            };
        }
        Push(new
        {
            type = "playnite", state, dir = data?.Dir, version = data?.Version,
            games = data?.Games.Count ?? 0, matched, parts, outcome, record = PlayniteRecordDto(),
        });
    }

    private static object? PlayniteRecordDto()
    {
        var r = PlayniteImport.LoadRecord();
        return r is null || r.IsEmpty ? null : new
        {
            at = r.At, playtime = r.Playtime.Count, flags = r.Flags.Count, collections = r.Collections.Count,
            games = r.Games.Count, sessions = r.Sessions.Count, achievements = r.Achievements.Count, settings = r.Settings.Count,
        };
    }

    private void PushGameActivity(string id) =>
        Push(new { type = "activity", id, sessions = _activityStore.ForGame(id), unlocks = UnlocksFor(id).Select(u => u.Dto) });

    private object AchievementsAllPayload()
    {
        var sets = _achievementStore.All();
        var titles = _library.Games.ToDictionary(g => g.Id, g => g.Title, StringComparer.Ordinal);
        var dated = sets.SelectMany(s => s.Items.Where(a => a.Unlocked && a.UnlockedAt is not null).Select(a => (set: s, a, at: a.UnlockedAt!.Value)))
            .OrderByDescending(x => x.at).ToList();
        // In full for the last few weeks (and never fewer than 60), newest first: the overview's
        // day picker and a day's timeline show each one with its icon and its time.
        var since = DateTime.Today.AddDays(-RecentUnlockDays);
        var recent = dated.Where((x, i) => i < 60 || x.at >= since).Take(RecentUnlockCap)
            .Select(x => new { gameId = x.set.GameId, title = titles.GetValueOrDefault(x.set.GameId, x.set.GameId), item = AchievementDto(x.a), at = x.at })
            .ToList();
        // Every unlock on record as a count per local day, for the playtime charts' markers.
        var unlocksByDay = dated.GroupBy(x => x.at.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture))
            .ToDictionary(g => g.Key, g => g.Count());
        // The rarity of every unlocked achievement, for the breakdown, without shipping the lists.
        var unlockedPercents = sets.SelectMany(s => s.Items.Where(a => a.Unlocked).Select(a => a.Percent)).ToList();
        // Unlocks per month for the last year, oldest first, for the chart.
        var now = DateTime.Now;
        var months = Enumerable.Range(0, 12).Select(i => new DateTime(now.Year, now.Month, 1).AddMonths(i - 11)).ToList();
        var unlocksByMonth = months.Select(m => new
        {
            key = m.ToString("yyyy-MM"),
            label = m.ToString("MMM yy", System.Globalization.CultureInfo.InvariantCulture),
            value = sets.Sum(s => s.Items.Count(a => a.Unlocked && a.UnlockedAt is { } d && d.Year == m.Year && d.Month == m.Month)),
        });
        return new
        {
            type = "achievementsAll",
            games = sets.Select(s => new { gameId = s.GameId, title = titles.GetValueOrDefault(s.GameId, s.GameId), summary = AchievementSummary.Of(s) }),
            recent,
            unlocksByDay,
            unlockedPercents,
            unlocksByMonth,
            // Where each store stands, for the screen to explain an empty list.
            providers = _achievements.Providers.Select(p => new
            {
                source = p.Source,
                blocked = _library.Games.FirstOrDefault(p.Supports) is { } any ? p.Blocked(any) : null,
                games = _library.Games.Count(p.Supports),
            }),
        };
    }

    /// <summary>A press, and the family of the pad it came from: xbox, playstation, switch or generic.</summary>
    public void PushPadEvent(string button, string layout) => Push(new { type = "pad", button, layout });

    /// <summary>The pad in use changed, or was picked up again after a pause. The page redraws its legends from it.</summary>
    public void PushPadLayout(string layout, string name) => Push(new { type = "padLayout", layout, name });

    /// <summary>A real mouse click is on its way from a pad's touchpad, so the page does not read it as the mouse.</summary>
    public void PushPadClick() => Push(new { type = "padClick" });

    /// <summary>The right stick's scroll speed, notches per second, up positive. See GamepadService.UiScroll.</summary>
    public void PushStickScroll(double v) => Push(new { type = "stickScroll", v = Math.Round(v, 2) });

    /// <summary>The built-in keyboard was closed with B while the launcher was in front.</summary>
    public void PushKeyboardDismissed() => Push(new { type = "keyboardDismissed" });

    /// <summary>
    /// The keyboard's own options page changed one of its switches or its size. Only those six
    /// fields go, and the page merges them into its copy rather than taking a whole settings object:
    /// a full copy would overwrite a change the page had made and not yet saved.
    /// </summary>
    public void PushKeyboardOptions()
    {
        var s = _settings.Settings;
        Push(new
        {
            type = "keyboardOptions",
            settings = new
            {
                keyboardSuggestions = s.KeyboardSuggestions,
                keyboardFunctionKeys = s.KeyboardFunctionKeys,
                keyboardNavKeys = s.KeyboardNavKeys,
                keyboardNumpad = s.KeyboardNumpad,
                keyboardModifiers = s.KeyboardModifiers,
                keyboardScale = s.KeyboardScale,
            },
        });
    }

    public void PushPadConnected(bool connected) => Push(new { type = "padConnected", connected });

    private void Push(object payload) =>
        _core.PostWebMessageAsJson(JsonSerializer.Serialize(payload, JsonOpts));
}
