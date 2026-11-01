using System;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;

namespace ZhuanLeMe;

public sealed class HolidayService : IDisposable
{
    private static readonly HttpClient DefaultClient = new() { Timeout = TimeSpan.FromSeconds(7) };
    private readonly HttpClient _client;
    private readonly string _directory;
    private readonly CancellationTokenSource _stop = new();
    private readonly CancellationToken _token;
    private int _refreshing;
    public HolidayCalendar Calendar { get; } = new();
    public event Action? Changed;

    public HolidayService(string settingsFile, HttpClient? client = null)
    {
        _token = _stop.Token;
        _client = client ?? DefaultClient;
        _directory = Path.Combine(Path.GetDirectoryName(settingsFile)!, "holidays");
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("ZhuanLeMe.Data.2026.json");
        if (stream != null)
        {
            using var reader = new StreamReader(stream);
            var bundled = HolidayYear.Parse(reader.ReadToEnd(), 2026);
            if (bundled != null) Calendar.SetYear(bundled);
        }
        var year = DateTime.Now.Year;
        for (var y = year - 1; y <= year + 1; y++) LoadCache(y);
    }

    private void LoadCache(int year)
    {
        try
        {
            var path = Path.Combine(_directory, year + ".json");
            if (!File.Exists(path) || new FileInfo(path).Length > 200_000) return;
            var data = HolidayYear.Parse(File.ReadAllText(path), year);
            if (data != null) Calendar.SetYear(data);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    public async Task RefreshAsync()
    {
        if (Interlocked.Exchange(ref _refreshing, 1) != 0) return;
        try
        {
            var year = DateTime.Now.Year;
            await Task.WhenAll(RefreshYearAsync(year), RefreshYearAsync(year + 1)).ConfigureAwait(false);
        }
        finally { Interlocked.Exchange(ref _refreshing, 0); }
    }

    private async Task RefreshYearAsync(int year)
    {
        var sources = new[] {
            $"https://raw.githubusercontent.com/NateScarlet/holiday-cn/master/{year}.json",
            $"https://cdn.jsdelivr.net/gh/NateScarlet/holiday-cn@master/{year}.json"
        };
        foreach (var source in sources)
        {
            try
            {
                using var response = await _client.GetAsync(source, HttpCompletionOption.ResponseHeadersRead, _token).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode || response.Content.Headers.ContentLength > 200_000) continue;
                var text = await response.Content.ReadAsStringAsync(_token).ConfigureAwait(false);
                if (text.Length > 200_000) continue;
                var data = HolidayYear.Parse(text, year);
                if (data == null) continue;
                Calendar.SetYear(data);
                try
                {
                    Directory.CreateDirectory(_directory);
                    var target = Path.Combine(_directory, year + ".json");
                    var temporary = Path.Combine(_directory, $"{year}.{Guid.NewGuid():N}.tmp");
                    try { await File.WriteAllTextAsync(temporary, text, _token).ConfigureAwait(false); File.Move(temporary, target, true); }
                    finally { if (File.Exists(temporary)) File.Delete(temporary); }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
                Changed?.Invoke();
                return;
            }
            catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or IOException) { }
        }
    }
    public void Dispose() { _stop.Cancel(); _stop.Dispose(); }
}
