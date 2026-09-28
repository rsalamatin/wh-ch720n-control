using HeadphoneControl.Protocol.Devices;
using HeadphoneControl.Protocol.Session;
using HeadphoneControl.Protocol.Transport;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace HeadphoneControl.Protocol.Tests.Devices;

public class HeadphoneDeviceTests
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(5);

    [Test]
    public async Task WhenHeadsetConfirmsV2ThenStateIsConnected()
    {
        var headset = new FakeHeadset();
        await using var device = CreateDevice(headset);

        await device.ConnectAsync(CancellationToken.None);

        await Assert.That(device.State.Connection).IsEqualTo(ConnectionStatus.Connected);
    }

    [Test]
    public async Task WhenConnectedThenGenerationIsV2()
    {
        var headset = new FakeHeadset();
        await using var device = CreateDevice(headset);

        await device.ConnectAsync(CancellationToken.None);

        await Assert.That(device.State.Generation).IsEqualTo(ProtocolGeneration.V2);
    }

    [Test]
    public async Task WhenConnectedThenBatteryIsRead()
    {
        var headset = new FakeHeadset();
        await using var device = CreateDevice(headset);

        await device.ConnectAsync(CancellationToken.None);

        await Assert.That(device.State.Battery).IsEqualTo(new BatteryState(80, true));
    }

    [Test]
    public async Task WhenConnectedThenNoiseControlIsRead()
    {
        var headset = new FakeHeadset();
        await using var device = CreateDevice(headset);

        await device.ConnectAsync(CancellationToken.None);

        await Assert.That(device.State.NoiseControl).IsEqualTo(new NoiseControlState(NoiseControlMode.Ambient, false, 8));
    }

    [Test]
    public async Task WhenConnectedThenFirmwareVersionIsRead()
    {
        var headset = new FakeHeadset();
        await using var device = CreateDevice(headset);

        await device.ConnectAsync(CancellationToken.None);

        await Assert.That(device.State.FirmwareVersion).IsEqualTo("1.0.2");
    }

    [Test]
    public async Task WhenConnectingThenHandshakeIsTheFirstFrameSent()
    {
        var headset = new FakeHeadset();
        await using var device = CreateDevice(headset);

        await device.ConnectAsync(CancellationToken.None);

        await Assert.That(Convert.ToHexString(headset.SentPayloads()[0])).IsEqualTo("0000");
    }

    [Test]
    [Arguments(ProtocolGeneration.V1)]
    [Arguments(ProtocolGeneration.Unknown)]
    public async Task WhenTransportServiceIsNotV2ThenConnectThrowsNotSupported(ProtocolGeneration service)
    {
        var headset = new FakeHeadset { ServiceGeneration = service };
        await using var device = CreateDevice(headset);

        await Assert.That(() => device.ConnectAsync(CancellationToken.None)).Throws<NotSupportedException>();
    }

    [Test]
    [Arguments(ProtocolGeneration.V1)]
    [Arguments(ProtocolGeneration.Unknown)]
    public async Task WhenTransportServiceIsNotV2ThenOnlyTheHandshakeIsSent(ProtocolGeneration service)
    {
        var headset = new FakeHeadset { ServiceGeneration = service };
        await using var device = CreateDevice(headset);

        await ConnectIgnoringFailureAsync(device);

        await Assert.That(headset.SentPayloads().Select(Convert.ToHexString)).IsEquivalentTo(["0000"]);
    }

    [Test]
    public async Task WhenInitReplyIsV1ShapedThenOnlyTheHandshakeIsSent()
    {
        var headset = new FakeHeadset { InitReply = [0x01, 0x00, 0x01, 0x00, 0x00, 0x00, 0x00, 0x00] };
        await using var device = CreateDevice(headset);

        await ConnectIgnoringFailureAsync(device);

        await Assert.That(headset.SentPayloads().Select(Convert.ToHexString)).IsEquivalentTo(["0000"]);
    }

    [Test]
    public async Task WhenGenerationIsRejectedThenStateIsFailed()
    {
        var headset = new FakeHeadset { ServiceGeneration = ProtocolGeneration.V1 };
        await using var device = CreateDevice(headset);

        await ConnectIgnoringFailureAsync(device);

        await Assert.That(device.State.Connection).IsEqualTo(ConnectionStatus.Failed);
    }

    [Test]
    public async Task WhenGenerationIsRejectedThenTransportIsReleased()
    {
        var headset = new FakeHeadset { ServiceGeneration = ProtocolGeneration.V1 };
        await using var device = CreateDevice(headset);

        await ConnectIgnoringFailureAsync(device);

        await Assert.That(headset.Transport.IsDisposed).IsTrue();
    }

    [Test]
    public async Task WhenTransportCannotConnectThenConnectThrowsTransportException()
    {
        await using var device = new HeadphoneDevice(
            "WH-CH720N",
            _ => throw new TransportException("Headset not in range."),
            NullLoggerFactory.Instance);

        await Assert.That(() => device.ConnectAsync(CancellationToken.None)).Throws<TransportException>();
    }

    [Test]
    public async Task WhenQueryIsUnansweredThenThatSettingStaysUnknown()
    {
        var time = new FakeTimeProvider();
        var headset = new FakeHeadset();
        headset.Replies.Remove(0xE6);
        await using var device = CreateDevice(headset, time);

        await ConnectPastTimeoutAsync(device, headset, time, 0xE6);

        await Assert.That(device.State.DseeEnabled).IsNull();
    }

    [Test]
    public async Task WhenQueryIsUnansweredThenDeviceStillConnects()
    {
        var time = new FakeTimeProvider();
        var headset = new FakeHeadset();
        headset.Replies.Remove(0xE6);
        await using var device = CreateDevice(headset, time);

        await ConnectPastTimeoutAsync(device, headset, time, 0xE6);

        await Assert.That(device.State.Connection).IsEqualTo(ConnectionStatus.Connected);
    }

    [Test]
    public async Task WhenQueryReplyIsMalformedThenThatSettingStaysUnknown()
    {
        var headset = new FakeHeadset();
        headset.Replies[0x22] = [0x23, 0x00, 0xFF, 0x00];
        await using var device = CreateDevice(headset);

        await device.ConnectAsync(CancellationToken.None);

        await Assert.That(device.State.Battery).IsNull();
    }

    [Test]
    public async Task WhenHandshakeIsUnansweredThenConnectThrowsTimeout()
    {
        var time = new FakeTimeProvider();
        var headset = new FakeHeadset { InitReply = null };
        await using var device = CreateDevice(headset, time);

        var connect = device.ConnectAsync(CancellationToken.None);
        await headset.WaitUntilSentAsync(0x00, Patience);
        time.Advance(ProtocolSession.DefaultTimeout);

        await Assert.That(() => connect.WaitAsync(Patience)).Throws<TimeoutException>();
    }

    [Test]
    public async Task WhenHandshakeIsUnansweredThenTransportIsReleased()
    {
        var time = new FakeTimeProvider();
        var headset = new FakeHeadset { InitReply = null };
        await using var device = CreateDevice(headset, time);
        var connect = device.ConnectAsync(CancellationToken.None);
        await headset.WaitUntilSentAsync(0x00, Patience);

        time.Advance(ProtocolSession.DefaultTimeout);
        await IgnoreFailureAsync(connect);

        await Assert.That(headset.Transport.IsDisposed).IsTrue();
    }

    [Test]
    public async Task WhenInitReplyIsShortThenConnectThrowsNotSupported()
    {
        var headset = new FakeHeadset { InitReply = [0x01, 0x00] };
        await using var device = CreateDevice(headset);

        await Assert.That(() => device.ConnectAsync(CancellationToken.None)).Throws<NotSupportedException>();
    }

    [Test]
    public async Task WhenLinkDropsDuringConnectThenStateIsFailed()
    {
        var headset = new FakeHeadset { DropLinkAfter = 0x00 };
        await using var device = CreateDevice(headset);

        await IgnoreFailureAsync(device.ConnectAsync(CancellationToken.None));

        await Assert.That(device.State.Connection).IsEqualTo(ConnectionStatus.Failed);
    }

    [Test]
    public async Task WhenConnectorThrowsUnexpectedExceptionThenConnectThrowsTransportException()
    {
        await using var device = new HeadphoneDevice(
            "WH-CH720N", _ => throw new NotImplementedException("E_NOTIMPL"), NullLoggerFactory.Instance);

        await Assert.That(() => device.ConnectAsync(CancellationToken.None)).Throws<TransportException>();
    }

    // Smoke test: nothing observable runs between ConnectAsync's link check and its final publish, so it cannot
    // force the Disconnected handler into that window.
    [Test]
    public async Task WhenLinkDropsAfterTheLastConnectQueryThenStateSettlesNotConnected()
    {
        var headset = new FakeHeadset { DropLinkAfter = 0x12 };
        await using var device = CreateDevice(headset);
        var settled = NextStateAsync(
            device, state => state.Connection is ConnectionStatus.Failed or ConnectionStatus.Disconnected);

        await IgnoreFailureAsync(device.ConnectAsync(CancellationToken.None));
        await settled;

        await Assert.That(device.State.Connection).IsNotEqualTo(ConnectionStatus.Connected);
    }

    [Test]
    public async Task WhenDisposedDuringConnectThenConnectThrowsObjectDisposed()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var device = CreateHangingDevice(entered);
        var connect = device.ConnectAsync(CancellationToken.None);
        await entered.Task.WaitAsync(Patience);

        await device.DisposeAsync().AsTask().WaitAsync(Patience);

        await Assert.That(() => connect.WaitAsync(Patience)).Throws<ObjectDisposedException>();
    }

    [Test]
    public async Task WhenDisposedDuringConnectThenStateIsDisconnected()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var device = CreateHangingDevice(entered);
        var connect = device.ConnectAsync(CancellationToken.None);
        await entered.Task.WaitAsync(Patience);

        await device.DisposeAsync().AsTask().WaitAsync(Patience);
        await IgnoreDisposedAsync(connect);

        await Assert.That(device.State).IsEqualTo(DeviceState.Disconnected);
    }

    [Test]
    public async Task WhenConnectorIsCancelledWithoutTheCallerThenConnectThrowsTransportException()
    {
        await using var device = new HeadphoneDevice(
            "WH-CH720N", _ => throw new OperationCanceledException(), NullLoggerFactory.Instance);

        await Assert.That(() => device.ConnectAsync(CancellationToken.None)).Throws<TransportException>();
    }

    [Test]
    public async Task WhenEqualizerReQueryIsUnansweredThenEqualizerIsUnknown()
    {
        var time = new FakeTimeProvider();
        var headset = new FakeHeadset();
        await using var device = CreateDevice(headset, time);
        await device.ConnectAsync(CancellationToken.None);
        headset.Replies.Remove(0x56);

        var set = device.SetEqualizerPresetAsync(EqualizerPreset.BassBoost, CancellationToken.None);
        await headset.WaitUntilSentAsync(0x56, Patience, times: 2);
        time.Advance(ProtocolSession.DefaultTimeout);
        await set.WaitAsync(Patience);

        await Assert.That(device.State.Equalizer).IsNull();
    }

    [Test]
    public async Task WhenEqualizerReplyArrivesAfterTheReQueryTimedOutThenTheLateCurveIsKept()
    {
        var time = new FakeTimeProvider();
        var headset = new FakeHeadset();
        var loggers = new HookedLoggerFactory();
        await using var device = new HeadphoneDevice("WH-CH720N", headset.ConnectAsync, loggers, time);
        await device.ConnectAsync(CancellationToken.None);
        headset.Replies.Remove(0x56);
        var lateCurveApplied = NextStateAsync(
            device, state => state.Equalizer?.Bands.SequenceEqual([6, 4, 0, 0, 0]) == true);

        // The device logs this after the re-query gave up and before it publishes, so the late reply is applied
        // exactly in the window where an unconditional null publish would wipe it.
        loggers.OnLog = message =>
        {
            if (message.StartsWith("Equalizer of", StringComparison.Ordinal))
            {
                headset.Notify(0x57, 0x00, 0x16, 0x06, 10, 16, 14, 10, 10, 10);
                if (!lateCurveApplied.Wait(Patience))
                {
                    throw new TimeoutException("The late equalizer reply was never applied.");
                }
            }
        };
        var set = device.SetEqualizerPresetAsync(EqualizerPreset.BassBoost, CancellationToken.None);
        await headset.WaitUntilSentAsync(0x56, Patience, times: 2);
        time.Advance(ProtocolSession.DefaultTimeout);
        await set.WaitAsync(Patience);

        await Assert.That(device.State.Equalizer!.Bands).IsEquivalentTo([6, 4, 0, 0, 0]);
    }

    [Test]
    public async Task WhenCallerCancelsAfterPresetIsAcknowledgedThenEqualizerCurveIsStillReRead()
    {
        var headset = new FakeHeadset();
        await using var device = CreateDevice(headset);
        await device.ConnectAsync(CancellationToken.None);
        headset.Replies.Remove(0x56);
        using var cancellation = new CancellationTokenSource();

        var set = device.SetEqualizerPresetAsync(EqualizerPreset.BassBoost, cancellation.Token);
        await headset.WaitUntilSentAsync(0x56, Patience, times: 2);
        await cancellation.CancelAsync();
        headset.Notify(0x57, 0x00, 0x16, 0x06, 10, 16, 14, 10, 10, 10);
        await set.WaitAsync(Patience);

        await Assert.That(device.State.Equalizer!.Bands).IsEquivalentTo([6, 4, 0, 0, 0]);
    }

    [Test]
    public async Task WhenPresetIsSetThenEqualizerCurveIsReRead()
    {
        var headset = new FakeHeadset();
        await using var device = CreateDevice(headset);
        await device.ConnectAsync(CancellationToken.None);
        headset.Replies[0x56] = [0x57, 0x00, 0x16, 0x06, 10, 16, 14, 10, 10, 10];

        await device.SetEqualizerPresetAsync(EqualizerPreset.BassBoost, CancellationToken.None);

        await Assert.That(device.State.Equalizer!.Bands).IsEquivalentTo([6, 4, 0, 0, 0]);
    }

    [Test]
    public async Task WhenLinkIsLostThenSettingThrowsInvalidOperation()
    {
        var headset = new FakeHeadset();
        await using var device = CreateDevice(headset);
        await device.ConnectAsync(CancellationToken.None);
        var disconnected = NextStateAsync(device, state => state.Connection == ConnectionStatus.Disconnected);
        headset.Transport.SimulateEof();
        await disconnected;

        await Assert.That(() => device.SetDseeAsync(true, CancellationToken.None)).Throws<InvalidOperationException>();
    }

    [Test]
    public async Task WhenDisposedThenTransportIsReleased()
    {
        var headset = new FakeHeadset();
        var device = CreateDevice(headset);
        await device.ConnectAsync(CancellationToken.None);

        await device.DisposeAsync();

        await Assert.That(headset.Transport.IsDisposed).IsTrue();
    }

    [Test]
    public async Task WhenNotConnectedThenSettingThrowsInvalidOperation()
    {
        var headset = new FakeHeadset();
        await using var device = CreateDevice(headset);

        await Assert.That(() => device.SetDseeAsync(true, CancellationToken.None)).Throws<InvalidOperationException>();
    }

    [Test]
    public async Task WhenDseeIsSetThenCommandIsSent()
    {
        var headset = new FakeHeadset();
        await using var device = CreateDevice(headset);
        await device.ConnectAsync(CancellationToken.None);

        await device.SetDseeAsync(false, CancellationToken.None);

        await Assert.That(Convert.ToHexString(headset.SentPayloads()[^1])).IsEqualTo("E80100");
    }

    [Test]
    public async Task WhenDseeIsAcknowledgedThenStateIsUpdated()
    {
        var headset = new FakeHeadset();
        await using var device = CreateDevice(headset);
        await device.ConnectAsync(CancellationToken.None);

        await device.SetDseeAsync(false, CancellationToken.None);

        await Assert.That(device.State.DseeEnabled).IsFalse();
    }

    [Test]
    public async Task WhenNoiseControlIsSetOutsideAmbientThenStoredLevelIsAtLeastOne()
    {
        var headset = new FakeHeadset();
        await using var device = CreateDevice(headset);
        await device.ConnectAsync(CancellationToken.None);

        await device.SetNoiseControlAsync(
            new NoiseControlState(NoiseControlMode.NoiseCancelling, false, 0), CancellationToken.None);

        await Assert.That(device.State.NoiseControl!.AmbientLevel).IsEqualTo(1);
    }

    [Test]
    public async Task WhenBatteryNotificationArrivesThenStateIsUpdated()
    {
        var headset = new FakeHeadset();
        await using var device = CreateDevice(headset);
        await device.ConnectAsync(CancellationToken.None);
        var changed = NextStateAsync(device, state => state.Battery?.Level == 42);

        headset.Notify(0x25, 0x00, 42, 0);

        await Assert.That((await changed).Battery).IsEqualTo(new BatteryState(42, false));
    }

    [Test]
    public async Task WhenMalformedNotificationArrivesThenLaterNotificationsAreApplied()
    {
        var headset = new FakeHeadset();
        await using var device = CreateDevice(headset);
        await device.ConnectAsync(CancellationToken.None);
        var changed = NextStateAsync(device, state => state.Battery?.Level == 42);

        headset.Notify(0x25, 0x00);
        headset.Notify(0x25, 0x00, 42, 0);

        await Assert.That((await changed).Battery!.Level).IsEqualTo(42);
    }

    [Test]
    public async Task WhenLinkDropsThenStateIsDisconnected()
    {
        var headset = new FakeHeadset();
        await using var device = CreateDevice(headset);
        await device.ConnectAsync(CancellationToken.None);
        var changed = NextStateAsync(device, state => state.Connection == ConnectionStatus.Disconnected);

        headset.Transport.SimulateEof();

        await Assert.That((await changed).Connection).IsEqualTo(ConnectionStatus.Disconnected);
    }

    [Test]
    public async Task WhenDisconnectedThenTransportIsReleased()
    {
        var headset = new FakeHeadset();
        await using var device = CreateDevice(headset);
        await device.ConnectAsync(CancellationToken.None);

        await device.DisconnectAsync(CancellationToken.None);

        await Assert.That(headset.Transport.IsDisposed).IsTrue();
    }

    [Test]
    public async Task WhenReconnectingThenANewTransportIsOpened()
    {
        var headsets = new Queue<FakeHeadset>([new FakeHeadset(), new FakeHeadset()]);
        var second = headsets.Last();
        await using var device = new HeadphoneDevice(
            "WH-CH720N", ct => headsets.Dequeue().ConnectAsync(ct), NullLoggerFactory.Instance);
        await device.ConnectAsync(CancellationToken.None);
        await device.DisconnectAsync(CancellationToken.None);

        await device.ConnectAsync(CancellationToken.None);

        await Assert.That(Convert.ToHexString(second.SentPayloads()[0])).IsEqualTo("0000");
    }

    private static HeadphoneDevice CreateDevice(FakeHeadset headset, TimeProvider? time = null) =>
        new("WH-CH720N", headset.ConnectAsync, NullLoggerFactory.Instance, time);

    // The connector hangs until cancelled, like discovery against an absent headset.
    private static HeadphoneDevice CreateHangingDevice(TaskCompletionSource entered) =>
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
            // Expected: these tests assert the state left behind by disposal.
        }
    }

    private static async Task ConnectIgnoringFailureAsync(HeadphoneDevice device)
    {
        try
        {
            await device.ConnectAsync(CancellationToken.None);
        }
        catch (NotSupportedException)
        {
            // Expected: these tests assert what was (not) sent before the refusal.
        }
    }

    private static Task<DeviceState> NextStateAsync(HeadphoneDevice device, Func<DeviceState, bool> predicate)
    {
        var reached = new TaskCompletionSource<DeviceState>(TaskCreationOptions.RunContinuationsAsynchronously);
        device.StateChanged += (_, state) =>
        {
            if (predicate(state))
            {
                reached.TrySetResult(state);
            }
        };
        return reached.Task.WaitAsync(Patience);
    }

    // The unanswered request waits on the fake clock: advance it once that request is on the wire.
    private static async Task ConnectPastTimeoutAsync(
        HeadphoneDevice device, FakeHeadset headset, FakeTimeProvider time, byte unansweredOpcode)
    {
        var connect = device.ConnectAsync(CancellationToken.None);
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
            // Expected: these tests assert the state left behind by the failure.
        }
    }
}
