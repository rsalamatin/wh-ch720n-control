using HeadphoneControl.Protocol.Devices;
using HeadphoneControl.Protocol.Framing;
using HeadphoneControl.Protocol.Transport;

namespace HeadphoneControl.Testing;

// Scripted WH-CH720N over FakeTransport: ACKs every frame and answers queries from a reply table.
public sealed class FakeHeadset
{
    // Real WH-CH720N reply to the init handshake.
    public static readonly byte[] V2InitReply = [0x01, 0x00, 0x03, 0x00, 0x10, 0x02, 0x00, 0x00];

    private readonly Lock _sequenceLock = new();
    private byte _nextSequence;

    public FakeHeadset()
    {
        Transport = new FakeTransport { AutoAck = true, Responder = Respond };
    }

    public FakeTransport Transport { get; }

    public ProtocolGeneration ServiceGeneration { get; set; } = ProtocolGeneration.V2;

    // Null means the headset never answers the handshake.
    public byte[]? InitReply { get; set; } = V2InitReply;

    // The link drops right after the headset answers this request opcode.
    public byte? DropLinkAfter { get; set; }

    // Request opcode -> reply payload. A missing entry means the headset never answers that query.
    public Dictionary<byte, byte[]> Replies { get; } = new()
    {
        [0x22] = [0x23, 0x00, 80, 1],
        [0x66] = [0x67, 0x17, 0x01, 0x01, 0x01, 0x00, 8],
        [0x56] = [0x57, 0x00, 0x10, 0x06, 10, 11, 12, 13, 14, 15],
        [0xE6] = [0xE7, 0x01, 0x01],
        [0x04] = [0x05, 0x02, 0x05, (byte)'1', (byte)'.', (byte)'0', (byte)'.', (byte)'2'],
        [0x12] = [0x13, 0x02, 0x02],
    };

    public Task<TransportConnection> ConnectAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(new TransportConnection(Transport, ServiceGeneration));
    }

    public IReadOnlyList<byte[]> SentPayloads() =>
        Transport.WrittenFrames
            .Where(frame => frame.Type == FrameType.DataMdr)
            .Select(frame => frame.Payload.ToArray())
            .ToList();

    public void Notify(params byte[] payload) =>
        Transport.QueueIncoming(new Frame(FrameType.DataMdr, NextSequence(), payload));

    private IEnumerable<Frame> Respond(Frame frame)
    {
        if (frame.Type != FrameType.DataMdr || frame.Payload.IsEmpty)
        {
            yield break;
        }

        var opcode = frame.Payload.Span[0];
        var reply = opcode == 0x00 ? InitReply : Replies.GetValueOrDefault(opcode);
        if (reply is not null)
        {
            yield return new Frame(FrameType.DataMdr, NextSequence(), reply);
        }

        if (opcode == DropLinkAfter)
        {
            Transport.SimulateEof();
        }
    }

    public async Task WaitUntilSentAsync(byte opcode, TimeSpan patience, int times = 1)
    {
        var deadline = DateTime.UtcNow + patience;
        while (SentPayloads().Count(payload => payload.Length > 0 && payload[0] == opcode) < times)
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException($"Opcode 0x{opcode:X2} was not sent {times} time(s).");
            }

            await Task.Delay(5);
        }
    }

    // The session drops a DATA_MDR frame that repeats the previous sequence number, so replies alternate like
    // the real device's.
    private byte NextSequence()
    {
        lock (_sequenceLock)
        {
            var sequence = _nextSequence;
            _nextSequence = (byte)(1 - _nextSequence);
            return sequence;
        }
    }
}
