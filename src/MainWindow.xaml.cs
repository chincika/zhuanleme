using System;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using Forms = System.Windows.Forms;

namespace ZhuanLeMe;

public partial class MainWindow : Window
{
    private readonly SettingsStore _store = new();
    private readonly HolidayService _holidays;
    private readonly Forms.NotifyIcon _tray;
    private readonly DispatcherTimer _timer;
    private readonly DispatcherTimer _holidayTimer;
    private readonly TaskbarWidget _taskbar;
    private AppSettings? _settings;
    private SettingsWindow? _settingsWindow;
    private bool _exiting;
    private bool _userHidden;
    private bool _taskbarReady;
    private string _taskbarStatus = "正在连接任务栏";
    private int _lastYear;
    private bool? _calendarNeedsNotice;

    public MainWindow()
    {
        InitializeComponent();
        _settings = _store.Load();
        _holidays = new HolidayService(_store.FilePath);
        _holidays.Changed += () =>
        {
            if (!Dispatcher.HasShutdownStarted) Dispatcher.BeginInvoke(new Action(() =>
            { RefreshDisplay(); _settingsWindow?.UpdateCalendar(_holidays.Calendar); }));
        };
        Topmost = _settings?.AlwaysOnTop ?? true;
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("打开设置", null, (_, _) => OpenSettings());
        menu.Items.Add("显示悬浮窗", null, (_, _) => ShowFloatingNow());
        var barThrough = new Forms.ToolStripMenuItem("任务栏鼠标穿透") { CheckOnClick = true };
        var floatingThrough = new Forms.ToolStripMenuItem("悬浮窗鼠标穿透") { CheckOnClick = true };
        barThrough.Click += (_, _) => ChangeClickThrough(true, barThrough.Checked);
        floatingThrough.Click += (_, _) => ChangeClickThrough(false, floatingThrough.Checked);
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add(barThrough);
        menu.Items.Add(floatingThrough);
        menu.Opening += (_, _) =>
        {
            barThrough.Enabled = floatingThrough.Enabled = _settings != null;
            barThrough.Checked = _settings?.TaskbarClickThrough == true;
            floatingThrough.Checked = _settings?.FloatingClickThrough == true;
        };
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("退出赚了么", null, (_, _) => ExitApplication());
        System.Drawing.Icon? icon = null;
        try { if (Environment.ProcessPath != null) icon = System.Drawing.Icon.ExtractAssociatedIcon(Environment.ProcessPath); } catch { }
        _tray = new Forms.NotifyIcon
        {
            Icon = icon ?? System.Drawing.SystemIcons.Application, Text = "赚了么", ContextMenuStrip = menu,
            // Automated fixtures use a mock taskbar and must not add icons to the user's notification area.
            Visible = Environment.GetEnvironmentVariable("ZHUANLEME_TEST_DISABLE_TRAY") != "1"
        };
        _tray.DoubleClick += (_, _) => RestoreFromTray();
        _taskbar = new TaskbarWidget(
            () => Dispatcher.BeginInvoke(new Action(OpenSettings)),
            () => Dispatcher.BeginInvoke(new Action(() => _tray.ContextMenuStrip?.Show(Forms.Cursor.Position))),
            position => Dispatcher.BeginInvoke(new Action(() => SaveTaskbarPosition(position))),
            (ready, status) => Dispatcher.BeginInvoke(new Action(() => TaskbarPresenceChanged(ready, status))));
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += (_, _) => RefreshDisplay();
        _timer.Start();
        _holidayTimer = new DispatcherTimer { Interval = TimeSpan.FromHours(6) };
        _holidayTimer.Tick += (_, _) => _ = _holidays.RefreshAsync();
        _holidayTimer.Start();
        SourceInitialized += (_, _) => ApplyFloatingOptions();
        IsVisibleChanged += (_, _) => { if (IsVisible) ApplyFloatingOptions(); };
        Loaded += (_, _) =>
        {
            PlaceWindow();
            RefreshDisplay();
            if (_settings == null) Dispatcher.BeginInvoke(new Action(OpenSettings), DispatcherPriority.ApplicationIdle);
            else ApplySurfaces();
            _ = _holidays.RefreshAsync();
        };
        Closing += OnClosing;
    }

    private void PlaceWindow()
    {
        var area = SystemParameters.WorkArea;
        var x = _settings?.WindowX; var y = _settings?.WindowY;
        var bounds = new Rect(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop,
            SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);
        if (x.HasValue && y.HasValue && double.IsFinite(x.Value) && double.IsFinite(y.Value) &&
            bounds.IntersectsWith(new Rect(x.Value, y.Value, Width, Height))) { Left = x.Value; Top = y.Value; }
        else { Left = area.Right - Width - 18; Top = area.Bottom - Height - 18; }
    }

    private void Card_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left || e.ButtonState != MouseButtonState.Pressed) return;
        try { DragMove(); } catch (InvalidOperationException) { return; }
        if (_settings == null) return;
        _settings.WindowX = Left; _settings.WindowY = Top;
        try { _store.Save(_settings); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { _tray.ShowBalloonTip(3000, "赚了么", "未能保存悬浮窗位置。", Forms.ToolTipIcon.Warning); }
    }

    private string Amount(decimal amount) => amount.ToString("N" + (_settings?.DecimalPlaces ?? 4), CultureInfo.GetCultureInfo("zh-CN"));

    private void RefreshDisplay()
    {
        if (_settings == null)
        {
            HeadingText.Text = "今日已赚"; AmountText.Text = "¥ --"; SecondaryText.Visibility = Visibility.Collapsed;
            StatusText.Text = "等待设置"; CalendarText.Text = "打开设置以开始";
            _taskbar.Apply(new BarContent(false, "", "", ""));
            return;
        }
        var now = DateTime.Now;
        if (_lastYear != 0 && _lastYear != now.Year) _ = _holidays.RefreshAsync();
        _lastYear = now.Year;
        var snapshot = EarningsCalculator.Calculate(_settings, now, _holidays.Calendar);
        var needsNotice = _settings.CalculationMode == CalculationMode.Workday && !snapshot.CalendarKnown;
        if (_calendarNeedsNotice != needsNotice)
        {
            _calendarNeedsNotice = needsNotice;
            ApplySurfaces();
        }
        var salary = Amount(snapshot.Salary); var fund = Amount(snapshot.Fund); var combined = Amount(snapshot.Combined);
        string line1, line2;
        switch (_settings.IncomeView)
        {
            case IncomeView.SalaryOnly:
                HeadingText.Text = "今日工资"; AmountText.Text = "¥ " + salary; SecondaryText.Visibility = Visibility.Collapsed;
                line1 = "工资 ¥" + salary; line2 = ""; break;
            case IncomeView.Separate:
                HeadingText.Text = "今日收入"; AmountText.Text = "工资  ¥ " + salary;
                SecondaryText.Text = "公积金  ¥ " + fund; SecondaryText.Visibility = Visibility.Visible;
                line1 = "工资  ¥" + salary; line2 = "公积金  ¥" + fund; break;
            default:
                HeadingText.Text = "今日已赚 · 合计"; AmountText.Text = "¥ " + combined; SecondaryText.Visibility = Visibility.Collapsed;
                line1 = "今日 ¥" + combined; line2 = ""; break;
        }
        StatusText.Text = snapshot.Status + "  ·  每秒更新";
        var calendarNote = snapshot.CalendarKnown ? $"本月 {snapshot.MonthWorkdays} 个工作日" : $"{now.Year} 年节假日未获取，暂按周一至周五估算";
        CalendarText.Text = _settings.CalculationMode == CalculationMode.AllDay ? "全天模式  ·  按当月自然日均分" : calendarNote;
        var description = line1 + (line2.Length > 0 ? "；" + line2 : "") + "；" + snapshot.Status +
            (_settings.CalculationMode == CalculationMode.Workday && !snapshot.CalendarKnown ? "；节假日数据未获取" : "");
        _taskbar.Apply(new BarContent(_settings.ShowTaskbar, line1, line2, description,
            _settings.TaskbarTransparent, _settings.TaskbarClickThrough, _settings.TaskbarPosition));
        _tray.Text = description.Length <= 63 ? description : "赚了么";
    }

    private void TaskbarPresenceChanged(bool ready, string status)
    {
        if (_exiting) return;
        var changed = _taskbarReady != ready || _taskbarStatus != status;
        _taskbarReady = ready; _taskbarStatus = status;
        if (changed && _settingsWindow != null) _settingsWindow.UpdateStatus(_taskbarStatus);
        if (changed) ApplySurfaces();
    }

    private void ApplySurfaces()
    {
        if (_settings == null || _settingsWindow != null) return;
        var show = EarningsCalculator.ShouldShowFloating(_settings, _userHidden, _taskbarReady,
            _holidays.Calendar.HasYear(DateTime.Now.Year));
        if (show && !IsVisible) Show();
        else if (!show && IsVisible) Hide();
    }

    private void ShowFloatingNow() { _userHidden = false; if (!IsVisible) Show(); Activate(); }
    private void ApplyFloatingOptions()
    {
        Topmost = _settings?.AlwaysOnTop ?? true;
        var handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero) return;
        var style = TaskbarNative.GetWindowExStyle(handle);
        var transparent = _settings?.FloatingClickThrough == true;
        TaskbarNative.SetWindowExStyle(handle, transparent ? style | 0x20 : style & ~0x20);
    }
    private void ChangeClickThrough(bool taskbar, bool enabled)
    {
        if (_settings == null) return;
        var previous = taskbar ? _settings.TaskbarClickThrough : _settings.FloatingClickThrough;
        if (taskbar) _settings.TaskbarClickThrough = enabled;
        else _settings.FloatingClickThrough = enabled;
        try { _store.Save(_settings); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            if (taskbar) _settings.TaskbarClickThrough = previous;
            else _settings.FloatingClickThrough = previous;
            _tray.ShowBalloonTip(3000, "赚了么", "未能保存鼠标穿透设置。", Forms.ToolTipIcon.Warning);
        }
        ApplyFloatingOptions();
        RefreshDisplay();
    }
    private void SaveTaskbarPosition(double position)
    {
        if (_settings == null) return;
        var previous = _settings.TaskbarPosition;
        _settings.TaskbarPosition = position;
        try { _store.Save(_settings); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _settings.TaskbarPosition = previous;
            _tray.ShowBalloonTip(3000, "赚了么", "未能保存任务栏位置。", Forms.ToolTipIcon.Warning);
        }
    }
    public void RestoreFromTray()
    {
        if (_exiting) return;
        if (_settings?.ShowFloating == true) ShowFloatingNow();
        else OpenSettings();
    }
    private void OpenSettings()
    {
        if (_settingsWindow != null) { _settingsWindow.Activate(); return; }
        if (!IsVisible) Show();
        try
        {
            var dialog = new SettingsWindow(_settings, _store, _holidays.Calendar, _taskbarStatus) { Owner = this };
            _settingsWindow = dialog;
            if (dialog.ShowDialog() == true)
            {
                _settings = dialog.SavedSettings;
                _userHidden = false;
                ApplyFloatingOptions();
                RefreshDisplay();
            }
        }
        finally { _settingsWindow = null; ApplySurfaces(); }
    }
    private void Settings_Click(object sender, RoutedEventArgs e) => OpenSettings();
    private void Hide_Click(object sender, RoutedEventArgs e) { _userHidden = true; Hide(); }
    private void Exit_Click(object sender, RoutedEventArgs e) => ExitApplication();
    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_exiting) return;
        e.Cancel = true;
        if (!_tray.Visible) { ExitApplication(); return; }
        _userHidden = true; Hide();
    }
    private void ExitApplication()
    {
        if (_exiting) return;
        _exiting = true;
        _timer.Stop(); _holidayTimer.Stop();
        _taskbar.Dispose(); _holidays.Dispose();
        _tray.Visible = false; _tray.Dispose();
        Application.Current.Shutdown();
    }
}
