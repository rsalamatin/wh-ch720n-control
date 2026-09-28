using System.Runtime.InteropServices;
using HeadphoneControl.Bluetooth;

namespace HeadphoneControl.Tests.Bluetooth;

public class TransportErrorsTests
{
    [Test]
    [Arguments(unchecked((int)0x80070005), "Connecting failed: access denied; check Settings > Privacy & security and that no other app holds the headset's control channel (HRESULT 0x80070005).")]
    [Arguments(unchecked((int)0x8007048F), "Connecting failed: the headset is not in range or is switched off (HRESULT 0x8007048F).")]
    [Arguments(unchecked((int)0x8007274C), "Connecting failed: the headset did not respond; it may be out of range or its control channel may be in use by a phone (HRESULT 0x8007274C).")]
    [Arguments(unchecked((int)0x8007274D), "Connecting failed: the headset refused the connection; its control channel may be in use by another device (e.g. the Sony app on a phone) (HRESULT 0x8007274D).")]
    [Arguments(unchecked((int)0x80072740), "Connecting failed: the control channel is already open by another connection on this PC; close other instances of this app or other Sony tools (HRESULT 0x80072740).")]
    [Arguments(unchecked((int)0x80070490), "Connecting failed: the Sony control service was not found; re-pair the headset in Windows Bluetooth settings (HRESULT 0x80070490).")]
    [Arguments(unchecked((int)0x80072742), "Connecting failed: the Bluetooth radio is off or unavailable (HRESULT 0x80072742).")]
    [Arguments(unchecked((int)0x80072746), "Connecting failed: the Bluetooth link was closed (HRESULT 0x80072746).")]
    public async Task WhenHResultIsKnownThenMessageExplainsCause(int hresult, string expected)
    {
        var error = new COMException("platform text", hresult);

        var mapped = TransportErrors.FromPlatformError("Connecting", error);

        await Assert.That(mapped.Message).IsEqualTo(expected);
    }

    [Test]
    public async Task WhenHResultIsUnknownThenPlatformMessageIsKept()
    {
        var error = new COMException(" Something odd happened. ", unchecked((int)0x80004005));

        var mapped = TransportErrors.FromPlatformError("Sending", error);

        await Assert.That(mapped.Message).IsEqualTo("Sending failed: Something odd happened. (HRESULT 0x80004005).");
    }

    [Test]
    public async Task WhenPlatformMessageRepeatsOnSeveralLinesThenOnlyFirstLineIsUsed()
    {
        var error = new COMException("Odd failure.\r\n\r\nOdd failure.\r\n", unchecked((int)0x80004005));

        var mapped = TransportErrors.FromPlatformError("Sending", error);

        await Assert.That(mapped.Message).IsEqualTo("Sending failed: Odd failure. (HRESULT 0x80004005).");
    }

    [Test]
    public async Task WhenMappedThenPlatformErrorIsKeptAsInnerException()
    {
        var error = new UnauthorizedAccessException("denied");

        var mapped = TransportErrors.FromPlatformError("Connecting", error);

        await Assert.That(mapped.InnerException).IsSameReferenceAs(error);
    }

    [Test]
    public async Task WhenOperationIsBlankThenArgumentExceptionIsThrown()
    {
        var error = new COMException("x", unchecked((int)0x80004005));

        var act = () => TransportErrors.FromPlatformError(" ", error);

        await Assert.That(act).Throws<ArgumentException>();
    }
}
