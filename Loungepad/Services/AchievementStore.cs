using System.IO;
using System.Text.Json;
using Loungepad.Models;

namespace Loungepad.Services;

/// <summary>
/// The achievement lists on disk, one JSON file per game under achievements\, all of them held in
/// memory once loaded. A library has a few hundred games with achievements and each list is a few
/// kilobytes, so the whole set is small; per-file so that one game's fetch rewrites one small
/// file rather than the lot, and so a corrupt file loses one game rather than every game.
///
/// The folder is a constructor argument so a harness can work in a scratch folder.
/// </summary>
public sealed class AchievementStore
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = false,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly object _gate = new();
    private readonly string _dir;
    private readonly Dictionary<string, GameAchievements> _sets = new(StringComparer.Ordinal);

    public AchievementStore(string? dir = null)
    {
        _dir = Path.Combine(dir ?? Paths.DataDir, "achievements");
    }

    public string Dir => _dir;

    /// <summary>"steam:1091500" is not a file name; this is what the file is called instead.</summary>
    public static string SafeName(string gameId)
    {
        var chars = gameId.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' or '.' ? c : '_').ToArray();
        return new string(chars);
    }

    public void Load()
    {
        lock (_gate)
        {
            _sets.Clear();
            try
            {
                if (!Directory.Exists(_dir)) return;
                foreach (var f in Directory.GetFiles(_dir, "*.json"))
                {
                    try
                    {
                        var set = JsonSerializer.Deserialize<GameAchievements>(File.ReadAllText(f));
                        if (set is not { GameId.Length: > 0 }) continue;
                        // One entry per achievement. GOG's paging once stored each up to twenty
                        // times; a list kept from then, when the store will not answer to replace
                        // it, would otherwise show every row twenty times and count 100 of 1140.
                        if (set.Items.Select(a => a.Id).Distinct(StringComparer.Ordinal).Count() != set.Items.Count)
                            set.Items = set.Items.GroupBy(a => a.Id, StringComparer.Ordinal).Select(g => g.First()).ToList();
                        _sets[set.GameId] = set;
                    }
                    catch (Exception ex) { Log.Info($"Achievements: {Path.GetFileName(f)} unreadable: {ex.Message}"); }
                }
            }
            catch (Exception ex) { Log.Info($"Achievements load failed: {ex.Message}"); }
        }
    }

    public GameAchievements? Get(string gameId)
    {
        lock (_gate) return _sets.TryGetValue(gameId, out var s) ? s : null;
    }

    public List<GameAchievements> All()
    {
        lock (_gate) return _sets.Values.ToList();
    }

    public Dictionary<string, AchievementSummary> Summaries()
    {
        lock (_gate) return _sets.Values.ToDictionary(s => s.GameId, AchievementSummary.Of, StringComparer.Ordinal);
    }

    /// <summary>
    /// Stores a list, carrying every edit made by hand on the previous one over onto it: a fetch
    /// brings the store's answer, and the edit is what the user said instead. The fresh item keeps
    /// the store's answer aside (StoreUnlocked) for the edit to be undone. Done in place on the
    /// incoming items, so the caller's copy -- which the post-session diff compares -- is the
    /// edited one too. A failed fetch hands back the previous list itself, whose items already
    /// carry their edits; the reference checks leave those alone.
    /// </summary>
    public void Put(GameAchievements set)
    {
        lock (_gate)
        {
            if (_sets.TryGetValue(set.GameId, out var prev) && !ReferenceEquals(prev, set))
            {
                var edits = prev.Items.Where(a => a.Edited).GroupBy(a => a.Id).ToDictionary(g => g.Key, g => g.First());
                foreach (var a in set.Items)
                {
                    if (!edits.TryGetValue(a.Id, out var e) || ReferenceEquals(a, e)) continue;
                    a.StoreUnlocked = a.Unlocked;
                    a.StoreUnlockedAt = a.UnlockedAt;
                    a.Unlocked = e.Unlocked;
                    a.UnlockedAt = e.UnlockedAt;
                    a.Edited = true;
                }
            }
            _sets[set.GameId] = set;
            Write(set);
        }
    }

    private void Write(GameAchievements set)
    {
        try
        {
            Directory.CreateDirectory(_dir);
            File.WriteAllText(Path.Combine(_dir, SafeName(set.GameId) + ".json"), JsonSerializer.Serialize(set, JsonOpts));
        }
        catch (Exception ex) { Log.Info($"Achievements: {set.GameId} not written: {ex.Message}"); }
    }

    /// <summary>
    /// Sets one achievement's state by hand. An edit that lands exactly on what the store says is
    /// no edit at all and is dropped, so "mark unlocked" on something the store already has
    /// unlocked at that time leaves nothing behind to carry over. False when there is no such item.
    /// </summary>
    public bool Edit(string gameId, string achievementId, bool unlocked, DateTime? at)
    {
        lock (_gate)
        {
            if (!_sets.TryGetValue(gameId, out var set)) return false;
            var a = set.Items.FirstOrDefault(x => x.Id == achievementId);
            if (a is null) return false;
            if (!a.Edited) { a.StoreUnlocked = a.Unlocked; a.StoreUnlockedAt = a.UnlockedAt; }
            a.Unlocked = unlocked;
            a.UnlockedAt = unlocked ? at : null;
            a.Edited = true;
            if (a.Unlocked == a.StoreUnlocked && a.UnlockedAt == a.StoreUnlockedAt) Restore(a);
            Write(set);
            return true;
        }
    }

    /// <summary>Puts one achievement back to what the store says.</summary>
    public bool Unedit(string gameId, string achievementId)
    {
        lock (_gate)
        {
            if (!_sets.TryGetValue(gameId, out var set)) return false;
            var a = set.Items.FirstOrDefault(x => x.Id == achievementId && x.Edited);
            if (a is null) return false;
            Restore(a);
            Write(set);
            return true;
        }
    }

    private static void Restore(Achievement a)
    {
        a.Unlocked = a.StoreUnlocked;
        a.UnlockedAt = a.StoreUnlockedAt;
        a.Edited = false;
        a.StoreUnlocked = false;
        a.StoreUnlockedAt = null;
    }

    public void Remove(string gameId)
    {
        lock (_gate)
        {
            _sets.Remove(gameId);
            try { File.Delete(Path.Combine(_dir, SafeName(gameId) + ".json")); } catch { /* already gone */ }
        }
    }

    /// <summary>The ids of the achievements unlocked in <paramref name="now"/> that were not in
    /// <paramref name="before"/>: what a session unlocked, for the notification after it.</summary>
    public static List<Achievement> NewlyUnlocked(GameAchievements? before, GameAchievements now)
    {
        var had = before is null ? new HashSet<string>() : before.Items.Where(a => a.Unlocked).Select(a => a.Id).ToHashSet();
        // A first fetch has nothing to compare against, and everything already unlocked years ago
        // would read as new: nothing is announced on it. Nor on a list that was empty, which is the
        // same thing: the Steam sign-in once saved an empty list over every Steam game's real one,
        // and the first fetch after that announced all of it.
        if (before is null || before.Items.Count == 0) return new List<Achievement>();
        return now.Items.Where(a => a.Unlocked && !had.Contains(a.Id)).ToList();
    }
}
