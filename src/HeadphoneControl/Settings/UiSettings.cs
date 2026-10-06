using System.Text.Json.Serialization;

namespace HeadphoneControl.Settings;

internal enum ThemePreference
{
    System,
    Light,
    Dark,
}

// What the last firmware check found, so the app neither asks more than once a day nor announces a version twice.
internal sealed record FirmwareCheckSettings(
    bool Automatic = true,
    DateTimeOffset? LastChecked = null,
    string? LatestVersion = null,
    string? InfoUrl = null,
    string? NotifiedVersion = null);

// FirmwareCheck is null in files written before the firmware check existed.
internal sealed record UiSettings(ThemePreference Theme = ThemePreference.System, FirmwareCheckSettings? FirmwareCheck = null)
{
    public static UiSettings Default { get; } = new();
}

[JsonSourceGenerationOptions(WriteIndented = true, UseStringEnumConverter = true)]
[JsonSerializable(typeof(UiSettings))]
internal sealed partial class UiSettingsJsonContext : JsonSerializerContext;
