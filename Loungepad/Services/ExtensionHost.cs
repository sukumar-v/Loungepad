using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Windows.Threading;
using Loungepad.Models;
using Microsoft.Web.WebView2.Core;

namespace Loungepad.Services;

/// <summary>An extension's hook or API call failed on the extension's side.</summary>
public sealed class ExtensionException : Exception
{
    public ExtensionException(string message) : base(message) { }
}

/// <summary>Where a running extension stands, for the add-on's page.</summary>
public sealed class ExtensionStatus
{
    /// <summary>stopped | starting | running | error | disabled</summary>
    public string State { get; set; } = "stopped";
    public string? Error { get; set; }
    public DateTime? StartedAt { get; set; }
    public string[] Hooks { get; set; } = Array.Empty<string>();
    public int Calls { get; set; }
    public int Failures { get; set; }
}

/// <summary>
/// The launcher's side of an extension's API (docs/ADDONS.md): what the runtime page may ask
/// for, each call checked here. One per running extension, holding its allow-list, its storage
/// file and its own HttpClient with no cookies and no automatic redirects.
/// </summary>
public sealed class ExtensionApi
{
    private const long MaxResponseBytes = 5L * 1024 * 1024;
    private const int MaxBodyChars = 1024 * 1024;
    private const int MaxStorageBytes = 256 * 1024;
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan MinGapPerHost = TimeSpan.FromMilliseconds(250);
    private static readonly HashSet<string> Methods = new(StringComparer.OrdinalIgnoreCase) { "GET", "POST", "PUT", "PATCH", "DELETE", "HEAD" };
    private static readonly HashSet<string> HeaderAllow = new(StringComparer.OrdinalIgnoreCase)
    {
        "accept", "accept-language", "content-type", "referer", "origin", "user-agent", "authorization", "cache-control", "pragma",
    };
    private static readonly JsonSerializerOptions Camel = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private readonly InstalledAddon _addon;
    private readonly string _storageFile;
    private readonly Func<AppSettings> _settings;
    private readonly Func<IReadOnlyList<Game>> _games;
    private readonly HttpClient _http;
    private readonly string _launcher;
    private readonly object _gate = new();
    private Dictionary<string, JsonElement>? _storage;
    private readonly Dictionary<string, DateTime> _lastByHost = new(StringComparer.OrdinalIgnoreCase);
    private readonly Queue<DateTime> _logTimes = new();

    public ExtensionApi(InstalledAddon addon, string storageFile, Func<AppSettings> settings, Func<IReadOnlyList<Game>> games, string launcher)
    {
        _addon = addon;
        _storageFile = storageFile;
        _settings = settings;
        _games = games;
        _launcher = launcher;
        var handler = new HttpClientHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false,
            AutomaticDecompression = System.Net.DecompressionMethods.All,
        };
        _http = new HttpClient(handler) { Timeout = RequestTimeout };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd($"Loungepad/{launcher} (+https://loungepad.app) {addon.Id}/{addon.Manifest.Version}");
    }

    public string Id => _addon.Id;

    /// <summary>The extension's option values, as loungepad.settings sees them.</summary>
    public Dictionary<string, JsonElement> SettingsValues()
    {
        var all = _settings().ExtensionSettings;
        return all is not null && all.TryGetValue(_addon.Id, out var v) && v is not null ? v : new();
    }

    /// <summary>What an extension is told about a game: enough to look it up, nothing that
    /// identifies the person. No paths, no account ids.</summary>
    public static object Project(Game g)
    {
        var colon = g.Id.IndexOf(':');
        var store = colon > 0 ? g.Id[..colon] : "manual";
        int? appId = store == "steam" && int.TryParse(g.Id[(colon + 1)..], out var n) ? n : null;
        var year = System.Text.RegularExpressions.Regex.Match(g.ReleaseDate ?? "", @"\b(19|20)\d{2}\b");
        return new
        {
            id = g.Id, title = g.Title, platform = g.Platform, platformId = g.PlatformId,
            store = g.Emulated ? "rom" : store, steamAppId = appId,
            releaseDate = g.ReleaseDate, year = year.Success ? int.Parse(year.Value) : (int?)null,
            developer = g.Developer, publisher = g.Publisher, genres = g.Genres,
            installed = g.Installed, playtimeMinutes = g.PlaytimeMinutes, lastPlayed = g.LastPlayed,
        };
    }

    public async Task<object?> HandleAsync(string fn, JsonElement args, CancellationToken ct)
    {
        JsonElement Arg(int i) => args.ValueKind == JsonValueKind.Array && args.GetArrayLength() > i ? args[i] : default;
        switch (fn)
        {
            case "fetch": return await FetchAsync(Arg(0), Arg(1), ct);
            case "storage.get":
            {
                var key = StorageKey(Arg(0));
                lock (_gate) return Storage().TryGetValue(key, out var v) ? v : null;
            }
            case "storage.set":
            {
                var key = StorageKey(Arg(0));
                var value = Arg(1);
                lock (_gate)
                {
                    var s = Storage();
                    if (value.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null) s.Remove(key); else s[key] = value.Clone();
                    SaveStorage(s);
                }
                return null;
            }
            case "storage.remove":
            {
                var key = StorageKey(Arg(0));
                lock (_gate) { var s = Storage(); s.Remove(key); SaveStorage(s); }
                return null;
            }
            case "storage.keys":
                lock (_gate) return Storage().Keys.ToList();
            case "games.list":
                return _games().Select(Project).ToList();
            case "settings.get":
                return SettingsValues();
            default:
                throw new ExtensionException($"Unknown API call {fn}");
        }
    }

    /// <summary>A log line from the extension, rate-limited so a loop cannot fill the log.</summary>
    public void LogLine(string? msg)
    {
        var now = DateTime.UtcNow;
        lock (_gate)
        {
            while (_logTimes.Count > 0 && now - _logTimes.Peek() > TimeSpan.FromSeconds(10)) _logTimes.Dequeue();
            if (_logTimes.Count >= 30) return;
            _logTimes.Enqueue(now);
        }
        var text = (msg ?? "").Replace('\r', ' ').Replace('\n', ' ');
        if (text.Length > 500) text = text[..500] + "…";
        Log.Info($"[ext:{_addon.Id}] {text}");
    }

    // ---- fetch ----

    private bool HostAllowed(string host)
    {
        foreach (var a in _addon.Manifest.Hosts)
        {
            if (a.StartsWith("*."))
            {
                var bare = a[2..];
                if (host == bare || host.EndsWith("." + bare, StringComparison.Ordinal)) return true;
            }
            else if (host == a) return true;
        }
        return false;
    }

    private async Task<object> FetchAsync(JsonElement urlArg, JsonElement init, CancellationToken ct)
    {
        var url = urlArg.ValueKind == JsonValueKind.String ? urlArg.GetString() : null;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != "https")
            throw new ExtensionException($"fetch: only https URLs are allowed ({url})");
        if (!HostAllowed(uri.Host.ToLowerInvariant()))
            throw new ExtensionException($"fetch: {uri.Host} is not in the extension's permissions.hosts");

        var method = "GET";
        string? body = null;
        var headers = new List<(string Name, string Value)>();
        if (init.ValueKind == JsonValueKind.Object)
        {
            if (init.TryGetProperty("method", out var m) && m.ValueKind == JsonValueKind.String)
            {
                method = m.GetString()!.ToUpperInvariant();
                if (!Methods.Contains(method)) throw new ExtensionException($"fetch: method {method} is not allowed");
            }
            if (init.TryGetProperty("body", out var b) && b.ValueKind == JsonValueKind.String)
            {
                body = b.GetString();
                if (body!.Length > MaxBodyChars) throw new ExtensionException("fetch: the body is too large");
            }
            if (init.TryGetProperty("headers", out var h) && h.ValueKind == JsonValueKind.Object)
            {
                foreach (var p in h.EnumerateObject())
                {
                    if (headers.Count >= 20) break;
                    var name = p.Name.Trim();
                    if (p.Value.ValueKind != JsonValueKind.String) continue;
                    if (!HeaderAllow.Contains(name) && !name.StartsWith("x-", StringComparison.OrdinalIgnoreCase)) continue;
                    var value = p.Value.GetString()!;
                    if (value.Length > 2048 || value.Any(c => c is '\r' or '\n')) continue;
                    headers.Add((name, value));
                }
            }
        }

        for (var hop = 0; hop < 6; hop++)
        {
            await PaceAsync(uri.Host, ct);
            using var request = new HttpRequestMessage(new HttpMethod(method), uri);
            if (body is not null && method is "POST" or "PUT" or "PATCH")
                request.Content = new StringContent(body, Encoding.UTF8);
            foreach (var (name, value) in headers)
            {
                if (name.Equals("content-type", StringComparison.OrdinalIgnoreCase))
                {
                    if (request.Content is not null)
                    {
                        request.Content.Headers.Remove("Content-Type");
                        request.Content.Headers.TryAddWithoutValidation("Content-Type", value);
                    }
                }
                else request.Headers.TryAddWithoutValidation(name, value);
            }
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(RequestTimeout);
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token);
            var status = (int)response.StatusCode;
            if (status is 301 or 302 or 303 or 307 or 308 && response.Headers.Location is { } loc)
            {
                var next = loc.IsAbsoluteUri ? loc : new Uri(uri, loc);
                if (next.Scheme != "https" || !HostAllowed(next.Host.ToLowerInvariant()))
                    throw new ExtensionException($"fetch: redirected to {next.Host}, which is not in permissions.hosts");
                if (status == 303 || (status is 301 or 302 && method == "POST")) { method = "GET"; body = null; }
                uri = next;
                continue;
            }
            if (response.Content.Headers.ContentLength is { } len && len > MaxResponseBytes)
                throw new ExtensionException("fetch: the response is too large");
            await using var stream = await response.Content.ReadAsStreamAsync(cts.Token);
            using var ms = new MemoryStream();
            var buffer = new byte[81920];
            int read;
            while ((read = await stream.ReadAsync(buffer, cts.Token)) > 0)
            {
                ms.Write(buffer, 0, read);
                if (ms.Length > MaxResponseBytes) throw new ExtensionException("fetch: the response is too large");
            }
            var charset = response.Content.Headers.ContentType?.CharSet;
            Encoding enc = Encoding.UTF8;
            if (!string.IsNullOrEmpty(charset)) { try { enc = Encoding.GetEncoding(charset.Trim('"')); } catch { /* utf-8 */ } }
            var text = enc.GetString(ms.ToArray());
            var outHeaders = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var hd in response.Headers.Concat(response.Content.Headers))
                if (!outHeaders.ContainsKey(hd.Key)) outHeaders[hd.Key.ToLowerInvariant()] = string.Join(", ", hd.Value);
            return new { status, ok = status is >= 200 and < 300, url = uri.ToString(), headers = outHeaders, body = text };
        }
        throw new ExtensionException("fetch: too many redirects");
    }

    private async Task PaceAsync(string host, CancellationToken ct)
    {
        TimeSpan wait;
        lock (_gate)
        {
            var now = DateTime.UtcNow;
            var last = _lastByHost.TryGetValue(host, out var t) ? t : DateTime.MinValue;
            var at = last + MinGapPerHost > now ? last + MinGapPerHost : now;
            wait = at - now;
            _lastByHost[host] = at;
        }
        if (wait > TimeSpan.Zero) await Task.Delay(wait, ct);
    }

    // ---- storage ----

    private static string StorageKey(JsonElement e)
    {
        var key = e.ValueKind == JsonValueKind.String ? e.GetString() : null;
        if (string.IsNullOrEmpty(key) || key.Length > 128) throw new ExtensionException("storage: the key must be a short string");
        return key;
    }

    private Dictionary<string, JsonElement> Storage()
    {
        if (_storage is not null) return _storage;
        try
        {
            _storage = File.Exists(_storageFile)
                ? JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(File.ReadAllText(_storageFile)) ?? new()
                : new();
        }
        catch (Exception ex)
        {
            Log.Info($"[ext:{_addon.Id}] storage could not be read, starting empty: {ex.Message}");
            _storage = new();
        }
        return _storage;
    }

    private void SaveStorage(Dictionary<string, JsonElement> s)
    {
        var json = JsonSerializer.Serialize(s);
        if (json.Length > MaxStorageBytes) throw new ExtensionException("storage: the store is full (256 KB)");
        Directory.CreateDirectory(Path.GetDirectoryName(_storageFile)!);
        File.WriteAllText(_storageFile, json);
    }
}

/// <summary>
/// One running extension: a hidden WebView2 controller of its own, on its own origin
/// (https://&lt;id&gt;.loungepad.ext), served from the extension's folder plus the runtime page
/// shipped in the exe. Messages go through chrome.webview.postMessage on that controller alone,
/// so the host knows who is speaking without a broker; the page has no bridge and nothing to
/// reach but the API in <see cref="ExtensionApi"/>.
///
/// Every WebView2 call is on the UI thread; <see cref="CallAsync"/> is the one thing the pass
/// uses from elsewhere, and it marshals through the dispatcher.
/// </summary>
public sealed class ExtensionHost : IDisposable
{
    public const int ApiVersion = 1;
    private static readonly TimeSpan StartTimeout = TimeSpan.FromSeconds(25);
    private static readonly JsonSerializerOptions Camel = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private readonly InstalledAddon _addon;
    private readonly CoreWebView2Environment _env;
    private readonly IntPtr _parent;
    private readonly Dispatcher _dispatcher;
    private readonly ExtensionApi _api;
    private readonly string _launcher;
    private CoreWebView2Controller? _controller;
    private CoreWebView2? _core;
    private readonly Dictionary<int, TaskCompletionSource<JsonElement>> _pending = new();
    private int _nextId;
    private TaskCompletionSource<bool>? _ready;
    private bool _disposed;

    public ExtensionStatus Status { get; } = new();
    public InstalledAddon Addon => _addon;
    public string Host => $"{_addon.Id}.loungepad.ext";
    public string Origin => $"https://{Host}";
    public event Action? StatusChanged;

    public ExtensionHost(InstalledAddon addon, CoreWebView2Environment env, IntPtr parent, Dispatcher dispatcher, ExtensionApi api, string launcher)
    {
        _addon = addon;
        _env = env;
        _parent = parent;
        _dispatcher = dispatcher;
        _api = api;
        _launcher = launcher;
    }

    public bool Running => Status.State == "running" && !_disposed;
    public bool HasHook(string name) => Status.Hooks.Contains(name);

    /// <summary>Create the controller, serve the folder and wait for the module to load. UI thread.</summary>
    public async Task StartAsync()
    {
        Status.State = "starting";
        Status.Error = null;
        StatusChanged?.Invoke();
        try
        {
            _controller = await _env.CreateCoreWebView2ControllerAsync(_parent);
            if (_disposed) { _controller.Close(); return; }
            _controller.IsVisible = false;
            _controller.Bounds = new System.Drawing.Rectangle(0, 0, 4, 4);
            _core = _controller.CoreWebView2;
            var s = _core.Settings;
            s.AreDefaultContextMenusEnabled = false;
            s.IsStatusBarEnabled = false;
            s.AreBrowserAcceleratorKeysEnabled = false;
            s.IsGeneralAutofillEnabled = false;
            s.IsPasswordAutosaveEnabled = false;
            s.IsZoomControlEnabled = false;
            // On, so the add-on's page can open the extension's console for whoever is writing it.
            s.AreDevToolsEnabled = true;

            _core.AddWebResourceRequestedFilter($"{Origin}/*", CoreWebView2WebResourceContext.All, CoreWebView2WebResourceRequestSourceKinds.All);
            _core.WebResourceRequested += Serve;
            // The page may never leave its origin, and may never open a window.
            _core.NavigationStarting += (_, e) =>
            {
                if (!e.Uri.StartsWith(Origin + "/", StringComparison.OrdinalIgnoreCase)) e.Cancel = true;
            };
            _core.NewWindowRequested += (_, e) => e.Handled = true;
            _core.WebMessageReceived += OnMessage;
            _core.ProcessFailed += (_, e) => Fail($"The extension's process failed ({e.Reason})");

            _ready = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            _core.Navigate($"{Origin}/");
            var done = await Task.WhenAny(_ready.Task, Task.Delay(StartTimeout));
            if (done != _ready.Task) throw new ExtensionException($"did not start within {StartTimeout.TotalSeconds:0} s");
            if (!await _ready.Task) throw new ExtensionException(Status.Error ?? "failed to load");
        }
        catch (Exception ex)
        {
            Fail(ex.Message);
            throw;
        }
    }

    private void Fail(string error)
    {
        Status.State = "error";
        Status.Error = error;
        Log.Info($"[ext:{_addon.Id}] {error}");
        _ready?.TrySetResult(false);
        FailPending(new ExtensionException(error));
        StatusChanged?.Invoke();
    }

    private void FailPending(Exception ex)
    {
        List<TaskCompletionSource<JsonElement>> all;
        lock (_pending) { all = _pending.Values.ToList(); _pending.Clear(); }
        foreach (var tcs in all) tcs.TrySetException(ex);
    }

    // ---- serving the origin ----

    private static readonly Dictionary<string, string> ContentTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        [".js"] = "text/javascript; charset=utf-8", [".mjs"] = "text/javascript; charset=utf-8", [".json"] = "application/json; charset=utf-8",
        [".txt"] = "text/plain; charset=utf-8", [".wasm"] = "application/wasm",
    };

    /// <summary>What the extension's page may do: run its own module and nothing else. No network
    /// (every request goes through the API), no images, no styles, no frames.</summary>
    private const string PageCsp = "default-src 'none'; script-src 'self'; connect-src 'none'; img-src 'none'; style-src 'none'; "
                                 + "frame-src 'none'; object-src 'none'; base-uri 'none'; form-action 'none'";

    private byte[] HostPage()
    {
        var main = "/" + string.Join("/", (_addon.Manifest.Main ?? "main.js").Split('/').Select(Uri.EscapeDataString));
        var html = "<!doctype html><html><head><meta charset=\"utf-8\">"
                 + $"<meta http-equiv=\"Content-Security-Policy\" content=\"{PageCsp}\">"
                 + $"<meta name=\"loungepad-main\" content=\"{main}\"><title>{_addon.Id}</title></head>"
                 + "<body><script type=\"module\" src=\"/_loungepad/runtime.js\"></script></body></html>";
        return Encoding.UTF8.GetBytes(html);
    }

    private void Serve(object? sender, CoreWebView2WebResourceRequestedEventArgs e)
    {
        if (_core is null || !Uri.TryCreate(e.Request.Uri, UriKind.Absolute, out var uri)
            || !uri.Host.Equals(Host, StringComparison.OrdinalIgnoreCase)) return;
        try
        {
            var rel = Uri.UnescapeDataString(uri.AbsolutePath).TrimStart('/');
            byte[]? body = null;
            string type;
            if (rel is "" or "index.html") { body = HostPage(); type = "text/html; charset=utf-8"; }
            else if (rel == "_loungepad/runtime.js") { body = ShippedFiles.ReadAllBytes("ui/ext/runtime.js"); type = "text/javascript; charset=utf-8"; }
            else
            {
                type = ContentTypes.TryGetValue(Path.GetExtension(rel), out var t) ? t : "application/octet-stream";
                if (AddonService.SafeRelativePath(rel))
                {
                    var root = Path.GetFullPath(_addon.Folder) + Path.DirectorySeparatorChar;
                    var full = Path.GetFullPath(Path.Combine(_addon.Folder, rel.Replace('/', Path.DirectorySeparatorChar)));
                    if (full.StartsWith(root, StringComparison.OrdinalIgnoreCase) && File.Exists(full)) body = File.ReadAllBytes(full);
                }
            }
            if (body is null)
            {
                e.Response = _core.Environment.CreateWebResourceResponse(null, 404, "Not Found", "");
                return;
            }
            e.Response = _core.Environment.CreateWebResourceResponse(new MemoryStream(body), 200, "OK",
                $"Content-Type: {type}\r\nContent-Length: {body.Length}\r\nCache-Control: no-store\r\nX-Content-Type-Options: nosniff\r\nContent-Security-Policy: {PageCsp}");
        }
        catch (Exception ex)
        {
            Log.Info($"[ext:{_addon.Id}] serving {e.Request.Uri} failed: {ex.Message}");
            e.Response = _core.Environment.CreateWebResourceResponse(null, 500, "Error", "");
        }
    }

    // ---- the protocol ----

    private void Post(object msg)
    {
        if (_core is null || _disposed) return;
        try { _core.PostWebMessageAsJson(JsonSerializer.Serialize(msg, Camel)); }
        catch (Exception ex) { Log.Info($"[ext:{_addon.Id}] post failed: {ex.Message}"); }
    }

    private void OnMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        if (!e.Source.StartsWith(Origin + "/", StringComparison.OrdinalIgnoreCase)) return;
        JsonDocument doc;
        try { doc = JsonDocument.Parse(e.WebMessageAsJson); }
        catch { return; }
        using (doc)
        {
            var m = doc.RootElement;
            if (m.ValueKind != JsonValueKind.Object || !m.TryGetProperty("t", out var tEl) || tEl.ValueKind != JsonValueKind.String) return;
            switch (tEl.GetString())
            {
                case "hello":
                    Post(new
                    {
                        t = "init", id = _addon.Id, version = _addon.Manifest.Version, name = _addon.Manifest.Name,
                        settings = _api.SettingsValues(), launcher = _launcher, api = ApiVersion,
                    });
                    break;
                case "ready":
                    Status.Hooks = m.TryGetProperty("hooks", out var hooks) && hooks.ValueKind == JsonValueKind.Array
                        ? hooks.EnumerateArray().Where(h => h.ValueKind == JsonValueKind.String).Select(h => h.GetString()!).ToArray()
                        : Array.Empty<string>();
                    Status.State = "running";
                    Status.Error = null;
                    Status.StartedAt = DateTime.UtcNow;
                    Log.Info($"[ext:{_addon.Id}] running, version {_addon.Manifest.Version}, hooks: {string.Join(", ", Status.Hooks)}");
                    _ready?.TrySetResult(true);
                    StatusChanged?.Invoke();
                    break;
                case "error":
                    Fail(m.TryGetProperty("error", out var err) && err.ValueKind == JsonValueKind.String ? err.GetString()! : "failed to load");
                    break;
                case "log":
                    _api.LogLine(m.TryGetProperty("msg", out var msg) && msg.ValueKind == JsonValueKind.String ? msg.GetString() : null);
                    break;
                case "reply":
                {
                    if (!m.TryGetProperty("id", out var idEl) || !idEl.TryGetInt32(out var id)) return;
                    TaskCompletionSource<JsonElement>? tcs;
                    lock (_pending) { _pending.Remove(id, out tcs); }
                    if (tcs is null) return;
                    var ok = m.TryGetProperty("ok", out var okEl) && okEl.ValueKind == JsonValueKind.True;
                    if (ok) tcs.TrySetResult(m.TryGetProperty("result", out var r) ? r.Clone() : default);
                    else tcs.TrySetException(new ExtensionException(m.TryGetProperty("error", out var er) && er.ValueKind == JsonValueKind.String ? er.GetString()! : "failed"));
                    break;
                }
                case "call":
                {
                    if (!m.TryGetProperty("id", out var idEl) || !idEl.TryGetInt32(out var id)) return;
                    var fn = m.TryGetProperty("fn", out var fnEl) && fnEl.ValueKind == JsonValueKind.String ? fnEl.GetString()! : "";
                    var args = m.TryGetProperty("args", out var a) ? a.Clone() : default;
                    _ = AnswerAsync(id, fn, args);
                    break;
                }
            }
        }
    }

    /// <summary>An API call from the extension, answered off the UI thread and posted back on it.</summary>
    private async Task AnswerAsync(int id, string fn, JsonElement args)
    {
        object reply;
        try
        {
            var result = await Task.Run(() => _api.HandleAsync(fn, args, CancellationToken.None));
            reply = new { t = "reply", id, ok = true, result };
        }
        catch (Exception ex)
        {
            var inner = ex is AggregateException ag ? ag.InnerException ?? ex : ex;
            reply = new { t = "reply", id, ok = false, error = inner.Message };
        }
        if (_disposed) return;
        await _dispatcher.InvokeAsync(() => Post(reply));
    }

    /// <summary>Call one of the module's exported hooks and wait for its answer. Any thread.</summary>
    public async Task<JsonElement> CallAsync(string fn, object?[] args, TimeSpan timeout, CancellationToken ct = default)
    {
        if (!Running) throw new ExtensionException("the extension is not running");
        var id = Interlocked.Increment(ref _nextId);
        var tcs = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_pending) _pending[id] = tcs;
        Status.Calls++;
        try
        {
            await _dispatcher.InvokeAsync(() => Post(new { t = "call", id, fn, args }));
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(timeout);
            await using var reg = cts.Token.Register(() => tcs.TrySetException(
                ct.IsCancellationRequested ? new OperationCanceledException() : new ExtensionException($"{fn} took longer than {timeout.TotalSeconds:0} s")));
            return await tcs.Task;
        }
        catch (ExtensionException) { Status.Failures++; throw; }
        finally { lock (_pending) _pending.Remove(id); }
    }

    /// <summary>Tell the extension something changed: its settings, for now.</summary>
    public void SendEvent(string name, object? data) =>
        _dispatcher.BeginInvoke(() => Post(new { t = "event", name, data }));

    public void OpenDevTools() => _core?.OpenDevToolsWindow();

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Status.State = "stopped";
        FailPending(new ExtensionException("the extension was stopped"));
        try { _controller?.Close(); } catch { /* already gone */ }
        _controller = null;
        _core = null;
    }
}

/// <summary>
/// Every running extension, and the metadata pass that drives them (docs/ADDONS.md). Hosts are
/// started for the enabled extensions once the WebView environment exists (<see cref="Attach"/>)
/// and kept in step with the installed set on every change; the pass runs after the launcher's
/// own metadata pass and on demand, one extension at a time, one game at a time.
/// </summary>
public sealed class ExtensionRuntime : IDisposable
{
    private const int MaxDataBytes = 16 * 1024;
    private const int MaxPerPass = 500;
    private static readonly TimeSpan HookTimeout = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan CheckpointEvery = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan RestartBackoff = TimeSpan.FromSeconds(60);

    private readonly AddonService _addons;
    private readonly LibraryStore _library;
    private readonly Func<AppSettings> _settings;
    private readonly Dispatcher _dispatcher;
    private readonly string _dataDir;
    private CoreWebView2Environment? _env;
    private IntPtr _parent;
    private readonly Dictionary<string, ExtensionHost> _hosts = new();
    private readonly Dictionary<string, DateTime> _lastStart = new();
    private readonly SemaphoreSlim _passGate = new(1, 1);
    private readonly SemaphoreSlim _syncGate = new(1, 1);
    private CancellationTokenSource? _passCts;
    private bool _disposed;

    /// <summary>An extension's status changed: the page's list is stale.</summary>
    public event Action? Changed;
    /// <summary>The pass saved what it had so far: the page wants the games again.</summary>
    public event Action? Checkpoint;

    public ExtensionRuntime(AddonService addons, LibraryStore library, Func<AppSettings> settings, Dispatcher dispatcher, string dataDir)
    {
        _addons = addons;
        _library = library;
        _settings = settings;
        _dispatcher = dispatcher;
        _dataDir = dataDir;
    }

    public bool Attached => _env is not null;
    public bool PassRunning => _passGate.CurrentCount == 0;

    /// <summary>The WebView environment exists: start what is enabled. UI thread.</summary>
    public void Attach(CoreWebView2Environment env, IntPtr parent)
    {
        _env = env;
        _parent = parent;
        _ = SyncAsync();
    }

    private Task? _syncInFlight;

    /// <summary>Start enabled extensions that are not running, stop ones that are gone or disabled,
    /// restart one whose folder changed under it. UI thread.</summary>
    public Task SyncAsync() => _syncInFlight = SyncCoreAsync();

    private async Task SyncCoreAsync()
    {
        if (_env is null || _disposed) return;
        if (!await _syncGate.WaitAsync(0)) return;
        try
        {
            var wanted = _addons.Installed()
                .Where(a => a.Kind == AddonKind.Extension && a.Error is null && a.Enabled)
                .ToDictionary(a => a.Key);
            foreach (var key in _hosts.Keys.ToList())
            {
                var host = _hosts[key];
                var stale = !wanted.TryGetValue(key, out var now)
                            || now.Manifest.Version != host.Addon.Manifest.Version
                            || now.Record?.InstalledAt != host.Addon.Record?.InstalledAt;
                if (!stale) continue;
                host.Dispose();
                _hosts.Remove(key);
                Log.Info($"[ext:{host.Addon.Id}] stopped");
            }
            var starts = new List<Task>();
            foreach (var (key, a) in wanted)
            {
                if (_hosts.TryGetValue(key, out var existing))
                {
                    if (existing.Status.State != "error") continue;
                    if (_lastStart.TryGetValue(key, out var at) && DateTime.UtcNow - at < RestartBackoff) continue;
                    existing.Dispose();
                    _hosts.Remove(key);
                }
                var api = new ExtensionApi(a, Path.Combine(_dataDir, a.Id + ".json"), _settings, () => _library.Games.ToList(),
                    UpdateService.Format(UpdateService.Current));
                var host = new ExtensionHost(a, _env, _parent, _dispatcher, api, UpdateService.Format(UpdateService.Current));
                host.StatusChanged += () => Changed?.Invoke();
                _hosts[key] = host;
                _lastStart[key] = DateTime.UtcNow;
                starts.Add(StartQuietly(host));
            }
            await Task.WhenAll(starts);
        }
        finally
        {
            _syncGate.Release();
            Changed?.Invoke();
        }
    }

    private static async Task StartQuietly(ExtensionHost host)
    {
        try { await host.StartAsync(); }
        catch (Exception ex) { Log.Info($"[ext:{host.Addon.Id}] could not start: {ex.Message}"); }
    }

    public object? StatusOf(InstalledAddon a)
    {
        if (!a.Enabled) return new { state = "disabled" };
        if (!_hosts.TryGetValue(a.Key, out var h)) return new { state = a.Error is null ? (_env is null ? "starting" : "stopped") : "error", error = a.Error };
        var s = h.Status;
        return new { state = s.State, error = s.Error, startedAt = s.StartedAt, hooks = s.Hooks, calls = s.Calls, failures = s.Failures };
    }

    /// <summary>Stop and start one extension: after an edit, or to get it out of an error.</summary>
    public async Task RestartAsync(string key)
    {
        if (_hosts.Remove(key, out var host)) host.Dispose();
        _lastStart.Remove(key);
        await SyncAsync();
    }

    public void OpenDevTools(string key)
    {
        if (_hosts.TryGetValue(key, out var h)) h.OpenDevTools();
        else throw new InvalidOperationException("The extension is not running");
    }

    /// <summary>The extension's options changed on the page: hand it the new values.</summary>
    public void NotifySettings(string key)
    {
        if (!_hosts.TryGetValue(key, out var h) || !h.Running) return;
        var all = _settings().ExtensionSettings;
        h.SendEvent("settings", all.TryGetValue(h.Addon.Id, out var v) ? v : new Dictionary<string, JsonElement>());
    }

    public void CancelPass() => _passCts?.Cancel();

    /// <summary>
    /// Ask every running metadata extension about the games it has no fresh answer for. One
    /// extension at a time, one game at a time, paced as the manifest asks; saved every twenty
    /// seconds like the launcher's own pass, so closing the launcher mid-pass keeps what was
    /// fetched. `force` asks about every game again (Fetch now).
    /// </summary>
    public async Task RunPassAsync(string? onlyKey = null, bool force = false)
    {
        if (_disposed || !await _passGate.WaitAsync(0)) return;
        var cts = _passCts = new CancellationTokenSource();
        var ct = cts.Token;
        try
        {
            // The extensions starting (the sync that attach began) finish first, or a pass that
            // follows a quick metadata pass finds nothing running yet.
            if (_syncInFlight is { IsCompleted: false } sync) { try { await sync; } catch { /* logged by the sync */ } }
            List<ExtensionHost> hosts;
            lock (_hosts) hosts = _hosts.Values.Where(h => h.Running && h.Addon.Manifest.IsMetadataSource && h.HasHook("enrich")).ToList();
            foreach (var host in hosts)
            {
                if (ct.IsCancellationRequested) break;
                if (onlyKey is not null && host.Addon.Key != onlyKey) continue;
                await RunOneAsync(host, force, ct);
            }
        }
        catch (OperationCanceledException) { /* the pass was cancelled */ }
        catch (Exception ex) { Log.Info($"Extensions: the pass failed: {ex.Message}"); }
        finally
        {
            _passCts = null;
            _passGate.Release();
        }
    }

    private async Task RunOneAsync(ExtensionHost host, bool force, CancellationToken ct)
    {
        var a = host.Addon;
        var md = a.Manifest.Contributes!.Metadata!;
        var version = a.Manifest.Version;
        var due = _library.Games.ToList()
            .Where(g => !g.Hidden && (!md.InstalledOnly || g.Installed) && (force || NeedsFetch(g, a.Id, md, version)))
            .OrderByDescending(g => g.Installed)
            .ThenByDescending(g => g.LastPlayed ?? DateTime.MinValue)
            .Take(MaxPerPass)
            .ToList();
        if (due.Count == 0) return;
        Log.Info($"[ext:{a.Id}] pass: {due.Count} game(s) to ask about");

        var summary = new PassSummary { At = DateTime.UtcNow };
        var unsaved = false;
        var lastCheckpoint = DateTime.UtcNow;
        var consecutiveFailures = 0;
        try
        {
            foreach (var g in due)
            {
                if (ct.IsCancellationRequested || !host.Running) break;
                if (unsaved && DateTime.UtcNow - lastCheckpoint > CheckpointEvery)
                {
                    SaveAndTell();
                    unsaved = false;
                    lastCheckpoint = DateTime.UtcNow;
                }
                summary.Tried++;
                try
                {
                    var result = await host.CallAsync("enrich", new[] { ExtensionApi.Project(g) }, HookTimeout, ct);
                    var found = result.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined);
                    JsonElement? data = null;
                    if (found)
                    {
                        if (result.ValueKind != JsonValueKind.Object) throw new ExtensionException("enrich returned something other than an object or null");
                        if (result.GetRawText().Length > MaxDataBytes) throw new ExtensionException("enrich returned more than 16 KB");
                        data = result;
                    }
                    var record = new ExtRecord { At = DateTime.UtcNow, Ext = version, Found = found, Data = data };
                    _library.Change(() =>
                    {
                        var live = _library.Games.FirstOrDefault(x => x.Id == g.Id);
                        if (live is null) return;
                        live.Ext ??= new Dictionary<string, ExtRecord>();
                        live.Ext[a.Id] = record;
                    });
                    unsaved = true;
                    if (found) summary.Found++;
                    consecutiveFailures = 0;
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    summary.Failed++;
                    if (summary.Failed <= 3) Log.Info($"[ext:{a.Id}] {g.Title}: {ex.Message}");
                    if (++consecutiveFailures >= 5)
                    {
                        summary.Error = $"Stopped after five failures in a row: {ex.Message}";
                        Log.Info($"[ext:{a.Id}] {summary.Error}");
                        break;
                    }
                }
                if (md.PaceMs > 0) await Task.Delay(md.PaceMs, ct);
            }
        }
        finally
        {
            if (unsaved) SaveAndTell();
            Log.Info($"[ext:{a.Id}] pass done: {summary.Tried} asked, {summary.Found} answered, {summary.Failed} failed");
            _addons.RecordPass(a.Key, summary);
        }
    }

    private void SaveAndTell()
    {
        try { _library.Save(); }
        catch (Exception ex) { Log.Info($"Extensions: save failed: {ex.Message}"); }
        Checkpoint?.Invoke();
    }

    /// <summary>Whether this game is due for this extension: never asked, asked by an older version
    /// of it, an answer older than the manifest allows, or a "nothing" older than the retry window.</summary>
    public static bool NeedsFetch(Game g, string extId, MetadataContribution md, string version)
    {
        if (g.Ext is null || !g.Ext.TryGetValue(extId, out var rec) || rec is null) return true;
        if (rec.Ext != version) return true;
        var age = DateTime.UtcNow - rec.At;
        return rec.Found ? age > TimeSpan.FromDays(md.StaleAfterDays) : age > TimeSpan.FromDays(md.RetryAfterDays);
    }

    public void Dispose()
    {
        _disposed = true;
        _passCts?.Cancel();
        foreach (var h in _hosts.Values) h.Dispose();
        _hosts.Clear();
    }
}
