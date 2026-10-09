using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Loungepad.Services;

// Download/extraction runs without elevation. Only the installer's protected, verified copy runs as SYSTEM.
internal static class InputServicePackage
{
    private const long MaxDownload = 256L * 1024 * 1024;
    private const long MaxExpanded = 768L * 1024 * 1024;
    public static string AssetName(Version version) => $"Loungepad.InputService-v{version.Major}.{version.Minor}.{version.Build}-x64.zip";

    public static async Task Download(HttpClient http, Version version, string destination, Action<int> progress, CancellationToken ct)
    {
        string tag = $"v{version.Major}.{version.Minor}.{version.Build}";
        using var release = await http.GetAsync($"https://api.github.com/repos/sukumar-v/Loungepad/releases/tags/{tag}", HttpCompletionOption.ResponseHeadersRead, ct);
        if (release.StatusCode == HttpStatusCode.NotFound)
            throw new InputServicePackageMissingException($"The input service for {tag} has not been published yet. Try again after the release is available.");
        release.EnsureSuccessStatusCode();
        using var metadata = new MemoryStream();
        await CopyBounded(await release.Content.ReadAsStreamAsync(ct), metadata, 2 * 1024 * 1024, ct);
        var root = JsonNode.Parse(metadata.ToArray());
        if (root?["tag_name"]?.GetValue<string>() != tag) throw new IOException("Unexpected input service release");
        var asset = root?["assets"]?.AsArray().FirstOrDefault(a => a?["name"]?.GetValue<string>() == AssetName(version));
        if (asset is null) throw new InputServicePackageMissingException($"The {tag} release does not include the input service yet.");
        long size = asset["size"]?.GetValue<long>() ?? 0;
        string digest = asset["digest"]?.GetValue<string>() ?? "";
        if (size is < 1 or > MaxDownload || !digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase)
            || digest.Length != 71 || !digest[7..].All(Uri.IsHexDigit)) throw new IOException("Missing or invalid input service checksum");
        if (!Uri.TryCreate(asset["browser_download_url"]?.GetValue<string>(), UriKind.Absolute, out var url)
            || url.Scheme != "https" || url.Host != "github.com" || url.UserInfo.Length != 0 || !url.IsDefaultPort
            || url.AbsolutePath != $"/sukumar-v/Loungepad/releases/download/{tag}/{AssetName(version)}")
            throw new IOException("Unexpected input service download address");
        using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength is { } length && length != size) throw new IOException("Input service download size mismatch");
        await using var input = await response.Content.ReadAsStreamAsync(ct);
        await using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] buffer = new byte[81920]; long total = 0; int previous = -1;
        int count;
        while ((count = await input.ReadAsync(buffer, ct)) > 0)
        {
            total += count;
            if (total > size) throw new IOException("Input service download exceeds expected size");
            hash.AppendData(buffer, 0, count);
            await output.WriteAsync(buffer.AsMemory(0, count), ct);
            int percent = (int)(total * 100 / size);
            if (percent != previous) { previous = percent; progress(percent); }
        }
        if (total != size || !Convert.ToHexString(hash.GetHashAndReset()).Equals(digest[7..], StringComparison.OrdinalIgnoreCase))
            throw new IOException("Input service download checksum mismatch");
    }

    public static async Task Extract(string archive, string destination, CancellationToken ct)
    {
        string root = Path.GetFullPath(destination).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        Directory.CreateDirectory(root);
        using var zip = ZipFile.OpenRead(archive);
        if (zip.Entries.Count > 4096) throw new IOException("Too many files in input service package");
        long total = 0;
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in zip.Entries)
        {
            ct.ThrowIfCancellationRequested();
            string name = entry.FullName.Replace('\\', '/');
            bool directory = name.EndsWith('/');
            string[] parts = name.TrimEnd('/').Split('/');
            if (parts.Any(p => p.Length == 0 || p is "." or ".." || p.EndsWith('.') || p.EndsWith(' ')
                || p.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
                || Regex.IsMatch(p.Split('.')[0], "^(CON|PRN|AUX|NUL|COM[1-9¹²³]|LPT[1-9¹²³])$", RegexOptions.IgnoreCase))
                || ((entry.ExternalAttributes >> 16) & 0xF000) == 0xA000 || (entry.ExternalAttributes & 0x400) != 0
                || !names.Add(name.TrimEnd('/'))) throw new IOException("Unsafe path in input service package");
            string path = Path.GetFullPath(Path.Combine(root, Path.Combine(parts)));
            if (!path.StartsWith(root, StringComparison.OrdinalIgnoreCase)) throw new IOException("Input service file escaped extraction directory");
            if (directory) { if (entry.Length != 0) throw new IOException("Invalid package directory"); Directory.CreateDirectory(path); continue; }
            total += entry.Length;
            if (entry.Length is < 0 or > 128L * 1024 * 1024 || total > MaxExpanded) throw new IOException("Input service package is too large");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await using var source = entry.Open();
            await using var target = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true);
            long copied = await CopyBounded(source, target, entry.Length, ct);
            if (copied != entry.Length) throw new IOException("Truncated input service file");
        }
        foreach (string file in new[] { "Loungepad.Service.exe", "Loungepad.Input.cat", "Agent/Loungepad.InputAgent.exe" })
            if (!File.Exists(Path.Combine(root, file))) throw new IOException("Incomplete input service package");
    }

    private static async Task<long> CopyBounded(Stream input, Stream output, long maximum, CancellationToken ct)
    {
        byte[] buffer = new byte[81920]; long total = 0; int count;
        while ((count = await input.ReadAsync(buffer, ct)) > 0)
        {
            total += count;
            if (total > maximum) throw new IOException("Input service response exceeds size limit");
            await output.WriteAsync(buffer.AsMemory(0, count), ct);
        }
        return total;
    }
}

/// <summary>The release for this version has no input service package (not published yet, or
/// published without one). Raised before anything is elevated, so a caller that only wanted to
/// update the service can fall back to configuring the one that is installed.</summary>
internal sealed class InputServicePackageMissingException(string message) : IOException(message);
