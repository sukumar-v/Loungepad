using Loungepad.Models;

namespace Loungepad.Services;

/// <summary>
/// Turns the launcher's sessions into the activity log: one PlaySession per sitting, with a
/// hardware reading every few seconds while the game runs.
///
/// Hangs off GameLaunchService's SessionStarted and SessionEnded rather than watching processes
/// itself, so "a session" here is exactly what the playtime counter calls one -- same start,
/// same end, same rule for a sitting too short to count. The sampling loop runs on the thread
/// pool for the length of the session and stops on the end event; whatever it gathered is
/// summarised onto the row and thinned into the session's own file by ActivityStore.
///
/// The latest reading is kept for the in-game menu, which shows the frame rate and the load
/// over the paused game, and is pushed as it is taken (Sampled) so that readout moves.
/// </summary>
public sealed class ActivityService : IDisposable
{
    /// <summary>The running session as the page sees it: which game, since when, the last
    /// reading, and where the readings come from.</summary>
    public sealed record Live(string GameId, DateTime Start, ActivitySample? Sample, HardwareMonitor.Sources Sources);

    private readonly ActivityStore _store;
    private readonly GameLaunchService _launcher;
    private readonly Func<AppSettings> _settings;
    private readonly object _gate = new();
    private HardwareMonitor? _monitor;
    private CancellationTokenSource? _loop;
    private List<ActivitySample> _samples = new();
    private Live? _live;

    /// <summary>A session was written. Off the UI thread.</summary>
    public event Action<PlaySession>? SessionRecorded;
    /// <summary>A reading was taken during the running session. Off the UI thread.</summary>
    public event Action<Live>? Sampled;

    public ActivityService(ActivityStore store, GameLaunchService launcher, Func<AppSettings> settings)
    {
        _store = store;
        _launcher = launcher;
        _settings = settings;
        launcher.SessionStarted += OnStarted;
        launcher.SessionEnded += OnEnded;
    }

    public ActivityStore Store => _store;
    public Live? Current { get { lock (_gate) return _live; } }

    /// <summary>What the readings would come from right now, for the Settings row. Opens the
    /// tools' shared memory if it has not yet, which is cheap and needs no game running.</summary>
    public HardwareMonitor.Sources Sources()
    {
        lock (_gate) _monitor ??= new HardwareMonitor();
        return _monitor.Probe();
    }

    private void OnStarted(Game game, DateTime start)
    {
        var s = _settings();
        if (!s.ActivityTracking) return;
        CancellationTokenSource cts;
        lock (_gate)
        {
            _loop?.Cancel();
            _loop = cts = new CancellationTokenSource();
            _samples = new List<ActivitySample>();
            _monitor ??= new HardwareMonitor();
            _live = new Live(game.Id, start, null, _monitor.Status());
        }
        if (!s.ActivityHardware) return;
        _ = Task.Run(() => SampleLoop(game, start, cts.Token));
    }

    private async Task SampleLoop(Game game, DateTime start, CancellationToken ct)
    {
        // The first CPU reading only primes the counters; it is taken and thrown away.
        try { _monitor?.Read(_launcher.TrackedPids()); } catch { /* optional */ }
        while (!ct.IsCancellationRequested)
        {
            var seconds = Math.Clamp(_settings().ActivitySampleSeconds, 2, 30);
            try { await Task.Delay(TimeSpan.FromSeconds(seconds), ct); }
            catch (TaskCanceledException) { return; }
            if (ct.IsCancellationRequested) return;

            HardwareMonitor.Reading r;
            HardwareMonitor.Sources src;
            try
            {
                var mon = _monitor;
                if (mon is null) return;
                r = mon.Read(_launcher.TrackedPids());
                src = mon.Status();
            }
            catch (Exception ex)
            {
                Log.Info($"Activity: reading failed ({ex.Message})");
                continue;
            }
            var sample = new ActivitySample
            {
                T = (int)(DateTime.Now - start).TotalSeconds,
                Fps = r.Fps, Cpu = r.Cpu, Gpu = r.Gpu, Ram = r.Ram, RamMb = r.RamMb, VramMb = r.VramMb,
                CpuTemp = r.CpuTemp, GpuTemp = r.GpuTemp, CpuPower = r.CpuPower, GpuPower = r.GpuPower,
                GameCpu = r.GameCpu, GameRamMb = r.GameRamMb,
            };
            Live live;
            lock (_gate)
            {
                if (ct.IsCancellationRequested) return;
                _samples.Add(sample);
                live = _live = new Live(game.Id, start, sample, src);
            }
            try { Sampled?.Invoke(live); } catch (Exception ex) { Log.Info($"Activity: sample handler failed ({ex.Message})"); }
        }
    }

    private void OnEnded(Game game, DateTime start, DateTime end, bool counted)
    {
        List<ActivitySample> samples;
        HardwareMonitor.Sources? sources;
        lock (_gate)
        {
            _loop?.Cancel();
            _loop = null;
            samples = _samples;
            _samples = new List<ActivitySample>();
            sources = _live?.Sources;
            _live = null;
        }
        if (!counted || !_settings().ActivityTracking) return;

        var session = new PlaySession
        {
            Id = ActivityStore.NewId(),
            GameId = game.Id,
            Start = start,
            End = end,
            Seconds = (int)(end - start).TotalSeconds,
            Sources = SourcesLabel(sources),
        };
        _store.Add(session, samples);
        Log.Info($"Activity: {game.Title} played {session.Seconds / 60} min, {samples.Count} readings" +
                 (session.AvgFps is { } f ? $", {f} fps avg" : ""));
        try { SessionRecorded?.Invoke(session); } catch (Exception ex) { Log.Info($"Activity: record handler failed ({ex.Message})"); }
    }

    private static string? SourcesLabel(HardwareMonitor.Sources? s)
    {
        if (s is null) return null;
        var bits = new List<string>();
        if (s.Fps is not null) bits.Add(s.Fps);
        if (s.Sensors is not null && s.Sensors != s.Fps) bits.Add(s.Sensors);
        return bits.Count == 0 ? null : string.Join(", ", bits);
    }

    public void Dispose()
    {
        _launcher.SessionStarted -= OnStarted;
        _launcher.SessionEnded -= OnEnded;
        lock (_gate)
        {
            _loop?.Cancel();
            _monitor?.Dispose();
            _monitor = null;
        }
    }
}
