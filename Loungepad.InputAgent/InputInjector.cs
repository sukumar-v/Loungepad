using System.ComponentModel;
using System.Runtime.InteropServices;
using Loungepad.Interop;
using static Loungepad.Interop.NativeMethods;

namespace Loungepad.InputAgent;

internal sealed class InputInjector
{
    private readonly Dictionary<(ushort Key, ushort Scan, uint Flags), INPUT> _keys = new();
    private uint _mouseUps;

    public void Send(INPUT[] inputs)
    {
        uint sent = NativeMethods.SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
        if (sent != inputs.Length) throw new Win32Exception(Marshal.GetLastWin32Error(), "Windows rejected controller input");
    }
    public uint SendLocal(INPUT[] inputs)
    {
        uint sent = NativeMethods.SendInputLocal(inputs);
        foreach (var input in inputs.Take(checked((int)sent)))
        {
            if (input.type == INPUT_KEYBOARD)
            {
                var k = input.u.ki;
                var id = (k.wVk, k.wScan, k.dwFlags & ~KEYEVENTF_KEYUP);
                if ((k.dwFlags & KEYEVENTF_KEYUP) != 0) _keys.Remove(id); else _keys[id] = input;
            }
            else
            {
                uint f = input.u.mi.dwFlags;
                foreach (var (down, up) in new[] { (MOUSEEVENTF_LEFTDOWN, MOUSEEVENTF_LEFTUP), (MOUSEEVENTF_RIGHTDOWN, MOUSEEVENTF_RIGHTUP) })
                { if ((f & down) != 0) _mouseUps |= up; if ((f & up) != 0) _mouseUps &= ~up; }
            }
        }
        if (sent != inputs.Length) throw new Win32Exception(Marshal.GetLastWin32Error(), "SendInput rejected controller input");
        return sent;
    }
    public void Mouse(uint flags, int x = 0, int y = 0, int data = 0) => Send(new[]
    {
        new INPUT { type = INPUT_MOUSE, u = new() { mi = new() { dx = x, dy = y, mouseData = unchecked((uint)data), dwFlags = flags } } }
    });
    public void ReleaseMouse() => Mouse(MOUSEEVENTF_LEFTUP | MOUSEEVENTF_RIGHTUP);
    public void Release()
    {
        var release = _keys.Values.Select(input => { input.u.ki.dwFlags |= KEYEVENTF_KEYUP; return input; }).ToList();
        if (_mouseUps != 0) release.Add(new() { type = INPUT_MOUSE, u = new() { mi = new() { dwFlags = _mouseUps } } });
        _keys.Clear(); _mouseUps = 0;
        if (release.Count != 0) NativeMethods.SendInput((uint)release.Count, release.ToArray(), Marshal.SizeOf<INPUT>());
    }
}
