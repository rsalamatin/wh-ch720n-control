using HeadphoneControl.Protocol.Devices;

namespace HeadphoneControl.Protocol.Tests.Devices;

public class EqualizerStateTests
{
    [Test]
    public async Task WhenBandsHaveTheSameLevelsInDifferentListsThenStatesAreEqual()
    {
        var first = new EqualizerState(EqualizerPreset.Manual, 2, [1, 2, 3, 4, 5]);
        var second = new EqualizerState(EqualizerPreset.Manual, 2, new List<int> { 1, 2, 3, 4, 5 });

        var equal = first.Equals(second);

        await Assert.That(equal).IsTrue();
    }

    [Test]
    public async Task WhenBandsHaveTheSameLevelsThenHashCodesAreEqual()
    {
        var first = new EqualizerState(EqualizerPreset.Manual, 2, [1, 2, 3, 4, 5]);
        var second = new EqualizerState(EqualizerPreset.Manual, 2, new List<int> { 1, 2, 3, 4, 5 });

        var sameHash = first.GetHashCode() == second.GetHashCode();

        await Assert.That(sameHash).IsTrue();
    }

    [Test]
    public async Task WhenOneBandDiffersThenStatesAreNotEqual()
    {
        var first = new EqualizerState(EqualizerPreset.Manual, 2, [1, 2, 3, 4, 5]);
        var second = new EqualizerState(EqualizerPreset.Manual, 2, [1, 2, 3, 4, 6]);

        var equal = first.Equals(second);

        await Assert.That(equal).IsFalse();
    }
}
