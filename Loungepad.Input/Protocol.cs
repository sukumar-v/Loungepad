using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using Loungepad.Interop;
using Loungepad.Services;

namespace Loungepad.Input;

internal static class Protocol
{
    public const int Version = 1;
    public const string ServiceName = "Loungepad.Service";
    public const string StatusPipe = "Loungepad.Service.Status.v1";
    public static string AgentPipe(int session) => $"Loungepad.Input.v1.{session}";
    public static readonly JsonSerializerOptions Json = new() { IncludeFields = true };
    public const int MaxMessageBytes = 32 * 1024;

    // Length-prefixed, bounded messages: a client cannot grow a SYSTEM process's line buffer.
    public static async Task Write<T>(Stream stream, T value, CancellationToken ct)
    {
        byte[] body = JsonSerializer.SerializeToUtf8Bytes(value, Json);
        if (body.Length > MaxMessageBytes) throw new IOException("Input message too large");
        await stream.WriteAsync(BitConverter.GetBytes(body.Length), ct);
        await stream.WriteAsync(body, ct);
        await stream.FlushAsync(ct);
    }

    public static async Task<T> Read<T>(Stream stream, CancellationToken ct)
    {
        byte[] header = new byte[4];
        await stream.ReadExactlyAsync(header, ct);
        int size = BitConverter.ToInt32(header);
        if (size is < 1 or > MaxMessageBytes) throw new IOException("Invalid input message size");
        byte[] body = new byte[size];
        await stream.ReadExactlyAsync(body, ct);
        return JsonSerializer.Deserialize<T>(body, Json) ?? throw new IOException("Missing input message");
    }

    public static NamedPipeServerStream Server(string name, SecurityIdentifier? user = null)
    {
        var acl = new PipeSecurity();
        acl.SetAccessRuleProtection(true, false);
        var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
        acl.SetOwner(system);
        acl.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.NetworkSid, null),
            PipeAccessRights.FullControl, AccessControlType.Deny));
        acl.AddAccessRule(new PipeAccessRule(system, PipeAccessRights.FullControl, AccessControlType.Allow));
        acl.AddAccessRule(new PipeAccessRule(user ?? new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null),
            PipeAccessRights.ReadWrite | PipeAccessRights.ReadPermissions, AccessControlType.Allow));
        return NamedPipeServerStreamAcl.Create(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.FirstPipeInstance, 4096, 4096, acl);
    }

    public static void VerifyServer(NamedPipeClientStream pipe)
    {
        var owner = pipe.GetAccessControl().GetOwner(typeof(SecurityIdentifier));
        if (owner?.Equals(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null)) != true)
            throw new UnauthorizedAccessException("Input pipe is not owned by SYSTEM");
    }
}

internal sealed record ServiceStatus(bool Enabled, int? Session, string State, string? Error = null, AgentHealth? Agent = null);
// The timing fields describe the worker's last health period (about a second): how far apart
// its ticks ran and the slowest capture, mapping and injection inside one. They are how a
// slow pointer is told apart from a slow loop without a debugger on a SYSTEM process.
internal sealed record AgentHealth(long Tick, int Session, int ProcessId, string Desktop, string Mode, bool ControllerPresent, bool Ready,
    long SuppressedNavigationEvents = 0, double TickMeanMs = 0, double TickMaxMs = 0, double CaptureMaxMs = 0, double MapMaxMs = 0,
    double SendMaxMs = 0, int SendShort = 0, int Ticks = 0, double XInputProbeMaxMs = 0, int XInputSlots = 0);
// MovePointer and ScrollWheel are the launcher's policy for this tick: whether the left stick is
// a mouse right now and whether the right stick is a wheel. The agent then moves the pointer
// itself, from its own reading, in its own loop (see DesktopWorker.Tick). PointerOwner is the
// agent saying it does that. All three default to off, so the two sides can be updated apart:
// an older agent never claims the pointer and the launcher moves it through the pipe as before;
// an older launcher never asks, and the new agent injects only what it is sent.
internal sealed record AgentRequest(int Version, InputEvent[] Events, bool Quiet = false, InputProfile? Profile = null,
    bool MovePointer = false, bool ScrollWheel = false);
internal sealed record AgentReply(int Version, bool DefaultDesktop, bool XInputPresent,
    NativeMethods.XINPUT_STATE Xbox, HidPadSnapshot Hid, bool PointerOwner = false);

// No native pointers or INPUT unions on the wire.
internal sealed record InputEvent(uint Type, int X, int Y, uint Data, uint Flags, ushort Key, ushort Scan)
{
    public static InputEvent From(NativeMethods.INPUT input) => input.type == NativeMethods.INPUT_MOUSE
        ? new(input.type, input.u.mi.dx, input.u.mi.dy, input.u.mi.mouseData, input.u.mi.dwFlags, 0, 0)
        : new(input.type, 0, 0, 0, input.u.ki.dwFlags, input.u.ki.wVk, input.u.ki.wScan);

    public NativeMethods.INPUT ToNative()
    {
        if (Type == NativeMethods.INPUT_MOUSE && (Flags & ~0xF81Fu) == 0)
            return new() { type = Type, u = new() { mi = new() { dx = X, dy = Y, mouseData = Data, dwFlags = Flags } } };
        if (Type == NativeMethods.INPUT_KEYBOARD && (Flags & ~0xFu) == 0)
            return new() { type = Type, u = new() { ki = new() { wVk = Key, wScan = Scan, dwFlags = Flags } } };
        throw new IOException("Unsupported input event");
    }
}
