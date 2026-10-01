using HeadphoneControl.Protocol.Devices;

namespace HeadphoneControl.Protocol.Tests.Devices;

public class EqualizerStateTests
{
    [Test]
    public async Task WhenBandsHaveTheSameLevelsInDifferentListsThenStatesAreEqual()
    {
        var first = new EqualizerState(EqualizerPreset.Manual, 2, [1, 2, 3, 4, 5]);
        var second = new EqualizerState(EqualizerPreset.Manual, 2, new List<int> { 1, 2, 3, 4, 5 });

        await Assert.That(first).IsEqualTo(second);
    }

    [Test]
    public async Task WhenBandsHaveTheSameLevelsThenHashCodesAreEqual()
    {
        var first = new EqualizerState(EqualizerPreset.Manual, 2, [1, 2, 3, 4, 5]);
        var second = new EqualizerState(EqualizerPreset.Manual, 2, new List<int> { 1, 2, 3, 4, 5 });

        await Assert.That(first.GetHashCode()).IsEqualTo(second.GetHashCode());
    }

    [Test]
    public async Task WhenOneBandDiffersThenStatesAreNotEqual()
    {
        var first = new EqualizerState(EqualizerPreset.Manual, 2, [1, 2, 3, 4, 5]);
        var second = new EqualizerState(EqualizerPreset.Manual, 2, [1, 2, 3, 4, 6]);

        await Assert.That(first).IsNotEqualTo(second);
    }
}
