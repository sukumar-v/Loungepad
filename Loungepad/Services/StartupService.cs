using System.Diagnostics;
using Microsoft.Win32;

namespace Loungepad.Services;

/// <summary>Registers/unregisters Loungepad to auto-start on user login (HKCU Run key).</summary>
public static class StartupService
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "Loungepad";

    public static bool IsRegistered()
    {
        using var k = Registry.CurrentUser.OpenSubKey(RunKey);
        return k?.GetValue(ValueName) is not null;
    }

    public static void SetRegistered(bool enabled)
    {
        using var k = Registry.CurrentUser.CreateSubKey(RunKey);
        if (enabled)
        {
            var exe = Environment.ProcessPath ?? Process.GetCurrentProcess().MainModule?.FileName;
            if (exe is not null) k.SetValue(ValueName, $"\"{exe}\"");
        }
        else
        {
            k.DeleteValue(ValueName, throwOnMissingValue: false);
        }
    }
}
