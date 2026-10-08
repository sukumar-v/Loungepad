using System.Text.Json;
using Microsoft.Win32;

namespace Loungepad.Input;

// Only input preferences cross the privilege boundary. Never serialize AppSettings here:
// it contains store credentials, paths and URLs that a SYSTEM process must not consume.
internal sealed record InputProfile
{
    public bool MouseEnabled { get; init; } = true;
    public double Deadzone { get; init; } = .18;
    public double Sensitivity { get; init; } = 1;
    public double AccelExponent { get; init; } = 1.8;
    public string BoostButton { get; init; } = "RT";
    public double BoostMultiplier { get; init; } = 2.5;
    public string LeftClick { get; init; } = "A";
    public string RightClick { get; init; } = "B";
    public bool TouchpadMouse { get; init; } = true;
    public double TouchpadSensitivity { get; init; } = 1;
    public bool TouchpadTapToClick { get; init; } = true;
    public bool TouchpadTapDrag { get; init; } = true;
    public bool TouchpadNaturalScroll { get; init; } = true;
    public double TouchpadScrollSpeed { get; init; } = 1;
    public string KeyboardApp { get; init; } = "Builtin";
    public string KeyboardToggle { get; init; } = "Back";
    public string KeyboardToggleMode { get; init; } = "Press";
    public int KeyboardToggleHoldMs { get; init; } = 400;
    public double KeyboardScale { get; init; } = 1;
    public bool FunctionKeys { get; init; }
    public bool NavKeys { get; init; }
    public bool Numpad { get; init; }
    public bool Modifiers { get; init; }
    public int RepeatDelayMs { get; init; } = 350;
    public int RepeatIntervalMs { get; init; } = 90;
    public string? Display { get; init; }

    public InputProfile Validate()
    {
        static double Range(double v, double min, double max) => double.IsFinite(v) && v >= min && v <= max
            ? v : throw new IOException("Input setting out of range");
        Range(Deadzone, .05, .4); Range(Sensitivity, .2, 3); Range(AccelExponent, 1, 3);
        Range(BoostMultiplier, 1.5, 5); Range(TouchpadSensitivity, .25, 4);
        Range(TouchpadScrollSpeed, .25, 4); Range(KeyboardScale, .3, 1.6);
        if (KeyboardApp is not ("Builtin" or "Osk" or "TabTip") || KeyboardToggleMode is not ("Press" or "Hold")
            || KeyboardToggleHoldMs is < 100 or > 5000 || RepeatDelayMs is < 120 or > 2000
            || RepeatIntervalMs is < 20 or > 1000 || Display?.Length > 64)
            throw new IOException("Invalid keyboard settings");
        foreach (string button in new[] { BoostButton, LeftClick, RightClick, KeyboardToggle })
            if (button is not ("Off" or "A" or "B" or "X" or "Y" or "LB" or "RB" or "LT" or "RT"
                or "LS" or "RS" or "Back" or "View" or "Start" or "Menu" or "Guide"))
                throw new IOException("Unknown controller button");
        return this;
    }
}

internal static class MachineInputSettings
{
    public const string RegistryPath = @"SOFTWARE\Loungepad\Input";
    public static InputProfile Load()
    {
        using var key = Registry.LocalMachine.OpenSubKey(RegistryPath);
        string? json = key?.GetValue("Profile") as string;
        return json is null ? new() : Parse(json);
    }
    public static InputProfile Parse(string json)
    {
        if (json.Length > 8192) throw new IOException("Input profile too large");
        return (JsonSerializer.Deserialize<InputProfile>(json, Protocol.Json) ?? throw new IOException("Missing input profile")).Validate();
    }
    public static void Save(InputProfile profile)
    {
        using var key = Registry.LocalMachine.CreateSubKey(RegistryPath);
        key.SetValue("Profile", JsonSerializer.Serialize(profile.Validate(), Protocol.Json), RegistryValueKind.String);
    }
    public static string? Error()
    {
        using var key = Registry.LocalMachine.OpenSubKey(RegistryPath);
        return key?.GetValue("LastError") as string;
    }
    public static AgentHealth? Agent()
    {
        using var key = Registry.LocalMachine.OpenSubKey(RegistryPath);
        if (key?.GetValue("AgentHealth") is not string json || json.Length > 4096) return null;
        try { return JsonSerializer.Deserialize<AgentHealth>(json, Protocol.Json); }
        catch (JsonException) { return null; }
    }
    public static void ReportAgent(AgentHealth agent)
    {
        using var key = Registry.LocalMachine.CreateSubKey(RegistryPath);
        key.SetValue("AgentHealth", JsonSerializer.Serialize(agent, Protocol.Json), RegistryValueKind.String);
    }
    public static void ClearAgent()
    {
        using var key = Registry.LocalMachine.CreateSubKey(RegistryPath);
        key.DeleteValue("AgentHealth", false);
    }
    public static void ReportError(string? error)
    {
        using var key = Registry.LocalMachine.CreateSubKey(RegistryPath);
        if (error is null) key.DeleteValue("LastError", false);
        else key.SetValue("LastError", error, RegistryValueKind.String);
    }
}
