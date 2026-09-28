using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;

namespace Loungepad.Services;

/// <summary>
/// The hash RetroAchievements identifies a ROM by, computed the way its own clients do it
/// (docs.retroachievements.org, "Game Identification"). For most cartridge systems it is the MD5
/// of the file as it is; the exceptions below strip a header the dump tools added or put the
/// bytes in the order the console reads them, so two dumps of one cartridge hash the same.
///
/// Disc systems are deliberately left out: their hash is built from a handful of files read
/// off the disc image's file system, which needs a CD image reader this launcher does not have.
/// A game on one of those is matched to RetroAchievements by title instead, which is weaker,
/// and the provider says so. A ROM kept in a zip is hashed by its content -- RetroAchievements
/// hashes the ROM inside, not the archive -- taking the largest entry, which is the game.
/// </summary>
public static class RetroHash
{
    /// <summary>Anything bigger is not a cartridge (an N64 cartridge tops out at 64 MB).</summary>
    private const long MaxBytes = 64L * 1024 * 1024;

    /// <summary>"Arcade": the set's name, not its bytes. Everything else: bytes, with the
    /// system's header rule. Null for a system this cannot hash.</summary>
    public static string? Compute(string path, string platformId)
    {
        try
        {
            if (platformId is "arcade" or "neogeo")
                return Md5(Encoding.ASCII.GetBytes(Path.GetFileNameWithoutExtension(path).ToLowerInvariant()));
            if (!Hashable(platformId)) return null;

            var bytes = ReadRom(path);
            if (bytes is null) return null;
            return Md5(Prepare(bytes, platformId));
        }
        catch (Exception ex)
        {
            Log.Info($"RetroAchievements: could not hash {Path.GetFileName(path)}: {ex.Message}");
            return null;
        }
    }

    /// <summary>The systems whose hash is a plain (headerless) MD5 of the cartridge image.</summary>
    public static bool Hashable(string platformId) => platformId is
        "nes" or "snes" or "n64" or "gb" or "gbc" or "gba" or "vb" or "sms" or "genesis" or "32x" or "gg"
        or "tg16" or "atari2600" or "atari7800" or "lynx" or "jaguar" or "ngp" or "ws" or "msx" or "intv" or "coleco"
        or "arcade" or "neogeo";

    /// <summary>The bytes RetroAchievements hashes for this system: public so a harness can check
    /// each header rule against a synthetic file.</summary>
    public static byte[] Prepare(byte[] rom, string platformId)
    {
        switch (platformId)
        {
            case "nes":
                // iNES ("NES\x1a") and FDS ("FDS\x1a") dumps carry a 16-byte header the console never sees.
                if (rom.Length > 16 && ((rom[0] == 0x4E && rom[1] == 0x45 && rom[2] == 0x53 && rom[3] == 0x1A)
                                        || (rom[0] == 0x46 && rom[1] == 0x44 && rom[2] == 0x53 && rom[3] == 0x1A)))
                    return rom[16..];
                return rom;
            case "snes":
                // A copier header is 512 bytes on the front of an image that is otherwise a whole number of 8 KB banks.
                return rom.Length > 512 && rom.Length % 8192 == 512 ? rom[512..] : rom;
            case "tg16":
                // Same idea, 128 KB banks.
                return rom.Length > 512 && rom.Length % 131072 == 512 ? rom[512..] : rom;
            case "lynx":
                return rom.Length > 64 && rom[0] == (byte)'L' && rom[1] == (byte)'Y' && rom[2] == (byte)'N' && rom[3] == (byte)'X' ? rom[64..] : rom;
            case "atari7800":
                // "\x01ATARI7800" opens the 128-byte header the A78 format adds.
                return rom.Length > 128 && rom[1] == (byte)'A' && rom[2] == (byte)'T' && rom[3] == (byte)'A' && rom[4] == (byte)'R' && rom[5] == (byte)'I' ? rom[128..] : rom;
            case "n64":
                return N64BigEndian(rom);
            default:
                return rom;
        }
    }

    /// <summary>An N64 image is hashed in .z64 (big-endian) order whatever order it was dumped in:
    /// .v64 is the same bytes swapped in pairs, .n64 in fours. The first word says which.</summary>
    private static byte[] N64BigEndian(byte[] rom)
    {
        if (rom.Length < 4 || rom.Length % 4 != 0) return rom;
        if (rom[0] == 0x80 && rom[1] == 0x37 && rom[2] == 0x12 && rom[3] == 0x40) return rom;      // z64, native
        var outp = new byte[rom.Length];
        if (rom[0] == 0x37 && rom[1] == 0x80 && rom[2] == 0x40 && rom[3] == 0x12)                   // v64, 16-bit swapped
        {
            for (var i = 0; i < rom.Length; i += 2) { outp[i] = rom[i + 1]; outp[i + 1] = rom[i]; }
            return outp;
        }
        if (rom[0] == 0x40 && rom[1] == 0x12 && rom[2] == 0x37 && rom[3] == 0x80)                   // n64, 32-bit little-endian
        {
            for (var i = 0; i < rom.Length; i += 4) { outp[i] = rom[i + 3]; outp[i + 1] = rom[i + 2]; outp[i + 2] = rom[i + 1]; outp[i + 3] = rom[i]; }
            return outp;
        }
        return rom;
    }

    private static byte[]? ReadRom(string path)
    {
        var info = new FileInfo(path);
        if (!info.Exists) return null;
        if (!info.Extension.Equals(".zip", StringComparison.OrdinalIgnoreCase))
            return info.Length > MaxBytes ? null : File.ReadAllBytes(path);

        using var zip = ZipFile.OpenRead(path);
        var entry = zip.Entries.Where(e => e.Length > 0).OrderByDescending(e => e.Length).FirstOrDefault();
        if (entry is null || entry.Length > MaxBytes) return null;
        using var s = entry.Open();
        using var ms = new MemoryStream((int)entry.Length);
        s.CopyTo(ms);
        return ms.ToArray();
    }

    public static string Md5(byte[] bytes) => Convert.ToHexString(MD5.HashData(bytes)).ToLowerInvariant();
}
