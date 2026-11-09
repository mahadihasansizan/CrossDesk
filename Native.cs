using System.Runtime.InteropServices;
namespace DeskRelay;

internal static class Native
{
    internal delegate nint HookProc(int code, nint message, nint data);
    [StructLayout(LayoutKind.Sequential)] internal struct Point { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] internal struct MouseHook
    {
        public Point Point;
        public uint MouseData, Flags, Time;
        public nuint Extra;
    }
    [StructLayout(LayoutKind.Sequential)] internal struct KeyboardHook
    {
        public uint VirtualKey, ScanCode, Flags, Time;
        public nuint Extra;
    }
    [StructLayout(LayoutKind.Sequential)] internal struct MouseInput
    {
        public int X, Y;
        public uint Data, Flags, Time;
        public nuint Extra;
    }
    [StructLayout(LayoutKind.Sequential)] internal struct KeyboardInput
    {
        public ushort VirtualKey, ScanCode;
        public uint Flags, Time;
        public nuint Extra;
    }
    [StructLayout(LayoutKind.Explicit)] internal struct InputUnion
    {
        [FieldOffset(0)] public MouseInput Mouse;
        [FieldOffset(0)] public KeyboardInput Keyboard;
    }
    [StructLayout(LayoutKind.Sequential)] internal struct Input
    {
        public uint Type;
        public InputUnion Union;
    }
    [DllImport("user32.dll", SetLastError = true)]
    internal static extern nint SetWindowsHookEx(int id, HookProc callback, nint module, uint thread);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool UnhookWindowsHookEx(nint hook);
    [DllImport("user32.dll")] internal static extern nint CallNextHookEx(nint hook, int code, nint message, nint data);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] internal static extern nint GetModuleHandle(string? name);
    [DllImport("user32.dll")] internal static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetCursorPos(out Point point);
    [DllImport("user32.dll")] internal static extern short GetAsyncKeyState(int key);
    [DllImport("user32.dll", SetLastError = true)]
    internal static extern uint SendInput(uint count, in Input input, int size);

    internal static bool Key(int virtualKey, int scanCode, int flags)
    {
        // Scan codes preserve the physical key across differing keyboard layouts.
        // Pause needs its virtual key because its E1 sequence has no KEYEVENTF equivalent.
        bool useScan = scanCode != 0 && virtualKey != 0x13;
        return Send(new Input
        {
            Type = 1,
            Union = new InputUnion
            {
                Keyboard = new KeyboardInput
                {
                    VirtualKey = useScan ? (ushort)0 : (ushort)virtualKey,
                    ScanCode = useScan ? (ushort)scanCode : (ushort)0,
                    Flags = (uint)(flags | (useScan ? 8 : 0))
                }
            }
        });
    }

    internal static bool Mouse(uint flags, uint data = 0, int x = 0, int y = 0) =>
        Send(new Input
        {
            Union = new InputUnion { Mouse = new MouseInput { X = x, Y = y, Data = data, Flags = flags } }
        });

    // Target the center of the pixel's 16-bit absolute-coordinate interval.
    // Endpoint interpolation can land one pixel short and make a pinned cursor drift.
    internal static int NormalizePixel(int pixel, int extent) =>
        (int)(((2L * Math.Clamp(pixel, 0, extent - 1) + 1) * 65536) / (2L * extent));

    static readonly int InputSize = Marshal.SizeOf<Input>();
    static bool Send(Input input) => SendInput(1, in input, InputSize) == 1;
}
