namespace HeadphoneControl.FirmwareUpdates;

public enum FirmwareStatus
{
    /// <summary>One of the two versions is not a dotted number, so they cannot be compared.</summary>
    Unknown,
    UpToDate,
    UpdateAvailable,
}

/// <summary>The newest firmware known for a headset model.</summary>
/// <param name="Version">Dotted numbers, e.g. <c>1.1.4</c>.</param>
/// <param name="InfoUrl">The manufacturer's page about the release; always https when set.</param>
public sealed record FirmwareRelease(string Version, Uri? InfoUrl)
{
    /// <summary>
    /// Compares number by number, so 1.1.10 is newer than 1.1.4 and 1.1 equals 1.1.0. An installed version newer than
    /// this release counts as up to date.
    /// </summary>
    public FirmwareStatus StatusFor(string installedVersion)
    {
        ArgumentNullException.ThrowIfNull(installedVersion);
        if (!FirmwareVersion.TryParse(Version, out var latest) || !FirmwareVersion.TryParse(installedVersion, out var installed))
        {
            return FirmwareStatus.Unknown;
        }

        return FirmwareVersion.Compare(latest, installed) > 0 ? FirmwareStatus.UpdateAvailable : FirmwareStatus.UpToDate;
    }
}
