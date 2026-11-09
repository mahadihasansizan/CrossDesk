using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows.Threading;

namespace DeskRelay;

/// <summary>Input is serialized on a dedicated message loop, independent of the application UI.</summary>
sealed class InputEngine : IDisposable
{
    readonly Dispatcher uiDispatcher = Dispatcher.CurrentDispatcher;
    readonly Dispatcher dispatcher;
    readonly Thread inputThread;
    readonly Native.HookProc mouseCallback;
    readonly Native.HookProc keyboardCallback;
    DispatcherTimer? watchdog;
    readonly HashSet<int> physicalKeys = new();
    readonly HashSet<int> localKeys = new();
    readonly HashSet<int> forwardedKeys = new();
    readonly Dictionary<int, (int Scan, int Flags)> receivedKeys = new();
    readonly HashSet<uint> receivedButtons = new();
    nint mouseHook, keyboardHook;
    Peer? peer;
    bool keyboardOnly;
    volatile bool disposed;
    bool shortcutHeld;
    bool edgeArmed = true;
    bool? keyboardFollowsPeer;
    int localButtons;
    double virtualX, virtualY;
    Native.Point anchor;
    int remoteWidth, remoteHeight, advertisedWidth, advertisedHeight;
    long ignoreEdgeUntil;
    volatile bool isRemote;
    public bool IsRemote { get => isRemote; private set => isRemote = value; }
    public bool? KeyboardFollowsPeer => disposed ? null : dispatcher.Invoke(() => keyboardFollowsPeer);
    public bool KeyboardOnly { get => disposed ? keyboardOnly : dispatcher.Invoke(() => keyboardOnly); set => SetKeyboardOnly(value); }
    public event Action? StateChanged;
    public event Action<string>? Error;

    public InputEngine()
    {
        mouseCallback = MouseHook;
        keyboardCallback = KeyboardHook;
        var started = new TaskCompletionSource<Dispatcher>(TaskCreationOptions.RunContinuationsAsynchronously);
        inputThread = new Thread(() =>
        {
            var loop = Dispatcher.CurrentDispatcher;
            try
            {
                Initialize(loop);
                started.SetResult(loop);

            }
            catch (Exception exception)
            {
                started.TrySetException(exception);
                return;
            }
            Dispatcher.Run();
        })
        {
            IsBackground = true,
            Name = "CrossDesk input",
            Priority = ThreadPriority.AboveNormal
        };
        inputThread.SetApartmentState(ApartmentState.STA);
        inputThread.Start();
        dispatcher = started.Task.GetAwaiter().GetResult();
    }

    void Initialize(Dispatcher loop)
    {
        var module = Native.GetModuleHandle(null);
        mouseHook = Native.SetWindowsHookEx(14, mouseCallback, module, 0);
        keyboardHook = Native.SetWindowsHookEx(13, keyboardCallback, module, 0);
        if (mouseHook == 0 || keyboardHook == 0)
        {
            int error = Marshal.GetLastWin32Error();
            if (mouseHook != 0) Native.UnhookWindowsHookEx(mouseHook);
            if (keyboardHook != 0) Native.UnhookWindowsHookEx(keyboardHook);
            throw new Win32Exception(error, "Windows could not start input sharing.");
        }
        watchdog = new DispatcherTimer(TimeSpan.FromMilliseconds(100), DispatcherPriority.Input,
            (_, _) => { if (peer is { Online: false }) Detach(); else PublishDisplaySize(); }, loop);
        watchdog.Start();
    }

    public void Attach(Peer connection)
    {
        if (disposed) return;
        if (!dispatcher.CheckAccess()) { dispatcher.Invoke(() => Attach(connection)); return; }
        Detach();
        peer = connection;
        remoteWidth = remoteHeight = advertisedWidth = advertisedHeight = 0;
        PublishDisplaySize();
        SetKeyboardFocus(!connection.IsHost);
        edgeArmed = false;
    }

    public void Detach()
    {
        if (disposed) return;
        if (!dispatcher.CheckAccess()) { dispatcher.Invoke(() => Detach()); return; }
        StopRemote();
        SetKeyboardFocus(null);
        ReleaseReceived();
        peer = null;
    }

    public void Toggle(bool onlyKeyboard)
    {
        if (disposed) return;
        if (!dispatcher.CheckAccess()) { dispatcher.Invoke(() => Toggle(onlyKeyboard)); return; }
        if (IsRemote) ReturnLocal();
        else StartRemote(onlyKeyboard);
    }

    void StartRemote(bool onlyKeyboard, Native.Point? crossingPoint = null)
    {
        if (peer is not { Online: true } || localButtons != 0) return;
        if (!onlyKeyboard && (remoteWidth < 2 || remoteHeight < 2)) return;
        if (!peer.Send(new Frame(Kind.Focus, 1))) return;
        ReleaseReceived();
        keyboardOnly = onlyKeyboard;
        IsRemote = true;
        SetKeyboardFocus(true);
        var bounds = Bounds();
        Native.GetCursorPos(out var point);
        point = crossingPoint ?? point;
        virtualX = peer.Side == Side.Right ? 3 : remoteWidth - 4;
        virtualY = PointerGeometry.MapHeight(point.Y - bounds.Top, bounds.Height, remoteHeight);
        anchor = new Native.Point { X = bounds.Left + bounds.Width / 2, Y = bounds.Top + bounds.Height / 2 };
        if (!onlyKeyboard)
        {
            Warp(anchor);
            SendPosition(remoteWidth, remoteHeight);
        }
        Notify();
    }

    public void ReturnLocal()
    {
        if (disposed) return;
        if (!dispatcher.CheckAccess()) { dispatcher.Invoke(() => ReturnLocal()); return; }
        if (!IsRemote) return;
        StopRemote();
        SetKeyboardFocus(false);
        peer?.Send(new Frame(Kind.Focus, 0));
    }

    void StopRemote()
    {
        if (!IsRemote) return;
        peer?.Send(new Frame(Kind.Release));
        IsRemote = false;
        edgeArmed = false;
        ignoreEdgeUntil = Environment.TickCount64 + 250;
        if (!keyboardOnly)
        {
            var bounds = Bounds();
            Warp(new Native.Point
            {
                X = peer?.Side == Side.Right ? bounds.Left + bounds.Width - 4 : bounds.Left + 3,
                Y = bounds.Top + (int)PointerGeometry.MapHeight(virtualY, remoteHeight, bounds.Height)
            });
        }
        Notify();
    }

    // Awaiting the operation preserves key/button ordering while the network reader
    // bypasses the UI dispatcher entirely. No unbounded dispatcher queue is created.
    public Task ReceiveAsync(Frame frame) => disposed
        ? Task.CompletedTask
        : dispatcher.InvokeAsync(() => Receive(frame), DispatcherPriority.Send).Task;

    public Task ReceiveAsync(Peer expected, Frame frame) => disposed
        ? Task.CompletedTask
        : dispatcher.InvokeAsync(() =>
        {
            if (ReferenceEquals(peer, expected)) Receive(frame);
        }, DispatcherPriority.Send).Task;

    public void Receive(Frame frame)
    {
        if (disposed) return;
        if (!dispatcher.CheckAccess()) { dispatcher.Invoke(() => Receive(frame)); return; }
        if (disposed) return;
        if (frame.Kind == Kind.DisplayInfo)
        {
            if (frame.A is < 8 or > 65536 || frame.B is < 8 or > 65536) return;
            if (IsRemote) ReturnLocal();
            remoteWidth = frame.A;
            remoteHeight = frame.B;
            return;
        }
        if (frame.Kind == Kind.Layout)
        {
            if (frame.A is not (0 or 1) || peer is null) return;
            StopRemote();
            ReleaseReceived();
            peer.Side = (Side)frame.A;
            SetKeyboardFocus(true);
            edgeArmed = false;
            Notify();
            return;
        }
        if (frame.Kind == Kind.Focus)
        {
            if (frame.A is not (0 or 1)) return;
            StopRemote();
            SetKeyboardFocus(frame.A == 0);
            return;
        }
        if (frame.Kind == Kind.Release)
        {
            if (frame.A == 2)
            {
                ReleaseReceivedKeys();
                return;
            }
            ReleaseReceived();
            if (frame.A == 1)
            {
                StopRemote();
                SetKeyboardFocus(null);
            }
            return;
        }
        bool success;
        switch (frame.Kind)
        {
            case Kind.Move:
                if (frame.A is < 0 or > 65535 || frame.B is < 0 or > 65535) return;
                if (IsRemote) return;
                success = Native.Mouse(0xC001, 0, frame.A, frame.B);
                ignoreEdgeUntil = Environment.TickCount64 + 250;
                edgeArmed = false;
                break;
            case Kind.Key:
                if (frame.A is < 1 or > 255 || frame.B is < 0 or > 255 || (frame.C & ~3) != 0) return;
                bool released = (frame.C & 2) != 0;
                if (released && !receivedKeys.Remove(frame.A)) return;
                success = (released && localKeys.Contains(frame.A)) || Native.Key(frame.A, frame.B, frame.C);
                if (!released && success) receivedKeys[frame.A] = (frame.B, frame.C);
                break;
            case Kind.Button:
                uint flags = (uint)frame.A;
                if (flags is not (2 or 4 or 8 or 16 or 32 or 64 or 128 or 256 or 0x800 or 0x1000)) return;
                if (flags is 128 or 256 && frame.B is not (1 or 2)) return;
                success = Native.Mouse(flags, unchecked((uint)frame.B));
                uint identity = flags is 128 or 256 ? (uint)(128 + frame.B) : flags;
                if (flags is 2 or 8 or 32 or 128)
                {
                    if (success) receivedButtons.Add(identity);
                }
                else if (flags is 4 or 16 or 64) receivedButtons.Remove(flags / 2);
                else if (flags == 256) receivedButtons.Remove(identity);
                break;
            default: return;
        }
        if (!success) ReportInputError();
    }

    nint KeyboardHook(int code, nint message, nint data)
    {
        if (code < 0) return Native.CallNextHookEx(keyboardHook, code, message, data);
        var key = Marshal.PtrToStructure<Native.KeyboardHook>(data);
        if ((key.Flags & 0x10) != 0) return Native.CallNextHookEx(keyboardHook, code, message, data);
        int vk = (int)key.VirtualKey;
        bool up = (key.Flags & 0x80) != 0;
        if (up) physicalKeys.Remove(vk); else physicalKeys.Add(vk);
        bool modifiers = (physicalKeys.Contains(0xA2) || physicalKeys.Contains(0xA3) || physicalKeys.Contains(0x11)) &&
            (physicalKeys.Contains(0xA4) || physicalKeys.Contains(0xA5) || physicalKeys.Contains(0x12));
        if (!up && modifiers && vk is 0x1B or 0x20)
        {
            if (!shortcutHeld)
            {
                shortcutHeld = true;
                if (vk == 0x1B)
                {
                    StopRemote();
                    SetKeyboardFocus(null);
                    ReleaseReceived();
                    peer?.Send(new Frame(Kind.Release, 1));
                }
                else Toggle(keyboardOnly);
            }
            return 1;
        }
        if (up && vk is 0x1B or 0x20 && shortcutHeld)
        {
            shortcutHeld = false;
            return 1;
        }
        if (localKeys.Contains(vk))
        {
            if (up && localKeys.Remove(vk) && receivedKeys.ContainsKey(vk)) return 1;
            return Native.CallNextHookEx(keyboardHook, code, message, data);
        }
        if (up && forwardedKeys.Remove(vk))
        {
            if (keyboardFollowsPeer == true && peer is { Online: true })
                peer.Send(new Frame(Kind.Key, vk, (int)key.ScanCode, ((key.Flags & 1) != 0 ? 1 : 0) | 2));
            return 1;
        }
        if (keyboardFollowsPeer != true || peer is not { Online: true })
        {
            if (!up) localKeys.Add(vk);
            return Native.CallNextHookEx(keyboardHook, code, message, data);
        }
        int flags = ((key.Flags & 1) != 0 ? 1 : 0) | (up ? 2 : 0);
        if (up) return Native.CallNextHookEx(keyboardHook, code, message, data);
        forwardedKeys.Add(vk);
        if (!peer.Send(new Frame(Kind.Key, vk, (int)key.ScanCode, flags)))
        {
            forwardedKeys.Remove(vk);
            StopRemote();
            SetKeyboardFocus(null);
            localKeys.Add(vk);
            return Native.CallNextHookEx(keyboardHook, code, message, data);
        }
        return 1;
    }

    nint MouseHook(int code, nint message, nint data)
    {
        if (code < 0) return Native.CallNextHookEx(mouseHook, code, message, data);
        var mouse = Marshal.PtrToStructure<Native.MouseHook>(data);
        if ((mouse.Flags & 1) != 0) return Native.CallNextHookEx(mouseHook, code, message, data);
        int msg = (int)message;
        var bounds = Bounds();
        if (msg == 0x200)
        {
            if (IsRemote && !keyboardOnly)
            {
                virtualX += mouse.Point.X - anchor.X;
                virtualY = Math.Clamp(virtualY + mouse.Point.Y - anchor.Y, 0, remoteHeight - 1);
                bool back = peer?.Side == Side.Right ? virtualX < 0 : virtualX >= remoteWidth;
                if (back && forwardedKeys.Count == 0 && localButtons == 0) ReturnLocal();
                else
                {
                    virtualX = Math.Clamp(virtualX, 0, remoteWidth - 1);
                    SendPosition(remoteWidth, remoteHeight);
                }
                return 1;
            }
            int x = mouse.Point.X - bounds.Left;
            if (keyboardFollowsPeer is null && peer is { Online: true } &&
                peer.Send(new Frame(Kind.Focus, 0)))
                SetKeyboardFocus(false);
            if (x > 12 && x < bounds.Width - 13) edgeArmed = true;
            bool edge = peer?.Side == Side.Right ? x >= bounds.Width - 1 : x <= 0;
            if (!IsRemote && !keyboardOnly && peer is { Online: true } && edge && edgeArmed &&
                Environment.TickCount64 > ignoreEdgeUntil && localButtons == 0)
            {
                StartRemote(false, mouse.Point);
                return 1;
            }
            return Native.CallNextHookEx(mouseHook, code, message, data);
        }
        uint flag = msg switch
        {
            0x201 => 2, 0x202 => 4, 0x204 => 8, 0x205 => 16,
            0x207 => 32, 0x208 => 64, 0x20B => 128, 0x20C => 256,
            0x20A => 0x800, 0x20E => 0x1000, _ => 0
        };
        if (flag == 0) return Native.CallNextHookEx(mouseHook, code, message, data);
        if (flag is 2 or 8 or 32 or 128) localButtons++;
        if (flag is 4 or 16 or 64 or 256) localButtons = Math.Max(0, localButtons - 1);
        if (!IsRemote || keyboardOnly) return Native.CallNextHookEx(mouseHook, code, message, data);
        int value = flag is 0x800 or 0x1000 ? (short)(mouse.MouseData >> 16) : (int)(mouse.MouseData >> 16);
        if (peer?.Send(new Frame(Kind.Button, (int)flag, value)) != true) ReturnLocal();
        return 1;
    }

    public void SetKeyboardOnly(bool value)
    {
        if (disposed) return;
        if (!dispatcher.CheckAccess()) { dispatcher.Invoke(() => SetKeyboardOnly(value)); return; }
        ReturnLocal();
        keyboardOnly = value;
    }

    public void SetSide(Side side)
    {
        if (disposed) return;
        if (!dispatcher.CheckAccess()) { dispatcher.Invoke(() => SetSide(side)); return; }
        if (peer is null || peer.Side == side) return;
        StopRemote();
        ReleaseReceived();
        peer.Side = side;
        SetKeyboardFocus(false);
        edgeArmed = false;
        peer.Send(new Frame(Kind.Layout, (int)(side == Side.Left ? Side.Right : Side.Left)));
        Notify();
    }

    void PublishDisplaySize()
    {
        if (peer is not { Online: true }) return;
        var bounds = Bounds();
        if (bounds.Width == advertisedWidth && bounds.Height == advertisedHeight) return;
        if (IsRemote) ReturnLocal();
        if (peer.Send(new Frame(Kind.DisplayInfo, bounds.Width, bounds.Height)))
        {
            advertisedWidth = bounds.Width;
            advertisedHeight = bounds.Height;
        }
    }

    void SendPosition(int width, int height)
    {
        if (peer?.Send(new Frame(Kind.Move,
            Native.NormalizePixel((int)virtualX, width),
            Native.NormalizePixel((int)virtualY, height))) != true) ReturnLocal();
    }

    void ReleaseReceived()
    {
        ReleaseReceivedKeys();
        foreach (uint button in receivedButtons)
        {
            if (button is 129 or 130) Native.Mouse(256, button - 128);
            else Native.Mouse(button * 2);
        }
        receivedButtons.Clear();
    }

    void SetKeyboardFocus(bool? followsPeer)
    {
        if (keyboardFollowsPeer == followsPeer) return;
        if (keyboardFollowsPeer == true && followsPeer != true)
            peer?.Send(new Frame(Kind.Release, 2));
        keyboardFollowsPeer = followsPeer;
        Notify();
    }

    void ReleaseReceivedKeys()
    {
        foreach (var key in receivedKeys)
            if (!localKeys.Contains(key.Key)) Native.Key(key.Key, key.Value.Scan, key.Value.Flags | 2);
        receivedKeys.Clear();
    }


    static (int Left, int Top, int Width, int Height) Bounds() =>
        (Native.GetSystemMetrics(76), Native.GetSystemMetrics(77),
         Math.Max(2, Native.GetSystemMetrics(78)), Math.Max(2, Native.GetSystemMetrics(79)));
    static void Warp(Native.Point point)
    {
        var b = Bounds();
        Native.Mouse(0xC001, 0, Native.NormalizePixel(point.X - b.Left, b.Width),
            Native.NormalizePixel(point.Y - b.Top, b.Height));
    }
    long lastInputError;
    void ReportInputError()
    {
        long now = Environment.TickCount64;
        if (now - lastInputError < 5000) return;
        lastInputError = now;
        uiDispatcher.BeginInvoke(() => Error?.Invoke("Windows blocked remote input. Run both apps at the same permission level."));
    }

    void Notify() => uiDispatcher.BeginInvoke(() => StateChanged?.Invoke());

    public void Dispose()
    {
        if (disposed) return;
        if (!dispatcher.CheckAccess())
        {
            dispatcher.Invoke(Dispose);
            inputThread.Join();
            return;
        }
        Detach();
        disposed = true;
        watchdog?.Stop();
        Native.UnhookWindowsHookEx(mouseHook);
        Native.UnhookWindowsHookEx(keyboardHook);
        dispatcher.BeginInvokeShutdown(DispatcherPriority.Send);
    }
}
