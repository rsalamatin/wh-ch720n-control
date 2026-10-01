using HeadphoneControl.Protocol.Framing;
using HeadphoneControl.Protocol.Session;
using HeadphoneControl.Testing;
using Microsoft.Extensions.Time.Testing;
using static HeadphoneControl.Protocol.Tests.Session.SessionTestHelpers;

namespace HeadphoneControl.Protocol.Tests.Session;

public class ProtocolSessionRequestTests
{
    [Test]
    public async Task WhenRealDeviceAnswersInitHandshakeThenRequestReturnsInitReply()
    {
        // Replay the exact bytes a real WH-CH720N sent, including its retransmission.
        var transport = new FakeTransport();
        transport.Responder = _ =>
        {
            transport.QueueIncoming(Convert.FromHexString(
                "3E010100000000023C3E0C000000000801000300100200002A3C3E0C000000000801000300100200002A3C"));
            return [];
        };
        await using var session = await StartSessionAsync(transport);

        var reply = await session.RequestAsync(new byte[] { 0x00, 0x00 }, 0x01, null, PatienceToken());

        await Assert.That(Hex(reply.Payload)).IsEqualTo("0100030010020000");
    }

    [Test]
    public async Task WhenResponseMatchesOpcodeThenRequestReturnsItsPayload()
    {
        var transport = new FakeTransport { Responder = _ => [Ack(1), Data(0, 0x23, 0x00, 0x64, 0x00)] };
        await using var session = await StartSessionAsync(transport);

        var reply = await session.RequestAsync(new byte[] { 0x22, 0x00 }, 0x23, null, PatienceToken());

        await Assert.That(Hex(reply.Payload)).IsEqualTo("23006400");
    }

    [Test]
    public async Task WhenFirstResponseHasWrongSubtypeThenRequestReturnsTheMatchingOne()
    {
        var transport = new FakeTransport()
        {
            Responder = _ => [Ack(1), Data(0, 0x23, 0x01, 0x50, 0x00), Data(1, 0x23, 0x00, 0x64, 0x00)],
        };
        await using var session = await StartSessionAsync(transport);

        var reply = await session.RequestAsync(new byte[] { 0x22, 0x00 }, 0x23, 0x00, PatienceToken());

        await Assert.That(Hex(reply.Payload)).IsEqualTo("23006400");
    }

    [Test]
    public async Task WhenUnrelatedFrameArrivesDuringRequestThenItIsRaisedAsNotification()
    {
        var transport = new FakeTransport()
        {
            Responder = _ => [Ack(1), Data(0, 0x69, 0x17, 0x01), Data(1, 0x23, 0x00, 0x64, 0x00)],
        };
        await using var session = await StartSessionAsync(transport);
        var notification = NextNotificationAsync(session);

        await session.RequestAsync(new byte[] { 0x22, 0x00 }, 0x23, null, PatienceToken());

        await Assert.That(await notification).IsEqualTo("691701");
    }

    [Test]
    public async Task WhenNotificationArrivesBetweenAckAndResponseThenRequestReturnsTheResponse()
    {
        var transport = new FakeTransport()
        {
            Responder = _ => [Ack(1), Data(0, 0x69, 0x17, 0x01), Data(1, 0x23, 0x00, 0x64, 0x00)],
        };
        await using var session = await StartSessionAsync(transport);

        var reply = await session.RequestAsync(new byte[] { 0x22, 0x00 }, 0x23, null, PatienceToken());

        await Assert.That(Hex(reply.Payload)).IsEqualTo("23006400");
    }

    [Test]
    public async Task WhenOnlyUnrelatedFramesArriveThenRequestThrowsTimeoutException()
    {
        var time = new FakeTimeProvider();
        var transport = new FakeTransport { Responder = _ => [Ack(1), Data(0, 0x69, 0x17, 0x01)] };
        await using var session = await StartSessionAsync(transport, time);
        var notified = NextNotificationAsync(session);
        var request = session.RequestAsync(new byte[] { 0x22, 0x00 }, 0x23, null, PatienceToken());
        await notified;

        time.Advance(ProtocolSession.DefaultTimeout);

        await Assert.That(() => request).Throws<TimeoutException>();
    }

    [Test]
    public async Task WhenMatchingFrameArrivedBeforeTheRequestThenRequestWaitsForAFreshReply()
    {
        var transport = new FakeTransport();
        await using var session = await StartSessionAsync(transport);
        var notified = NextNotificationAsync(session);
        transport.QueueIncoming(Data(0, 0x23, 0x00, 0x55, 0x00));
        await notified;
        transport.Responder = frame => frame.Type == FrameType.DataMdr ? [Ack(1), Data(1, 0x23, 0x00, 0x64, 0x00)] : [];

        var reply = await session.RequestAsync(new byte[] { 0x22, 0x00 }, 0x23, null, PatienceToken());

        await Assert.That(Hex(reply.Payload)).IsEqualTo("23006400");
    }

    [Test]
    public async Task WhenLateReplyToTimedOutRequestArrivesThenNextRequestDoesNotReturnIt()
    {
        var time = new FakeTimeProvider();
        var transport = new FakeTransport();
        await using var session = await StartSessionAsync(transport, time);
        var timedOut = session.RequestAsync(new byte[] { 0x22, 0x00 }, 0x23, null, PatienceToken());
        await transport.WaitForWrittenFramesAsync(1, PatienceToken());
        time.Advance(ProtocolSession.DefaultTimeout);
        await Assert.That(() => timedOut).Throws<TimeoutException>();
        var late = NextNotificationAsync(session);
        transport.QueueIncoming(Data(0, 0x23, 0x00, 0x55, 0x00));
        await late;
        transport.Responder = frame => frame.Type == FrameType.DataMdr ? [Ack(0), Data(1, 0x23, 0x00, 0x64, 0x00)] : [];

        var reply = await session.RequestAsync(new byte[] { 0x22, 0x00 }, 0x23, null, PatienceToken());

        await Assert.That(Hex(reply.Payload)).IsEqualTo("23006400");
    }

    [Test]
    public async Task WhenRequestTimedOutThenNextRequestStillSucceeds()
    {
        var time = new FakeTimeProvider();
        var transport = new FakeTransport();
        await using var session = await StartSessionAsync(transport, time);
        var timedOut = session.RequestAsync(new byte[] { 0x22, 0x00 }, 0x23, null, PatienceToken());
        await transport.WaitForWrittenFramesAsync(1, PatienceToken());
        time.Advance(ProtocolSession.DefaultTimeout);
        await Assert.That(() => timedOut).Throws<TimeoutException>();
        transport.Responder = frame => frame.Type == FrameType.DataMdr ? [Ack(1), Data(1, 0x29, 0x00, 0x01)] : [];

        var reply = await session.RequestAsync(new byte[] { 0x28, 0x00 }, 0x29, null, PatienceToken());

        await Assert.That(Hex(reply.Payload)).IsEqualTo("290001");
    }

    [Test]
    public async Task WhenNotificationHandlerThrowsThenLaterNotificationsAreStillRaised()
    {
        var transport = new FakeTransport();
        await using var session = await StartSessionAsync(transport);
        session.NotificationReceived += (_, _) => throw new InvalidOperationException("faulty subscriber");
        var second = NextNotificationAsync(session, frame => frame.Payload.Span[0] == 0x02);

        transport.QueueIncoming(Data(0, 0x01));
        transport.QueueIncoming(Data(1, 0x02));

        await Assert.That(await second).IsEqualTo("02");
    }

    [Test]
    public async Task WhenNotificationHandlerThrewThenDisposeDoesNotThrow()
    {
        var transport = new FakeTransport();
        var session = await StartSessionAsync(transport);
        session.NotificationReceived += (_, _) => throw new InvalidOperationException("faulty subscriber");
        var raised = NextNotificationAsync(session);
        transport.QueueIncoming(Data(0, 0x01));
        await raised;

        var act = async () => await session.DisposeAsync();

        await Assert.That(act).ThrowsNothing();
    }

    [Test]
    public async Task WhenIncomingBytesAreFragmentedThenRequestStillCompletes()
    {
        var transport = new FakeTransport()
        {
            MaxReadChunk = 1,
            Responder = _ => [Ack(1), Data(0, 0x23, 0x00, 0x3E, 0x00)],
        };
        await using var session = await StartSessionAsync(transport);

        var reply = await session.RequestAsync(new byte[] { 0x22, 0x00 }, 0x23, null, PatienceToken());

        await Assert.That(Hex(reply.Payload)).IsEqualTo("23003E00");
    }

    [Test]
    public async Task WhenCorruptFramePrecedesResponseThenRequestStillCompletes()
    {
        var transport = new FakeTransport();
        transport.Responder = _ =>
        {
            transport.QueueIncoming(Convert.FromHexString("FF3E0C00000000022200313C"));
            return [Ack(1), Data(0, 0x23, 0x00, 0x64, 0x00)];
        };
        await using var session = await StartSessionAsync(transport);

        var reply = await session.RequestAsync(new byte[] { 0x22, 0x00 }, 0x23, null, PatienceToken());

        await Assert.That(Hex(reply.Payload)).IsEqualTo("23006400");
    }

    [Test]
    public async Task WhenTwoRequestsRunConcurrentlyThenSecondIsWrittenOnlyAfterFirstCompletes()
    {
        // Each request is answered at once, so without serialization both requests would be written first.
        var deviceSequence = (byte)0;
        var transport = new FakeTransport();
        transport.Responder = frame =>
        {
            if (frame.Type != FrameType.DataMdr)
            {
                return [];
            }

            deviceSequence ^= 1;
            return [Ack((byte)(1 - frame.Sequence)), Data(deviceSequence, (byte)(frame.Payload.Span[0] + 1), 0x00)];
        };
        await using var session = await StartSessionAsync(transport);

        await Task.WhenAll(
            session.RequestAsync(new byte[] { 0x22, 0x00 }, 0x23, null, PatienceToken()),
            session.RequestAsync(new byte[] { 0x28, 0x00 }, 0x29, null, PatienceToken()));

        await Assert.That(transport.WrittenFrames.Select(f => (int)f.Type)).IsEquivalentTo(
            [12, 1, 12, 1],
            TUnit.Assertions.Enums.CollectionOrdering.Matching);
    }

    [Test]
    public async Task WhenNotificationHandlerSendsRequestThenItCompletes()
    {
        var transport = new FakeTransport();
        await using var session = await StartSessionAsync(transport);
        var handlerReply = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        session.NotificationReceived += (_, _) =>
        {
            transport.Responder = _ => [Ack(1), Data(1, 0x23, 0x00, 0x64, 0x00)];

            // Blocking on purpose: this would deadlock if handlers ran on the receive loop.
            var reply = session.RequestAsync(new byte[] { 0x22, 0x00 }, 0x23, null, PatienceToken()).GetAwaiter().GetResult();
            handlerReply.TrySetResult(Hex(reply.Payload));
        };

        transport.QueueIncoming(Data(0, 0x69, 0x17));
        var result = await handlerReply.Task.WaitAsync(Patience);

        await Assert.That(result).IsEqualTo("23006400");
    }

    [Test]
    public async Task WhenSeveralNotificationsArriveThenTheyAreRaisedInOrder()
    {
        var transport = new FakeTransport();
        await using var session = await StartSessionAsync(transport);
        var payloads = new List<string>();
        session.NotificationReceived += (_, frame) => payloads.Add(Hex(frame.Payload));
        var last = NextNotificationAsync(session, frame => frame.Payload.Span[0] == 0x03);

        transport.QueueIncoming(Data(0, 0x01));
        transport.QueueIncoming(Data(1, 0x02));
        transport.QueueIncoming(Data(0, 0x03));
        await last;

        await Assert.That(payloads).IsEquivalentTo(["01", "02", "03"], TUnit.Assertions.Enums.CollectionOrdering.Matching);
    }
}
