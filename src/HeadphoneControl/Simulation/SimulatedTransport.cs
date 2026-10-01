using System.Threading.Channels;
using HeadphoneControl.Protocol.Framing;
using HeadphoneControl.Protocol.Transport;

namespace HeadphoneControl.Simulation;

// One link to a SimulatedHeadset: decodes our frames, ACKs each with 1 - seq, and sends the headset's replies and
// notifications as DATA_MDR frames with its own alternating sequence. Unlike the real headset it never retransmits
// an un-ACKed frame and never waits for our ACK before its next frame.
internal sealed class SimulatedTransport : ITransport
{
    // The real headset sends its noise-control echo about 0.5 s after the ACK of the SET; the DSEE echo is assumed to
    // follow the same delay. Timed from delivering the ACK, so an echo can never overtake it.
    private static readonly TimeSpan NotificationDelay = TimeSpan.FromMilliseconds(500);

    private readonly SimulatedHeadset _headset;
    private readonly TimeSpan _latency;
    private readonly TimeProvider _timeProvider;
    private readonly Action<SimulatedTransport> _closed;
    private readonly FrameDecoder _decoder = new();
    private readonly Channel<byte[]> _inbound = Channel.CreateUnbounded<byte[]>(new UnboundedChannelOptions { SingleReader = true });
    private readonly Channel<Response> _delayedResponses =
        Channel.CreateUnbounded<Response>(new UnboundedChannelOptions { SingleReader = true });
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Lock _gate = new();
    private readonly Task _responsePump;

    private byte _nextSequence;
    private bool _disposed;

    // Only touched by the single reader.
    private ReadOnlyMemory<byte> _pending;

    public SimulatedTransport(
        SimulatedHeadset headset, TimeSpan latency, TimeProvider timeProvider, Action<SimulatedTransport> closed)
    {
        _headset = headset;
        _latency = latency;
        _timeProvider = timeProvider;
        _closed = closed;
        _responsePump = latency > TimeSpan.Zero ? Task.Run(PumpDelayedResponsesAsync) : Task.CompletedTask;
    }

    // Never blocks: the session also writes its ACKs from its receive loop.
    public Task SendAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (_disposed)
            {
                throw new TransportException("The simulated link is closed.");
            }

            foreach (var frame in _decoder.Feed(data.Span))
            {
                if (frame.Type != FrameType.Ack)
                {
                    Respond(frame);
                }
            }
        }

        return Task.CompletedTask;
    }

    public async Task<int> ReceiveAsync(Memory<byte> buffer, CancellationToken cancellationToken)
    {
        if (_pending.IsEmpty)
        {
            if (!await _inbound.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false)
                || !_inbound.Reader.TryRead(out var chunk))
            {
                return 0;
            }

            _pending = chunk;
        }

        var count = Math.Min(buffer.Length, _pending.Length);
        _pending[..count].CopyTo(buffer);
        _pending = _pending[count..];
        return count;
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

        try
        {
            await _lifetime.CancelAsync().ConfigureAwait(false);
            _delayedResponses.Writer.TryComplete();
            _inbound.Writer.TryComplete();
            await _responsePump.ConfigureAwait(false);
        }
        finally
        {
            // Even if the pump faulted, the connector must not stay busy with a closed link.
            _closed(this);
        }
    }

    // The headset ACKs before it replies, as captured on the real device.
    private void Respond(Frame request)
    {
        var reaction = request.Type == FrameType.DataMdr && !request.Payload.IsEmpty
            ? _headset.Handle(request.Payload.Span)
            : SimulatedHeadset.Reaction.AckOnly;

        List<Frame> frames = [new Frame(FrameType.Ack, (byte)(1 - (request.Sequence & 1)), ReadOnlyMemory<byte>.Empty)];
        if (reaction.Reply is { } reply)
        {
            frames.Add(new Frame(FrameType.DataMdr, 0, reply));
        }

        var response = new Response(frames, reaction.Notifications);
        if (_latency > TimeSpan.Zero)
        {
            _delayedResponses.Writer.TryWrite(response);
        }
        else
        {
            Deliver(response);
        }
    }

    private async Task PumpDelayedResponsesAsync()
    {
        try
        {
            await foreach (var response in _delayedResponses.Reader.ReadAllAsync(_lifetime.Token).ConfigureAwait(false))
            {
                await Task.Delay(_latency, _timeProvider, _lifetime.Token).ConfigureAwait(false);
                lock (_gate)
                {
                    Deliver(response);
                }
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
            // The link was closed; undelivered responses go with it.
        }
    }

    private async Task NotifyLaterAsync(byte[] payload)
    {
        try
        {
            await Task.Delay(NotificationDelay, _timeProvider, _lifetime.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // The link was closed before the notification was due; the real headset would not send it either.
            return;
        }

        lock (_gate)
        {
            Deliver(new Response([new Frame(FrameType.DataMdr, 0, payload)], []));
        }
    }

    // Caller holds _gate. DATA sequences are assigned here, in wire order, because the session drops a DATA frame
    // that repeats the previous sequence.
    private void Deliver(Response response)
    {
        if (_disposed)
        {
            return;
        }

        foreach (var frame in response.Frames)
        {
            var onWire = frame;
            if (frame.Type != FrameType.Ack)
            {
                onWire = frame with { Sequence = _nextSequence };
                _nextSequence = (byte)(1 - _nextSequence);
            }

            _inbound.Writer.TryWrite(FrameCodec.Encode(onWire));
        }

        foreach (var notification in response.Notifications)
        {
            // Not awaited: _lifetime cancels the wait on dispose, and Deliver re-checks _disposed.
            _ = NotifyLaterAsync(notification);
        }
    }

    private sealed record Response(IReadOnlyList<Frame> Frames, IReadOnlyList<byte[]> Notifications);
}
