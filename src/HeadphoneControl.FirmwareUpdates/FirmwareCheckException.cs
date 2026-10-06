namespace HeadphoneControl.FirmwareUpdates;

/// <summary>The firmware manifest could not be downloaded or is not valid.</summary>
public sealed class FirmwareCheckException : Exception
{
    public FirmwareCheckException(string message)
        : base(message)
    {
    }

    public FirmwareCheckException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
