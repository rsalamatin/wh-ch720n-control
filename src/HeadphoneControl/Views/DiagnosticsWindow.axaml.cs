using System.Runtime.InteropServices;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using HeadphoneControl.Diagnostics;
using HeadphoneControl.Resources;
using HeadphoneControl.ViewModels;

namespace HeadphoneControl.Views;

public partial class DiagnosticsWindow : Window
{
    public DiagnosticsWindow()
    {
        InitializeComponent();
    }

    // async void handlers would crash the app on an escaping exception, so expected failures (clipboard held by
    // another process, shell refusing the folder) are shown in the window instead.
    private async void OnCopyAllClick(object? sender, RoutedEventArgs e)
    {
        if (Clipboard is not { } clipboard || DataContext is not DiagnosticsViewModel viewModel)
        {
            return;
        }

        try
        {
            await clipboard.SetTextAsync(viewModel.Text);
            ShowActionError(null);
        }
        catch (ExternalException ex)
        {
            ShowActionError(Strings.Format("Diagnostics_CopyFailedFormat", ex.Message));
        }
    }

    private async void OnOpenLogFolderClick(object? sender, RoutedEventArgs e)
    {
        var folder = Path.GetDirectoryName(JournalLoggerProvider.DefaultLogFilePath) ?? AppContext.BaseDirectory;
        bool opened;
        try
        {
            opened = await Launcher.LaunchDirectoryInfoAsync(new DirectoryInfo(folder));
        }
        catch (Exception ex) when (ex is ExternalException or IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            opened = false;
        }

        ShowActionError(opened ? null : Strings.Format("Diagnostics_OpenFolderFailedFormat", folder));
    }

    private void ShowActionError(string? message)
    {
        ActionError.Text = message;
        ActionError.IsVisible = message is not null;
        Hint.IsVisible = message is null;
    }
}
