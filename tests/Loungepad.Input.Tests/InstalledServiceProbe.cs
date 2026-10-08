using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Security.Principal;
using Loungepad.Input;
using Loungepad.InputAgent;
using Loungepad.Interop;

// Explicit opt-in integration check against an installed service. Unlike the default
// suite, this moves the real cursor by twelve pixels and restores it. No clicks or keys.
internal static class InstalledServiceProbe
{
    public static async Task Run(string? expectedForeground = null)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var ct = deadline.Token;
        var initial = await Status(ct);
        Require(initial.Agent is { Desktop: "Default", ControllerPresent: true, Ready: true },
            "A ready controller and Default desktop worker are required");
        int worker = initial.Agent!.ProcessId;
        using (var pipe = Client(Protocol.AgentPipe(Process.GetCurrentProcess().SessionId)))
        {
            await pipe.ConnectAsync(ct);
            Protocol.VerifyServer(pipe);
            var snapshot = await Request(pipe, [], ct);
            Require(snapshot.XInputPresent || snapshot.Hid.Present, "Controller must be neutral for IPC handoff");
            Console.WriteLine($"PASS: authenticated SYSTEM input pipe; Xbox={snapshot.XInputPresent}, HID={snapshot.Hid.Present}");
            Require(NativeMethods.GetCursorPos(out var original), "Cannot read cursor");
            NativeMethods.GetWindowThreadProcessId(NativeMethods.GetForegroundWindow(), out uint foregroundId);
            using var foreground = Process.GetProcessById(checked((int)foregroundId));
            Require(expectedForeground is null || foreground.ProcessName.Equals(expectedForeground, StringComparison.OrdinalIgnoreCase),
                $"Expected foreground {expectedForeground}, found {foreground.ProcessName}");
            Console.WriteLine($"Foreground test target: {foreground.ProcessName}");
            int left = NativeMethods.GetSystemMetrics(NativeMethods.SM_XVIRTUALSCREEN);
            int width = NativeMethods.GetSystemMetrics(NativeMethods.SM_CXVIRTUALSCREEN);
            int target = original.X + (original.X + 12 < left + width ? 12 : -12);
            try
            {
                await Request(pipe, Move(target, original.Y), ct);
                await Task.Delay(60, ct);
                Require(NativeMethods.GetCursorPos(out var actual), "Cannot read moved cursor");
                Require(Math.Abs(actual.X - target) <= 2 && Math.Abs(actual.Y - original.Y) <= 2,
                    $"Injected cursor movement was not observed (expected X={target}, actual X={actual.X})");
                Console.WriteLine("PASS: real cursor movement through installed service");
            }
            finally
            {
                if (DesktopApi.IsCurrent("Default") && pipe.IsConnected)
                {
                    await Request(pipe, Move(original.X, original.Y), ct);
                    Console.WriteLine("Cursor restored; no buttons or keys injected.");
                }
            }
        }
        for (int i = 0; i < 6; i++)
        {
            await Task.Delay(1000, ct);
            var current = await Status(ct);
            Require(current.Agent?.ProcessId == worker && current.Error is null,
                $"Worker restarted or failed: {current.State}; {current.Error}");
        }
        Console.WriteLine("PASS: worker heartbeat remains healthy after IPC disconnect and background handoff");
    }

    private static NamedPipeClientStream Client(string name) => new(".", name,
        PipeDirection.InOut, PipeOptions.Asynchronous, TokenImpersonationLevel.Identification);
    private static async Task<ServiceStatus> Status(CancellationToken ct)
    {
        using var pipe = Client(Protocol.StatusPipe);
        await pipe.ConnectAsync(ct);
        Protocol.VerifyServer(pipe);
        return await Protocol.Read<ServiceStatus>(pipe, ct);
    }
    private static async Task<AgentReply> Request(NamedPipeClientStream pipe, InputEvent[] events, CancellationToken ct)
    {
        await Protocol.Write(pipe, new AgentRequest(Protocol.Version, events), ct);
        return await Protocol.Read<AgentReply>(pipe, ct);
    }
    private static InputEvent[] Move(int x, int y)
    {
        InputEvent[] events = [];
        var old = NativeMethods.InputSink;
        try
        {
            NativeMethods.InputSink = input => { events = input.Select(InputEvent.From).ToArray(); return (uint)input.Length; };
            NativeMethods.MoveCursorTo(x, y);
        }
        finally { NativeMethods.InputSink = old; }
        return events;
    }
    private static void Require(bool condition, string message)
    {
        if (!condition) throw new IOException(message);
    }
}
