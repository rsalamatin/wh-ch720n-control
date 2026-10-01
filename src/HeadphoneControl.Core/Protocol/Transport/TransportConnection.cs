using HeadphoneControl.Protocol.Devices;

namespace HeadphoneControl.Protocol.Transport;

/// <summary>
/// A connected transport plus the protocol generation implied by the Sony service it connected to. The generation
/// is one of the two independent V2 signals required before any V2 command may be sent.
/// </summary>
public sealed record TransportConnection(ITransport Transport, ProtocolGeneration ServiceGeneration);
