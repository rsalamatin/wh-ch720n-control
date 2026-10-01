using HeadphoneControl.Protocol.Commands;
using HeadphoneControl.Protocol.Devices;
using TUnit.Assertions.Enums;

namespace HeadphoneControl.Protocol.Tests.Commands;

public class ProtocolHandshakeTests
{
    [Test]
    public async Task WhenInitRequestIsCreatedThenPayloadIsSpecExampleA()
    {
        var request = ProtocolHandshake.CreateRequest();

        await Assert.That(request.Payload.ToArray()).IsEquivalentTo(new byte[] { 0x00, 0x00 }, CollectionOrdering.Matching);
    }

    [Test]
    public async Task WhenInitRequestIsCreatedThenItAwaitsOpcode0x01()
    {
        var request = ProtocolHandshake.CreateRequest();

        await Assert.That(request.ResponseOpcode).IsEqualTo((byte)0x01);
    }

    [Test]
    public async Task WhenInitRequestIsCreatedThenItAcceptsAnyResponseSubtype()
    {
        var request = ProtocolHandshake.CreateRequest();

        await Assert.That(request.ResponseSubtype).IsNull();
    }

    [Test]
    public async Task WhenInitReplyIsRealWhCh720nCaptureThenGenerationIsV2()
    {
        var generation = ProtocolHandshake.ParseGeneration(GoldenHandshake.Reply());

        await Assert.That(generation).IsEqualTo(ProtocolGeneration.V2);
    }

    [Test]
    public async Task WhenInitReplyProtocolInfoIs0x01ThenGenerationIsV1()
    {
        byte[] reply = [0x01, 0x00, 0x01, 0x00, 0x00, 0x00, 0x00, 0x00];

        var generation = ProtocolHandshake.ParseGeneration(reply);

        await Assert.That(generation).IsEqualTo(ProtocolGeneration.V1);
    }

    [Test]
    [Arguments(new byte[] { 0x01, 0x00, 0x40, 0x00, 0x00, 0x00, 0x00, 0x00 })]
    [Arguments(new byte[] { 0x01, 0x00, 0x00, 0x00, 0x10, 0x02, 0x00, 0x00 })]
    [Arguments(new byte[] { 0x01, 0x00, 0x02, 0x00, 0x10, 0x02, 0x00, 0x00 })]
    public async Task WhenInitReplyProtocolInfoIsUnknownThenGenerationIsUnknown(byte[] reply)
    {
        var generation = ProtocolHandshake.ParseGeneration(reply);

        await Assert.That(generation).IsEqualTo(ProtocolGeneration.Unknown);
    }

    [Test]
    [Arguments(new byte[] { 0x01 })]
    [Arguments(new byte[] { 0x01, 0x00, 0x03, 0x10 })]
    [Arguments(new byte[] { 0x01, 0x00, 0x03, 0x00, 0x10, 0x02, 0x00, 0x00, 0x00 })]
    public async Task WhenInitReplyIsNotEightBytesThenGenerationIsUnknown(byte[] reply)
    {
        var generation = ProtocolHandshake.ParseGeneration(reply);

        await Assert.That(generation).IsEqualTo(ProtocolGeneration.Unknown);
    }

    [Test]
    [Arguments(new byte[0])]
    [Arguments(new byte[] { 0x23, 0x00, 0x64, 0x00, 0x00, 0x00, 0x00, 0x00 })]
    public async Task WhenInitReplyIsNotOpcode0x01ThenParseThrowsProtocolFormatException(byte[] reply)
    {
        await Assert.That(() => ProtocolHandshake.ParseGeneration(reply)).ThrowsExactly<ProtocolFormatException>();
    }
}
