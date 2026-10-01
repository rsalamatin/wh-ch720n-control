using System.Threading.Channels;
using HeadphoneControl.Protocol.Framing;
using HeadphoneControl.Protocol.Transport;
using Microsoft.Extensions.Logging;

namespace HeadphoneControl.Protocol.Session;

/// <summary>
/// Owns one connected <see cref="ITransport"/>: ACKs every non-ACK frame with <c>1 - deviceSeq</c> (the device
/// retransmits anything un-ACKed), matches DATA_MDR responses by opcode + subtype and raises the rest as
/// notifications. One request is in flight at a time. Throws <see cref="TimeoutException"/> when no ACK/response
/// arrives in time (no host-side retransmission; the caller decides whether to retry) and
/// <see cref="TransportException"/> when the link drops.
/// </summary>
/// <remarks>
/// <para>
/// Retransmitted DATA_MDR frames (same sequence) are ACKed and dropped. A response is matched only against frames
/// arriving after its request was registered, so a stale reply to a timed-out request is never returned.
/// A write interrupted after it started leaves the stream in an unknown state and ends the session.
/// </para>
/// <para>
/// Disposing the session disposes the transport. Events are raised in order from a separate dispatch task, never
/// on the receive loop, so a handler may await <see cref="RequestAsync"/> or <see cref="DisposeAsync"/>.
/// A handler exception is logged and affects neither other handlers nor the session.
/// </para>
/// </remarks>
public sealed class ProtocolSession : IAsyncDisposable
{
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(2);

    private const int ReceiveBufferSize = 1024;

    private readonly ITransport _transport;
    private readonly ILogger<ProtocolSession> _logger;
    private readonly TimeProvider _timeProvider;
    private readonly FrameDecoder _decoder = new();
    private readonly SemaphoreSlim _requestLock = new(1, 1);
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Channel<DispatchItem> _dispatch =
        Channel.CreateUnbounded<DispatchItem>(new UnboundedChannelOptions { SingleReader = true });
    private readonly Lock _gate = new();

    private Task? _receiveLoop;
    private Task? _dispatchPump;
    private byte _nextSequence;
    private byte? _expectedAckSequence;
    private byte? _lastDeviceSequence;
    private TaskCompletionSource<long>? _pendingAck;
    private PendingResponse? _pendingResponse;

    // Written only by the receive loop.
    private long _receivedFrames;

    private TransportException? _linkFailure;
    private bool _started;
    private bool _disposed;

    /// <summary>Creates a session over an already connected transport.</summary>
    public ProtocolSession(ITransport transport, ILogger<ProtocolSession> logger, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(transport);
        ArgumentNullException.ThrowIfNull(logger);
        _transport = transport;
        _logger = logger;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _decoder.FrameRejected += OnFrameRejected;
    }

    /// <summary>A DATA_MDR frame arrived that did not answer the pending request (unsolicited notification).</summary>
    public event EventHandler<ReceivedPayload>? NotificationReceived;

    /// <summary>The receive loop ended because the link dropped or the remote side closed it.</summary>
    public event EventHandler<Exception?>? Disconnected;

    /// <summary>Starts the background receive loop. Call once.</summary>
    public Task StartAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_started)
            {
                throw new InvalidOperationException("The session has already been started.");
            }

            _started = true;
        }

        _dispatchPump = Task.Run(PumpDispatchAsync, CancellationToken.None);
        _receiveLoop = Task.Run(() => ReceiveLoopAsync(_lifetime.Token), CancellationToken.None);
        return Task.CompletedTask;
    }

    /// <summary>Sends a DATA_MDR payload; completes with the ACK's <see cref="ReceivedPayload.Ordinal"/> when the device ACKs it.</summary>
    public async Task<long> SendAsync(ReadOnlyMemory<byte> payload, CancellationToken cancellationToken, TimeSpan? timeout = null)
    {
        var wait = ValidateTimeout(timeout);
        await _requestLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var ack = new TaskCompletionSource<long>(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (_gate)
            {
                ThrowIfUnusable();
                _pendingAck = ack;

                // The expectation is set only once this send's sequence is chosen, so a late ACK for a timed-out
                // send cannot complete this one before its frame is written.
                _expectedAckSequence = null;
            }

            await ExchangeAsync(payload, ack.Task, wait, "an ACK", cancellationToken).ConfigureAwait(false);
            return await ack.Task.ConfigureAwait(false);
        }
        finally
        {
            lock (_gate)
            {
                _pendingAck = null;
                _expectedAckSequence = null;
            }

            _requestLock.Release();
        }
    }

    /// <summary>
    /// Sends a DATA_MDR payload and returns the first DATA_MDR frame whose byte 0 is <paramref name="responseOpcode"/>
    /// and, when given, whose byte 1 is <paramref name="responseSubtype"/>.
    /// </summary>
    public async Task<ReceivedPayload> RequestAsync(
        ReadOnlyMemory<byte> payload,
        byte responseOpcode,
        byte? responseSubtype,
        CancellationToken cancellationToken,
        TimeSpan? timeout = null)
    {
        var wait = ValidateTimeout(timeout);
        await _requestLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var pending = new PendingResponse(responseOpcode, responseSubtype);
            lock (_gate)
            {
                ThrowIfUnusable();
                _pendingResponse = pending;
            }

            await ExchangeAsync(payload, pending.Completion.Task, wait, $"a response with opcode 0x{responseOpcode:X2}", cancellationToken)
                .ConfigureAwait(false);
            return await pending.Completion.Task.ConfigureAwait(false);
        }
        finally
        {
            lock (_gate)
            {
                _pendingResponse = null;
            }

            _requestLock.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
        }

        FailPending(new TransportException("The session was disposed."));
        await _lifetime.CancelAsync().ConfigureAwait(false);
        _dispatch.Writer.TryComplete();

        // Disposing the transport unblocks a receive that does not honour cancellation.
        await _transport.DisposeAsync().ConfigureAwait(false);

        // The semaphores and CTS are deliberately not disposed: a just-failed caller may still be releasing them,
        // and none owns a wait handle or timer that would leak.
        if (_receiveLoop is not null)
        {
            await _receiveLoop.ConfigureAwait(false);
        }

        if (_dispatchPump is not null)
        {
            await _dispatchPump.ConfigureAwait(false);
        }
    }

    private static TimeSpan ValidateTimeout(TimeSpan? timeout)
    {
        var value = timeout ?? DefaultTimeout;
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(value, TimeSpan.Zero, nameof(timeout));
        return value;
    }

    private static bool Matches(Frame frame, PendingResponse pending)
    {
        var payload = frame.Payload.Span;
        return payload.Length >= 1
            && payload[0] == pending.Opcode
            && (pending.Subtype is not { } subtype || (payload.Length >= 2 && payload[1] == subtype));
    }

    // One deadline bounds both the write and the wait for completion.
    private async Task ExchangeAsync(
        ReadOnlyMemory<byte> payload,
        Task completion,
        TimeSpan timeout,
        string what,
        CancellationToken cancellationToken)
    {
        using var timeoutSource = new CancellationTokenSource(timeout, _timeProvider);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutSource.Token, _lifetime.Token);
        try
        {
            await WriteDataAsync(payload, linked.Token).ConfigureAwait(false);
            await completion.WaitAsync(linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested && timeoutSource.IsCancellationRequested)
        {
            throw new TimeoutException($"The device did not send {what} within {timeout.TotalMilliseconds:0} ms.", ex);
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw LinkDown(ex);
        }
    }

    private TransportException LinkDown(Exception cause)
    {
        lock (_gate)
        {
            return _linkFailure is not null
                ? new TransportException("The link to the device is down.", _linkFailure)
                : new TransportException("The session was disposed.", cause);
        }
    }

    // Caller holds _gate.
    private void ThrowIfUnusable()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_linkFailure is not null)
        {
            throw new TransportException("The link to the device is down.", _linkFailure);
        }

        if (!_started)
        {
            throw new InvalidOperationException("StartAsync must be called before sending.");
        }
    }

    private async Task WriteDataAsync(ReadOnlyMemory<byte> payload, CancellationToken cancellationToken)
    {
        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            byte sequence;
            lock (_gate)
            {
                sequence = _nextSequence;
                _expectedAckSequence = (byte)(1 - sequence);
            }

            await WriteToTransportAsync(new Frame(FrameType.DataMdr, sequence, payload), cancellationToken).ConfigureAwait(false);

            // Consumed only once the frame is on the wire. Deliberately no resync from ACKs (unlike
            // SonyProtocolSession.cpp:199): any ACK other than 1 - n is late, and resyncing from it would reuse this
            // sequence, which the device drops as a duplicate.
            lock (_gate)
            {
                _nextSequence = (byte)(1 - sequence);
            }
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private async Task WriteAckAsync(byte sequence, CancellationToken cancellationToken)
    {
        using var timeoutSource = new CancellationTokenSource(DefaultTimeout, _timeProvider);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutSource.Token);
        await _writeLock.WaitAsync(linked.Token).ConfigureAwait(false);
        try
        {
            await WriteToTransportAsync(new Frame(FrameType.Ack, sequence, ReadOnlyMemory<byte>.Empty), linked.Token)
                .ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    // Caller holds _writeLock.
    private async Task WriteToTransportAsync(Frame frame, CancellationToken cancellationToken)
    {
        var bytes = FrameCodec.Encode(frame);
        cancellationToken.ThrowIfCancellationRequested();

        if (_logger.IsEnabled(LogLevel.Debug))
        {
            SessionLog.Transmitted(_logger, Convert.ToHexString(bytes));
        }

        try
        {
            await _transport.SendAsync(bytes, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException ex)
        {
            // Part of the frame may already be on the wire, and a cancelled RFCOMM write can leave the socket
            // unusable, so the stream can no longer be trusted.
            OnLinkLost(new TransportException("A write was interrupted; the link state is unknown.", ex));
            throw;
        }
        catch (TransportException ex)
        {
            OnLinkLost(ex);
            throw;
        }
        catch (ObjectDisposedException ex)
        {
            var failure = new TransportException("The transport was closed during a write.", ex);
            OnLinkLost(failure);
            throw failure;
        }
    }

    private async Task ReceiveLoopAsync(CancellationToken cancellationToken)
    {
        var buffer = new byte[ReceiveBufferSize];
        Exception? cause = null;
        try
        {
            while (true)
            {
                var read = await _transport.ReceiveAsync(buffer, cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    cause = new TransportException("The device closed the connection.");
                    break;
                }

                if (_logger.IsEnabled(LogLevel.Debug))
                {
                    SessionLog.Received(_logger, Convert.ToHexString(buffer, 0, read));
                }

                foreach (var frame in _decoder.Feed(buffer.AsSpan(0, read)))
                {
                    await HandleFrameAsync(frame, ++_receivedFrames, cancellationToken).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Stopped on purpose by DisposeAsync or after a failed write, which already reported the loss.
        }
        catch (Exception ex)
        {
            // Top of an unawaited background task: any cause must surface as a disconnect, not an unobserved fault.
            cause = ex;
        }

        if (cause is not null)
        {
            OnLinkLost(cause);
        }
    }

    private async Task HandleFrameAsync(Frame frame, long ordinal, CancellationToken cancellationToken)
    {
        if (frame.Type == FrameType.Ack)
        {
            bool matched;
            lock (_gate)
            {
                // Only an ACK carrying 1 - sentSequence of the frame in flight completes the send.
                matched = frame.Sequence == _expectedAckSequence;
                if (matched)
                {
                    _pendingAck?.TrySetResult(ordinal);
                }
            }

            if (!matched)
            {
                SessionLog.LateAckIgnored(_logger, frame.Sequence);
            }

            return;
        }

        // Sony ACKs are type-generic and the device retransmits anything un-ACKed, so every non-ACK frame is
        // ACKed (duplicates too) before deciding what to do with it.
        await WriteAckAsync((byte)(1 - (frame.Sequence & 1)), cancellationToken).ConfigureAwait(false);

        if (frame.Type == FrameType.DataMdr)
        {
            DispatchData(frame, ordinal);
        }
        else
        {
            SessionLog.FrameIgnored(_logger, (byte)frame.Type, frame.Sequence);
        }
    }

    private void DispatchData(Frame frame, long ordinal)
    {
        var received = new ReceivedPayload(frame.Payload, ordinal);
        bool duplicate;
        lock (_gate)
        {
            duplicate = _lastDeviceSequence == frame.Sequence;
            _lastDeviceSequence = frame.Sequence;

            if (!duplicate && _pendingResponse is { } pending && Matches(frame, pending)
                && pending.Completion.TrySetResult(received))
            {
                return;
            }
        }

        if (duplicate)
        {
            SessionLog.DuplicateDropped(_logger, frame.Sequence);
            return;
        }

        _dispatch.Writer.TryWrite(new DispatchItem(received, null));
    }

    private async Task PumpDispatchAsync()
    {
        await foreach (var item in _dispatch.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            if (item.Notification is { } notification)
            {
                Raise(NotificationReceived, notification, nameof(NotificationReceived));
            }
            else
            {
                Raise(Disconnected, item.DisconnectCause, nameof(Disconnected));
            }
        }
    }

    private void Raise<T>(EventHandler<T>? handlers, T args, string eventName)
    {
        if (handlers is null)
        {
            return;
        }

        foreach (var handler in handlers.GetInvocationList().Cast<EventHandler<T>>())
        {
            try
            {
                handler(this, args);
            }
            catch (Exception ex)
            {
                // Event boundary: one faulty subscriber must not starve the others or stop dispatch.
                SessionLog.HandlerFailed(_logger, eventName, ex);
            }
        }
    }

    private void OnLinkLost(Exception cause)
    {
        TransportException failure;
        lock (_gate)
        {
            if (_linkFailure is not null || _disposed)
            {
                return;
            }

            _linkFailure = cause as TransportException ?? new TransportException("The link to the device was lost.", cause);
            failure = _linkFailure;
        }

        SessionLog.LinkLost(_logger, cause);
        FailPending(failure);
        _lifetime.Cancel();
        _dispatch.Writer.TryWrite(new DispatchItem(null, cause));
        _dispatch.Writer.TryComplete();
    }

    private void FailPending(TransportException error)
    {
        lock (_gate)
        {
            _pendingAck?.TrySetException(error);
            _pendingResponse?.Completion.TrySetException(error);
        }
    }

    private void OnFrameRejected(object? sender, FrameRejectedEventArgs e)
    {
        if (_logger.IsEnabled(LogLevel.Warning))
        {
            SessionLog.FrameRejected(_logger, e.Reason, Convert.ToHexString(e.RawBytes.Span));
        }
    }

    // Exactly one of the two is set: a notification to raise, or the cause of the disconnect.
    private readonly record struct DispatchItem(ReceivedPayload? Notification, Exception? DisconnectCause);

    private sealed class PendingResponse(byte opcode, byte? subtype)
    {
        public byte Opcode { get; } = opcode;

        public byte? Subtype { get; } = subtype;

        public TaskCompletionSource<ReceivedPayload> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
