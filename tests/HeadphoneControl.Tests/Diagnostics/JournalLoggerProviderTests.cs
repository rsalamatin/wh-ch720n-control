using System.IO.MemoryMappedFiles;
using HeadphoneControl.Diagnostics;
using Microsoft.Extensions.Logging;

namespace HeadphoneControl.Tests.Diagnostics;

public sealed class JournalLoggerProviderTests : IDisposable
{
    // Small so each test writes little; the production cap is JournalLoggerProvider.DefaultMaxFileBytes.
    private const int MaxFileBytes = 64 * 1024;

    private readonly string _directory = Directory.CreateDirectory(
        Path.Combine(Path.GetTempPath(), "HeadphoneControl.Tests", Guid.NewGuid().ToString("N"))).FullName;

    private string LogPath => Path.Combine(_directory, "test.log");

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    [Test]
    public async Task WhenLineIsLoggedThenItIsAppendedToTheFile()
    {
        var provider = CreateProvider(new DiagnosticsJournal());
        provider.CreateLogger("Test.Category").LogInformation("hello file");

        await provider.DisposeAsync();

        await Assert.That(await File.ReadAllTextAsync(LogPath)).Contains("[INF] Category: hello file");
    }

    [Test]
    public async Task WhenNoLogFileExistsThenANewFileIsCreated()
    {
        var provider = CreateProvider(new DiagnosticsJournal());
        provider.CreateLogger("Test").LogInformation("first line");

        await provider.DisposeAsync();

        await Assert.That(File.Exists(LogPath)).IsTrue();
    }

    [Test]
    public async Task WhenFlushedThenLoggedLineIsAlreadyOnDisk()
    {
        await using var provider = CreateProvider(new DiagnosticsJournal());
        provider.CreateLogger("Test").LogCritical("crash");

        provider.Flush(TimeSpan.FromSeconds(5));

        await using var stream = new FileStream(LogPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream);
        await Assert.That(await reader.ReadToEndAsync()).Contains("crash");
    }

    [Test]
    public async Task WhenMoreThanTheLimitIsLoggedThenTheFileStaysWithinTheLimit()
    {
        var provider = CreateProvider(new DiagnosticsJournal());

        LogPadding(provider, totalChars: MaxFileBytes * 3 / 2);
        await provider.DisposeAsync();

        await Assert.That(new FileInfo(LogPath).Length).IsLessThanOrEqualTo(MaxFileBytes);
    }

    [Test]
    public async Task WhenTheFileIsFullThenTheOlderLinesAreDropped()
    {
        var provider = CreateProvider(new DiagnosticsJournal());
        provider.CreateLogger("Test").LogInformation("oldest line");

        LogPadding(provider, totalChars: MaxFileBytes * 3 / 2);
        await provider.DisposeAsync();

        await Assert.That(await File.ReadAllTextAsync(LogPath)).DoesNotContain("oldest line");
    }

    [Test]
    public async Task WhenTheFileIsFullThenTheNewestLineIsInTheLogFile()
    {
        var provider = CreateProvider(new DiagnosticsJournal());
        LogPadding(provider, totalChars: MaxFileBytes * 3 / 2);

        provider.CreateLogger("Test").LogInformation("newest line");
        await provider.DisposeAsync();

        await Assert.That(await File.ReadAllTextAsync(LogPath)).Contains("newest line");
    }

    [Test]
    public async Task WhenTheFileIsFullThenNoSecondFileIsCreated()
    {
        var provider = CreateProvider(new DiagnosticsJournal());

        LogPadding(provider, totalChars: MaxFileBytes * 3 / 2);
        await provider.DisposeAsync();

        await Assert.That(Directory.GetFiles(_directory)).IsEquivalentTo([LogPath]);
    }

    [Test]
    public async Task WhenAViewerHoldsTheFileOpenThenTheFullFileIsStillCleared()
    {
        var provider = CreateProvider(new DiagnosticsJournal());
        provider.CreateLogger("Test").LogInformation("oldest line");
        provider.Flush(TimeSpan.FromSeconds(5));
        await using var viewer = new FileStream(LogPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);

        LogPadding(provider, totalChars: MaxFileBytes * 3 / 2);
        await provider.DisposeAsync();

        await Assert.That(await ReadSharedAsync()).DoesNotContain("oldest line");
    }

    [Test]
    public async Task WhenTheFileCannotBeClearedThenJournalReportsIt()
    {
        var journal = new DiagnosticsJournal();
        var provider = CreateProvider(journal);
        provider.CreateLogger("Test").LogInformation("mapped");
        provider.Flush(TimeSpan.FromSeconds(5));
        using var mapping = MapLogFile();

        LogPadding(provider, totalChars: MaxFileBytes * 3 / 2);
        await provider.DisposeAsync();

        await Assert.That(journal.Snapshot().Any(line => line.Contains("could not be cleared", StringComparison.Ordinal))).IsTrue();
    }

    [Test]
    public async Task WhenTheFileCannotBeClearedThenLoggingContinuesInTheLogFile()
    {
        var provider = CreateProvider(new DiagnosticsJournal());
        provider.CreateLogger("Test").LogInformation("mapped");
        provider.Flush(TimeSpan.FromSeconds(5));
        using var mapping = MapLogFile();
        LogPadding(provider, totalChars: MaxFileBytes * 3 / 2);

        provider.CreateLogger("Test").LogInformation("after failed clear");
        await provider.DisposeAsync();

        await Assert.That(await ReadSharedAsync()).Contains("after failed clear");
    }

    [Test]
    public async Task WhenAnotherInstanceClearsTheSharedFileThenNoZeroFilledGapIsLeft()
    {
        var running = CreateProvider(new DiagnosticsJournal());
        LogPadding(running, totalChars: MaxFileBytes - 4000);
        running.Flush(TimeSpan.FromSeconds(5));
        var started = CreateProvider(new DiagnosticsJournal());

        LogPadding(started, totalChars: 10_000);
        started.Flush(TimeSpan.FromSeconds(5));
        running.CreateLogger("Test").LogInformation("running instance after the clear");
        await running.DisposeAsync();
        await started.DisposeAsync();

        await Assert.That(await File.ReadAllTextAsync(LogPath)).DoesNotContain('\0');
    }

    [Test]
    public async Task WhenExistingFileIsAtTheLimitThenItIsClearedBeforeTheFirstLine()
    {
        await File.WriteAllTextAsync(LogPath, new string('x', MaxFileBytes));
        var provider = CreateProvider(new DiagnosticsJournal());

        provider.CreateLogger("Test").LogInformation("after restart");
        await provider.DisposeAsync();

        await Assert.That(new FileInfo(LogPath).Length).IsLessThan(1024);
    }

    [Test]
    public async Task WhenASingleLineExceedsTheLimitThenTheFileStaysWithinTheLimit()
    {
        var provider = CreateProvider(new DiagnosticsJournal());

        provider.CreateLogger("Test").LogInformation(new string('x', MaxFileBytes * 2));
        await provider.DisposeAsync();

        await Assert.That(new FileInfo(LogPath).Length).IsLessThanOrEqualTo(MaxFileBytes);
    }

    [Test]
    public async Task WhenASingleLineExceedsTheLimitThenItsStartIsKept()
    {
        var provider = CreateProvider(new DiagnosticsJournal());

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

    private JournalLoggerProvider CreateProvider(DiagnosticsJournal journal) =>
        new(journal, LogPath, maxFileBytes: MaxFileBytes);

    // Windows refuses to truncate a file while another process has it mapped (ERROR_USER_MAPPED_FILE).
    private MemoryMappedFile MapLogFile() =>
        MemoryMappedFile.CreateFromFile(
            new FileStream(LogPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite),
            mapName: null,
            capacity: 0,
            MemoryMappedFileAccess.Read,
            HandleInheritability.None,
            leaveOpen: false);

    private async Task<string> ReadSharedAsync()
    {
        await using var stream = new FileStream(LogPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream);
        return await reader.ReadToEndAsync();
    }

    private static void LogPadding(JournalLoggerProvider provider, int totalChars)
    {
        var logger = provider.CreateLogger("Test");
        var padding = new string('p', 1000);
        for (var written = 0; written < totalChars; written += padding.Length)
        {
            logger.LogInformation("{Padding}", padding);
        }
    }
}
