using HeadphoneControl.Bluetooth;
using HeadphoneControl.Protocol.Devices;

namespace HeadphoneControl.Tests.Bluetooth;

public class SonyServiceIdsTests
{
    [Test]
    [Arguments("956C7B26-D49A-4BA8-B03F-B17D393CB6E2", ProtocolGeneration.V2)]
    [Arguments("96cc203e-5068-46ad-b32d-e316f5e069ba", ProtocolGeneration.V1)]
    [Arguments("00001101-0000-1000-8000-00805F9B34FB", ProtocolGeneration.Unknown)]
    public async Task WhenServiceUuidIsGivenThenMatchingGenerationIsReturned(string uuid, ProtocolGeneration expected)
    {
        var serviceUuid = Guid.Parse(uuid);

        var generation = SonyServiceIds.GetGeneration(serviceUuid);

        await Assert.That(generation).IsEqualTo(expected);
    }

    [Test]
    public async Task WhenComparingServiceIdsThenV1AndV2Differ()
    {
        var same = SonyServiceIds.V1 == SonyServiceIds.V2;

        await Assert.That(same).IsFalse();
    }
}
