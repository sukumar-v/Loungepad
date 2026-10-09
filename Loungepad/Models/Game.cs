namespace Loungepad.Models;

/// <summary>One entry in a game's gallery: a picture, or a film with a picture to stand for it.</summary>
public class MediaItem
{
    /// <summary>"image" or "video".</summary>
    public string Kind { get; set; } = "image";
    /// <summary>The full-size picture, or the film: an mp4 on Steam's CDN, or a YouTube watch URL.</summary>
    public string Url { get; set; } = "";
    /// <summary>A small 16:9 picture for the strip; for a film, the store's own poster frame.</summary>
    public string? Thumb { get; set; }
    /// <summary>The store's name for a film ("Launch Trailer"); null for a screenshot.</summary>
    public string? Name { get; set; }
}

public class Game
{
    public string Id { get; set; } = "";            // e.g. "steam:1091500", "manual:<guid>"
    public string Title { get; set; } = "";
    /// <summary>Steam | Epic | GOG | Xbox | Manual, or for a ROM the system's name -- "Super
    /// Nintendo", "PlayStation" -- which is what the library's platform filter lists it under.</summary>
    public string Platform { get; set; } = "Manual";
    public string? ExePath { get; set; }             // direct executable (GOG / Manual / Epic)

    // ---- Emulated games ----
    // A ROM found in one of the folders set up under Settings → Library. It launches through
    // the folder's emulator (see EmulatorLaunch), never through ExePath or LaunchUri.
    public bool Emulated { get; set; }
    /// <summary>One of EmulatedPlatforms.All -- "snes", "ps1" -- the stable id behind Platform.</summary>
    public string? PlatformId { get; set; }
    public string? RomPath { get; set; }
    public string? RomFolderId { get; set; }
    /// <summary>An emulator chosen for this game alone, under Manage. Null -- the normal case --
    /// means the ROM folder's emulator, resolved at launch, so changing the folder's choice moves
    /// every game in it that has not been given one of its own.</summary>
    public string? EmulatorId { get; set; }
    /// <summary>The title was typed in by hand, so a rescan must not put the file name's back.
    /// A ROM's title is a guess from its file name (see RomTitles) and the guess is what the
    /// metadata lookup runs on, so fixing it is the way to fix a game the lookup got wrong.</summary>
    public bool TitleEdited { get; set; }
    public string? Args { get; set; }
    public string? LaunchUri { get; set; }           // steam:// or com.epicgames.launcher:// URI
    public string? InstallDir { get; set; }          // used to find the game process after URI launches
    public string? CoverFile { get; set; }           // portrait art, file name inside <appdata>\covers
    public string? BannerFile { get; set; }          // ~16:9 tile art, the shape a landscape tile wants
    /// <summary>Wide backdrop art (~3:1). Deliberately separate from BannerFile: a hero has its
    /// subject off-centre with empty space either side, so centre-cropping one into a tile shows a
    /// slice of background rather than the game.</summary>
    public string? HeroFile { get; set; }
    /// <summary>16:9 key art, for anything that fills a whole screen. The hero is 3.1:1 and a
    /// screen is not, so one of the two has to be cropped or banded; this is the one that
    /// needs neither. Only IGDB publishes art of this shape.</summary>
    public string? BackdropFile { get; set; }
    /// <summary>Transparent wordmark, for a theme that wants the title as art rather than text.</summary>
    public string? LogoFile { get; set; }
    public long SizeBytes { get; set; }
    public double PlaytimeMinutes { get; set; }      // tracked by Loungepad sessions
    public int Sessions { get; set; }
    public DateTime? LastPlayed { get; set; }
    public bool Installed { get; set; } = true;
    public bool Manual { get; set; }
    public bool Favorite { get; set; }
    public bool Hidden { get; set; }                 // kept out of the library, listed under Hidden
    /// <summary>How to ask the game's own store to install it, for an entry that is owned but
    /// not on disk: steam://install, Epic's apps/…?action=install, goggalaxy://openGameView, the
    /// Microsoft Store's product page. Null for anything installed and for a store with no such
    /// route, and it is the only thing the Install button keys off.</summary>
    public string? InstallUri { get; set; }
    /// <summary>Xbox only: the package family name, which is what an installed Xbox game and a
    /// catalogue entry for the same game have in common when nothing else matches.</summary>
    public string? PackageFamilyName { get; set; }
    /// <summary>Xbox only: the title id Xbox Live files the game under, from the account's title
    /// history. It is what the achievements service asks by; an installed game the account has
    /// never played on any device has none yet.</summary>
    public string? XboxTitleId { get; set; }
    /// <summary>Epic only: the catalogue namespace ("sandbox") the game lives in, from the launcher's
    /// manifest or the library service. Achievements are keyed by it, not by the app name.</summary>
    public string? EpicNamespace { get; set; }
    /// <summary>Art the game's own store published, used as the last fallback after Steam and the
    /// service: Galaxy's GOG-hosted covers, the Microsoft Store's posters.</summary>
    public string? RemoteCoverUrl { get; set; }
    public string? RemoteBackdropUrl { get; set; }
    public bool PreferDirectLaunch { get; set; } // user chose an exe to bypass the store launcher
    /// <summary>
    /// The copy this game launches from when it is in more than one store -- picked under Manage
    /// → Launch with. The page groups entries by title into one tile, and among the copies of one
    /// game at most one carries this. Without it the tile takes an installed copy first, then the
    /// stores in order: Steam, Epic, GOG, Xbox.
    /// </summary>
    public bool PreferredEdition { get; set; }

    // ---- Fetched metadata ----
    // Everything below is filled in by MetadataService and is purely cosmetic: the launcher works
    // exactly the same with all of it null, which is what an offline first run looks like.

    /// <summary>One or two sentences. The store's short pitch, not the full description -- the
    /// detail page has room for about three lines and nobody reads a wall of text from a sofa.</summary>
    public string? Description { get; set; }
    public string? Developer { get; set; }
    public string? Publisher { get; set; }
    public List<string> Genres { get; set; } = new();
    /// <summary>Display string as the source gave it, e.g. "Feb 24, 2017". Not parsed: sources
    /// disagree on format and precision, and it is only ever shown, never sorted on.</summary>
    public string? ReleaseDate { get; set; }
    /// <summary>Metacritic score, 0-100, as Steam's store carries it, or null. Nothing else is
    /// ever put here: a game Metacritic has not scored, or that Steam does not sell, shows no
    /// score at all. Plenty of games -- most indies -- are not scored, so null is the normal case.</summary>
    public int? CriticScore { get; set; }
    /// <summary>"Metacritic" whenever there is a score. LibraryStore.Load drops a score carrying
    /// anything else, which is what older builds wrote for IGDB's own average.</summary>
    public string? CriticSource { get; set; }
    /// <summary>PEGI age, one of 3, 7, 12, 16, 18, or null.</summary>
    public int? PegiRating { get; set; }
    /// <summary>ESRB rating as it is printed -- "E", "E10+", "T", "M", "AO", "RP" -- or null.
    /// Which of the two the page shows is AppSettings.AgeRatingBoard.</summary>
    public string? EsrbRating { get; set; }
    /// <summary>Why ESRB rated it what it did -- "Blood and Gore", "Mild Lyrics" -- in ESRB's own
    /// words. Kept per board because the page prints them under that board's mark, and one board's
    /// reasons under the other's logo would put words in its mouth.</summary>
    public List<string> EsrbDescriptors { get; set; } = new();
    /// <summary>PEGI's reasons -- "Violence", "Bad Language", "In-game purchases".</summary>
    public List<string> PegiDescriptors { get; set; } = new();
    /// <summary>"full", "partial" or null. Worth surfacing in a couch launcher above almost
    /// anything else: it answers "can I actually play this from the sofa".</summary>
    public string? ControllerSupport { get; set; }
    /// <summary>The store trailer, as a URL the page can stream: Steam's highlight movie for the
    /// app, mp4 at the largest size published. Steam is the only source -- IGDB carries YouTube
    /// ids, which cannot be played inline -- so a game with no Steam listing has none.</summary>
    public string? TrailerUrl { get; set; }
    /// <summary>A copy of TrailerUrl on this PC, as a file name under the trailers cache, written
    /// by TrailerCache once a download completes and cleared whenever the URL changes or the file
    /// is evicted. The page plays this when it is set and streams the URL when it is not.</summary>
    public string? TrailerFile { get; set; }
    /// <summary>The store page's pictures and films, for the gallery on the game's page: Steam's
    /// screenshots and every movie, or IGDB's screenshots and videos for a game Steam has not got.
    /// Remote URLs only, never cached; fetched for installed games only, since the list rides in
    /// every state push and a catalogue of five hundred games nobody has opened would be most of
    /// the payload.</summary>
    public List<MediaItem> Media { get; set; } = new();
    /// <summary>Which provider answered, e.g. "steam". Also the flag for "we have tried this one".</summary>
    public string? MetadataSource { get; set; }
    /// <summary>When it was fetched, so a rescan does not re-hit the network for everything.
    /// Null means never tried.</summary>
    public DateTime? MetadataFetched { get; set; }
    /// <summary>What the app knew how to fetch when this entry was last filled in. A build that
    /// learned a new field or a better-shaped picture bumps MetadataService.FetchVersion, and
    /// everything stamped with an older one is fetched again -- otherwise a library sits on the
    /// old answers until the freshness window runs out, which is not what that window is for.</summary>
    public int MetadataVersion { get; set; }

    /// <summary>
    /// What each extension stored about this game, by extension id (see docs/ADDONS.md): a
    /// stamp and the data the extension returned, as it was. Kept across rescans by
    /// MergeScanned like every other fetched field. Null until an extension has looked.
    /// </summary>
    public Dictionary<string, ExtRecord>? Ext { get; set; }

    /// <summary>
    /// Whatever library.json holds that this build has no property for, kept and written back
    /// as it was. An older build run against the same data folder -- an updater test, a copy
    /// left on another drive -- used to read the file, drop every field it had never heard of
    /// and save: a 1.5.0 start in Sept 2026 wiped TrailerUrl, Media and the content descriptors
    /// from all 954 games, and because MetadataVersion survived, 1.6.0 never fetched them again.
    /// Only builds from this one on carry this, so it protects the next such run, not that one.
    /// </summary>
    [System.Text.Json.Serialization.JsonExtensionData]
    public Dictionary<string, System.Text.Json.JsonElement>? Unknown { get; set; }
}
