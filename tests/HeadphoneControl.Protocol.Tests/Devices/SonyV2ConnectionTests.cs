using HeadphoneControl.Protocol.Devices;
using HeadphoneControl.Protocol.Session;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace HeadphoneControl.Protocol.Tests.Devices;

public class SonyV2ConnectionTests
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(5);

    [Test]
    [Arguments(ProtocolGeneration.V1)]
    [Arguments(ProtocolGeneration.Unknown)]
    public async Task WhenServiceIsNotV2ThenOpenThrowsNotSupported(ProtocolGeneration service)
    {
        var headset = new FakeHeadset { ServiceGeneration = service };

        await Assert.That(async () =>
        {
            await using var connection = await OpenAsync(headset);
        }).Throws<NotSupportedException>();
    }

    // Opcode 0x22 is BATTERY on V2 but POWER OFF on V1, so nothing but the handshake may reach a V1 device.
    [Test]
    public async Task WhenServiceIsV1ThenOnlyTheHandshakeIsSent()
    {
        var headset = new FakeHeadset { ServiceGeneration = ProtocolGeneration.V1 };

        await OpenIgnoringRefusalAsync(headset);

        await Assert.That(headset.SentPayloads().Select(Convert.ToHexString)).IsEquivalentTo(["0000"]);
    }

    [Test]
    public async Task WhenInitReplyIsV1ShapedThenOnlyTheHandshakeIsSent()
    {
        var headset = new FakeHeadset { InitReply = [0x01, 0x00, 0x01, 0x00, 0x00, 0x00, 0x00, 0x00] };

        await OpenIgnoringRefusalAsync(headset);

        await Assert.That(headset.SentPayloads().Select(Convert.ToHexString)).IsEquivalentTo(["0000"]);
    }

    [Test]
    public async Task WhenHandshakeIsRefusedThenTransportIsReleased()
    {
        var headset = new FakeHeadset { ServiceGeneration = ProtocolGeneration.V1 };

        await OpenIgnoringRefusalAsync(headset);

        await Assert.That(headset.Transport.IsDisposed).IsTrue();
    }

    [Test]
    public async Task WhenOpenedThenHandshakeIsTheFirstFrameSent()
    {
        var headset = new FakeHeadset();

        await using var connection = await OpenAsync(headset);

        await Assert.That(Convert.ToHexString(headset.SentPayloads()[0])).IsEqualTo("0000");
    }

    [Test]
    public async Task WhenAllQueriesAreAnsweredThenReadAllReturnsTheBattery()
    {
        var headset = new FakeHeadset();
        await using var connection = await OpenAsync(headset);

        var settings = await connection.ReadAllAsync(CancellationToken.None);

        await Assert.That(settings.Battery).IsEqualTo(new BatteryState(80, true));
    }

    [Test]
    public async Task WhenQueryIsUnansweredThenReadAllLeavesThatSettingNull()
    {
        var time = new FakeTimeProvider();
        var headset = new FakeHeadset();
        headset.Replies.Remove(0xE6);
        await using var connection = await OpenAsync(headset, time);

        var settings = await ReadAllPastTimeoutAsync(connection, headset, time, 0xE6);

        await Assert.That(settings.DseeEnabled).IsNull();
    }

    [Test]
    public async Task WhenQueryIsUnansweredThenReadAllStillReadsTheLaterSettings()
    {
        var time = new FakeTimeProvider();
        var headset = new FakeHeadset();
        headset.Replies.Remove(0xE6);
        await using var connection = await OpenAsync(headset, time);

        var settings = await ReadAllPastTimeoutAsync(connection, headset, time, 0xE6);

        await Assert.That(settings.FirmwareVersion).IsEqualTo("1.0.2");
    }

    [Test]
    public async Task WhenDseeIsSetThenCommandIsSent()
    {
        var headset = new FakeHeadset();
        await using var connection = await OpenAsync(headset);

        await connection.SetDseeAsync(false, CancellationToken.None);

        await Assert.That(Convert.ToHexString(headset.SentPayloads()[^1])).IsEqualTo("E80100");
    }

    [Test]
    public async Task WhenNotificationArrivesThenNotificationReceivedIsRaised()
    {
        var headset = new FakeHeadset();
        await using var connection = await OpenAsync(headset);
        var received = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        connection.NotificationReceived += (_, payload) => received.TrySetResult(payload.ToArray());

        headset.Notify(0x25, 0x00, 42, 0);

        await Assert.That(Convert.ToHexString(await received.Task.WaitAsync(Patience))).IsEqualTo("25002A00");
    }

    [Test]
    public async Task WhenLinkDropsThenLinkLostIsRaised()
    {
        var headset = new FakeHeadset();
        await using var connection = await OpenAsync(headset);
        var lost = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        connection.LinkLost += (_, _) => lost.TrySetResult();

        headset.Transport.SimulateEof();

        await Assert.That(() => lost.Task.WaitAsync(Patience)).ThrowsNothing();
    }

    [Test]
    public async Task WhenLinkDropsWithNoHandlerSubscribedThenIsLinkLostIsTrue()
    {
        var headset = new FakeHeadset();
        await using var connection = await OpenAsync(headset);

        headset.Transport.SimulateEof();
        await WaitUntilAsync(() => connection.IsLinkLost);

        await Assert.That(connection.IsLinkLost).IsTrue();
    }

    [Test]
    public async Task WhenDisposedThenTransportIsReleased()
    {
        var headset = new FakeHeadset();
        var connection = await OpenAsync(headset);

        await connection.DisposeAsync();

        await Assert.That(headset.Transport.IsDisposed).IsTrue();
    }

    [Test]
    public async Task WhenDisposedThenLinkLostIsNotRaised()
    {
        var headset = new FakeHeadset();
        var connection = await OpenAsync(headset);
        var raised = false;
        connection.LinkLost += (_, _) => raised = true;

        await connection.DisposeAsync();

        await Assert.That(raised).IsFalse();
    }

    [Test]
    public async Task WhenSettingIsUnansweredThenApplyKeepsTheCurrentValue()
    {
        var current = DeviceState.Disconnected with { Battery = new BatteryState(42, false) };
        var settings = new DeviceSettings(null, null, null, true, null, null);

        var updated = settings.ApplyTo(current);

        await Assert.That(updated.Battery).IsEqualTo(new BatteryState(42, false));
    }

    private static async Task<SonyV2Connection> OpenAsync(FakeHeadset headset, TimeProvider? time = null)
    {
        var transport = await headset.ConnectAsync(CancellationToken.None);
        return await SonyV2Connection.OpenAsync(
            transport, NullLoggerFactory.Instance, time ?? TimeProvider.System, CancellationToken.None);
    }

    private static async Task OpenIgnoringRefusalAsync(FakeHeadset headset)
    {
        try
        {
            await using var connection = await OpenAsync(headset);
        }
        catch (NotSupportedException)
        {
            // Expected: these tests assert what was (not) sent before the refusal.
        }
    }

    // The unanswered request waits on the fake clock: advance it once that request is on the wire.
    private static async Task<DeviceSettings> ReadAllPastTimeoutAsync(
        SonyV2Connection connection, FakeHeadset headset, FakeTimeProvider time, byte unansweredOpcode)
    {
        var read = connection.ReadAllAsync(CancellationToken.None);
        await headset.WaitUntilSentAsync(unansweredOpcode, Patience);
        time.Advance(ProtocolSession.DefaultTimeout);
        return await read.WaitAsync(Patience);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + Patience;
        while (!condition() && DateTime.UtcNow < deadline)
        {
            await Task.Delay(5);
        }
    }
}
