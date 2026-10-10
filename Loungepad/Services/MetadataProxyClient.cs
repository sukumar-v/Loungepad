using System.Net.Http;
using System.Text.Json;

namespace Loungepad.Services;

/// <summary>Somewhere facts can come from: our proxy, or the user's own IGDB credentials.</summary>
public interface IFactsProvider
{
    /// <param name="platforms">IGDB platform ids to confine a title search to, for a ROM: the
    /// folder says it is a SNES game, and "Doom" on the SNES is the 1993 game, not the 2016 one.
    /// Null for anything that is not emulated. Ignored when the lookup is by Steam app id.</param>
    Task<IgdbGame?> FindAsync(string title, string? steamAppId, IReadOnlyList<int>? platforms, CancellationToken ct);
    /// <summary>True once this source has failed in a way that will repeat for every game, so the
    /// pass can stop asking rather than failing once per title.</summary>
    bool Unavailable { get; }
}

/// <summary>Somewhere art can come from: our proxy, or the user's own SteamGridDB key.</summary>
public interface IArtProvider
{
    Task<SteamGridArt?> FindArtAsync(string title, string? steamAppId, CancellationToken ct);
    bool Unavailable { get; }
}

/// <summary>
/// The default source for games that are not on Steam, and the reason installing Loungepad does
/// not come with a signup form. The credentials live on the proxy; this end holds nothing.
///
/// The same arrangement Playnite uses -- its IGDB plugin ships no keys and points at
/// api2.playnite.link -- and for the same reason: a shipped binary cannot keep a secret.
///
/// Note that the strict title check still happens *here*, on every response, even though the proxy
/// applies one of its own. The proxy is a convenience, not an authority: if it is ever wrong,
/// stale or replaced, it still cannot put another game's art on a tile.
///
/// What the service is sent is a game's title and, when there is one, its Steam app id and the
/// IGDB platform ids of a ROM's system -- about a game, never about the person. The owned-games
/// and achievements routes it once had took a SteamID; they are gone from both ends.
/// </summary>
public class MetadataProxyClient : IFactsProvider, IArtProvider
{
    /// <summary>
    /// Sent with every request so the service can refuse the drive-by traffic that only knows its
    /// URL -- a scraper, a bot, somebody else's launcher -- before it costs an upstream call. It is
    /// a speed bump and not a secret: the value is in the binary. The service checks for it.
    /// </summary>
    public const string ClientHeader = "X-Loungepad-Client";
    public const string ClientHeaderValue = "1";

    /// <summary>
    /// Where a shipped build looks, and the reason installing Loungepad comes with no setup. Set
    /// to empty to turn the proxy tier off entirely; a user can override it in Settings, and their
    /// own credentials take priority over it either way. See proxy/README.md.
    ///
    /// A domain of our own since 1.9.0, so the service behind it can be renamed or moved without
    /// a launcher release. Builds up to 1.8.0 have the old workers.dev address, which is now a
    /// forwarder to the same worker (proxy/legacy); see the note in proxy/wrangler.toml.
    /// </summary>
    public const string DefaultEndpoint = "https://api.loungepad.app";

    private readonly HttpClient _http;
    private readonly string _endpoint;

    public bool Unavailable { get; private set; }

    public MetadataProxyClient(HttpClient http, string endpoint)
    {
        _http = http;
        _endpoint = endpoint.TrimEnd('/');
    }

    public static bool IsConfigured(string? endpoint) =>
        !string.IsNullOrWhiteSpace(endpoint)
        && Uri.TryCreate(endpoint, UriKind.Absolute, out var u)
        && (u.Scheme == Uri.UriSchemeHttps || u.IsLoopback);

    public async Task<IgdbGame?> FindAsync(string title, string? steamAppId, IReadOnlyList<int>? platforms, CancellationToken ct)
    {
        var d = await GetAsync("facts", title, steamAppId, platforms, ct);
        if (d is null) return null;

        using (d)
        {
            var root = d.RootElement;
            // The proxy said this was the game. We do not take its word for it -- unless it was
            // asked by Steam app id, in which case there was no matching to second-guess and the
            // upstream name may legitimately differ from the one Steam prints.
            var name = Str(root, "name");
            if (steamAppId is null && !TitleMatch.IsConfident(title, name))
            {
                Log.Info($"Proxy: returned '{name}' for '{title}', which is not a confident match");
                return null;
            }

            DateTime? released = null;
            if (Str(root, "released") is { } iso && DateTime.TryParse(iso, out var parsed)) released = parsed;

            var genres = new List<string>();
            if (root.TryGetProperty("genres", out var gs) && gs.ValueKind == JsonValueKind.Array)
                genres = gs.EnumerateArray().Select(x => x.GetString())
                    .Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x!).ToList();

            return new IgdbGame
            {
                Name = name ?? "",
                Summary = Str(root, "summary"),
                Developer = Str(root, "developer"),
                Publisher = Str(root, "publisher"),
                Genres = genres,
                Released = released,
                PegiRating = JsonNum.Int(root, "pegi"),
                // The proxy hands back finished URLs rather than image ids, so these go straight
                // into the art slots without IgdbClient.ImageUrl in between.
                CoverUrl = Str(root, "cover"),
                ArtworkUrl = Str(root, "artwork"),
                VideoId = Str(root, "video"),
                Videos = root.TryGetProperty("videos", out var vids) && vids.ValueKind == JsonValueKind.Array
                    ? vids.EnumerateArray()
                        .Where(v => v.ValueKind == JsonValueKind.Object && Str(v, "id") is { Length: > 0 })
                        .Select(v => new IgdbVideo(Str(v, "id")!, Str(v, "name")))
                        .ToList()
                    : new List<IgdbVideo>(),
                Screenshots = root.TryGetProperty("screenshots", out var shots) && shots.ValueKind == JsonValueKind.Array
                    ? shots.EnumerateArray().Where(s => s.ValueKind == JsonValueKind.String)
                        .Select(s => s.GetString()!).Where(s => s.Length > 0).ToList()
                    : new List<string>(),
            };
        }
    }

    public async Task<SteamGridArt?> FindArtAsync(string title, string? steamAppId, CancellationToken ct)
    {
        var d = await GetAsync("art", title, steamAppId, null, ct);
        if (d is null) return null;

        using (d)
        {
            var root = d.RootElement;
            var name = Str(root, "name");
            if (steamAppId is null && !TitleMatch.IsConfident(title, name))
            {
                Log.Info($"Proxy: art for '{name}' does not confidently match '{title}'");
                return null;
            }

            return new SteamGridArt
            {
                Portrait = Str(root, "portrait"),
                Tile = Str(root, "tile"),
                Hero = Str(root, "hero"),
                Logo = Str(root, "logo"),
            };
        }
    }

    /// <summary>
    /// A square picture's URL (/v1/square). `Answered` is false when the service did not really
    /// answer: it could not be reached, said slow down, or 404'd WITHOUT its X-Cache header -- the
    /// shape of a route it does not have. That last one is a service from before the route, and its
    /// "not found" must not be read as "this game has no square": that is how 390 games were marked
    /// as having none, for a month, in the hour before the route was deployed (Oct 10 2026).
    /// Without an app id the answer is checked against the title like any other art.
    /// </summary>
    public async Task<(string? Url, bool Answered)> FindSquareAsync(string title, string? steamAppId, CancellationToken ct)
    {
        var d = await GetAsync("square", title, steamAppId, null, ct);
        if (d is null) return (null, !Unavailable && _lastNotFoundAnswered);
        using (d)
        {
            var root = d.RootElement;
            if (steamAppId is null && !TitleMatch.IsConfident(title, Str(root, "name"))) return (null, true);
            return (Str(root, "square"), true);
        }
    }

    // Whether the last 404 was the service's own "no match" (it carries X-Cache) rather than a
    // route it does not have.
    private bool _lastNotFoundAnswered;

    private async Task<JsonDocument?> GetAsync(string kind, string title, string? steamAppId,
        IReadOnlyList<int>? platforms, CancellationToken ct)
    {
        if (Unavailable) return null;
        try
        {
            // Both are sent when both are known: the id is what the service looks up, and the
            // title is its fallback when that game is not in the upstream database under that id.
            var url = $"{_endpoint}/v1/{kind}?title={Uri.EscapeDataString(title)}";
            if (steamAppId is not null) url += $"&appid={Uri.EscapeDataString(steamAppId)}";
            // A worker that predates the parameter ignores it and answers by title alone, which
            // is what every non-Steam game got before ROMs existed -- a downgrade, not a failure.
            if (platforms is { Count: > 0 }) url += $"&platform={string.Join(",", platforms)}";
            // On the request rather than the client: the client is shared with Steam's CDN, which
            // has no business seeing the header.
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.TryAddWithoutValidation(ClientHeader, ClientHeaderValue);
            using var res = await _http.SendAsync(req, ct);

            // 404 is the service saying "no confident answer", which is an ordinary outcome.
            if (res.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                _lastNotFoundAnswered = res.Headers.Contains("X-Cache");
                return null;
            }

            if ((int)res.StatusCode == 429)
            {
                // Backing off for the rest of the pass is the polite response, and the user loses
                // nothing they had: art already on disk stays.
                Unavailable = true;
                Log.Info("Proxy: rate limited, skipping the rest of this pass");
                return null;
            }

            if (!res.IsSuccessStatusCode)
            {
                Log.Info($"Proxy: /v1/{kind} returned {(int)res.StatusCode}");
                return null;
            }

            return JsonDocument.Parse(await res.Content.ReadAsStringAsync(ct));
        }
        catch (Exception ex)
        {
            // One dead connection is enough to conclude the service is not reachable right now.
            // Everything it would have supplied is optional, so this is a quiet downgrade.
            Unavailable = true;
            Log.Info($"Proxy: unreachable ({ex.Message}); metadata for non-Steam games is skipped");
            return null;
        }
    }

    private static string? Str(JsonElement e, string key) =>
        e.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
