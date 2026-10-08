namespace Loungepad.Services;

public record DisplayInfo(string DeviceName, string FriendlyName, int X, int Y, int Width, int Height, bool IsPrimary);
internal static class Log
{
    public static void Info(string message) { }
}
