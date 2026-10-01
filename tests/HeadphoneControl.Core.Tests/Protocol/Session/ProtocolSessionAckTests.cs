using HeadphoneControl.Protocol.Framing;
using HeadphoneControl.Protocol.Session;
using HeadphoneControl.Testing;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using static HeadphoneControl.Protocol.Tests.Session.SessionTestHelpers;

namespace HeadphoneControl.Protocol.Tests.Session;

public class ProtocolSessionAckTests
{
    // One read captured from a real WH-CH720N after the init handshake: ACK seq 1, DATA_MDR seq 0 reply,
    // and the same reply retransmitted because the capture tool never ACKed it.
    private const string RealDeviceInitReply =
        "3E010100000000023C" + "3E0C000000000801000300100200002A3C" + "3E0C000000000801000300100200002A3C";

    [Test]
    [Arguments((byte)0, (byte)1)]
    [Arguments((byte)1, (byte)0)]
    public async Task WhenDataMdrArrivesThenAckWithOppositeSequenceIsWritten(byte deviceSequence, byte expectedAckSequence)
    {
        var transport = new FakeTransport();
        await using var session = await StartSessionAsync(transport);

        transport.QueueIncoming(Data(deviceSequence, 0x69, 0x17));
        await transport.WaitForWrittenFramesAsync(1, PatienceToken());

        await Assert.That(Describe(transport.WrittenFrames[0])).IsEqualTo($"1/{expectedAckSequence}/");
    }

    [Test]
    public async Task WhenAckArrivesThenNoAckIsWrittenBack()
    {
        var transport = new FakeTransport();
        await using var session = await StartSessionAsync(transport);

        // The trailing DATA_MDR proves the ACK before it has been processed.
        transport.QueueIncoming(Ack(1));
        transport.QueueIncoming(Data(0, 0x69));
        await transport.WaitForWrittenFramesAsync(1, PatienceToken());

        await Assert.That(transport.WrittenFrames.Select(Describe)).IsEquivalentTo(["1/1/"]);
    }

    [Test]
    [Arguments(FrameType.DataMdrNo2)]
    [Arguments(FrameType.DataCommon)]
    [Arguments(FrameType.ShotMdr)]
    public async Task WhenNonMdrFrameArrivesThenItIsAcked(FrameType type)
    {
        var transport = new FakeTransport();
        await using var session = await StartSessionAsync(transport);

        transport.QueueIncoming(new Frame(type, 0, new byte[] { 0x01 }));
        await transport.WaitForWrittenFramesAsync(1, PatienceToken());

        await Assert.That(Describe(transport.WrittenFrames[0])).IsEqualTo("1/1/");
    }

    [Test]
    public async Task WhenNonMdrFrameArrivesThenItIsNotRaisedAsNotification()
    {
        var transport = new FakeTransport();
        await using var session = await StartSessionAsync(transport);
        var first = NextNotificationAsync(session);

        // The trailing DATA_MDR is raised only after the non-MDR frame was handled.
        transport.QueueIncoming(new Frame(FrameType.DataMdrNo2, 0, new byte[] { 0x01 }));
        transport.QueueIncoming(Data(1, 0x69));

        await Assert.That(await first).IsEqualTo("69");
    }

    [Test]
    public async Task WhenDeviceRetransmitsFrameThenEveryCopyIsAcked()
    {
        var transport = new FakeTransport();
        await using var session = await StartSessionAsync(transport);

        transport.QueueIncoming(Convert.FromHexString(RealDeviceInitReply));
        await transport.WaitForWrittenFramesAsync(2, PatienceToken());

        await Assert.That(transport.WrittenFrames.Select(Describe)).IsEquivalentTo(["1/1/", "1/1/"]);
    }

    [Test]
    public async Task WhenDeviceRetransmitsFrameThenItIsDispatchedOnce()
    {
        var transport = new FakeTransport();
        await using var session = await StartSessionAsync(transport);
        var payloads = new List<string>();
        session.NotificationReceived += (_, frame) => payloads.Add(Hex(frame.Payload));
        var marker = NextNotificationAsync(session, frame => frame.Payload.Span[0] == 0x69);

        // A later, different-sequence notification marks the end of the burst.
        transport.QueueIncoming(Convert.FromHexString(RealDeviceInitReply));
        transport.QueueIncoming(Data(1, 0x69, 0x01));
        await marker;

        await Assert.That(payloads).IsEquivalentTo(["0100030010020000", "6901"], TUnit.Assertions.Enums.CollectionOrdering.Matching);
    }

    [Test]
    public async Task WhenAckArrivesThenSendAsyncCompletes()
    {
        var transport = new FakeTransport { AutoAck = true };
        await using var session = await StartSessionAsync(transport);

        await session.SendAsync(new byte[] { 0x00, 0x00 }, PatienceToken());

        await Assert.That(Describe(transport.WrittenFrames[0])).IsEqualTo("12/0/0000");
    }

    [Test]
    public async Task WhenSendAsyncIsCalledThenWireBytesMatchRealDeviceCapture()
    {
        var transport = new FakeTransport { AutoAck = true };
        await using var session = await StartSessionAsync(transport);

        await session.SendAsync(new byte[] { 0x00, 0x00 }, PatienceToken());

        await Assert.That(Convert.ToHexString(transport.Written[0])).IsEqualTo("3E0C000000000200000E3C");
    }

    [Test]
    public async Task WhenNoAckArrivesThenSendAsyncThrowsTimeoutException()
    {
        var time = new FakeTimeProvider();
        var transport = new FakeTransport();
        await using var session = await StartSessionAsync(transport, time);
        var send = session.SendAsync(new byte[] { 0x00, 0x00 }, PatienceToken());
        await transport.WaitForWrittenFramesAsync(1, PatienceToken());

        time.Advance(ProtocolSession.DefaultTimeout);

        await Assert.That(() => send).Throws<TimeoutException>();
    }

    [Test]
    public async Task WhenEveryWriteIsAckedThenOutgoingSequenceAlternates()
    {
        var transport = new FakeTransport { AutoAck = true };
        await using var session = await StartSessionAsync(transport);

        await session.SendAsync(new byte[] { 0x01 }, PatienceToken());
        await session.SendAsync(new byte[] { 0x02 }, PatienceToken());
        await session.SendAsync(new byte[] { 0x03 }, PatienceToken());

        await Assert.That(transport.WrittenFrames.Select(f => f.Sequence)).IsEquivalentTo(
            new byte[] { 0, 1, 0 },
            TUnit.Assertions.Enums.CollectionOrdering.Matching);
    }

    [Test]
    public async Task WhenAckCarriesTheSentSequenceThenSendIsNotAcknowledged()
    {
        // The real WH-CH720N ACKs seq n with 1 - n; an ACK carrying n is a late ACK for an earlier send.
        var time = new FakeTimeProvider();
        var transport = new FakeTransport { Responder = frame => [Ack(frame.Sequence)] };
        await using var session = await StartSessionAsync(transport, time);
        var send = session.SendAsync(new byte[] { 0x01 }, PatienceToken());
        await transport.WaitForWrittenFramesAsync(1, PatienceToken());

        time.Advance(ProtocolSession.DefaultTimeout);

        await Assert.That(() => send).Throws<TimeoutException>();
    }

    [Test]
    public async Task WhenLateAckForTimedOutSendArrivesThenNextSendIsNotAcknowledged()
    {
        // The receive loop's ACK write hangs holding the write lock, so the second send is registered but cannot
        // write its frame; the late ACK(1) for the first send (seq 0) follows in the same read, and the trailing
        // DATA_MDR proves it was processed.
        var time = new FakeTimeProvider();
        var transport = new FakeTransport();
        await using var session = await StartSessionAsync(transport, time);
        var timedOut = session.SendAsync(new byte[] { 0x01 }, PatienceToken());
        await transport.WaitForWrittenFramesAsync(1, PatienceToken());
        time.Advance(ProtocolSession.DefaultTimeout);
        await Assert.That(() => timedOut).Throws<TimeoutException>();
        var hung = transport.HangNextWrite();
        transport.QueueIncoming([.. FrameCodec.Encode(Data(1, 0x69)), .. FrameCodec.Encode(Ack(1)), .. FrameCodec.Encode(Data(0, 0x6A))]);
        await hung.WaitAsync(Patience);
        var next = session.SendAsync(new byte[] { 0x02 }, PatienceToken());
        transport.ReleaseHungWrite();
        await transport.WaitForWrittenFramesAsync(4, PatienceToken());

        time.Advance(ProtocolSession.DefaultTimeout);

        await Assert.That(() => next).Throws<TimeoutException>();
    }

    [Test]
    public async Task WhenLateAckArrivesDuringNextWriteThenFollowingSendStillToggles()
    {
        // Send A (seq 0) times out; its late ACK(1) arrives while send B (seq 1) is being written, and B's own ACK
        // is lost. Reads are capped at one ACK frame, so the trailing garbage byte is decoded in a later read and
        // its rejection proves the ACK was handled before B's write completes.
        var time = new FakeTimeProvider();
        var transport = new FakeTransport { MaxReadChunk = FrameCodec.Encode(Ack(1)).Length };
        var logger = new RejectionSignallingLogger();
        await using var session = new ProtocolSession(transport, logger, time);
        await session.StartAsync(CancellationToken.None);
        var sendA = session.SendAsync(new byte[] { 0x01 }, PatienceToken());
        await transport.WaitForWrittenFramesAsync(1, PatienceToken());
        time.Advance(ProtocolSession.DefaultTimeout);
        await Assert.That(() => sendA).Throws<TimeoutException>();
        var hung = transport.HangNextWrite();
        var sendB = session.SendAsync(new byte[] { 0x02 }, PatienceToken());
        await hung.WaitAsync(Patience);
        transport.QueueIncoming([.. FrameCodec.Encode(Ack(1)), 0xFF]);
        await logger.FrameRejected.WaitAsync(Patience);
        transport.ReleaseHungWrite();
        await transport.WaitForWrittenFramesAsync(2, PatienceToken());
        time.Advance(ProtocolSession.DefaultTimeout);
        await Assert.That(() => sendB).Throws<TimeoutException>();
        transport.AutoAck = true;

        await session.SendAsync(new byte[] { 0x03 }, PatienceToken());

        await Assert.That(transport.WrittenFrames.Select(f => f.Sequence)).IsEquivalentTo(
            new byte[] { 0, 1, 0 },
            TUnit.Assertions.Enums.CollectionOrdering.Matching);
    }

    [Test]
    public async Task WhenAckIsLostThenNextSendStillToggles()
    {
        var time = new FakeTimeProvider();
        var transport = new FakeTransport();
        await using var session = await StartSessionAsync(transport, time);
        var lost = session.SendAsync(new byte[] { 0x01 }, PatienceToken());
        await transport.WaitForWrittenFramesAsync(1, PatienceToken());
        time.Advance(ProtocolSession.DefaultTimeout);
        await Assert.That(() => lost).Throws<TimeoutException>();
        transport.AutoAck = true;

        await session.SendAsync(new byte[] { 0x02 }, PatienceToken());

        await Assert.That(transport.WrittenFrames[1].Sequence).IsEqualTo((byte)1);
    }

    [Test]
    public async Task WhenSendIsCancelledBeforeItsFrameIsWrittenThenSequenceIsNotConsumed()
    {
        // The receive loop's ACK write hangs and holds the write lock, so the send cannot start writing.
        var transport = new FakeTransport();
        await using var session = await StartSessionAsync(transport);
        var hung = transport.HangNextWrite();
        transport.QueueIncoming(Data(1, 0x69));
        await hung.WaitAsync(Patience);
        using var cancellation = new CancellationTokenSource();
        var cancelled = session.SendAsync(new byte[] { 0x01 }, cancellation.Token);
        await cancellation.CancelAsync();
        await Assert.That(() => cancelled).Throws<OperationCanceledException>();
        transport.ReleaseHungWrite();
        transport.AutoAck = true;

        await session.SendAsync(new byte[] { 0x02 }, PatienceToken());

        await Assert.That(transport.WrittenFrames.Single(f => f.Type == FrameType.DataMdr).Sequence).IsEqualTo((byte)0);
    }

    // The session logs decoder rejections at Warning; this is the only hook that fires after an ACK was handled.
    private sealed class RejectionSignallingLogger : ILogger<ProtocolSession>
    {
        private readonly TaskCompletionSource _rejected = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task FrameRejected => _rejected.Task;

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Warning;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Warning)
            {
                _rejected.TrySetResult();
            }
        }
    }
}
