using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Automation;
using System.Windows.Threading;
using Microsoft.Win32;
using Drawing = System.Drawing;
using Forms = System.Windows.Forms;

namespace ZhuanLeMe;

internal sealed record BarContent(bool Enabled, string Line1, string Line2, string Description,
    bool Transparent = true, bool ClickThrough = false, double? Position = null);

// An independent top-level layered window avoids drawing into Explorer's taskbar.
internal sealed class TaskbarWidget : IDisposable
{
    private readonly Action _openSettings;
    private readonly Action _showMenu;
    private readonly Action<double> _savePosition;
    private readonly Action<bool, string> _presence;
    private readonly Thread _thread;
    private Dispatcher? _dispatcher;
    private DispatcherTimer? _safety;
    private TaskbarOverlay? _overlay;
    private RightButtonHold? _hold;
    private volatile BarContent _content = new(false, "", "", "");
    private volatile bool _disposed;
    private int _locating;
    private NativeRect _last, _bar;
    private IntPtr _barHandle;
    private bool _light, _dragging;
    private volatile bool _holdAvailable = true;
    private double _scale = 1;

    public TaskbarWidget(Action openSettings, Action showMenu, Action<double> savePosition, Action<bool, string> presence)
    {
        _openSettings = openSettings; _showMenu = showMenu; _savePosition = savePosition; _presence = presence;
        _thread = new Thread(Run) { IsBackground = true, Name = "赚了么任务栏" };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
    }
    private void Run()
    {
        _dispatcher = Dispatcher.CurrentDispatcher;
        _hold = new RightButtonHold(_showMenu, available =>
        {
            _holdAvailable = available;
            if (!available) _presence(true, "右键长按不可用，请通过托盘打开设置");
        });
        _safety = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _safety.Tick += (_, _) => Locate();
        _safety.Start();
        if (_disposed) _dispatcher.BeginInvokeShutdown(DispatcherPriority.Send);
        else Locate();
        Dispatcher.Run();
        _safety.Stop();
        Destroy();
        _hold.Dispose();
    }
    public void Apply(BarContent content)
    {
        _content = content;
        var dispatcher = _dispatcher;
        if (dispatcher == null || dispatcher.HasShutdownStarted) return;
        dispatcher.BeginInvoke(new Action(() =>
        {
            if (_disposed) return;
            if (!content.Enabled)
            {
                Destroy(); _presence(false, "任务栏显示已关闭"); return;
            }
            if (_overlay != null)
            {
                _overlay.SetClickThrough(content.ClickThrough);
                UpdateHold();
                if (!Render()) { Destroy(); _presence(false, "任务栏暂不可用，已切换为悬浮窗"); }
            }
            Locate();
        }));
    }
    private void Locate()
    {
        if (_disposed || !_content.Enabled || _dragging || Interlocked.Exchange(ref _locating, 1) != 0) return;
        var content = _content;
        _ = Task.Run(() => TaskbarPlacement.Find(!string.IsNullOrEmpty(content.Line2), content.Position)).ContinueWith(task =>
        {
            Interlocked.Exchange(ref _locating, 0);
            var dispatcher = _dispatcher;
            if (_disposed || dispatcher == null || dispatcher.HasShutdownStarted) return;
            try { dispatcher.BeginInvoke(new Action(() =>
            {
                if (_disposed || !_content.Enabled || _dragging) return;
                // A fresh update or drop invalidates this asynchronous lookup.
                if (_content.Position != content.Position || string.IsNullOrEmpty(_content.Line2) != string.IsNullOrEmpty(content.Line2))
                { Locate(); return; }
                if (task.Status != TaskStatus.RanToCompletion || task.Result == null)
                { Destroy(); _presence(false, "任务栏暂时没有足够的可用空间"); return; }
                Place(task.Result.Value);
            })); } catch (InvalidOperationException) { }
        }, TaskScheduler.Default);
    }
    private void Place(TaskbarPlacement layout)
    {
        try
        {
            if (_overlay != null && !TaskbarNative.IsWindow(_overlay.Handle)) Destroy();
            _bar = layout.Bar; _barHandle = layout.Parent; _light = layout.Light; _scale = layout.Scale;
            _overlay ??= new TaskbarOverlay(_showMenu, BeginDrag, MoveDrag, EndDrag);
            _overlay.SetClickThrough(_content.ClickThrough);
            _last = layout.Target;
            if (!TaskbarNative.SetWindowPos(_overlay.Handle, new IntPtr(-1), _last.Left, _last.Top,
                _last.Width, _last.Height, 0x0050)) throw new InvalidOperationException("无法放置任务栏窗口。");
            if (!Render()) throw new InvalidOperationException("任务栏绘制失败。");
            UpdateHold();
            _presence(true, _content.ClickThrough && !_holdAvailable
                ? "右键长按不可用，请通过托盘打开设置"
                : "已显示在任务栏，可关闭穿透后拖动位置");
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or ArgumentException or ExternalException)
        { Destroy(); _presence(false, "任务栏暂不可用，已切换为悬浮窗"); }
    }
    private void BeginDrag() => _dragging = true;
    private void MoveDrag(int left)
    {
        if (_overlay == null || _bar.Width < _last.Width) return;
        left = Math.Clamp(left, _bar.Left, _bar.Right - _last.Width);
        _last = new NativeRect(left, _bar.Top, left + _last.Width, _bar.Bottom);
        TaskbarNative.SetWindowPos(_overlay.Handle, new IntPtr(-1), _last.Left, _last.Top,
            _last.Width, _last.Height, 0x0050);
        UpdateHold();
    }
    private void EndDrag(bool? moved)
    {
        _dragging = false;
        if (moved == true && _bar.Width > _last.Width)
        {
            var fraction = (double)(_last.Left - _bar.Left) / (_bar.Width - _last.Width);
            fraction = Math.Clamp(fraction, 0, 1);
            _content = _content with { Position = fraction };
            _savePosition(fraction);
        }
        else if (moved == false) _openSettings();
        else Locate();
    }
    private void UpdateHold() =>
        _hold?.Configure(_overlay != null && _content.ClickThrough, _last, _barHandle);

    private bool Render()
    {
        if (_overlay == null || _last.Width <= 0 || _last.Height <= 0) return false;
        TaskbarNative.SetWindowText(_overlay.Handle, _content.Description);
        using var bitmap = new Drawing.Bitmap(_last.Width, _last.Height, Drawing.Imaging.PixelFormat.Format32bppPArgb);
        using (var g = Drawing.Graphics.FromImage(bitmap))
        using (var format = new Drawing.StringFormat { Alignment = Drawing.StringAlignment.Center, LineAlignment = Drawing.StringAlignment.Center, Trimming = Drawing.StringTrimming.EllipsisCharacter, FormatFlags = Drawing.StringFormatFlags.NoWrap })
        using (var brush = new Drawing.SolidBrush(_light ? Drawing.Color.Black : Drawing.Color.White))
        using (var shadow = new Drawing.SolidBrush(_light ? Drawing.Color.FromArgb(190, Drawing.Color.White) : Drawing.Color.FromArgb(200, Drawing.Color.Black)))
        {
            // Alpha 1 is visually transparent but keeps the whole rectangle draggable.
            g.Clear(Drawing.Color.FromArgb(1, 0, 0, 0));
            g.SmoothingMode = Drawing.Drawing2D.SmoothingMode.AntiAlias;
            if (!_content.Transparent)
            {
                using var background = new Drawing.SolidBrush(_light ? Drawing.Color.FromArgb(245, 245, 245) : Drawing.Color.FromArgb(32, 32, 32));
                using var shape = new Drawing.Drawing2D.GraphicsPath();
                var inset = (float)Math.Max(2, 3 * _scale); var top = (float)Math.Max(2, 4 * _scale);
                var w = _last.Width - inset * 2; var h = _last.Height - top * 2;
                var diameter = (float)Math.Min(12 * _scale, Math.Min(w, h));
                shape.AddArc(inset, top, diameter, diameter, 180, 90);
                shape.AddArc(inset + w - diameter, top, diameter, diameter, 270, 90);
                shape.AddArc(inset + w - diameter, top + h - diameter, diameter, diameter, 0, 90);
                shape.AddArc(inset, top + h - diameter, diameter, diameter, 90, 90);
                shape.CloseFigure(); g.FillPath(background, shape);
            }
            g.TextRenderingHint = Drawing.Text.TextRenderingHint.AntiAliasGridFit;
            var separate = !string.IsNullOrEmpty(_content.Line2);
            using var font = new Drawing.Font("Microsoft YaHei UI", (float)((separate ? 11 : 13) * _scale),
                separate ? Drawing.FontStyle.Regular : Drawing.FontStyle.Bold, Drawing.GraphicsUnit.Pixel);
            void Draw(string text, Drawing.RectangleF rect)
            {
                if (_content.Transparent) { var shade = rect; shade.Offset(1, 1); g.DrawString(text, font, shadow, shade, format); }
                g.DrawString(text, font, brush, rect, format);
            }
            if (separate)
            {
                Draw(_content.Line1, new Drawing.RectangleF(3, _last.Height * .16f, _last.Width - 6, _last.Height * .34f));
                Draw(_content.Line2, new Drawing.RectangleF(3, _last.Height * .50f, _last.Width - 6, _last.Height * .34f));
            }
            else Draw(_content.Line1, new Drawing.RectangleF(3, 0, _last.Width - 6, _last.Height));
        }
        var screen = TaskbarNative.GetDC(IntPtr.Zero);
        var memory = TaskbarNative.CreateCompatibleDC(screen);
        var image = bitmap.GetHbitmap(Drawing.Color.FromArgb(0));
        var previous = TaskbarNative.SelectObject(memory, image);
        try
        {
            var size = new NativeSize(_last.Width, _last.Height); var source = new NativePoint(0, 0);
            var blend = new NativeBlend { SourceConstantAlpha = 255, AlphaFormat = 1 };
            return TaskbarNative.UpdateLayeredWindow(_overlay.Handle, screen, IntPtr.Zero, ref size, memory, ref source, 0, ref blend, 2);
        }
        finally
        {
            TaskbarNative.SelectObject(memory, previous); TaskbarNative.DeleteObject(image);
            TaskbarNative.DeleteDC(memory); TaskbarNative.ReleaseDC(IntPtr.Zero, screen);
        }
    }
    private void Destroy()
    {
        _dragging = false; _hold?.Configure(false, default, IntPtr.Zero);
        _overlay?.DestroyHandle(); _overlay = null; _barHandle = IntPtr.Zero; _last = default;
    }
    public void Dispose()
    {
        _disposed = true;
        var dispatcher = _dispatcher;
        if (dispatcher != null && !dispatcher.HasShutdownStarted) dispatcher.BeginInvokeShutdown(DispatcherPriority.Send);
        if (Thread.CurrentThread != _thread) _thread.Join(TimeSpan.FromSeconds(2));
    }
}

internal sealed class TaskbarOverlay : Forms.NativeWindow
{
    private readonly Action _menu, _begin;
    private readonly Action<int> _move;
    private readonly Action<bool?> _end;
    private bool _pressed, _moved, _clickThrough;
    private int _startX, _grab;
    public TaskbarOverlay(Action menu, Action begin, Action<int> move, Action<bool?> end)
    {
        _menu = menu; _begin = begin; _move = move; _end = end;
        CreateHandle(new Forms.CreateParams
        {
            Caption = "赚了么任务栏", X = 0, Y = 0, Width = 1, Height = 1,
            Style = unchecked((int)0x90000000), ExStyle = unchecked((int)0x08080088)
        });
    }
    public void SetClickThrough(bool enabled)
    {
        if (_clickThrough == enabled) return;
        var style = TaskbarNative.GetWindowExStyle(Handle);
        TaskbarNative.SetWindowExStyle(Handle, enabled ? style | 0x20 : style & ~0x20);
        _clickThrough = enabled;
    }
    protected override void WndProc(ref Forms.Message message)
    {
        switch (message.Msg)
        {
            case 0x0021: message.Result = new IntPtr(3); return; // MA_NOACTIVATE
            case 0x0201: // left down
                if (TaskbarNative.GetCursorPos(out var start) && TaskbarNative.GetWindowRect(Handle, out var rect))
                { _pressed = true; _moved = false; _startX = start.X; _grab = start.X - rect.Left; _begin(); TaskbarNative.SetCapture(Handle); }
                message.Result = IntPtr.Zero; return;
            case 0x0200: // move
                if (_pressed && TaskbarNative.GetCursorPos(out var point))
                {
                    if (Math.Abs(point.X - _startX) >= Math.Max(4, TaskbarNative.GetSystemMetrics(68))) _moved = true;
                    if (_moved) _move(point.X - _grab);
                }
                message.Result = IntPtr.Zero; return;
            case 0x0202: // left up
                if (_pressed)
                {
                    var moved = _moved; _pressed = false; _moved = false;
                    if (TaskbarNative.GetCapture() == Handle) TaskbarNative.ReleaseCapture();
                    _end(moved);
                }
                message.Result = IntPtr.Zero; return;
            case 0x0215: // capture changed
                if (_pressed) { _pressed = false; _moved = false; _end(null); }
                break;
            case 0x0205: _menu(); message.Result = IntPtr.Zero; return;
        }
        base.WndProc(ref message);
    }
}

[StructLayout(LayoutKind.Sequential)]
internal struct NativeRect
{
    public int Left, Top, Right, Bottom;
    public NativeRect(int left, int top, int right, int bottom) { Left = left; Top = top; Right = right; Bottom = bottom; }
    public int Width => Right - Left;
    public int Height => Bottom - Top;
    public bool Contains(NativePoint p) => p.X >= Left && p.X < Right && p.Y >= Top && p.Y < Bottom;
}
[StructLayout(LayoutKind.Sequential)] internal struct NativePoint { public int X, Y; public NativePoint(int x, int y) { X = x; Y = y; } }
[StructLayout(LayoutKind.Sequential)] internal struct NativeSize { public int Width, Height; public NativeSize(int w, int h) { Width = w; Height = h; } }
[StructLayout(LayoutKind.Sequential, Pack = 1)] internal struct NativeBlend { public byte BlendOp, BlendFlags, SourceConstantAlpha, AlphaFormat; }

internal readonly record struct TaskbarPlacement(IntPtr Parent, NativeRect Bar, NativeRect Target, bool Light, double Scale)
{
    public static TaskbarPlacement? Find(bool twoLines, double? desiredPosition)
    {
        // Tests can supply their own taskbar-shaped window without touching Explorer.
        var testHandle = Environment.GetEnvironmentVariable("ZHUANLEME_TEST_TASKBAR_HWND");
        var handle = long.TryParse(testHandle, NumberStyles.Integer, CultureInfo.InvariantCulture, out var raw) && raw != 0
            ? new IntPtr(raw) : TaskbarNative.FindWindow("Shell_TrayWnd", null);
        if (handle == IntPtr.Zero || !TaskbarNative.IsWindowVisible(handle) || !TaskbarNative.GetWindowRect(handle, out var bar) ||
            bar.Width < bar.Height || bar.Width < 300 || bar.Height < 20) return null;
        var screen = Forms.Screen.FromHandle(handle).Bounds;
        var visibleHeight = Math.Min(bar.Bottom, screen.Bottom) - Math.Max(bar.Top, screen.Top);
        if (visibleHeight < bar.Height / 2) return null; // auto-hidden taskbar
        var dpi = TaskbarNative.GetDpiForWindow(handle); var scale = dpi > 0 ? dpi / 96.0 : 1.0;
        var width = (int)Math.Ceiling((twoLines ? 160 : 142) * scale);
        if (width > bar.Width) return null;
        var gap = Math.Max(4, (int)Math.Ceiling(5 * scale));
        var occupied = new List<(int Left, int Right)>();
        void Reserve(int left, int right)
        {
            left = Math.Max(bar.Left, left - gap); right = Math.Min(bar.Right, right + gap);
            if (right > left) occupied.Add((left, right));
        }
        int? position = desiredPosition.HasValue
            ? bar.Left + (int)Math.Round(Math.Clamp(desiredPosition.Value, 0, 1) * (bar.Width - width)) : null;
        if (!position.HasValue)
        {
            try
            {
                var root = AutomationElement.FromHandle(handle);
                var items = root.FindAll(TreeScope.Descendants, Condition.TrueCondition);
                foreach (AutomationElement item in items)
                {
                    try
                    {
                        var current = item.Current;
                        if (current.ProcessId == Environment.ProcessId) continue;
                        var rect = current.BoundingRectangle;
                        if (rect.IsEmpty || rect.Bottom <= bar.Top || rect.Top >= bar.Bottom) continue;
                        var cls = current.ClassName ?? "";
                        var protect = current.ControlType == ControlType.Button || cls == "TrayNotifyWnd" ||
                            cls.EndsWith(".TaskbarWidget", StringComparison.Ordinal) || cls.StartsWith("WindowsForms10.Window", StringComparison.Ordinal);
                        if (protect && rect.Width > 0 && rect.Width < bar.Width * .9)
                            Reserve((int)Math.Floor(rect.Left), (int)Math.Ceiling(rect.Right));
                    }
                    catch (ElementNotAvailableException) { }
                }
            }
            catch (Exception ex) when (ex is ElementNotAvailableException or InvalidOperationException or COMException) { return null; }
            var tray = TaskbarNative.FindWindowEx(handle, IntPtr.Zero, "TrayNotifyWnd", null);
            if (tray != IntPtr.Zero && TaskbarNative.GetWindowRect(tray, out var trayRect)) Reserve(trayRect.Left, trayRect.Right);
            occupied.Sort((a, b) => a.Left.CompareTo(b.Left));
            var cursor = bar.Left + gap;
            foreach (var range in occupied)
            {
                if (range.Left - cursor >= width) position = range.Left - width;
                cursor = Math.Max(cursor, range.Right);
            }
            if (bar.Right - gap - cursor >= width) position = bar.Right - gap - width;
        }
        if (!position.HasValue) return null;
        var light = false;
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            light = Convert.ToInt32(key?.GetValue("SystemUsesLightTheme", 0), CultureInfo.InvariantCulture) != 0;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException) { }
        return new(handle, bar, new NativeRect(position.Value, bar.Top, position.Value + width, bar.Bottom), light, scale);
    }
}

internal static class TaskbarNative
{
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern IntPtr FindWindow(string name, string? title);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string name, string? title);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern bool SetWindowText(IntPtr window, string text);
    [DllImport("user32.dll")] internal static extern bool GetWindowRect(IntPtr window, out NativeRect rect);
    [DllImport("user32.dll")] internal static extern bool IsWindow(IntPtr window);
    [DllImport("user32.dll")] internal static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32.dll")] internal static extern uint GetDpiForWindow(IntPtr window);
    [DllImport("user32.dll")] internal static extern bool SetWindowPos(IntPtr window, IntPtr after, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll")] internal static extern bool GetCursorPos(out NativePoint p);
    [DllImport("user32.dll")] internal static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll")] internal static extern IntPtr SetCapture(IntPtr window);
    [DllImport("user32.dll")] internal static extern IntPtr GetCapture();
    [DllImport("user32.dll")] internal static extern bool ReleaseCapture();
    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")] private static extern int GetWindowLong32(IntPtr h, int index);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern IntPtr GetWindowLongPtr64(IntPtr h, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongW")] private static extern int SetWindowLong32(IntPtr h, int index, int value);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] private static extern IntPtr SetWindowLongPtr64(IntPtr h, int index, IntPtr value);
    internal static long GetWindowExStyle(IntPtr h) => IntPtr.Size == 8 ? GetWindowLongPtr64(h, -20).ToInt64() : GetWindowLong32(h, -20);
    internal static void SetWindowExStyle(IntPtr h, long value)
    {
        if (IntPtr.Size == 8) SetWindowLongPtr64(h, -20, new IntPtr(value));
        else SetWindowLong32(h, -20, (int)value);
    }
    [DllImport("user32.dll")] internal static extern IntPtr GetDC(IntPtr window);
    [DllImport("user32.dll")] internal static extern int ReleaseDC(IntPtr window, IntPtr dc);
    [DllImport("gdi32.dll")] internal static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll")] internal static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);
    [DllImport("gdi32.dll")] internal static extern bool DeleteObject(IntPtr obj);
    [DllImport("gdi32.dll")] internal static extern bool DeleteDC(IntPtr dc);
    [DllImport("user32.dll", SetLastError = true)] internal static extern bool UpdateLayeredWindow(IntPtr hwnd, IntPtr dstDc, IntPtr dst, ref NativeSize size, IntPtr srcDc, ref NativePoint src, uint color, ref NativeBlend blend, uint flags);
}
