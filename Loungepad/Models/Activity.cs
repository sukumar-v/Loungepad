using System.Text.Json.Serialization;

namespace Loungepad.Models;

/// <summary>
/// One sitting with a game: from the moment its process was started to the moment the last of
/// its processes went. The launcher already tracked the total (Game.PlaytimeMinutes) and the
/// count (Game.Sessions); this is the record behind those numbers, one row per sitting, which is
/// what a "when did I play what" view needs and a total cannot give back.
///
/// The averages are kept on the row so a list of sessions never has to open the sample files.
/// The samples themselves -- one reading every few seconds -- live in a file of their own per
/// session (see ActivityStore), because a year of evenings is tens of thousands of them and the
/// list is read on every start.
/// </summary>
public class PlaySession
{
    /// <summary>Twelve hex characters, random. It is also the sample file's name.</summary>
    public string Id { get; set; } = "";
    public string GameId { get; set; } = "";
    /// <summary>Local time, like Game.LastPlayed.</summary>
    public DateTime Start { get; set; }
    public DateTime End { get; set; }
    public int Seconds { get; set; }
    /// <summary>How many readings the sample file holds; 0 means there is no file.</summary>
    public int Samples { get; set; }

    // Session averages, each null when nothing supplied that reading. The 5th-percentile frame
    // rate is what a "1% low" stands in for at five-second sampling: it says whether the average
    // hid a stutter without pretending to a precision the sampling does not have.
    public int? AvgFps { get; set; }
    public int? LowFps { get; set; }
    public int? AvgCpu { get; set; }
    public int? AvgGpu { get; set; }
    public int? AvgRam { get; set; }
    public int? PeakRamMb { get; set; }
    public int? AvgCpuTemp { get; set; }
    public int? MaxCpuTemp { get; set; }
    public int? AvgGpuTemp { get; set; }
    public int? MaxGpuTemp { get; set; }
    public int? AvgCpuPower { get; set; }
    public int? AvgGpuPower { get; set; }
    /// <summary>Which tools the readings came from, e.g. "RivaTuner Statistics Server, HWiNFO".
    /// Named on the row because a session recorded before a tool was installed has no frame
    /// rate, and the list should be able to say why rather than show a gap.</summary>
    public string? Sources { get; set; }

    /// <summary>Null for a sitting the launcher recorded; "manual" for one logged by hand on the
    /// game's Stats sheet; "playnite" for one imported from Playnite's GameActivity.</summary>
    public string? Origin { get; set; }
    /// <summary>A hand-logged session that was added to the game's PlaytimeMinutes and Sessions
    /// when it was logged, so editing or removing it moves the totals with it. A recorded session
    /// is in the totals by construction and removing it never takes it back out (the totals are
    /// the launcher's own count); an imported one never goes in, because the import brings
    /// Playnite's totals separately and the two would count the same evening twice.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool Counted { get; set; }
    /// <summary>The imported original's key -- Playnite's game id and the session's start -- so
    /// running the import again skips what it brought last time.</summary>
    public string? ImportKey { get; set; }
}

/// <summary>One reading during a session, T seconds after its start. Every value is optional:
/// the built-in readers always supply CPU, RAM and (on a PC whose GPU driver publishes the
/// counters) GPU; frame rate, temperatures and power need a tool that publishes them.</summary>
public class ActivitySample
{
    public int T { get; set; }
    public int? Fps { get; set; }
    public int? Cpu { get; set; }
    public int? Gpu { get; set; }
    public int? Ram { get; set; }
    public int? RamMb { get; set; }
    public int? VramMb { get; set; }
    public int? CpuTemp { get; set; }
    public int? GpuTemp { get; set; }
    public int? CpuPower { get; set; }
    public int? GpuPower { get; set; }
    /// <summary>The game's own processes: their share of the CPU and their working set.</summary>
    public int? GameCpu { get; set; }
    public int? GameRamMb { get; set; }
}

/// <summary>What activity.json holds.</summary>
public class ActivityFile
{
    public List<PlaySession> Sessions { get; set; } = new();
}

/// <summary>A session's sample file.</summary>
public class SessionDetail
{
    public string Id { get; set; } = "";
    public List<ActivitySample> Samples { get; set; } = new();
}
