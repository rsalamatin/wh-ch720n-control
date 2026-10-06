namespace HeadphoneControl.FirmwareUpdates.Tests;

public class FirmwareReleaseTests
{
    [Test]
    [Arguments("1.1.5", "1.1.4")]
    [Arguments("1.1.10", "1.1.4")]
    [Arguments("1.2", "1.1.4")]
    [Arguments("2", "1.9.9")]
    [Arguments("1.1.4.1", "1.1.4")]
    public async Task WhenLatestIsNewerThanInstalledThenAnUpdateIsAvailable(string latest, string installed)
    {
        var release = new FirmwareRelease(latest, InfoUrl: null);

        var status = release.StatusFor(installed);

        await Assert.That(status).IsEqualTo(FirmwareStatus.UpdateAvailable);
    }

    [Test]
    [Arguments("1.1.4", "1.1.4")]
    [Arguments("1.1", "1.1.0")]
    [Arguments("1.1.4", " 1.1.4 ")]
    [Arguments("1.1.4", "1.1.5")]
    [Arguments("1.1.4", "1.1.10")]
    [Arguments("1.1.4", "2.0")]
    public async Task WhenLatestIsNotNewerThanInstalledThenFirmwareIsUpToDate(string latest, string installed)
    {
        var release = new FirmwareRelease(latest, InfoUrl: null);

        var status = release.StatusFor(installed);

        await Assert.That(status).IsEqualTo(FirmwareStatus.UpToDate);
    }

    [Test]
    [Arguments("1.1.5", "")]
    [Arguments("1.1.5", "unknown")]
    [Arguments("1.1.5", "1.1.4b")]
    [Arguments("1.1.5", "1..4")]
    [Arguments("1.1.5", "-1.1.4")]
    [Arguments("latest", "1.1.4")]
    [Arguments("1.2.3.4.5.6.7.8.9", "1.1.4")]
    public async Task WhenAVersionIsNotADottedNumberThenStatusIsUnknown(string latest, string installed)
    {
        var release = new FirmwareRelease(latest, InfoUrl: null);

        var status = release.StatusFor(installed);

        await Assert.That(status).IsEqualTo(FirmwareStatus.Unknown);
    }
}
