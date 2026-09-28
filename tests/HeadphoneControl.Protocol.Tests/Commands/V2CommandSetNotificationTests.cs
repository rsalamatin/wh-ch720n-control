using HeadphoneControl.Protocol.Commands;
using HeadphoneControl.Protocol.Devices;
using TUnit.Assertions.Enums;

namespace HeadphoneControl.Protocol.Tests.Commands;

public class V2CommandSetNotificationTests
{
    private readonly V2CommandSet _commands = GoldenHandshake.CreateCommands();

    private readonly DeviceState _connected =
        DeviceState.Disconnected with { Connection = ConnectionStatus.Connected, Generation = ProtocolGeneration.V2 };

    [Test]
    [Arguments(new byte[] { 0x25, 0x00, 0x32, 0x01 })]
    [Arguments(new byte[] { 0x23, 0x00, 0x32, 0x01 })]
    public async Task WhenBatteryNotificationArrivesThenBatteryIsUpdated(byte[] payload)
    {
        _commands.TryApplyNotification(_connected, payload, out var updated);

        await Assert.That(updated.Battery).IsEqualTo(new BatteryState(50, true));
    }

    public static IEnumerable<Func<(byte[] Payload, Func<DeviceState, DeviceState> ClearTouchedField)>> FamilyNotifications()
    {
        yield return () => ([0x25, 0x00, 0x32, 0x01], s => s with { Battery = null });
        yield return () => ([0x69, 0x17, 0x01, 0x01, 0x01, 0x00, 0x08], s => s with { NoiseControl = null });
        yield return () => ([0x59, 0x00, 0x16, 0x06, 10, 10, 10, 10, 10, 10], s => s with { Equalizer = null });
        yield return () => ([0xE9, 0x01, 0x01], s => s with { DseeEnabled = null });
    }

    [Test]
    [MethodDataSource(nameof(FamilyNotifications))]
    public async Task WhenNotificationArrivesThenOtherFieldsArePreserved(
        byte[] payload, Func<DeviceState, DeviceState> clearTouchedField)
    {
        _commands.TryApplyNotification(_connected, payload, out var updated);

        await Assert.That(clearTouchedField(updated)).IsEqualTo(_connected);
    }

    [Test]
    [Arguments(new byte[] { 0x25, 0x09, 0x32, 0x00, 0x30, 0x00 })]
    [Arguments(new byte[] { 0x25, 0x0A, 0x32, 0x00 })]
    public async Task WhenBatteryNotificationIsNotSingleBatteryThenItIsNotApplied(byte[] payload)
    {
        var applied = _commands.TryApplyNotification(_connected, payload, out _);

        await Assert.That(applied).IsFalse();
    }

    [Test]
    [Arguments(new byte[] { 0x69, 0x17, 0x01, 0x01, 0x01, 0x00, 0x08 })]
    [Arguments(new byte[] { 0x67, 0x17, 0x01, 0x01, 0x01, 0x00, 0x08 })]
    public async Task WhenNoiseControlNotificationArrivesThenNoiseControlIsUpdated(byte[] payload)
    {
        _commands.TryApplyNotification(_connected, payload, out var updated);

        await Assert.That(updated.NoiseControl).IsEqualTo(new NoiseControlState(NoiseControlMode.Ambient, false, 8));
    }

    [Test]
    public async Task WhenFullEqualizerNotificationArrivesThenBandsAreUpdated()
    {
        byte[] payload = [0x59, 0x00, 0xA0, 0x06, 10, 5, 10, 15, 20, 0];

        _commands.TryApplyNotification(_connected, payload, out var updated);

        await Assert.That(updated.Equalizer!.Bands).IsEquivalentTo(new[] { -5, 0, 5, 10, -10 }, CollectionOrdering.Matching);
    }

    [Test]
    public async Task WhenPresetOnlyEqualizerNotificationArrivesThenPresetIsUpdated()
    {
        var known = _connected with { Equalizer = new EqualizerState(EqualizerPreset.Off, 2, [1, 2, 3, 4, 5]) };
        byte[] payload = [0x59, 0x00, 0x16, 0x00];

        _commands.TryApplyNotification(known, payload, out var updated);

        await Assert.That(updated.Equalizer!.Preset).IsEqualTo(EqualizerPreset.BassBoost);
    }

    [Test]
    public async Task WhenPresetOnlyEqualizerNotificationArrivesThenPreviousBandsAreKept()
    {
        var known = _connected with { Equalizer = new EqualizerState(EqualizerPreset.Off, 2, [1, 2, 3, 4, 5]) };
        byte[] payload = [0x59, 0x00, 0x16, 0x00];

        _commands.TryApplyNotification(known, payload, out var updated);

        await Assert.That(updated.Equalizer!.Bands).IsEquivalentTo(new[] { 1, 2, 3, 4, 5 }, CollectionOrdering.Matching);
    }

    [Test]
    public async Task WhenPresetOnlyEqualizerNotificationArrivesWithoutKnownBandsThenItIsNotApplied()
    {
        byte[] payload = [0x59, 0x00, 0x16, 0x00];

        var applied = _commands.TryApplyNotification(_connected, payload, out _);

        await Assert.That(applied).IsFalse();
    }

    [Test]
    [Arguments(new byte[] { 0xE9, 0x01, 0x01 })]
    [Arguments(new byte[] { 0xE7, 0x01, 0x01 })]
    public async Task WhenDseeNotificationArrivesThenDseeIsUpdated(byte[] payload)
    {
        _commands.TryApplyNotification(_connected, payload, out var updated);

        await Assert.That(updated.DseeEnabled).IsTrue();
    }

    [Test]
    [Arguments(new byte[] { 0x25, 0x00, 0x32, 0x01 })]
    [Arguments(new byte[] { 0x69, 0x17, 0x01, 0x00, 0x00, 0x00, 0x01 })]
    [Arguments(new byte[] { 0x59, 0x00, 0x00, 0x06, 10, 10, 10, 10, 10, 10 })]
    [Arguments(new byte[] { 0xE9, 0x01, 0x00 })]
    public async Task WhenRecognisedNotificationArrivesThenItIsApplied(byte[] payload)
    {
        var applied = _commands.TryApplyNotification(_connected, payload, out _);

        await Assert.That(applied).IsTrue();
    }

    [Test]
    [Arguments(new byte[] { 0x69, 0x15, 0x01, 0x01, 0x01, 0x00, 0x08 })]
    [Arguments(new byte[] { 0x69, 0x17, 0x02, 0x01, 0x01, 0x00, 0x08 })]
    [Arguments(new byte[] { 0xE9, 0x02, 0x01 })]
    [Arguments(new byte[] { 0x59, 0x01, 0x16, 0x06, 10, 10, 10, 10, 10, 10 })]
    [Arguments(new byte[] { 0x25, 0x07, 0x32, 0x00 })]
    public async Task WhenKnownOpcodeHasUnknownSubtypeThenItIsNotApplied(byte[] payload)
    {
        var applied = _commands.TryApplyNotification(_connected, payload, out _);

        await Assert.That(applied).IsFalse();
    }

    [Test]
    [Arguments(new byte[0])]
    [Arguments(new byte[] { 0x0D, 0x00 })]
    [Arguments(new byte[] { 0x13, 0x02, 0x01 })]
    [Arguments(new byte[] { 0x29, 0x05, 0x10, 0x00 })]
    public async Task WhenUnrecognisedPayloadArrivesThenItIsNotApplied(byte[] payload)
    {
        var applied = _commands.TryApplyNotification(_connected, payload, out _);

        await Assert.That(applied).IsFalse();
    }

    [Test]
    public async Task WhenUnrecognisedPayloadArrivesThenStateIsUnchanged()
    {
        byte[] payload = [0x0D, 0x00];

        _commands.TryApplyNotification(_connected, payload, out var updated);

        await Assert.That(updated).IsSameReferenceAs(_connected);
    }

    [Test]
    [Arguments(new byte[] { 0x25, 0x00, 0x64 })]
    [Arguments(new byte[] { 0x69, 0x17, 0x01, 0x01 })]
    [Arguments(new byte[] { 0x59, 0x00 })]
    [Arguments(new byte[] { 0x59, 0x00, 0x00, 0x06, 30, 10, 10, 10, 10, 10 })]
    [Arguments(new byte[] { 0x59, 0x00, 0x16, 0x06, 10, 10 })]
    [Arguments(new byte[] { 0x69, 0x17, 0x01, 0x01, 0x01, 0x00, 0x15 })]
    [Arguments(new byte[] { 0xE9, 0x01 })]
    [Arguments(new byte[] { 0x25 })]
    public async Task WhenRecognisedNotificationIsMalformedThenApplyThrows(byte[] payload)
    {
        await Assert.That(() => _commands.TryApplyNotification(_connected, payload, out _)).ThrowsExactly<ProtocolFormatException>();
    }

    [Test]
    public async Task WhenCurrentStateIsNullThenApplyThrows()
    {
        byte[] payload = [0x25, 0x00, 0x32, 0x01];

        await Assert.That(() => _commands.TryApplyNotification(null!, payload, out _)).ThrowsExactly<ArgumentNullException>();
    }
}
