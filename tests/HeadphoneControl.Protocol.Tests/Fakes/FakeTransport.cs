using HeadphoneControl.Protocol.Framing;
using HeadphoneControl.Protocol.Transport;

namespace HeadphoneControl.Protocol.Tests.Fakes;

/// <summary>
/// In-memory <see cref="ITransport"/> for hardware-free tests (port of the reference <c>FakeTransport</c>).
/// Thread-safe. Inbound bytes are scripted with <see cref="QueueIncoming(ReadOnlySpan{byte})"/> /
/// <see cref="QueueIncoming(Frame)"/>; everything the code under test writes is recorded in <see cref="Written"/>
/// and, decoded, in <see cref="WrittenFrames"/>.
/// </summary>
/// <remarks>
/// <see cref="ReceiveAsync"/> behaves like a real socket: it blocks until bytes are queued, EOF is simulated,
/// the link is dropped, or the call is cancelled. Set <see cref="AutoAck"/> and/or <see cref="Responder"/> to
/// have the fake answer written frames like a headset would.
/// </remarks>
public sealed class FakeTransport : ITransport
{
    private readonly Lock _gate = new();
    private readonly Queue<byte> _incoming = new();
    private readonly List<byte[]> _written = [];
    private readonly List<Frame> _writtenFrames = [];
    private readonly FrameDecoder _writeDecoder = new();

    private TaskCompletionSource _incomingChanged = NewSignal();
    private TaskCompletionSource _writtenChanged = NewSignal();

    // Connected from the start, like every transport a connector hands out.
    private bool _connected = true;
    private bool _eof;
    private bool _disposed;
    private Exception? _nextWriteFailure;
    private Exception? _receiveFailure;
    private TaskCompletionSource? _hangNextWrite;
    private TaskCompletionSource? _releaseHungWrite;

    public bool IsDisposed
    {
        get
        {
            lock (_gate)
            {
                return _disposed;
            }
        }
    }

    /// <summary>Upper bound on the bytes returned by one <see cref="ReceiveAsync"/> call (simulates fragmentation).</summary>
    public int? MaxReadChunk { get; set; }

    /// <summary>When true, every written DATA_MDR frame is answered with an ACK carrying <c>1 - seq</c>.</summary>
    public bool AutoAck { get; set; }

    /// <summary>
    /// Called for every frame the code under test writes (after the optional auto ACK is queued); the returned
    /// frames are queued as inbound data, in order. Use it to script request/response exchanges.
    /// </summary>
    public Func<Frame, IEnumerable<Frame>>? Responder { get; set; }

    /// <summary>Snapshot of every <see cref="SendAsync"/> buffer, one entry per call.</summary>
    public IReadOnlyList<byte[]> Written
    {
        get
        {
            lock (_gate)
            {
                return [.. _written];
            }
        }
    }

    /// <summary>Snapshot of the frames decoded from everything written so far, in order.</summary>
    public IReadOnlyList<Frame> WrittenFrames
    {
        get
        {
            lock (_gate)
            {
                return [.. _writtenFrames];
            }
        }
    }

    public int IncomingBytesAvailable
    {
        get
        {
            lock (_gate)
            {
                return _incoming.Count;
            }
        }
    }

    public async Task SendAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        TaskCompletionSource? hungSignal;
        TaskCompletionSource? release = null;
        lock (_gate)
        {
            hungSignal = _hangNextWrite;
            _hangNextWrite = null;
            if (hungSignal is not null)
            {
                release = _releaseHungWrite = NewSignal();
            }
        }

        if (hungSignal is not null)
        {
            hungSignal.TrySetResult();

            // A cancelled hung write records nothing, like a socket write aborted before any byte left.
            await release!.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }

        Record(data);
    }

    private void Record(ReadOnlyMemory<byte> data)
    {
        TaskCompletionSource writtenSignal;
        TaskCompletionSource? incomingSignal = null;
        var queued = false;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!_connected)
            {
                throw new TransportException("FakeTransport is not connected.");
            }

            if (_nextWriteFailure is { } failure)
            {
                _nextWriteFailure = null;
                throw failure;
            }

            var bytes = data.ToArray();
            _written.Add(bytes);

            foreach (var frame in _writeDecoder.Feed(bytes))
            {
                _writtenFrames.Add(frame);
                if (AutoAck && frame.Type == FrameType.DataMdr)
                {
                    Enqueue(FrameCodec.Encode(new Frame(FrameType.Ack, (byte)(1 - (frame.Sequence & 1)), ReadOnlyMemory<byte>.Empty)));
                    queued = true;
                }

                if (Responder?.Invoke(frame) is { } replies)
                {
                    foreach (var reply in replies)
                    {
                        Enqueue(FrameCodec.Encode(reply));
                        queued = true;
                    }
                }
            }

            if (queued)
            {
                incomingSignal = SwapIncomingSignal();
            }

            writtenSignal = _writtenChanged;
            _writtenChanged = NewSignal();
        }

        incomingSignal?.TrySetResult();
        writtenSignal.TrySetResult();
    }

    public async Task<int> ReceiveAsync(Memory<byte> buffer, CancellationToken cancellationToken)
    {
        while (true)
        {
            Task changed;
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                if (_receiveFailure is { } failure)
                {
                    throw failure;
                }

                if (!_connected)
                {
                    throw new TransportException("FakeTransport is not connected.");
                }

                if (_incoming.Count > 0)
                {
                    var count = Math.Min(buffer.Length, _incoming.Count);
                    if (MaxReadChunk is > 0 and var max)
                    {
                        count = Math.Min(count, max);
                    }

                    var span = buffer.Span;
                    for (var i = 0; i < count; i++)
                    {
                        span[i] = _incoming.Dequeue();
                    }

                    return count;
                }

                if (_eof)
                {
                    return 0;
                }

                changed = _incomingChanged.Task;
            }

            await changed.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Queues raw inbound bytes (may be partial frames, garbage, or several frames).</summary>
    public void QueueIncoming(ReadOnlySpan<byte> bytes)
    {
        TaskCompletionSource signal;
        lock (_gate)
        {
            foreach (var b in bytes)
            {
                _incoming.Enqueue(b);
            }

            signal = SwapIncomingSignal();
        }

        signal.TrySetResult();
    }

    /// <summary>Encodes <paramref name="frame"/> with <see cref="FrameCodec"/> and queues it as inbound bytes.</summary>
    public void QueueIncoming(Frame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        QueueIncoming(FrameCodec.Encode(frame));
    }

    /// <summary>The remote side closes the link: once queued bytes are drained, <see cref="ReceiveAsync"/> returns 0.</summary>
    public void SimulateEof()
    {
        TaskCompletionSource signal;
        lock (_gate)
        {
            _eof = true;
            signal = SwapIncomingSignal();
        }

        signal.TrySetResult();
    }

    /// <summary>The link drops: pending and future reads and writes throw <see cref="TransportException"/>.</summary>
    public void SimulateDisconnect()
    {
        TaskCompletionSource signal;
        lock (_gate)
        {
            _connected = false;
            signal = SwapIncomingSignal();
        }

        signal.TrySetResult();
    }

    /// <summary>Makes the next <see cref="SendAsync"/> throw <paramref name="exception"/> without recording anything.</summary>
    public void FailNextWrite(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        lock (_gate)
        {
            _nextWriteFailure = exception;
        }
    }

    /// <summary>
    /// Makes the next <see cref="SendAsync"/> block until <see cref="ReleaseHungWrite"/> is called (then it is
    /// recorded normally) or its token is cancelled (then nothing is recorded). The returned task completes once
    /// that write is blocked.
    /// </summary>
    public Task HangNextWrite()
    {
        var hung = NewSignal();
        lock (_gate)
        {
            _hangNextWrite = hung;
        }

        return hung.Task;
    }

    /// <summary>Lets the write blocked by <see cref="HangNextWrite"/> complete.</summary>
    public void ReleaseHungWrite()
    {
        TaskCompletionSource? release;
        lock (_gate)
        {
            release = _releaseHungWrite;
            _releaseHungWrite = null;
        }

        release?.TrySetResult();
    }

    /// <summary>Pending and future <see cref="ReceiveAsync"/> calls throw <paramref name="exception"/> (e.g. a platform bug).</summary>
    public void FailReceive(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        TaskCompletionSource signal;
        lock (_gate)
        {
            _receiveFailure = exception;
            signal = SwapIncomingSignal();
        }

        signal.TrySetResult();
    }

    /// <summary>Completes once at least <paramref name="count"/> frames have been written.</summary>
    public async Task WaitForWrittenFramesAsync(int count, CancellationToken cancellationToken)
    {
        while (true)
        {
            Task changed;
            lock (_gate)
            {
                if (_writtenFrames.Count >= count)
                {
                    return;
                }

                changed = _writtenChanged.Task;
            }

            await changed.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    public ValueTask DisposeAsync()
    {
        TaskCompletionSource signal;
        lock (_gate)
        {
            _disposed = true;
            _connected = false;
            signal = SwapIncomingSignal();
        }

        signal.TrySetResult();
        return ValueTask.CompletedTask;
    }

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    // Caller holds _gate.
    private void Enqueue(byte[] bytes)
    {
        foreach (var b in bytes)
        {
            _incoming.Enqueue(b);
        }
    }

    // Caller holds _gate; the returned signal must be completed after the lock is released.
    private TaskCompletionSource SwapIncomingSignal()
    {
        var signal = _incomingChanged;
        _incomingChanged = NewSignal();
        return signal;
    }
}
