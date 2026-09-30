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

        await device.SetNoiseControlAsync(requested, CancellationToken.None);
        await device.RefreshAsync(CancellationToken.None);

        await Assert.That(device.State.NoiseControl).IsEqualTo(requested);
    }

    [Test]
    public async Task WhenNoiseCancellingIsSetWithFocusOnVoiceThenTheEchoReportsItOff()
    {
        await using var device = await ConnectAsync(CreateConnector());
        await device.SetNoiseControlAsync(
            new NoiseControlState(NoiseControlMode.NoiseCancelling, FocusOnVoice: true, AmbientLevel: 5), CancellationToken.None);

        _time.Advance(EchoDelay);

        await WaitUntilAsync(() => device.State.NoiseControl?.FocusOnVoice == false);
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
        var commands = V2CommandSet.FromHandshake(ProtocolGeneration.V2, (await link.HandshakeAsync()).Span);

        await link.Session.SendAsync(commands.SetDsee(true).Payload, CancellationToken.None);
        _time.Advance(EchoDelay);

        await WaitUntilAsync(() => link.Notifications.Count > 0);
        await Assert.That(link.Notifications[0]).IsEqualTo("E90101");
    }

    [Test]
    public async Task WhenEqualizerPresetIsSetThenNoEchoFollows()
    {
        await using var link = await OpenLinkAsync(CreateConnector());
        var commands = V2CommandSet.FromHandshake(ProtocolGeneration.V2, (await link.HandshakeAsync()).Span);
        await link.Session.SendAsync(commands.SetEqualizerPreset(EqualizerPreset.Bright).Payload, CancellationToken.None);

        // The DSEE echo is a marker: any echo of the preset would be due no later and would arrive first.
        await link.Session.SendAsync(commands.SetDsee(true).Payload, CancellationToken.None);
        _time.Advance(EchoDelay);

        await WaitUntilAsync(() => link.Notifications.Count > 0);
        await Assert.That(link.Notifications[0]).IsEqualTo("E90101");
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

        await device.SetCustomEqualizerAsync(5, [0, 1, 2, 1, 0], CancellationToken.None);
        await device.RefreshAsync(CancellationToken.None);

        await Assert.That(Describe(device.State.Equalizer)).IsEqualTo("Manual/5/0,1,2,1,0");
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
        await using var device = CreateDevice(new SimulatedHeadsetConnector(TimeSpan.FromMilliseconds(1)));

        await device.ConnectAsync(CancellationToken.None);

        await Assert.That(device.State.FirmwareVersion).IsEqualTo("1.1.4");
    }

    private static HeadphoneDevice CreateDevice(IHeadsetConnector connector) =>
        new(SimulatedHeadsetConnector.HeadsetName, connector.ConnectPreferredAsync, NullLoggerFactory.Instance);

    private static string Describe(EqualizerState? equalizer) =>
        equalizer is null ? "null" : $"{equalizer.Preset}/{equalizer.ClearBass}/{string.Join(",", equalizer.Bands)}";

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + Patience;
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException("The expected simulator traffic did not arrive.");
            }

            await Task.Delay(5);
        }
    }

    private static async Task<Link> OpenLinkAsync(SimulatedHeadsetConnector connector)
    {
        var connection = await connector.ConnectPreferredAsync(CancellationToken.None);
        var link = new Link(new ProtocolSession(connection.Transport, NullLogger<ProtocolSession>.Instance));
        await link.Session.StartAsync(CancellationToken.None);
        return link;
    }

    private SimulatedHeadsetConnector CreateConnector() => new(TimeSpan.Zero, _time);

    private async Task<HeadphoneDevice> ConnectAsync(SimulatedHeadsetConnector connector)
    {
        var device = CreateDevice(connector);
        await device.ConnectAsync(CancellationToken.None);
        return device;
    }

    // A raw session over the simulated link, for traffic HeadphoneDevice does not expose.
    private sealed class Link : IAsyncDisposable
    {
        private readonly List<string> _notifications = [];

        public Link(ProtocolSession session)
        {
            Session = session;
            Session.NotificationReceived += OnNotification;
        }

        public ProtocolSession Session { get; }

        public IReadOnlyList<string> Notifications
        {
            get
            {
                lock (_notifications)
                {
                    return [.. _notifications];
                }
            }
        }

        public async Task<ReadOnlyMemory<byte>> HandshakeAsync()
        {
            var handshake = ProtocolHandshake.CreateRequest();
            return await Session.RequestAsync(
                handshake.Payload, handshake.ResponseOpcode!.Value, handshake.ResponseSubtype, CancellationToken.None);
        }

        public ValueTask DisposeAsync() => Session.DisposeAsync();

        private void OnNotification(object? sender, Frame frame)
        {
            lock (_notifications)
            {
                _notifications.Add(Convert.ToHexString(frame.Payload.Span));
            }
        }
    }
}
