using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using Loungepad.Models;

namespace Loungepad.Services;

/// <summary>
/// Fills in the things a filesystem scan cannot know: a description, who made it, when it came
/// out, what it scored, and art at a resolution worth putting on a television.
///
/// Three sources, in descending order of trust:
///
///   Steam        keyed by app id, which the scanner already has. No key, no account, and no
///                title matching, so a Steam game can never be given another game's art. Always
///                used for "steam:" entries, and the others never override it.
///   IGDB         everything else's facts. Needs the user's Twitch credentials.
///   SteamGridDB  everything else's art, which is what it exists for. Needs the user's API key.
///
/// The two keyed providers match by title, so everything they return goes through TitleMatch,
/// which declines anything short of an exact match. See the note there: a wrong cover is worse
/// than a missing one, because nothing about it looks wrong.
///
/// Nothing here is required. Every field it writes is cosmetic, every failure is swallowed, and a
/// machine with no network -- or no credentials -- simply keeps the art the scanner copied out of
/// Steam's local cache.
/// </summary>
public class MetadataService
{
    // Steam's store endpoint is undocumented and rate limited at roughly 200 requests per 5
    // minutes per IP. A library big enough to matter is fetched once and then not again for a
    // fortnight, and the gap below keeps even a 200-game first run inside the limit.
    private const int StoreGapMs = 1500;
    private static readonly TimeSpan Freshness = TimeSpan.FromDays(14);

    /// <summary>
    /// What this build knows how to fetch. Bump it whenever a field is added or a picture starts
    /// being chosen differently, and every entry stamped with an older number is fetched again on
    /// the next pass.
    ///
    /// Without this a library only picks up a change after the freshness window runs out, which
    /// is a fortnight of the app knowing about PEGI ratings and never asking for one. Freshness is
    /// about not re-hitting the network for the same answer; it was never meant to pin a library
    /// to whatever the app happened to know the day it first scanned.
    /// </summary>
    private const int FetchVersion = 10;  // 6: the store trailer; 7: IGDB's YouTube trailer; 8: the gallery; 9: descriptors per board; 10: Metacritic only

    /// <summary>
    /// Which source wrote a file, as part of its name.
    ///
    /// This is the whole reason the capsule fix did not work the first time. A slot's file was
    /// named for the slot and the extension only -- "_hdtile" + ".jpg" -- so Steam's 616x353
    /// capsule and SteamGridDB's 920x430 grid, which is also served as .jpg, resolved to the
    /// SAME PATH. The service ran, overwrote the capsule in place, and every later pass found a
    /// file that was named like a capsule, skipped the download because it already existed, and
    /// pointed the tile at 2.14:1 art. Every tile in the library had a blurred mat under it and
    /// nothing in the code said why.
    ///
    /// With the source in the name the two can coexist on disk, and which one a game uses is
    /// decided by the code that runs rather than by whichever provider wrote last.
    /// </summary>
    private const string Steam = "_st", Service = "_sv";
    /// <summary>Art the game's own store published: Galaxy's GOG-hosted covers, the Microsoft
    /// Store's posters. A third source, so a third name -- see above for why that matters.</summary>
    private const string Store = "_pf";

    private static readonly HttpClient Http = CreateClient();

    private DateTime _lastStoreCall = DateTime.MinValue;

    private static HttpClient CreateClient()
    {
        var c = new HttpClient(new HttpClientHandler { AutomaticDecompression = DecompressionMethods.All })
        {
            Timeout = TimeSpan.FromSeconds(20)
        };
        c.DefaultRequestHeaders.Add("User-Agent", "Loungepad/1.0 (+https://github.com/sukumar-v/Loungepad)");
        return c;
    }

    /// <summary>Which picture a file is, independent of what it ends up called on disk.</summary>
    private enum Slot { Cover, Tile, Hero, Backdrop, Logo }

    // Steam serves all of these straight off its CDN, unauthenticated, for any app id, at their
    // original sizes rather than the half-size copies the client keeps on disk.
    //
    // capsule_616x353 is the one that matters for a landscape tile: it has the logo burnt in, so a
    // tile reads as the game at a glance from across a room. Not every app has one -- header.jpg
    // (2.14:1) is the fallback and always exists, and is why tiles must never be cover-cropped.
    //
    // The hero is taken at 2x. The backdrop element is inset -80px, so on a 1920x1080 stage it is
    // 2080x1240 -- and covering that from the 1920x620 hero meant a 2x upscale showing 54% of the
    // width, which is exactly the "zoomed in and blurry" it looked like. library_hero_2x is
    // 3840x1240: the same crop, but every pixel is now one pixel or better.

    /// <summary>
    /// Steam's official library assets, asked for BEFORE the service and given first refusal on
    /// their slots. Each is published at a fixed size that is exactly what the app's boxes are cut
    /// to, and "fixed size" is the whole point: a tile is 1.75:1 because capsule_616x353 is, and a
    /// hero is 3.1:1 because library_hero is.
    ///
    /// The service is still asked first for facts, and it still owns every slot Steam has nothing
    /// for. But its ART is community-uploaded and comes in whatever shape somebody made it --
    /// SteamGridDB's landscape grids are all 2.14:1, IGDB's artworks run from 0.75:1 to 3.1:1 --
    /// so letting it win a slot Steam publishes properly meant the library's tiles changed shape
    /// depending on which pass ran last. That is what "the artwork keeps changing" was.
    /// </summary>
    private static readonly (string Remote, Slot Slot, string Suffix)[] SteamPreferred =
    {
        ("capsule_616x353.jpg",    Slot.Tile,  Steam + "_cap"),    // 616x353, the landscape tile
        ("library_600x900_2x.jpg", Slot.Cover, Steam + "_cover"),  // 600x900, portrait box art
        ("library_hero_2x.jpg",    Slot.Hero,  Steam + "_hero2x"), // 3840x1240 -- see below
        // Steam's wordmark is 640x360 or wider, every time. SteamGridDB's logos are whatever
        // somebody drew: 0.92:1 for DREDGE, 1.11:1 for Henry Stickmin, 7.34:1 for ULTRAKILL. The
        // detail page hangs this where the title goes, so a square one lands as a small blob in
        // the corner of a box cut for a wordmark. Same argument as the capsule and the hero.
        ("logo.png",               Slot.Logo,  Steam + "_logo"),   // transparent wordmark
    };

    /// <summary>
    /// The rest of Steam's art, asked for after the service as the last fallback. These are the
    /// ones that are either the wrong shape (header.jpg is 2.14:1) or a lower-resolution copy of
    /// something above, so anything the service has beats them.
    /// </summary>
    private static readonly (string Remote, Slot Slot, string Suffix)[] SteamFallback =
    {
        ("header.jpg",       Slot.Tile, Steam + "_head"),   // 460x215, for apps with no capsule
        ("library_hero.jpg", Slot.Hero, Steam + "_hero"),   // 1920x620, the 1x hero
    };

    /// <summary>How often a pass hands its work so far to the caller to save and show.</summary>
    private static readonly TimeSpan CheckpointEvery = TimeSpan.FromSeconds(20);

    /// <summary>
    /// Brings every game that needs it up to date, in place. Returns how many were given
    /// something new (Changed) and how many were stamped as tried (Stamped), so the caller can
    /// skip a UI push when nothing is different on screen but still save the stamps.
    ///
    /// `checkpoint` is called every CheckpointEvery while there is unsaved work, and it is not
    /// optional in practice. A pass over a whole library runs at store pace -- 1.5 s a game, so
    /// the best part of half an hour for a Game Pass catalogue -- and everything it found used to
    /// be held in memory until the last game. Close the launcher before then and the pass had
    /// never happened: every start began again from the first game and nothing ever reached the
    /// screen. Installed games go first, most recently played first, so what is actually on the
    /// tiles is filled in within the first seconds rather than after five hundred catalogue
    /// entries nobody has played.
    /// </summary>
    public async Task<(int Changed, int Stamped)> EnrichAsync(IReadOnlyList<Game> games, AppSettings settings,
        Action? checkpoint = null, CancellationToken ct = default)
    {
        // The user's own credentials win over the shared service. Somebody who has gone to the
        // trouble of registering a Twitch application should not be silently routed through
        // somebody else's server, and it gives them a way out if the service is ever down.
        IFactsProvider? facts = IgdbClient.IsConfigured(settings.IgdbClientId, settings.IgdbClientSecret)
            ? new IgdbClient(Http, settings.IgdbClientId, settings.IgdbClientSecret)
            : null;
        IArtProvider? art = SteamGridDbClient.IsConfigured(settings.SteamGridDbKey)
            ? new SteamGridDbClient(Http, settings.SteamGridDbKey)
            : null;

        var endpoint = string.IsNullOrWhiteSpace(settings.MetadataEndpoint)
            ? MetadataProxyClient.DefaultEndpoint
            : settings.MetadataEndpoint;
        if ((facts is null || art is null) && MetadataProxyClient.IsConfigured(endpoint))
        {
            var proxy = new MetadataProxyClient(Http, endpoint);
            facts ??= proxy;
            art ??= proxy;
        }

        var search = new SteamSearchClient(Http);
        var due = games.Where(g => NeedsFetch(g))
            .OrderByDescending(g => g.Installed)
            .ThenByDescending(g => g.LastPlayed ?? DateTime.MinValue)
            .ToList();
        if (due.Count == 0) return (0, 0);

        Log.Info($"Metadata: {due.Count} game(s) to fetch" +
                 $", facts={Describe(facts)}, art={Describe(art)}");
        var changed = 0;
        var stamped = 0;
        var unsaved = false;
        var lastCheckpoint = DateTime.UtcNow;

        foreach (var g in due)
        {
            if (unsaved && DateTime.UtcNow - lastCheckpoint > CheckpointEvery)
            {
                try { checkpoint?.Invoke(); Log.Info($"Metadata: checkpoint, {changed} changed and {stamped} stamped so far"); }
                catch (Exception ex) { Log.Info($"Metadata: checkpoint failed: {ex.Message}"); }
                unsaved = false;
                lastCheckpoint = DateTime.UtcNow;
            }
            if (ct.IsCancellationRequested) break;
            try
            {
                var appId = SteamAppId(g);
                IReadOnlyList<int>? platforms = null;

                if (g.Emulated)
                {
                    // A ROM is never looked up on Steam. Steam sells "DOOM" (2016) and "DOOM
                    // (1993)" both, and the search's exact-title rule would hand the SNES
                    // cartridge the 2016 game's capsule with nothing about it looking wrong. The
                    // service is asked instead, with the folder's system as a constraint on the
                    // search, and that is the whole of what a ROM can be matched on.
                    platforms = EmulatedPlatforms.Find(g.PlatformId)?.IgdbIds;
                }
                // A non-Steam game that Steam nonetheless sells. Resolved first even though Steam
                // is now the fallback, because an app id is worth having either way: the service
                // is asked by id rather than by title, which removes the matching from the whole
                // exchange, and Steam can then fill anything the service leaves empty.
                else if (appId is null)
                {
                    await PaceStoreAsync(ct);
                    appId = await search.FindAppIdAsync(g.Title, ct);
                }

                // A slot holding art the user chose by hand is already settled: counting it as
                // filled means nothing is downloaded for it at all, rather than fetched and then
                // discarded by the guard in Assign.
                var filled = CustomSlots(g);
                var touched = false;

                // A game that is owned but not installed gets the lite pass: the cover and the
                // tile, which are what the grid is made of, and Steam's own facts when Steam
                // sells it. Not the 2x hero, the backdrop or the wordmark -- those dress a
                // detail page, and at several hundred uninstalled games (a Game Pass catalogue
                // alone is five hundred) they are most of a gigabyte for pictures behind games
                // nobody has played -- and not the shared service, whose budget is everyone's
                // and which would otherwise be asked twice per game per user. MergeScanned
                // clears the stamp the moment the game turns installed, and the next pass fills
                // the rest.
                var lite = !g.Installed;
                if (lite) { filled.Add(Slot.Hero); filled.Add(Slot.Backdrop); filled.Add(Slot.Logo); }

                // One priority order for art, best source first, and the first to fill a slot
                // keeps it. Steam's own library assets lead because they are published at fixed
                // sizes that are exactly the shapes this app's boxes are cut to.
                if (appId is not null) touched |= await PreferSteamArtAsync(g, appId, filled, ct);

                // Then the service, for the slots Steam has nothing for -- a landscape tile for a
                // game with no capsule, a wordmark, 16:9 key art -- and for every non-Steam game,
                // where it is the only source there is. Facts come from here first regardless;
                // the critic score (Metacritic's) and the controller-support flag are Steam's alone.
                var (elsewhere, serviceFacts, igdbVideo) = lite
                    ? (false, false, (string?)null)
                    : await EnrichElsewhereAsync(g, facts, art, appId, platforms, filled, ct);
                touched |= elsewhere;

                // Steam runs after, as the fallback: it fills every art slot and every field the
                // service left empty, and it always supplies controller support, which IGDB has
                // no equivalent of.
                if (appId is not null) touched |= await EnrichSteamAsync(g, appId, filled, serviceFacts, lite, ct);

                // The trailer, settled once both have spoken. Steam's is a file -- cacheable, no
                // player chrome -- and it has already written itself above when it exists. IGDB's
                // is a YouTube id and is only for the games Steam has nothing for: ROMs, store
                // exclusives, anything the search did not find. One that IGDB has since dropped
                // goes too, but only when IGDB actually answered.
                if (g.TrailerUrl is null || IsYouTube(g.TrailerUrl))
                {
                    if (igdbVideo is not null) touched |= SetTrailer(g, "https://www.youtube.com/watch?v=" + igdbVideo);
                    else if (serviceFacts) touched |= SetTrailer(g, null);
                }

                // The store's own art, last. Galaxy's GOG-hosted covers and the Microsoft Store's
                // posters are proper box art, and for a game that is not on Steam and that the
                // service does not know, they are the only picture there is.
                if (g.RemoteCoverUrl is { } storeCover)
                    touched |= await StoreRemoteAsync(g, Slot.Cover, storeCover, filled, ct, Store);
                if (g.RemoteBackdropUrl is { } storeBackdrop)
                    touched |= await StoreRemoteAsync(g, Slot.Backdrop, storeBackdrop, filled, ct, Store);

                // Stamped even when nothing was found, so a game that genuinely has no metadata is
                // not looked up again on every launch -- but NOT when every source that could have
                // answered was unavailable. Otherwise a typo'd key or an outage would mark the
                // library "tried" and fixing it would appear to do nothing for a fortnight.
                if (appId is not null || !AllUnavailable(facts, art))
                {
                    g.MetadataFetched = DateTime.UtcNow;
                    g.MetadataVersion = FetchVersion;
                    stamped++;
                    unsaved = true;
                }
                if (touched) { changed++; unsaved = true; }
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                // Left unstamped, so a later run tries again rather than treating a dropped
                // connection as "this game has no metadata".
                Log.Info($"Metadata: {g.Title} failed: {ex.Message}");
            }
        }

        Log.Info($"Metadata: updated {changed} game(s), stamped {stamped}");
        return (changed, stamped);
    }

    private static string Describe(object? provider) => provider switch
    {
        null => "none",
        MetadataProxyClient => "proxy",
        IgdbClient => "your IGDB key",
        SteamGridDbClient => "your SteamGridDB key",
        _ => "on",
    };

    /// <summary>
    /// True when every source that could answer for a non-Steam game has given up for this pass --
    /// bad credentials, an outage, a rate limit -- or when there was never one configured.
    /// </summary>
    private static bool AllUnavailable(IFactsProvider? facts, IArtProvider? art)
    {
        if (facts is null && art is null) return true;
        return (facts is null || facts.Unavailable) && (art is null || art.Unavailable);
    }

    /// <summary>
    /// Steam's store endpoints are rate limited at roughly 200 requests per 5 minutes per IP, and
    /// both the search and appdetails count. Spacing every store call rather than every game keeps
    /// a library that leans on the search tier inside the same budget.
    /// </summary>
    private async Task PaceStoreAsync(CancellationToken ct)
    {
        var wait = StoreGapMs - (int)(DateTime.UtcNow - _lastStoreCall).TotalMilliseconds;
        if (wait > 0) await Task.Delay(wait, ct);
        _lastStoreCall = DateTime.UtcNow;
    }

    private static bool NeedsFetch(Game g)
    {
        // Filled in by a build that fetched less than this one does, so the answers on file are
        // not wrong, just short. Checked before freshness on purpose: the window is there to stop
        // us asking the same question twice, not to stop us asking a new one.
        if (g.MetadataVersion != FetchVersion) return true;

        // Art can go missing on its own -- a cleared covers folder, a half-finished first run --
        // so a game inside the freshness window is still due if its files are not there. Nothing
        // is excluded up front any more: with the keyless Steam tiers there is always something
        // that might answer, and the sources themselves decide whether they can.
        if (g.MetadataFetched is { } at && DateTime.UtcNow - at < Freshness)
        {
            if (HasFetchedArt(g)) return false;
            // A ROM with no art is the normal case for a lot of ROMs -- an obscure set, a title
            // the file name did not carry -- and there is one source for it, which answered the
            // same nothing last time. Asked again on every start, a folder of a thousand arcade
            // sets is a thousand proxy calls per start for nothing; so a ROM waits out the
            // window unless a picture it did have has gone missing.
            if (g.Emulated && !ArtGoneMissing(g)) return false;
        }
        return true;
    }

    /// <summary>True when the entry names an art file that is no longer on disk.</summary>
    private static bool ArtGoneMissing(Game g) =>
        new[] { g.CoverFile, g.BannerFile, g.HeroFile, g.BackdropFile, g.LogoFile }
            .Any(f => f is not null && !File.Exists(Path.Combine(Paths.CoversDir, f)));

    /// <summary>
    /// True when the tile is art this pass would not improve on: something downloaded, or
    /// something the user chose. Without the second half a hand-picked tile reads as "no fetched
    /// art" forever and puts its game back in the queue on every single start.
    /// </summary>
    private static bool HasFetchedArt(Game g) =>
        Fetched(g.BannerFile)
        // An uninstalled game gets the lite pass, whose cover may be the only picture any source
        // had -- a Game Pass game that is not on Steam has no capsule to fetch. Judged on the tile
        // alone it would be "no art" and back in the queue on every start, for a search that
        // answers the same nothing each time.
        || (!g.Installed && Fetched(g.CoverFile));

    private static bool Fetched(string? name) =>
        name is { } b && (b.Contains(Steam) || b.Contains(Service) || b.Contains(Store) || IsCustom(b))
        && File.Exists(Path.Combine(Paths.CoversDir, b));

    private static string? SteamAppId(Game g) =>
        g.Id.StartsWith("steam:", StringComparison.Ordinal) && g.Id.Length > 6 ? g.Id[6..] : null;

    /// <summary>Base name for this game's downloaded art. "steam:367520" -> "steam_367520".</summary>
    private static string ArtPrefix(Game g) => g.Id.Replace(':', '_');

    // ---------- Steam ----------

    /// <summary>
    /// The Steam path, used both for a "steam:" entry and for a non-Steam game the search resolved
    /// to an app id. Identical either way: once there is an app id there is no guessing left.
    ///
    /// Facts come first because they carry a fallback the art step needs. Newer apps have stopped
    /// publishing art at the legacy cdn/steam/apps/&lt;id&gt;/&lt;name&gt; paths -- Forza Horizon 6 has only
    /// library_hero.jpg there, and 404s for the capsule and the header -- but appdetails always
    /// names a working header_image under store_item_assets, hashed per release.
    /// </summary>
    private async Task<bool> EnrichSteamAsync(Game g, string appId, HashSet<Slot> filled,
        bool serviceAnswered, bool lite, CancellationToken ct)
    {
        await PaceStoreAsync(ct);
        var (gotFacts, headerImage) = await FetchSteamFactsAsync(g, appId, serviceAnswered, lite, ct);
        if (gotFacts && g.MetadataSource is null) g.MetadataSource = "steam";

        var gotArt = await FetchSteamArtAsync(g, appId, headerImage, filled, ct);
        return gotArt || gotFacts;
    }

    /// <summary>
    /// Steam's own art, before the service. A game that publishes none of it -- REANIMAL and
    /// Forza Horizon 6 both 404 for the capsule -- falls through untouched and the service fills
    /// the slot instead, which is what it is there for.
    /// </summary>
    private async Task<bool> PreferSteamArtAsync(Game g, string appId, HashSet<Slot> filled,
        CancellationToken ct)
    {
        var any = false;
        foreach (var entry in SteamPreferred)
        {
            if (ct.IsCancellationRequested) break;
            any |= await FetchSteamEntryAsync(g, appId, entry, filled, ct);
        }
        return any;
    }

    private async Task<bool> FetchSteamArtAsync(Game g, string appId, string? headerImage,
        HashSet<Slot> filled, CancellationToken ct)
    {
        var any = false;

        // Whatever is still empty after the preferred assets and the service. Listed best first,
        // and a slot already filled is skipped.
        foreach (var entry in SteamFallback)
        {
            if (ct.IsCancellationRequested) break;
            any |= await FetchSteamEntryAsync(g, appId, entry, filled, ct);
        }

        // Last resort for the tile, and the only art newer apps publish at all. Checked against
        // the game rather than a filename because the loop above may have written a tile from a
        // legacy path already, and that one is the better shape.
        if (!filled.Contains(Slot.Tile) && !string.IsNullOrWhiteSpace(headerImage))
            any |= await StoreRemoteAsync(g, Slot.Tile, headerImage, filled, ct, Steam);

        return any;
    }

    /// <summary>One entry off the CDN, skipped when its slot is already taken.</summary>
    private async Task<bool> FetchSteamEntryAsync(Game g, string appId,
        (string Remote, Slot Slot, string Suffix) entry, HashSet<Slot> filled, CancellationToken ct)
    {
        if (ct.IsCancellationRequested || filled.Contains(entry.Slot)) return false;

        var name = ArtPrefix(g) + entry.Suffix + Path.GetExtension(entry.Remote);
        var dest = Path.Combine(Paths.CoversDir, name);

        // Already have this exact asset, so nothing to fetch. The name says which remote file it
        // is AND which source wrote it, so unlike the old scheme this cannot be some other
        // provider's picture sitting under a name that claims to be Steam's.
        if (File.Exists(dest))
        {
            if (!Fits(entry.Slot, dest)) return false;
            filled.Add(entry.Slot);
            return Assign(g, entry.Slot, name);
        }

        var url = $"https://cdn.cloudflare.steamstatic.com/steam/apps/{appId}/{entry.Remote}";
        if (!await DownloadAsync(url, dest, ct)) return false;
        if (!Fits(entry.Slot, dest))
        {
            try { File.Delete(dest); } catch { }
            return false;
        }

        filled.Add(entry.Slot);
        return Assign(g, entry.Slot, name);
    }

    /// <summary>Facts, plus the header image URL appdetails names -- the art step needs it as a
    /// fallback for apps that no longer publish to the legacy CDN paths.</summary>
    private async Task<(bool Ok, string? HeaderImage)> FetchSteamFactsAsync(Game g, string appId,
        bool serviceAnswered, bool lite, CancellationToken ct)
    {
        // "ratings" is the one that carries the age boards, and it has to be asked for by name --
        // the filter list is exhaustive, so leaving it out drops the whole block silently.
        var details = await SteamAppDetailsAsync(appId,
            "basic,genres,metacritic,release_date,developers,publishers,controller_support,ratings,movies,screenshots", ct);
        if (details is null) return (false, null);
        using var doc = details.Value.Doc;
        var d = details.Value.Data;

        string? Str(string key) =>
            d.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

        string? First(string key) =>
            d.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Array
                ? v.EnumerateArray().FirstOrDefault().GetString() : null;

        // Controller support is taken whatever else happened: IGDB has no equivalent of it, and on
        // a couch it is the most useful line on the detail page. This is the reason Steam is still
        // worth asking for a game the service has already answered.
        g.ControllerSupport = Str("controller_support");

        // The critic score is Metacritic's or nothing, and Steam is the one free source of it: most
        // big releases carry it, almost no indies do. Taken whenever Steam answers, so a score Steam
        // has dropped goes too. IGDB's aggregated_rating used to fill the gaps and was dropped -- it
        // is IGDB's own average, and a badge reading IGDB on one game and Metacritic on the next
        // was two scales passing for one.
        var metacritic = d.TryGetProperty("metacritic", out var mc) ? JsonNum.Int(mc, "score") : null;
        g.CriticScore = metacritic;
        g.CriticSource = metacritic is null ? null : "Metacritic";

        // Steam carries the age boards itself, PEGI among them, keyed by app id and with no key
        // and no title matching -- which makes it a better source for this than IGDB ever was.
        // Read before the early return below, for the same reason as the two above: it is worth
        // having whether or not the service answered.
        if (g.PegiRating is null && SteamPegi(d) is { } pegi) g.PegiRating = pegi;
        if (g.EsrbRating is null && SteamEsrb(d) is { } esrb) g.EsrbRating = esrb;
        if (g.EsrbDescriptors.Count == 0) g.EsrbDescriptors = SteamDescriptors(d, "esrb");
        if (g.PegiDescriptors.Count == 0) g.PegiDescriptors = SteamDescriptors(d, "pegi");

        // The trailer, from the same call and whether or not the service answered: Steam's is the
        // one worth having, a plain file with no player around it. A changed URL drops the cached
        // file's name with it -- Steam re-cuts trailers now and then, and the name on disk is keyed
        // on the URL it came from, so an old name would play an old cut. Steam saying "none" only
        // takes away a Steam trailer: a YouTube one from IGDB is not Steam's to remove. Nothing is
        // dropped for a probe that failed to answer: an outage is not "no trailer".
        var (movies, sure) = await SteamMoviesAsync(d, onlyHighlight: lite, ct);
        var trailer = movies.FirstOrDefault(m => m.Highlight).Url ?? movies.FirstOrDefault().Url;
        if (sure)
        {
            if (trailer is not null) SetTrailer(g, trailer);
            else if (IsSteamTrailer(g.TrailerUrl)) SetTrailer(g, null);
        }

        // The gallery: every film, then the screenshots. Steam's list wins over IGDB's whenever
        // it has anything, and is left alone when it has nothing -- not for the lite pass, whose
        // games have no page worth dressing yet.
        if (!lite)
        {
            var media = movies
                .Select(m => new MediaItem { Kind = "video", Url = m.Url!, Thumb = m.Thumb, Name = m.Name })
                .ToList();
            media.AddRange(SteamScreenshots(d));
            if (media.Count > 0) g.Media = media;
        }

        // The rest is Steam's only when the service did not answer at all. Keyed on that rather
        // than on whether each field happens to be empty: a field left over from a previous run is
        // also non-empty, and testing emptiness would make stale values impossible to correct.
        if (serviceAnswered) return (true, Str("header_image"));

        g.Description = Clean(Str("short_description"));
        g.Developer = First("developers");
        g.Publisher = First("publishers");

        if (d.TryGetProperty("genres", out var genres) && genres.ValueKind == JsonValueKind.Array)
            g.Genres = genres.EnumerateArray()
                .Select(x => x.TryGetProperty("description", out var n2) ? n2.GetString() : null)
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Select(x => x!)
                .ToList();

        if (d.TryGetProperty("release_date", out var rel)
            && rel.TryGetProperty("date", out var date) && date.ValueKind == JsonValueKind.String)
        {
            var text = date.GetString();
            g.ReleaseDate = string.IsNullOrWhiteSpace(text) ? null : text;
        }

        return (true, Str("header_image"));
    }

    /// <summary>
    /// One appdetails call: the app's `data` object, with the document that owns it (dispose it),
    /// or null. Callers pace the store themselves.
    ///
    /// The answer is an object with one entry, and the entry is NOT reliably keyed by the app id
    /// asked for: with `basic` in the filter list, an app that has DLC comes back keyed by one of
    /// the DLC ids -- Hollow Knight under "916000", Portal 2 under "323180" -- with the right
    /// steam_appid inside. Looked up by name, every game with DLC read as "not found" and got no
    /// facts, no rating and no trailer. So the entry is taken by the id when it is there and as
    /// the single entry otherwise, and the app id inside is checked instead.
    /// </summary>
    private static async Task<(JsonDocument Doc, JsonElement Data)?> SteamAppDetailsAsync(string appId, string filters, CancellationToken ct)
    {
        var url = $"https://store.steampowered.com/api/appdetails?appids={appId}&l=english&filters={filters}";
        using var res = await Http.GetAsync(url, ct);
        if (!res.IsSuccessStatusCode) return null;

        var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync(ct));
        try
        {
            if (doc.RootElement.ValueKind != JsonValueKind.Object) { doc.Dispose(); return null; }
            JsonElement entry;
            if (!doc.RootElement.TryGetProperty(appId, out entry))
            {
                var only = doc.RootElement.EnumerateObject().Select(p => (JsonElement?)p.Value).FirstOrDefault();
                if (only is null) { doc.Dispose(); return null; }
                entry = only.Value;
            }
            if (!entry.TryGetProperty("success", out var ok) || ok.ValueKind != JsonValueKind.True) { doc.Dispose(); return null; }
            if (!entry.TryGetProperty("data", out var d) || d.ValueKind != JsonValueKind.Object) { doc.Dispose(); return null; }
            if (JsonNum.Long(d, "steam_appid") is { } inside && inside.ToString() != appId)
            {
                Log.Info($"Metadata: appdetails for {appId} answered with app {inside}; ignored");
                doc.Dispose();
                return null;
            }
            return (doc, d);
        }
        catch { doc.Dispose(); throw; }
    }

    /// <summary>
    /// The gallery for one game, on demand. The page asks for it when a game's page opens with
    /// nothing in the strip: an uninstalled game got the lite pass, which stops at the trailer,
    /// because the list rides in every state push and five hundred catalogue games' galleries
    /// would be most of the payload -- so a gallery is fetched only for a game somebody opens.
    /// Steam's page first (by app id, or by the same exact-title search the pass uses; never for
    /// a ROM), IGDB for the rest. Returns true when the game now has one.
    /// </summary>
    public async Task<bool> FetchMediaAsync(Game g, AppSettings settings, CancellationToken ct = default)
    {
        if (g.Media.Count > 0) return false;

        var appId = SteamAppId(g);
        if (appId is null && !g.Emulated)
        {
            await PaceStoreAsync(ct);
            appId = await new SteamSearchClient(Http).FindAppIdAsync(g.Title, ct);
        }
        if (appId is not null)
        {
            await PaceStoreAsync(ct);
            var details = await SteamAppDetailsAsync(appId, "movies,screenshots", ct);
            if (details is { } found)
            {
                using var doc = found.Doc;
                var (movies, _) = await SteamMoviesAsync(found.Data, onlyHighlight: false, ct);
                var media = movies
                    .Select(m => new MediaItem { Kind = "video", Url = m.Url!, Thumb = m.Thumb, Name = m.Name })
                    .ToList();
                media.AddRange(SteamScreenshots(found.Data));
                if (media.Count > 0) { g.Media = media; return true; }
            }
        }

        IFactsProvider? facts = IgdbClient.IsConfigured(settings.IgdbClientId, settings.IgdbClientSecret)
            ? new IgdbClient(Http, settings.IgdbClientId, settings.IgdbClientSecret)
            : null;
        var endpoint = string.IsNullOrWhiteSpace(settings.MetadataEndpoint) ? MetadataProxyClient.DefaultEndpoint : settings.MetadataEndpoint;
        if (facts is null && MetadataProxyClient.IsConfigured(endpoint)) facts = new MetadataProxyClient(Http, endpoint);
        if (facts is null) return false;

        var platforms = g.Emulated ? EmulatedPlatforms.Find(g.PlatformId)?.IgdbIds : null;
        var hit = await facts.FindAsync(g.Title, appId, platforms, ct);
        if (hit is null) return false;
        var fromIgdb = IgdbMedia(hit);
        if (fromIgdb.Count == 0) return false;
        g.Media = fromIgdb;
        return true;
    }

    /// <summary>At most this many films and this many screenshots go into a gallery.</summary>
    private const int MaxMovies = 6, MaxScreenshots = 12;

    /// <summary>
    /// The app's films, out of appdetails' `movies`, each as a URL a plain &lt;video&gt; can play:
    /// (list, sure). `sure` is false when the CDN could not be asked about one of them, so the
    /// caller keeps whatever trailer it had. With `onlyHighlight` only the store's headline movie
    /// is resolved -- the lite pass has no gallery to fill and no reason to probe five films.
    ///
    /// Steam marks one movie per app as the highlight, the one the store page opens on. What an
    /// entry carries has changed under us: it used to name webm and mp4 files at "480" and "max",
    /// and since 2025 it names only DASH and HLS manifests, which a plain &lt;video&gt; cannot play.
    /// The progressive files are still on the CDN, at a path keyed on the MOVIE id -- checked
    /// Sept 2026 across nine games from Portal (2007) to Black Myth: Wukong -- so the URL is built
    /// from the id and probed with a one-byte range request before it is believed. The probe is
    /// what catches the one shape a missing file takes: a 200 with no body, which Portal 2's
    /// oldest movie answers for movie_max.mp4 while movie480.mp4 is there. The legacy keys are
    /// still honoured first when an entry has them. mp4 over webm because that is what every
    /// decoder does in hardware, and the largest size first: this fills a television. http:// is
    /// upgraded, since the page is served over https and the browser would refuse the mix.
    /// </summary>
    private static async Task<(List<(string? Url, string? Thumb, string? Name, bool Highlight)> Movies, bool Sure)>
        SteamMoviesAsync(JsonElement data, bool onlyHighlight, CancellationToken ct)
    {
        var list = new List<(string? Url, string? Thumb, string? Name, bool Highlight)>();
        if (!data.TryGetProperty("movies", out var movies) || movies.ValueKind != JsonValueKind.Array)
            return (list, true);

        var entries = movies.EnumerateArray().Where(m => m.ValueKind == JsonValueKind.Object).Take(MaxMovies).ToList();
        if (entries.Count == 0) return (list, true);
        if (onlyHighlight)
        {
            var pick = entries.FirstOrDefault(m => m.TryGetProperty("highlight", out var h) && h.ValueKind == JsonValueKind.True);
            entries = new List<JsonElement> { pick.ValueKind == JsonValueKind.Object ? pick : entries[0] };
        }

        var sure = true;
        foreach (var movie in entries)
        {
            var (url, reachable) = await SteamMovieUrlAsync(movie, ct);
            if (!reachable) sure = false;
            if (url is null) continue;
            list.Add((url, StrOf(movie, "thumbnail") is { } t ? Https(t) : null, StrOf(movie, "name"),
                movie.TryGetProperty("highlight", out var hl) && hl.ValueKind == JsonValueKind.True));
        }
        return (list, sure);
    }

    /// <summary>One movie's playable URL, or null; `Reachable` is false when the CDN did not answer.</summary>
    private static async Task<(string? Url, bool Reachable)> SteamMovieUrlAsync(JsonElement movie, CancellationToken ct)
    {
        foreach (var (format, size) in new[] { ("mp4", "max"), ("mp4", "480"), ("webm", "max"), ("webm", "480") })
        {
            if (movie.TryGetProperty(format, out var f) && f.ValueKind == JsonValueKind.Object
                && f.TryGetProperty(size, out var u) && u.ValueKind == JsonValueKind.String
                && u.GetString() is { Length: > 0 } legacy)
                return (Https(legacy), true);
        }

        var id = JsonNum.Long(movie, "id");
        if (id is null) return (null, true);
        var reachable = false;
        foreach (var file in new[] { "movie_max.mp4", "movie480.mp4" })
        {
            var url = $"https://video.akamai.steamstatic.com/store_trailers/{id}/{file}";
            var probe = await ProbeVideoAsync(url, ct);
            if (probe is null) continue;        // could not ask
            reachable = true;
            if (probe == true) return (url, true);
        }
        return (null, reachable);
    }

    /// <summary>The store page's screenshots: the 1920x1080 file and its 600x338 thumbnail.</summary>
    private static List<MediaItem> SteamScreenshots(JsonElement data)
    {
        var list = new List<MediaItem>();
        if (!data.TryGetProperty("screenshots", out var shots) || shots.ValueKind != JsonValueKind.Array) return list;
        foreach (var s in shots.EnumerateArray().Take(MaxScreenshots))
        {
            if (StrOf(s, "path_full") is not { } full) continue;
            list.Add(new MediaItem { Kind = "image", Url = Https(full), Thumb = Https(StrOf(s, "path_thumbnail") ?? full) });
        }
        return list;
    }

    private static string? StrOf(JsonElement e, string key) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static string Https(string url) =>
        url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ? "https://" + url[7..] : url;

    /// <summary>The gallery IGDB can offer: its videos (YouTube, with YouTube's own poster frame)
    /// and its screenshots. Only the fallback for a game Steam has no page for.</summary>
    private static List<MediaItem> IgdbMedia(IgdbGame hit)
    {
        var list = new List<MediaItem>();
        foreach (var v in hit.Videos.Take(MaxMovies))
            list.Add(new MediaItem
            {
                Kind = "video", Url = "https://www.youtube.com/watch?v=" + v.Id,
                Thumb = $"https://i.ytimg.com/vi/{v.Id}/hqdefault.jpg", Name = v.Name,
            });
        foreach (var s in hit.Screenshots.Take(MaxScreenshots))
            list.Add(new MediaItem { Kind = "image", Url = s, Thumb = s });
        return list;
    }

    /// <summary>Points the game at a trailer, or at none. The cached copy's name goes with any
    /// change, because it is keyed on the URL it was fetched from. Returns true when it changed.</summary>
    private static bool SetTrailer(Game g, string? url)
    {
        if (url == g.TrailerUrl) return false;
        g.TrailerUrl = url;
        g.TrailerFile = null;
        return true;
    }

    private static bool IsYouTube(string? url) =>
        url is not null && Regex.IsMatch(url, @"^https?://(www\.)?(youtube\.com|youtu\.be)/", RegexOptions.IgnoreCase);

    private static bool IsSteamTrailer(string? url) =>
        url is not null && url.Contains("steamstatic.com/", StringComparison.OrdinalIgnoreCase);

    /// <summary>True when the URL answers a range request with a slice of video, false when it
    /// answers with something else (Steam's "missing" is a 200 with no body), null when the CDN
    /// could not be reached at all.</summary>
    private static async Task<bool?> ProbeVideoAsync(string url, CancellationToken ct)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(0, 0);
            using var res = await Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
            if (res.StatusCode != HttpStatusCode.PartialContent) return false;
            var type = res.Content.Headers.ContentType?.MediaType ?? "";
            return type.StartsWith("video/", StringComparison.OrdinalIgnoreCase);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            Log.Info($"Metadata: trailer probe {url} failed: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// The PEGI age out of Steam's own ratings block, or null.
    ///
    /// `ratings` holds one entry per board -- esrb, pegi, usk, cero, oflc and a dozen more -- so
    /// the board is named rather than guessed at, and only PEGI's own ages are accepted. The
    /// rating arrives as a STRING ("18"), and a few boards put letters there ("m", "r18", "z"),
    /// so anything that is not one of PEGI's five numbers is not a PEGI age and is dropped.
    ///
    /// Plenty of games have no entry at all: a European rating only exists if somebody paid for
    /// one, which most indies have not. That is a fact about the game, not a failure here.
    /// </summary>
    private static int? SteamPegi(JsonElement data)
    {
        if (!data.TryGetProperty("ratings", out var ratings) || ratings.ValueKind != JsonValueKind.Object)
            return null;
        if (!ratings.TryGetProperty("pegi", out var pegi) || pegi.ValueKind != JsonValueKind.Object)
            return null;
        if (!pegi.TryGetProperty("rating", out var r) || r.ValueKind != JsonValueKind.String)
            return null;

        return int.TryParse(r.GetString(), out var age) && age is 3 or 7 or 12 or 16 or 18
            ? age : null;
    }

    /// <summary>
    /// The ESRB rating as the board prints it, or null. Steam sends it lower case and without the
    /// plus ("e10"), so it is mapped rather than upper-cased: "E10+" is the name of the rating and
    /// "E10" is not.
    /// </summary>
    private static string? SteamEsrb(JsonElement data) =>
        Board(data, "esrb") is { } r
            ? r.ToLowerInvariant() switch
            {
                "e" => "E", "e10" => "E10+", "t" => "T",
                "m" => "M", "ao" => "AO", "rp" => "RP",
                _ => null,   // a value we do not recognise is not a rating we can draw
            }
            : null;

    /// <summary>
    /// Why one board rated the game what it did, in that board's own words. Kept per board, since
    /// the page shows whichever board the user picked and the words have to agree with the mark --
    /// PEGI's descriptors under an ESRB logo would attribute one board's judgement to another.
    /// </summary>
    private static List<string> SteamDescriptors(JsonElement data, string board)
    {
        var text = Descriptors(data, board);
        if (text is null) return new List<string>();

        return text.Split('\n')
            // ESRB writes the list as a sentence, so the last line arrives as "and Strong
            // Language". As a chip of its own that reads like a mistake.
            .Select(s => Regex.Replace(s.Trim(), @"^and\s+", "", RegexOptions.IgnoreCase))
            .Where(s => s.Length > 0)
            .Take(8)
            .ToList();
    }

    private static string? Board(JsonElement data, string board) =>
        data.TryGetProperty("ratings", out var ratings) && ratings.ValueKind == JsonValueKind.Object
        && ratings.TryGetProperty(board, out var b) && b.ValueKind == JsonValueKind.Object
        && b.TryGetProperty("rating", out var r) && r.ValueKind == JsonValueKind.String
            ? r.GetString() : null;

    private static string? Descriptors(JsonElement data, string board) =>
        data.TryGetProperty("ratings", out var ratings) && ratings.ValueKind == JsonValueKind.Object
        && ratings.TryGetProperty(board, out var b) && b.ValueKind == JsonValueKind.Object
        && b.TryGetProperty("descriptors", out var d) && d.ValueKind == JsonValueKind.String
            ? d.GetString() : null;

    // ---------- Everything else ----------

    /// <summary>
    /// The shared service: IGDB for the facts, SteamGridDB for the art. Asked first, and asked by
    /// Steam app id whenever there is one, which is what makes asking it first safe -- an id
    /// lookup cannot come back with a different game the way a title search can.
    ///
    /// Either half may be absent, in which case that half is simply missing and Steam fills it.
    /// </summary>
    /// <summary>The service's pass: (anything written, facts answered, IGDB's YouTube trailer id).</summary>
    private async Task<(bool Touched, bool Facts, string? VideoId)> EnrichElsewhereAsync(Game g,
        IFactsProvider? facts, IArtProvider? art, string? appId, IReadOnlyList<int>? platforms,
        HashSet<Slot> filled, CancellationToken ct)
    {
        var any = false;
        var gotFacts = false;
        IgdbGame? factsHit = null;

        if (facts is not null && !facts.Unavailable)
        {
            var hit = await facts.FindAsync(g.Title, appId, platforms, ct);
            if (hit is not null)
            {
                g.Description = Clean(hit.Summary);
                g.Developer = hit.Developer;
                g.Publisher = hit.Publisher;
                if (hit.Genres.Count > 0) g.Genres = hit.Genres;
                g.ReleaseDate = hit.Released?.ToString("MMM d, yyyy");
                g.PegiRating = hit.PegiRating;
                g.MetadataSource = "igdb";
                // The gallery, for a game Steam has no page for; Steam's replaces it when Steam
                // runs after this and has anything of its own.
                var media = IgdbMedia(hit);
                if (media.Count > 0) g.Media = media;
                factsHit = hit;
                any = true;
                gotFacts = true;
            }
        }

        // The art provider before IGDB's own pictures, because every source is now first-wins and
        // the order therefore has to run best to worst. SteamGridDB publishes art in the shapes a
        // launcher asks for; IGDB's cover is an afterthought and its artworks are whatever was
        // uploaded, which is why they are last.
        if (art is not null && !art.Unavailable)
        {
            var found = await art.FindArtAsync(g.Title, appId, ct);
            if (found is not null)
            {
                if (found.Portrait is { } p) any |= await StoreRemoteAsync(g, Slot.Cover, p, filled, ct);
                if (found.Tile is { } t) any |= await StoreRemoteAsync(g, Slot.Tile, t, filled, ct);
                if (found.Hero is { } h) any |= await StoreRemoteAsync(g, Slot.Hero, h, filled, ct);
                if (found.Logo is { } l) any |= await StoreRemoteAsync(g, Slot.Logo, l, filled, ct);
            }
        }

        if (factsHit is { } igdb)
        {
            if (igdb.CoverUrl is { } cover) any |= await StoreRemoteAsync(g, Slot.Cover, cover, filled, ct);
            if (igdb.ArtworkUrl is { } wide)
            {
                // Only when there is no hero. Fits can tell that an artwork is 16:9 and cannot
                // tell that it is any good, and IGDB's artworks are user uploads in no particular
                // order -- Persona 3's first one is a blue diagonal, two floating leaves and 31 KB
                // of JPEG, against 873 KB of key art in Steam's hero. Steam's library_hero is
                // curated and is the picture the store itself shows, so it wins whenever it
                // exists; this slot is for the games that have nothing else.
                if (!filled.Contains(Slot.Hero))
                    any |= await StoreRemoteAsync(g, Slot.Backdrop, wide, filled, ct);
                // And as the tile of last resort, for a game with no capsule and nothing from
                // SteamGridDB. Fits keeps anything squarer than 1.3:1 out of a landscape box.
                any |= await StoreRemoteAsync(g, Slot.Tile, wide, filled, ct);
            }
        }

        return (any, gotFacts, factsHit?.VideoId);
    }

    // ---------- Art plumbing ----------

    /// <summary>
    /// Downloads one picture into a slot, unless that slot is already settled. Refused, too, if
    /// the picture turns out to be the wrong shape for it -- see Fits.
    ///
    /// The `filled` check is not a detail. Without it this method wrote its slot unconditionally
    /// while only the Steam CDN loop consulted the set, so "Steam's capsule gets first refusal"
    /// was true right up until the service ran two lines later and overwrote it. Every caller now
    /// goes through the same gate, in one priority order, and the first source to fill a slot
    /// keeps it -- which is also what makes the art stop changing between passes.
    /// </summary>
    private static async Task<bool> StoreRemoteAsync(Game g, Slot slot, string url,
        HashSet<Slot> filled, CancellationToken ct, string tag = Service)
    {
        if (filled.Contains(slot)) return false;

        var name = ArtPrefix(g) + tag + SlotSuffix(slot) + ExtensionOf(url);
        var dest = Path.Combine(Paths.CoversDir, name);
        if (!await DownloadAsync(url, dest, ct)) return false;

        if (!Fits(slot, dest))
        {
            try { File.Delete(dest); } catch { /* a cache file; leaving it costs nothing */ }
            Unassign(g, slot, name);
            return false;
        }

        // Assign returns false when the name has not changed, but the bytes on disk are new, so
        // the slot still counts as filled.
        Assign(g, slot, name);
        filled.Add(slot);
        return true;
    }

    private static string SlotSuffix(Slot slot) => slot switch
    {
        Slot.Tile => "_tile",
        Slot.Hero => "_hero",
        Slot.Backdrop => "_bg",
        Slot.Logo => "_logo",
        _ => "_cover",
    };

    /// <summary>
    /// The shapes a slot will accept, as (min, max) aspect.
    ///
    /// Written because IGDB's artworks are whatever somebody uploaded: taking the first of them
    /// for the backdrop put a 1080x1080 square behind DREDGE's whole screen and an 810x1080
    /// portrait behind Hollow Knight's. The slot is defined as 16:9 key art and the launcher was
    /// treating it as "a wide picture, probably". A square is not key art, and the fix is to say
    /// so here rather than to add another fallback in the page.
    ///
    /// Generous at the edges on purpose. These reject art that is the wrong KIND of picture, not
    /// art that is a few percent off -- a 2:1 promotional still is still a backdrop.
    /// </summary>
    private static (double Min, double Max) Bounds(Slot slot) => slot switch
    {
        Slot.Cover => (0.0, 0.95),      // portrait box art; a landscape one is somebody else's slot
        Slot.Tile => (1.30, 2.60),      // 1.75 capsule through 2.14 header, and nothing squarer
        Slot.Hero => (2.40, 5.00),      // the 3.1:1 band
        Slot.Backdrop => (1.60, 2.10),  // 16:9 key art, which is the only thing this slot is for
        // A WORDMARK, which is wider than it is tall by definition. Anything squarer is a logo
        // mark rather than the title set as art, and the detail page hangs this where the title
        // goes -- so a square one arrives as a small blob in the corner of a wide box. Rejecting
        // it falls back to the text title, which is the better of the two.
        _ => (1.20, 10.0),
    };

    /// <summary>
    /// True when the file is a shape this slot can use. Art we cannot measure is accepted: a
    /// format we do not decode is not evidence of a bad picture, and refusing it would throw away
    /// every .webp SteamGridDB serves.
    /// </summary>
    private static bool Fits(Slot slot, string path)
    {
        if (ImageAspect(path) is not { } aspect) return true;
        var (min, max) = Bounds(slot);
        if (aspect >= min && aspect <= max) return true;
        Log.Info($"Metadata: {Path.GetFileName(path)} is {aspect:0.00}:1, " +
                 $"which is not a {slot} ({min:0.00}-{max:0.00}); discarded");
        return false;
    }

    /// <summary>Width over height, or null when the file cannot be decoded here.</summary>
    private static double? ImageAspect(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            var frame = System.Windows.Media.Imaging.BitmapFrame.Create(
                stream,
                System.Windows.Media.Imaging.BitmapCreateOptions.DelayCreation,
                System.Windows.Media.Imaging.BitmapCacheOption.None);
            return frame.PixelHeight > 0 ? (double)frame.PixelWidth / frame.PixelHeight : null;
        }
        catch { return null; }
    }

    /// <summary>
    /// The file extension a URL implies. SteamGridDB serves .png, .jpg and .webp from the same
    /// endpoint, and the name has to match the bytes for WebView2 to decode it.
    /// </summary>
    private static string ExtensionOf(string url)
    {
        var path = Uri.TryCreate(url, UriKind.Absolute, out var u) ? u.AbsolutePath : url;
        var ext = Path.GetExtension(path).ToLowerInvariant();
        return ext is ".jpg" or ".jpeg" or ".png" or ".webp" ? ext : ".jpg";
    }

    /// <summary>
    /// Points the game at art we just wrote. Returns true when it actually changed.
    ///
    /// Art the user picked by hand outranks anything we can download, in every slot -- the cover
    /// was the only one that could be picked when this was written, and the guard was on that one
    /// alone. Now that a tile can be chosen too, an enrich that moved it back would make the
    /// option look like it had not worked.
    /// </summary>
    private static bool Assign(Game g, Slot slot, string name)
    {
        switch (slot)
        {
            case Slot.Cover when g.CoverFile != name && !IsCustom(g.CoverFile):
                g.CoverFile = name; return true;
            case Slot.Tile when g.BannerFile != name && !IsCustom(g.BannerFile):
                g.BannerFile = name; return true;
            case Slot.Hero when g.HeroFile != name && !IsCustom(g.HeroFile):
                g.HeroFile = name; return true;
            case Slot.Backdrop when g.BackdropFile != name && !IsCustom(g.BackdropFile):
                g.BackdropFile = name; return true;
            case Slot.Logo when g.LogoFile != name && !IsCustom(g.LogoFile):
                g.LogoFile = name; return true;
            default: return false;
        }
    }

    /// <summary>
    /// Let go of a file we just deleted, if this game was pointing at it.
    ///
    /// A rejected download is removed from disk, and a previous pass may already have written its
    /// name into the game -- the same source, the same slot, the same filename, accepted back when
    /// nothing checked the shape. Left alone that is a library entry naming a file that is not
    /// there, which renders as no picture at all and survives every refresh, because a name only
    /// ever gets replaced by a download that succeeds.
    /// </summary>
    private static void Unassign(Game g, Slot slot, string name)
    {
        switch (slot)
        {
            case Slot.Cover when g.CoverFile == name: g.CoverFile = null; break;
            case Slot.Tile when g.BannerFile == name: g.BannerFile = null; break;
            case Slot.Hero when g.HeroFile == name: g.HeroFile = null; break;
            case Slot.Backdrop when g.BackdropFile == name: g.BackdropFile = null; break;
            case Slot.Logo when g.LogoFile == name: g.LogoFile = null; break;
        }
    }

    private static bool IsCustom(string? file) =>
        file is not null && file.StartsWith("custom_", StringComparison.Ordinal);

    /// <summary>The slots this game already has hand-picked art in.</summary>
    private static HashSet<Slot> CustomSlots(Game g)
    {
        var set = new HashSet<Slot>();
        if (IsCustom(g.CoverFile)) set.Add(Slot.Cover);
        if (IsCustom(g.BannerFile)) set.Add(Slot.Tile);
        if (IsCustom(g.HeroFile)) set.Add(Slot.Hero);
        if (IsCustom(g.BackdropFile)) set.Add(Slot.Backdrop);
        if (IsCustom(g.LogoFile)) set.Add(Slot.Logo);
        return set;
    }

    private static async Task<bool> DownloadAsync(string url, string dest, CancellationToken ct)
    {
        try
        {
            using var res = await Http.GetAsync(url, ct);
            if (!res.IsSuccessStatusCode) return false;
            var bytes = await res.Content.ReadAsByteArrayAsync(ct);
            if (bytes.Length < 1024) return false;   // Steam answers 200 with a placeholder for some ids

            // Write beside the target and move into place: a download interrupted halfway would
            // otherwise leave a truncated file that looks present and renders as a broken tile.
            var tmp = dest + ".part";
            await File.WriteAllBytesAsync(tmp, bytes, ct);
            File.Move(tmp, dest, overwrite: true);
            return true;
        }
        catch (Exception ex)
        {
            Log.Info($"Metadata: {Path.GetFileName(dest)} <- {url} failed: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Store descriptions are HTML: entities throughout and the occasional tag. Both render as
    /// literal noise in a text node, so strip the tags and decode the entities.
    /// </summary>
    private static string? Clean(string? html)
    {
        if (string.IsNullOrWhiteSpace(html)) return null;
        var text = Regex.Replace(html, "<[^>]+>", " ");
        text = WebUtility.HtmlDecode(text);
        text = Regex.Replace(text, @"\s+", " ").Trim();
        return text.Length == 0 ? null : text;
    }
}
