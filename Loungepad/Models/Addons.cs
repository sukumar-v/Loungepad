using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Loungepad.Models;

/// <summary>
/// An add-on is a theme or an extension from outside the launcher (see docs/ADDONS.md). What
/// is on disk is the truth about what is installed: a theme is a folder under themes\ with a
/// theme.json, an extension a folder under extensions\ with a manifest.json. The records in
/// addons.json only add where each came from and whether it is enabled.
/// </summary>
public static class AddonKind
{
    public const string Theme = "theme";
    public const string Extension = "extension";
    public static bool IsValid(string? kind) => kind is Theme or Extension;
}

/// <summary>
/// What a manifest declares -- an extension's manifest.json, or the same fields read off a theme's
/// theme.json. Everything but the id is optional on disk; the reader fills in the id from the
/// folder when the file has none, which is the case for every theme.
/// </summary>
public class AddonManifest
{
    public string Id { get; set; } = "";
    public string Kind { get; set; } = AddonKind.Extension;
    public string Name { get; set; } = "";
    public string Version { get; set; } = "";
    public string? Description { get; set; }
    public string? Author { get; set; }
    public string? Homepage { get; set; }
    /// <summary>The module the runtime loads, relative to the folder. Extensions only.</summary>
    public string? Main { get; set; }
    /// <summary>An image in the folder for the tile: svg, png, jpg, webp.</summary>
    public string? Icon { get; set; }
    /// <summary>The oldest launcher this works with; anything older refuses to install it.</summary>
    public string? MinLauncher { get; set; }
    public AddonPermissions? Permissions { get; set; }
    public AddonContributes? Contributes { get; set; }
    /// <summary>The options the add-on declares, in the shape theme.json uses (see
    /// themeSettingDefs in app.js). The page checks them; the host only keeps the values clean.</summary>
    public JsonElement? Settings { get; set; }

    /// <summary>An id as a folder name, a URL host label and a settings key all at once.</summary>
    public static readonly Regex IdPattern = new("^[a-z][a-z0-9-]{0,39}$", RegexOptions.Compiled);
    public static bool IsValidId(string? id) => id is not null && IdPattern.IsMatch(id);

    public List<string> Hosts => Permissions?.Hosts ?? new();
    public bool IsMetadataSource => Kind == AddonKind.Extension && Contributes?.Metadata is not null;
}

public class AddonPermissions
{
    /// <summary>Hosts the extension may send requests to: "howlongtobeat.com" or "*.example.com".</summary>
    public List<string> Hosts { get; set; } = new();
}

public class AddonContributes
{
    public MetadataContribution? Metadata { get; set; }
    public List<GameFact>? GameFacts { get; set; }
}

/// <summary>The extension fills in facts per game; these are the pass's knobs.</summary>
public class MetadataContribution
{
    /// <summary>How long an answer is kept before the game is asked about again.</summary>
    public int StaleAfterDays { get; set; } = 30;
    /// <summary>How long a game the extension had nothing for waits before it is tried again.</summary>
    public int RetryAfterDays { get; set; } = 7;
    /// <summary>The gap the launcher leaves between two games.</summary>
    public int PaceMs { get; set; } = 1500;
    /// <summary>Only games on disk; the default also covers the owned-but-not-installed ones.</summary>
    public bool InstalledOnly { get; set; }
}

/// <summary>One stored field the page shows on a game's page: which key, what to call it, how to print it.</summary>
public class GameFact
{
    public string Key { get; set; } = "";
    public string Label { get; set; } = "";
    /// <summary>hours | number | text | percent</summary>
    public string Format { get; set; } = "text";
}

/// <summary>Where an install came from and how it stands, kept in addons.json beside the folders.</summary>
public class AddonRecord
{
    public string Kind { get; set; } = AddonKind.Extension;
    public string Version { get; set; } = "";
    /// <summary>catalogue | zip | folder</summary>
    public string Source { get; set; } = "catalogue";
    /// <summary>For a folder install: where it was copied from, so it can be reloaded after an edit.</summary>
    public string? SourcePath { get; set; }
    public DateTime InstalledAt { get; set; }
    public bool Enabled { get; set; } = true;
    /// <summary>The last metadata pass this extension ran, for the add-on's page.</summary>
    public PassSummary? LastPass { get; set; }
}

public class PassSummary
{
    public DateTime At { get; set; }
    public int Tried { get; set; }
    public int Found { get; set; }
    public int Failed { get; set; }
    public string? Error { get; set; }
}

public class AddonsFile
{
    public Dictionary<string, AddonRecord> Installed { get; set; } = new();
}

/// <summary>One entry of the repository's index.json.</summary>
public class CatalogueEntry
{
    public string Id { get; set; } = "";
    public string Kind { get; set; } = "";
    public string Name { get; set; } = "";
    public string Version { get; set; } = "";
    public string? Summary { get; set; }
    public string? Author { get; set; }
    public string? Homepage { get; set; }
    public string? MinLauncher { get; set; }
    public AddonPermissions? Permissions { get; set; }
    public string? Icon { get; set; }
    /// <summary>Where the files are, ending in a slash; every file is Base + Path.</summary>
    public string Base { get; set; } = "";
    public List<CatalogueFile> Files { get; set; } = new();
}

public class CatalogueFile
{
    public string Path { get; set; } = "";
    public string Sha256 { get; set; } = "";
    public long Size { get; set; }
}

public class CatalogueIndex
{
    public int Schema { get; set; }
    public DateTime? Generated { get; set; }
    public List<CatalogueEntry> Addons { get; set; } = new();
}

/// <summary>The cached index, with when it was fetched and from where.</summary>
public class CatalogueCache
{
    public string Url { get; set; } = "";
    public DateTime FetchedAt { get; set; }
    public CatalogueIndex Index { get; set; } = new();
}

/// <summary>
/// "major.minor.patch", compared as numbers. Anything after the three numbers (a pre-release
/// tag) is kept for display and ignored for ordering; a missing part is 0.
/// </summary>
public readonly struct AddonVersion : IComparable<AddonVersion>
{
    public int Major { get; }
    public int Minor { get; }
    public int Patch { get; }
    public string Text { get; }

    private AddonVersion(int major, int minor, int patch, string text)
    {
        Major = major; Minor = minor; Patch = patch; Text = text;
    }

    public static bool TryParse(string? text, out AddonVersion version)
    {
        version = default;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var s = text.Trim();
        if (s.StartsWith('v') || s.StartsWith('V')) s = s[1..];
        var core = s.Split('-', '+')[0];
        var parts = core.Split('.');
        if (parts.Length is 0 or > 3) return false;
        var nums = new int[3];
        for (var i = 0; i < parts.Length; i++)
        {
            if (!int.TryParse(parts[i], out var n) || n < 0) return false;
            nums[i] = n;
        }
        version = new AddonVersion(nums[0], nums[1], nums[2], s);
        return true;
    }

    public static AddonVersion From(Version v) => new(v.Major, v.Minor, Math.Max(0, v.Build), $"{v.Major}.{v.Minor}.{Math.Max(0, v.Build)}");

    public int CompareTo(AddonVersion other)
    {
        var c = Major.CompareTo(other.Major);
        if (c != 0) return c;
        c = Minor.CompareTo(other.Minor);
        return c != 0 ? c : Patch.CompareTo(other.Patch);
    }

    public override string ToString() => Text;

    /// <summary>True when <paramref name="candidate"/> is a newer version than <paramref name="installed"/>;
    /// an unparseable version on either side is never an update.</summary>
    public static bool IsNewer(string? candidate, string? installed) =>
        TryParse(candidate, out var c) && TryParse(installed, out var i) && c.CompareTo(i) > 0;
}

/// <summary>
/// What an extension stored on a game, under Game.Ext[extension id]. The stamp is what the pass
/// reads to decide whether to ask again; the data is whatever the extension returned, as it was.
/// </summary>
public class ExtRecord
{
    /// <summary>When it was fetched.</summary>
    public DateTime At { get; set; }
    /// <summary>The extension's version at the time. A new version is asked again, because it may
    /// fetch something the old one did not.</summary>
    public string? Ext { get; set; }
    /// <summary>False when the extension had nothing for this game: asked again after RetryAfterDays.</summary>
    public bool Found { get; set; }
    public JsonElement? Data { get; set; }
}
