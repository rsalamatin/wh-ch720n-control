using HeadphoneControl.Protocol.Devices;
using HeadphoneControl.Protocol.Transport;
using NSubstitute;

namespace HeadphoneControl.Core.Tests;

public class ConnectPreferredTests
{
    [Test]
    public async Task WhenNoHeadsetIsPairedThenTransportExceptionIsThrown()
    {
        var connector = Substitute.For<IHeadsetConnector>();
        connector.FindPairedAsync(Arg.Any<CancellationToken>()).Returns([]);

        Func<Task> act = () => connector.ConnectPreferredAsync(CancellationToken.None);

        await Assert.That(act).Throws<TransportException>();
    }

    [Test]
    public async Task WhenSeveralHeadsetsArePairedThenTheFirstListedIsConnected()
    {
        var first = new DiscoveredHeadset("WH-CH720N", "id-a", ProtocolGeneration.V2);
        var second = new DiscoveredHeadset("WH-1000XM4", "id-b", ProtocolGeneration.V2);
        var connection = new TransportConnection(Substitute.For<ITransport>(), ProtocolGeneration.V2);
        var connector = Substitute.For<IHeadsetConnector>();
        connector.FindPairedAsync(Arg.Any<CancellationToken>()).Returns([first, second]);
        connector.ConnectAsync(first, Arg.Any<CancellationToken>()).Returns(connection);

        var result = await connector.ConnectPreferredAsync(CancellationToken.None);

        await Assert.That(result).IsSameReferenceAs(connection);
    }
}
