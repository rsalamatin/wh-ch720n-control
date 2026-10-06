using System.Text.Json;
using System.Text.Json.Serialization;

namespace HeadphoneControl.FirmwareUpdates;

/// <summary>
/// Looks up the newest firmware of a headset model in a JSON manifest:
/// <c>{ "WH-CH720N": { "latest": "1.1.4", "infoUrl": "https://…" } }</c>.
/// </summary>
/// <remarks>The request carries nothing but the manifest URL. This class never downloads or installs firmware.</remarks>
public sealed class FirmwareUpdateChecker
{
    // A manifest is a few hundred bytes; the cap keeps a wrong or hostile URL from filling memory.
    private const int MaxManifestBytes = 64 * 1024;

    private static readonly byte[] Utf8Bom = [0xEF, 0xBB, 0xBF];

    private readonly HttpClient _http;
    private readonly Uri _manifestUrl;
    private readonly TimeSpan _timeout;

    /// <param name="http">Not disposed by this class.</param>
    /// <param name="timeout">
    /// Bounds the whole check, headers and body. The client's own timeout stops applying once the headers arrive.
    /// </param>
    public FirmwareUpdateChecker(HttpClient http, Uri manifestUrl, TimeSpan timeout)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(manifestUrl);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(timeout, TimeSpan.Zero);
        _timeout = timeout;
        if (!manifestUrl.IsAbsoluteUri || (manifestUrl.Scheme != Uri.UriSchemeHttps && manifestUrl.Scheme != Uri.UriSchemeHttp))
        {
            throw new ArgumentException("The manifest URL must be an absolute http or https URL.", nameof(manifestUrl));
        }

        _http = http;
        _manifestUrl = manifestUrl;
    }

    /// <returns>The newest release, or null when the manifest does not list <paramref name="model"/>.</returns>
    /// <exception cref="FirmwareCheckException">
    /// The manifest could not be downloaded (offline, HTTP error, timeout), is larger than 64 KB, is not valid JSON, or
    /// its entry for the model has no dotted-number version.
    /// </exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    public async Task<FirmwareRelease?> GetLatestAsync(string model, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(model);
        var manifest = await DownloadManifestAsync(cancellationToken).ConfigureAwait(false);
        var entry = manifest.FirstOrDefault(e => string.Equals(e.Key, model, StringComparison.OrdinalIgnoreCase)).Value;
        if (entry is null)
        {
            return null;
        }

        if (entry.Latest is null || !FirmwareVersion.TryParse(entry.Latest, out _))
        {
            throw new FirmwareCheckException($"The firmware manifest has no valid version for {model}.");
        }

        // The link ends up in a browser, so anything but https (file:, a custom scheme) is dropped.
        var infoUrl = Uri.TryCreate(entry.InfoUrl, UriKind.Absolute, out var url) && url.Scheme == Uri.UriSchemeHttps
            ? url
            : null;
        return new FirmwareRelease(entry.Latest.Trim(), infoUrl);
    }

    private async Task<Dictionary<string, ManifestEntry?>> DownloadManifestAsync(CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_timeout);
        try
        {
            using var response = await _http
                .GetAsync(_manifestUrl, HttpCompletionOption.ResponseHeadersRead, timeout.Token)
                .ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            ReadOnlySpan<byte> content = await ReadBoundedAsync(response.Content, timeout.Token).ConfigureAwait(false);

            // The file is edited by hand, and Windows editors may save it with a byte order mark the parser rejects.
            if (content.StartsWith(Utf8Bom))
            {
                content = content[Utf8Bom.Length..];
            }

            return JsonSerializer.Deserialize(content, ManifestJsonContext.Default.DictionaryStringManifestEntry)
                ?? throw new FirmwareCheckException("The firmware manifest is empty.");
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or JsonException)
        {
            throw new FirmwareCheckException($"The firmware manifest could not be read: {ex.Message}", ex);
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            // The timeout above, or the client's own, which it also reports as a cancellation.
            throw new FirmwareCheckException("The firmware manifest request timed out.", ex);
        }
    }

    private static async Task<byte[]> ReadBoundedAsync(HttpContent content, CancellationToken cancellationToken)
    {
        using var stream = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        var buffer = new byte[MaxManifestBytes + 1];
        var length = await stream.ReadAtLeastAsync(buffer, buffer.Length, throwOnEndOfStream: false, cancellationToken)
            .ConfigureAwait(false);
        if (length > MaxManifestBytes)
        {
            throw new FirmwareCheckException($"The firmware manifest is larger than {MaxManifestBytes / 1024} KB.");
        }

        return buffer[..length];
    }
}

internal sealed record ManifestEntry(string? Latest, string? InfoUrl);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(Dictionary<string, ManifestEntry?>))]
internal sealed partial class ManifestJsonContext : JsonSerializerContext;
