using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HeadphoneControl.FirmwareUpdates;
using HeadphoneControl.Protocol.Devices;
using HeadphoneControl.Resources;
using HeadphoneControl.Settings;
using Microsoft.Extensions.Logging;

namespace HeadphoneControl.ViewModels;

public sealed record FirmwareUpdateNotice(string InstalledVersion, string LatestVersion);

// Compares the firmware the headset reports with the newest known release. The release is looked up at most once a
// day while a headset is connected and remembered in the settings, so the answer is also there offline and after a
// restart. Only tells: installing firmware stays with the manufacturer's app.
public sealed partial class FirmwareUpdateViewModel : ViewModelBase, IDisposable
{
    private static readonly TimeSpan CheckInterval = TimeSpan.FromDays(1);

    // Sooner than the daily check, but bounded: every device state change asks whether a check is due.
    private static readonly TimeSpan RetryInterval = TimeSpan.FromHours(1);

    private readonly IHeadphoneDevice _device;
    private readonly Func<CancellationToken, Task<FirmwareRelease?>> _getLatest;
    private readonly Action<FirmwareCheckSettings> _save;
    private readonly ILogger _logger;
    private readonly Action<Action> _dispatch;
    private readonly TimeProvider _timeProvider;
    private readonly CancellationTokenSource _lifetime = new();

    private FirmwareCheckSettings _settings;
    private DateTimeOffset _nextAutomaticCheck;
    private string? _installedVersion;
    private bool _lastCheckFailed;
    private bool _isChecking;

    internal FirmwareUpdateViewModel(
        IHeadphoneDevice device,
        // Fails only with FirmwareCheckException or OperationCanceledException; null when the model is not listed.
        Func<CancellationToken, Task<FirmwareRelease?>> getLatest,
        FirmwareCheckSettings settings,
        Action<FirmwareCheckSettings> save,
        ILogger<FirmwareUpdateViewModel> logger,
        // Runs an action on the UI thread; device events can arrive on any thread.
        Action<Action> dispatch,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(device);
        ArgumentNullException.ThrowIfNull(getLatest);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(save);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(dispatch);

        _device = device;
        _getLatest = getLatest;
        _settings = settings;
        _save = save;
        _logger = logger;
        _dispatch = dispatch;
        _timeProvider = timeProvider ?? TimeProvider.System;

        // A time in the future (clock set back, edited file) counts as never checked; it must not postpone the check,
        // and adding the interval to a date near MaxValue would throw.
        _nextAutomaticCheck = settings.LastChecked is { } last && last <= _timeProvider.GetUtcNow()
            ? last + CheckInterval
            : DateTimeOffset.MinValue;

        ApplyState(device.State);
        _device.StateChanged += OnDeviceStateChanged;
    }

    // Raised once per new version, also across restarts.
    public event EventHandler<FirmwareUpdateNotice>? UpdateFound;

    [ObservableProperty]
    public partial string StatusText { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial bool IsUpdateAvailable { get; private set; }

    // Only set while an update is available.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasInfoPage))]
    public partial Uri? InfoUrl { get; private set; }

    public bool HasInfoPage => InfoUrl is not null;

    // Governs only the automatic check; Check now works either way.
    public bool IsAutomatic
    {
        get => _settings.Automatic;
        set
        {
            if (value == _settings.Automatic)
            {
                return;
            }

            Store(_settings with { Automatic = value });
            OnPropertyChanged();
            CheckIfDue();
        }
    }

    public void Dispose()
    {
        _device.StateChanged -= OnDeviceStateChanged;

        // Not disposed: a state change already queued on the dispatcher may still read its token.
        _lifetime.Cancel();
    }

    // The event argument may be older than State by the time the dispatcher runs; always use the latest snapshot.
    private void OnDeviceStateChanged(object? sender, DeviceState state) => _dispatch(() => ApplyState(_device.State));

    private void ApplyState(DeviceState state)
    {
        _installedVersion = state.Connection == ConnectionStatus.Connected ? state.FirmwareVersion : null;
        UpdateStatus();
        CheckIfDue();
    }

    private void CheckIfDue()
    {
        if (_installedVersion is null || !IsAutomatic || _isChecking || _timeProvider.GetUtcNow() < _nextAutomaticCheck)
        {
            return;
        }

        _ = CheckAutomaticallyAsync();
    }

    private async Task CheckAutomaticallyAsync()
    {
        try
        {
            await CheckAsync(_lifetime.Token);
        }
        catch (OperationCanceledException)
        {
            // Disposed while the request was in flight; nobody is left to tell.
        }
    }

    [RelayCommand]
    private Task CheckNowAsync(CancellationToken cancellationToken) => CheckAsync(cancellationToken);

    private async Task CheckAsync(CancellationToken cancellationToken)
    {
        if (_isChecking)
        {
            return;
        }

        _isChecking = true;
        try
        {
            var release = await _getLatest(cancellationToken);
            var now = _timeProvider.GetUtcNow();
            _lastCheckFailed = false;
            _nextAutomaticCheck = now + CheckInterval;
            Store(_settings with
            {
                LastChecked = now,
                LatestVersion = release?.Version,
                InfoUrl = release?.InfoUrl?.AbsoluteUri,
            });
            _logger.LogInformation(
                "Firmware check: latest known version of {Name} is {Latest}", _device.Name, release?.Version ?? "not listed");
        }
        catch (FirmwareCheckException ex)
        {
            _lastCheckFailed = true;
            _nextAutomaticCheck = _timeProvider.GetUtcNow() + RetryInterval;
            _logger.LogWarning(ex, "Firmware check failed");
        }
        finally
        {
            _isChecking = false;
        }

        UpdateStatus();
    }

    private void UpdateStatus()
    {
        var release = _settings.LatestVersion is { } latest ? new FirmwareRelease(latest, ParseInfoUrl(_settings.InfoUrl)) : null;
        var status = _installedVersion is not null && release is not null
            ? release.StatusFor(_installedVersion)
            : FirmwareStatus.Unknown;

        IsUpdateAvailable = status == FirmwareStatus.UpdateAvailable;
        InfoUrl = IsUpdateAvailable ? release!.InfoUrl : null;
        StatusText = status switch
        {
            FirmwareStatus.UpdateAvailable => Strings.Format("Firmware_UpdateAvailableFormat", release!.Version),
            FirmwareStatus.UpToDate => Strings.Get("Firmware_UpToDate"),
            _ => _lastCheckFailed && _installedVersion is not null ? Strings.Get("Firmware_CheckFailed") : string.Empty,
        };

        if (IsUpdateAvailable && _settings.NotifiedVersion != release!.Version)
        {
            Store(_settings with { NotifiedVersion = release.Version });
            UpdateFound?.Invoke(this, new FirmwareUpdateNotice(_installedVersion!, release.Version));
        }
    }

    // The settings file is editable, and the link is opened in a browser.
    private static Uri? ParseInfoUrl(string? text) =>
        Uri.TryCreate(text, UriKind.Absolute, out var url) && url.Scheme == Uri.UriSchemeHttps ? url : null;

    private void Store(FirmwareCheckSettings settings)
    {
        _settings = settings;
        _save(settings);
    }
}
