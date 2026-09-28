namespace HeadphoneControl.Protocol.Framing;

/// <summary>
/// One unescaped Sony frame. The payload is owned by the frame (never a view into a reusable buffer).
/// Note: record equality compares <see cref="Payload"/> by reference; compare <c>Payload.ToArray()</c> in tests.
/// </summary>
public sealed record Frame(FrameType Type, byte Sequence, ReadOnlyMemory<byte> Payload);
