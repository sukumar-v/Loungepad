using System.Text.Json.Serialization;

namespace Loungepad.Models;

/// <summary>One achievement of one game, as its store describes it and as this account stands with it.</summary>
public class Achievement
{
    /// <summary>The store's own key: Steam's API name, Xbox's id, Epic's name, GOG's key, a
    /// RetroAchievements id. Stable across fetches, which is what lets two fetches be compared to
    /// find what was unlocked between them.</summary>
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string? Description { get; set; }
    /// <summary>The store keeps the description (and on some, the name) back until it is
    /// unlocked. The page prints a placeholder for a hidden one that is still locked, and can be
    /// asked to reveal them.</summary>
    public bool Hidden { get; set; }
    public bool Unlocked { get; set; }
    /// <summary>Local time. Null for a locked one, and for the odd unlocked one whose store did
    /// not say when.</summary>
    public DateTime? UnlockedAt { get; set; }
    /// <summary>The share of players who have it, 0-100, as the store reports it -- what the
    /// rarity chips are read off. Null where the store publishes none.</summary>
    public double? Percent { get; set; }
    public string? IconUrl { get; set; }
    /// <summary>The greyed version for the locked state, where the store draws one; the page
    /// desaturates the unlocked icon itself when there is none.</summary>
    public string? IconLockedUrl { get; set; }
    /// <summary>Xbox's gamerscore, RetroAchievements' points, Epic's XP. Null for Steam and GOG,
    /// which have no such number.</summary>
    public int? Score { get; set; }

    // ---- an edit by hand ----
    // Unlocked and UnlockedAt are always what everything reads -- the tiles, the sheet, the
    // stats, the post-session diff -- so an edit simply changes them, and the store's own answer
    // is set aside beside them for the edit to be undone. A fetch brings a fresh list with the
    // store's answer; AchievementStore.Put carries every edit over onto it. Written only when set.

    /// <summary>Unlocked/UnlockedAt were set by hand on the game's Achievements sheet. The Playnite
    /// import never edits a list (it only brings lists for games that have none).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool Edited { get; set; }
    /// <summary>What the store said before the edit, restored when it is undone.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool StoreUnlocked { get; set; }
    public DateTime? StoreUnlockedAt { get; set; }
}

/// <summary>Everything known about one game's achievements: the list, where it came from, and
/// when it was last asked for. One file per game under achievements\ (see AchievementStore).</summary>
public class GameAchievements
{
    public string GameId { get; set; } = "";
    /// <summary>steam | xbox | epic | gog | retro.</summary>
    public string Source { get; set; } = "";
    /// <summary>The id the source knows the game by, where it differs from ours: an Xbox title
    /// id, an Epic product id, a RetroAchievements game id. Cached so the next fetch skips the
    /// lookup that found it.</summary>
    public string? SourceGameId { get; set; }
    /// <summary>The source's own title for the game, for the page to say what it matched.</summary>
    public string? SourceName { get; set; }
    /// <summary>UTC. When the list was last fetched successfully.</summary>
    public DateTime FetchedAt { get; set; }
    /// <summary>Which build's fetch made the list (<see cref="CurrentVersion"/> at the time; 0 for
    /// one from before there was a number). Anything older is stale however recent FetchedAt is.</summary>
    public int Version { get; set; }

    /// <summary>
    /// Bump when a provider learns to answer differently, so every list it wrote before is asked
    /// again rather than held for its freshness window. 1: Steam stopped saving an empty list over
    /// a real one when the sign-in could not read it, and GOG stopped storing each achievement up
    /// to twenty times (Oct 6 2026) -- lists both had written that same day looked fresh.
    /// </summary>
    public const int CurrentVersion = 1;
    /// <summary>Why the last attempt gave nothing, in words for the page, or null. A failed
    /// attempt keeps the previous list.</summary>
    public string? Error { get; set; }
    public List<Achievement> Items { get; set; } = new();

    public int Total => Items.Count;
    public int Unlocked => Items.Count(a => a.Unlocked);
    public int Score => Items.Where(a => a.Unlocked).Sum(a => a.Score ?? 0);
    public int TotalScore => Items.Sum(a => a.Score ?? 0);
    public DateTime? LastUnlock => Items.Where(a => a.Unlocked && a.UnlockedAt is not null).Max(a => a.UnlockedAt);
}

/// <summary>What rides in every state push, per game: enough for a tile or a stats row, never the
/// list. The list is fetched when a page asks for it.</summary>
public class AchievementSummary
{
    public int Unlocked { get; set; }
    public int Total { get; set; }
    public int Score { get; set; }
    public int TotalScore { get; set; }
    public DateTime? LastUnlock { get; set; }
    public string Source { get; set; } = "";
    public DateTime FetchedAt { get; set; }
    public string? Error { get; set; }

    public static AchievementSummary Of(GameAchievements g) => new()
    {
        Unlocked = g.Unlocked, Total = g.Total, Score = g.Score, TotalScore = g.TotalScore,
        LastUnlock = g.LastUnlock, Source = g.Source, FetchedAt = g.FetchedAt, Error = g.Error,
    };
}
