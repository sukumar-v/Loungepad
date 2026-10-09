using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Loungepad.Input;

namespace Loungepad.Services;

internal static class InputServiceSetup
{
    public static string ServiceExe => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), @"Loungepad\Input\Loungepad.Service.exe");

    /// <summary>The version the installed service was built as: its exe's file version, which
    /// package-input-service.ps1 stamps with the release's number. Null when there is no service or
    /// the file cannot be read.</summary>
    public static Version? InstalledVersion()
    {
        try
        {
            if (!File.Exists(ServiceExe)) return null;
            var info = FileVersionInfo.GetVersionInfo(ServiceExe);
            return info.FileMajorPart == 0 && info.FileMinorPart == 0 ? null : new Version(info.FileMajorPart, info.FileMinorPart, info.FileBuildPart);
        }
        catch (Exception) { return null; }
    }

    /// <summary>The service this launcher installs: the one published with its own release.</summary>
    public static Version Bundled => new(UpdateService.Current.Major, UpdateService.Current.Minor, Math.Max(0, UpdateService.Current.Build));

    /// <summary>Whether this launcher can install the service at all: only a signed release build
    /// can, because the installer checks the package against the launcher's own publisher. Read
    /// once; the signature does not change while the process runs.</summary>
    public static bool CanInstall => _canInstall.Value;
    private static readonly Lazy<bool> _canInstall = new(() => { try { Publisher(); return true; } catch (IOException) { return false; } });

    public static bool IsInstalled()
    {
        IntPtr manager = OpenSCManager(null, null, 1);
        if (manager == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
        try
        {
            IntPtr service = OpenService(manager, Protocol.ServiceName, 4); // QUERY_STATUS
            if (service != IntPtr.Zero) { CloseServiceHandle(service); return true; }
            int error = Marshal.GetLastWin32Error();
            if (error == 1060) return false;
            if (error == 1072) return true; // SCM deletion is pending while another management handle is open
            throw new Win32Exception(error);
        }
        finally { CloseServiceHandle(manager); }
    }

    public static async Task Install(string profile, InputFeatures features, Action<string, int?> report, CancellationToken ct)
    {
        string publisher = Publisher(); // fail unsigned dev builds before downloading anything
        string work = Path.Combine(Path.GetTempPath(), "Loungepad.InputService." + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(15) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("Loungepad/" + UpdateService.Format(UpdateService.Current));
            http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
            string archive = Path.Combine(work, "package.zip"), files = Path.Combine(work, "package");
            report("Downloading input service", 0);
            await InputServicePackage.Download(http, UpdateService.Current, archive, p => report("Downloading input service", p), ct);
            report("Preparing input service", null);
            await Task.Run(() => InputServicePackage.Extract(archive, files, ct), ct);
            ct.ThrowIfCancellationRequested();
            report("Waiting for administrator approval / installing", null);
            // The downloaded .ps1 is never executed. Installer code comes from this signed launcher.
            await Elevate("install-input-service.ps1", " -SourcePath " + Quote(files)
                + " -ExpectedPublisher " + Quote(publisher) + " -EnableProfile " + Quote(profile)
                + $" -UacEnabled {(features.Uac ? 1 : 0)} -SignInEnabled {(features.SignIn ? 1 : 0)}");
            if (!IsInstalled()) throw new IOException("Windows did not register the input service");
        }
        finally
        {
            // Only this operation's generated directory. Never follow redirected paths during cleanup.
            try
            {
                // Directory.Delete removes directory links themselves rather than recursing into their targets.
                string full = Path.GetFullPath(work);
                string prefix = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
                if (full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                    && Path.GetFileName(full).StartsWith("Loungepad.InputService.", StringComparison.Ordinal)
                    && (File.GetAttributes(full) & FileAttributes.ReparsePoint) == 0) Directory.Delete(full, true);
            }
            catch { /* a locked download may remain in temp; installation does not depend on cleanup */ }
        }
    }

    public static async Task Uninstall()
    {
        string sid = System.Security.Principal.WindowsIdentity.GetCurrent().User!.Value;
        await Elevate("uninstall-input-service.ps1", " -UserSid " + Quote(sid));
        if (IsInstalled()) throw new IOException("Windows still has the service registered; close service-management windows and try again");
    }

    private static string Publisher()
    {
        try
        {
            using var certificate = new X509Certificate2(X509Certificate.CreateFromSignedFile(Environment.ProcessPath!));
            return certificate.Subject;
        }
        catch (Exception ex) when (ex is System.Security.Cryptography.CryptographicException or ArgumentException)
        { throw new IOException("Install the input service using the signed Loungepad release build", ex); }
    }

    private static string Quote(string value) => "'" + value.Replace("'", "''") + "'";

    private static async Task Elevate(string resource, string parameters)
    {
        string script = ShippedFiles.ReadAllText("setup/" + resource) ?? throw new IOException("Missing bundled input service installer");
        string command = "$ErrorActionPreference='Stop'; try { & {\n" + script + "\n}" + parameters + "; exit 0 } catch { Write-Error $_ -ErrorAction Continue; exit 1 }";
        string arguments = "-NoProfile -NonInteractive -EncodedCommand " + Convert.ToBase64String(Encoding.Unicode.GetBytes(command));
        if (arguments.Length > 30000) throw new IOException("Input service setup command is too large");
        string powershell = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), @"System32\WindowsPowerShell\v1.0\powershell.exe");
        try
        {
            using var process = Process.Start(new ProcessStartInfo(powershell, arguments)
            { UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden }) ?? throw new IOException("Could not start input service setup");
            // Once approved, finish the transaction even if the launcher is closing.
            await process.WaitForExitAsync();
            if (process.ExitCode != 0) throw new IOException("Input service setup failed. Retry or use the signed package's installer for details.");
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223) { throw new OperationCanceledException("Administrator approval was cancelled", ex); }
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr OpenSCManager(string? machine, string? database, uint access);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr OpenService(IntPtr manager, string name, uint access);
    [DllImport("advapi32.dll")] private static extern bool CloseServiceHandle(IntPtr handle);
}
