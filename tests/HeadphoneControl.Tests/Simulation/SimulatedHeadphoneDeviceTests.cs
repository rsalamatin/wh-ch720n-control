using HeadphoneControl.Protocol.Devices;
using HeadphoneControl.Protocol.Transport;
using HeadphoneControl.Simulation;

namespace HeadphoneControl.Tests.Simulation;

public class SimulatedHeadphoneDeviceTests
{
    private static SimulatedHeadphoneDevice CreateDevice() => new(TimeSpan.Zero);

    private static async Task<SimulatedHeadphoneDevice> CreateConnectedDeviceAsync()
    {
        var device = CreateDevice();
        await device.ConnectAsync(CancellationToken.None);
        return device;
    }

    [Test]
    public async Task WhenCreatedThenStateIsDisconnected()
    {
        await using var device = CreateDevice();

        var state = device.State;

        await Assert.That(state).IsEqualTo(DeviceState.Disconnected);
    }

    [Test]
    public async Task WhenConnectedThenConnectionIsConnected()
    {
        await using var device = CreateDevice();

        await device.ConnectAsync(CancellationToken.None);

        await Assert.That(device.State.Connection).IsEqualTo(ConnectionStatus.Connected);
    }

    [Test]
    public async Task WhenConnectedThenGenerationIsV2()
    {
        await using var device = CreateDevice();

        await device.ConnectAsync(CancellationToken.None);

        await Assert.That(device.State.Generation).IsEqualTo(ProtocolGeneration.V2);
    }

    [Test]
    public async Task WhenConnectedThenStateChangedReportsConnected()
    {
        await using var device = CreateDevice();
        var reported = new List<ConnectionStatus>();
        device.StateChanged += (_, s) => reported.Add(s.Connection);

        await device.ConnectAsync(CancellationToken.None);

        await Assert.That(string.Join(",", reported)).IsEqualTo("Connecting,Connected");
    }

    [Test]
    public async Task WhenConnectFailureIsSimulatedThenConnectThrowsTransportException()
    {
        await using var device = CreateDevice();
        device.FailNextConnect = true;

        var act = () => device.ConnectAsync(CancellationToken.None);

        await Assert.That(act).Throws<TransportException>();
    }

    [Test]
    public async Task WhenConnectFailureIsSimulatedThenConnectionIsFailed()
    {
        await using var device = CreateDevice();
        device.FailNextConnect = true;

        await Assert.That(() => device.ConnectAsync(CancellationToken.None)).Throws<TransportException>();

        await Assert.That(device.State.Connection).IsEqualTo(ConnectionStatus.Failed);
    }

    [Test]
    public async Task WhenConnectFailedOnceThenNextConnectSucceeds()
    {
        await using var device = CreateDevice();
        device.FailNextConnect = true;
        await Assert.That(() => device.ConnectAsync(CancellationToken.None)).Throws<TransportException>();

        await device.ConnectAsync(CancellationToken.None);

        await Assert.That(device.State.Connection).IsEqualTo(ConnectionStatus.Connected);
    }

    [Test]
    public async Task WhenConnectIsCancelledThenOperationCanceledExceptionIsThrown()
    {
        await using var device = CreateDevice();
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        var act = () => device.ConnectAsync(cancellation.Token);

        await Assert.That(act).Throws<OperationCanceledException>();
    }

    [Test]
    public async Task WhenDisconnectedThenStateIsDisconnected()
    {
        await using var device = await CreateConnectedDeviceAsync();

        await device.DisconnectAsync(CancellationToken.None);

        await Assert.That(device.State).IsEqualTo(DeviceState.Disconnected);
    }

    [Test]
    public async Task WhenSettingWhileDisconnectedThenInvalidOperationExceptionIsThrown()
    {
        await using var device = CreateDevice();

        var act = () => device.SetDseeAsync(true, CancellationToken.None);

        await Assert.That(act).Throws<InvalidOperationException>();
    }

    [Test]
    public async Task WhenNoiseControlSetThenStateHoldsNewValue()
    {
        await using var device = await CreateConnectedDeviceAsync();
        var requested = new NoiseControlState(NoiseControlMode.Ambient, FocusOnVoice: true, AmbientLevel: 17);

        await device.SetNoiseControlAsync(requested, CancellationToken.None);

        await Assert.That(device.State.NoiseControl).IsEqualTo(requested);
    }

    [Test]
    public async Task WhenSettingChangesThenStateChangedCarriesNewSnapshot()
    {
        await using var device = await CreateConnectedDeviceAsync();
        DeviceState? reported = null;
        device.StateChanged += (_, s) => reported = s;

        await device.SetDseeAsync(true, CancellationToken.None);

        await Assert.That(reported?.DseeEnabled ?? false).IsTrue();
    }

    [Test]
    [Arguments(-1)]
    [Arguments(21)]
    public async Task WhenAmbientLevelIsOutOfRangeThenArgumentOutOfRangeExceptionIsThrown(int level)
    {
        await using var device = await CreateConnectedDeviceAsync();
        var requested = new NoiseControlState(NoiseControlMode.Ambient, FocusOnVoice: false, level);

        var act = () => device.SetNoiseControlAsync(requested, CancellationToken.None);

        await Assert.That(act).Throws<ArgumentOutOfRangeException>();
    }

    [Test]
    public async Task WhenAmbientLevelIsZeroThenLevelOneIsStored()
    {
        await using var device = await CreateConnectedDeviceAsync();

        await device.SetNoiseControlAsync(new NoiseControlState(NoiseControlMode.Ambient, false, 0), CancellationToken.None);

        await Assert.That(device.State.NoiseControl?.AmbientLevel).IsEqualTo(1);
    }

    [Test]
    [Arguments(NoiseControlMode.Off)]
    [Arguments(NoiseControlMode.NoiseCancelling)]
    public async Task WhenNotAmbientThenFocusOnVoiceIsStoredOff(NoiseControlMode mode)
    {
        await using var device = await CreateConnectedDeviceAsync();

        await device.SetNoiseControlAsync(new NoiseControlState(mode, FocusOnVoice: true, AmbientLevel: 5), CancellationToken.None);

        await Assert.That(device.State.NoiseControl?.FocusOnVoice ?? true).IsFalse();
    }

    [Test]
    public async Task WhenPresetSetThenBandsFollowPresetCurve()
    {
        await using var device = await CreateConnectedDeviceAsync();

        await device.SetEqualizerPresetAsync(EqualizerPreset.BassBoost, CancellationToken.None);

        await Assert.That(string.Join(",", device.State.Equalizer!.Bands)).IsEqualTo("5,3,0,0,0");
    }

    [Test]
    public async Task WhenPresetOffSetThenBandsAreFlat()
    {
        await using var device = await CreateConnectedDeviceAsync();
        await device.SetCustomEqualizerAsync(0, [4, 4, 4, 4, 4], CancellationToken.None);

        await device.SetEqualizerPresetAsync(EqualizerPreset.Off, CancellationToken.None);

        await Assert.That(string.Join(",", device.State.Equalizer!.Bands)).IsEqualTo("0,0,0,0,0");
    }

    [Test]
    public async Task WhenCustomEqualizerSetThenPresetIsManual()
    {
        await using var device = await CreateConnectedDeviceAsync();

        await device.SetCustomEqualizerAsync(5, [0, 1, 2, 1, 0], CancellationToken.None);

        await Assert.That(device.State.Equalizer?.Preset).IsEqualTo(EqualizerPreset.Manual);
    }

    [Test]
    public async Task WhenCustomEqualizerSetThenBandsAreStored()
    {
        await using var device = await CreateConnectedDeviceAsync();

        await device.SetCustomEqualizerAsync(5, [0, 1, 2, 1, 0], CancellationToken.None);

        await Assert.That(string.Join(",", device.State.Equalizer!.Bands)).IsEqualTo("0,1,2,1,0");
    }

    [Test]
    [Arguments(-11)]
    [Arguments(11)]
    public async Task WhenBandIsOutOfRangeThenArgumentOutOfRangeExceptionIsThrown(int level)
    {
        await using var device = await CreateConnectedDeviceAsync();

        var act = () => device.SetCustomEqualizerAsync(0, [0, 0, level, 0, 0], CancellationToken.None);

        await Assert.That(act).Throws<ArgumentOutOfRangeException>();
    }

    [Test]
    public async Task WhenBandCountIsNotFiveThenArgumentExceptionIsThrown()
    {
        await using var device = await CreateConnectedDeviceAsync();

        var act = () => device.SetCustomEqualizerAsync(0, [0, 0, 0, 0], CancellationToken.None);

        await Assert.That(act).Throws<ArgumentException>();
    }

    [Test]
    public async Task WhenPresetIsUndefinedThenArgumentOutOfRangeExceptionIsThrown()
    {
        await using var device = await CreateConnectedDeviceAsync();

        var act = () => device.SetEqualizerPresetAsync((EqualizerPreset)0x18, CancellationToken.None);

        await Assert.That(act).Throws<ArgumentOutOfRangeException>();
    }

    [Test]
    public async Task WhenReconnectedThenPreviousSettingsAreKept()
    {
        await using var device = await CreateConnectedDeviceAsync();
        await device.SetEqualizerPresetAsync(EqualizerPreset.BassBoost, CancellationToken.None);
        await device.DisconnectAsync(CancellationToken.None);

        await device.ConnectAsync(CancellationToken.None);

        await Assert.That(device.State.Equalizer?.Preset).IsEqualTo(EqualizerPreset.BassBoost);
    }

    [Test]
    public async Task WhenDisposedThenConnectThrowsObjectDisposedException()
    {
        var device = CreateDevice();
        await device.DisposeAsync();

        var act = () => device.ConnectAsync(CancellationToken.None);

        await Assert.That(act).Throws<ObjectDisposedException>();
    }
}
