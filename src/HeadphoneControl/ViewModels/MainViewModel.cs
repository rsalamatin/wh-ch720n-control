using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HeadphoneControl.Protocol.Devices;
using HeadphoneControl.Resources;
using Microsoft.Extensions.Logging;

namespace HeadphoneControl.ViewModels;

// Device snapshots are the source of truth. While the device has a pending edit of a setting group, snapshots don't
// overwrite that group; once its last edit completes or fails, the group is re-synced from the device.
public sealed partial class MainViewModel : ViewModelBase, IDisposable
{
    private const int MinAmbientLevel = 1;
    private const int MaxAmbientLevel = 20;
    private const int LowBatteryLevel = 20;

    private readonly IHeadphoneDevice _device;
    private readonly ILogger _logger;
    private readonly Action<Action> _dispatch;
    private readonly IAsyncRelayCommand[] _connectionCommands;

    // Setting sends are cancelled only by the Cancel button or Dispose, never because a newer edit started.
    private CancellationTokenSource _settingsCancellation = new();

    // Set while a device snapshot is copied into bound properties, so those writes aren't sent back to the device.
    private bool _applyingState;
    private int _runningConnectionOperations;
    private int _runningSettingSends;
    private int _lastAmbientLevel = 10;
    private bool _lastFocusOnVoice;
    private bool _isCodecKnown;

    public MainViewModel(
        IHeadphoneDevice device,
        DiagnosticsViewModel diagnostics,
        ILogger<MainViewModel> logger,
        // Runs an action on the UI thread; device events can arrive on any thread.
        Action<Action> dispatch)
    {
        ArgumentNullException.ThrowIfNull(device);
        ArgumentNullException.ThrowIfNull(diagnostics);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(dispatch);

        _device = device;
        _logger = logger;
        _dispatch = dispatch;
        Diagnostics = diagnostics;
        DeviceName = device.Name;

        NoiseModes = [.. new[] { NoiseControlMode.NoiseCancelling, NoiseControlMode.Ambient, NoiseControlMode.Off }
            .Select(mode => new Choice<NoiseControlMode>(mode, DisplayNames.Of(mode)))];
        Presets = DisplayNames.ChoicesOf<EqualizerPreset>(DisplayNames.Of);
        ClearBass = new EqualizerBandViewModel(Strings.Get("EqBand_ClearBass"));
        Bands =
        [
            new(Strings.Get("EqBand_400Hz")),
            new(Strings.Get("EqBand_1kHz")),
            new(Strings.Get("EqBand_2500Hz")),
            new(Strings.Get("EqBand_6300Hz")),
            new(Strings.Get("EqBand_16kHz")),
        ];
        ClearBass.PropertyChanged += OnEqualizerBandChanged;
        foreach (var band in Bands)
        {
            band.PropertyChanged += OnEqualizerBandChanged;
        }

        _connectionCommands = [ConnectCommand, DisconnectCommand, RefreshCommand];
        ConnectCommand.PropertyChanged += OnConnectCommandChanged;

        SetStatus(new Status(Strings.Get("Status_Ready"), StatusSeverity.Info));
        ApplyState(device.State);
        _device.StateChanged += OnDeviceStateChanged;
    }

    public DiagnosticsViewModel Diagnostics { get; }

    public string DeviceName { get; }

    public IReadOnlyList<Choice<NoiseControlMode>> NoiseModes { get; }

    public IReadOnlyList<Choice<EqualizerPreset>> Presets { get; }

    public EqualizerBandViewModel ClearBass { get; }

    public IReadOnlyList<EqualizerBandViewModel> Bands { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ConnectionText), nameof(ConnectionSummary), nameof(IsConnected), nameof(IsConnectionFailed),
        nameof(IsConnecting), nameof(EmptyStateTitle), nameof(EmptyStateBody), nameof(ConnectButtonText))]
    [NotifyCanExecuteChangedFor(nameof(ConnectCommand), nameof(DisconnectCommand), nameof(RefreshCommand))]
    public partial ConnectionStatus Connection { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsOperationRunning))]
    [NotifyCanExecuteChangedFor(nameof(ConnectCommand), nameof(DisconnectCommand), nameof(RefreshCommand), nameof(CancelCommand))]
    public partial bool IsBusy { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsOperationRunning))]
    [NotifyCanExecuteChangedFor(nameof(CancelCommand))]
    public partial bool IsApplyingSettings { get; private set; }

    [ObservableProperty]
    public partial string StatusMessage { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial StatusSeverity StatusSeverity { get; private set; }

    // Failures and warnings surface in an info bar; routine confirmations only update StatusMessage.
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(DismissAlertCommand))]
    public partial bool IsAlertVisible { get; private set; }

    [ObservableProperty]
    public partial string BatteryText { get; private set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsBatteryLow))]
    public partial int BatteryLevel { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsBatteryLow))]
    public partial bool HasBattery { get; private set; }

    [ObservableProperty]
    public partial bool IsCharging { get; private set; }

    [ObservableProperty]
    public partial string FirmwareText { get; private set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ConnectionSummary))]
    public partial string CodecText { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial bool IsNoiseSectionExpanded { get; set; } = true;

    [ObservableProperty]
    public partial bool IsSoundSectionExpanded { get; set; } = true;

    [ObservableProperty]
    public partial bool HasNoiseControl { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsAmbientMode))]
    public partial Choice<NoiseControlMode>? SelectedNoiseMode { get; set; }

    [ObservableProperty]
    public partial bool FocusOnVoice { get; set; }

    [ObservableProperty]
    public partial int AmbientLevel { get; set; } = 10;

    [ObservableProperty]
    public partial bool HasEqualizer { get; private set; }

    [ObservableProperty]
    public partial Choice<EqualizerPreset>? SelectedPreset { get; set; }

    [ObservableProperty]
    public partial bool HasDsee { get; private set; }

    [ObservableProperty]
    public partial bool IsDseeEnabled { get; set; }

    public string ConnectionText => DisplayNames.Of(Connection);

    public string ConnectionSummary => IsConnected && _isCodecKnown
        ? Strings.Format("ConnectionWithCodecFormat", ConnectionText, CodecText)
        : ConnectionText;

    public bool IsConnected => Connection == ConnectionStatus.Connected;

    public bool IsConnectionFailed => Connection == ConnectionStatus.Failed;

    // Also true before the device reports Connecting, so the connect screen reacts to the click at once. Only the
    // connect command counts: Disconnect and Refresh are busy too, and the link may already read Disconnected then.
    public bool IsConnecting => !IsConnected && (ConnectCommand.IsRunning || Connection == ConnectionStatus.Connecting);

    public bool IsOperationRunning => IsBusy || IsApplyingSettings;

    public bool IsBatteryLow => HasBattery && BatteryLevel <= LowBatteryLevel;

    public string EmptyStateTitle => IsConnecting
        ? DisplayNames.Of(ConnectionStatus.Connecting)
        : Strings.Get(IsConnectionFailed ? "EmptyState_FailedTitle" : "EmptyState_DisconnectedTitle");

    public string EmptyStateBody => Strings.Get(IsConnecting
        ? "EmptyState_ConnectingBody"
        : IsConnectionFailed ? "EmptyState_FailedBody" : "EmptyState_DisconnectedBody");

    public string ConnectButtonText => Strings.Get(IsConnectionFailed ? "TryAgain" : "Connect");

    public string NoiseSummary => SelectedNoiseMode switch
    {
        null => Strings.UnknownValue,
        { Value: NoiseControlMode.Ambient } mode => Strings.Format("Summary_AmbientFormat", mode.Label, AmbientLevel),
        var mode => mode.Label,
    };

    public string SoundSummary => Strings.Format(
        "Summary_SoundFormat",
        HasEqualizer ? SelectedPreset?.Label ?? Strings.UnknownValue : Strings.UnknownValue,
        HasDsee ? Strings.Get(IsDseeEnabled ? "On" : "Off") : Strings.UnknownValue);

    public bool IsAmbientMode => SelectedNoiseMode?.Value == NoiseControlMode.Ambient;

    // Not tied to IsBusy: sends are serialized internally, and disabling controls would break a slider drag.
    public bool CanEditSettings => IsConnected;

    public bool CanEditNoiseControl => CanEditSettings && HasNoiseControl;

    public bool CanEditAmbient => CanEditNoiseControl && IsAmbientMode;

    public bool CanEditEqualizer => CanEditSettings && HasEqualizer;

    public bool CanEditDsee => CanEditSettings && HasDsee;

    public void Dispose()
    {
        _device.StateChanged -= OnDeviceStateChanged;
        ConnectCommand.PropertyChanged -= OnConnectCommandChanged;
        Cancel();
        Diagnostics.Dispose();
    }

    protected override void OnPropertyChanged(PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (e.PropertyName is nameof(Connection) or nameof(HasNoiseControl) or nameof(HasEqualizer)
            or nameof(HasDsee) or nameof(SelectedNoiseMode))
        {
            base.OnPropertyChanged(new PropertyChangedEventArgs(nameof(CanEditSettings)));
            base.OnPropertyChanged(new PropertyChangedEventArgs(nameof(CanEditNoiseControl)));
            base.OnPropertyChanged(new PropertyChangedEventArgs(nameof(CanEditAmbient)));
            base.OnPropertyChanged(new PropertyChangedEventArgs(nameof(CanEditEqualizer)));
            base.OnPropertyChanged(new PropertyChangedEventArgs(nameof(CanEditDsee)));
            ResetEqualizerCommand.NotifyCanExecuteChanged();
        }

        if (e.PropertyName is nameof(SelectedNoiseMode) or nameof(AmbientLevel))
        {
            base.OnPropertyChanged(new PropertyChangedEventArgs(nameof(NoiseSummary)));
        }

        if (e.PropertyName is nameof(SelectedPreset) or nameof(IsDseeEnabled) or nameof(HasDsee) or nameof(HasEqualizer))
        {
            base.OnPropertyChanged(new PropertyChangedEventArgs(nameof(SoundSummary)));
        }
    }

    private bool CanConnect() => !IsBusy && Connection is ConnectionStatus.Disconnected or ConnectionStatus.Failed;

    private bool CanUseConnectedDevice() => !IsBusy && IsConnected;

    private bool CanCancel() => IsBusy || IsApplyingSettings;

    [RelayCommand(CanExecute = nameof(CanConnect))]
    private Task ConnectAsync(CancellationToken cancellationToken) =>
        RunConnectionOperationAsync("Operation_Connect", _device.ConnectAsync, () => Status.Info(Strings.Format("Status_ConnectedFormat", DeviceName)), cancellationToken);

    [RelayCommand(CanExecute = nameof(CanUseConnectedDevice))]
    private Task DisconnectAsync(CancellationToken cancellationToken) =>
        RunConnectionOperationAsync("Operation_Disconnect", _device.DisconnectAsync, () => Status.Info(Strings.Get("Status_Disconnected")), cancellationToken);

    [RelayCommand(CanExecute = nameof(CanUseConnectedDevice))]
    private Task RefreshAsync(CancellationToken cancellationToken) =>
        RunConnectionOperationAsync("Operation_Refresh", _device.RefreshAsync, () => Status.Info(Strings.Get("Status_Refreshed")), cancellationToken);

    [RelayCommand(CanExecute = nameof(IsAlertVisible))]
    private void DismissAlert() => IsAlertVisible = false;

    // Preset Off is the device's flat curve; selecting it sends the preset like a user pick would.
    [RelayCommand(CanExecute = nameof(CanEditEqualizer))]
    private void ResetEqualizer() => SelectedPreset = Presets.First(p => p.Value == EqualizerPreset.Off);

    [RelayCommand(CanExecute = nameof(CanCancel))]
    private void Cancel()
    {
        // The old source is not disposed: in-flight sends may still register on its token.
        var previous = _settingsCancellation;
        _settingsCancellation = new CancellationTokenSource();
        previous.Cancel();
        foreach (var command in _connectionCommands)
        {
            command.Cancel();
        }
    }

    [RelayCommand]
    private Task ApplyNoiseControlAsync(SettingEdit<NoiseControlState> edit)
    {
        ArgumentNullException.ThrowIfNull(edit);
        return SendSettingAsync("Operation_NoiseControl", ct => _device.SetNoiseControlAsync(edit.Value, edit.Pacing, ct));
    }

    [RelayCommand]
    private Task ApplyEqualizerPresetAsync(EqualizerPreset preset) =>
        SendSettingAsync(
            "Operation_EqualizerPreset",
            ct => _device.SetEqualizerPresetAsync(preset, ct),
            // The device re-reads the EQ after a preset change; if that read fails the EQ card is disabled until Refresh.
            () => _device.State is { Connection: ConnectionStatus.Connected, Equalizer: null }
                ? new Status(Strings.Get("Status_EqualizerUnknown"), StatusSeverity.Warning)
                : Status.Info(Strings.Get("Status_Applied")));

    [RelayCommand]
    private Task ApplyCustomEqualizerAsync(SettingEdit<EqualizerState> edit)
    {
        ArgumentNullException.ThrowIfNull(edit);
        return SendSettingAsync(
            "Operation_CustomEqualizer",
            ct => _device.SetCustomEqualizerAsync(edit.Value.ClearBass, edit.Value.Bands, edit.Pacing, ct));
    }

    [RelayCommand]
    private Task ApplyDseeAsync(bool enabled) =>
        SendSettingAsync("Operation_Dsee", ct => _device.SetDseeAsync(enabled, ct));

    partial void OnSelectedNoiseModeChanged(Choice<NoiseControlMode>? value)
    {
        if (value is null)
        {
            // Ctrl+click can clear the tile selection; a mode is always active on the headset, so show it again.
            if (!_applyingState && _device.State.NoiseControl is { } noise)
            {
                ApplyLocally(() => SelectedNoiseMode = NoiseModes.First(m => m.Value == noise.Mode));
            }

            return;
        }

        ApplyLocally(() => FocusOnVoice = EffectiveFocusOnVoice(value.Value));
        if (!_applyingState)
        {
            ApplyNoiseControlCommand.Execute(new SettingEdit<NoiseControlState>(CurrentNoiseControl(value.Value), EditPacing.Immediate));
        }
    }

    partial void OnFocusOnVoiceChanged(bool value)
    {
        if (_applyingState || SelectedNoiseMode is not { } mode)
        {
            return;
        }

        _lastFocusOnVoice = value;
        ApplyNoiseControlCommand.Execute(new SettingEdit<NoiseControlState>(CurrentNoiseControl(mode.Value), EditPacing.Immediate));
    }

    partial void OnAmbientLevelChanged(int value)
    {
        if (_applyingState || SelectedNoiseMode is not { } mode)
        {
            return;
        }

        _lastAmbientLevel = value;
        ApplyNoiseControlCommand.Execute(new SettingEdit<NoiseControlState>(CurrentNoiseControl(mode.Value), EditPacing.Debounced));
    }

    partial void OnSelectedPresetChanged(Choice<EqualizerPreset>? value)
    {
        if (_applyingState || value is null)
        {
            return;
        }

        // The device has no "Manual" preset command; Manual is a custom curve, so the curve on screen is sent as one.
        if (value.Value == EqualizerPreset.Manual)
        {
            ApplyCustomEqualizerCommand.Execute(new SettingEdit<EqualizerState>(CurrentCurve(), EditPacing.Immediate));
            return;
        }

        ApplyEqualizerPresetCommand.Execute(value.Value);
    }

    partial void OnIsDseeEnabledChanged(bool value)
    {
        if (!_applyingState)
        {
            ApplyDseeCommand.Execute(value);
        }
    }

    private void OnEqualizerBandChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_applyingState || e.PropertyName != nameof(EqualizerBandViewModel.Value))
        {
            return;
        }

        // Editing a band means a custom curve; reflect that locally without sending a preset command.
        ApplyLocally(() => SelectedPreset = Presets.First(p => p.Value == EqualizerPreset.Manual));
        ApplyCustomEqualizerCommand.Execute(new SettingEdit<EqualizerState>(CurrentCurve(), EditPacing.Debounced));
    }

    private EqualizerState CurrentCurve() =>
        new(EqualizerPreset.Manual, ClearBass.Value, [.. Bands.Select(b => b.Value)]);

    private void OnConnectCommandChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(IAsyncRelayCommand.IsRunning))
        {
            OnPropertyChanged(nameof(IsConnecting));
            OnPropertyChanged(nameof(EmptyStateTitle));
            OnPropertyChanged(nameof(EmptyStateBody));
        }
    }

    private NoiseControlState CurrentNoiseControl(NoiseControlMode mode) =>
        new(mode, EffectiveFocusOnVoice(mode), Math.Clamp(_lastAmbientLevel, MinAmbientLevel, MaxAmbientLevel));

    // Outside Ambient the headset holds focus on voice off; the remembered choice is only restored in Ambient.
    private bool EffectiveFocusOnVoice(NoiseControlMode mode) => mode == NoiseControlMode.Ambient && _lastFocusOnVoice;

    // The event argument may be older than State by the time the dispatcher runs; always show the latest snapshot.
    private void OnDeviceStateChanged(object? sender, DeviceState state) => _dispatch(() => ApplyState(_device.State));

    private void ApplyState(DeviceState state) => ApplyLocally(() =>
    {
        Connection = state.Connection;

        BatteryText = state.Battery is { } battery
            ? Strings.Format("BatteryLevelFormat", battery.Level)
            : Strings.UnknownValue;
        HasBattery = state.Battery is not null;
        BatteryLevel = state.Battery?.Level ?? 0;
        IsCharging = state.Battery?.IsCharging ?? false;
        FirmwareText = state.FirmwareVersion ?? Strings.UnknownValue;
        _isCodecKnown = state.Codec is not null;
        CodecText = state.Codec is { } codec ? DisplayNames.Of(codec) : Strings.UnknownValue;

        HasNoiseControl = state.NoiseControl is not null;
        if (state.NoiseControl is { } noise && !_device.HasPendingEdit(SettingGroup.NoiseControl))
        {
            SelectedNoiseMode = NoiseModes.First(m => m.Value == noise.Mode);
            // Outside ambient mode the device reports placeholders; keep the user's last ambient settings.
            if (noise.Mode == NoiseControlMode.Ambient)
            {
                _lastAmbientLevel = noise.AmbientLevel;
                _lastFocusOnVoice = noise.FocusOnVoice;
            }

            AmbientLevel = _lastAmbientLevel;
            FocusOnVoice = EffectiveFocusOnVoice(noise.Mode);
        }

        HasEqualizer = state.Equalizer is not null;
        if (state.Equalizer is { } equalizer && !_device.HasPendingEdit(SettingGroup.Equalizer))
        {
            SelectedPreset = Presets.FirstOrDefault(p => p.Value == equalizer.Preset);
            ClearBass.Value = equalizer.ClearBass;
            for (var i = 0; i < Bands.Count && i < equalizer.Bands.Count; i++)
            {
                Bands[i].Value = equalizer.Bands[i];
            }
        }

        HasDsee = state.DseeEnabled is not null;
        if (!_device.HasPendingEdit(SettingGroup.Dsee))
        {
            IsDseeEnabled = state.DseeEnabled ?? false;
        }
    });

    private void ApplyLocally(Action apply)
    {
        var wasApplying = _applyingState;
        _applyingState = true;
        try
        {
            apply();
        }
        finally
        {
            _applyingState = wasApplying;
        }
    }

    private async Task RunConnectionOperationAsync(
        string operationKey,
        Func<CancellationToken, Task> operation,
        Func<Status> success,
        CancellationToken cancellationToken)
    {
        IsBusy = Interlocked.Increment(ref _runningConnectionOperations) > 0;
        try
        {
            await RunGuardedAsync(
                operationKey,
                async ct =>
                {
                    await operation(ct);
                    return true;
                },
                success,
                cancellationToken);
        }
        finally
        {
            IsBusy = Interlocked.Decrement(ref _runningConnectionOperations) > 0;
            ApplyState(_device.State);
        }
    }

    private async Task SendSettingAsync(
        string operationKey,
        Func<CancellationToken, Task<EditOutcome>> send,
        Func<Status>? success = null)
    {
        IsApplyingSettings = Interlocked.Increment(ref _runningSettingSends) > 0;
        try
        {
            await RunGuardedAsync(
                operationKey,
                // A superseded edit sent nothing; the newer edit that replaced it reports the outcome.
                async ct => await send(ct) == EditOutcome.Applied,
                success ?? (() => Status.Info(Strings.Get("Status_Applied"))),
                _settingsCancellation.Token);
        }
        finally
        {
            IsApplyingSettings = Interlocked.Decrement(ref _runningSettingSends) > 0;

            // Re-syncs every group that has no pending edit, including this one once its last edit is done.
            ApplyState(_device.State);
        }
    }

    private async Task RunGuardedAsync(
        string operationKey,
        Func<CancellationToken, Task<bool>> operation,
        Func<Status> success,
        CancellationToken cancellationToken)
    {
        var operationName = Strings.Get(operationKey);
        try
        {
            if (await operation(cancellationToken))
            {
                SetStatus(success());
                _logger.LogInformation("{Operation} succeeded", operationName);
            }
        }
        catch (OperationCanceledException)
        {
            SetStatus(Status.Info(Strings.Get("Status_Cancelled")));
            _logger.LogInformation("{Operation} cancelled", operationName);
        }
        catch (NotSupportedException ex)
        {
            SetStatus(new Status(Strings.Format("Status_NotSupportedFormat", operationName), StatusSeverity.Error));
            _logger.LogError(ex, "{Operation} refused: not a confirmed V2 device", operationName);
        }
        catch (Exception ex) when (IsDeviceFailure(ex))
        {
            SetStatus(new Status(Strings.Format("Status_FailedFormat", operationName, ex.Message), StatusSeverity.Error));
            _logger.LogError(ex, "{Operation} failed", operationName);
        }
    }

    // Exactly the failure contract documented on IHeadphoneDevice (cancellation is handled separately).
    private static bool IsDeviceFailure(Exception ex) =>
        ex is IOException or TimeoutException or NotSupportedException or FormatException
            or InvalidOperationException or ArgumentException;

    private void SetStatus(Status status)
    {
        StatusMessage = status.Message;
        StatusSeverity = status.Severity;
        IsAlertVisible = status.Severity != StatusSeverity.Info;
    }

    private readonly record struct Status(string Message, StatusSeverity Severity)
    {
        public static Status Info(string message) => new(message, StatusSeverity.Info);
    }
}
