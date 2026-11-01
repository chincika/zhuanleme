using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using Forms = System.Windows.Forms;
using ZhuanLeMe;

internal static class Program
{
    private static int _failures, _checks, _menus, _settings, _rightDown, _rightUp, _leftDown;
    private static bool _testLeftDown, _testRightDown;
    private static double? _savedPosition;
    private static TaskbarWidget? _widget;
    private static Forms.Form? _host;
    private static readonly string Caption = "ZhuanLeMe isolated taskbar fixture";

    [STAThread]
    private static int Main(string[] args)
    {
        Forms.Application.EnableVisualStyles();
        var area = Forms.Screen.PrimaryScreen!.WorkingArea;
        _host = new Forms.Form
        {
            Text = "ZhuanLeMe simulated taskbar", FormBorderStyle = Forms.FormBorderStyle.None,
            ShowInTaskbar = false, TopMost = true, StartPosition = Forms.FormStartPosition.Manual,
            Location = new Point(area.Left + 100, area.Top + 120), Size = new Size(900, 72),
            BackColor = Color.FromArgb(45, 75, 95)
        };
        if (args.Contains("--host"))
        {
            _host.Shown += (_, _) => System.IO.File.WriteAllText(
                Environment.GetEnvironmentVariable("ZHUANLEME_TEST_HOST_PATH")!, _host.Handle.ToInt64().ToString());
            Forms.Application.Run(_host);
            return 0;
        }
        _host.MouseDown += (_, e) => { if (e.Button == Forms.MouseButtons.Right) _rightDown++; if (e.Button == Forms.MouseButtons.Left) _leftDown++; };
        _host.MouseUp += (_, e) => { if (e.Button == Forms.MouseButtons.Right) _rightUp++; };
        _host.Shown += async (_, _) =>
        {
            GetCursorPos(out var original);
            try { await Run(_host); }
            catch (Exception ex) { _failures++; Console.WriteLine("FAIL: " + ex); }
            finally
            {
                if (_testLeftDown) MouseUp(false);
                if (_testRightDown) MouseUp(true);
                _widget?.Dispose();
                SetCursorPos(original.X, original.Y);
                _host.Close();
            }
        };
        Forms.Application.Run(_host);
        Console.WriteLine($"UI smoke: {_checks} checks, {_failures} failures");
        return _failures == 0 ? 0 : 1;
    }
    private static void Check(bool value, string label)
    {
        _checks++;
        if (value) Console.WriteLine("PASS: " + label);
        else { _failures++; Console.WriteLine("FAIL: " + label); }
    }
    private static async Task WaitFor(Func<bool> condition, string label)
    {
        var timer = Stopwatch.StartNew();
        while (!condition() && timer.ElapsedMilliseconds < 6000) await Task.Delay(50);
        if (!condition()) throw new Exception("Timed out: " + label);
    }
    private static async Task Run(Forms.Form host)
    {
        await WaitFor(() => (GetAsyncKeyState(1) & 0x8000) == 0 && (GetAsyncKeyState(2) & 0x8000) == 0, "mouse buttons released");
        Environment.SetEnvironmentVariable("ZHUANLEME_TEST_TASKBAR_HWND", host.Handle.ToInt64().ToString());
        _widget = new TaskbarWidget(() => Interlocked.Increment(ref _settings),
            () => Interlocked.Increment(ref _menus), position => _savedPosition = position, (_, _) => { });
        BarContent Content(bool transparent, bool clickThrough, double? position) =>
            new(true, "工资 ¥100.0000", "公积金 ¥50.0000", Caption, transparent, clickThrough, position);
        _widget.Apply(Content(true, false, .35));
        IntPtr overlay() => FindWindow(null, Caption);
        await WaitFor(() => overlay() != IntPtr.Zero && IsWindowVisible(overlay()), "overlay appears");
        await Task.Delay(350);
        var h = overlay();
        GetWindowRect(h, out var rect);
        Check(GetParent(h) == IntPtr.Zero, "overlay is independent of simulated taskbar");
        Check((TaskbarNative.GetWindowExStyle(h) & 0x8) != 0, "overlay is topmost");
        var color = Pixel(rect.Left + 12, rect.Top + 8);
        Console.WriteLine($"Sample transparent pixel: {color.R},{color.G},{color.B}; host: {host.BackColor.R},{host.BackColor.G},{host.BackColor.B}");
        Check(Math.Abs(color.R - host.BackColor.R) <= 2 && Math.Abs(color.G - host.BackColor.G) <= 2 &&
              Math.Abs(color.B - host.BackColor.B) <= 2, "transparent background preserves underlying pixels");
        Check(WindowFromPoint(new NativePoint(rect.Left + 12, rect.Top + 8)) == h, "transparent background still accepts dragging");

        _widget.Apply(Content(false, false, .35));
        await Task.Delay(350);
        var opaque = Pixel(rect.Left + 15, rect.Top + 15);
        Check(Math.Abs(opaque.R - host.BackColor.R) > 5, "opaque background can be selected");
        _widget.Apply(Content(true, false, .35));
        await Task.Delay(250);
        GetWindowRect(h, out rect);
        var startX = rect.Left + rect.Width / 2;
        var y = rect.Top + rect.Height / 2;
        if (WindowFromPoint(new NativePoint(startX, y)) != h) throw new Exception("Drag target is not the fixture overlay");
        SetCursorPos(startX, y); await Task.Delay(80);
        MouseDown(false); await Task.Delay(100);
        SetCursorPos(startX + 300, y); await Task.Delay(180);
        MouseUp(false);
        await WaitFor(() => _savedPosition.HasValue, "drag persisted");
        GetWindowRect(h, out var moved);
        Check(moved.Left > rect.Left + 250 && _savedPosition > .35, "drag moves and saves taskbar position");
        Check(_settings == 0, "drag does not open settings");
        Check(Uniform(rect, host.BackColor), "dragging leaves no old image behind");

        _widget.Apply(Content(true, true, _savedPosition));
        await Task.Delay(500);
        GetWindowRect(h, out moved);
        var x = moved.Left + 12; y = moved.Top + 8;
        Check(WindowFromPoint(new NativePoint(x, y)) == host.Handle, "click-through exposes the underlying taskbar");
        if (WindowFromPoint(new NativePoint(x, y)) != host.Handle) throw new Exception("Click target is not the fixture");
        SetCursorPos(x, y); await Task.Delay(80);
        MouseDown(false); await Task.Delay(70);
        MouseUp(false); await Task.Delay(200);
        Check(_leftDown > 0, "left click passes through");

        var down = _rightDown; var up = _rightUp;
        MouseDown(true); await Task.Delay(120);
        MouseUp(true);
        await WaitFor(() => _rightUp > up, "short right click reaches underlying window");
        Console.WriteLine($"Short right click: down {down}->{_rightDown}, up {up}->{_rightUp}, menus {_menus}");
        Check(_rightDown == down + 1 && _rightUp == up + 1 && _menus == 0, "short right click passes through once");

        down = _rightDown; up = _rightUp;
        MouseDown(true);
        await WaitFor(() => _menus == 1, "long right press opens menu");
        MouseUp(true); await Task.Delay(250);
        Check(_rightDown == down && _rightUp == up, "long press does not trigger underlying right click");

        _widget.Dispose(); _widget = null;
        await Task.Delay(350);
        Check(!IsWindow(h), "disposing removes overlay window");
        Check(Uniform(moved, host.BackColor), "closing leaves no visible taskbar artifact");
        _widget = new TaskbarWidget(() => Interlocked.Increment(ref _settings),
            () => Interlocked.Increment(ref _menus), position => _savedPosition = position, (_, _) => { });
        _widget.Apply(Content(true, false, _savedPosition));
        await WaitFor(() => overlay() != IntPtr.Zero && IsWindowVisible(overlay()), "overlay restores");
        GetWindowRect(overlay(), out var restored);
        Check(Math.Abs(restored.Left - moved.Left) <= 2, "saved position is restored after restart");
    }
    private static Color Pixel(int x, int y)
    {
        using var bitmap = new Bitmap(1, 1);
        using var g = Graphics.FromImage(bitmap);
        g.CopyFromScreen(x, y, 0, 0, new Size(1, 1));
        return bitmap.GetPixel(0, 0);
    }
    private static bool Uniform(NativeRect rect, Color expected)
    {
        using var bitmap = new Bitmap(rect.Width, rect.Height);
        using var g = Graphics.FromImage(bitmap);
        g.CopyFromScreen(rect.Left, rect.Top, 0, 0, bitmap.Size);
        for (var y = 0; y < bitmap.Height; y += 2)
            for (var x = 0; x < bitmap.Width; x += 2)
            {
                var pixel = bitmap.GetPixel(x, y);
                if (Math.Abs(pixel.R - expected.R) > 2 || Math.Abs(pixel.G - expected.G) > 2 || Math.Abs(pixel.B - expected.B) > 2)
                    return false;
            }
        return true;
    }
    private static void MouseDown(bool right)
    {
        if (right) _testRightDown = true; else _testLeftDown = true;
        mouse_event(right ? 0x0008u : 0x0002u, 0, 0, 0, UIntPtr.Zero);
    }
    private static void MouseUp(bool right)
    {
        mouse_event(right ? 0x0010u : 0x0004u, 0, 0, 0, UIntPtr.Zero);
        if (right) _testRightDown = false; else _testLeftDown = false;
    }
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr FindWindow(string? name, string? title);
    [DllImport("user32.dll")] private static extern IntPtr GetParent(IntPtr h);
    [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr h);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr h, out NativeRect rect);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out NativePoint point);
    [DllImport("user32.dll")] private static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int key);
    [DllImport("user32.dll")] private static extern IntPtr WindowFromPoint(NativePoint point);
    [DllImport("user32.dll")] private static extern void mouse_event(uint flags, uint dx, uint dy, uint data, UIntPtr info);
}
