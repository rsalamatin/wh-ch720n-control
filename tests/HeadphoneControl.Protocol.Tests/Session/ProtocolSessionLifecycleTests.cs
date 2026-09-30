using HeadphoneControl.Protocol.Session;
using HeadphoneControl.Protocol.Tests.Fakes;
using HeadphoneControl.Protocol.Transport;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using static HeadphoneControl.Protocol.Tests.Session.SessionTestHelpers;

namespace HeadphoneControl.Protocol.Tests.Session;

public class ProtocolSessionLifecycleTests
{
    [Test]
    public async Task WhenTransportIsNullThenConstructorThrowsArgumentNullException()
    {
        ITransport transport = null!;

        var act = () => new ProtocolSession(transport, NullLogger<ProtocolSession>.Instance);

        await Assert.That(act).Throws<ArgumentNullException>();
    }

    [Test]
    public async Task WhenStartedTwiceThenInvalidOperationExceptionIsThrown()
    {
        await using var session = await StartSessionAsync(new FakeTransport());

        var act = () => session.StartAsync(CancellationToken.None);

        await Assert.That(act).Throws<InvalidOperationException>();
    }

    [Test]
    public async Task WhenSendingBeforeStartThenInvalidOperationExceptionIsThrown()
    {
        await using var session = new ProtocolSession(new FakeTransport(), NullLogger<ProtocolSession>.Instance);

        var act = () => session.SendAsync(new byte[] { 0x00, 0x00 }, CancellationToken.None);

        await Assert.That(act).Throws<InvalidOperationException>();
    }

    [Test]
    public async Task WhenTimeoutIsNotPositiveThenArgumentOutOfRangeExceptionIsThrown()
    {
        await using var session = await StartSessionAsync(new FakeTransport());

        var act = () => session.SendAsync(new byte[] { 0x00 }, CancellationToken.None, TimeSpan.Zero);

        await Assert.That(act).Throws<ArgumentOutOfRangeException>();
    }

    [Test]
    public async Task WhenCallerCancelsThenRequestThrowsOperationCanceledException()
    {
        await using var session = await StartSessionAsync(new FakeTransport());
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));

        var act = () => session.RequestAsync(new byte[] { 0x22, 0x00 }, 0x23, null, cancellation.Token);

        await Assert.That(act).Throws<OperationCanceledException>();
    }

    [Test]
    public async Task WhenDeviceClosesLinkDuringRequestThenRequestThrowsTransportException()
    {
        var transport = new FakeTransport();
        await using var session = await StartSessionAsync(transport);
        var request = session.RequestAsync(new byte[] { 0x22, 0x00 }, 0x23, null, PatienceToken());
        await transport.WaitForWrittenFramesAsync(1, PatienceToken());

        transport.SimulateEof();

        await Assert.That(() => request).Throws<TransportException>();
    }

    [Test]
    public async Task WhenLinkDropsDuringSendThenSendThrowsTransportException()
    {
        var transport = new FakeTransport();
        await using var session = await StartSessionAsync(transport);
        var send = session.SendAsync(new byte[] { 0x00, 0x00 }, PatienceToken());
        await transport.WaitForWrittenFramesAsync(1, PatienceToken());

        transport.SimulateDisconnect();

        await Assert.That(() => send).Throws<TransportException>();
    }

    [Test]
    public async Task WhenDeviceClosesLinkThenDisconnectedIsRaised()
    {
        var transport = new FakeTransport();
        await using var session = await StartSessionAsync(transport);
        var disconnected = new TaskCompletionSource<Exception?>(TaskCreationOptions.RunContinuationsAsynchronously);
        session.Disconnected += (_, cause) => disconnected.TrySetResult(cause);

        transport.SimulateEof();
        var cause = await disconnected.Task.WaitAsync(Patience);

        await Assert.That(cause).IsTypeOf<TransportException>();
    }

    [Test]
    public async Task WhenLinkIsDownThenNextCallThrowsTransportExceptionImmediately()
    {
        var transport = new FakeTransport();
        await using var session = await StartSessionAsync(transport);
        var disconnected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        session.Disconnected += (_, _) => disconnected.TrySetResult();
        transport.SimulateEof();
        await disconnected.Task.WaitAsync(Patience);

        var act = () => session.SendAsync(new byte[] { 0x00, 0x00 }, PatienceToken());

        await Assert.That(act).Throws<TransportException>();
    }

    [Test]
    public async Task WhenWriteFailsThenSendThrowsTransportException()
    {
        var transport = new FakeTransport();
        await using var session = await StartSessionAsync(transport);
        transport.FailNextWrite(new TransportException("simulated write failure"));

        var act = () => session.SendAsync(new byte[] { 0x00, 0x00 }, PatienceToken());

        await Assert.That(act).Throws<TransportException>();
    }

    [Test]
    public async Task WhenWriteFailsThenDisconnectedIsRaised()
    {
        var transport = new FakeTransport();
        await using var session = await StartSessionAsync(transport);
        var disconnected = new TaskCompletionSource<Exception?>(TaskCreationOptions.RunContinuationsAsynchronously);
        session.Disconnected += (_, cause) => disconnected.TrySetResult(cause);
        var failure = new TransportException("simulated write failure");
        transport.FailNextWrite(failure);

        await Assert.That(() => session.SendAsync(new byte[] { 0x00, 0x00 }, PatienceToken())).Throws<TransportException>();
        var cause = await disconnected.Task.WaitAsync(Patience);

        await Assert.That(cause).IsSameReferenceAs(failure);
    }

    [Test]
    public async Task WhenReceiveFailsUnexpectedlyThenDisconnectedCarriesTheCause()
    {
        var transport = new FakeTransport();
        await using var session = await StartSessionAsync(transport);
        var disconnected = new TaskCompletionSource<Exception?>(TaskCreationOptions.RunContinuationsAsynchronously);
        session.Disconnected += (_, cause) => disconnected.TrySetResult(cause);
        var failure = new InvalidOperationException("platform bug");

        transport.FailReceive(failure);
        var cause = await disconnected.Task.WaitAsync(Patience);

        await Assert.That(cause).IsSameReferenceAs(failure);
    }

    [Test]
    public async Task WhenDisconnectedHandlerDisposesSessionThenDisposeCompletes()
    {
        var transport = new FakeTransport();
        var session = await StartSessionAsync(transport);
        var disposing = new TaskCompletionSource<Task>(TaskCreationOptions.RunContinuationsAsynchronously);
        session.Disconnected += (_, _) => disposing.TrySetResult(session.DisposeAsync().AsTask());

        transport.SimulateEof();
        var dispose = await disposing.Task.WaitAsync(Patience);

        await Assert.That(() => dispose.WaitAsync(Patience)).ThrowsNothing();
    }

    [Test]
    public async Task WhenDisconnectedHandlerThrowsThenDisposeDoesNotThrow()
    {
        var transport = new FakeTransport();
        var session = await StartSessionAsync(transport);
        var raised = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        session.Disconnected += (_, _) => throw new InvalidOperationException("faulty subscriber");
        session.Disconnected += (_, _) => raised.TrySetResult();
        transport.SimulateEof();
        await raised.Task.WaitAsync(Patience);

        var act = async () => await session.DisposeAsync();

        await Assert.That(act).ThrowsNothing();
    }

    [Test]
    public async Task WhenWriteHangsPastTimeoutThenSendThrowsTimeoutException()
    {
        var time = new FakeTimeProvider();
        var transport = new FakeTransport();
        await using var session = await StartSessionAsync(transport, time);
        var hung = transport.HangNextWrite();
        var send = session.SendAsync(new byte[] { 0x00, 0x00 }, PatienceToken());
        await hung.WaitAsync(Patience);

        time.Advance(ProtocolSession.DefaultTimeout);

        await Assert.That(() => send).Throws<TimeoutException>();
    }

    [Test]
    public async Task WhenWriteIsInterruptedThenDisconnectedIsRaised()
    {
        var time = new FakeTimeProvider();
        var transport = new FakeTransport();
        await using var session = await StartSessionAsync(transport, time);
        var disconnected = new TaskCompletionSource<Exception?>(TaskCreationOptions.RunContinuationsAsynchronously);
        session.Disconnected += (_, cause) => disconnected.TrySetResult(cause);
        var hung = transport.HangNextWrite();
        var send = session.SendAsync(new byte[] { 0x00, 0x00 }, PatienceToken());
        await hung.WaitAsync(Patience);

        time.Advance(ProtocolSession.DefaultTimeout);
        var cause = await disconnected.Task.WaitAsync(Patience);

        await Assert.That(cause).IsTypeOf<TransportException>();
    }

    [Test]
    public async Task WhenDisposedDuringHungWriteThenSendThrowsTransportException()
    {
        var transport = new FakeTransport();
        var session = await StartSessionAsync(transport);
        var hung = transport.HangNextWrite();
        var send = session.SendAsync(new byte[] { 0x00, 0x00 }, PatienceToken());
        await hung.WaitAsync(Patience);

        await session.DisposeAsync();

        await Assert.That(() => send).Throws<TransportException>();
    }

    [Test]
    public async Task WhenDisposedWhileReaderIsBlockedThenDisposeCompletesWithinOneSecond()
    {
        var session = await StartSessionAsync(new FakeTransport());

        var dispose = session.DisposeAsync().AsTask();

        await Assert.That(() => dispose.WaitAsync(TimeSpan.FromSeconds(1))).ThrowsNothing();
    }

    [Test]
    public async Task WhenDisposedWithPendingRequestThenRequestThrowsTransportException()
    {
        var transport = new FakeTransport();
        var session = await StartSessionAsync(transport);
        var request = session.RequestAsync(new byte[] { 0x22, 0x00 }, 0x23, null, PatienceToken());
        await transport.WaitForWrittenFramesAsync(1, PatienceToken());

        await session.DisposeAsync();

        await Assert.That(() => request).Throws<TransportException>();
    }

    [Test]
    public async Task WhenDisposedThenDisconnectedIsNotRaised()
    {
        var session = await StartSessionAsync(new FakeTransport());
        var raised = false;
        session.Disconnected += (_, _) => raised = true;

        await session.DisposeAsync();

        await Assert.That(raised).IsFalse();
    }

    [Test]
    public async Task WhenDisposedThenTransportIsDisposed()
    {
        var transport = new FakeTransport();
        var session = await StartSessionAsync(transport);

        await session.DisposeAsync();

        await Assert.That(transport.IsDisposed).IsTrue();
    }

    [Test]
    public async Task WhenCalledAfterDisposeThenObjectDisposedExceptionIsThrown()
    {
        var session = await StartSessionAsync(new FakeTransport());
        await session.DisposeAsync();

        var act = () => session.SendAsync(new byte[] { 0x00, 0x00 }, CancellationToken.None);

        await Assert.That(act).Throws<ObjectDisposedException>();
    }

    [Test]
    public async Task WhenDisposedTwiceThenSecondDisposeDoesNothing()
    {
        var session = await StartSessionAsync(new FakeTransport());
        await session.DisposeAsync();

        var act = async () => await session.DisposeAsync();

        await Assert.That(act).ThrowsNothing();
    }
}
