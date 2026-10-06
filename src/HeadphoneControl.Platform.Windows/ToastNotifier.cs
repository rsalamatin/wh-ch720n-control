using Microsoft.Extensions.Logging;
using Microsoft.Win32;
using Windows.UI.Notifications;

namespace HeadphoneControl.Platform.Windows;

/// <summary>Shows Windows toast notifications for an app that has no package identity.</summary>
public sealed class ToastNotifier
{
    private const string AppIdRegistryRoot = @"Software\Classes\AppUserModelId";

    private readonly string _appId;
    private readonly string _displayName;
    private readonly string? _iconPath;
    private readonly ILogger<ToastNotifier> _logger;
    private bool _registered;

    /// <param name="appId">The AppUserModelID the toasts are filed under in the notification centre.</param>
    /// <param name="iconPath">An image file shown beside <paramref name="displayName"/>; null for the default icon.</param>
    public ToastNotifier(string appId, string displayName, string? iconPath, ILogger<ToastNotifier> logger)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(appId);
        ArgumentException.ThrowIfNullOrWhiteSpace(displayName);
        ArgumentNullException.ThrowIfNull(logger);
        _appId = appId;
        _displayName = displayName;
        _iconPath = iconPath;
        _logger = logger;
    }

    /// <summary>Shows a toast. A failure is logged, not thrown: Windows may have notifications turned off.</summary>
    public void Show(string title, string message)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        ArgumentNullException.ThrowIfNull(message);
        try
        {
            RegisterAppId();
            var content = ToastNotificationManager.GetTemplateContent(ToastTemplateType.ToastText02);
            var lines = content.GetElementsByTagName("text");
            lines[0].AppendChild(content.CreateTextNode(title));
            lines[1].AppendChild(content.CreateTextNode(message));
            ToastNotificationManager.CreateToastNotifier(_appId).Show(new ToastNotification(content));
        }
        catch (Exception ex)
        {
            // WinRT reports failures as COMException and other types by HRESULT; a notification is never worth
            // failing the caller for.
            _logger.LogWarning(ex, "Showing the notification '{Title}' failed", title);
        }
    }

    // Without a package or a Start menu shortcut, Windows only shows toasts of an AppUserModelID it finds here.
    private void RegisterAppId()
    {
        if (_registered)
        {
            return;
        }

        using var key = Registry.CurrentUser.CreateSubKey($@"{AppIdRegistryRoot}\{_appId}");
        key.SetValue("DisplayName", _displayName);
        if (_iconPath is not null)
        {
            key.SetValue("IconUri", _iconPath);
        }

        _registered = true;
    }
}
