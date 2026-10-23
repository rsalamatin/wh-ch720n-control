using HeadphoneControl.Settings;

namespace HeadphoneControl.ViewModels;

// The settings dialog. Every choice takes effect and is saved as soon as it changes; there is no OK or Cancel.
internal sealed class SettingsViewModel : ViewModelBase
{
    private readonly Action<ThemePreference> _selectTheme;
    private Choice<ThemePreference> _selectedTheme;

    public SettingsViewModel(
        ThemePreference theme,
        // Applies and saves the theme.
        Action<ThemePreference> selectTheme,
        FirmwareUpdateViewModel firmware,
        AutoConnectViewModel autoConnect)
    {
        ArgumentNullException.ThrowIfNull(selectTheme);
        ArgumentNullException.ThrowIfNull(firmware);
        ArgumentNullException.ThrowIfNull(autoConnect);

        _selectTheme = selectTheme;
        Themes = DisplayNames.ChoicesOf<ThemePreference>(DisplayNames.Of);
        _selectedTheme = Themes.First(t => t.Value == theme);
        Firmware = firmware;
        AutoConnect = autoConnect;
    }

    public IReadOnlyList<Choice<ThemePreference>> Themes { get; }

    public FirmwareUpdateViewModel Firmware { get; }

    public AutoConnectViewModel AutoConnect { get; }

    // A combo box can clear its selection; the app always has a theme, so null is ignored.
    public Choice<ThemePreference>? SelectedTheme
    {
        get => _selectedTheme;
        set
        {
            if (value is null || value == _selectedTheme)
            {
                return;
            }

            _selectedTheme = value;
            OnPropertyChanged();
            _selectTheme(value.Value);
        }
    }
}
