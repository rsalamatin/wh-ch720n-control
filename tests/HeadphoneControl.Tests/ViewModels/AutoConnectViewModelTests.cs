using HeadphoneControl.Core;
using HeadphoneControl.Protocol.Devices;
using HeadphoneControl.ViewModels;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;

namespace HeadphoneControl.Tests.ViewModels;

public class AutoConnectViewModelTests
{
    private static readonly DiscoveredHeadset Headset = new("WH-CH720N", "bluetooth-id", ProtocolGeneration.V2);

    private readonly FakeTimeProvider _time = new();
    private readonly IHeadsetPresenceMonitor _presence = Substitute.For<IHeadsetPresenceMonitor>();
    private readonly List<bool> _saved = [];
    private int _requests;

    private AutoConnectViewModel CreateViewModel(bool isEnabled = true)
    {
        var viewModel = new AutoConnectViewModel(
            _presence, isEnabled, _saved.Add, NullLogger<AutoConnectViewModel>.Instance, action => action(), _time);
        viewModel.ConnectRequested += (_, _) => Interlocked.Increment(ref _requests);
        return viewModel;
    }

    private void ReportHeadsetJoined() =>
        _presence.HeadsetConnected += Raise.Event<EventHandler<DiscoveredHeadset>>(_presence, Headset);

    private async Task WaitForRequestsAsync(int count)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (Volatile.Read(ref _requests) < count && DateTime.UtcNow < deadline)
        {
            await Task.Delay(5);
        }
    }

    [Test]
    public async Task WhenHeadsetJoinsThePcThenAConnectIsRequestedOnceItSettled()
    {
        using var viewModel = CreateViewModel();

        ReportHeadsetJoined();
        _time.Advance(AutoConnectViewModel.SettleDelay);
        await WaitForRequestsAsync(1);

        await Assert.That(_requests).IsEqualTo(1);
    }

    [Test]
    public async Task WhenHeadsetJoinsThePcThenNoConnectIsRequestedBeforeItSettled()
    {
        using var viewModel = CreateViewModel();

        ReportHeadsetJoined();
        _time.Advance(AutoConnectViewModel.SettleDelay - TimeSpan.FromMilliseconds(1));

        await Assert.That(_requests).IsEqualTo(0);
    }

    [Test]
    public async Task WhenDisabledThenAHeadsetJoiningRequestsNoConnect()
    {
        using var viewModel = CreateViewModel(isEnabled: false);

        ReportHeadsetJoined();
        _time.Advance(AutoConnectViewModel.SettleDelay);

        await Assert.That(_requests).IsEqualTo(0);
    }

    [Test]
    public async Task WhenDisabledWhileSettlingThenNoConnectIsRequested()
    {
        using var viewModel = CreateViewModel();
        ReportHeadsetJoined();

        viewModel.IsEnabled = false;
        _time.Advance(AutoConnectViewModel.SettleDelay);

        await Assert.That(_requests).IsEqualTo(0);
    }

    [Test]
    public async Task WhenHeadsetIsReportedAgainWhileSettlingThenOneConnectIsRequested()
    {
        using var viewModel = CreateViewModel();
        ReportHeadsetJoined();

        ReportHeadsetJoined();
        _time.Advance(AutoConnectViewModel.SettleDelay);
        await WaitForRequestsAsync(1);

        await Assert.That(_requests).IsEqualTo(1);
    }

    [Test]
    public async Task WhenDisposedThenAHeadsetJoiningRequestsNoConnect()
    {
        var viewModel = CreateViewModel();
        viewModel.Dispose();

        ReportHeadsetJoined();
        _time.Advance(AutoConnectViewModel.SettleDelay);

        await Assert.That(_requests).IsEqualTo(0);
    }

    [Test]
    public async Task WhenHeldOffThenAHeadsetJoiningRequestsNoConnect()
    {
        using var viewModel = CreateViewModel();
        viewModel.HoldOff();

        ReportHeadsetJoined();
        _time.Advance(AutoConnectViewModel.SettleDelay);

        await Assert.That(_requests).IsEqualTo(0);
    }

    [Test]
    public async Task WhenTheHoldOffHasPassedThenAHeadsetJoiningRequestsAConnect()
    {
        using var viewModel = CreateViewModel();
        viewModel.HoldOff();
        _time.Advance(AutoConnectViewModel.FailureHoldOff);

        ReportHeadsetJoined();
        _time.Advance(AutoConnectViewModel.SettleDelay);
        await WaitForRequestsAsync(1);

        await Assert.That(_requests).IsEqualTo(1);
    }

    [Test]
    public async Task WhenTurnedOffThenTheChoiceIsSaved()
    {
        using var viewModel = CreateViewModel();

        viewModel.IsEnabled = false;

        await Assert.That(_saved).IsEquivalentTo([false]);
    }
}
