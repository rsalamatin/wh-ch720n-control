using System.Text.Json.Serialization;

namespace HeadphoneControl.Settings;

internal enum ThemePreference
{
    System,
    Light,
    Dark,
}

internal sealed record UiSettings(ThemePreference Theme = ThemePreference.System)
{
    public static UiSettings Default { get; } = new();
}

[JsonSourceGenerationOptions(WriteIndented = true, UseStringEnumConverter = true)]
[JsonSerializable(typeof(UiSettings))]
internal sealed partial class UiSettingsJsonContext : JsonSerializerContext;
