using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;

namespace ZhuanLeMe;

public sealed record HolidayEntry(DateOnly Date, string Name, bool IsOffDay);
public sealed record DayInfo(bool IsWorkday, bool IsKnown, bool IsMakeupWorkday, string Reason);

public sealed class HolidayYear
{
    public int Year { get; }
    public IReadOnlyList<HolidayEntry> Days { get; }
    private HolidayYear(int year, IReadOnlyList<HolidayEntry> days) { Year = year; Days = days; }

    public static HolidayYear? Parse(string json, int expectedYear)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.GetProperty("year").GetInt32() != expectedYear) return null;
            var paperUrls = root.GetProperty("papers").EnumerateArray().Select(p => p.GetString()).ToArray();
            if (!paperUrls.Any(url => Uri.TryCreate(url, UriKind.Absolute, out var uri) &&
                uri.Scheme == Uri.UriSchemeHttps && (uri.Host == "gov.cn" || uri.Host.EndsWith(".gov.cn", StringComparison.OrdinalIgnoreCase)))) return null;
            var list = new List<HolidayEntry>();
            var dates = new HashSet<DateOnly>();
            foreach (var item in root.GetProperty("days").EnumerateArray())
            {
                var name = item.GetProperty("name").GetString();
                var text = item.GetProperty("date").GetString();
                if (string.IsNullOrWhiteSpace(name) || name.Length > 80 ||
                    !DateOnly.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date) ||
                    Math.Abs(date.Year - expectedYear) > 1 || !dates.Add(date)) return null;
                var off = item.GetProperty("isOffDay").GetBoolean();
                list.Add(new HolidayEntry(date, name, off));
            }
            var required = new[] { "元旦", "春节", "清明", "劳动", "端午", "中秋", "国庆" };
            if (list.Count < 7 || list.Count > 150 || list.Count(x => x.IsOffDay) < 7 ||
                required.Any(name => !list.Any(d => d.IsOffDay && d.Name.Contains(name, StringComparison.Ordinal)))) return null;
            return new HolidayYear(expectedYear, list);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or KeyNotFoundException or FormatException)
        {
            return null;
        }
    }
}

public sealed class HolidayCalendar
{
    private readonly Dictionary<int, HolidayYear> _years = new();
    private readonly Dictionary<DateOnly, HolidayEntry> _overrides = new();
    private readonly object _lock = new();
    public bool HasYear(int year) { lock (_lock) return _years.ContainsKey(year); }
    public void SetYear(HolidayYear year)
    {
        lock (_lock)
        {
            _years[year.Year] = year;
            _overrides.Clear();
            foreach (var pair in _years.OrderBy(x => x.Key))
                foreach (var day in pair.Value.Days) _overrides[day.Date] = day;
        }
    }
    public DayInfo GetDay(DateOnly date)
    {
        lock (_lock)
        {
            var known = _years.ContainsKey(date.Year);
            var weekend = date.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday;
            if (_overrides.TryGetValue(date, out var item))
                return new(!item.IsOffDay, known, !item.IsOffDay && weekend,
                    item.IsOffDay ? item.Name + " · 休息" : item.Name + " · 调休上班");
            return new(!weekend, known, false, weekend ? "休息日" : "工作日");
        }
    }
    public int WorkdaysInMonth(int year, int month)
    {
        lock (_lock)
        {
            var count = 0;
            for (var d = 1; d <= DateTime.DaysInMonth(year, month); d++)
                if (GetDay(new DateOnly(year, month, d)).IsWorkday) count++;
            return count;
        }
    }
}
