namespace Loungepad.Services;

// The shared input code must not write to a user's profile from SYSTEM.
internal static class InputLog
{
    public static Action<string>? Sink;
    public static void Info(string message) => Sink?.Invoke(message);
}
