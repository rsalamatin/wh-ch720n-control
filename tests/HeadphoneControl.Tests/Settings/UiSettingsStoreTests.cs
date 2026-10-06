using HeadphoneControl.Settings;
using Microsoft.Extensions.Logging.Abstractions;

namespace HeadphoneControl.Tests.Settings;

public sealed class UiSettingsStoreTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "HeadphoneControl.Tests", Guid.NewGuid().ToString("N"));

    private string SettingsPath => Path.Combine(_directory, "nested", "settings.json");

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    private UiSettingsStore CreateStore() => new(SettingsPath, NullLogger<UiSettingsStore>.Instance);

    [Test]
    public async Task WhenNoFileExistsThenDefaultsAreLoaded()
    {
        var store = CreateStore();

        var settings = store.Load();

        await Assert.That(settings).IsEqualTo(UiSettings.Default);
    }

    [Test]
    public async Task WhenSettingsAreSavedThenTheyAreLoadedBack()
    {
        var store = CreateStore();
        store.Save(new UiSettings(ThemePreference.Dark));

        var settings = CreateStore().Load();

        await Assert.That(settings.Theme).IsEqualTo(ThemePreference.Dark);
    }

    [Test]
    public async Task WhenFirmwareCheckIsSavedThenItIsLoadedBack()
    {
        var firmwareCheck = new FirmwareCheckSettings(
            Automatic: false,
            LastChecked: new DateTimeOffset(2026, 10, 6, 12, 0, 0, TimeSpan.Zero),
            LatestVersion: "1.1.4",
            InfoUrl: "https://example.com/wh-ch720n",
            NotifiedVersion: "1.1.4");
        var store = CreateStore();
        store.Save(new UiSettings(FirmwareCheck: firmwareCheck));

        var settings = CreateStore().Load();

        await Assert.That(settings.FirmwareCheck).IsEqualTo(firmwareCheck);
    }

    [Test]
    public async Task WhenFileHasOnlyAThemeThenFirmwareCheckIsAbsent()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
        await File.WriteAllTextAsync(SettingsPath, """{ "Theme": "Dark" }""");
        var store = CreateStore();

        var settings = store.Load();

        await Assert.That(settings).IsEqualTo(new UiSettings(ThemePreference.Dark));
    }

    [Test]
    public async Task WhenFileIsCorruptThenDefaultsAreLoaded()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
        await File.WriteAllTextAsync(SettingsPath, "{ not json");
        var store = CreateStore();

        var settings = store.Load();

        await Assert.That(settings).IsEqualTo(UiSettings.Default);
    }
}
