using System.Runtime.InteropServices;
using Avalonia.Controls;
using Avalonia.Interactivity;
using HeadphoneControl.ViewModels;

namespace HeadphoneControl.Views;

public partial class SettingsWindow : Window
{
    public SettingsWindow()
    {
        InitializeComponent();
    }

    private void OnCloseClick(object? sender, RoutedEventArgs e) => Close();

    private async void OnFirmwarePageClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not SettingsViewModel { Firmware.InfoUrl: { } url })
        {
            return;
        }

        try
        {
            await Launcher.LaunchUriAsync(url);
        }
        catch (Exception ex) when (ex is ExternalException or IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            // An escaping exception in an async void handler would crash the app. The user sees that no browser
            // opened, and the button is still there to try again.
        }
    }
}
