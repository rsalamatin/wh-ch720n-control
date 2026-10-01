using System.Reflection;
using HeadphoneControl.Protocol.Commands;
using HeadphoneControl.Protocol.Devices;

namespace HeadphoneControl.Protocol.Tests.Commands;

// Opcode 0x22 is BATTERY on V2 but POWER OFF on V1, so no path that starts from a V1 or unconfirmed generation
// may produce a payload beginning with 0x22. Ported from ProtocolV1Tests.cpp:30 and ProtocolSafetyTests.cpp.
public class V2CommandSetSafetyTests
{
    private const byte PowerOffOnV1 = 0x22;

    [Test]
    public async Task WhenTransportAndReplyAreV2ThenCommandSetIsReturned()
    {
        var commands = V2CommandSet.FromHandshake(ProtocolGeneration.V2, GoldenHandshake.Reply());

        await Assert.That(commands).IsNotNull();
    }

    [Test]
    public async Task WhenTransportIsV1AndReplyIsV2ThenFromHandshakeThrowsNotSupported()
    {
        await Assert.That(() => V2CommandSet.FromHandshake(ProtocolGeneration.V1, GoldenHandshake.Reply()))
            .ThrowsExactly<NotSupportedException>();
    }

    [Test]
    public async Task WhenTransportIsUnknownAndReplyIsV2ThenFromHandshakeThrowsNotSupported()
    {
        await Assert.That(() => V2CommandSet.FromHandshake(ProtocolGeneration.Unknown, GoldenHandshake.Reply()))
            .ThrowsExactly<NotSupportedException>();
    }

    [Test]
    public async Task WhenTransportIsUndefinedAndReplyIsV2ThenFromHandshakeThrowsNotSupported()
    {
        await Assert.That(() => V2CommandSet.FromHandshake((ProtocolGeneration)99, GoldenHandshake.Reply()))
            .ThrowsExactly<NotSupportedException>();
    }

    [Test]
    public async Task WhenTransportIsV2AndReplyIsV1ThenFromHandshakeThrowsNotSupported()
    {
        byte[] v1Reply = [0x01, 0x00, 0x01, 0x00, 0x00, 0x00, 0x00, 0x00];

        await Assert.That(() => V2CommandSet.FromHandshake(ProtocolGeneration.V2, v1Reply))
            .ThrowsExactly<NotSupportedException>();
    }

    [Test]
    [Arguments(new byte[] { 0x01, 0x00, 0x40, 0x10 })]
    [Arguments(new byte[] { 0x01, 0x00, 0x40, 0x00, 0x00, 0x00, 0x00, 0x00 })]
    public async Task WhenTransportIsV2AndReplyIsUnknownThenFromHandshakeThrowsNotSupported(byte[] reply)
    {
        // Length alone must not unlock V2 (ProtocolSafetyTests.cpp:91: unrecognised means not V2).
        await Assert.That(() => V2CommandSet.FromHandshake(ProtocolGeneration.V2, reply))
            .ThrowsExactly<NotSupportedException>();
    }

    [Test]
    public async Task WhenTransportIsV2AndReplyIsNotInitReplyThenFromHandshakeThrowsProtocolFormat()
    {
        byte[] notInitReply = [0x23, 0x00, 0x64, 0x00];

        await Assert.That(() => V2CommandSet.FromHandshake(ProtocolGeneration.V2, notInitReply))
            .ThrowsExactly<ProtocolFormatException>();
    }

    [Test]
    public async Task WhenHandshakeIsBuiltThenPayloadDoesNotStartWithPowerOffOpcode()
    {
        var request = ProtocolHandshake.CreateRequest();

        await Assert.That(request.Payload.Span[0]).IsNotEqualTo(PowerOffOnV1);
    }

    [Test]
    public async Task WhenV2CommandSetIsInspectedThenItHasNoPublicConstructor()
    {
        var constructors = typeof(V2CommandSet).GetConstructors(BindingFlags.Public | BindingFlags.Instance);

        await Assert.That(constructors).IsEmpty();
    }

    [Test]
    public async Task WhenV2CommandSetIsInspectedThenFromHandshakeIsTheOnlyFactory()
    {
        var factories = typeof(V2CommandSet)
            .GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Where(m => m.ReturnType == typeof(V2CommandSet))
            .Select(m => m.Name);

        await Assert.That(factories).IsEquivalentTo(new[] { nameof(V2CommandSet.FromHandshake) });
    }

    [Test]
    public async Task WhenHandshakeIsV2ThenBatteryQueryUsesOpcode0x22()
    {
        // The guard only means something because the V2 battery query really is 0x22 (ProtocolV2Tests.cpp:12).
        var commands = GoldenHandshake.CreateCommands();

        var request = commands.QueryBattery();

        await Assert.That(request.Payload.Span[0]).IsEqualTo(PowerOffOnV1);
    }
}
