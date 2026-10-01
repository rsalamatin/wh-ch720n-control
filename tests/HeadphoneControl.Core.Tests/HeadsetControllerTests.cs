using HeadphoneControl.Protocol.Devices;
using HeadphoneControl.Protocol.Session;
using HeadphoneControl.Protocol.Transport;
using HeadphoneControl.Testing;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using TUnit.Assertions.Enums;

namespace HeadphoneControl.Core.Tests;

public class HeadsetControllerTests
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(5);

    [Test]
    public async Task WhenHeadsetConfirmsV2ThenStateIsConnected()
    {
        var headset = new FakeHeadset();
        await using var controller = CreateController(headset);

        await controller.ConnectAsync(CancellationToken.None);

        await Assert.That(controller.State.Connection).IsEqualTo(ConnectionStatus.Connected);
    }

    [Test]
    public async Task WhenConnectedThenGenerationIsV2()
    {
        var headset = new FakeHeadset();
        await using var controller = CreateController(headset);

        await controller.ConnectAsync(CancellationToken.None);

        await Assert.That(controller.State.Generation).IsEqualTo(ProtocolGeneration.V2);
    }

    [Test]
    public async Task WhenConnectedThenBatteryIsRead()
    {
        var headset = new FakeHeadset();
        await using var controller = CreateController(headset);

        await controller.ConnectAsync(CancellationToken.None);

        await Assert.That(controller.State.Battery).IsEqualTo(new BatteryState(80, true));
    }

    [Test]
    public async Task WhenConnectedThenNoiseControlIsRead()
    {
        var headset = new FakeHeadset();
        await using var controller = CreateController(headset);

        await controller.ConnectAsync(CancellationToken.None);

        await Assert.That(controller.State.NoiseControl).IsEqualTo(new NoiseControlState(NoiseControlMode.Ambient, false, 8));
    }

    [Test]
    public async Task WhenConnectedThenFirmwareVersionIsRead()
    {
        var headset = new FakeHeadset();
        await using var controller = CreateController(headset);

        await controller.ConnectAsync(CancellationToken.None);

        await Assert.That(controller.State.FirmwareVersion).IsEqualTo("1.0.2");
    }

    [Test]
    public async Task WhenConnectingThenHandshakeIsTheFirstFrameSent()
    {
        var headset = new FakeHeadset();
        await using var controller = CreateController(headset);

        await controller.ConnectAsync(CancellationToken.None);

        await Assert.That(Convert.ToHexString(headset.SentPayloads()[0])).IsEqualTo("0000");
    }

    [Test]
    [Arguments(ProtocolGeneration.V1)]
    [Arguments(ProtocolGeneration.Unknown)]
    public async Task WhenTransportServiceIsNotV2ThenConnectThrowsNotSupported(ProtocolGeneration service)
    {
        var headset = new FakeHeadset { ServiceGeneration = service };
        await using var controller = CreateController(headset);

        await Assert.That(() => controller.ConnectAsync(CancellationToken.None)).Throws<NotSupportedException>();
    }

    // Opcode 0x22 is BATTERY on V2 but POWER OFF on V1, so nothing but the handshake may reach a non-V2 device.
    [Test]
    [Arguments(ProtocolGeneration.V1)]
    [Arguments(ProtocolGeneration.Unknown)]
    public async Task WhenTransportServiceIsNotV2ThenOnlyTheHandshakeIsSent(ProtocolGeneration service)
    {
        var headset = new FakeHeadset { ServiceGeneration = service };
        await using var controller = CreateController(headset);

        await ConnectIgnoringFailureAsync(controller);

        await Assert.That(headset.SentPayloads().Select(Convert.ToHexString)).IsEquivalentTo(["0000"]);
    }

    [Test]
    public async Task WhenInitReplyIsV1ShapedThenOnlyTheHandshakeIsSent()
    {
        var headset = new FakeHeadset { InitReply = [0x01, 0x00, 0x01, 0x00, 0x00, 0x00, 0x00, 0x00] };
        await using var controller = CreateController(headset);

        await ConnectIgnoringFailureAsync(controller);

        await Assert.That(headset.SentPayloads().Select(Convert.ToHexString)).IsEquivalentTo(["0000"]);
    }

    [Test]
    public async Task WhenGenerationIsRejectedThenStateIsFailed()
    {
        var headset = new FakeHeadset { ServiceGeneration = ProtocolGeneration.V1 };
        await using var controller = CreateController(headset);

        await ConnectIgnoringFailureAsync(controller);

        await Assert.That(controller.State.Connection).IsEqualTo(ConnectionStatus.Failed);
    }

    [Test]
    public async Task WhenGenerationIsRejectedThenTransportIsReleased()
    {
        var headset = new FakeHeadset { ServiceGeneration = ProtocolGeneration.V1 };
        await using var controller = CreateController(headset);

        await ConnectIgnoringFailureAsync(controller);

        await Assert.That(headset.Transport.IsDisposed).IsTrue();
    }

    [Test]
    public async Task WhenTransportCannotConnectThenConnectThrowsTransportException()
    {
        await using var controller = new HeadsetController(
            "WH-CH720N",
            _ => throw new TransportException("Headset not in range."),
            NullLoggerFactory.Instance);

        await Assert.That(() => controller.ConnectAsync(CancellationToken.None)).Throws<TransportException>();
    }

    [Test]
    public async Task WhenQueryIsUnansweredThenThatSettingStaysUnknown()
    {
        var time = new FakeTimeProvider();
        var headset = new FakeHeadset();
        headset.Replies.Remove(0xE6);
        await using var controller = CreateController(headset, time);

        await ConnectPastTimeoutAsync(controller, headset, time, 0xE6);

        await Assert.That(controller.State.DseeEnabled).IsNull();
    }

    [Test]
    public async Task WhenQueryIsUnansweredThenControllerStillConnects()
    {
        var time = new FakeTimeProvider();
        var headset = new FakeHeadset();
        headset.Replies.Remove(0xE6);
        await using var controller = CreateController(headset, time);

        await ConnectPastTimeoutAsync(controller, headset, time, 0xE6);

        await Assert.That(controller.State.Connection).IsEqualTo(ConnectionStatus.Connected);
    }

    [Test]
    public async Task WhenQueryReplyIsMalformedThenThatSettingStaysUnknown()
    {
        var headset = new FakeHeadset();
        headset.Replies[0x22] = [0x23, 0x00, 0xFF, 0x00];
        await using var controller = CreateController(headset);

        await controller.ConnectAsync(CancellationToken.None);

        await Assert.That(controller.State.Battery).IsNull();
    }

    [Test]
    public async Task WhenHandshakeIsUnansweredThenConnectThrowsTimeout()
    {
        var time = new FakeTimeProvider();
        var headset = new FakeHeadset { InitReply = null };
        await using var controller = CreateController(headset, time);

        var connect = controller.ConnectAsync(CancellationToken.None);
        await headset.WaitUntilSentAsync(0x00, Patience);
        time.Advance(ProtocolSession.DefaultTimeout);

        await Assert.That(() => connect.WaitAsync(Patience)).Throws<TimeoutException>();
    }

    [Test]
    public async Task WhenHandshakeIsUnansweredThenTransportIsReleased()
    {
        var time = new FakeTimeProvider();
        var headset = new FakeHeadset { InitReply = null };
        await using var controller = CreateController(headset, time);
        var connect = controller.ConnectAsync(CancellationToken.None);
        await headset.WaitUntilSentAsync(0x00, Patience);

        time.Advance(ProtocolSession.DefaultTimeout);
        await IgnoreFailureAsync(connect);

        await Assert.That(headset.Transport.IsDisposed).IsTrue();
    }

    [Test]
    public async Task WhenInitReplyIsShortThenConnectThrowsNotSupported()
    {
        var headset = new FakeHeadset { InitReply = [0x01, 0x00] };
        await using var controller = CreateController(headset);

        await Assert.That(() => controller.ConnectAsync(CancellationToken.None)).Throws<NotSupportedException>();
    }

    [Test]
    public async Task WhenLinkDropsDuringConnectThenStateIsFailed()
    {
        var headset = new FakeHeadset { DropLinkAfter = 0x00 };
        await using var controller = CreateController(headset);

        await IgnoreFailureAsync(controller.ConnectAsync(CancellationToken.None));

        await Assert.That(controller.State.Connection).IsEqualTo(ConnectionStatus.Failed);
    }

    [Test]
    public async Task WhenConnectorThrowsUnexpectedExceptionThenConnectThrowsTransportException()
    {
        await using var controller = new HeadsetController(
            "WH-CH720N", _ => throw new NotImplementedException("E_NOTIMPL"), NullLoggerFactory.Instance);

        await Assert.That(() => controller.ConnectAsync(CancellationToken.None)).Throws<TransportException>();
    }

    // Smoke test: nothing observable runs between ConnectAsync's link check and its final publish, so it cannot
    // force the drop into that window.
    [Test]
    public async Task WhenLinkDropsAfterTheLastConnectQueryThenStateSettlesNotConnected()
    {
        var headset = new FakeHeadset { DropLinkAfter = 0x12 };
        await using var controller = CreateController(headset);
        var settled = NextStateAsync(
            controller, state => state.Connection is ConnectionStatus.Failed or ConnectionStatus.Disconnected);

        await IgnoreFailureAsync(controller.ConnectAsync(CancellationToken.None));
        await settled;

        await Assert.That(controller.State.Connection).IsNotEqualTo(ConnectionStatus.Connected);
    }

    [Test]
    public async Task WhenDisposedDuringConnectThenConnectThrowsObjectDisposed()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var controller = CreateHangingController(entered);
        var connect = controller.ConnectAsync(CancellationToken.None);
        await entered.Task.WaitAsync(Patience);

        await controller.DisposeAsync().AsTask().WaitAsync(Patience);

        await Assert.That(() => connect.WaitAsync(Patience)).Throws<ObjectDisposedException>();
    }

    [Test]
    public async Task WhenDisposedDuringConnectThenStateIsDisconnected()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var controller = CreateHangingController(entered);
        var connect = controller.ConnectAsync(CancellationToken.None);
        await entered.Task.WaitAsync(Patience);

        await controller.DisposeAsync().AsTask().WaitAsync(Patience);
        await IgnoreDisposedAsync(connect);

        await Assert.That(controller.State).IsEqualTo(DeviceState.Disconnected);
    }

    [Test]
    public async Task WhenConnectorIsCancelledWithoutTheCallerThenConnectThrowsTransportException()
    {
        await using var controller = new HeadsetController(
            "WH-CH720N", _ => throw new OperationCanceledException(), NullLoggerFactory.Instance);

        await Assert.That(() => controller.ConnectAsync(CancellationToken.None)).Throws<TransportException>();
    }

    [Test]
    public async Task WhenEqualizerReQueryIsUnansweredThenEqualizerIsUnknown()
    {
        var time = new FakeTimeProvider();
        var headset = new FakeHeadset();
        await using var controller = CreateController(headset, time);
        await controller.ConnectAsync(CancellationToken.None);
        headset.Replies.Remove(0x56);

        var set = controller.SetEqualizerPresetAsync(EqualizerPreset.BassBoost, CancellationToken.None);
        await headset.WaitUntilSentAsync(0x56, Patience, times: 2);
        time.Advance(ProtocolSession.DefaultTimeout);
        await set.WaitAsync(Patience);

        await Assert.That(controller.State.Equalizer).IsNull();
    }

    [Test]
    public async Task WhenEqualizerReplyArrivesAfterTheReQueryTimedOutThenTheLateCurveIsKept()
    {
        var time = new FakeTimeProvider();
        var headset = new FakeHeadset();
        var loggers = new HookedLoggerFactory();
        await using var controller = new HeadsetController("WH-CH720N", headset.ConnectAsync, loggers, time);
        await controller.ConnectAsync(CancellationToken.None);
        headset.Replies.Remove(0x56);
        var lateCurveApplied = NextStateAsync(
            controller, state => state.Equalizer?.Bands.SequenceEqual([6, 4, 0, 0, 0]) == true);

        // The controller logs this after the re-query gave up and before it publishes the unknown curve, so the late
        // reply arrives exactly where an unconditional publish would wipe it.
        loggers.OnLog = message =>
        {
            if (message.StartsWith("Equalizer of", StringComparison.Ordinal))
            {
                headset.Notify(0x57, 0x00, 0x16, 0x06, 10, 16, 14, 10, 10, 10);
            }
        };
        var set = controller.SetEqualizerPresetAsync(EqualizerPreset.BassBoost, CancellationToken.None);
        await headset.WaitUntilSentAsync(0x56, Patience, times: 2);
        time.Advance(ProtocolSession.DefaultTimeout);
        await set.WaitAsync(Patience);
        await lateCurveApplied;

        await Assert.That(controller.State.Equalizer!.Bands).IsEquivalentTo([6, 4, 0, 0, 0]);
    }

    [Test]
    public async Task WhenCallerCancelsAfterPresetIsAcknowledgedThenEqualizerCurveIsStillReRead()
    {
        var headset = new FakeHeadset();
        await using var controller = CreateController(headset);
        await controller.ConnectAsync(CancellationToken.None);
        headset.Replies.Remove(0x56);
        using var cancellation = new CancellationTokenSource();

        var set = controller.SetEqualizerPresetAsync(EqualizerPreset.BassBoost, cancellation.Token);
        await headset.WaitUntilSentAsync(0x56, Patience, times: 2);
        await cancellation.CancelAsync();
        headset.Notify(0x57, 0x00, 0x16, 0x06, 10, 16, 14, 10, 10, 10);
        await set.WaitAsync(Patience);

        await Assert.That(controller.State.Equalizer!.Bands).IsEquivalentTo([6, 4, 0, 0, 0]);
    }

    [Test]
    public async Task WhenPresetIsSetThenEqualizerCurveIsReRead()
    {
        var headset = new FakeHeadset();
        await using var controller = CreateController(headset);
        await controller.ConnectAsync(CancellationToken.None);
        headset.Replies[0x56] = [0x57, 0x00, 0x16, 0x06, 10, 16, 14, 10, 10, 10];

        await controller.SetEqualizerPresetAsync(EqualizerPreset.BassBoost, CancellationToken.None);

        await Assert.That(controller.State.Equalizer!.Bands).IsEquivalentTo([6, 4, 0, 0, 0]);
    }

    [Test]
    public async Task WhenLinkIsLostThenSettingThrowsInvalidOperation()
    {
        var headset = new FakeHeadset();
        await using var controller = CreateController(headset);
        await controller.ConnectAsync(CancellationToken.None);
        var disconnected = NextStateAsync(controller, state => state.Connection == ConnectionStatus.Disconnected);
        headset.Transport.SimulateEof();
        await disconnected;

        await Assert.That(() => controller.SetDseeAsync(true, CancellationToken.None)).Throws<InvalidOperationException>();
    }

    [Test]
    public async Task WhenDisposedThenTransportIsReleased()
    {
        var headset = new FakeHeadset();
        var controller = CreateController(headset);
        await controller.ConnectAsync(CancellationToken.None);

        await controller.DisposeAsync();

        await Assert.That(headset.Transport.IsDisposed).IsTrue();
    }

    [Test]
    public async Task WhenNotConnectedThenSettingThrowsInvalidOperation()
    {
        var headset = new FakeHeadset();
        await using var controller = CreateController(headset);

        await Assert.That(() => controller.SetDseeAsync(true, CancellationToken.None)).Throws<InvalidOperationException>();
    }

    [Test]
    public async Task WhenDseeIsSetThenCommandIsSent()
    {
        var headset = new FakeHeadset();
        await using var controller = CreateController(headset);
        await controller.ConnectAsync(CancellationToken.None);

        await controller.SetDseeAsync(false, CancellationToken.None);

        await Assert.That(Convert.ToHexString(headset.SentPayloads()[^1])).IsEqualTo("E80100");
    }

    [Test]
    public async Task WhenDseeIsAcknowledgedThenStateIsUpdated()
    {
        var headset = new FakeHeadset();
        await using var controller = CreateController(headset);
        await controller.ConnectAsync(CancellationToken.None);

        await controller.SetDseeAsync(false, CancellationToken.None);

        await Assert.That(controller.State.DseeEnabled).IsFalse();
    }

    [Test]
    public async Task WhenNoiseControlIsSetOutsideAmbientThenStoredLevelIsAtLeastOne()
    {
        var headset = new FakeHeadset();
        await using var controller = CreateController(headset);
        await controller.ConnectAsync(CancellationToken.None);

        await controller.SetNoiseControlAsync(
            new NoiseControlState(NoiseControlMode.NoiseCancelling, false, 0), EditPacing.Immediate, CancellationToken.None);

        await Assert.That(controller.State.NoiseControl!.AmbientLevel).IsEqualTo(1);
    }

    [Test]
    public async Task WhenBatteryNotificationArrivesThenStateIsUpdated()
    {
        var headset = new FakeHeadset();
        await using var controller = CreateController(headset);
        await controller.ConnectAsync(CancellationToken.None);
        var changed = NextStateAsync(controller, state => state.Battery?.Level == 42);

        headset.Notify(0x25, 0x00, 42, 0);

        await Assert.That((await changed).Battery).IsEqualTo(new BatteryState(42, false));
    }

    [Test]
    public async Task WhenMalformedNotificationArrivesThenLaterNotificationsAreApplied()
    {
        var headset = new FakeHeadset();
        await using var controller = CreateController(headset);
        await controller.ConnectAsync(CancellationToken.None);
        var changed = NextStateAsync(controller, state => state.Battery?.Level == 42);

        headset.Notify(0x25, 0x00);
        headset.Notify(0x25, 0x00, 42, 0);

        await Assert.That((await changed).Battery!.Level).IsEqualTo(42);
    }

    [Test]
    public async Task WhenLinkDropsThenStateIsDisconnected()
    {
        var headset = new FakeHeadset();
        await using var controller = CreateController(headset);
        await controller.ConnectAsync(CancellationToken.None);
        var changed = NextStateAsync(controller, state => state.Connection == ConnectionStatus.Disconnected);

        headset.Transport.SimulateEof();

        await Assert.That((await changed).Connection).IsEqualTo(ConnectionStatus.Disconnected);
    }

    [Test]
    public async Task WhenDisconnectedThenTransportIsReleased()
    {
        var headset = new FakeHeadset();
        await using var controller = CreateController(headset);
        await controller.ConnectAsync(CancellationToken.None);

        await controller.DisconnectAsync(CancellationToken.None);

        await Assert.That(headset.Transport.IsDisposed).IsTrue();
    }

    [Test]
    public async Task WhenReconnectingThenANewTransportIsOpened()
    {
        var headsets = new Queue<FakeHeadset>([new FakeHeadset(), new FakeHeadset()]);
        var second = headsets.Last();
        await using var controller = new HeadsetController(
            "WH-CH720N", ct => headsets.Dequeue().ConnectAsync(ct), NullLoggerFactory.Instance);
        await controller.ConnectAsync(CancellationToken.None);
        await controller.DisconnectAsync(CancellationToken.None);

        await controller.ConnectAsync(CancellationToken.None);

        await Assert.That(Convert.ToHexString(second.SentPayloads()[0])).IsEqualTo("0000");
    }

    [Test]
    public async Task WhenLinkDropsThenTransportIsReleased()
    {
        var headset = new FakeHeadset();
        await using var controller = CreateController(headset);
        await controller.ConnectAsync(CancellationToken.None);
        var disconnected = NextStateAsync(controller, state => state.Connection == ConnectionStatus.Disconnected);

        headset.Transport.SimulateEof();
        await disconnected;

        await Assert.That(headset.Transport.IsDisposed).IsTrue();
    }

    [Test]
    public async Task WhenNotificationArrivesWhileConnectingThenItOverridesTheEarlierReply()
    {
        var time = new FakeTimeProvider();
        var headset = new FakeHeadset();
        headset.Replies.Remove(0x12);
        await using var controller = CreateController(headset, time);
        var notified = NextStateAsync(
            controller, state => state is { Connection: ConnectionStatus.Connected, Battery.Level: 42 });

        var connect = controller.ConnectAsync(CancellationToken.None);
        await headset.WaitUntilSentAsync(0x12, Patience);
        headset.Notify(0x25, 0x00, 42, 0);
        time.Advance(ProtocolSession.DefaultTimeout);
        await connect.WaitAsync(Patience);

        await Assert.That((await notified).Battery).IsEqualTo(new BatteryState(42, false));
    }

    // The echo is received before the SET's ACK but queued behind the SET operation, so it is applied after it.
    [Test]
    public async Task WhenOlderNoiseControlEchoIsAppliedAfterANewerSetThenTheSetValueIsKept()
    {
        var headset = new FakeHeadset();
        await using var controller = CreateController(headset);
        await controller.ConnectAsync(CancellationToken.None);
        var writeHung = headset.Transport.HangNextWrite();
        var set = controller.SetNoiseControlAsync(
            new NoiseControlState(NoiseControlMode.NoiseCancelling, false, 5), EditPacing.Immediate, CancellationToken.None);
        await writeHung.WaitAsync(Patience);

        headset.Notify(0x69, 0x17, 0x01, 0x01, 0x01, 0x00, 3);
        headset.Transport.ReleaseHungWrite();
        await set.WaitAsync(Patience);
        await ProcessQueuedNotificationsAsync(controller, headset);

        await Assert.That(controller.State.NoiseControl!.Mode).IsEqualTo(NoiseControlMode.NoiseCancelling);
    }

    // A late Bright curve, e.g. the reply to an earlier timed-out query, is received before the BassBoost re-read.
    [Test]
    public async Task WhenOlderEqualizerCurveIsAppliedAfterAPresetReReadThenTheReReadCurveIsKept()
    {
        var headset = new FakeHeadset();
        await using var controller = CreateController(headset);
        await controller.ConnectAsync(CancellationToken.None);
        headset.Replies[0x56] = [0x57, 0x00, 0x16, 0x06, 10, 16, 14, 10, 10, 10];
        var writeHung = headset.Transport.HangNextWrite();
        var set = controller.SetEqualizerPresetAsync(EqualizerPreset.BassBoost, CancellationToken.None);
        await writeHung.WaitAsync(Patience);

        headset.Notify(0x57, 0x00, 0x10, 0x06, 10, 11, 12, 13, 14, 15);
        headset.Transport.ReleaseHungWrite();
        await set.WaitAsync(Patience);
        await ProcessQueuedNotificationsAsync(controller, headset);

        await Assert.That(controller.State.Equalizer!.Preset).IsEqualTo(EqualizerPreset.BassBoost);
    }

    [Test]
    public async Task WhenStateChangedHandlerThrowsThenLaterOperationsStillComplete()
    {
        var headset = new FakeHeadset();
        await using var controller = CreateController(headset);
        await controller.ConnectAsync(CancellationToken.None);
        var thrown = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        controller.StateChanged += (_, state) =>
        {
            if (state.Battery?.Level == 42 && thrown.TrySetResult())
            {
                throw new InvalidOperationException("Handler failure.");
            }
        };
        headset.Notify(0x25, 0x00, 42, 0);
        await thrown.Task.WaitAsync(Patience);

        var act = () => controller.SetDseeAsync(false, CancellationToken.None).WaitAsync(Patience);

        await Assert.That(act).ThrowsNothing();
    }

    [Test]
    public async Task WhenStateChangedHandlerThrowsDuringASetThenTheSetSucceeds()
    {
        var headset = new FakeHeadset();
        await using var controller = CreateController(headset);
        await controller.ConnectAsync(CancellationToken.None);
        controller.StateChanged += (_, _) => throw new InvalidOperationException("Handler failure.");

        var act = () => controller.SetDseeAsync(false, CancellationToken.None).WaitAsync(Patience);

        await Assert.That(act).ThrowsNothing();
    }

    [Test]
    public async Task WhenStateChangedHandlerThrowsDuringConnectThenStateIsConnected()
    {
        var headset = new FakeHeadset();
        await using var controller = CreateController(headset);
        controller.StateChanged += (_, _) => throw new InvalidOperationException("Handler failure.");

        await controller.ConnectAsync(CancellationToken.None);

        await Assert.That(controller.State.Connection).IsEqualTo(ConnectionStatus.Connected);
    }

    [Test]
    public async Task WhenTokenIsAlreadyCancelledThenConnectThrowsOperationCanceled()
    {
        var headset = new FakeHeadset();
        await using var controller = CreateController(headset);

        var act = () => controller.ConnectAsync(new CancellationToken(canceled: true)).WaitAsync(Patience);

        await Assert.That(act).Throws<OperationCanceledException>();
    }

    [Test]
    public async Task WhenTokenIsAlreadyCancelledThenStateNeverChanges()
    {
        var headset = new FakeHeadset();
        await using var controller = CreateController(headset);
        var changes = 0;
        controller.StateChanged += (_, _) => Interlocked.Increment(ref changes);

        await IgnoreCancelledAsync(controller.ConnectAsync(new CancellationToken(canceled: true)));

        await Assert.That(changes).IsEqualTo(0);
    }

    [Test]
    public async Task WhenConnectSucceedsThenStatusGoesFromConnectingToConnected()
    {
        var headset = new FakeHeadset();
        await using var controller = CreateController(headset);
        var statuses = new List<ConnectionStatus>();
        controller.StateChanged += (_, state) => statuses.Add(state.Connection);

        await controller.ConnectAsync(CancellationToken.None);

        await Assert.That(statuses).IsEquivalentTo(
            [ConnectionStatus.Connecting, ConnectionStatus.Connected], CollectionOrdering.Matching);
    }

    [Test]
    public async Task WhenCreatedThenStateIsDisconnected()
    {
        var headset = new FakeHeadset();

        await using var controller = CreateController(headset);

        await Assert.That(controller.State).IsEqualTo(DeviceState.Disconnected);
    }

    [Test]
    public async Task WhenDebouncedEditsComeInABurstThenOnlyOneIsSent()
    {
        var time = new FakeTimeProvider();
        var headset = new FakeHeadset();
        await using var controller = CreateController(headset, time);
        await controller.ConnectAsync(CancellationToken.None);

        var edits = SendBandBurst(controller, [1, 2, 3]);
        time.Advance(controller.EditDebounce);
        await Task.WhenAll(edits).WaitAsync(Patience);

        await Assert.That(headset.SentPayloads().Count(payload => payload[0] == 0x58)).IsEqualTo(1);
    }

    [Test]
    public async Task WhenDebouncedEditsComeInABurstThenTheLastValueIsApplied()
    {
        var time = new FakeTimeProvider();
        var headset = new FakeHeadset();
        await using var controller = CreateController(headset, time);
        await controller.ConnectAsync(CancellationToken.None);

        var edits = SendBandBurst(controller, [1, 2, 3]);
        time.Advance(controller.EditDebounce);
        await Task.WhenAll(edits).WaitAsync(Patience);

        await Assert.That(controller.State.Equalizer!.Bands[0]).IsEqualTo(3);
    }

    [Test]
    public async Task WhenDebouncedEditIsWaitingThenItsGroupHasAPendingEdit()
    {
        var time = new FakeTimeProvider();
        var headset = new FakeHeadset();
        await using var controller = CreateController(headset, time);
        await controller.ConnectAsync(CancellationToken.None);

        _ = SendBandBurst(controller, [4]);

        await Assert.That(controller.HasPendingEdit(SettingGroup.Equalizer)).IsTrue();
    }

    [Test]
    public async Task WhenDebouncedEditIsWaitingThenOtherGroupsHaveNoPendingEdit()
    {
        var time = new FakeTimeProvider();
        var headset = new FakeHeadset();
        await using var controller = CreateController(headset, time);
        await controller.ConnectAsync(CancellationToken.None);

        _ = SendBandBurst(controller, [4]);

        await Assert.That(controller.HasPendingEdit(SettingGroup.NoiseControl)).IsFalse();
    }

    [Test]
    public async Task WhenEditCompletesThenItsGroupHasNoPendingEdit()
    {
        var headset = new FakeHeadset();
        await using var controller = CreateController(headset);
        await controller.ConnectAsync(CancellationToken.None);

        await controller.SetDseeAsync(false, CancellationToken.None);

        await Assert.That(controller.HasPendingEdit(SettingGroup.Dsee)).IsFalse();
    }

    [Test]
    public async Task WhenEditFailsThenItsGroupHasNoPendingEdit()
    {
        var headset = new FakeHeadset();
        await using var controller = CreateController(headset);

        await IgnoreNotConnectedAsync(controller.SetDseeAsync(false, CancellationToken.None));

        await Assert.That(controller.HasPendingEdit(SettingGroup.Dsee)).IsFalse();
    }

    [Test]
    public async Task WhenDebouncedEditIsCancelledThenNothingIsSent()
    {
        var time = new FakeTimeProvider();
        var headset = new FakeHeadset();
        await using var controller = CreateController(headset, time);
        await controller.ConnectAsync(CancellationToken.None);
        using var cancellation = new CancellationTokenSource();
        var edit = controller.SetCustomEqualizerAsync(0, [1, 0, 0, 0, 0], EditPacing.Debounced, cancellation.Token);

        await cancellation.CancelAsync();
        await IgnoreCancelledAsync(edit);
        time.Advance(controller.EditDebounce);

        await Assert.That(headset.SentPayloads().Any(payload => payload[0] == 0x58)).IsFalse();
    }

    // The first SET is on the wire when the next two are queued, so the middle one is replaced before its turn.
    [Test]
    public async Task WhenNewerEditOfTheGroupIsQueuedThenTheOlderQueuedEditIsNotSent()
    {
        var headset = new FakeHeadset();
        await using var controller = CreateController(headset);
        await controller.ConnectAsync(CancellationToken.None);
        var writeHung = headset.Transport.HangNextWrite();
        var first = controller.SetDseeAsync(false, CancellationToken.None);
        await writeHung.WaitAsync(Patience);

        var older = controller.SetDseeAsync(true, CancellationToken.None);
        var newer = controller.SetDseeAsync(false, CancellationToken.None);
        headset.Transport.ReleaseHungWrite();
        await Task.WhenAll(first, older, newer).WaitAsync(Patience);

        await Assert.That(headset.SentPayloads().Where(payload => payload[0] == 0xE8).Select(Convert.ToHexString))
            .IsEquivalentTo(["E80100", "E80100"], CollectionOrdering.Matching);
    }

    [Test]
    public async Task WhenOlderQueuedEditIsReplacedThenItCompletesAsSuperseded()
    {
        var headset = new FakeHeadset();
        await using var controller = CreateController(headset);
        await controller.ConnectAsync(CancellationToken.None);
        var writeHung = headset.Transport.HangNextWrite();
        var first = controller.SetDseeAsync(false, CancellationToken.None);
        await writeHung.WaitAsync(Patience);

        var older = controller.SetDseeAsync(true, CancellationToken.None);
        var newer = controller.SetDseeAsync(false, CancellationToken.None);
        headset.Transport.ReleaseHungWrite();
        await Task.WhenAll(first, older, newer).WaitAsync(Patience);

        await Assert.That(older.Result).IsEqualTo(EditOutcome.Superseded);
    }

    [Test]
    public async Task WhenEditIsAcknowledgedThenItCompletesAsApplied()
    {
        var headset = new FakeHeadset();
        await using var controller = CreateController(headset);
        await controller.ConnectAsync(CancellationToken.None);

        var outcome = await controller.SetDseeAsync(false, CancellationToken.None);

        await Assert.That(outcome).IsEqualTo(EditOutcome.Applied);
    }

    // The connect is cancelled while both edits wait behind it, so neither finds a link.
    [Test]
    public async Task WhenReplacedEditFindsNoLinkThenItCompletesAsSuperseded()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var controller = CreateHangingController(entered);
        using var connectCancellation = new CancellationTokenSource();
        var connect = controller.ConnectAsync(connectCancellation.Token);
        await entered.Task.WaitAsync(Patience);

        var older = controller.SetDseeAsync(true, CancellationToken.None);
        var newer = controller.SetDseeAsync(false, CancellationToken.None);
        await connectCancellation.CancelAsync();
        await IgnoreCancelledAsync(connect);
        await IgnoreNotConnectedAsync(newer);

        await Assert.That(await older.WaitAsync(Patience)).IsEqualTo(EditOutcome.Superseded);
    }

    [Test]
    public async Task WhenDebouncedCurveIsReplacedByAPresetThenTheCurveIsNotSent()
    {
        var time = new FakeTimeProvider();
        var headset = new FakeHeadset();
        await using var controller = CreateController(headset, time);
        await controller.ConnectAsync(CancellationToken.None);

        var curve = controller.SetCustomEqualizerAsync(0, [5, 0, 0, 0, 0], EditPacing.Debounced, CancellationToken.None);
        await controller.SetEqualizerPresetAsync(EqualizerPreset.BassBoost, CancellationToken.None);
        time.Advance(controller.EditDebounce);
        await curve.WaitAsync(Patience);

        await Assert.That(headset.SentPayloads().Any(payload => payload[0] == 0x58 && payload[2] == 0xA0)).IsFalse();
    }

    [Test]
    public async Task WhenEditIsQueuedBehindARunningOperationThenItsGroupHasAPendingEdit()
    {
        var headset = new FakeHeadset();
        await using var controller = CreateController(headset);
        await controller.ConnectAsync(CancellationToken.None);
        var writeHung = headset.Transport.HangNextWrite();
        var running = controller.SetNoiseControlAsync(
            new NoiseControlState(NoiseControlMode.Off, false, 1), EditPacing.Immediate, CancellationToken.None);
        await writeHung.WaitAsync(Patience);

        var queued = controller.SetDseeAsync(false, CancellationToken.None);

        try
        {
            await Assert.That(controller.HasPendingEdit(SettingGroup.Dsee)).IsTrue();
        }
        finally
        {
            headset.Transport.ReleaseHungWrite();
            await Task.WhenAll(running, queued).WaitAsync(Patience);
        }
    }

    [Test]
    public async Task WhenTheLastPendingEditEndsThenStateChangedIsRaisedWithNothingPending()
    {
        var headset = new FakeHeadset();
        await using var controller = CreateController(headset);
        await controller.ConnectAsync(CancellationToken.None);
        var pendingAtEachChange = new List<bool>();
        controller.StateChanged += (_, _) => pendingAtEachChange.Add(controller.HasPendingEdit(SettingGroup.Dsee));

        await controller.SetDseeAsync(false, CancellationToken.None);

        await Assert.That(pendingAtEachChange).IsEquivalentTo([true, false], CollectionOrdering.Matching);
    }

    [Test]
    public async Task WhenDisposedDuringADebounceThenTheEditThrowsObjectDisposed()
    {
        var time = new FakeTimeProvider();
        var headset = new FakeHeadset();
        var controller = CreateController(headset, time);
        await controller.ConnectAsync(CancellationToken.None);
        var edit = controller.SetCustomEqualizerAsync(0, [1, 0, 0, 0, 0], EditPacing.Debounced, CancellationToken.None);

        await controller.DisposeAsync();

        await Assert.That(() => edit.WaitAsync(Patience)).Throws<ObjectDisposedException>();
    }

    // The notification is received during the debounce, before the edit's ACK, so the edit's value wins.
    [Test]
    public async Task WhenNotificationArrivesDuringADebounceThenTheEditedValueIsKept()
    {
        var time = new FakeTimeProvider();
        var headset = new FakeHeadset();
        await using var controller = CreateController(headset, time);
        await controller.ConnectAsync(CancellationToken.None);
        var notified = NextStateAsync(controller, state => state.NoiseControl?.AmbientLevel == 3);

        var edit = controller.SetNoiseControlAsync(
            new NoiseControlState(NoiseControlMode.Ambient, false, 15), EditPacing.Debounced, CancellationToken.None);
        headset.Notify(0x69, 0x17, 0x01, 0x01, 0x01, 0x00, 3);
        await notified;
        time.Advance(controller.EditDebounce);
        await edit.WaitAsync(Patience);

        await Assert.That(controller.State.NoiseControl!.AmbientLevel).IsEqualTo(15);
    }

    [Test]
    public async Task WhenEditOfAnotherGroupIsQueuedThenTheEditIsStillSent()
    {
        var headset = new FakeHeadset();
        await using var controller = CreateController(headset);
        await controller.ConnectAsync(CancellationToken.None);
        var writeHung = headset.Transport.HangNextWrite();
        var first = controller.SetDseeAsync(false, CancellationToken.None);
        await writeHung.WaitAsync(Patience);

        var dsee = controller.SetDseeAsync(true, CancellationToken.None);
        var equalizer = controller.SetCustomEqualizerAsync(0, [1, 0, 0, 0, 0], EditPacing.Immediate, CancellationToken.None);
        headset.Transport.ReleaseHungWrite();
        await Task.WhenAll(first, dsee, equalizer).WaitAsync(Patience);

        await Assert.That(Convert.ToHexString(headset.SentPayloads().Last(payload => payload[0] == 0xE8)))
            .IsEqualTo("E80101");
    }

    [Test]
    public async Task WhenSettingIsRequestedDuringConnectThenItIsSentOnceConnected()
    {
        var time = new FakeTimeProvider();
        var headset = new FakeHeadset();
        headset.Replies.Remove(0x12);
        await using var controller = CreateController(headset, time);
        var connect = controller.ConnectAsync(CancellationToken.None);
        await headset.WaitUntilSentAsync(0x12, Patience);

        var set = controller.SetDseeAsync(false, CancellationToken.None);
        time.Advance(ProtocolSession.DefaultTimeout);
        await connect.WaitAsync(Patience);
        await set.WaitAsync(Patience);

        await Assert.That(Convert.ToHexString(headset.SentPayloads()[^1])).IsEqualTo("E80100");
    }

    [Test]
    public async Task WhenQueuedOperationIsCancelledThenItThrowsOperationCanceled()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var controller = CreateHangingController(entered);
        _ = controller.ConnectAsync(CancellationToken.None);
        await entered.Task.WaitAsync(Patience);
        using var cancellation = new CancellationTokenSource();

        var set = controller.SetDseeAsync(false, cancellation.Token);
        await cancellation.CancelAsync();

        await Assert.That(() => set.WaitAsync(Patience)).Throws<OperationCanceledException>();
    }

    [Test]
    public async Task WhenQueuedOperationIsCancelledThenItIsNeverSent()
    {
        var time = new FakeTimeProvider();
        var headset = new FakeHeadset();
        headset.Replies.Remove(0x12);
        await using var controller = CreateController(headset, time);
        var connect = controller.ConnectAsync(CancellationToken.None);
        await headset.WaitUntilSentAsync(0x12, Patience);
        using var cancellation = new CancellationTokenSource();
        var set = controller.SetDseeAsync(false, cancellation.Token);

        await cancellation.CancelAsync();
        await IgnoreCancelledAsync(set);
        time.Advance(ProtocolSession.DefaultTimeout);
        await connect.WaitAsync(Patience);
        await controller.DisconnectAsync(CancellationToken.None);

        await Assert.That(headset.SentPayloads().Any(payload => payload[0] == 0xE8)).IsFalse();
    }

    [Test]
    public async Task WhenDisposedWhileOperationIsQueuedThenItThrowsObjectDisposed()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var controller = CreateHangingController(entered);
        _ = controller.ConnectAsync(CancellationToken.None);
        await entered.Task.WaitAsync(Patience);
        var set = controller.SetDseeAsync(false, CancellationToken.None);

        await controller.DisposeAsync().AsTask().WaitAsync(Patience);

        await Assert.That(() => set.WaitAsync(Patience)).Throws<ObjectDisposedException>();
    }

    [Test]
    public async Task WhenDisposedThenConnectThrowsObjectDisposed()
    {
        var headset = new FakeHeadset();
        var controller = CreateController(headset);

        await controller.DisposeAsync();

        await Assert.That(() => controller.ConnectAsync(CancellationToken.None)).Throws<ObjectDisposedException>();
    }

    [Test]
    public async Task WhenDisposedTwiceThenSecondDisposeCompletes()
    {
        var headset = new FakeHeadset();
        var controller = CreateController(headset);
        await controller.DisposeAsync();

        var act = () => controller.DisposeAsync().AsTask().WaitAsync(Patience);

        await Assert.That(act).ThrowsNothing();
    }

    private static List<Task> SendBandBurst(HeadsetController controller, int[] levels) =>
        [.. levels.Select(level => controller.SetCustomEqualizerAsync(
            0, [level, 0, 0, 0, 0], EditPacing.Debounced, CancellationToken.None))];

    private static HeadsetController CreateController(FakeHeadset headset, TimeProvider? time = null) =>
        new("WH-CH720N", headset.ConnectAsync, NullLoggerFactory.Instance, time);

    private static HeadsetController CreateHangingController(TaskCompletionSource entered) =>
        new(
            "WH-CH720N",
            async ct =>
            {
                entered.TrySetResult();
                await Task.Delay(Timeout.Infinite, ct);
                throw new InvalidOperationException("Unreachable: the delay never completes.");
            },
            NullLoggerFactory.Instance);

    private static async Task IgnoreDisposedAsync(Task operation)
    {
        try
        {
            await operation.WaitAsync(Patience);
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private static async Task IgnoreNotConnectedAsync(Task operation)
    {
        try
        {
            await operation.WaitAsync(Patience);
        }
        catch (InvalidOperationException)
        {
        }
    }

    private static async Task IgnoreCancelledAsync(Task operation)
    {
        try
        {
            await operation.WaitAsync(Patience);
        }
        catch (OperationCanceledException)
        {
        }
    }

    private static async Task ConnectIgnoringFailureAsync(HeadsetController controller)
    {
        try
        {
            await controller.ConnectAsync(CancellationToken.None);
        }
        catch (NotSupportedException)
        {
        }
    }

    private static Task<DeviceState> NextStateAsync(HeadsetController controller, Func<DeviceState, bool> predicate)
    {
        var reached = new TaskCompletionSource<DeviceState>(TaskCreationOptions.RunContinuationsAsynchronously);
        controller.StateChanged += (_, state) =>
        {
            if (predicate(state))
            {
                reached.TrySetResult(state);
            }
        };
        return reached.Task.WaitAsync(Patience);
    }

    // Notifications are applied in receive order, so once a later battery notification is applied every earlier one
    // has been too.
    private static async Task ProcessQueuedNotificationsAsync(HeadsetController controller, FakeHeadset headset)
    {
        var applied = NextStateAsync(controller, state => state.Battery?.Level == 17);
        headset.Notify(0x25, 0x00, 17, 0);
        await applied;
    }

    private static async Task ConnectPastTimeoutAsync(
        HeadsetController controller, FakeHeadset headset, FakeTimeProvider time, byte unansweredOpcode)
    {
        var connect = controller.ConnectAsync(CancellationToken.None);
        await headset.WaitUntilSentAsync(unansweredOpcode, Patience);
        time.Advance(ProtocolSession.DefaultTimeout);
        await connect.WaitAsync(Patience);
    }

    private static async Task IgnoreFailureAsync(Task operation)
    {
        try
        {
            await operation.WaitAsync(Patience);
        }
        catch (Exception ex) when (ex is IOException or TimeoutException)
        {
        }
    }
}
