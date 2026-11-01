using System;
using System.Globalization;
using System.Text.RegularExpressions;

namespace ZhuanLeMe;

public enum CalculationMode { Workday, AllDay }
public enum IncomeView { SalaryOnly, Separate, Combined }
public enum WorkdayDivisor { ActualMonth, FixedDays }

public sealed class AppSettings
{
    public decimal MonthlySalary { get; set; }
    public decimal MonthlyFund { get; set; }
    public decimal PaidDays { get; set; } = 21.75m;
    public string WorkStart { get; set; } = "09:00";
    public string WorkEnd { get; set; } = "18:00";
    public bool LunchEnabled { get; set; } = true;
    public string LunchStart { get; set; } = "12:00";
    public string LunchEnd { get; set; } = "13:00";
    public bool[] WorkDays { get; set; } = new[] { true, true, true, true, true, false, false };
    public bool AlwaysOnTop { get; set; } = true;
    public double? WindowX { get; set; }
    public double? WindowY { get; set; }
    public CalculationMode CalculationMode { get; set; } = CalculationMode.Workday;
    public IncomeView IncomeView { get; set; } = IncomeView.Combined;
    public WorkdayDivisor WorkdayDivisor { get; set; } = WorkdayDivisor.ActualMonth;
    public bool ShowTaskbar { get; set; } = true;
    public bool ShowFloating { get; set; } = false;
    public bool TaskbarTransparent { get; set; } = true;
    public bool TaskbarClickThrough { get; set; }
    public bool FloatingClickThrough { get; set; }
    // Fraction of the movable taskbar width, so the position survives DPI and resolution changes.
    public double? TaskbarPosition { get; set; }
    public int DecimalPlaces { get; set; } = 4;
}

public static class SettingsValidator
{
    public static bool TryTime(string? text, out TimeSpan result)
    {
        result = default;
        var match = Regex.Match(text ?? "", @"^([01]?\d|2[0-3]):([0-5]\d)$");
        if (!match.Success) return false;
        result = new TimeSpan(int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture),
            int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture), 0);
        return true;
    }

    public static string? Error(AppSettings s)
    {
        if (s.MonthlySalary < 0 || s.MonthlySalary > 1_000_000_000m ||
            s.MonthlyFund < 0 || s.MonthlyFund > 1_000_000_000m)
            return "工资与公积金必须是 0 至 1,000,000,000 之间的金额。";
        if (s.PaidDays < 0.1m || s.PaidDays > 31)
            return "固定计薪天数必须在 0.1 至 31 天之间。";
        if (!Enum.IsDefined(s.CalculationMode) || !Enum.IsDefined(s.IncomeView) || !Enum.IsDefined(s.WorkdayDivisor))
            return "计算或显示方式无效。";
        if (s.DecimalPlaces is < 2 or > 4) return "显示精度须为 2 至 4 位小数。";
        if (s.TaskbarPosition is double position && (!double.IsFinite(position) || position < 0 || position > 1))
            return "任务栏位置无效。";
        if (!s.ShowTaskbar && !s.ShowFloating) return "请至少启用任务栏或悬浮窗中的一种显示方式。";
        if (s.CalculationMode == CalculationMode.AllDay) return null;
        if (!TryTime(s.WorkStart, out var start) || !TryTime(s.WorkEnd, out var end))
            return "工作时间格式应为 09:00，且必须在 00:00 至 23:59 之间。";
        if (end <= start) return "工作结束时间必须晚于开始时间；暂不支持跨夜排班。";
        if (s.LunchEnabled)
        {
            if (!TryTime(s.LunchStart, out var lunchStart) || !TryTime(s.LunchEnd, out var lunchEnd))
                return "午休时间格式应为 12:00，且必须在 00:00 至 23:59 之间。";
            if (lunchStart < start || lunchEnd > end || lunchEnd <= lunchStart)
                return "午休必须在工作时段内，且结束时间要晚于开始时间。";
            if (lunchEnd - lunchStart >= end - start) return "午休不能占满整个工作时段。";
        }
        return null;
    }
}

public sealed record EarningsSnapshot(decimal Salary, decimal Fund, decimal DailySalary, decimal DailyFund,
    decimal Progress, string Status, bool IsWorkingDay, bool CalendarKnown, int MonthWorkdays)
{
    public decimal Combined => Salary + Fund;
}

public static class EarningsCalculator
{
    public static bool ShouldShowFloating(AppSettings settings, bool userHidden, bool taskbarReady, bool calendarKnown)
    {
        var needsCalendarNotice = settings.CalculationMode == CalculationMode.Workday && !calendarKnown && !userHidden;
        return (settings.ShowFloating && !userHidden) || (settings.ShowTaskbar && !taskbarReady) || needsCalendarNotice;
    }

    public static EarningsSnapshot Calculate(AppSettings s, DateTime localNow, HolidayCalendar calendar)
    {
        var error = SettingsValidator.Error(s);
        if (error != null) throw new ArgumentException(error, nameof(s));
        var date = DateOnly.FromDateTime(localNow);
        var day = calendar.GetDay(date);
        var workdays = calendar.WorkdaysInMonth(date.Year, date.Month);
        if (s.CalculationMode == CalculationMode.AllDay)
        {
            var days = DateTime.DaysInMonth(date.Year, date.Month);
            var progress = (decimal)localNow.TimeOfDay.Ticks / TimeSpan.TicksPerDay;
            var salary = s.MonthlySalary / days;
            var fund = s.MonthlyFund / days;
            return new(salary * progress, fund * progress, salary, fund, progress, "全天计算中", day.IsWorkday, day.IsKnown, workdays);
        }
        var divisor = s.WorkdayDivisor == WorkdayDivisor.FixedDays ? s.PaidDays : workdays;
        if (divisor <= 0) throw new InvalidOperationException("本月没有可计算的工作日。");
        var dailySalary = s.MonthlySalary / divisor;
        var dailyFund = s.MonthlyFund / divisor;
        if (!day.IsWorkday)
            return new(0, 0, dailySalary, dailyFund, 0, day.Reason, false, day.IsKnown, workdays);

        SettingsValidator.TryTime(s.WorkStart, out var start);
        SettingsValidator.TryTime(s.WorkEnd, out var end);
        var lunchStart = TimeSpan.Zero; var lunchEnd = TimeSpan.Zero;
        if (s.LunchEnabled)
        {
            SettingsValidator.TryTime(s.LunchStart, out lunchStart);
            SettingsValidator.TryTime(s.LunchEnd, out lunchEnd);
        }
        var workingTicks = (end - start).Ticks - (s.LunchEnabled ? (lunchEnd - lunchStart).Ticks : 0);
        var now = localNow.TimeOfDay;
        var elapsedTicks = Math.Clamp((now - start).Ticks, 0, (end - start).Ticks);
        if (s.LunchEnabled) elapsedTicks -= Math.Clamp((now - lunchStart).Ticks, 0, (lunchEnd - lunchStart).Ticks);
        var fraction = (decimal)elapsedTicks / workingTicks;
        var status = now < start ? "尚未上班" : now >= end ? "今日已下班" :
            s.LunchEnabled && now >= lunchStart && now < lunchEnd ? "午休中" :
            day.IsMakeupWorkday ? "调休上班中" : "工作中";
        return new(dailySalary * fraction, dailyFund * fraction, dailySalary, dailyFund,
            fraction, status, true, day.IsKnown, workdays);
    }
}
