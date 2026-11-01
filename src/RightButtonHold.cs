using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Threading;

namespace ZhuanLeMe;

// Installed only for an enabled, click-through taskbar widget. The hook does
// no rendering or IO and only intercepts right-button presses over its bounds.
internal sealed class RightButtonHold : IDisposable
{
    private sealed record Target(bool Enabled, NativeRect Rect, IntPtr Taskbar);
    private const ulong ReplayMarker = 0x5A4C4D21;
    private readonly Action _openMenu;
    private readonly Action<bool> _availability;
    private readonly Thread _thread;
    private readonly HookProcedure _callback;
    private volatile Target _target = new(false, default, IntPtr.Zero);
    private volatile bool _disposed;
    private Dispatcher? _dispatcher;
    private DispatcherTimer? _timer;
    private IntPtr _hook;
    private bool _pressed, _held;
    private NativePoint _down;

    public RightButtonHold(Action openMenu, Action<bool> availability)
    {
        _openMenu = openMenu; _availability = availability; _callback = Hook;
        _thread = new Thread(Run) { IsBackground = true, Name = "赚了么长按" };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
    }
    public void Configure(bool enabled, NativeRect rect, IntPtr taskbar)
    {
        _target = new Target(enabled, rect, taskbar);
        var dispatcher = _dispatcher;
        if (dispatcher != null && !dispatcher.HasShutdownStarted)
            dispatcher.BeginInvoke(new Action(RefreshHook));
    }
    private void Run()
    {
        _dispatcher = Dispatcher.CurrentDispatcher;
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(700) };
        _timer.Tick += (_, _) =>
        {
            _timer.Stop();
            if (!_pressed || !_target.Enabled) return;
            _held = true;
            try { _openMenu(); } catch (InvalidOperationException) { }
        };
        RefreshHook();
        if (_disposed) _dispatcher.BeginInvokeShutdown(DispatcherPriority.Send);
        Dispatcher.Run();
        _timer.Stop();
        if (_pressed && !_held) Replay(false);
        Uninstall();
    }
    private void RefreshHook()
    {
        if (_target.Enabled && !_disposed)
        {
            if (_hook == IntPtr.Zero)
            {
                _hook = SetWindowsHookEx(14, _callback, GetModuleHandle(null), 0);
                _availability(_hook != IntPtr.Zero);
            }
            return;
        }
        _timer?.Stop();
        if (_pressed && !_held)
        {
            // Deliver the withheld down; the eventual physical up then follows normally.
            Replay(false);
            _pressed = false;
        }
        if (!_pressed) Uninstall();
    }
    private void Uninstall()
    {
        if (_hook != IntPtr.Zero) { UnhookWindowsHookEx(_hook); _hook = IntPtr.Zero; }
    }
    private IntPtr Hook(int code, IntPtr message, IntPtr data)
    {
        if (code < 0) return CallNextHookEx(_hook, code, message, data);
        try
        {
            var mouse = Marshal.PtrToStructure<MouseHookData>(data);
            if (mouse.ExtraInfo.ToUInt64() == ReplayMarker) return CallNextHookEx(_hook, code, message, data);
            var msg = message.ToInt32();
            if (msg == 0x0204 && !_pressed)
            {
                var target = _target;
                // Only interfere with the taskbar itself, never an unrelated popup above it.
                var hit = WindowFromPoint(mouse.Point);
                if (target.Enabled && target.Rect.Contains(mouse.Point) &&
                    GetAncestor(hit, 2) == target.Taskbar)
                {
                    _down = mouse.Point; _pressed = true; _held = false;
                    _timer!.Stop(); _timer.Start();
                    return new IntPtr(1);
                }
            }
            else if (msg == 0x0200 && _pressed && !_held &&
                (Math.Abs(mouse.Point.X - _down.X) > 8 || Math.Abs(mouse.Point.Y - _down.Y) > 8))
            {
                // This is a drag rather than a long press: let the underlying window receive it.
                _timer!.Stop(); _pressed = false;
                _dispatcher!.BeginInvoke(new Action(() => Replay(false)), DispatcherPriority.Send);
            }
            else if (msg == 0x0205 && _pressed)
            {
                _timer!.Stop();
                var held = _held; _pressed = false; _held = false;
                // Do not inject recursively while Windows is still dispatching the original up.
                if (!held) _dispatcher!.BeginInvoke(new Action(() => Replay(true)), DispatcherPriority.Send);
                if (!_target.Enabled) _dispatcher!.BeginInvoke(new Action(RefreshHook));
                return new IntPtr(1);
            }
        }
        catch (Exception) { /* A hook must fail open and return immediately. */ }
        return CallNextHookEx(_hook, code, message, data);
    }
    private static void Replay(bool includeUp)
    {
        var inputs = includeUp ? new Input[2] : new Input[1];
        inputs[0].Type = 0; inputs[0].Data.Mouse.Flags = 0x0008;
        inputs[0].Data.Mouse.ExtraInfo = new UIntPtr(ReplayMarker);
        if (includeUp)
        {
            inputs[1].Type = 0; inputs[1].Data.Mouse.Flags = 0x0010;
            inputs[1].Data.Mouse.ExtraInfo = new UIntPtr(ReplayMarker);
        }
        SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<Input>());
    }
    public void Dispose()
    {
        _disposed = true;
        var dispatcher = _dispatcher;
        if (dispatcher != null && !dispatcher.HasShutdownStarted) dispatcher.BeginInvokeShutdown(DispatcherPriority.Send);
        if (Thread.CurrentThread != _thread) _thread.Join(TimeSpan.FromSeconds(2));
    }

    private delegate IntPtr HookProcedure(int code, IntPtr message, IntPtr data);
    [StructLayout(LayoutKind.Sequential)]
    private struct MouseHookData
    {
        public NativePoint Point;
        public uint MouseData, Flags, Time;
        public UIntPtr ExtraInfo;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct MouseInput
    {
        public int X, Y;
        public uint MouseData, Flags, Time;
        public UIntPtr ExtraInfo;
    }
    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion { [FieldOffset(0)] public MouseInput Mouse; }
    [StructLayout(LayoutKind.Sequential)]
    private struct Input { public uint Type; public InputUnion Data; }
    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr SetWindowsHookEx(int id, HookProcedure callback, IntPtr module, uint thread);
    [DllImport("user32.dll")] private static extern bool UnhookWindowsHookEx(IntPtr hook);
    [DllImport("user32.dll")] private static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr message, IntPtr data);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandle(string? name);
    [DllImport("user32.dll")] private static extern IntPtr WindowFromPoint(NativePoint point);
    [DllImport("user32.dll")] private static extern IntPtr GetAncestor(IntPtr window, uint flags);
    [DllImport("user32.dll", SetLastError = true)] private static extern uint SendInput(uint count, Input[] inputs, int size);
}
