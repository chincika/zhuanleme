using System;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;

namespace ZhuanLeMe;

public partial class SettingsWindow : Window
{
    private readonly SettingsStore _store;
    private readonly AppSettings? _original;
    public AppSettings? SavedSettings { get; private set; }

    public SettingsWindow(AppSettings? settings, SettingsStore store, HolidayCalendar calendar, string taskbarStatus)
    {
        InitializeComponent();
        MaxHeight = Math.Max(480, SystemParameters.WorkArea.Height - 36);
        Height = Math.Min(Height, MaxHeight);
        _store = store; _original = settings;
        var value = settings ?? new AppSettings();
        SalaryBox.Text = settings == null ? "" : value.MonthlySalary.ToString(CultureInfo.CurrentCulture);
        FundBox.Text = settings == null ? "" : value.MonthlyFund.ToString(CultureInfo.CurrentCulture);
        DaysBox.Text = value.PaidDays.ToString(CultureInfo.CurrentCulture);
        StartBox.Text = value.WorkStart; EndBox.Text = value.WorkEnd;
        LunchStartBox.Text = value.LunchStart; LunchEndBox.Text = value.LunchEnd;
        LunchCheck.IsChecked = value.LunchEnabled;
        IncomeCombo.SelectedIndex = (int)value.IncomeView;
        ModeCombo.SelectedIndex = (int)value.CalculationMode;
        DivisorCombo.SelectedIndex = (int)value.WorkdayDivisor;
        TaskbarCheck.IsChecked = value.ShowTaskbar; FloatingCheck.IsChecked = value.ShowFloating;
        TopmostCheck.IsChecked = value.AlwaysOnTop; PrecisionCombo.SelectedIndex = value.DecimalPlaces - 2;
        TaskbarTransparentCheck.IsChecked = value.TaskbarTransparent;
        TaskbarClickThroughCheck.IsChecked = value.TaskbarClickThrough;
        FloatingClickThroughCheck.IsChecked = value.FloatingClickThrough;
        UpdateCalendar(calendar);
        UpdateStatus(taskbarStatus);
        using (var noticeStream = Assembly.GetExecutingAssembly().GetManifestResourceStream("ZhuanLeMe.THIRD-PARTY-NOTICES.txt"))
        {
            if (noticeStream != null) using (var reader = new StreamReader(noticeStream)) NoticesText.Text = reader.ReadToEnd();
        }
        UpdateEnabled();
        Loaded += (_, _) => SalaryBox.Focus();
    }

    public void UpdateStatus(string status) => TaskbarStatusText.Text = status;
    public void UpdateCalendar(HolidayCalendar calendar)
    {
        var year = DateTime.Now.Year;
        CalendarStatusText.Text = calendar.HasYear(year) ? $"已加载 {year} 年中国大陆节假日和调休安排。" :
            $"尚未获取 {year} 年节假日安排，暂按周一至周五估算。";
    }
    private void Mode_Changed(object sender, SelectionChangedEventArgs e) => UpdateEnabled();
    private void Divisor_Changed(object sender, SelectionChangedEventArgs e) => UpdateEnabled();
    private void Lunch_Changed(object sender, RoutedEventArgs e) => UpdateEnabled();
    private void UpdateEnabled()
    {
        if (ModeCombo == null || WorkdayPanel == null || LunchCheck == null || LunchStartBox == null ||
            LunchEndBox == null || DaysBox == null || DivisorCombo == null || ModeDescription == null) return;
        var workday = ModeCombo.SelectedIndex == 0;
        WorkdayPanel.IsEnabled = workday;
        LunchStartBox.IsEnabled = workday && LunchCheck.IsChecked == true;
        LunchEndBox.IsEnabled = workday && LunchCheck.IsChecked == true;
        DivisorCombo.IsEnabled = workday;
        DaysBox.IsEnabled = workday && DivisorCombo.SelectedIndex == 1;
        ModeDescription.Text = workday
            ? "自动识别工作日、节假日和调休，只在设定的有效工作时间内累加。"
            : "每月收入按当月自然日均分，每天从 00:00 开始逐秒累加，休息日也计算。";
    }
    private static bool TryNumber(TextBox box, out decimal value) =>
        decimal.TryParse(box.Text.Trim(), NumberStyles.Number, CultureInfo.CurrentCulture, out value);

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        ErrorText.Text = "";
        if (!TryNumber(SalaryBox, out var salary)) { Fail("请填写每月到手工资。", SalaryBox); return; }
        if (!TryNumber(FundBox, out var fund)) { Fail("请填写公积金金额，没有可填 0。", FundBox); return; }
        if (!TryNumber(DaysBox, out var paidDays) || paidDays < 0.1m || paidDays > 31)
        {
            if (ModeCombo.SelectedIndex == 0 && DivisorCombo.SelectedIndex == 1)
            { Fail("固定计薪天数必须在 0.1 至 31 天之间。", DaysBox); return; }
            paidDays = _original?.PaidDays ?? 21.75m;
        }
        var s = new AppSettings
        {
            MonthlySalary = salary, MonthlyFund = fund, PaidDays = paidDays,
            WorkStart = StartBox.Text.Trim(), WorkEnd = EndBox.Text.Trim(),
            LunchEnabled = LunchCheck.IsChecked == true, LunchStart = LunchStartBox.Text.Trim(), LunchEnd = LunchEndBox.Text.Trim(),
            WorkDays = _original?.WorkDays ?? new[] { true, true, true, true, true, false, false },
            AlwaysOnTop = TopmostCheck.IsChecked == true, WindowX = _original?.WindowX, WindowY = _original?.WindowY,
            CalculationMode = (CalculationMode)ModeCombo.SelectedIndex, IncomeView = (IncomeView)IncomeCombo.SelectedIndex,
            WorkdayDivisor = (WorkdayDivisor)DivisorCombo.SelectedIndex,
            ShowTaskbar = TaskbarCheck.IsChecked == true, ShowFloating = FloatingCheck.IsChecked == true,
            TaskbarTransparent = TaskbarTransparentCheck.IsChecked == true,
            TaskbarClickThrough = TaskbarClickThroughCheck.IsChecked == true,
            FloatingClickThrough = FloatingClickThroughCheck.IsChecked == true,
            TaskbarPosition = _original?.TaskbarPosition,
            DecimalPlaces = PrecisionCombo.SelectedIndex + 2
        };
        var error = SettingsValidator.Error(s);
        if (error != null) { ErrorText.Text = error; return; }
        try { _store.Save(s); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { ErrorText.Text = "保存失败：" + ex.Message; return; }
        SavedSettings = s;
        DialogResult = true;
    }
    private void Fail(string message, TextBox box)
    {
        ErrorText.Text = message;
        if (box == SalaryBox || box == FundBox) SettingsTabs.SelectedIndex = 0;
        else SettingsTabs.SelectedIndex = 1;
        box.Focus(); box.SelectAll();
    }
}
