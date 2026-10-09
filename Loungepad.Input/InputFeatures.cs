using Microsoft.Win32;

namespace Loungepad.Input;

// Administrator-controlled feature switches, separate from the user-editable mapping profile.
internal sealed record InputFeatures(bool Uac, bool SignIn)
{
    public bool Enabled => Uac || SignIn;
    public static InputFeatures FromRegistry(bool enabled, object? uac, object? signIn) => new(
        enabled && (uac is int u ? u == 1 : true),
        enabled && (signIn is int s ? s == 1 : true));

    public static InputFeatures Load()
    {
        using var key = Registry.LocalMachine.OpenSubKey(MachineInputSettings.RegistryPath);
        return FromRegistry(key?.GetValue("Enabled") is int enabled && enabled == 1,
            key?.GetValue("UacEnabled"), key?.GetValue("SignInEnabled"));
    }

    public void Save()
    {
        using var key = Registry.LocalMachine.CreateSubKey(MachineInputSettings.RegistryPath);
        key.SetValue("UacEnabled", Uac ? 1 : 0, RegistryValueKind.DWord);
        key.SetValue("SignInEnabled", SignIn ? 1 : 0, RegistryValueKind.DWord);
        key.SetValue("Enabled", Enabled ? 1 : 0, RegistryValueKind.DWord);
    }

    // UAC on Winlogon has an unlocked, authenticated console session. Unknown state is
    // disabled when the switches differ, rather than guessing which opt-in applies.
    public bool Allows(string desktop, bool? signedInAndUnlocked) => desktop switch
    {
        // Keep normal-desktop capture/profile sync available for either feature.
        "Default" => Enabled,
        "Winlogon" => signedInAndUnlocked switch { true => Uac, false => SignIn, null => Uac && SignIn },
        _ => false,
    };
}
