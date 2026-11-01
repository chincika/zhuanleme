using System;
using System.IO;
using System.Text.Json;

namespace ZhuanLeMe;

public sealed class SettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    public string FilePath { get; }

    public SettingsStore(string? directory = null)
    {
        directory ??= Environment.GetEnvironmentVariable("ZHUANLEME_DATA_DIR");
        if (string.IsNullOrWhiteSpace(directory))
            directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ZhuanLeMe");
        FilePath = Path.Combine(directory, "settings.json");
    }

    public AppSettings? Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return null;
            var settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath), JsonOptions);
            return settings != null && SettingsValidator.Error(settings) == null ? settings : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
        {
            return null;
        }
    }

    public void Save(AppSettings settings)
    {
        var error = SettingsValidator.Error(settings);
        if (error != null) throw new ArgumentException(error, nameof(settings));
        var directory = Path.GetDirectoryName(FilePath)!;
        Directory.CreateDirectory(directory);
        var temporary = Path.Combine(directory, $"settings.{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(settings, JsonOptions));
            File.Move(temporary, FilePath, true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }
}
