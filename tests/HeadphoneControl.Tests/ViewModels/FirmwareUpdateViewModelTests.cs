using HeadphoneControl.FirmwareUpdates;
using HeadphoneControl.Protocol.Devices;
using HeadphoneControl.Resources;
using HeadphoneControl.Settings;
using HeadphoneControl.ViewModels;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;

namespace HeadphoneControl.Tests.ViewModels;

public class FirmwareUpdateViewModelTests
{
    private static readonly Uri InfoUrl = new("https://example.com/wh-ch720n");

    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 6, 12, 0, 0, TimeSpan.Zero));
    private readonly IHeadphoneDevice _device = Substitute.For<IHeadphoneDevice>();
    private readonly List<FirmwareCheckSettings> _saved = [];
    private int _checks;

    public FirmwareUpdateViewModelTests()
    {
        _device.Name.Returns("WH-CH720N");
        _device.State.Returns(DeviceState.Disconnected);
    }

    private FirmwareUpdateViewModel CreateViewModel(
        FirmwareCheckSettings? settings = null, Func<FirmwareRelease?>? latest = null) => new(
        _device,
        _ =>
        {
            _checks++;
            return Task.FromResult(latest is null ? new FirmwareRelease("1.1.4", InfoUrl) : latest());
        },
        settings ?? new FirmwareCheckSettings(),
        _saved.Add,
        NullLogger<FirmwareUpdateViewModel>.Instance,
        action => action(),
        _time);

    private void Connect(string firmware = "1.1.4") => Report(DeviceState.Disconnected with
    {
        Connection = ConnectionStatus.Connected,
        Generation = ProtocolGeneration.V2,
        FirmwareVersion = firmware,
    });

    private void Report(DeviceState state)
    {
        _device.State.Returns(state);
        _device.StateChanged += Raise.Event<EventHandler<DeviceState>>(_device, state);
    }

    [Test]
    public async Task WhenHeadsetConnectsThenTheLatestFirmwareIsLookedUp()
    {
        using var viewModel = CreateViewModel();

        Connect();

        await Assert.That(_checks).IsEqualTo(1);
    }

    [Test]
    public async Task WhenInstalledFirmwareIsTheLatestThenStatusSaysUpToDate()
    {
        using var viewModel = CreateViewModel();

        Connect("1.1.4");

        await Assert.That(viewModel.StatusText).IsEqualTo(Strings.Get("Firmware_UpToDate"));
    }

    [Test]
    public async Task WhenANewerFirmwareExistsThenStatusNamesIt()
    {
        using var viewModel = CreateViewModel(latest: () => new FirmwareRelease("1.2.0", InfoUrl));

        Connect("1.1.4");

        await Assert.That(viewModel.StatusText).IsEqualTo(Strings.Format("Firmware_UpdateAvailableFormat", "1.2.0"));
    }

    [Test]
    public async Task WhenANewerFirmwareExistsThenItsInfoPageIsOffered()
    {
        using var viewModel = CreateViewModel(latest: () => new FirmwareRelease("1.2.0", InfoUrl));

        Connect("1.1.4");

        await Assert.That(viewModel.InfoUrl).IsEqualTo(InfoUrl);
    }

    [Test]
    public async Task WhenFirmwareIsUpToDateThenNoInfoPageIsOffered()
    {
        using var viewModel = CreateViewModel();

        Connect("1.1.4");

        await Assert.That(viewModel.HasInfoPage).IsFalse();
    }

    [Test]
    public async Task WhenANewerFirmwareExistsThenUpdateFoundReportsBothVersions()
    {
        using var viewModel = CreateViewModel(latest: () => new FirmwareRelease("1.2.0", InfoUrl));
        var found = new List<FirmwareUpdateNotice>();
        viewModel.UpdateFound += (_, notice) => found.Add(notice);

        Connect("1.1.4");

        await Assert.That(found).IsEquivalentTo([new FirmwareUpdateNotice("1.1.4", "1.2.0")]);
    }

    [Test]
    public async Task WhenHeadsetReconnectsThenTheSameUpdateIsNotAnnouncedAgain()
    {
        using var viewModel = CreateViewModel(latest: () => new FirmwareRelease("1.2.0", InfoUrl));
        var found = new List<FirmwareUpdateNotice>();
        viewModel.UpdateFound += (_, notice) => found.Add(notice);
        Connect("1.1.4");
        Report(DeviceState.Disconnected);

        Connect("1.1.4");

        await Assert.That(found.Count).IsEqualTo(1);
    }

    [Test]
    public async Task WhenTheUpdateWasAnnouncedInAnEarlierRunThenItIsNotAnnouncedAgain()
    {
        var settings = new FirmwareCheckSettings(LastChecked: _time.GetUtcNow(), LatestVersion: "1.2.0", NotifiedVersion: "1.2.0");
        using var viewModel = CreateViewModel(settings);
        var found = new List<FirmwareUpdateNotice>();
        viewModel.UpdateFound += (_, notice) => found.Add(notice);

        Connect("1.1.4");

        await Assert.That(found).IsEmpty();
    }

    [Test]
    public async Task WhenCheckedLessThanADayAgoThenConnectingDoesNotCheckAgain()
    {
        using var viewModel = CreateViewModel(new FirmwareCheckSettings(LastChecked: _time.GetUtcNow().AddHours(-23)));

        Connect();

        await Assert.That(_checks).IsEqualTo(0);
    }

    [Test]
    public async Task WhenTheLastCheckIsDatedInTheFutureThenConnectingChecks()
    {
        using var viewModel = CreateViewModel(new FirmwareCheckSettings(LastChecked: DateTimeOffset.MaxValue));

        Connect();

        await Assert.That(_checks).IsEqualTo(1);
    }

    [Test]
    public async Task WhenCheckedLessThanADayAgoThenTheRememberedReleaseIsShown()
    {
        var settings = new FirmwareCheckSettings(LastChecked: _time.GetUtcNow().AddHours(-1), LatestVersion: "1.2.0");
        using var viewModel = CreateViewModel(settings);

        Connect("1.1.4");

        await Assert.That(viewModel.IsUpdateAvailable).IsTrue();
    }

    [Test]
    public async Task WhenADayHasPassedSinceTheLastCheckThenTheNextStateChangeChecksAgain()
    {
        using var viewModel = CreateViewModel();
        Connect();
        _time.Advance(TimeSpan.FromDays(1));

        Connect();

        await Assert.That(_checks).IsEqualTo(2);
    }

    [Test]
    public async Task WhenAutomaticCheckIsOffThenConnectingDoesNotCheck()
    {
        using var viewModel = CreateViewModel(new FirmwareCheckSettings(Automatic: false));

        Connect();

        await Assert.That(_checks).IsEqualTo(0);
    }

    [Test]
    public async Task WhenAutomaticCheckIsTurnedOnWhileConnectedThenItChecks()
    {
        using var viewModel = CreateViewModel(new FirmwareCheckSettings(Automatic: false));
        Connect();

        viewModel.IsAutomatic = true;

        await Assert.That(_checks).IsEqualTo(1);
    }

    [Test]
    public async Task WhenAutomaticCheckIsTurnedOffThenTheChoiceIsSaved()
    {
        using var viewModel = CreateViewModel();

        viewModel.IsAutomatic = false;

        await Assert.That(_saved.Last().Automatic).IsFalse();
    }

    [Test]
    public async Task WhenCheckNowRunsThenItChecksDespiteARecentCheck()
    {
        using var viewModel = CreateViewModel(new FirmwareCheckSettings(LastChecked: _time.GetUtcNow()));
        Connect();

        await viewModel.CheckNowCommand.ExecuteAsync(null);

        await Assert.That(_checks).IsEqualTo(1);
    }

    [Test]
    public async Task WhenACheckSucceedsThenItsResultIsSaved()
    {
        using var viewModel = CreateViewModel(latest: () => new FirmwareRelease("1.2.0", InfoUrl));

        Connect("1.2.0");

        await Assert.That(_saved.Last()).IsEqualTo(new FirmwareCheckSettings(
            LastChecked: _time.GetUtcNow(), LatestVersion: "1.2.0", InfoUrl: InfoUrl.AbsoluteUri));
    }

    [Test]
    public async Task WhenTheCheckFailsThenStatusSaysSo()
    {
        using var viewModel = CreateViewModel(latest: () => throw new FirmwareCheckException("offline"));

        Connect();

        await Assert.That(viewModel.StatusText).IsEqualTo(Strings.Get("Firmware_CheckFailed"));
    }

    [Test]
    public async Task WhenTheCheckFailsThenTheRememberedReleaseStaysShown()
    {
        var settings = new FirmwareCheckSettings(LastChecked: _time.GetUtcNow().AddDays(-2), LatestVersion: "1.2.0");
        using var viewModel = CreateViewModel(settings, () => throw new FirmwareCheckException("offline"));

        Connect("1.1.4");

        await Assert.That(viewModel.IsUpdateAvailable).IsTrue();
    }

    [Test]
    public async Task WhenTheCheckFailedThenStateChangesWithinTheHourDoNotRetry()
    {
        using var viewModel = CreateViewModel(latest: () => throw new FirmwareCheckException("offline"));
        Connect();
        _time.Advance(TimeSpan.FromMinutes(59));

        Connect();

        await Assert.That(_checks).IsEqualTo(1);
    }

    [Test]
    public async Task WhenTheCheckFailedThenItIsRetriedAfterAnHour()
    {
        using var viewModel = CreateViewModel(latest: () => throw new FirmwareCheckException("offline"));
        Connect();
        _time.Advance(TimeSpan.FromHours(1));

        Connect();

        await Assert.That(_checks).IsEqualTo(2);
    }

    [Test]
    public async Task WhenTheModelIsNotListedThenStatusIsEmpty()
    {
        using var viewModel = CreateViewModel(latest: () => null);

        Connect();

        await Assert.That(viewModel.StatusText).IsEmpty();
    }

    [Test]
    public async Task WhenTheRememberedInfoUrlIsNotHttpsThenNoInfoPageIsOffered()
    {
        var settings = new FirmwareCheckSettings(
            LastChecked: _time.GetUtcNow(), LatestVersion: "1.2.0", InfoUrl: "file:///C:/Windows/System32/calc.exe");
        using var viewModel = CreateViewModel(settings);

        Connect("1.1.4");

        await Assert.That(viewModel.HasInfoPage).IsFalse();
    }

    [Test]
    public async Task WhenHeadsetDisconnectsThenStatusIsCleared()
    {
        using var viewModel = CreateViewModel();
        Connect();

        Report(DeviceState.Disconnected);

        await Assert.That(viewModel.StatusText).IsEmpty();
    }
}
