using System.IO;
using System.IO.Pipes;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using Loungepad;
using Loungepad.Input;
using Loungepad.InputAgent;
using Loungepad.Interop;
using Loungepad.Services;
using static Loungepad.Interop.NativeMethods;

internal static class Program
{
    private static int _checks;
    [STAThread]
    public static int Main(string[] args)
    {
        try
        {
            if (args is ["--navigation-hook-probe"])
            {
                using var hook = new GamepadNavigationFilter(() => false);
                var dispatcher = System.Windows.Threading.Dispatcher.CurrentDispatcher;
                var timer = new System.Windows.Threading.DispatcherTimer(TimeSpan.FromMilliseconds(100),
                    System.Windows.Threading.DispatcherPriority.Normal,
                    (_, _) => dispatcher.BeginInvokeShutdown(System.Windows.Threading.DispatcherPriority.Send), dispatcher);
                System.Windows.Threading.Dispatcher.Run();
                timer.Stop();
                Check(hook.BlockedCount == 0, "native navigation hook installs and forwards when disabled");
                return 0;
            }
            if (args is ["--navigation-hook-stall-probe"])
            {
                NavigationHookStallProbe();
                return 0;
            }
            if (args is ["--hid-probe"])
            {
                MotionProbes.Hid();
                return 0;
            }
            if (args.Length == 2 && args[0] == "--pointer-trace" && int.TryParse(args[1], out int traceSeconds))
            {
                MotionProbes.PointerTrace(traceSeconds);
                return 0;
            }
            if (args is ["--dispatcher-cadence-probe"])
            {
                MotionProbes.DispatcherCadence();
                return 0;
            }
            if (args is ["--input-timing-probe"])
            {
                InputTimingProbe.Run();
                return 0;
            }
            if (args.Length is 1 or 2 && args[0] == "--service-mouse-probe")
            {
                InstalledServiceProbe.Run(args.Length == 2 ? args[1] : null).GetAwaiter().GetResult();
                return 0;
            }
            if (args is ["--desktop-probe"])
            {
                using var desktop = DesktopApi.Open();
                DesktopApi.VerifyWorkerDesktop(desktop.Name);
                Check(true, "STA worker accepts its startup desktop without rebinding");
                try { DesktopApi.VerifyWorkerDesktop("NotTheWorkerDesktop"); throw new Exception("Wrong desktop accepted"); }
                catch (InvalidOperationException) { Check(true, "incorrect worker desktop rejected"); }
                return 0;
            }
            Profiles();
            NavigationFilter();
            Handoff();
            Mapping();
            Pointer();
            OnePadPerController();
            Framing().GetAwaiter().GetResult();
            SpoofPipe().GetAwaiter().GetResult();
            SecureKeyboard();
            ServicePackages().GetAwaiter().GetResult();
            Console.WriteLine($"PASS: {_checks} input checks. No real keys were injected.");
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }

    private static void Check(bool condition, string name)
    {
        if (!condition) throw new Exception("FAIL: " + name);
        _checks++; Console.WriteLine("PASS: " + name);
    }

    // A pad on the cable and on Bluetooth at once is one pad, two of the same model are two.
    private static void OnePadPerController()
    {
        Check(HidPad.SamePhysicalPad(0x054C, 0x0CE6, "0C:27:56:5B:1C:5B", 0x054C, 0x0CE6, "0c27565b1c5b"), "the same serial in two spellings is one pad");
        Check(HidPad.SamePhysicalPad(0x054C, 0x0CE6, "", 0x054C, 0x0CE6, "0c27565b1c5b") && HidPad.SamePhysicalPad(0x054C, 0x0CE6, "", 0x054C, 0x0CE6, ""),
            "a missing serial cannot say two instances of one model differ");
        Check(!HidPad.SamePhysicalPad(0x054C, 0x0CE6, "0c27565b1c5b", 0x054C, 0x0CE6, "0c27565b1c5c"), "two serials are two pads of one model");
        Check(!HidPad.SamePhysicalPad(0x054C, 0x0CE6, "", 0x054C, 0x09CC, ""), "a different product is a different pad");
    }

    // The stick as a pointer: one formula for the launcher and the agent, the agent's
    // pointer-only mapping for a connected launcher, the protocol's compatibility with an older
    // peer, and the guard against aiming a move from a position the last move is about to change.
    private static void Pointer()
    {
        const double dz = .18, sens = 1, accel = 1.8;
        var (vx, vy) = StickPointer.Velocity(32767, 0, dz, sens, accel, 1);
        Check(Math.Abs(vx - StickPointer.MaxSpeedPxPerSec) < 1e-6 && vy == 0, "full deflection is the maximum speed, along the stick's own direction");
        var (dx, dy) = StickPointer.Velocity(23170, 23170, dz, sens, accel, 1);   // 45 degrees, |stick| = 1
        Check(Math.Abs(Math.Sqrt(dx * dx + dy * dy) - StickPointer.MaxSpeedPxPerSec) < 1 && dy < 0 && dx > 0,
            "a diagonal push is no faster than a straight one, and up is negative Y");
        // 5000 on each axis is 0.15 per axis but 0.22 in deflection: outside the 0.18 deadzone.
        Check(StickPointer.Velocity(5000, -5000, dz, sens, accel, 1) != (0, 0) && StickPointer.Velocity(4000, -4000, dz, sens, accel, 1) == (0, 0),
            "the deadzone is measured on the stick's deflection, not per axis");
        var (bx, _) = StickPointer.Velocity(32767, 0, dz, sens, accel, 2.5);
        Check(Math.Abs(bx - 2.5 * StickPointer.MaxSpeedPxPerSec) < 1e-6, "boost scales the speed");
        var mover = new StickPointer.Mover();
        int total = 0;
        for (int i = 0; i < 125; i++) total += mover.Step(32767, 0, .008, dz, sens, accel, 1).Dx;
        Check(Math.Abs(total - StickPointer.MaxSpeedPxPerSec) <= 1, "whole pixels per tick carry the fraction: 125 ticks of 8 ms travel one second's worth");
        int slow = 0;
        for (int i = 0; i < 125; i++) slow += mover.Step(9000, 0, .008, dz, sens, accel, 1).Dx;
        Check(slow is > 10 and < 60, "a slow push still adds up over a second");
        Check(mover.Step(0, 0, .008, dz, sens, accel, 1) == (0, 0), "inside the deadzone nothing moves and the carried fraction is dropped");

        var output = new List<INPUT>();
        var original = NativeMethods.InputSink;
        NativeMethods.InputSink = inputs => { output.AddRange(inputs); return (uint)inputs.Length; };
        try
        {
            using var mapper = new SecureMapper(new InputProfile { KeyboardToggle = "Off", LeftClick = "A" }, new InputInjector());
            var pushed = new AgentReply(1, true, true, new() { Gamepad = new() { wButtons = XINPUT_GAMEPAD_A, sThumbLX = 32767, sThumbRY = 32767 } }, default);
            for (int i = 0; i < 25; i++) mapper.MovePointer(pushed, 10 + i * 8, move: true, wheel: true);
            Check(output.Any(i => i.type == INPUT_MOUSE && (i.u.mi.dwFlags & MOUSEEVENTF_MOVE) != 0)
                && output.Any(i => i.type == INPUT_MOUSE && i.u.mi.dwFlags == MOUSEEVENTF_WHEEL && unchecked((int)i.u.mi.mouseData) % 120 == 0)
                && output.All(i => i.type == INPUT_MOUSE && (i.u.mi.dwFlags & (MOUSEEVENTF_LEFTDOWN | MOUSEEVENTF_RIGHTDOWN)) == 0),
                "the agent's pointer-only mapping moves and scrolls in whole notches, and leaves a held click button to the launcher");
            output.Clear();
            mapper.MovePointer(pushed, 400, move: false, wheel: false);
            mapper.MovePointer(pushed, 408, move: false, wheel: false);
            Check(output.Count == 0, "the launcher's policy holds the agent's pointer still");

            // A DualSense pushed while a resting Xbox pad wobbles by a few hundred counts: the
            // DualSense keeps the pointer, and the wobble moves nothing.
            using var two = new SecureMapper(new InputProfile { KeyboardToggle = "Off" }, new InputInjector());
            var rng = new Random(7);
            output.Clear();
            int moved = 0;
            for (int i = 0; i < 60; i++)
            {
                var noisy = new NativeMethods.XINPUT_STATE { Gamepad = new() { sThumbLX = (short)rng.Next(-600, 600), sThumbLY = (short)rng.Next(-600, 600) } };
                var both = new AgentReply(1, true, true, noisy, new(true, "playstation", "DualSense", "usb", new() { sThumbLX = 32767 }, i, 0, 0, 0, 0, false));
                int before = output.Count;
                two.MovePointer(both, 1000 + i * 8, move: true, wheel: false);
                if (output.Count > before) moved++;
            }
            Check(moved >= 58, "a resting Xbox pad's wobble does not take the pointer from the DualSense being pushed");
            // The DualSense is let go (one tick where only it changes: it keeps the pointer, at
            // rest), then the Xbox pad is pushed: the only pad moving takes over.
            var resting = new HidPadSnapshot(true, "playstation", "DualSense", "usb", default, 99, 0, 0, 0, 0, false);
            two.MovePointer(new AgentReply(1, true, true, default, resting), 2000, move: true, wheel: false);
            output.Clear();
            var xboxPushed = new AgentReply(1, true, true, new() { Gamepad = new() { sThumbLX = 32767 } }, resting);
            for (int i = 1; i <= 10; i++) two.MovePointer(xboxPushed, 2000 + i * 8, move: true, wheel: false);
            Check(output.Count >= 9, "a real push on the Xbox pad takes it over");

            var reply = System.Text.Json.JsonSerializer.Deserialize<AgentReply>("{\"Version\":1,\"DefaultDesktop\":true,\"XInputPresent\":false}", Protocol.Json)!;
            Check(!reply.PointerOwner, "an older agent's reply reads as not owning the pointer, so the launcher keeps moving it");
            var request = System.Text.Json.JsonSerializer.Deserialize<AgentRequest>(System.Text.Json.JsonSerializer.Serialize(
                new AgentRequest(Protocol.Version, [], MovePointer: true), Protocol.Json), Protocol.Json)!;
            Check(request.MovePointer && !request.ScrollWheel, "the pointer policy travels with the request");

            // Two moves with nothing landing between them (the sink swallows the first): the
            // second must aim from where the first was heading, not from the unmoved pointer.
            Thread.Sleep(120);   // let the mapper's last aim go stale
            output.Clear();
            Check(GetCursorPos(out var at), "cursor position readable");
            NativeMethods.MoveCursorBy(3, 0);
            NativeMethods.MoveCursorBy(3, 0);
            int left = GetSystemMetrics(SM_XVIRTUALSCREEN), width = GetSystemMetrics(SM_CXVIRTUALSCREEN);
            int aim = Math.Clamp(at.X + 6, left, left + width - 1);
            int expected = Math.Clamp((int)(((aim - left) * 65535.0 + 32767.0) / (width - 1)), 0, 65535);
            Check(output.Count == 2 && output[1].u.mi.dx == expected, "a move aimed while the last one is still in flight carries on from where that one was heading");
        }
        finally { NativeMethods.InputSink = original; }
    }

    // Opt-in: injects an inert gamepad virtual key (VK_GAMEPAD_RIGHT_THUMBSTICK_LEFT, 0xDA) a few
    // times, so it is not part of the default run. With the service installed and enabled, its
    // worker's hook drops whatever this process's hooks let through.
    private static void NavigationHookStallProbe()
    {
        static void Tap()
        {
            var down = new INPUT { type = INPUT_KEYBOARD, u = new() { ki = new() { wVk = 0xDA } } };
            var up = new INPUT { type = INPUT_KEYBOARD, u = new() { ki = new() { wVk = 0xDA, dwFlags = KEYEVENTF_KEYUP } } };
            if (NativeMethods.SendInputLocal(new[] { down, up }) != 2) throw new Exception("SendInput refused the probe key");
        }
        static void Pump(int ms)
        {
            var frame = new System.Windows.Threading.DispatcherFrame();
            var timer = new System.Windows.Threading.DispatcherTimer(TimeSpan.FromMilliseconds(ms), System.Windows.Threading.DispatcherPriority.Normal,
                (_, _) => frame.Continue = false, System.Windows.Threading.Dispatcher.CurrentDispatcher);
            System.Windows.Threading.Dispatcher.PushFrame(frame);
            timer.Stop();
        }
        using (var filter = new GamepadNavigationFilter(() => true))
        {
            Tap(); Pump(200);
            Check(filter.BlockedCount == 2, "the filter's own thread suppresses a gamepad virtual key");
            // The worker's dispatcher thread, stalled -- the secure keyboard's first show, say --
            // with a key arriving in the middle of it.
            var during = Task.Run(() => { Thread.Sleep(500); Tap(); });
            Thread.Sleep(1500);
            during.Wait(); Pump(200);
            Check(filter.BlockedCount == 4, "the filter keeps working through a 1.5 s stall of the dispatcher thread");
            Tap(); Pump(200);
            Check(filter.BlockedCount == 6, "and after it");
        }
        // For the record: the same hook on the stalled thread itself. Windows is documented to
        // remove a hook that times out, silently; this says what this build does.
        long seen = 0;
        RawHook callback = (code, w, l) => { if (code >= 0 && unchecked((uint)Marshal.ReadInt32(l)) == 0xDA) seen++; return CallNextHookEx(IntPtr.Zero, code, w, l); };
        IntPtr hook = SetWindowsHookEx(13, callback, GetModuleHandle(null), 0);
        if (hook == IntPtr.Zero) throw new Exception("Reference hook failed");
        try
        {
            Tap(); Pump(200);
            long before = seen;
            var during = Task.Run(() => { Thread.Sleep(500); Tap(); });
            Thread.Sleep(1500);
            during.Wait(); Pump(200);
            Tap(); Pump(200);
            Console.WriteLine($"INFO: a hook on the stalled thread itself saw {before} events before the stall and {seen - before} of the 4 sent during and after it"
                + (seen - before == 0 ? ": Windows removed it silently" : ""));
        }
        finally { UnhookWindowsHookEx(hook); GC.KeepAlive(callback); }
    }

    private delegate IntPtr RawHook(int code, IntPtr message, IntPtr data);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr SetWindowsHookEx(int type, RawHook callback, IntPtr module, uint thread);
    [DllImport("user32.dll")] private static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr message, IntPtr data);
    [DllImport("user32.dll")] private static extern bool UnhookWindowsHookEx(IntPtr hook);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandle(string? module);

    private static void NavigationFilter()
    {
        Check(Enumerable.Range(0xC3, 24).All(key => GamepadNavigationFilter.ShouldSuppress(0, (uint)key, true)),
            "Windows gamepad virtual keys are suppressed in mouse mode");
        Check(Enumerable.Range(0, 256).Where(key => key < 0xC3 || key > 0xDA)
            .All(key => !GamepadNavigationFilter.ShouldSuppress(0, (uint)key, true)),
            "normal keyboard, credential characters, Tab, Enter and arrow keys are never suppressed");
        Check(!GamepadNavigationFilter.ShouldSuppress(-1, 0xC3, true)
            && !GamepadNavigationFilter.ShouldSuppress(0, 0xC3, false),
            "hook passes through when mouse mapping is off or Windows requires forwarding");
    }

    private static async Task ServicePackages()
    {
        string work = Path.Combine(Path.GetTempPath(), "Loungepad.PackageTests." + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        try
        {
            async Task RejectPackage(Func<Task> action, string name)
            {
                try { await action(); } catch (IOException) { Check(true, name); return; }
                throw new Exception("FAIL: accepted " + name);
            }
            byte[] data = Encoding.UTF8.GetBytes("signed package contents are validated separately by the elevated installer");
            string digest = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(data)).ToLowerInvariant();
            var version = new Version(1, 8, 0);
            string name = InputServicePackage.AssetName(version);
            string metadata = System.Text.Json.JsonSerializer.Serialize(new { tag_name = "v1.8.0", assets = new[] { new {
                name, size = data.Length, digest = "sha256:" + digest,
                browser_download_url = "https://github.com/sukumar-v/Loungepad/releases/download/v1.8.0/" + name } } });
            System.Net.Http.HttpClient Client(string json, byte[]? body = null, bool missing = false) => new(new PackageHttp(json, body ?? data, missing));
            using (var http = Client(metadata))
            {
                int progress = 0;
                string path = Path.Combine(work, "valid.zip");
                await InputServicePackage.Download(http, version, path, p => progress = p, CancellationToken.None);
                Check(File.ReadAllBytes(path).SequenceEqual(data) && progress == 100, "matching version and SHA-256 download succeeds");
            }
            async Task RejectDownload(string json, string test, byte[]? body = null, bool missing = false)
            {
                using var http = Client(json, body, missing);
                await RejectPackage(() => InputServicePackage.Download(http, version, Path.Combine(work, Guid.NewGuid() + ".zip"), _ => { }, CancellationToken.None), test);
            }
            await RejectDownload(metadata, "unpublished service release gives a recoverable error", missing: true);
            await RejectDownload(metadata.Replace("sha256:" + digest, "sha256:" + new string('0', 64)), "tampered download rejected");
            await RejectDownload(metadata.Replace("v1.8.0", "v9.9.9"), "wrong release version rejected");
            await RejectDownload(metadata.Replace("https://github.com", "https://attacker.example"), "external package address rejected");
            await RejectDownload(metadata.Replace("sha256:" + digest, ""), "missing download checksum rejected");
            await RejectDownload(metadata, "oversized download rejected", data.Concat(new byte[] { 1 }).ToArray());

            string MakeZip(params (string Name, int Attributes)[] entries)
            {
                string path = Path.Combine(work, Guid.NewGuid() + ".zip");
                using var zip = System.IO.Compression.ZipFile.Open(path, System.IO.Compression.ZipArchiveMode.Create);
                foreach (var entry in entries) { var item = zip.CreateEntry(entry.Name); item.ExternalAttributes = entry.Attributes; using var stream = item.Open(); stream.WriteByte(42); }
                return path;
            }
            string valid = MakeZip(("Loungepad.Service.exe", 0), ("Loungepad.Input.cat", 0), ("Agent/Loungepad.InputAgent.exe", 0));
            string extracted = Path.Combine(work, "valid");
            await InputServicePackage.Extract(valid, extracted, CancellationToken.None);
            Check(File.Exists(Path.Combine(extracted, "Agent/Loungepad.InputAgent.exe")), "valid service archive extracts");
            foreach (string unsafePath in new[] { "../escape.exe", "/absolute.exe", "C:/outside.exe", "safe/../../escape.exe", "file:stream", "file.", "NUL.txt" })
            {
                string archive = MakeZip((unsafePath, 0));
                await RejectPackage(() => InputServicePackage.Extract(archive, Path.Combine(work, Guid.NewGuid().ToString()), CancellationToken.None), "unsafe archive path rejected: " + unsafePath);
            }
            await RejectPackage(() => InputServicePackage.Extract(MakeZip(("link", unchecked((int)0xA0000000))), Path.Combine(work, "link"), CancellationToken.None), "symbolic link archive entry rejected");
            await RejectPackage(() => InputServicePackage.Extract(MakeZip(("FILE.exe", 0), ("file.exe", 0)), Path.Combine(work, "duplicates"), CancellationToken.None), "case-insensitive duplicate archive entry rejected");
            await RejectPackage(() => InputServicePackage.Extract(MakeZip(("readme.txt", 0)), Path.Combine(work, "incomplete"), CancellationToken.None), "incomplete service archive rejected");
        }
        finally { Directory.Delete(work, true); }
    }

    private sealed class PackageHttp(string metadata, byte[] body, bool missing) : System.Net.Http.HttpMessageHandler
    {
        protected override Task<System.Net.Http.HttpResponseMessage> SendAsync(System.Net.Http.HttpRequestMessage request, CancellationToken ct)
        {
            bool api = request.RequestUri!.Host == "api.github.com";
            return Task.FromResult(new System.Net.Http.HttpResponseMessage(api && missing ? System.Net.HttpStatusCode.NotFound : System.Net.HttpStatusCode.OK)
            { Content = api ? new System.Net.Http.StringContent(metadata) : new System.Net.Http.ByteArrayContent(body) });
        }
    }
    private static void Reject(Action action, string name)
    {
        try { action(); } catch (IOException) { Check(true, name); return; }
        throw new Exception("FAIL: accepted " + name);
    }

    private static void Profiles()
    {
        Check(new InputProfile().Validate().KeyboardApp == "Builtin", "default configured keyboard");
        foreach (var profile in new[]
        {
            new InputProfile { Sensitivity = double.NaN }, new InputProfile { Deadzone = 1 },
            new InputProfile { KeyboardApp = "cmd.exe" }, new InputProfile { KeyboardToggle = "run command" },
            new InputProfile { RepeatIntervalMs = 0 }, new InputProfile { Display = new string('x', 65) },
        }) Reject(() => profile.Validate(), "invalid preference rejected");
        Reject(() => MachineInputSettings.Parse(new string(' ', 8193)), "oversized profile rejected");
        var native = new INPUT { type = INPUT_KEYBOARD, u = new() { ki = new() { wVk = VK_RETURN, dwFlags = KEYEVENTF_KEYUP } } };
        var result = InputEvent.From(native).ToNative();
        Check(result.u.ki.wVk == VK_RETURN && result.u.ki.dwFlags == KEYEVENTF_KEYUP && result.u.ki.dwExtraInfo == IntPtr.Zero, "keyboard events round-trip without native pointers");
        Reject(() => new InputEvent(2, 0, 0, 0, 0, 0, 0).ToNative(), "hardware INPUT rejected");
        Reject(() => new InputEvent(INPUT_MOUSE, 0, 0, 0, 0x10000, 0, 0).ToNative(), "unsupported mouse flags rejected");
    }

    private static async Task Framing()
    {
        using var stream = new MemoryStream();
        var hid = new HidPadSnapshot(true, "playstation", "Test pad", "device", new() { wButtons = XINPUT_GAMEPAD_A, sThumbLX = 12345 }, 42, 16, -4, 2.5, 2, true);
        var source = new AgentReply(Protocol.Version, true, true, new() { Gamepad = new() { sThumbLY = -9000 } }, hid);
        await Protocol.Write(stream, source, CancellationToken.None);
        stream.Position = 0;
        var copy = await Protocol.Read<AgentReply>(stream, CancellationToken.None);
        Check(copy == source, "XInput, HID and touchpad snapshots survive IPC");
        foreach (int size in new[] { -1, 0, Protocol.MaxMessageBytes + 1 })
        {
            using var invalid = new MemoryStream(BitConverter.GetBytes(size));
            try { await Protocol.Read<AgentReply>(invalid, CancellationToken.None); throw new Exception("Invalid frame accepted"); }
            catch (IOException) { Check(true, "invalid frame length rejected"); }
        }
        using var truncated = new MemoryStream(BitConverter.GetBytes(15).Concat(Encoding.UTF8.GetBytes("{}")).ToArray());
        try { await Protocol.Read<AgentReply>(truncated, CancellationToken.None); throw new Exception("Truncated frame accepted"); }
        catch (EndOfStreamException) { Check(true, "truncated frame rejected"); }
        using var oversized = new MemoryStream();
        try { await Protocol.Write(oversized, new string('x', Protocol.MaxMessageBytes), CancellationToken.None); throw new Exception("Oversized output accepted"); }
        catch (IOException) { Check(oversized.Length == 0, "oversized output rejected before writing"); }
    }

    private static void Handoff()
    {
        var gate = new NeutralInputGate();
        var held = new AgentReply(1, false, true, new() { Gamepad = new() { wButtons = XINPUT_GAMEPAD_A } }, default);
        Check(!gate.Accept(held), "held confirm button is swallowed on desktop handoff");
        var neutral = held with { Xbox = default };
        Check(gate.Accept(neutral) && gate.Accept(held), "fresh physical presses work after returning to neutral");
        gate.Reset();
        var touch = neutral with { Hid = new(true, "playstation", "pad", "id", default, 1, 0, 0, 0, 1, true) };
        Check(!gate.Accept(touch), "held touchpad click is swallowed on handoff");
        Check(gate.Accept(neutral), "touch release arms input");
        Check(!gate.Accept(neutral with { XInputPresent = false }) && !gate.Accept(held), "disconnect resets neutral gate");
    }

    private static async Task SpoofPipe()
    {
        string name = "Loungepad.Tests." + Guid.NewGuid().ToString("N");
        using var identity = WindowsIdentity.GetCurrent();
        var acl = new PipeSecurity();
        acl.SetAccessRuleProtection(true, false);
        acl.SetOwner(identity.User!);
        acl.AddAccessRule(new PipeAccessRule(identity.User!, PipeAccessRights.ReadWrite | PipeAccessRights.ReadPermissions, AccessControlType.Allow));
        using var server = NamedPipeServerStreamAcl.Create(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 4096, 4096, acl);
        using var client = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
        using var timeout = new CancellationTokenSource(3000);
        await Task.WhenAll(server.WaitForConnectionAsync(timeout.Token), client.ConnectAsync(timeout.Token));
        try { Protocol.VerifyServer(client); throw new Exception("User-owned server accepted"); }
        catch (UnauthorizedAccessException) { Check(true, "user-owned spoof pipe rejected"); }
    }

    private static void Mapping()
    {
        var output = new List<INPUT>();
        var original = NativeMethods.InputSink;
        NativeMethods.InputSink = inputs => { output.AddRange(inputs); return (uint)inputs.Length; };
        try
        {
            using var mapper = new SecureMapper(new InputProfile { KeyboardToggle = "Off", LeftClick = "X" }, new InputInjector());
            var held = new AgentReply(1, false, true, new() { Gamepad = new() { wButtons = XINPUT_GAMEPAD_X } }, default);
            mapper.Update(held, 10);
            mapper.Update(held, 20);
            Check(output.Count(i => i.type == INPUT_MOUSE && i.u.mi.dwFlags == MOUSEEVENTF_LEFTDOWN) == 1, "secure mapper honors configured click and does not repeat held clicks");
            mapper.Update(held with { Xbox = default }, 30);
            Check(output.Any(i => i.type == INPUT_MOUSE && i.u.mi.dwFlags == MOUSEEVENTF_LEFTUP), "secure mapper releases the configured click");
            output.Clear();
            mapper.Update(held with { Xbox = new() { Gamepad = new() { sThumbLX = 32767 } } }, 40);
            Check(output.Any(i => i.type == INPUT_MOUSE && (i.u.mi.dwFlags & MOUSEEVENTF_MOVE) != 0)
                && output.All(i => i.type != INPUT_KEYBOARD), "Xbox joystick produces mouse movement, not arrow keys");
            output.Clear();
            mapper.Update(new(1, false, false, default, new(true, "playstation", "DualSense", "test", new() { sThumbLX = -32767 }, 1, 0, 0, 0, 0, false)), 50);
            Check(output.Any(i => i.type == INPUT_MOUSE && (i.u.mi.dwFlags & MOUSEEVENTF_MOVE) != 0)
                && output.All(i => i.type != INPUT_KEYBOARD), "HID joystick produces mouse movement, not arrow keys");
        }
        finally { NativeMethods.InputSink = original; }
    }

    private static void SecureKeyboard()
    {
        var output = new List<KeyStroke>();
        var originalOutput = KeyboardWindow.Output;
        var originalProbe = KeyboardWindow.Probe;
        KeyboardWindow.Output = output.Add;
        KeyboardWindow.Probe = () => default;
        try
        {
            var keyboard = new KeyboardWindow(secureInput: true);
            var type = typeof(KeyboardWindow).GetMethod("TypeChar", BindingFlags.Instance | BindingFlags.NonPublic)!;
            const string sample = "A..''!?12";
            foreach (char c in sample) type.Invoke(keyboard, new object[] { c });
            Check(string.Concat(output.Select(k => k.Text)) == sample && output.All(k => k.Vk == 0), "secure keyboard preserves repeated punctuation and character case");
            var text = (KeyboardText)typeof(KeyboardWindow).GetField("_text", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(keyboard)!;
            Check(text.CurrentWord == "" && text.PreviousWords(3).Count == 0, "secure keyboard retains no typed-word history");
            typeof(KeyboardWindow).GetMethod("RefreshSuggestions", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(keyboard, null);
            var predictor = typeof(KeyboardWindow).GetField("_predictor", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(keyboard)!;
            Check(typeof(WordPredictor).GetField("_gen", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(predictor) is null, "secure keyboard never initializes prediction engine");
            keyboard.Close();
        }
        finally { KeyboardWindow.Output = originalOutput; KeyboardWindow.Probe = originalProbe; }
    }
}
