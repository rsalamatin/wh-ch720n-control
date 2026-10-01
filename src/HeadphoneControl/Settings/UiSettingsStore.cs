using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace HeadphoneControl.Settings;

// A missing or unreadable file falls back to the defaults: losing a UI preference must never stop the app.
internal sealed class UiSettingsStore
{
    private readonly string _path;
    private readonly ILogger _logger;

    public UiSettingsStore(string path, ILogger<UiSettingsStore> logger)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(logger);
        _path = path;
        _logger = logger;
    }

    public static string DefaultPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "HeadphoneControl", "settings.json");

    public UiSettings Load()
    {
        try
        {
            if (!File.Exists(_path))
            {
                return UiSettings.Default;
            }

            using var stream = File.OpenRead(_path);
            return JsonSerializer.Deserialize(stream, UiSettingsJsonContext.Default.UiSettings) ?? UiSettings.Default;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            _logger.LogWarning(ex, "Couldn't read {Path}; using default settings", _path);
            return UiSettings.Default;
        }
    }

    public void Save(UiSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            using var stream = File.Create(_path);
            JsonSerializer.Serialize(stream, settings, UiSettingsJsonContext.Default.UiSettings);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Couldn't save settings to {Path}", _path);
        }
    }
}
