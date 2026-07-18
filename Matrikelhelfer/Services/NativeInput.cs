using System;
using System.Runtime.InteropServices;

namespace Matrikelhelfer.Services;

// Simulated keyboard input via SendInput, used by BrowserConnection for its
// Ctrl+L / Enter address-bar sequence (re-navigating to a saved entry's URL).
static class NativeInput
{
    [DllImport("user32.dll")]
    static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    const uint InputKeyboard = 1;
    const uint KeyEventFKeyUp = 0x0002;

    public const ushort VkReturn = 0x0D;
    public const ushort VkControl = 0x11;
    public const ushort VkL = 0x4C;

    // Struct layout matters here: SendInput's native INPUT is a tagged union
    // (type + {MOUSEINPUT|KEYBDINPUT|HARDWAREINPUT}) that gets 8-byte-aligned
    // on x64 because the union members end in an IntPtr. Wrapping the members
    // in their own [FieldOffset(0)] union struct lets the CLR insert the same
    // padding automatically; declaring only the member actually used would
    // produce the wrong struct size and make SendInput silently do nothing.
    // MOUSEINPUT is only here for that sizing - nothing constructs one, since
    // this app only ever sends keyboard input.
    [StructLayout(LayoutKind.Sequential)]
    struct INPUT
    {
        public uint type;
        public InputUnion U;
    }

    [StructLayout(LayoutKind.Explicit)]
    struct InputUnion
    {
        [FieldOffset(0)] public MOUSEINPUT mi;
        [FieldOffset(0)] public KEYBDINPUT ki;
        [FieldOffset(0)] public HARDWAREINPUT hi;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct MOUSEINPUT
    {
        public int dx;
        public int dy;
        public uint mouseData;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct KEYBDINPUT
    {
        public ushort wVk;
        public ushort wScan;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct HARDWAREINPUT
    {
        public uint uMsg;
        public ushort wParamL;
        public ushort wParamH;
    }

    public static void SendKey(ushort vk)
    {
        var inputs = new[] { KeyInput(vk, up: false), KeyInput(vk, up: true) };
        SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
    }

    public static void SendChord(params ushort[] vks)
    {
        var inputs = new INPUT[vks.Length * 2];
        for (int i = 0; i < vks.Length; i++)
        {
            inputs[i] = KeyInput(vks[i], up: false);
        }
        for (int i = 0; i < vks.Length; i++)
        {
            inputs[vks.Length + i] = KeyInput(vks[vks.Length - 1 - i], up: true);
        }
        SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
    }

    static INPUT KeyInput(ushort vk, bool up) => new INPUT
    {
        type = InputKeyboard,
        U = new InputUnion { ki = new KEYBDINPUT { wVk = vk, dwFlags = up ? KeyEventFKeyUp : 0 } }
    };
}
