namespace HeadphoneControl.Protocol.Framing;

/// <summary>
/// One unescaped Sony frame; it owns its payload. Record equality compares <see cref="Payload"/> by reference.
/// </summary>
public sealed record Frame(FrameType Type, byte Sequence, ReadOnlyMemory<byte> Payload);
