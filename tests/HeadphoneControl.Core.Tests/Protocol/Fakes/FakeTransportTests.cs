using HeadphoneControl.Protocol.Framing;
using HeadphoneControl.Protocol.Transport;
using HeadphoneControl.Testing;

namespace HeadphoneControl.Protocol.Tests.Fakes;

public class FakeTransportTests
{
    [Test]
    public async Task WhenBytesAreQueuedThenReceiveReturnsThem()
    {
        var transport = new FakeTransport();
        transport.QueueIncoming([0x01, 0x02, 0x03]);
        var buffer = new byte[16];

        var read = await transport.ReceiveAsync(buffer, CancellationToken.None);

        await Assert.That(Convert.ToHexString(buffer, 0, read)).IsEqualTo("010203");
    }

    [Test]
    public async Task WhenMaxReadChunkIsSetThenReadsNeverExceedIt()
    {
        var transport = new FakeTransport { MaxReadChunk = 2 };
        transport.QueueIncoming([0x01, 0x02, 0x03]);

        var read = await transport.ReceiveAsync(new byte[16], CancellationToken.None);

        await Assert.That(read).IsEqualTo(2);
    }

    [Test]
    public async Task WhenNoDataIsQueuedThenReceiveWaitsUntilDataArrives()
    {
        var transport = new FakeTransport();
        var receive = transport.ReceiveAsync(new byte[16], CancellationToken.None);
        await Task.Delay(50);
        var completedEarly = receive.IsCompleted;

        transport.QueueIncoming([0x01]);
        await receive.WaitAsync(TimeSpan.FromSeconds(5));

        await Assert.That(completedEarly).IsFalse();
    }

    [Test]
    public async Task WhenPendingReceiveIsCancelledThenOperationCanceledExceptionIsThrown()
    {
        var transport = new FakeTransport();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));

        var act = () => transport.ReceiveAsync(new byte[16], cancellation.Token);

        await Assert.That(act).Throws<OperationCanceledException>();
    }

    [Test]
    public async Task WhenEofIsSimulatedThenReceiveReturnsZeroAfterQueuedBytes()
    {
        var transport = new FakeTransport();
        transport.QueueIncoming([0x01]);
        transport.SimulateEof();
        await transport.ReceiveAsync(new byte[16], CancellationToken.None);

        var read = await transport.ReceiveAsync(new byte[16], CancellationToken.None);

        await Assert.That(read).IsEqualTo(0);
    }

    [Test]
    public async Task WhenDisconnectedThenPendingReceiveThrowsTransportException()
    {
        var transport = new FakeTransport();
        var receive = transport.ReceiveAsync(new byte[16], CancellationToken.None);

        transport.SimulateDisconnect();

        await Assert.That(() => receive).Throws<TransportException>();
    }

    [Test]
    public async Task WhenLinkDroppedThenSendThrowsTransportException()
    {
        var transport = new FakeTransport();
        transport.SimulateDisconnect();

        var act = () => transport.SendAsync(new byte[] { 0x01 }, CancellationToken.None);

        await Assert.That(act).Throws<TransportException>();
    }

    [Test]
    public async Task WhenFrameIsWrittenThenWrittenFramesContainsItDecoded()
    {
        var transport = new FakeTransport();

        await transport.SendAsync(FrameCodec.Encode(new Frame(FrameType.DataMdr, 1, new byte[] { 0x22, 0x00 })), CancellationToken.None);

        await Assert.That(Convert.ToHexString(transport.WrittenFrames[0].Payload.Span)).IsEqualTo("2200");
    }

    [Test]
    public async Task WhenAutoAckIsOnThenWrittenDataFrameIsAnsweredWithAck()
    {
        var transport = new FakeTransport { AutoAck = true };
        await transport.SendAsync(FrameCodec.Encode(new Frame(FrameType.DataMdr, 0, new byte[] { 0x00 })), CancellationToken.None);
        var buffer = new byte[64];

        var read = await transport.ReceiveAsync(buffer, CancellationToken.None);

        await Assert.That(Convert.ToHexString(buffer, 0, read)).IsEqualTo("3E010100000000023C");
    }

    [Test]
    public async Task WhenHungWriteIsReleasedThenItIsRecorded()
    {
        var transport = new FakeTransport();
        var hung = transport.HangNextWrite();
        var send = transport.SendAsync(new byte[] { 0x01 }, CancellationToken.None);
        await hung.WaitAsync(TimeSpan.FromSeconds(5));

        transport.ReleaseHungWrite();
        await send.WaitAsync(TimeSpan.FromSeconds(5));

        await Assert.That(transport.Written.Select(Convert.ToHexString)).IsEquivalentTo(["01"]);
    }

    [Test]
    public async Task WhenHungWriteIsCancelledThenOperationCanceledExceptionIsThrown()
    {
        var transport = new FakeTransport();
        var hung = transport.HangNextWrite();
        using var cancellation = new CancellationTokenSource();
        var send = transport.SendAsync(new byte[] { 0x01 }, cancellation.Token);
        await hung.WaitAsync(TimeSpan.FromSeconds(5));

        await cancellation.CancelAsync();

        await Assert.That(() => send).Throws<OperationCanceledException>();
    }

    [Test]
    public async Task WhenReceiveFailureIsSetThenPendingReceiveThrowsIt()
    {
        var transport = new FakeTransport();
        var receive = transport.ReceiveAsync(new byte[16], CancellationToken.None);

        transport.FailReceive(new InvalidOperationException("simulated"));

        await Assert.That(() => receive).Throws<InvalidOperationException>();
    }

    [Test]
    public async Task WhenNextWriteIsFailedThenThatWriteIsNotRecorded()
    {
        var transport = new FakeTransport();
        transport.FailNextWrite(new TransportException("simulated"));
        await Assert.That(() => transport.SendAsync(new byte[] { 0x01 }, CancellationToken.None)).Throws<TransportException>();

        await transport.SendAsync(new byte[] { 0x02 }, CancellationToken.None);

        await Assert.That(transport.Written.Select(Convert.ToHexString)).IsEquivalentTo(["02"]);
    }
}
