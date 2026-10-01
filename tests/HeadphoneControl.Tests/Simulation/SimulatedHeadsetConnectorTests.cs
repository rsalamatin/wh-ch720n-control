using System.Threading.Channels;
using HeadphoneControl.Core;
using HeadphoneControl.Protocol.Commands;
using HeadphoneControl.Protocol.Devices;
using HeadphoneControl.Protocol.Framing;
using HeadphoneControl.Protocol.Session;
using HeadphoneControl.Protocol.Transport;
using HeadphoneControl.Simulation;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace HeadphoneControl.Tests.Simulation;

// Drives the real protocol stack against the simulator. Replies come at once; the echo notifications the headset
// sends after a SET wait for the fake clock.
public class SimulatedHeadsetConnectorTests
{
    private static readonly TimeSpan EchoDelay = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(5);

    private readonly FakeTimeProvider _time = new();

    [Test]
    public async Task WhenPairedHeadsetsAreListedThenOnlyTheSimulatedV2HeadsetIsFound()
    {
        var connector = CreateConnector();

        var headsets = await connector.FindPairedAsync(CancellationToken.None);

        await Assert.That(headsets.Select(h => $"{h.Name}/{h.Generation}"))
            .IsEquivalentTo([$"{SimulatedHeadsetConnector.HeadsetName}/V2"]);
    }

    [Test]
    public async Task WhenAForeignHeadsetIsConnectedThenTransportExceptionIsThrown()
    {
        var connector = CreateConnector();
        var foreign = new DiscoveredHeadset("WH-1000XM4", "other-device", ProtocolGeneration.V2);

        Func<Task> act = () => connector.ConnectAsync(foreign, CancellationToken.None);

        await Assert.That(act).Throws<TransportException>();
    }

    [Test]
    public async Task WhenHandshakeIsSentThenTheCapturedV2ReplyArrives()
    {
        await using var link = await OpenLinkAsync(CreateConnector());

        var reply = await link.HandshakeAsync();

        await Assert.That(Convert.ToHexString(reply.Span)).IsEqualTo("0100030010020000");
    }

    [Test]
    public async Task WhenConnectedThenDeviceIsConnectedAsV2()
    {
        await using var device = CreateDevice(CreateConnector());

        await device.ConnectAsync(CancellationToken.None);

        await Assert.That($"{device.State.Connection}/{device.State.Generation}").IsEqualTo("Connected/V2");
    }

    [Test]
    public async Task WhenConnectedThenDefaultSettingsAreRead()
    {
        await using var device = CreateDevice(CreateConnector());

        await device.ConnectAsync(CancellationToken.None);

        var state = device.State;
        await Assert.That(state.Battery).IsEqualTo(new BatteryState(80, IsCharging: false));
        await Assert.That(state.NoiseControl).IsEqualTo(new NoiseControlState(NoiseControlMode.NoiseCancelling, false, 0));
        await Assert.That(Describe(state.Equalizer)).IsEqualTo("Off/0/0,0,0,0,0");
        await Assert.That(state.DseeEnabled ?? true).IsFalse();
        await Assert.That(state.FirmwareVersion).IsEqualTo("1.1.4");
        await Assert.That(state.Codec).IsEqualTo(AudioCodec.Aac);
    }

    [Test]
    public async Task WhenNoiseControlIsSetThenTheHeadsetStoresIt()
    {
        await using var device = await ConnectAsync(CreateConnector());
        var requested = new NoiseControlState(NoiseControlMode.Ambient, FocusOnVoice: true, AmbientLevel: 17);

        await device.SetNoiseControlAsync(requested, EditPacing.Immediate, CancellationToken.None);
        await device.RefreshAsync(CancellationToken.None);

        await Assert.That(device.State.NoiseControl).IsEqualTo(requested);
    }

    [Test]
    public async Task WhenNoiseControlIsSetThenTheEchoMirrorsTheRequest()
    {
        await using var link = await OpenLinkAsync(CreateConnector());
        var commands = await link.ConfirmV2Async();
        var request = commands.SetNoiseControl(new NoiseControlState(NoiseControlMode.NoiseCancelling, true, 5));

        await link.Session.SendAsync(request.Payload, CancellationToken.None);
        _time.Advance(EchoDelay);

        var echo = await link.NextNotificationAsync();
        await Assert.That(echo).IsEqualTo("69" + Convert.ToHexString(request.Payload.Span)[2..]);
    }

    [Test]
    public async Task WhenDseeIsSetThenTheHeadsetStoresIt()
    {
        await using var device = await ConnectAsync(CreateConnector());

        await device.SetDseeAsync(true, CancellationToken.None);
        await device.RefreshAsync(CancellationToken.None);

        await Assert.That(device.State.DseeEnabled ?? false).IsTrue();
    }

    [Test]
    public async Task WhenDseeIsSetThenTheHeadsetEchoesTheNewValue()
    {
        await using var link = await OpenLinkAsync(CreateConnector());
        var commands = await link.ConfirmV2Async();

        await link.Session.SendAsync(commands.SetDsee(true).Payload, CancellationToken.None);
        _time.Advance(EchoDelay);

        await Assert.That(await link.NextNotificationAsync()).IsEqualTo("E90101");
    }

    [Test]
    public async Task WhenDseeIsSetThenTheCapturedE5FrameFollowsTheEcho()
    {
        await using var link = await OpenLinkAsync(CreateConnector());
        var commands = await link.ConfirmV2Async();
        await link.Session.SendAsync(commands.SetDsee(true).Payload, CancellationToken.None);
        _time.Advance(EchoDelay);
        await link.NextNotificationAsync();

        var next = await link.NextNotificationAsync();

        await Assert.That(next).IsEqualTo("E50100");
    }

    [Test]
    public async Task WhenEqualizerPresetIsSetThenNoEchoFollows()
    {
        await using var link = await OpenLinkAsync(CreateConnector());
        var commands = await link.ConfirmV2Async();
        await link.Session.SendAsync(commands.SetEqualizerPreset(EqualizerPreset.Bright).Payload, CancellationToken.None);

        // The DSEE echo is a marker: any echo of the preset would be due no later and would arrive first.
        await link.Session.SendAsync(commands.SetDsee(true).Payload, CancellationToken.None);
        _time.Advance(EchoDelay);

        await Assert.That(await link.NextNotificationAsync()).IsEqualTo("E90101");
    }

    [Test]
    public async Task WhenAnUnknownRequestIsSentThenTheHeadsetOnlyAcksIt()
    {
        await using var link = await OpenLinkAsync(CreateConnector());
        var commands = await link.ConfirmV2Async();

        // 26 05 is the auto power-off query the real headset only ACKs. A reply would arrive before the DSEE marker.
        await link.Session.SendAsync(new byte[] { 0x26, 0x05 }, CancellationToken.None);
        await link.Session.SendAsync(commands.SetDsee(true).Payload, CancellationToken.None);
        _time.Advance(EchoDelay);

        await Assert.That(await link.NextNotificationAsync()).IsEqualTo("E90101");
    }

    [Test]
    public async Task WhenPresetIsSetThenTheCapturedCurveIsReadBack()
    {
        await using var device = await ConnectAsync(CreateConnector());

        await device.SetEqualizerPresetAsync(EqualizerPreset.BassBoost, CancellationToken.None);

        await Assert.That(Describe(device.State.Equalizer)).IsEqualTo("BassBoost/7/0,0,0,0,0");
    }

    [Test]
    public async Task WhenCustomEqualizerIsSetThenTheHeadsetStoresAManualCurve()
    {
        await using var device = await ConnectAsync(CreateConnector());

        await device.SetCustomEqualizerAsync(5, [0, 1, 2, 1, 0], EditPacing.Immediate, CancellationToken.None);
        await device.RefreshAsync(CancellationToken.None);

        await Assert.That(Describe(device.State.Equalizer)).IsEqualTo("Manual/5/0,1,2,1,0");
    }

    [Test]
    public async Task WhenPresetOffFollowsACustomCurveThenTheBandsAreFlat()
    {
        await using var device = await ConnectAsync(CreateConnector());
        await device.SetCustomEqualizerAsync(4, [4, 4, 4, 4, 4], EditPacing.Immediate, CancellationToken.None);

        await device.SetEqualizerPresetAsync(EqualizerPreset.Off, CancellationToken.None);

        await Assert.That(Describe(device.State.Equalizer)).IsEqualTo("Off/0/0,0,0,0,0");
    }

    [Test]
    public async Task WhenReconnectedThenPreviousSettingsAreKept()
    {
        await using var device = await ConnectAsync(CreateConnector());
        await device.SetEqualizerPresetAsync(EqualizerPreset.Bright, CancellationToken.None);
        await device.DisconnectAsync(CancellationToken.None);

        await device.ConnectAsync(CancellationToken.None);

        await Assert.That(Describe(device.State.Equalizer)).IsEqualTo("Bright/-1/0,5,7,7,9");
    }

    [Test]
    public async Task WhenConnectFailureIsSimulatedThenConnectFails()
    {
        var connector = CreateConnector();
        connector.FailNextConnect = true;
        await using var device = CreateDevice(connector);

        await Assert.That(() => device.ConnectAsync(CancellationToken.None)).Throws<TransportException>();

        await Assert.That(device.State.Connection).IsEqualTo(ConnectionStatus.Failed);
    }

    [Test]
    public async Task WhenConnectFailedOnceThenNextConnectSucceeds()
    {
        var connector = CreateConnector();
        connector.FailNextConnect = true;
        await using var device = CreateDevice(connector);
        await Assert.That(() => device.ConnectAsync(CancellationToken.None)).Throws<TransportException>();

        await device.ConnectAsync(CancellationToken.None);

        await Assert.That(device.State.Connection).IsEqualTo(ConnectionStatus.Connected);
    }

    [Test]
    public async Task WhenConnectIsCancelledThenDeviceIsDisconnected()
    {
        await using var device = CreateDevice(new SimulatedHeadsetConnector(TimeSpan.FromSeconds(1), _time));
        using var cancellation = new CancellationTokenSource();
        var connect = device.ConnectAsync(cancellation.Token);

        await cancellation.CancelAsync();

        await Assert.That(() => connect).Throws<OperationCanceledException>();
        await Assert.That(device.State.Connection).IsEqualTo(ConnectionStatus.Disconnected);
    }

    [Test]
    public async Task WhenAChannelIsOpenThenASecondConnectIsRefused()
    {
        var connector = CreateConnector();
        await using var first = await OpenLinkAsync(connector);

        Func<Task> second = () => connector.ConnectPreferredAsync(CancellationToken.None);

        await Assert.That(second).Throws<TransportException>();
    }

    [Test]
    public async Task WhenTheChannelIsClosedThenItCanBeOpenedAgain()
    {
        var connector = CreateConnector();
        await (await OpenLinkAsync(connector)).DisposeAsync();

        await using var reopened = await OpenLinkAsync(connector);

        await Assert.That(Convert.ToHexString((await reopened.HandshakeAsync()).Span)).IsEqualTo("0100030010020000");
    }

    [Test]
    public async Task WhenRepliesAreDelayedThenConnectStillReadsEverySetting()
    {
        // System clock: the latency pump waits on real timers, which a fake clock would never fire.
        await using var device = CreateDevice(new SimulatedHeadsetConnector(TimeSpan.FromMilliseconds(1)));

        await device.ConnectAsync(CancellationToken.None);

        var state = device.State;
        await Assert.That(
                $"{state.Battery?.Level}/{state.NoiseControl?.Mode}/{state.Equalizer?.Preset}/{state.DseeEnabled}/{state.FirmwareVersion}/{state.Codec}")
            .IsEqualTo("80/NoiseCancelling/Off/False/1.1.4/Aac");
    }

    private static HeadsetController CreateDevice(IHeadsetConnector connector) =>
        new(SimulatedHeadsetConnector.HeadsetName, connector.ConnectPreferredAsync, NullLoggerFactory.Instance);

    private static string Describe(EqualizerState? equalizer) =>
        equalizer is null ? "null" : $"{equalizer.Preset}/{equalizer.ClearBass}/{string.Join(",", equalizer.Bands)}";

    private static async Task<Link> OpenLinkAsync(SimulatedHeadsetConnector connector)
    {
        var connection = await connector.ConnectPreferredAsync(CancellationToken.None);
        var link = new Link(new ProtocolSession(connection.Transport, NullLogger<ProtocolSession>.Instance));
        await link.Session.StartAsync(CancellationToken.None);
        return link;
    }

    private SimulatedHeadsetConnector CreateConnector() => new(TimeSpan.Zero, _time);

    private async Task<HeadsetController> ConnectAsync(SimulatedHeadsetConnector connector)
    {
        var device = CreateDevice(connector);
        await device.ConnectAsync(CancellationToken.None);
        return device;
    }

    private sealed class Link : IAsyncDisposable
    {
        private readonly Channel<string> _notifications = Channel.CreateUnbounded<string>();

        public Link(ProtocolSession session)
        {
            Session = session;
            Session.NotificationReceived += OnNotification;
        }

        public ProtocolSession Session { get; }

        public async Task<ReadOnlyMemory<byte>> HandshakeAsync()
        {
            var handshake = ProtocolHandshake.CreateRequest();
            var reply = await Session.RequestAsync(
                handshake.Payload, handshake.ResponseOpcode!.Value, handshake.ResponseSubtype, CancellationToken.None);
            return reply.Payload;
        }

        public async Task<V2CommandSet> ConfirmV2Async() =>
            V2CommandSet.FromHandshake(ProtocolGeneration.V2, (await HandshakeAsync()).Span);

        public Task<string> NextNotificationAsync() =>
            _notifications.Reader.ReadAsync().AsTask().WaitAsync(Patience);

        public ValueTask DisposeAsync() => Session.DisposeAsync();

        private void OnNotification(object? sender, ReceivedPayload notification) =>
            _notifications.Writer.TryWrite(Convert.ToHexString(notification.Payload.Span));
    }
}
