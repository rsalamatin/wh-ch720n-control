using System.Net;

namespace HeadphoneControl.FirmwareUpdates.Tests;

public class FirmwareUpdateCheckerTests
{
    private const string Manifest =
        """{ "WH-CH720N": { "latest": "1.1.4", "infoUrl": "https://example.com/wh-ch720n" } }""";

    private static readonly Uri ManifestUrl = new("https://example.com/firmware.json");

    private static FirmwareUpdateChecker CreateChecker(string body, HttpStatusCode status = HttpStatusCode.OK) =>
        CreateChecker(_ => Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body) }));

    private static FirmwareUpdateChecker CreateChecker(Func<HttpRequestMessage, Task<HttpResponseMessage>> respond) =>
        new(new HttpClient(new StubHandler(respond)), ManifestUrl, TimeSpan.FromSeconds(30));

    [Test]
    public async Task WhenManifestListsTheModelThenItsLatestVersionIsReturned()
    {
        var checker = CreateChecker(Manifest);

        var release = await checker.GetLatestAsync("WH-CH720N", CancellationToken.None);

        await Assert.That(release?.Version).IsEqualTo("1.1.4");
    }

    [Test]
    public async Task WhenManifestListsTheModelThenItsInfoUrlIsReturned()
    {
        var checker = CreateChecker(Manifest);

        var release = await checker.GetLatestAsync("WH-CH720N", CancellationToken.None);

        await Assert.That(release?.InfoUrl).IsEqualTo(new Uri("https://example.com/wh-ch720n"));
    }

    [Test]
    public async Task WhenModelDiffersOnlyInCaseThenItsReleaseIsReturned()
    {
        var checker = CreateChecker(Manifest);

        var release = await checker.GetLatestAsync("wh-ch720n", CancellationToken.None);

        await Assert.That(release?.Version).IsEqualTo("1.1.4");
    }

    [Test]
    public async Task WhenManifestIsRequestedThenTheManifestUrlIsFetched()
    {
        Uri? requested = null;
        var checker = CreateChecker(request =>
        {
            requested = request.RequestUri;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Manifest) });
        });

        await checker.GetLatestAsync("WH-CH720N", CancellationToken.None);

        await Assert.That(requested).IsEqualTo(ManifestUrl);
    }

    [Test]
    public async Task WhenModelIsNotListedThenNoReleaseIsReturned()
    {
        var checker = CreateChecker(Manifest);

        var release = await checker.GetLatestAsync("WH-1000XM4", CancellationToken.None);

        await Assert.That(release).IsNull();
    }

    [Test]
    [Arguments("http://example.com/wh-ch720n")]
    [Arguments("file:///C:/Windows/System32/calc.exe")]
    [Arguments("ms-settings:bluetooth")]
    [Arguments("not a url")]
    public async Task WhenInfoUrlIsNotHttpsThenItIsDropped(string infoUrl)
    {
        var checker = CreateChecker($$"""{ "WH-CH720N": { "latest": "1.1.4", "infoUrl": "{{infoUrl}}" } }""");

        var release = await checker.GetLatestAsync("WH-CH720N", CancellationToken.None);

        await Assert.That(release?.InfoUrl).IsNull();
    }

    [Test]
    public async Task WhenServerAnswersWithAnErrorThenFirmwareCheckExceptionIsThrown()
    {
        var checker = CreateChecker("Not found", HttpStatusCode.NotFound);

        var act = () => checker.GetLatestAsync("WH-CH720N", CancellationToken.None);

        await Assert.That(act).Throws<FirmwareCheckException>();
    }

    [Test]
    public async Task WhenTheNetworkFailsThenFirmwareCheckExceptionIsThrown()
    {
        var checker = CreateChecker(_ => throw new HttpRequestException("No such host is known."));

        var act = () => checker.GetLatestAsync("WH-CH720N", CancellationToken.None);

        await Assert.That(act).Throws<FirmwareCheckException>();
    }

    [Test]
    public async Task WhenTheRequestTimesOutThenFirmwareCheckExceptionIsThrown()
    {
        var checker = CreateChecker(_ => throw new TaskCanceledException("The request timed out."));

        var act = () => checker.GetLatestAsync("WH-CH720N", CancellationToken.None);

        await Assert.That(act).Throws<FirmwareCheckException>();
    }

    [Test]
    public async Task WhenTheCallerCancelsThenOperationCanceledExceptionIsThrown()
    {
        using var cancellation = new CancellationTokenSource();
        var checker = CreateChecker(async request =>
        {
            await cancellation.CancelAsync();
            cancellation.Token.ThrowIfCancellationRequested();
            return new HttpResponseMessage(HttpStatusCode.OK);
        });

        var act = () => checker.GetLatestAsync("WH-CH720N", cancellation.Token);

        await Assert.That(act).Throws<OperationCanceledException>();
    }

    [Test]
    [Arguments("<html>Sign in</html>")]
    [Arguments("null")]
    [Arguments("""{ "WH-CH720N": "1.1.4" }""")]
    [Arguments("""{ "WH-CH720N": { "infoUrl": "https://example.com" } }""")]
    [Arguments("""{ "WH-CH720N": { "latest": "newest" } }""")]
    [Arguments("""{ "WH-CH720N": { "latest": "1.1.4-beta" } }""")]
    [Arguments("""{ "WH-CH720N": { "latest": "000000000000000000000000000000001.1.4" } }""")]
    public async Task WhenManifestIsInvalidThenFirmwareCheckExceptionIsThrown(string body)
    {
        var checker = CreateChecker(body);

        var act = () => checker.GetLatestAsync("WH-CH720N", CancellationToken.None);

        await Assert.That(act).Throws<FirmwareCheckException>();
    }

    [Test]
    public async Task WhenManifestIsLargerThan64KilobytesThenFirmwareCheckExceptionIsThrown()
    {
        var padding = new string(' ', 64 * 1024);
        var checker = CreateChecker(Manifest + padding);

        var act = () => checker.GetLatestAsync("WH-CH720N", CancellationToken.None);

        await Assert.That(act).Throws<FirmwareCheckException>();
    }

    [Test]
    public async Task WhenTheBodyStallsAfterTheHeadersThenFirmwareCheckExceptionIsThrown()
    {
        using var stalled = new StalledStream();
        var checker = new FirmwareUpdateChecker(
            new HttpClient(new StubHandler(_ => Task.FromResult(
                new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(stalled) }))),
            ManifestUrl,
            TimeSpan.FromMilliseconds(50));

        var act = () => checker.GetLatestAsync("WH-CH720N", CancellationToken.None);

        await Assert.That(act).Throws<FirmwareCheckException>();
    }

    [Test]
    public async Task WhenManifestStartsWithAByteOrderMarkThenItIsStillRead()
    {
        var checker = CreateChecker("\uFEFF" + Manifest);

        var release = await checker.GetLatestAsync("WH-CH720N", CancellationToken.None);

        await Assert.That(release?.Version).IsEqualTo("1.1.4");
    }

    [Test]
    public async Task WhenTheRepositoryManifestIsReadThenItListsTheHeadset()
    {
        var checker = CreateChecker(await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "firmware.json")));

        var release = await checker.GetLatestAsync("WH-CH720N", CancellationToken.None);

        await Assert.That(release?.InfoUrl).IsNotNull();
    }

    [Test]
    public async Task WhenManifestUrlIsNotHttpThenArgumentExceptionIsThrown()
    {
        using var http = new HttpClient();

        var act = () => new FirmwareUpdateChecker(http, new Uri("file:///C:/firmware.json"), TimeSpan.FromSeconds(30));

        await Assert.That(act).Throws<ArgumentException>();
    }

    // Never returns data, like a connection that goes quiet after the headers.
    private sealed class StalledStream : MemoryStream
    {
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return 0;
        }
    }

    private sealed class StubHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            respond(request);
    }
}
