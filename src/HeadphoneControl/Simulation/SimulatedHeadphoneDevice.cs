using HeadphoneControl.Protocol.Devices;
using HeadphoneControl.Protocol.Transport;
using HeadphoneControl.Resources;

namespace HeadphoneControl.Simulation;

// Like the real device, it raises StateChanged from a background thread.
public sealed class SimulatedHeadphoneDevice : IHeadphoneDevice
{
    private const int BandCount = 5;
    private const int MinEqualizerLevel = -10;
    private const int MaxEqualizerLevel = 10;
    private const int MinAmbientLevel = 1;
    private const int MaxAmbientLevel = 20;

    // Plausible band curves (400 Hz .. 16 kHz) for each preset; the real values live in the headset firmware.
    private static readonly Dictionary<EqualizerPreset, int[]> PresetBands = new()
    {
        [EqualizerPreset.Off] = [0, 0, 0, 0, 0],
        [EqualizerPreset.Bright] = [0, 1, 3, 4, 3],
        [EqualizerPreset.Excited] = [3, 1, 0, 2, 4],
        [EqualizerPreset.Mellow] = [2, 1, 0, -2, -4],
        [EqualizerPreset.Relaxed] = [-1, -2, -2, -3, -4],
        [EqualizerPreset.Vocal] = [-2, 2, 4, 2, -1],
        [EqualizerPreset.TrebleBoost] = [0, 0, 1, 4, 6],
        [EqualizerPreset.BassBoost] = [5, 3, 0, 0, 0],
        [EqualizerPreset.Speech] = [-4, 2, 4, 2, -3],
    };

    private static readonly DeviceState ConnectedDefaults = new(
        ConnectionStatus.Connected,
        ProtocolGeneration.V2,
        new BatteryState(80, IsCharging: false),
        new NoiseControlState(NoiseControlMode.NoiseCancelling, FocusOnVoice: false, AmbientLevel: 10),
        new EqualizerState(EqualizerPreset.Off, ClearBass: 0, Bands: [0, 0, 0, 0, 0]),
        DseeEnabled: false,
        FirmwareVersion: "1.0.2",
        AudioCodec.Aac);

    private readonly TimeSpan _latency;
    private readonly TimeProvider _timeProvider;
    private readonly Lock _gate = new();
    private DeviceState _state = DeviceState.Disconnected;

    // What the headset "remembers" while disconnected, so reconnecting shows the last settings.
    private DeviceState _stored = ConnectedDefaults;
    private bool _disposed;
    private int _failNextConnect;

    public SimulatedHeadphoneDevice(TimeSpan latency, TimeProvider? timeProvider = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(latency, TimeSpan.Zero);
        _latency = latency;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public string Name => "WH-CH720N (simulated)";

    public DeviceState State
    {
        get
        {
            lock (_gate)
            {
                return _state;
            }
        }
    }

    // Makes only the next ConnectAsync fail with a TransportException.
    public bool FailNextConnect
    {
        get => Volatile.Read(ref _failNextConnect) != 0;
        set => Volatile.Write(ref _failNextConnect, value ? 1 : 0);
    }

    public event EventHandler<DeviceState>? StateChanged;

    public async Task ConnectAsync(CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        Publish(s => s with { Connection = ConnectionStatus.Connecting });
        try
        {
            await DelayAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            Publish(_ => DeviceState.Disconnected);
            throw;
        }

        if (Interlocked.Exchange(ref _failNextConnect, 0) != 0)
        {
            Publish(_ => DeviceState.Disconnected with { Connection = ConnectionStatus.Failed });
            throw new TransportException(Strings.Get("Error_SimulatedConnectFailure"));
        }

        Publish(_ => _stored);
    }

    public async Task DisconnectAsync(CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        await DelayAsync(cancellationToken).ConfigureAwait(false);
        Publish(_ => DeviceState.Disconnected);
    }

    public async Task RefreshAsync(CancellationToken cancellationToken)
    {
        EnsureConnected();
        await DelayAsync(cancellationToken).ConfigureAwait(false);
        EnsureConnected();
        Publish(s => s);
    }

    public async Task SetNoiseControlAsync(NoiseControlState state, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (!Enum.IsDefined(state.Mode))
        {
            throw new ArgumentOutOfRangeException(nameof(state), state.Mode, Strings.Get("Error_UndefinedValue"));
        }

        // Mirrors V2CommandSet: 0..20 is accepted in every mode and the wire level is max(1, level).
        if (state.AmbientLevel is < 0 or > MaxAmbientLevel)
        {
            throw new ArgumentOutOfRangeException(nameof(state), state.AmbientLevel, Strings.Get("Error_AmbientLevelRange"));
        }

        var level = Math.Max(MinAmbientLevel, state.AmbientLevel);
        // The headset only keeps focus on voice in ambient mode.
        var stored = state.Mode == NoiseControlMode.Ambient
            ? state with { AmbientLevel = level }
            : state with { FocusOnVoice = false, AmbientLevel = level };
        await SetAsync(s => s with { NoiseControl = stored }, cancellationToken).ConfigureAwait(false);
    }

    public async Task SetEqualizerPresetAsync(EqualizerPreset preset, CancellationToken cancellationToken)
    {
        if (!Enum.IsDefined(preset))
        {
            throw new ArgumentOutOfRangeException(nameof(preset), preset, Strings.Get("Error_UndefinedValue"));
        }

        await SetAsync(
            s =>
            {
                var current = s.Equalizer ?? ConnectedDefaults.Equalizer!;
                // Manual keeps the current custom curve; other presets load their fixed curve.
                IReadOnlyList<int> bands = PresetBands.TryGetValue(preset, out var curve) ? [.. curve] : current.Bands;
                return s with { Equalizer = current with { Preset = preset, Bands = bands } };
            },
            cancellationToken).ConfigureAwait(false);
    }

    public async Task SetCustomEqualizerAsync(int clearBass, IReadOnlyList<int> bands, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(bands);
        if (bands.Count != BandCount)
        {
            throw new ArgumentException(Strings.Get("Error_EqualizerBandCount"), nameof(bands));
        }

        if (clearBass is < MinEqualizerLevel or > MaxEqualizerLevel)
        {
            throw new ArgumentOutOfRangeException(nameof(clearBass), clearBass, Strings.Get("Error_EqualizerLevelRange"));
        }

        if (bands.Any(b => b is < MinEqualizerLevel or > MaxEqualizerLevel))
        {
            throw new ArgumentOutOfRangeException(nameof(bands), Strings.Get("Error_EqualizerLevelRange"));
        }

        int[] copy = [.. bands];
        await SetAsync(
            s => s with { Equalizer = new EqualizerState(EqualizerPreset.Manual, clearBass, copy) },
            cancellationToken).ConfigureAwait(false);
    }

    public Task SetDseeAsync(bool enabled, CancellationToken cancellationToken) =>
        SetAsync(s => s with { DseeEnabled = enabled }, cancellationToken);

    public ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            _disposed = true;
            _state = DeviceState.Disconnected;
        }

        return ValueTask.CompletedTask;
    }

    private async Task SetAsync(Func<DeviceState, DeviceState> change, CancellationToken cancellationToken)
    {
        EnsureConnected();
        await DelayAsync(cancellationToken).ConfigureAwait(false);
        // The link may have dropped while the request was in flight.
        EnsureConnected();
        Publish(change);
    }

    private Task DelayAsync(CancellationToken cancellationToken) =>
        _latency == TimeSpan.Zero
            ? Task.Run(() => cancellationToken.ThrowIfCancellationRequested(), cancellationToken)
            : Task.Delay(_latency, _timeProvider, cancellationToken);

    private void Publish(Func<DeviceState, DeviceState> change)
    {
        DeviceState next;
        lock (_gate)
        {
            next = change(_state);
            _state = next;
            if (next.Connection == ConnectionStatus.Connected)
            {
                _stored = next;
            }
        }

        StateChanged?.Invoke(this, next);
    }

    private void EnsureConnected()
    {
        ThrowIfDisposed();
        if (State.Connection != ConnectionStatus.Connected)
        {
            throw new InvalidOperationException(Strings.Get("Error_NotConnected"));
        }
    }

    private void ThrowIfDisposed()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
        }
    }
}
