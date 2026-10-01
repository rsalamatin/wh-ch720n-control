namespace HeadphoneControl.Protocol.Commands;

/// <summary>A command payload; a null <see cref="ResponseOpcode"/> means it is ACK-only and has no reply.</summary>
public sealed record MdrRequest(ReadOnlyMemory<byte> Payload, byte? ResponseOpcode, byte? ResponseSubtype);
