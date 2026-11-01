using ZhuanLeMe;

var checks = 0;
void Check(bool condition, string message) { checks++; if (!condition) throw new Exception(message); }
void Near(decimal actual, decimal expected, string label) => Check(Math.Abs(actual - expected) < 0.000001m, $"{label}: {actual} != {expected}");
var json = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "2026.json"));
var year = HolidayYear.Parse(json, 2026);
Check(year != null && year.Days.Count == 39, "official 2026 data parses");
Check(HolidayYear.Parse(json, 2027) == null, "reject wrong year");
Check(HolidayYear.Parse("{\"year\":2027,\"papers\":[],\"days\":[]}", 2027) == null, "reject unpublished year");
Check(HolidayYear.Parse("{\"year\":2027,\"papers\":[\"https://evil.example/\"],\"days\":[]}", 2027) == null, "reject untrusted paper");
var calendar = new HolidayCalendar(); calendar.SetYear(year!);
Check(!calendar.GetDay(new DateOnly(2026, 10, 1)).IsWorkday, "National Day is off");
Check(calendar.GetDay(new DateOnly(2026, 10, 10)).IsMakeupWorkday, "Saturday makeup is workday");
Check(!calendar.GetDay(new DateOnly(2026, 10, 31)).IsWorkday, "ordinary weekend is off");
Check(calendar.GetDay(new DateOnly(2026, 10, 12)).IsWorkday, "ordinary weekday is workday");
Check(!calendar.GetDay(new DateOnly(2027, 1, 1)).IsKnown, "missing year marked unknown");
Check(calendar.WorkdaysInMonth(2026, 10) == 18, "October workdays account for holiday and makeup");
var visibility = new AppSettings { ShowTaskbar = true, ShowFloating = false };
Check(EarningsCalculator.ShouldShowFloating(visibility, false, true, false), "unknown workday calendar is visibly warned");
Check(!EarningsCalculator.ShouldShowFloating(visibility, true, true, false), "calendar notice may be dismissed");
visibility.CalculationMode = CalculationMode.AllDay;
Check(!EarningsCalculator.ShouldShowFloating(visibility, false, true, false), "all-day mode does not require calendar");

var s = new AppSettings { MonthlySalary = 9000, MonthlyFund = 1800 };
var makeup = new DateTime(2026, 10, 10);
EarningsSnapshot At(int hour, int minute = 0, int second = 0) => EarningsCalculator.Calculate(s, makeup.AddHours(hour).AddMinutes(minute).AddSeconds(second), calendar);
Near(At(8).Salary, 0, "before work");
Near(At(9).Salary, 0, "start");
Near(At(10).Salary, 62.5m, "salary after one hour");
Near(At(10).Fund, 12.5m, "fund after one hour");
Near(At(12, 30).Salary, 187.5m, "lunch pauses");
Near(At(13).Salary, 187.5m, "lunch ends");
Near(At(18).Salary, 500m, "workday salary");
Near(At(18).Fund, 100m, "workday fund");
Near(At(23).Combined, 600m, "workday cap");
Check(At(10).Status == "调休上班中", "makeup status");
Check(EarningsCalculator.Calculate(s, new DateTime(2026,10,1,15,0,0), calendar).Combined == 0, "holiday not accrued");
Near(At(9, 0, 1).Combined, 600m / (8m * 3600m), "one-second workday increment");
s.WorkdayDivisor = WorkdayDivisor.FixedDays; s.PaidDays = 20;
Near(At(18).Combined, 540m, "optional fixed divisor");

s.MonthlySalary = 9300; s.MonthlyFund = 3100; s.CalculationMode = CalculationMode.AllDay;
var weekend = new DateTime(2026, 10, 31, 12, 0, 0);
var allDay = EarningsCalculator.Calculate(s, weekend, calendar);
Near(allDay.Salary, 150m, "all-day salary at noon");
Near(allDay.Fund, 50m, "all-day fund at noon");
Near(allDay.Combined, 200m, "all-day combined");
Near(EarningsCalculator.Calculate(s, weekend.AddSeconds(1), calendar).Salary - allDay.Salary, 300m / 86400m, "all-day seconds including weekends");
Near(EarningsCalculator.Calculate(s, weekend.Date, calendar).Combined, 0, "midnight reset");
s.MonthlySalary = 2900; s.MonthlyFund = 0;
Near(EarningsCalculator.Calculate(s, new DateTime(2028,2,29,12,0,0), calendar).Salary, 50m, "leap month divisor");

s.ShowTaskbar = false; s.ShowFloating = false;
Check(SettingsValidator.Error(s) != null, "require one display surface");
s.ShowTaskbar = true; s.CalculationMode = CalculationMode.Workday; s.WorkEnd = "08:00";
Check(SettingsValidator.Error(s) != null, "reject overnight shift");
s.WorkEnd = "18:00";
var dir = Path.Combine(Path.GetTempPath(), "zhuanleme-v2-tests-" + Guid.NewGuid().ToString("N"));
try
{
    Directory.CreateDirectory(dir);
    var store = new SettingsStore(dir);
    File.WriteAllText(store.FilePath, "{\"MonthlySalary\":8700,\"MonthlyFund\":1740,\"PaidDays\":21.75,\"WorkStart\":\"09:00\",\"WorkEnd\":\"18:00\"}");
    var migrated = store.Load();
    Check(migrated != null && migrated.MonthlySalary == 8700 && migrated.MonthlyFund == 1740 && migrated.ShowTaskbar && migrated.CalculationMode == CalculationMode.Workday, "v1 settings preserved and migrated");
    Check(migrated!.TaskbarTransparent && !migrated.TaskbarClickThrough && !migrated.FloatingClickThrough && migrated.TaskbarPosition == null, "new overlay defaults preserve interaction");
    migrated.TaskbarPosition = 0.42;
    migrated.TaskbarClickThrough = true;
    migrated.FloatingClickThrough = true;
    store.Save(migrated!);
    var restored = store.Load()!;
    Check(restored.MonthlySalary == 8700 && restored.TaskbarPosition == 0.42 && restored.TaskbarClickThrough && restored.FloatingClickThrough, "overlay options and income survive round-trip");
    restored.TaskbarPosition = -0.01;
    Check(SettingsValidator.Error(restored) != null, "reject taskbar position outside bounds");
    restored.TaskbarPosition = double.NaN;
    Check(SettingsValidator.Error(restored) != null, "reject non-finite taskbar position");
    File.WriteAllText(store.FilePath, "{broken");
    Check(store.Load() == null, "corrupt config safely rejected");
}
finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
var dataDir = Path.Combine(Path.GetTempPath(), "zhuanleme-calendar-" + Guid.NewGuid().ToString("N"));
try
{
    var currentYear = DateTime.Now.Year;
    var nextYear = currentYear + 1;
    string ForYear(int y) => json.Replace("\"year\": 2026", $"\"year\": {y}").Replace("2026-", $"{y}-");
    var holidayDir = Path.Combine(dataDir, "holidays");
    Directory.CreateDirectory(holidayDir);
    var nextPath = Path.Combine(holidayDir, nextYear + ".json");
    var cachedNext = ForYear(nextYear);
    File.WriteAllText(nextPath, cachedNext);
    using var fakeClient = new HttpClient(new HolidayHandler(currentYear, ForYear(currentYear)));
    using var service = new HolidayService(Path.Combine(dataDir, "settings.json"), fakeClient);
    Check(service.Calendar.HasYear(nextYear), "load valid cached future year");
    await service.RefreshAsync();
    Check(service.Calendar.HasYear(currentYear), "automatic update loads current year");
    Check(File.ReadAllText(nextPath) == cachedNext && service.Calendar.HasYear(nextYear), "unpublished placeholder never overwrites valid cache");
}
finally { if (Directory.Exists(dataDir)) Directory.Delete(dataDir, true); }
Console.WriteLine($"PASS: {checks} calendar, earnings, precision, migration and storage checks");

sealed class HolidayHandler(int year, string validJson) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var isCurrent = request.RequestUri!.AbsolutePath.EndsWith($"/{year}.json", StringComparison.Ordinal);
        var body = isCurrent ? validJson : $"{{\"year\":{year + 1},\"papers\":[],\"days\":[]}}";
        return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StringContent(body) });
    }
}
