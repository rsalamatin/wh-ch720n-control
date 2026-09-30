using HeadphoneControl.Core;
using HeadphoneControl.Protocol.Devices;

namespace HeadphoneControl.Core.Tests;

public class HeadsetSelectionTests
{
    [Test]
    public async Task WhenWhCh720nIsListedLastThenItComesFirst()
    {
        var headsets = new[]
        {
            new DiscoveredHeadset("WH-1000XM4", "id-a", ProtocolGeneration.V2),
            new DiscoveredHeadset("LE_WH-CH720N", "id-b", ProtocolGeneration.V2),
        };

        var ordered = HeadsetSelection.OrderForDisplay(headsets);

        await Assert.That(ordered[0].DeviceId).IsEqualTo("id-b");
    }

    [Test]
    public async Task WhenGenerationIsUnknownThenDeviceIsExcluded()
    {
        var headsets = new[]
        {
            new DiscoveredHeadset("Keyboard", "id-a", ProtocolGeneration.Unknown),
            new DiscoveredHeadset("WH-CH720N", "id-b", ProtocolGeneration.V2),
        };

        var ordered = HeadsetSelection.OrderForDisplay(headsets);

        await Assert.That(ordered.Select(h => h.DeviceId)).IsEquivalentTo(["id-b"]);
    }

    [Test]
    public async Task WhenOtherSonyModelsArePresentThenTheyAreKept()
    {
        var headsets = new[]
        {
            new DiscoveredHeadset("WF-1000XM3", "id-a", ProtocolGeneration.V1),
            new DiscoveredHeadset("WH-CH720N", "id-b", ProtocolGeneration.V2),
        };

        var ordered = HeadsetSelection.OrderForDisplay(headsets);

        await Assert.That(ordered.Count).IsEqualTo(2);
    }

    [Test]
    public async Task WhenNoPreferredModelIsPresentThenV2ComesBeforeV1()
    {
        var headsets = new[]
        {
            new DiscoveredHeadset("A-old", "id-a", ProtocolGeneration.V1),
            new DiscoveredHeadset("Z-new", "id-b", ProtocolGeneration.V2),
        };

        var ordered = HeadsetSelection.OrderForDisplay(headsets);

        await Assert.That(ordered[0].DeviceId).IsEqualTo("id-b");
    }

    [Test]
    public async Task WhenRankIsEqualThenHeadsetsAreOrderedByNameIgnoringCase()
    {
        var headsets = new[]
        {
            new DiscoveredHeadset("wh-b", "id-b", ProtocolGeneration.V2),
            new DiscoveredHeadset("WH-C", "id-c", ProtocolGeneration.V2),
            new DiscoveredHeadset("WH-A", "id-a", ProtocolGeneration.V2),
        };

        var ordered = HeadsetSelection.OrderForDisplay(headsets);

        await Assert.That(ordered.Select(h => h.DeviceId)).IsEquivalentTo(["id-a", "id-b", "id-c"], TUnit.Assertions.Enums.CollectionOrdering.Matching);
    }
}
