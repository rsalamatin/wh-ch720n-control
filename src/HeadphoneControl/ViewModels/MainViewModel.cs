using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HeadphoneControl.Protocol.Devices;
using HeadphoneControl.Resources;
using Microsoft.Extensions.Logging;

namespace HeadphoneControl.ViewModels;

// Device snapshots are the source of truth. While an edit of a setting group is pending, snapshots don't overwrite
// that group; once its last edit completes or fails, the group is re-synced from the device.
public sealed partial class MainViewModel : ViewModelBase, IDisposable
{
    private const int MinAmbientLevel = 1;
    private const int MaxAmbientLevel = 20;

    private readonly IHeadphoneDevice _device;
    private readonly ILogger _logger;
    private readonly Action<Action> _dispatch;
    private readonly TimeSpan _sliderDebounce;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;

    // One device call at a time: the headset handles a single request in flight.
    private readonly SemaphoreSlim _operationGate = new(1, 1);
    private readonly IAsyncRelayCommand[] _connectionCommands;

    private readonly SettingGroup _noiseGroup = new();
    private readonly SettingGroup _equalizerGroup = new();
    private readonly SettingGroup _dseeGroup = new();

    // Setting sends are cancelled only by the Cancel button or Dispose, never because a newer edit started.
    private CancellationTokenSource _settingsCancellation = new();

    // Set while a device snapshot is copied into bound properties, so those writes aren't sent back to the device.
    private bool _applyingState;
    private int _runningConnectionOperations;
    private int _runningSettingSends;
    private int _lastAmbientLevel = 10;
    private bool _lastFocusOnVoice;

    public MainViewModel(
        IHeadphoneDevice device,
        DiagnosticsViewModel diagnostics,
        ILogger<MainViewModel> logger,
        // Runs an action on the UI thread; device events can arrive on any thread.
        Action<Action> dispatch,
        TimeSpan sliderDebounce,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        ArgumentNullException.ThrowIfNull(device);
        ArgumentNullException.ThrowIfNull(diagnostics);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(dispatch);
        ArgumentOutOfRangeException.ThrowIfLessThan(sliderDebounce, TimeSpan.Zero);

        _device = device;
        _logger = logger;
        _dispatch = dispatch;
        _sliderDebounce = sliderDebounce;
        _delay = delay ?? Task.Delay;
        Diagnostics = diagnostics;
        DeviceName = device.Name;

        NoiseModes = DisplayNames.ChoicesOf<NoiseControlMode>(DisplayNames.Of);
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

        StatusMessage = Strings.Get("Status_Ready");
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
    [NotifyPropertyChangedFor(nameof(ConnectionText), nameof(IsConnected))]
    [NotifyCanExecuteChangedFor(nameof(ConnectCommand), nameof(DisconnectCommand), nameof(RefreshCommand))]
    public partial ConnectionStatus Connection { get; private set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ConnectCommand), nameof(DisconnectCommand), nameof(RefreshCommand), nameof(CancelCommand))]
    public partial bool IsBusy { get; private set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CancelCommand))]
    public partial bool IsApplyingSettings { get; private set; }

    [ObservableProperty]
    public partial string StatusMessage { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial string BatteryText { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial bool IsCharging { get; private set; }

    [ObservableProperty]
    public partial string FirmwareText { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial string CodecText { get; private set; } = string.Empty;

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

    public bool IsConnected => Connection == ConnectionStatus.Connected;

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
        }
    }

    private bool CanConnect() => !IsBusy && Connection is ConnectionStatus.Disconnected or ConnectionStatus.Failed;

    private bool CanUseConnectedDevice() => !IsBusy && IsConnected;

    private bool CanCancel() => IsBusy || IsApplyingSettings;

    [RelayCommand(CanExecute = nameof(CanConnect))]
    private Task ConnectAsync(CancellationToken cancellationToken) =>
        RunConnectionOperationAsync("Operation_Connect", _device.ConnectAsync, () => Strings.Format("Status_ConnectedFormat", DeviceName), cancellationToken);

    [RelayCommand(CanExecute = nameof(CanUseConnectedDevice))]
    private Task DisconnectAsync(CancellationToken cancellationToken) =>
        RunConnectionOperationAsync("Operation_Disconnect", _device.DisconnectAsync, () => Strings.Get("Status_Disconnected"), cancellationToken);

    [RelayCommand(CanExecute = nameof(CanUseConnectedDevice))]
    private Task RefreshAsync(CancellationToken cancellationToken) =>
        RunConnectionOperationAsync("Operation_Refresh", _device.RefreshAsync, () => Strings.Get("Status_Refreshed"), cancellationToken);

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
        return SendSettingAsync(_noiseGroup, "Operation_NoiseControl", edit.Debounce, ct => _device.SetNoiseControlAsync(edit.Value, ct));
    }

    [RelayCommand]
    private Task ApplyEqualizerPresetAsync(EqualizerPreset preset) =>
        SendSettingAsync(
            _equalizerGroup,
            "Operation_EqualizerPreset",
            debounce: false,
            ct => _device.SetEqualizerPresetAsync(preset, ct),
            // The device re-reads the EQ after a preset change; if that read fails the EQ card is disabled until Refresh.
            () => Strings.Get(_device.State is { Connection: ConnectionStatus.Connected, Equalizer: null }
                ? "Status_EqualizerUnknown"
                : "Status_Applied"));

    [RelayCommand]
    private Task ApplyCustomEqualizerAsync(SettingEdit<EqualizerState> edit)
    {
        ArgumentNullException.ThrowIfNull(edit);
        return SendSettingAsync(
            _equalizerGroup,
            "Operation_CustomEqualizer",
            edit.Debounce,
            ct => _device.SetCustomEqualizerAsync(edit.Value.ClearBass, edit.Value.Bands, ct));
    }

    [RelayCommand]
    private Task ApplyDseeAsync(bool enabled) =>
        SendSettingAsync(_dseeGroup, "Operation_Dsee", debounce: false, ct => _device.SetDseeAsync(enabled, ct));

    partial void OnSelectedNoiseModeChanged(Choice<NoiseControlMode>? value)
    {
        if (value is null)
        {
            return;
        }

        ApplyLocally(() => FocusOnVoice = EffectiveFocusOnVoice(value.Value));
        if (!_applyingState)
        {
            ApplyNoiseControlCommand.Execute(new SettingEdit<NoiseControlState>(CurrentNoiseControl(value.Value), Debounce: false));
        }
    }

    partial void OnFocusOnVoiceChanged(bool value)
    {
        if (_applyingState || SelectedNoiseMode is not { } mode)
        {
            return;
        }

        _lastFocusOnVoice = value;
        ApplyNoiseControlCommand.Execute(new SettingEdit<NoiseControlState>(CurrentNoiseControl(mode.Value), Debounce: false));
    }

    partial void OnAmbientLevelChanged(int value)
    {
        if (_applyingState || SelectedNoiseMode is not { } mode)
        {
            return;
        }

        _lastAmbientLevel = value;
        ApplyNoiseControlCommand.Execute(new SettingEdit<NoiseControlState>(CurrentNoiseControl(mode.Value), Debounce: true));
    }

    partial void OnSelectedPresetChanged(Choice<EqualizerPreset>? value)
    {
        if (!_applyingState && value is not null)
        {
            ApplyEqualizerPresetCommand.Execute(value.Value);
        }
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
        var curve = new EqualizerState(EqualizerPreset.Manual, ClearBass.Value, [.. Bands.Select(b => b.Value)]);
        ApplyCustomEqualizerCommand.Execute(new SettingEdit<EqualizerState>(curve, Debounce: true));
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
        IsCharging = state.Battery?.IsCharging ?? false;
        FirmwareText = state.FirmwareVersion ?? Strings.UnknownValue;
        CodecText = state.Codec is { } codec ? DisplayNames.Of(codec) : Strings.UnknownValue;

        HasNoiseControl = state.NoiseControl is not null;
        if (state.NoiseControl is { } noise && !_noiseGroup.HasPendingEdits)
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
        if (state.Equalizer is { } equalizer && !_equalizerGroup.HasPendingEdits)
        {
            SelectedPreset = Presets.FirstOrDefault(p => p.Value == equalizer.Preset);
            ClearBass.Value = equalizer.ClearBass;
            for (var i = 0; i < Bands.Count && i < equalizer.Bands.Count; i++)
            {
                Bands[i].Value = equalizer.Bands[i];
            }
        }

        HasDsee = state.DseeEnabled is not null;
        if (!_dseeGroup.HasPendingEdits)
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
        Func<string> successMessage,
        CancellationToken cancellationToken)
    {
        IsBusy = Interlocked.Increment(ref _runningConnectionOperations) > 0;
        try
        {
            await RunGuardedAsync(
                operationKey,
                async ct =>
                {
                    await _operationGate.WaitAsync(ct);
                    try
                    {
                        await operation(ct);
                        return true;
                    }
                    finally
                    {
                        _operationGate.Release();
                    }
                },
                successMessage,
                cancellationToken);
        }
        finally
        {
            IsBusy = Interlocked.Decrement(ref _runningConnectionOperations) > 0;
            ApplyState(_device.State);
        }
    }

    private async Task SendSettingAsync(
        SettingGroup group,
        string operationKey,
        bool debounce,
        Func<CancellationToken, Task> send,
        Func<string>? successMessage = null)
    {
        var version = group.BeginEdit();
        IsApplyingSettings = Interlocked.Increment(ref _runningSettingSends) > 0;
        try
        {
            await RunGuardedAsync(
                operationKey,
                async ct =>
                {
                    if (debounce)
                    {
                        await _delay(_sliderDebounce, ct);
                    }

                    await _operationGate.WaitAsync(ct);
                    try
                    {
                        // A newer edit of this group carries the complete value; this one is obsolete.
                        if (group.IsSuperseded(version))
                        {
                            return false;
                        }

                        await send(ct);
                        return true;
                    }
                    finally
                    {
                        _operationGate.Release();
                    }
                },
                successMessage ?? (() => Strings.Get("Status_Applied")),
                _settingsCancellation.Token);
        }
        finally
        {
            IsApplyingSettings = Interlocked.Decrement(ref _runningSettingSends) > 0;
            if (group.EndEdit())
            {
                // Last edit of the group done (or failed): show what the device actually holds.
                ApplyState(_device.State);
            }
        }
    }

    private async Task RunGuardedAsync(
        string operationKey,
        Func<CancellationToken, Task<bool>> operation,
        Func<string> successMessage,
        CancellationToken cancellationToken)
    {
        var operationName = Strings.Get(operationKey);
        try
        {
            if (await operation(cancellationToken))
            {
                StatusMessage = successMessage();
                _logger.LogInformation("{Operation} succeeded", operationName);
            }
        }
        catch (OperationCanceledException)
        {
            StatusMessage = Strings.Get("Status_Cancelled");
            _logger.LogInformation("{Operation} cancelled", operationName);
        }
        catch (NotSupportedException ex)
        {
            StatusMessage = Strings.Format("Status_NotSupportedFormat", operationName);
            _logger.LogError(ex, "{Operation} refused: not a confirmed V2 device", operationName);
        }
        catch (Exception ex) when (IsDeviceFailure(ex))
        {
            StatusMessage = Strings.Format("Status_FailedFormat", operationName, ex.Message);
            _logger.LogError(ex, "{Operation} failed", operationName);
        }
    }

    // Exactly the failure contract documented on IHeadphoneDevice (cancellation is handled separately).
    private static bool IsDeviceFailure(Exception ex) =>
        ex is IOException or TimeoutException or NotSupportedException or FormatException
            or InvalidOperationException or ArgumentException;

    private sealed class SettingGroup
    {
        private int _version;
        private int _pending;

        public bool HasPendingEdits => Volatile.Read(ref _pending) > 0;

        public int BeginEdit()
        {
            Interlocked.Increment(ref _pending);
            return Interlocked.Increment(ref _version);
        }

        public bool IsSuperseded(int version) => Volatile.Read(ref _version) != version;

        public bool EndEdit() => Interlocked.Decrement(ref _pending) == 0;
    }
}
