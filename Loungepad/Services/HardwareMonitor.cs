using System.Diagnostics;
using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using Loungepad.Interop;

namespace Loungepad.Services;

/// <summary>
/// What the PC is doing while a game runs, read once every few seconds by ActivityService.
///
/// Three readers need nothing installed and work on every PC: the system CPU load
/// (GetSystemTimes), memory (GlobalMemoryStatusEx) and the GPU through the performance
/// counters Windows' own Task Manager reads (PDH, "GPU Engine"). That baseline is the difference
/// from Playnite's GameActivity, which records nothing at all until a third-party tool is set up.
///
/// The rest -- frame rate, temperatures, power -- only exists where a tool publishes it, and the
/// three tools people already run for exactly this are read where they are found, from the
/// shared memory each of them documents for overlays and loggers:
///
///   RivaTuner Statistics Server   "RTSSSharedMemoryV2"      frame rate, per process
///   MSI Afterburner               "MAHMSharedMemory"        frame rate (through RTSS), temperatures, usage, power
///   HWiNFO                        "Global\HWiNFO_SENS_SM2"  every sensor it shows (needs "Shared Memory Support" on)
///
/// Nothing is chosen in Settings: whichever tools are running are used, the frame rate from the
/// first that has one (RTSS, then Afterburner, then HWiNFO) and the sensors from HWiNFO before
/// Afterburner. A tool started after the game is picked up on the next retry, which is every
/// half minute; one that stops is dropped the same way. Every reader is wrapped so a layout
/// this code does not expect costs a null reading and never a crash in the sampling loop.
///
/// HWiNFO's readings are matched by their labels, which is a heuristic and is meant to be: the
/// labels are the same on every PC ("CPU Package", "GPU Temperature") while the sensor ids differ
/// per board. The list of labels tried is in the reader below.
/// </summary>
public sealed class HardwareMonitor : IDisposable
{
    public sealed record Reading(int? Fps, int? Cpu, int? Gpu, int? Ram, int? RamMb, int? VramMb,
        int? CpuTemp, int? GpuTemp, int? CpuPower, int? GpuPower, int? GameCpu, int? GameRamMb);

    /// <summary>What is being read from, for the Settings row and the session record.</summary>
    public sealed record Sources(bool Gpu, string? Fps, string? Sensors)
    {
        public string Describe()
        {
            var bits = new List<string> { Gpu ? "built-in CPU, GPU and RAM" : "built-in CPU and RAM" };
            if (Fps is not null) bits.Add($"frame rate from {Fps}");
            if (Sensors is not null) bits.Add($"temperatures and power from {Sensors}");
            return string.Join(", ", bits);
        }
    }

    private static readonly TimeSpan Retry = TimeSpan.FromSeconds(30);

    private long _idle, _kernel, _user;
    private bool _cpuPrimed;
    private readonly Dictionary<int, (TimeSpan Cpu, DateTime At)> _procCpu = new();

    private GpuCounters? _gpu;
    private DateTime _gpuRetryAt;
    private bool _gpuGaveValue;
    private RtssReader? _rtss;
    private DateTime _rtssRetryAt;
    private HwinfoReader? _hwinfo;
    private DateTime _hwinfoRetryAt;
    private AfterburnerReader? _mahm;
    private DateTime _mahmRetryAt;
    private string? _fpsSource, _sensorSource;
    /// <summary>One reader at a time: the sampling loop and the Settings probe can both ask, and
    /// the PDH query and the per-process CPU table are not built for two callers.</summary>
    private readonly object _readGate = new();

    public Reading Read(IReadOnlyCollection<int> gamePids)
    {
        lock (_readGate) return ReadLocked(gamePids);
    }

    private Reading ReadLocked(IReadOnlyCollection<int> gamePids)
    {
        var cpu = SystemCpu();
        var (ram, ramMb) = Memory();
        var (gpu, vramMb) = Gpu(gamePids);
        var (gameCpu, gameRam) = GameProcesses(gamePids);

        int? fps = null, cpuT = null, gpuT = null, cpuP = null, gpuP = null;
        _fpsSource = null; _sensorSource = null;

        var rtss = Open(ref _rtss, ref _rtssRetryAt, () => new RtssReader());
        if (rtss is not null)
        {
            fps = Guard(ref _rtss, () => rtss.Fps(gamePids));
            if (fps is not null) _fpsSource = "RivaTuner Statistics Server";
        }

        var hw = Open(ref _hwinfo, ref _hwinfoRetryAt, () => new HwinfoReader());
        if (hw is not null)
        {
            var v = Guard(ref _hwinfo, () => hw.Read());
            if (v is not null)
            {
                cpuT = v.CpuTemp; gpuT = v.GpuTemp; cpuP = v.CpuPower; gpuP = v.GpuPower;
                if (cpuT is not null || gpuT is not null || cpuP is not null || gpuP is not null) _sensorSource = "HWiNFO";
                if (fps is null && v.Fps is not null) { fps = v.Fps; _fpsSource = "HWiNFO"; }
                gpu ??= v.Gpu;
            }
        }

        var mahm = Open(ref _mahm, ref _mahmRetryAt, () => new AfterburnerReader());
        if (mahm is not null)
        {
            var v = Guard(ref _mahm, () => mahm.Read());
            if (v is not null)
            {
                if (fps is null && v.Fps is not null) { fps = v.Fps; _fpsSource = "MSI Afterburner"; }
                if (_sensorSource is null && (v.CpuTemp is not null || v.GpuTemp is not null || v.GpuPower is not null || v.CpuPower is not null))
                    _sensorSource = "MSI Afterburner";
                cpuT ??= v.CpuTemp; gpuT ??= v.GpuTemp; cpuP ??= v.CpuPower; gpuP ??= v.GpuPower;
                gpu ??= v.Gpu;
            }
        }

        return new Reading(fps, cpu, gpu, ram, ramMb, vramMb, cpuT, gpuT, cpuP, gpuP, gameCpu, gameRam);
    }

    /// <summary>What the last Read used, plus whether the GPU counters work on this PC.</summary>
    public Sources Status() => new(_gpuGaveValue, _fpsSource, _sensorSource);

    /// <summary>Opens each tool once so Status() can say what is there before any game has run.</summary>
    public Sources Probe()
    {
        try { Read(Array.Empty<int>()); } catch { /* a reading is optional */ }
        var fps = _rtss is not null ? "RivaTuner Statistics Server"
            : _mahm is not null && _mahm.HasFramerate ? "MSI Afterburner"
            : _hwinfo is not null && _hwinfo.HasFramerate ? "HWiNFO" : null;
        var sensors = _hwinfo is not null ? "HWiNFO" : _mahm is not null ? "MSI Afterburner" : null;
        return new Sources(_gpuGaveValue, fps, sensors);
    }

    // ---- built-in ----

    private int? SystemCpu()
    {
        if (!NativeMethods.GetSystemTimes(out var idle, out var kernel, out var user)) return null;
        var dIdle = idle - _idle;
        var dBusy = (kernel - _kernel) + (user - _user);   // kernel time includes idle
        _idle = idle; _kernel = kernel; _user = user;
        if (!_cpuPrimed) { _cpuPrimed = true; return null; }
        if (dBusy <= 0) return null;
        return (int)Math.Clamp(Math.Round(100.0 * (1.0 - (double)dIdle / dBusy)), 0, 100);
    }

    private static (int? Percent, int? UsedMb) Memory()
    {
        var m = new NativeMethods.MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<NativeMethods.MEMORYSTATUSEX>() };
        if (!NativeMethods.GlobalMemoryStatusEx(ref m)) return (null, null);
        var used = (m.ullTotalPhys - m.ullAvailPhys) / (1024 * 1024);
        return ((int)m.dwMemoryLoad, (int)used);
    }

    private (int? Gpu, int? VramMb) Gpu(IReadOnlyCollection<int> pids)
    {
        var g = Open(ref _gpu, ref _gpuRetryAt, () => new GpuCounters());
        if (g is null) return (null, null);
        var r = Guard<GpuCounters, (int? Gpu, int? VramMb)?>(ref _gpu, () => g.Read(pids));
        if (r is null) return (null, null);
        if (r.Value.Gpu is not null) _gpuGaveValue = true;
        return r.Value;
    }

    private (int? Cpu, int? RamMb) GameProcesses(IReadOnlyCollection<int> pids)
    {
        if (pids.Count == 0) return (null, null);
        var now = DateTime.UtcNow;
        double busy = 0, wall = 0;
        long ws = 0;
        var seen = new HashSet<int>();
        foreach (var pid in pids)
        {
            try
            {
                using var p = Process.GetProcessById(pid);
                var cpu = p.TotalProcessorTime;
                ws += p.WorkingSet64;
                seen.Add(pid);
                if (_procCpu.TryGetValue(pid, out var last))
                {
                    busy += (cpu - last.Cpu).TotalSeconds;
                    wall += (now - last.At).TotalSeconds;
                }
                _procCpu[pid] = (cpu, now);
            }
            catch { /* exited between the listing and the query */ }
        }
        foreach (var gone in _procCpu.Keys.Where(k => !seen.Contains(k)).ToList()) _procCpu.Remove(gone);
        int? pct = wall > 0 ? (int)Math.Clamp(Math.Round(100.0 * busy / (wall * Environment.ProcessorCount)), 0, 100) : null;
        return (pct, seen.Count == 0 ? null : (int)(ws / (1024 * 1024)));
    }

    // ---- the readers, each opened lazily and retried on a timer ----

    private static T? Open<T>(ref T? reader, ref DateTime retryAt, Func<T> make) where T : class, IDisposable
    {
        if (reader is not null) return reader;
        if (DateTime.UtcNow < retryAt) return null;
        retryAt = DateTime.UtcNow + Retry;
        try { reader = make(); }
        catch { reader = null; }   // not installed, not running, or a layout this build does not know
        return reader;
    }

    /// <summary>Runs one read; a throw drops the reader so it is reopened on the next retry.</summary>
    private static TResult? Guard<TReader, TResult>(ref TReader? reader, Func<TResult?> read) where TReader : class, IDisposable
    {
        try { return read(); }
        catch (Exception ex)
        {
            Log.Info($"Hardware monitor: {typeof(TReader).Name} dropped ({ex.Message})");
            try { reader?.Dispose(); } catch { /* already gone */ }
            reader = null;
            return default;
        }
    }

    public void Dispose()
    {
        _gpu?.Dispose(); _rtss?.Dispose(); _hwinfo?.Dispose(); _mahm?.Dispose();
        _gpu = null; _rtss = null; _hwinfo = null; _mahm = null;
    }

    private static string Ansi(MemoryMappedViewAccessor acc, long offset, int len)
    {
        var buf = new byte[len];
        acc.ReadArray(offset, buf, 0, len);
        var end = Array.IndexOf(buf, (byte)0);
        return Encoding.Latin1.GetString(buf, 0, end < 0 ? len : end).Trim();
    }

    // ---- PDH: GPU Engine utilisation and adapter memory ----

    private sealed class GpuCounters : IDisposable
    {
        private static readonly Regex PidRx = new(@"^pid_(\d+)_", RegexOptions.Compiled);
        private static readonly Regex TypeRx = new(@"engtype_(\w+)$", RegexOptions.Compiled);
        private IntPtr _query, _engine, _memory;

        public GpuCounters()
        {
            if (NativeMethods.PdhOpenQueryW(null, IntPtr.Zero, out _query) != 0) throw new InvalidOperationException("PdhOpenQuery");
            if (NativeMethods.PdhAddEnglishCounterW(_query, @"\GPU Engine(*)\Utilization Percentage", IntPtr.Zero, out _engine) != 0)
            {
                NativeMethods.PdhCloseQuery(_query);
                throw new InvalidOperationException("no GPU Engine counter");
            }
            // Optional: an older driver publishes the engines but not the memory.
            if (NativeMethods.PdhAddEnglishCounterW(_query, @"\GPU Adapter Memory(*)\Dedicated Usage", IntPtr.Zero, out _memory) != 0)
                _memory = IntPtr.Zero;
            // A rate counter needs two collections; the first primes it.
            NativeMethods.PdhCollectQueryData(_query);
        }

        public (int? Gpu, int? VramMb) Read(IReadOnlyCollection<int> pids)
        {
            if (NativeMethods.PdhCollectQueryData(_query) != 0) return (null, null);

            // Task Manager's number: per engine type, the sum over every process; then the busiest
            // type. Summing every engine would count a 3D-bound game and its copy engine twice.
            var byType = new Dictionary<string, double>(StringComparer.Ordinal);
            foreach (var (name, value) in Items(_engine))
            {
                var t = TypeRx.Match(name);
                var type = t.Success ? t.Groups[1].Value : "other";
                byType[type] = byType.GetValueOrDefault(type) + value;
            }
            int? gpu = byType.Count == 0 ? null : (int)Math.Clamp(Math.Round(byType.Values.Max()), 0, 100);

            int? vram = null;
            if (_memory != IntPtr.Zero)
            {
                double bytes = 0;
                var any = false;
                foreach (var (_, value) in Items(_memory)) { bytes += value; any = true; }
                if (any) vram = (int)(bytes / (1024 * 1024));
            }
            return (gpu, vram);
        }

        private static List<(string Name, double Value)> Items(IntPtr counter)
        {
            var list = new List<(string, double)>();
            uint size = 0;
            var rc = NativeMethods.PdhGetFormattedCounterArrayW(counter, NativeMethods.PDH_FMT_DOUBLE, ref size, out _, IntPtr.Zero);
            if (rc != NativeMethods.PDH_MORE_DATA || size == 0) return list;
            var buf = Marshal.AllocHGlobal((int)size);
            try
            {
                rc = NativeMethods.PdhGetFormattedCounterArrayW(counter, NativeMethods.PDH_FMT_DOUBLE, ref size, out var count, buf);
                if (rc != 0) return list;
                for (var i = 0; i < count; i++)
                {
                    var item = buf + i * NativeMethods.PDH_ITEM_SIZE;
                    var namePtr = Marshal.ReadIntPtr(item);
                    var status = Marshal.ReadInt32(item, 8);
                    if (status != 0) continue;   // PDH_CSTATUS_VALID_DATA is 0; anything else is stale or gone
                    var value = BitConverter.Int64BitsToDouble(Marshal.ReadInt64(item, 16));
                    var name = namePtr == IntPtr.Zero ? "" : Marshal.PtrToStringUni(namePtr) ?? "";
                    if (double.IsNaN(value) || double.IsInfinity(value)) continue;
                    list.Add((name, value));
                }
            }
            finally { Marshal.FreeHGlobal(buf); }
            return list;
        }

        public void Dispose()
        {
            if (_query != IntPtr.Zero) NativeMethods.PdhCloseQuery(_query);
            _query = IntPtr.Zero;
        }
    }

    // ---- RTSS ----

    /// <summary>RivaTuner's shared memory (RTSSSharedMemory.h, v2): a header of DWORDs -- the
    /// signature 'RTSS', the version, then the app entry size, the array's offset and its
    /// length -- and one entry per hooked process. In an entry: the process id at 0, its name at
    /// 4 (260 bytes), then at 268/272 the start and end tick of the measurement window, at 276
    /// the frames rendered in it and at 280 the average frame time in microseconds.</summary>
    private sealed class RtssReader : IDisposable
    {
        private const uint Signature = 0x52545353;
        private readonly MemoryMappedFile _mmf;
        private readonly MemoryMappedViewAccessor _acc;

        public RtssReader()
        {
            _mmf = MemoryMappedFile.OpenExisting("RTSSSharedMemoryV2", MemoryMappedFileRights.Read);
            _acc = _mmf.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read);
            if (_acc.ReadUInt32(0) != Signature) { Dispose(); throw new InvalidOperationException("not RTSS"); }
        }

        public int? Fps(IReadOnlyCollection<int> pids)
        {
            uint entrySize = _acc.ReadUInt32(8), arrOffset = _acc.ReadUInt32(12), arrSize = _acc.ReadUInt32(16);
            if (entrySize < 284 || arrSize > 512) return null;
            var tick = unchecked((uint)Environment.TickCount);
            double ours = -1, any = -1;
            uint oursAge = uint.MaxValue, anyAge = uint.MaxValue;
            for (uint i = 0; i < arrSize; i++)
            {
                long b = arrOffset + (long)i * entrySize;
                if (b + 284 > _acc.Capacity) break;
                var pid = _acc.ReadUInt32(b);
                if (pid == 0) continue;
                uint t0 = _acc.ReadUInt32(b + 268), t1 = _acc.ReadUInt32(b + 272), frames = _acc.ReadUInt32(b + 276), ft = _acc.ReadUInt32(b + 280);
                var fps = t1 > t0 && frames > 0 ? frames * 1000.0 / (t1 - t0) : ft > 0 ? 1e6 / ft : 0;
                if (fps <= 0 || fps > 2000) continue;
                // The ticks are GetTickCount's, the same clock as Environment.TickCount; an entry
                // not updated in ten seconds is a game that has stopped rendering, or gone.
                var age = unchecked(tick - t1);
                if (age > 10_000) continue;
                if (pids.Contains((int)pid)) { if (age < oursAge) { ours = fps; oursAge = age; } }
                else if (age < anyAge) { any = fps; anyAge = age; }
            }
            var v = ours >= 0 ? ours : any;
            return v >= 0 ? (int)Math.Round(v) : null;
        }

        public void Dispose() { _acc.Dispose(); _mmf.Dispose(); }
    }

    // ---- HWiNFO ----

    /// <summary>HWiNFO's sensor memory (HWiSenSM2, packed): a 44-byte header, then the sensors
    /// and the readings as two arrays whose offsets and element sizes the header gives. A
    /// reading is its type, its sensor's index, an id, the original and user labels (128 each),
    /// the unit (16) and four doubles, the current value first at offset 284.</summary>
    private sealed class HwinfoReader : IDisposable
    {
        private const uint Signature = 0x53695748;   // "HWiS"
        private const uint TypeTemp = 1, TypePower = 5, TypeUsage = 7, TypeOther = 8;
        private readonly MemoryMappedFile _mmf;
        private readonly MemoryMappedViewAccessor _acc;

        public sealed record Values(int? Fps, int? CpuTemp, int? GpuTemp, int? CpuPower, int? GpuPower, int? Gpu);
        public bool HasFramerate { get; private set; }

        public HwinfoReader()
        {
            _mmf = MemoryMappedFile.OpenExisting(@"Global\HWiNFO_SENS_SM2", MemoryMappedFileRights.Read);
            _acc = _mmf.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read);
            if (_acc.ReadUInt32(0) != Signature) { Dispose(); throw new InvalidOperationException("not HWiNFO"); }
        }

        // In order of preference; the first label present wins. The CPU has several candidates
        // because AMD and Intel name the package reading differently.
        private static readonly string[] CpuTempLabels = { "CPU (Tctl/Tdie)", "CPU Package", "CPU Die (average)", "CPU CCD1 (Tdie)", "CPU Tdie", "Core Max", "CPU Temperature" };
        private static readonly string[] GpuTempLabels = { "GPU Temperature", "GPU Hot Spot Temperature" };
        private static readonly string[] CpuPowerLabels = { "CPU Package Power", "CPU PPT", "CPU Power" };
        private static readonly string[] GpuPowerLabels = { "GPU Power", "GPU Board Power", "GPU Total Power" };
        private static readonly string[] GpuUsageLabels = { "GPU Core Load", "GPU D3D Usage", "GPU Utilization" };

        public Values Read()
        {
            uint offR = _acc.ReadUInt32(32), sizeR = _acc.ReadUInt32(36), numR = _acc.ReadUInt32(40);
            if (sizeR < 292 || numR == 0 || numR > 8192) return new Values(null, null, null, null, null, null);

            int? fps = null;
            var cpuT = new int?[CpuTempLabels.Length]; var gpuT = new int?[GpuTempLabels.Length];
            var cpuP = new int?[CpuPowerLabels.Length]; var gpuP = new int?[GpuPowerLabels.Length];
            var gpuU = new int?[GpuUsageLabels.Length];
            for (uint i = 0; i < numR; i++)
            {
                long b = offR + (long)i * sizeR;
                if (b + 292 > _acc.Capacity) break;
                var type = _acc.ReadUInt32(b);
                var label = Ansi(_acc, b + 140, 128);
                if (label.Length == 0) label = Ansi(_acc, b + 12, 128);
                var unit = Ansi(_acc, b + 268, 16);
                var value = _acc.ReadDouble(b + 284);
                if (double.IsNaN(value) || double.IsInfinity(value)) continue;
                var v = (int)Math.Round(value);

                if (type is TypeOther or TypeUsage && unit.Equals("FPS", StringComparison.OrdinalIgnoreCase)
                    && label.StartsWith("Framerate", StringComparison.OrdinalIgnoreCase) && !label.Contains("Low", StringComparison.OrdinalIgnoreCase))
                { HasFramerate = true; fps ??= v > 0 ? v : null; continue; }
                if (type == TypeTemp) { Slot(cpuT, CpuTempLabels, label, v); Slot(gpuT, GpuTempLabels, label, v); }
                else if (type == TypePower) { Slot(cpuP, CpuPowerLabels, label, v); Slot(gpuP, GpuPowerLabels, label, v); }
                else if (type == TypeUsage) Slot(gpuU, GpuUsageLabels, label, v);
            }
            return new Values(fps, First(cpuT), First(gpuT), First(cpuP), First(gpuP), First(gpuU));
        }

        private static void Slot(int?[] slots, string[] labels, string label, int v)
        {
            for (var i = 0; i < labels.Length; i++)
                if (slots[i] is null && label.Equals(labels[i], StringComparison.OrdinalIgnoreCase)) { slots[i] = v; return; }
        }
        private static int? First(int?[] slots) => slots.FirstOrDefault(s => s is not null);

        public void Dispose() { _acc.Dispose(); _mmf.Dispose(); }
    }

    // ---- MSI Afterburner ----

    /// <summary>Afterburner's monitoring memory (MAHMSharedMemory.h): 'MAHM', a version, the
    /// header size, the entry count and size, then one entry per monitored source. An entry
    /// leads with five 260-byte ANSI strings (source name, units, localised name and units, a
    /// format), so the float value sits at 1300; the older 2.0 layout is shorter and keeps it at
    /// 544. FLT_MAX is Afterburner's "no reading".</summary>
    private sealed class AfterburnerReader : IDisposable
    {
        private const uint Signature = 0x4D41484D;
        private readonly MemoryMappedFile _mmf;
        private readonly MemoryMappedViewAccessor _acc;

        public sealed record Values(int? Fps, int? CpuTemp, int? GpuTemp, int? CpuPower, int? GpuPower, int? Gpu);
        public bool HasFramerate { get; private set; }

        public AfterburnerReader()
        {
            _mmf = MemoryMappedFile.OpenExisting("MAHMSharedMemory", MemoryMappedFileRights.Read);
            _acc = _mmf.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read);
            if (_acc.ReadUInt32(0) != Signature) { Dispose(); throw new InvalidOperationException("not Afterburner"); }
        }

        public Values Read()
        {
            uint headerSize = _acc.ReadUInt32(8), num = _acc.ReadUInt32(12), entrySize = _acc.ReadUInt32(16);
            var dataOff = entrySize < 640 ? 544 : 1300;
            if (entrySize < dataOff + 4 || num > 512) return new Values(null, null, null, null, null, null);
            int? fps = null, cpuT = null, gpuT = null, cpuP = null, gpuP = null, gpu = null;
            for (uint i = 0; i < num; i++)
            {
                long b = headerSize + (long)i * entrySize;
                if (b + dataOff + 4 > _acc.Capacity) break;
                var name = Ansi(_acc, b, 260);
                var f = _acc.ReadSingle(b + dataOff);
                if (float.IsNaN(f) || float.IsInfinity(f) || f > 1e30f) continue;
                var v = (int)Math.Round(f);
                switch (name.ToLowerInvariant())
                {
                    case "framerate": HasFramerate = true; if (v > 0) fps ??= v; break;
                    case "gpu temperature": gpuT ??= v; break;
                    case "cpu temperature": cpuT ??= v; break;
                    case "gpu usage": gpu ??= v; break;
                    case "gpu power": gpuP ??= v; break;
                    case "cpu power": cpuP ??= v; break;
                }
            }
            return new Values(fps, cpuT, gpuT, cpuP, gpuP, gpu);
        }

        public void Dispose() { _acc.Dispose(); _mmf.Dispose(); }
    }
}
