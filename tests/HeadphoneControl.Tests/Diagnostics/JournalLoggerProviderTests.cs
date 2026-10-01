using HeadphoneControl.Diagnostics;
using Microsoft.Extensions.Logging;

namespace HeadphoneControl.Tests.Diagnostics;

public sealed class JournalLoggerProviderTests : IDisposable
{
    private const int MaxFileBytes = 10 * 1024 * 1024;

    private readonly string _directory = Directory.CreateDirectory(
        Path.Combine(Path.GetTempPath(), "HeadphoneControl.Tests", Guid.NewGuid().ToString("N"))).FullName;

    private string LogPath => Path.Combine(_directory, "test.log");

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    [Test]
    public async Task WhenLineIsLoggedThenItIsAppendedToTheFile()
    {
        var provider = new JournalLoggerProvider(new DiagnosticsJournal(), LogPath);
        provider.CreateLogger("Test.Category").LogInformation("hello file");

        await provider.DisposeAsync();

        await Assert.That(await File.ReadAllTextAsync(LogPath)).Contains("[INF] Category: hello file");
    }

    [Test]
    public async Task WhenNoLogFileExistsThenANewFileIsCreated()
    {
        var provider = new JournalLoggerProvider(new DiagnosticsJournal(), LogPath);
        provider.CreateLogger("Test").LogInformation("first line");

        await provider.DisposeAsync();

        await Assert.That(File.Exists(LogPath)).IsTrue();
    }

    [Test]
    public async Task WhenFlushedThenLoggedLineIsAlreadyOnDisk()
    {
        await using var provider = new JournalLoggerProvider(new DiagnosticsJournal(), LogPath);
        provider.CreateLogger("Test").LogCritical("crash");

        provider.Flush(TimeSpan.FromSeconds(5));

        await using var stream = new FileStream(LogPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream);
        await Assert.That(await reader.ReadToEndAsync()).Contains("crash");
    }

    [Test]
    public async Task WhenMoreThanTheLimitIsLoggedThenTheFileStaysWithinTheLimit()
    {
        var provider = new JournalLoggerProvider(new DiagnosticsJournal(), LogPath);

        LogPadding(provider, totalChars: MaxFileBytes * 3 / 2);
        await provider.DisposeAsync();

        await Assert.That(new FileInfo(LogPath).Length).IsLessThanOrEqualTo(MaxFileBytes);
    }

    [Test]
    public async Task WhenTheFileIsFullThenTheOlderLinesAreDropped()
    {
        var provider = new JournalLoggerProvider(new DiagnosticsJournal(), LogPath);
        provider.CreateLogger("Test").LogInformation("oldest line");

        LogPadding(provider, totalChars: MaxFileBytes * 3 / 2);
        await provider.DisposeAsync();

        await Assert.That(await File.ReadAllTextAsync(LogPath)).DoesNotContain("oldest line");
    }

    [Test]
    public async Task WhenTheFileIsFullThenTheNewestLineIsInTheLogFile()
    {
        var provider = new JournalLoggerProvider(new DiagnosticsJournal(), LogPath);
        LogPadding(provider, totalChars: MaxFileBytes * 3 / 2);

        provider.CreateLogger("Test").LogInformation("newest line");
        await provider.DisposeAsync();

        await Assert.That(await File.ReadAllTextAsync(LogPath)).Contains("newest line");
    }

    [Test]
    public async Task WhenTheFileIsFullThenNoSecondFileIsCreated()
    {
        var provider = new JournalLoggerProvider(new DiagnosticsJournal(), LogPath);

        LogPadding(provider, totalChars: MaxFileBytes * 3 / 2);
        await provider.DisposeAsync();

        await Assert.That(Directory.GetFiles(_directory)).IsEquivalentTo([LogPath]);
    }

    [Test]
    public async Task WhenAViewerHoldsTheFileOpenThenTheFullFileIsStillCleared()
    {
        var provider = new JournalLoggerProvider(new DiagnosticsJournal(), LogPath);
        provider.Flush(TimeSpan.FromSeconds(5));
        await using var viewer = new FileStream(LogPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);

        LogPadding(provider, totalChars: MaxFileBytes * 3 / 2);
        await provider.DisposeAsync();

        await Assert.That(new FileInfo(LogPath).Length).IsLessThanOrEqualTo(MaxFileBytes);
    }

    [Test]
    public async Task WhenExistingFileIsAtTheLimitThenItIsClearedBeforeTheFirstLine()
    {
        await File.WriteAllTextAsync(LogPath, new string('x', MaxFileBytes));
        var provider = new JournalLoggerProvider(new DiagnosticsJournal(), LogPath);

        provider.CreateLogger("Test").LogInformation("after restart");
        await provider.DisposeAsync();

        await Assert.That(new FileInfo(LogPath).Length).IsLessThan(1024);
    }

    [Test]
    public async Task WhenASingleLineExceedsTheLimitThenTheFileStaysWithinTheLimit()
    {
        var provider = new JournalLoggerProvider(new DiagnosticsJournal(), LogPath);

        provider.CreateLogger("Test").LogInformation(new string('x', MaxFileBytes * 2));
        await provider.DisposeAsync();

        await Assert.That(new FileInfo(LogPath).Length).IsLessThanOrEqualTo(MaxFileBytes);
    }

    [Test]
    public async Task WhenASingleLineExceedsTheLimitThenItsStartIsKept()
    {
        var provider = new JournalLoggerProvider(new DiagnosticsJournal(), LogPath);

        provider.CreateLogger("Test").LogInformation("start of huge line " + new string('x', MaxFileBytes * 2));
        await provider.DisposeAsync();

        await Assert.That(await File.ReadAllTextAsync(LogPath)).Contains("start of huge line");
    }

    [Test]
    public async Task WhenLogFileIsNotWritableThenJournalReportsIt()
    {
        var path = Path.Combine(_directory, "missing", "x.log");
        var journal = new DiagnosticsJournal();
        var provider = new JournalLoggerProvider(journal, path);
        provider.CreateLogger("Test").LogInformation("still shown");

        await provider.DisposeAsync();

        await Assert.That(journal.Snapshot().Any(line => line.Contains("not writable", StringComparison.Ordinal))).IsTrue();
    }

    [Test]
    public async Task WhenLogFileIsNotWritableThenLinesStillReachTheJournal()
    {
        var path = Path.Combine(_directory, "missing", "x.log");
        var journal = new DiagnosticsJournal();
        var provider = new JournalLoggerProvider(journal, path);

        provider.CreateLogger("Test").LogInformation("still shown");
        await provider.DisposeAsync();

        await Assert.That(journal.Snapshot().Any(line => line.Contains("still shown", StringComparison.Ordinal))).IsTrue();
    }

    private static void LogPadding(JournalLoggerProvider provider, int totalChars)
    {
        var logger = provider.CreateLogger("Test");
        // Large lines keep the count far below the provider's 10,000-line queue, so none are dropped.
        var padding = new string('p', 64 * 1024);
        for (var written = 0; written < totalChars; written += padding.Length)
        {
            logger.LogInformation("{Padding}", padding);
        }
    }
}
