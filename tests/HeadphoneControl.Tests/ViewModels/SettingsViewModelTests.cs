using HeadphoneControl.Core;
using HeadphoneControl.FirmwareUpdates;
using HeadphoneControl.Protocol.Devices;
using HeadphoneControl.Settings;
using HeadphoneControl.ViewModels;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace HeadphoneControl.Tests.ViewModels;

public class SettingsViewModelTests
{
    private readonly List<ThemePreference> _selected = [];

    private SettingsViewModel CreateViewModel(ThemePreference theme)
    {
        var device = Substitute.For<IHeadphoneDevice>();
        device.State.Returns(DeviceState.Disconnected);
        Action<Action> inline = action => action();
        return new SettingsViewModel(
            theme,
            _selected.Add,
            new FirmwareUpdateViewModel(
                device,
                _ => Task.FromResult<FirmwareRelease?>(null),
                new FirmwareCheckSettings(Automatic: false),
                _ => { },
                NullLogger<FirmwareUpdateViewModel>.Instance,
                inline),
            new AutoConnectViewModel(
                Substitute.For<IHeadsetPresenceMonitor>(),
                isEnabled: true,
                _ => { },
                NullLogger<AutoConnectViewModel>.Instance,
                inline));
    }

    [Test]
    public async Task WhenCreatedThenTheSavedThemeIsSelected()
    {
        var viewModel = CreateViewModel(ThemePreference.Dark);

        await Assert.That(viewModel.SelectedTheme?.Value).IsEqualTo(ThemePreference.Dark);
    }

    [Test]
    public async Task WhenCreatedThenNoThemeIsApplied()
    {
        _ = CreateViewModel(ThemePreference.Dark);

        await Assert.That(_selected).IsEmpty();
    }

    [Test]
    public async Task WhenAThemeIsChosenThenItIsApplied()
    {
        var viewModel = CreateViewModel(ThemePreference.System);

        viewModel.SelectedTheme = viewModel.Themes.First(t => t.Value == ThemePreference.Light);

        await Assert.That(_selected).IsEquivalentTo([ThemePreference.Light]);
    }

    [Test]
    public async Task WhenTheSelectionIsClearedThenTheThemeStays()
    {
        var viewModel = CreateViewModel(ThemePreference.Light);

        viewModel.SelectedTheme = null;

        await Assert.That(viewModel.SelectedTheme?.Value).IsEqualTo(ThemePreference.Light);
    }
}
