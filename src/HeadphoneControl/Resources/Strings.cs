using System.Globalization;
using System.Resources;

namespace HeadphoneControl.Resources;

// Hand-written because the project has no resx code generator configured; property names match the resource keys.
public static class Strings
{
    private static readonly ResourceManager Manager = new("HeadphoneControl.Resources.Strings", typeof(Strings).Assembly);

    public static string Get(string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        return Manager.GetString(key, CultureInfo.CurrentUICulture)
            ?? throw new MissingManifestResourceException($"Missing string resource '{key}'.");
    }

    public static string Format(string key, params object?[] args) =>
        string.Format(CultureInfo.CurrentCulture, Get(key), args);

    public static string AppTitle => Get(nameof(AppTitle));
    public static string Disconnect => Get(nameof(Disconnect));
    public static string Cancel => Get(nameof(Cancel));
    public static string Clear => Get(nameof(Clear));
    public static string Unknown => Get(nameof(Unknown));
    public static string UnknownValue => Get(nameof(UnknownValue));

    public static string Label_Firmware => Get(nameof(Label_Firmware));
    public static string Label_Codec => Get(nameof(Label_Codec));
    public static string Label_Charging => Get(nameof(Label_Charging));

    public static string Section_NoiseControl => Get(nameof(Section_NoiseControl));
    public static string Section_Equalizer => Get(nameof(Section_Equalizer));
    public static string Section_Sound => Get(nameof(Section_Sound));

    public static string Label_FocusOnVoice => Get(nameof(Label_FocusOnVoice));
    public static string Label_AmbientLevel => Get(nameof(Label_AmbientLevel));
    public static string Label_Preset => Get(nameof(Label_Preset));
    public static string Label_Dsee => Get(nameof(Label_Dsee));
    public static string Label_DseeCaption => Get(nameof(Label_DseeCaption));

    public static string MoreOptions => Get(nameof(MoreOptions));
    public static string Menu_RefreshNow => Get(nameof(Menu_RefreshNow));
    public static string Menu_CancelOperation => Get(nameof(Menu_CancelOperation));
    public static string Menu_Diagnostics => Get(nameof(Menu_Diagnostics));
    public static string Menu_Settings => Get(nameof(Menu_Settings));
    public static string SettingsTitle => Get(nameof(SettingsTitle));
    public static string Settings_Appearance => Get(nameof(Settings_Appearance));
    public static string Settings_Theme => Get(nameof(Settings_Theme));
    public static string Settings_Connection => Get(nameof(Settings_Connection));
    public static string Settings_AutoConnect => Get(nameof(Settings_AutoConnect));
    public static string Settings_AutoConnectCaption => Get(nameof(Settings_AutoConnectCaption));
    public static string Settings_FirmwareUpdates => Get(nameof(Settings_FirmwareUpdates));
    public static string Settings_FirmwareAutomatic => Get(nameof(Settings_FirmwareAutomatic));
    public static string Settings_FirmwareAutomaticCaption => Get(nameof(Settings_FirmwareAutomaticCaption));
    public static string Settings_FirmwareCheckNow => Get(nameof(Settings_FirmwareCheckNow));
    public static string Settings_FirmwareOpenPage => Get(nameof(Settings_FirmwareOpenPage));
    public static string Settings_FirmwareNotConnected => Get(nameof(Settings_FirmwareNotConnected));
    public static string Settings_About => Get(nameof(Settings_About));
    public static string Close => Get(nameof(Close));
    public static string Tray_Open => Get(nameof(Tray_Open));
    public static string Tray_Exit => Get(nameof(Tray_Exit));
    public static string Dismiss => Get(nameof(Dismiss));
    public static string ResetEqualizer => Get(nameof(ResetEqualizer));
    public static string CopyAll => Get(nameof(CopyAll));
    public static string OpenLogFolder => Get(nameof(OpenLogFolder));
    public static string DiagnosticsTitle => Get(nameof(DiagnosticsTitle));
    public static string DiagnosticsHint => Get(nameof(DiagnosticsHint));
}
