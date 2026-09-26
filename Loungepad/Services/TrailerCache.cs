using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using Loungepad.Models;

namespace Loungepad.Services;

/// <summary>
/// Keeps a copy of each trailer the page plays, so the next play comes off disk.
///
/// The page streams a trailer straight from Steam the first time -- a 1080p mp4 starts within a
/// second or two on any broadband -- and asks for it to be cached at the same moment. The download
/// runs here, one at a time, in the background; when it lands the game is pointed at the file and
/// the page is told, and every later play is local and instant. The two transfers overlap on the
/// first play, which costs that one play twice its bandwidth and nothing after.
///
/// One at a time, and only the LATEST request waits: browsing along a row asks for a trailer every
/// few seconds, and a queue of all of them would spend the evening downloading games the user has
/// already moved past. The one in flight finishes (most of it is usually down by the time the
/// highlight moves), the newest request is next, and anything in between is simply asked again the
/// next time it plays.
///
/// The folder is capped; the oldest files go first. "Oldest" is the file's write time, which is
/// touched on every play -- a cache folder can carry that hack, and NTFS stopped maintaining last
/// access times reliably long ago.
/// </summary>
public sealed class TrailerCache
{
    /// <summary>Four gigabytes: room for a few dozen trailers at Steam's "max" size, which runs
    /// from 30 MB for an indie to 300 MB for a big release.</summary>
    public const long CapBytes = 4L * 1024 * 1024 * 1024;

    private static readonly HttpClient Http = new(new HttpClientHandler { AutomaticDecompression = System.Net.DecompressionMethods.All })
    {
        // A 300 MB file on a slow line. Progress is a stream, so a stall still ends the request.
        Timeout = TimeSpan.FromMinutes(15),
    };

    private readonly LibraryStore _library;
    private readonly Func<bool> _enabled;
    private readonly object _gate = new();
    private Game? _next;
    private bool _busy;

    /// <summary>A download landed. Raised off the UI thread, with the game already pointed at the file.</summary>
    public event Action<Game>? Cached;

    public TrailerCache(LibraryStore library, Func<bool> enabled)
    {
        _library = library;
        _enabled = enabled;
        Http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "Loungepad/1.0 (+https://github.com/sukumar-v/Loungepad)");
    }

    /// <summary>
    /// The file name a trailer would have here. Keyed on the URL as well as the game, so a
    /// re-cut trailer (Steam changes the ?t= stamp) gets a new name rather than an old file.
    /// </summary>
    public static string NameFor(Game g)
    {
        var url = g.TrailerUrl ?? "";
        var hash = Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(url)))[..10].ToLowerInvariant();
        var path = Uri.TryCreate(url, UriKind.Absolute, out var u) ? u.AbsolutePath : url;
        var ext = Path.GetExtension(path).ToLowerInvariant();
        if (ext is not (".mp4" or ".webm")) ext = ".mp4";
        return g.Id.Replace(':', '_') + "_" + hash + ext;
    }

    /// <summary>
    /// Bring the library's trailer names in line with what is on disk. A file evicted or deleted
    /// leaves a name pointing at nothing, and a name the page trusts is a black video; clearing it
    /// makes the page stream instead, and the next play asks for the download again. Half-written
    /// files from a run that ended mid-download go too.
    /// </summary>
    public void Prune()
    {
        try
        {
            Directory.CreateDirectory(Paths.TrailersDir);
            foreach (var part in Directory.GetFiles(Paths.TrailersDir, "*.part"))
                try { File.Delete(part); } catch { }

            var cleared = 0;
            foreach (var g in _library.Games.ToList())
            {
                if (g.TrailerFile is null) continue;
                var stale = g.TrailerUrl is null || g.TrailerFile != NameFor(g)
                            || !File.Exists(Path.Combine(Paths.TrailersDir, g.TrailerFile));
                if (!stale) continue;
                g.TrailerFile = null;
                cleared++;
            }
            if (cleared > 0)
            {
                _library.Save();
                Log.Info($"Trailers: {cleared} cached name(s) no longer on disk, cleared");
            }
        }
        catch (Exception ex) { Log.Info($"Trailers: prune failed: {ex.Message}"); }
    }

    /// <summary>
    /// The page is about to play this game's trailer. Already here: the file is touched so it
    /// stays ahead of the eviction line. Not here: it becomes the next download.
    /// </summary>
    /// <summary>A URL there is a file behind. IGDB's trailers are YouTube pages: nothing to fetch,
    /// and the page plays them through YouTube's own player.</summary>
    public static bool IsFile(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var u)
        && (u.Scheme == "https" || u.Scheme == "http")
        && Path.GetExtension(u.AbsolutePath).ToLowerInvariant() is ".mp4" or ".webm";

    public void Request(Game g)
    {
        if (!IsFile(g.TrailerUrl) || !_enabled()) return;

        var name = NameFor(g);
        var path = Path.Combine(Paths.TrailersDir, name);
        if (File.Exists(path))
        {
            try { File.SetLastWriteTimeUtc(path, DateTime.UtcNow); } catch { }
            if (g.TrailerFile != name)
            {
                // Downloaded on an earlier run and never recorded, or recorded under an old name.
                g.TrailerFile = name;
                _library.Save();
                Cached?.Invoke(g);
            }
            return;
        }

        lock (_gate)
        {
            _next = g;
            if (_busy) return;
            _busy = true;
        }
        _ = Task.Run(RunAsync);
    }

    private async Task RunAsync()
    {
        while (true)
        {
            Game g;
            lock (_gate)
            {
                if (_next is null) { _busy = false; return; }
                g = _next;
                _next = null;
            }
            if (!_enabled()) continue;
            await DownloadAsync(g);
        }
    }

    private async Task DownloadAsync(Game g)
    {
        var url = g.TrailerUrl;
        if (url is null) return;
        var name = NameFor(g);
        var dest = Path.Combine(Paths.TrailersDir, name);
        var tmp = dest + ".part";
        try
        {
            Directory.CreateDirectory(Paths.TrailersDir);
            using var res = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
            if (!res.IsSuccessStatusCode)
            {
                Log.Info($"Trailers: {g.Title} <- {url} answered {(int)res.StatusCode}");
                return;
            }
            // Streamed to disk rather than read into memory: these are hundreds of megabytes.
            await using (var body = await res.Content.ReadAsStreamAsync())
            await using (var file = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16))
                await body.CopyToAsync(file);

            var size = new FileInfo(tmp).Length;
            if (size < 64 * 1024)
            {
                // A placeholder or an error page with a 200 on it. Not a trailer.
                try { File.Delete(tmp); } catch { }
                Log.Info($"Trailers: {g.Title} came back as {size} bytes; not kept");
                return;
            }
            File.Move(tmp, dest, overwrite: true);

            // The library may have been re-read while this was in flight; write to the copy that
            // is current, and to the one we were handed if it is still that copy.
            var live = _library.Find(g.Id) ?? g;
            live.TrailerFile = name;
            if (!ReferenceEquals(live, g)) g.TrailerFile = name;
            _library.Save();
            Log.Info($"Trailers: cached {g.Title} ({size / (1024 * 1024)} MB)");
            Cached?.Invoke(live);

            Evict();
        }
        catch (Exception ex)
        {
            try { File.Delete(tmp); } catch { }
            Log.Info($"Trailers: {g.Title} <- {url} failed: {ex.Message}");
        }
    }

    /// <summary>Oldest out until the folder is under the cap. Names left in the library are
    /// cleared by Prune on the next start, and by the page's fallback to the stream before then.</summary>
    private void Evict()
    {
        try
        {
            var files = new DirectoryInfo(Paths.TrailersDir).GetFiles()
                .Where(f => !f.Name.EndsWith(".part", StringComparison.OrdinalIgnoreCase))
                .OrderBy(f => f.LastWriteTimeUtc)
                .ToList();
            var total = files.Sum(f => f.Length);
            foreach (var f in files)
            {
                if (total <= CapBytes) break;
                try
                {
                    f.Delete();
                    total -= f.Length;
                    var owner = _library.Games.FirstOrDefault(x => x.TrailerFile == f.Name);
                    if (owner is not null) { owner.TrailerFile = null; _library.Save(); }
                    Log.Info($"Trailers: evicted {f.Name}");
                }
                catch (Exception ex) { Log.Info($"Trailers: could not evict {f.Name}: {ex.Message}"); }
            }
        }
        catch (Exception ex) { Log.Info($"Trailers: eviction failed: {ex.Message}"); }
    }
}
