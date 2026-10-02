using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;

namespace Loungepad.Services;

/// <summary>
/// A Steam sign-in, for the library of a profile whose game details are private.
///
/// GetOwnedGames answers anyone for a public profile and only the owner for a private one. The
/// owner is whoever holds the account's own token: Steam's web store hands a signed-in session one
/// (<c>webapi_token</c> from /pointssummary/ajaxgetasyncconfig, the token Augmented Steam and
/// Steam's own pages call the Web API with), and the API takes it as <c>access_token=</c>. So the
/// user signs in on Steam's own login page in the usual window -- password, Steam Guard, the QR code,
/// all Steam's -- and nothing about the profile has to be made public.
///
/// The token is a JWT that lasts about a day. It is kept (DPAPI, like every store's) with its expiry
/// and the SteamID it names, and fetched again shortly before it runs out by loading the store once
/// in the same window run hidden: the sign-in's browser profile is kept, and Steam's own page renews
/// its session from the long-lived login cookie the way it does in any browser. A hidden run that
/// lands on the login page means the session itself has gone, and the row says to sign in again.
/// </summary>
public class SteamWebSession
{
    public const string Store = "steam";
    private const string SignInUrl = "https://store.steampowered.com/login/?redir=account%2F";
    private const string RenewUrl = "https://store.steampowered.com/account/";
    private const string TokenUrl = "https://store.steampowered.com/pointssummary/ajaxgetasyncconfig";

    public class Session
    {
        public string SteamId { get; set; } = "";
        public string? Token { get; set; }
        public DateTime? Expires { get; set; }
    }

    private Session? _session;
    private readonly SemaphoreSlim _renew = new(1, 1);

    public StoreStatus Status { get; } = new();

    public SteamWebSession()
    {
        _session = AccountStore.LoadSecret<Session>(Store);
        Status.SignedIn = _session is not null;
    }

    public bool SignedIn => _session is not null;
    public string? SteamId => _session?.SteamId;

    /// <summary>The account's token and SteamID for the Web API, renewed first when it is missing
    /// or within ten minutes of running out. Null when not signed in, or when the renewal found the
    /// session gone (Status.Error then says so).</summary>
    public async Task<(string SteamId, string Token)?> TokenAsync(CancellationToken ct)
    {
        var s = _session;
        if (s is null) return null;
        if (s.Token is { Length: > 0 } t && s.Expires is { } exp && DateTime.UtcNow < exp.AddMinutes(-10))
            return (s.SteamId, t);

        await _renew.WaitAsync(ct);
        try
        {
            s = _session;
            if (s is null) return null;
            if (s.Token is { Length: > 0 } t2 && s.Expires is { } exp2 && DateTime.UtcNow < exp2.AddMinutes(-10))
                return (s.SteamId, t2);
            var (renewed, signedOut) = await OnUiThread(() => RunAsync(null, visible: false));
            if (renewed is null)
            {
                Status.Error = signedOut ? "The Steam sign-in has expired. Sign in again" : "Could not renew the Steam sign-in";
                Log.Info($"Steam sign-in: renewal {(signedOut ? "found the session signed out" : "did not answer")}");
                return null;
            }
            _session = renewed;
            AccountStore.SaveSecret(Store, renewed);
            Status.Error = null;
            return (renewed.SteamId, renewed.Token!);
        }
        finally { _renew.Release(); }
    }

    public async Task<bool> SignInAsync(Window owner)
    {
        var (session, _) = await RunAsync(owner, visible: true);
        if (session is null) return false;
        _session = session;
        AccountStore.SaveSecret(Store, session);
        Status.SignedIn = true;
        Status.Error = null;
        Log.Info($"Steam: signed in as {session.SteamId}");
        return true;
    }

    public void SignOut()
    {
        _session = null;
        AccountStore.DeleteSecret(Store);
        AccountStore.ClearProfile(Store);
        Status.SignedIn = false;
        Status.User = null;
        Status.Error = null;
    }

    /// <summary>
    /// The window, visible for a sign-in or hidden for a renewal. Both end the same way: on a store
    /// page with the login cookie set, the page itself is asked for the token (a fetch from inside
    /// it, so it carries the session exactly as Steam's own scripts do). A hidden run that arrives
    /// on the login page stops at once: nobody is there to sign in.
    /// </summary>
    private static async Task<(Session? Session, bool SignedOut)> RunAsync(Window? owner, bool visible)
    {
        Session? captured = null;
        var signedOut = false;
        var ok = await StoreLoginWindow.RunAsync(owner, "Sign in to Steam", AccountStore.ProfileDir(Store),
            visible ? SignInUrl : RenewUrl,
            async core =>
            {
                var src = core.Source ?? "";
                if (!src.StartsWith("https://store.steampowered.com/", StringComparison.OrdinalIgnoreCase)) return false;
                if (src.Contains("/login", StringComparison.OrdinalIgnoreCase))
                {
                    if (visible) return false;
                    signedOut = true;
                    return true;
                }
                var cookies = await core.CookieManager.GetCookiesAsync("https://store.steampowered.com");
                if (!cookies.Any(c => c.Name == "steamLoginSecure")) return false;
                captured = await TokenFromPageAsync(core);
                return captured is not null;
            }, visible);
        return (ok ? captured : null, signedOut);
    }

    private static async Task<Session?> TokenFromPageAsync(CoreWebView2 core)
    {
        try
        {
            var args = JsonSerializer.Serialize(new
            {
                expression = $"fetch('{TokenUrl}', {{ credentials: 'include' }}).then(r => r.text())",
                awaitPromise = true,
                returnByValue = true,
            });
            var raw = await core.CallDevToolsProtocolMethodAsync("Runtime.evaluate", args);
            using var outer = JsonDocument.Parse(raw);
            if (!outer.RootElement.TryGetProperty("result", out var result)
                || !result.TryGetProperty("value", out var value) || value.ValueKind != JsonValueKind.String) return null;
            using var doc = JsonDocument.Parse(value.GetString()!);
            // Signed out, Steam answers {"success":1,"data":[]}: an array where the object would be.
            if (!doc.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object
                || !data.TryGetProperty("webapi_token", out var tok) || tok.ValueKind != JsonValueKind.String) return null;
            var token = tok.GetString()!;
            var (steamId, expires) = ReadJwt(token);
            if (steamId is null) return null;
            return new Session { SteamId = steamId, Token = token, Expires = expires ?? DateTime.UtcNow.AddHours(12) };
        }
        catch (Exception ex)
        {
            Log.Info($"Steam sign-in: token not read ({ex.Message})");
            return null;
        }
    }

    /// <summary>The SteamID ("sub") and expiry ("exp") a token names. Read, not verified: the token
    /// is Steam's to check, and a bad one is answered with a 401 like any other.</summary>
    public static (string? SteamId, DateTime? Expires) ReadJwt(string jwt)
    {
        try
        {
            var parts = jwt.Split('.');
            if (parts.Length < 2) return (null, null);
            var b64 = parts[1].Replace('-', '+').Replace('_', '/');
            b64 = b64.PadRight(b64.Length + (4 - b64.Length % 4) % 4, '=');
            using var doc = JsonDocument.Parse(Encoding.UTF8.GetString(Convert.FromBase64String(b64)));
            var root = doc.RootElement;
            var sub = root.TryGetProperty("sub", out var s) && s.ValueKind == JsonValueKind.String ? s.GetString() : null;
            DateTime? exp = JsonNum.Long(root, "exp") is { } e && e > 0 ? DateTimeOffset.FromUnixTimeSeconds(e).UtcDateTime : null;
            return (sub is { Length: 17 } && sub.StartsWith("7656") ? sub : null, exp);
        }
        catch { return (null, null); }
    }

    /// <summary>The window is WPF's, so it is made on the UI thread, whichever thread asked: a
    /// renewal is usually wanted mid-scan, off it.</summary>
    private static Task<T> OnUiThread<T>(Func<Task<T>> work)
    {
        var d = Application.Current?.Dispatcher;
        if (d is null || d.CheckAccess()) return work();
        return d.InvokeAsync(work, DispatcherPriority.Normal).Task.Unwrap();
    }
}
