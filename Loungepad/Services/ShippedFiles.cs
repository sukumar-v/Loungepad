using System.IO;
using System.Reflection;

namespace Loungepad.Services;

/// <summary>
/// The files that ship inside Loungepad.exe: the page (ui/), the bundled themes (themes/) and the
/// Vortex extension (vortex-bridge/). They are embedded resources named by their path in the
/// project (see the csproj), which is what makes a release one exe rather than an exe that has to
/// be kept beside a folder.
///
/// Paths are relative and use forward slashes, as a URL does: "ui/player/player.js". Lookups
/// ignore case, like the file system they used to come from.
/// </summary>
public static class ShippedFiles
{
    private const string Prefix = "shipped/";

    private static readonly Assembly Self = typeof(ShippedFiles).Assembly;

    /// <summary>Path to resource name. MSBuild writes RelativeDir with backslashes, so the names
    /// are folded to forward slashes once here rather than at every lookup.</summary>
    private static readonly Dictionary<string, string> Index = BuildIndex();

    private static Dictionary<string, string> BuildIndex()
    {
        var index = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in Self.GetManifestResourceNames())
        {
            var path = name.Replace('\\', '/');
            if (path.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase))
                index[path[Prefix.Length..]] = name;
        }
        return index;
    }

    public static bool Exists(string path) => Index.ContainsKey(Normalize(path));

    /// <summary>The file's contents as a stream, or null when nothing of that name shipped.</summary>
    public static Stream? Open(string path) =>
        Index.TryGetValue(Normalize(path), out var name) ? Self.GetManifestResourceStream(name) : null;

    public static byte[]? ReadAllBytes(string path)
    {
        using var s = Open(path);
        if (s is null) return null;
        using var ms = new MemoryStream();
        s.CopyTo(ms);
        return ms.ToArray();
    }

    public static string? ReadAllText(string path)
    {
        using var s = Open(path);
        if (s is null) return null;
        using var reader = new StreamReader(s);
        return reader.ReadToEnd();
    }

    /// <summary>The files directly inside a folder, as full paths ("themes/loungepad/theme.css"),
    /// in name order.</summary>
    public static IReadOnlyList<string> Files(string folder)
    {
        var prefix = Normalize(folder).TrimEnd('/') + "/";
        return Index.Keys
            .Where(p => p.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && p.IndexOf('/', prefix.Length) < 0)
            .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>The names of the folders directly inside a folder: "themes" gives "loungepad".</summary>
    public static IReadOnlyList<string> Folders(string folder)
    {
        var prefix = Normalize(folder).TrimEnd('/') + "/";
        return Index.Keys
            .Where(p => p.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            .Select(p => p[prefix.Length..])
            .Where(rest => rest.Contains('/'))
            .Select(rest => rest[..rest.IndexOf('/')])
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static string Normalize(string path) => path.Replace('\\', '/').TrimStart('/');
}
