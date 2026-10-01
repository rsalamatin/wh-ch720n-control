using System.Buffers.Binary;
using System.Runtime.InteropServices;

namespace HeadphoneControl.Protocol.Framing;

/// <summary>
/// Reassembles frames from an arbitrarily fragmented byte stream. One per connection, not thread-safe.
/// Malformed input is dropped, reported through <see cref="FrameRejected"/>, and decoding resumes at the next
/// start marker.
/// </summary>
public sealed class FrameDecoder
{
    // Start marker included.
    private readonly List<byte> _frameBytes = new(FrameCodec.MaxEscapedFrameSize);

    // Bounded so a noisy link cannot grow it forever.
    private readonly List<byte> _garbage = new(FrameCodec.MaxEscapedFrameSize);

    private bool _inFrame;

    public event EventHandler<FrameRejectedEventArgs>? FrameRejected;

    public IReadOnlyList<Frame> Feed(ReadOnlySpan<byte> bytes)
    {
        List<Frame>? frames = null;

        foreach (var b in bytes)
        {
            if (b == FrameCodec.StartMarker)
            {
                FlushGarbage();
                if (_inFrame)
                {
                    RejectFrame("start marker inside an unfinished frame");
                }

                _inFrame = true;
                _frameBytes.Add(b);
                continue;
            }

            if (!_inFrame)
            {
                _garbage.Add(b);
                if (_garbage.Count >= FrameCodec.MaxEscapedFrameSize)
                {
                    FlushGarbage();
                }

                continue;
            }

            _frameBytes.Add(b);

            if (b == FrameCodec.EndMarker)
            {
                var frame = TryDecodeCurrent();
                if (frame is not null)
                {
                    (frames ??= []).Add(frame);
                }

                continue;
            }

            // The end marker still has to fit, so a full buffer without it can never become a valid frame.
            if (_frameBytes.Count >= FrameCodec.MaxEscapedFrameSize)
            {
                RejectFrame($"frame exceeds {FrameCodec.MaxEscapedFrameSize} bytes without an end marker");
            }
        }

        FlushGarbage();
        return frames ?? (IReadOnlyList<Frame>)[];
    }

    /// <summary>Discards any partially received frame.</summary>
    public void Reset()
    {
        _frameBytes.Clear();
        _garbage.Clear();
        _inFrame = false;
    }

    private Frame? TryDecodeCurrent()
    {
        var escaped = CollectionsMarshal.AsSpan(_frameBytes)[1..^1];
        Span<byte> body = stackalloc byte[escaped.Length];
        var length = 0;

        for (var i = 0; i < escaped.Length; i++)
        {
            var b = escaped[i];
            if (b != FrameCodec.EscapeMarker)
            {
                body[length++] = b;
                continue;
            }

            if (i + 1 >= escaped.Length)
            {
                return RejectFrame("dangling escape marker before end marker");
            }

            var original = (byte)(escaped[++i] + FrameCodec.EscapeOffset);
            if (!FrameCodec.IsReserved(original))
            {
                return RejectFrame($"invalid escape sequence 0x3D 0x{escaped[i]:X2}");
            }

            body[length++] = original;
        }

        body = body[..length];

        if (body.Length < FrameCodec.MinBodySize)
        {
            return RejectFrame($"body of {body.Length} bytes is shorter than the {FrameCodec.MinBodySize}-byte minimum");
        }

        var declared = BinaryPrimitives.ReadUInt32BigEndian(body[2..FrameCodec.HeaderSize]);
        var actual = body.Length - FrameCodec.MinBodySize;
        if (declared != (uint)actual)
        {
            return RejectFrame($"declared payload length {declared} does not match actual {actual}");
        }

        var expected = body[^1];
        var computed = FrameCodec.Checksum(body[..^1]);
        if (expected != computed)
        {
            return RejectFrame($"checksum mismatch (expected 0x{computed:X2}, got 0x{expected:X2})");
        }

        var frame = new Frame((FrameType)body[0], body[1], body[FrameCodec.HeaderSize..^1].ToArray());
        _frameBytes.Clear();
        _inFrame = false;
        return frame;
    }

    private Frame? RejectFrame(string reason)
    {
        var raw = _frameBytes.ToArray();
        _frameBytes.Clear();
        _inFrame = false;
        FrameRejected?.Invoke(this, new FrameRejectedEventArgs(reason, raw));
        return null;
    }

    private void FlushGarbage()
    {
        if (_garbage.Count == 0)
        {
            return;
        }

        var raw = _garbage.ToArray();
        _garbage.Clear();
        FrameRejected?.Invoke(this, new FrameRejectedEventArgs("bytes outside a frame", raw));
    }
}
