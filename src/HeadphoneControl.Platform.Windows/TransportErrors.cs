using System.Runtime.InteropServices;
using HeadphoneControl.Protocol.Transport;

namespace HeadphoneControl.Platform.Windows;

/// <summary>
/// Turns WinRT/COM failures from the Bluetooth stack into <see cref="TransportException"/>s whose message says
/// what the user can do about them. WinRT only reports an HRESULT, so the mapping is keyed on it.
/// </summary>
internal static class TransportErrors
{
    private const int AccessDenied = unchecked((int)0x80070005);
    private const int GenFailure = unchecked((int)0x8007001F);
    private const int SemaphoreTimeout = unchecked((int)0x80070079);
    private const int OperationAborted = unchecked((int)0x800703E3);
    private const int DeviceNotConnected = unchecked((int)0x8007048F);
    private const int NotFound = unchecked((int)0x80070490);
    private const int Timeout = unchecked((int)0x800705B4);
    private const int WsaAddressInUse = unchecked((int)0x80072740);
    private const int WsaNetDown = unchecked((int)0x80072742);
    private const int WsaConnectionAborted = unchecked((int)0x80072745);
    private const int WsaConnectionReset = unchecked((int)0x80072746);
    private const int WsaNotConnected = unchecked((int)0x80072749);
    private const int WsaTimedOut = unchecked((int)0x8007274C);
    private const int WsaConnectionRefused = unchecked((int)0x8007274D);
    private const int WsaHostDown = unchecked((int)0x80072750);
    private const int WsaHostUnreachable = unchecked((int)0x80072751);
    private const int ObjectClosed = unchecked((int)0x80000013);

    /// <summary>
    /// Wraps <paramref name="error"/> in a <see cref="TransportException"/> named after <paramref name="operation"/>,
    /// e.g. <c>"Connecting to WH-CH720N failed: access denied ... (HRESULT 0x80070005)."</c>
    /// </summary>
    public static TransportException FromPlatformError(string operation, Exception error)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operation);
        ArgumentNullException.ThrowIfNull(error);

        // WinRT messages often repeat the same sentence on several lines; the first one is enough.
        var reason = Describe(error.HResult)
            ?? error.Message.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault()
            ?? error.GetType().Name;
        return new TransportException($"{operation} failed: {reason} (HRESULT 0x{error.HResult:X8}).", error);
    }

    // CsWinRT maps some Bluetooth/socket HRESULTs to InvalidOperation/Argument/InvalidCast exceptions, so those
    // count as platform errors too; cancellation and already-mapped transport errors do not.
    internal static bool IsPlatformError(Exception error) =>
        error is not (OperationCanceledException or TransportException)
            and (COMException or UnauthorizedAccessException or TimeoutException or IOException
                or InvalidOperationException or ArgumentException or InvalidCastException);

    private static string? Describe(int hresult) => hresult switch
    {
        AccessDenied =>
            "access denied; check Settings > Privacy & security and that no other app holds the headset's control channel",
        DeviceNotConnected or WsaHostDown or WsaHostUnreachable or SemaphoreTimeout =>
            "the headset is not in range or is switched off",
        WsaTimedOut or Timeout =>
            "the headset did not respond; it may be out of range or its control channel may be in use by a phone",
        WsaConnectionRefused =>
            "the headset refused the connection; its control channel may be in use by another device (e.g. the Sony app on a phone)",
        WsaAddressInUse =>
            "the control channel is already open by another connection on this PC; close other instances of this app or other Sony tools",
        NotFound =>
            "the Sony control service was not found; re-pair the headset in Windows Bluetooth settings",
        WsaNetDown =>
            "the Bluetooth radio is off or unavailable",
        WsaConnectionReset or WsaConnectionAborted or WsaNotConnected or OperationAborted or ObjectClosed =>
            "the Bluetooth link was closed",
        GenFailure =>
            "the Bluetooth driver reported a general failure; toggle Bluetooth off and on",
        _ => null,
    };
}
