using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using Loungepad.Models;

namespace Loungepad.Services;

/// <summary>
/// The play sessions on disk: activity.json for the list, and one small file per session under
/// activity\ for its readings.
///
/// Two files rather than one because the two are read at different times. The list rides with
/// the launcher from start to finish -- every stats screen is built from it -- and it is a few
/// hundred bytes a session. The readings are only ever opened for one session at a time, when
/// somebody asks to see that evening's frame rate, and at a reading every five seconds a long
/// session is a few hundred of them. Kept in one file the list would be megabytes parsed on
/// every start for a view that is opened once a month.
///
/// The folder is a constructor argument, not Paths.DataDir, so a harness can record into a
/// scratch folder without touching the real history.
/// </summary>
public sealed class ActivityStore
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = false,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>The most readings a session file keeps. Beyond it, neighbouring readings are
    /// averaged in pairs until it fits: a chart is a few hundred pixels wide, and a fourteen-hour
    /// session at five seconds is ten thousand points nobody can tell apart from a thousand.</summary>
    public const int MaxSamplesPerSession = 720;

    private readonly object _gate = new();
    private readonly string _file;
    private readonly string _detailDir;
    private List<PlaySession> _sessions = new();

    public ActivityStore(string? dir = null)
    {
        var root = dir ?? Paths.DataDir;
        _file = Path.Combine(root, "activity.json");
        _detailDir = Path.Combine(root, "activity");
    }

    public void Load()
    {
        lock (_gate)
        {
            try
            {
                if (File.Exists(_file))
                {
                    var data = JsonSerializer.Deserialize<ActivityFile>(File.ReadAllText(_file));
                    _sessions = data?.Sessions ?? new List<PlaySession>();
                }
            }
            catch (Exception ex)
            {
                Log.Info($"Activity load failed, starting empty: {ex.Message}");
                _sessions = new List<PlaySession>();
            }
            // Oldest first, which is the order every aggregate walks them in.
            _sessions.Sort((a, b) => a.Start.CompareTo(b.Start));
        }
    }

    private void Save()
    {
        lock (_gate)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_file)!);
                File.WriteAllText(_file, JsonSerializer.Serialize(new ActivityFile { Sessions = _sessions }, JsonOpts));
            }
            catch (Exception ex) { Log.Info($"Activity save failed: {ex.Message}"); }
        }
    }

    /// <summary>A copy, oldest first.</summary>
    public List<PlaySession> All()
    {
        lock (_gate) return _sessions.ToList();
    }

    public List<PlaySession> ForGame(string gameId)
    {
        lock (_gate) return _sessions.Where(s => s.GameId == gameId).ToList();
    }

    public PlaySession? Find(string id)
    {
        lock (_gate) return _sessions.FirstOrDefault(s => s.Id == id);
    }

    public static string NewId() => Convert.ToHexString(RandomNumberGenerator.GetBytes(6)).ToLowerInvariant();

    /// <summary>Records a finished session. The readings are thinned to the cap and written to
    /// their own file; the averages on the row are computed here so every writer agrees.</summary>
    public void Add(PlaySession session, List<ActivitySample>? samples)
    {
        if (samples is { Count: > 0 })
        {
            Summarise(session, samples);
            var thinned = Thin(samples, MaxSamplesPerSession);
            session.Samples = thinned.Count;
            try
            {
                Directory.CreateDirectory(_detailDir);
                File.WriteAllText(Path.Combine(_detailDir, session.Id + ".json"),
                    JsonSerializer.Serialize(new SessionDetail { Id = session.Id, Samples = thinned }, JsonOpts));
            }
            catch (Exception ex)
            {
                Log.Info($"Activity: readings for {session.Id} not written: {ex.Message}");
                session.Samples = 0;
            }
        }
        lock (_gate)
        {
            _sessions.Add(session);
            _sessions.Sort((a, b) => a.Start.CompareTo(b.Start));
        }
        Save();
    }

    /// <summary>Replaces a session by id (a hand-logged one being edited). False when it is gone.</summary>
    public bool Update(PlaySession session)
    {
        lock (_gate)
        {
            var i = _sessions.FindIndex(s => s.Id == session.Id);
            if (i < 0) return false;
            _sessions[i] = session;
            _sessions.Sort((a, b) => a.Start.CompareTo(b.Start));
        }
        Save();
        return true;
    }

    /// <summary>Sessions brought in from elsewhere, with their readings where they have any. One
    /// whose ImportKey is already on record is skipped, so an import run twice brings nothing the
    /// second time. Returns the sessions actually added.</summary>
    public List<PlaySession> AddImported(IEnumerable<(PlaySession Session, List<ActivitySample>? Samples)> items)
    {
        HashSet<string> have;
        lock (_gate) have = _sessions.Where(s => s.ImportKey is not null).Select(s => s.ImportKey!).ToHashSet();
        var added = new List<PlaySession>();
        foreach (var (session, samples) in items)
        {
            if (session.ImportKey is null || !have.Add(session.ImportKey)) continue;
            if (samples is { Count: > 0 })
            {
                Summarise(session, samples);
                var thinned = Thin(samples, MaxSamplesPerSession);
                session.Samples = thinned.Count;
                try
                {
                    Directory.CreateDirectory(_detailDir);
                    File.WriteAllText(Path.Combine(_detailDir, session.Id + ".json"),
                        JsonSerializer.Serialize(new SessionDetail { Id = session.Id, Samples = thinned }, JsonOpts));
                }
                catch { session.Samples = 0; }
            }
            added.Add(session);
        }
        if (added.Count == 0) return added;
        lock (_gate)
        {
            _sessions.AddRange(added);
            _sessions.Sort((a, b) => a.Start.CompareTo(b.Start));
        }
        Save();
        return added;
    }

    /// <summary>Removes the sessions with these ids in one write (undoing an import).</summary>
    public int RemoveMany(ICollection<string> ids)
    {
        int n;
        lock (_gate) n = _sessions.RemoveAll(s => ids.Contains(s.Id));
        foreach (var id in ids) DeleteDetail(id);
        if (n > 0) Save();
        return n;
    }

    public List<ActivitySample> LoadSamples(string id)
    {
        try
        {
            var path = Path.Combine(_detailDir, id + ".json");
            if (!File.Exists(path)) return new List<ActivitySample>();
            return JsonSerializer.Deserialize<SessionDetail>(File.ReadAllText(path))?.Samples ?? new List<ActivitySample>();
        }
        catch (Exception ex)
        {
            Log.Info($"Activity: readings for {id} unreadable: {ex.Message}");
            return new List<ActivitySample>();
        }
    }

    public bool Remove(string id)
    {
        bool removed;
        lock (_gate) removed = _sessions.RemoveAll(s => s.Id == id) > 0;
        if (!removed) return false;
        DeleteDetail(id);
        Save();
        return true;
    }

    /// <summary>Every session of one game, or of every game when the id is null.</summary>
    public int Clear(string? gameId)
    {
        List<string> gone;
        lock (_gate)
        {
            gone = _sessions.Where(s => gameId is null || s.GameId == gameId).Select(s => s.Id).ToList();
            _sessions.RemoveAll(s => gameId is null || s.GameId == gameId);
        }
        foreach (var id in gone) DeleteDetail(id);
        Save();
        return gone.Count;
    }

    private void DeleteDetail(string id)
    {
        try { File.Delete(Path.Combine(_detailDir, id + ".json")); } catch { /* a cache of readings; not worth failing over */ }
    }

    /// <summary>Sample files whose session is gone -- a crash between the two writes, a hand-edited
    /// list -- are deleted on start, the way the trailer cache prunes its strays.</summary>
    public void Prune()
    {
        try
        {
            if (!Directory.Exists(_detailDir)) return;
            HashSet<string> keep;
            lock (_gate) keep = _sessions.Select(s => s.Id).ToHashSet();
            foreach (var f in Directory.GetFiles(_detailDir, "*.json"))
                if (!keep.Contains(Path.GetFileNameWithoutExtension(f)))
                    try { File.Delete(f); } catch { /* next time */ }
        }
        catch (Exception ex) { Log.Info($"Activity prune failed: {ex.Message}"); }
    }

    // ---- the arithmetic, public so a harness can check it ----

    public static void Summarise(PlaySession s, List<ActivitySample> samples)
    {
        int? Avg(Func<ActivitySample, int?> pick)
        {
            var vals = samples.Select(pick).Where(v => v.HasValue).Select(v => v!.Value).ToList();
            return vals.Count == 0 ? null : (int)Math.Round(vals.Average());
        }
        int? Max(Func<ActivitySample, int?> pick)
        {
            var vals = samples.Select(pick).Where(v => v.HasValue).Select(v => v!.Value).ToList();
            return vals.Count == 0 ? null : vals.Max();
        }
        s.AvgFps = Avg(x => x.Fps);
        s.LowFps = Percentile(samples.Select(x => x.Fps).Where(v => v.HasValue).Select(v => v!.Value).ToList(), 0.05);
        s.AvgCpu = Avg(x => x.Cpu);
        s.AvgGpu = Avg(x => x.Gpu);
        s.AvgRam = Avg(x => x.Ram);
        s.PeakRamMb = Max(x => x.RamMb);
        s.AvgCpuTemp = Avg(x => x.CpuTemp);
        s.MaxCpuTemp = Max(x => x.CpuTemp);
        s.AvgGpuTemp = Avg(x => x.GpuTemp);
        s.MaxGpuTemp = Max(x => x.GpuTemp);
        s.AvgCpuPower = Avg(x => x.CpuPower);
        s.AvgGpuPower = Avg(x => x.GpuPower);
    }

    /// <summary>The value below which the given share of the readings fall; null for no readings.</summary>
    public static int? Percentile(List<int> values, double share)
    {
        if (values.Count == 0) return null;
        var sorted = values.OrderBy(v => v).ToList();
        var idx = (int)Math.Floor(share * (sorted.Count - 1));
        return sorted[Math.Clamp(idx, 0, sorted.Count - 1)];
    }

    /// <summary>Halves the list by averaging neighbours until it is under the cap. Time is the
    /// first of the pair's, so the last reading keeps its place at the end of the session.</summary>
    public static List<ActivitySample> Thin(List<ActivitySample> samples, int max)
    {
        var cur = samples;
        while (cur.Count > max)
        {
            var next = new List<ActivitySample>((cur.Count + 1) / 2);
            for (var i = 0; i < cur.Count; i += 2)
            {
                if (i + 1 >= cur.Count) { next.Add(cur[i]); break; }
                next.Add(Merge(cur[i], cur[i + 1]));
            }
            cur = next;
        }
        return cur;
    }

    private static ActivitySample Merge(ActivitySample a, ActivitySample b)
    {
        static int? Mid(int? x, int? y) => x is null ? y : y is null ? x : (int)Math.Round((x.Value + y.Value) / 2.0);
        static int? Top(int? x, int? y) => x is null ? y : y is null ? x : Math.Max(x.Value, y.Value);
        return new ActivitySample
        {
            T = a.T,
            Fps = Mid(a.Fps, b.Fps), Cpu = Mid(a.Cpu, b.Cpu), Gpu = Mid(a.Gpu, b.Gpu), Ram = Mid(a.Ram, b.Ram),
            RamMb = Top(a.RamMb, b.RamMb), VramMb = Top(a.VramMb, b.VramMb),
            CpuTemp = Mid(a.CpuTemp, b.CpuTemp), GpuTemp = Mid(a.GpuTemp, b.GpuTemp),
            CpuPower = Mid(a.CpuPower, b.CpuPower), GpuPower = Mid(a.GpuPower, b.GpuPower),
            GameCpu = Mid(a.GameCpu, b.GameCpu), GameRamMb = Top(a.GameRamMb, b.GameRamMb),
        };
    }
}
