using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;

namespace ScreenFlow;
public static class NativeInput
{
    [StructLayout(LayoutKind.Sequential)] public struct Input { public uint Type; public InputUnion Data; }
    [StructLayout(LayoutKind.Explicit)] public struct InputUnion {
        [FieldOffset(0)] public MouseInput Mouse;
        [FieldOffset(0)] public KeyboardInput Keyboard;
    }
    [StructLayout(LayoutKind.Sequential)] public struct MouseInput { public int X, Y; public uint MouseData, Flags, Time; public UIntPtr Extra; }
    [StructLayout(LayoutKind.Sequential)] public struct KeyboardInput { public ushort Vk, Scan; public uint Flags, Time; public UIntPtr Extra; }
    [DllImport("user32.dll", SetLastError = true)] private static extern uint SendInput(uint count, Input[] inputs, int size);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] public static extern bool GetCursorPos(out NativePoint point);
    [DllImport("user32.dll")] public static extern short GetAsyncKeyState(int key);
    [DllImport("user32.dll", SetLastError = true)] public static extern bool RegisterHotKey(IntPtr handle, int id, uint modifiers, uint key);
    [DllImport("user32.dll")] public static extern bool UnregisterHotKey(IntPtr handle, int id);
    [DllImport("user32.dll")] public static extern uint GetClipboardSequenceNumber();
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [StructLayout(LayoutKind.Sequential)] public struct NativePoint { public int X, Y; }
    private static void Send(params Input[] inputs) {
        if (SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<Input>()) != inputs.Length)
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Windows blocked input. Target applications must run at the same privilege level as ScreenFlow.");
    }
    private static Input KeyInput(ushort key, bool up, ushort scan = 0, bool unicode = false) => new() {
        Type = 1, Data = new InputUnion { Keyboard = new KeyboardInput { Vk = key, Scan = scan,
            Flags = (up ? 2u : 0u) | (unicode ? 4u : IsExtended(key) ? 1u : 0u) } }
    };
    private static bool IsExtended(ushort key) => key is >= 0x21 and <= 0x28 or 0x2D or 0x2E or 0x5B or 0x5C or 0x6F;
    public static ushort[] ParseKeys(string chord)
    {
        if (string.IsNullOrWhiteSpace(chord)) throw new ArgumentException("Enter a keyboard shortcut, for example Ctrl+Tab.");
        return chord.Split('+').Select(part => {
            string token = part.Trim();
            int special = token.ToUpperInvariant() switch {
                "CTRL" or "CONTROL" => 0x11, "ALT" => 0x12, "SHIFT" => 0x10,
                "ENTER" or "RETURN" => 0x0D, "ESC" or "ESCAPE" => 0x1B, "SPACE" => 0x20,
                "BACKSPACE" => 8, "TAB" => 9, "DELETE" => 0x2E,
                _ => 0
            };
            if (special != 0) return (ushort)special;
            if (!Enum.TryParse<Key>(token.Length == 1 && char.IsDigit(token[0]) ? "D" + token : token, true, out var key) || key == Key.None)
                throw new ArgumentException($"Unknown key '{token}'. Use Ctrl, Alt, Shift, Tab, Enter, A–Z, digits, arrows, or F1–F12.");
            int vk = KeyInterop.VirtualKeyFromKey(key);
            if (vk == 0) throw new ArgumentException($"Unsupported key '{token}'.");
            return (ushort)vk;
        }).ToArray();
    }
    public static void Hotkey(string chord)
    {
        var keys = ParseKeys(chord);
        var pressed = new List<ushort>();
        try { foreach (ushort key in keys) { Send(KeyInput(key, false)); pressed.Add(key); } }
        finally { foreach (ushort key in pressed.AsEnumerable().Reverse()) Send(KeyInput(key, true)); }
    }
    public static void Move(int x, int y) {
        if (!System.Windows.Forms.SystemInformation.VirtualScreen.Contains(x, y)) throw new ArgumentException($"Point ({x}, {y}) is outside the connected displays.");
        if (!SetCursorPos(x, y)) throw new Win32Exception(Marshal.GetLastWin32Error());
    }
    public static async Task Click(int x, int y, ActionKind kind, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        Move(x, y);
        await Task.Delay(70, token);
        uint down = kind == ActionKind.RightClick ? 8u : 2u;
        for (int i = 0; i < (kind == ActionKind.DoubleClick ? 2 : 1); i++) {
            token.ThrowIfCancellationRequested();
            Send(new Input { Data = new InputUnion { Mouse = new MouseInput { Flags = down } } },
                new Input { Data = new InputUnion { Mouse = new MouseInput { Flags = down * 2 } } });
            if (kind == ActionKind.DoubleClick) await Task.Delay(80, token);
        }
    }
    public static void Scroll(int notches) => Send(new Input { Data = new InputUnion { Mouse = new MouseInput { Flags = 0x800, MouseData = unchecked((uint)(notches * 120)) } } });
    public static async Task TypeText(string text, CancellationToken token)
    {
        // Keep each group of Unicode key events together in the input stream.
        // Separate SendInput calls for every character allow unrelated keyboard
        // activity to interleave with a string while the destination processes it.
        var pending = new List<Input>(128);
        async Task Flush()
        {
            if (pending.Count == 0) return;
            token.ThrowIfCancellationRequested();
            Send(pending.ToArray()); pending.Clear();
            await Task.Delay(3, token);
        }
        foreach (char ch in text) {
            token.ThrowIfCancellationRequested();
            if (ch == '\r') continue;
            if (ch is '\n' or '\t') { await Flush(); Hotkey(ch == '\n' ? "Enter" : "Tab"); }
            else { pending.Add(KeyInput(0, false, ch, true)); pending.Add(KeyInput(0, true, ch, true)); }
            if (pending.Count >= 128 && !char.IsHighSurrogate(ch)) await Flush();
        }
        await Flush();
    }
}
