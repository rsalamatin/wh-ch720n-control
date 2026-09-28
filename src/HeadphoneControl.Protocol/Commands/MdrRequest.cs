namespace HeadphoneControl.Protocol.Commands;

/// <summary>
/// A command payload plus how to recognise its reply. When <see cref="ResponseOpcode"/> is null the command
/// is fire-and-ACK (<c>ProtocolSession.SendAsync</c>); otherwise it is sent with <c>ProtocolSession.RequestAsync</c>.
/// </summary>
public sealed record MdrRequest(ReadOnlyMemory<byte> Payload, byte? ResponseOpcode, byte? ResponseSubtype);
