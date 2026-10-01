using System.Buffers.Binary;

namespace HeadphoneControl.Protocol.Framing;

/// <summary>Encodes frames to wire bytes (spec section 4).</summary>
public static class FrameCodec
{
    /// <summary>Largest escaped frame, including start and end markers, that may be sent or accepted.</summary>
    public const int MaxEscapedFrameSize = 2048;

    public const byte StartMarker = 0x3E;

    public const byte EndMarker = 0x3C;

    /// <summary>A reserved body byte is sent as this marker followed by the byte minus 0x10.</summary>
    public const byte EscapeMarker = 0x3D;

    // Unescaped body: type, sequence, 4-byte length, payload, then one checksum byte.
    internal const int HeaderSize = 6;
    internal const int MinBodySize = HeaderSize + 1;

    internal const byte EscapeOffset = 0x10;

    /// <summary>
    /// Returns <c>0x3E + escaped(type, seq, u32 BE length, payload, checksum) + 0x3C</c>.
    /// Throws <see cref="ArgumentException"/> when the result would exceed <see cref="MaxEscapedFrameSize"/>.
    /// </summary>
    public static byte[] Encode(Frame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);

        var payload = frame.Payload.Span;

        // Even unescaped the frame cannot fit; reject before allocating a body for a huge payload.
        if (payload.Length > MaxEscapedFrameSize - MinBodySize - 2)
        {
            throw TooLarge(payload.Length);
        }

        Span<byte> body = stackalloc byte[MinBodySize + payload.Length];
        body[0] = (byte)frame.Type;
        body[1] = frame.Sequence;
        BinaryPrimitives.WriteUInt32BigEndian(body[2..HeaderSize], (uint)payload.Length);
        payload.CopyTo(body[HeaderSize..]);
        body[^1] = Checksum(body[..^1]);

        var escapedLength = 2;
        foreach (var b in body)
        {
            escapedLength += IsReserved(b) ? 2 : 1;
        }

        if (escapedLength > MaxEscapedFrameSize)
        {
            throw TooLarge(payload.Length);
        }

        var result = new byte[escapedLength];
        var index = 0;
        result[index++] = StartMarker;
        foreach (var b in body)
        {
            if (IsReserved(b))
            {
                result[index++] = EscapeMarker;
                result[index++] = (byte)(b - EscapeOffset);
            }
            else
            {
                result[index++] = b;
            }
        }

        result[index] = EndMarker;
        return result;
    }

    internal static byte Checksum(ReadOnlySpan<byte> unescaped)
    {
        byte sum = 0;
        foreach (var b in unescaped)
        {
            sum += b;
        }

        return sum;
    }

    internal static bool IsReserved(byte b) => b is StartMarker or EndMarker or EscapeMarker;

    private static ArgumentException TooLarge(int payloadLength) =>
        new($"A payload of {payloadLength} bytes exceeds the {MaxEscapedFrameSize}-byte escaped frame limit.", "frame");
}
