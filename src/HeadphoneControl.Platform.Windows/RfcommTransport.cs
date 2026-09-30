using System.Buffers;
using System.Runtime.InteropServices.WindowsRuntime;
using HeadphoneControl.Core;
using HeadphoneControl.Protocol.Devices;
using HeadphoneControl.Protocol.Transport;
using Microsoft.Extensions.Logging;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.Rfcomm;
using Windows.Devices.Enumeration;
using Windows.Networking.Sockets;
using Windows.Storage.Streams;

namespace HeadphoneControl.Platform.Windows;

// Prefers the V2 service and only falls back to V1 when V2 is absent; the generation actually connected is reported
// in DetectedGeneration so the caller can refuse to send V2 opcodes to a V1 device.
internal sealed class RfcommTransport : ITransport
{
    private readonly string _deviceId;
    private readonly ILogger<RfcommTransport> _logger;
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();

    private BluetoothDevice? _device;
    private RfcommDeviceService? _service;
    private StreamSocket? _socket;
    private string _deviceName;
    private volatile bool _isConnected;
    private bool _connectStarted;
    private volatile bool _disposed;

    internal RfcommTransport(string deviceId, string deviceName, ILogger<RfcommTransport> logger)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceId);
        ArgumentNullException.ThrowIfNull(deviceName);
        ArgumentNullException.ThrowIfNull(logger);

        _deviceId = deviceId;
        _deviceName = deviceName;
        _logger = logger;
    }

    public bool IsConnected => _isConnected;

    // Generation of the service UUID actually connected to; Unknown until ConnectAsync succeeds, so a failed or
    // pending connect can never pass for V2.
    public ProtocolGeneration DetectedGeneration { get; private set; } = ProtocolGeneration.Unknown;

    // A transport connects once; create a new one to reconnect.
    public async Task ConnectAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_connectStarted)
        {
            throw new InvalidOperationException("This transport was already connected once; create a new transport to reconnect.");
        }

        _connectStarted = true;
        _logger.LogInformation("Connecting to {DeviceName} ({DeviceId})", _deviceName, _deviceId);

        // Dispose cancels _lifetime so a connect in flight stops instead of opening a channel nobody will close.
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        var token = linked.Token;
        var connected = false;
        try
        {
            var device = await BluetoothDevice.FromIdAsync(_deviceId).AsTask(token).ConfigureAwait(false)
                ?? throw new TransportException(
                    $"Bluetooth device {_deviceName} was not found; make sure it is still paired in Windows Bluetooth settings.");
            _device = device;
            ThrowIfDisposedDuringConnect();
            _deviceName = device.Name;

            var (service, generation) = await ResolveServiceAsync(device, token).ConfigureAwait(false);
            _service = service;
            ThrowIfDisposedDuringConnect();

            await RequestAccessAsync(service, token).ConfigureAwait(false);
            ThrowIfDisposedDuringConnect();

            _logger.LogInformation(
                "Opening RFCOMM socket to {DeviceName}: host {HostName}, service {ServiceName}",
                _deviceName,
                service.ConnectionHostName.RawName,
                service.ConnectionServiceName);

            var socket = new StreamSocket();
            _socket = socket;
            ThrowIfDisposedDuringConnect();
            await socket
                .ConnectAsync(
                    service.ConnectionHostName,
                    service.ConnectionServiceName,
                    SocketProtectionLevel.BluetoothEncryptionAllowNullAuthentication)
                .AsTask(token)
                .ConfigureAwait(false);

            DetectedGeneration = generation;
            _isConnected = true;
            ThrowIfDisposedDuringConnect();
            connected = true;
        }
        catch (Exception ex) when (_disposed && ex is not ObjectDisposedException)
        {
            _logger.LogDebug(ex, "Connecting to {DeviceName} stopped because the transport was disposed", _deviceName);
            throw new ObjectDisposedException(nameof(RfcommTransport), ex);
        }
        catch (Exception ex) when (TransportErrors.IsPlatformError(ex))
        {
            var error = TransportErrors.FromPlatformError($"Connecting to {_deviceName}", ex);
            _logger.LogError(ex, "{Message}", error.Message);
            throw error;
        }
        catch (Exception ex) when (ex is TransportException or OperationCanceledException)
        {
            _logger.LogWarning("Connecting to {DeviceName} stopped: {Reason}", _deviceName, ex.Message);
            throw;
        }
        finally
        {
            if (!connected)
            {
                DetectedGeneration = ProtocolGeneration.Unknown;
                ReleaseResources();
            }
        }

        _logger.LogInformation(
            "Connected to {DeviceName} over the Sony {Generation} RFCOMM service",
            _deviceName,
            DetectedGeneration);
    }

    public async Task SendAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken)
    {
        var socket = GetConnectedSocket();
        if (data.IsEmpty)
        {
            return;
        }

        await _sendLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var bytes = data.ToArray();
            var offset = 0;
            while (offset < bytes.Length)
            {
                var written = await socket.OutputStream
                    .WriteAsync(bytes.AsBuffer(offset, bytes.Length - offset))
                    .AsTask(cancellationToken)
                    .ConfigureAwait(false);
                if (written == 0)
                {
                    throw new TransportException($"Sending to {_deviceName} failed: the socket accepted no bytes.");
                }

                offset += (int)written;
            }

            _logger.LogTrace("Sent {Count} bytes: {Hex}", data.Length, Convert.ToHexString(data.Span));
        }
        catch (Exception ex) when (TransportErrors.IsPlatformError(ex))
        {
            throw Fail($"Sending to {_deviceName}", ex);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    // Cancelling a pending read cancels the underlying WinRT operation; the socket is not guaranteed to be usable
    // afterwards, so callers should dispose the transport after cancelling a receive.
    public async Task<int> ReceiveAsync(Memory<byte> buffer, CancellationToken cancellationToken)
    {
        var socket = GetConnectedSocket();
        if (buffer.IsEmpty)
        {
            throw new ArgumentException("The receive buffer must not be empty.", nameof(buffer));
        }

        var scratch = ArrayPool<byte>.Shared.Rent(buffer.Length);
        try
        {
            var target = scratch.AsBuffer(0, 0, buffer.Length);
            var result = await socket.InputStream
                .ReadAsync(target, (uint)buffer.Length, InputStreamOptions.Partial)
                .AsTask(cancellationToken)
                .ConfigureAwait(false);

            var count = (int)result.Length;
            if (count == 0)
            {
                _isConnected = false;
                _logger.LogInformation("{DeviceName} closed the RFCOMM link", _deviceName);
                return 0;
            }

            // WinRT may hand back a different buffer than the one passed in, so copy from the result.
            result.CopyTo(0, scratch, 0, count);
            scratch.AsSpan(0, count).CopyTo(buffer.Span);
            _logger.LogTrace("Received {Count} bytes: {Hex}", count, Convert.ToHexString(scratch, 0, count));
            return count;
        }
        catch (Exception ex) when (TransportErrors.IsPlatformError(ex))
        {
            throw Fail($"Receiving from {_deviceName}", ex);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(scratch);
        }
    }

    public ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return ValueTask.CompletedTask;
        }

        _disposed = true;
        var wasConnected = _isConnected;
        _lifetime.Cancel();
        ReleaseResources();

        // _sendLock and _lifetime are deliberately not disposed: a send or connect may still be in flight and must
        // fail as a TransportException/ObjectDisposedException, not trip over a disposed primitive. Neither holds
        // a kernel handle unless AvailableWaitHandle/WaitHandle is touched, which this class never does.
        if (wasConnected)
        {
            _logger.LogInformation("Disconnected from {DeviceName}", _deviceName);
        }

        return ValueTask.CompletedTask;
    }

    private async Task<(RfcommDeviceService Service, ProtocolGeneration Generation)> ResolveServiceAsync(
        BluetoothDevice device,
        CancellationToken cancellationToken)
    {
        var v2 = await FindServiceAsync(device, SonyServiceIds.V2, cancellationToken).ConfigureAwait(false);
        if (v2.Service is not null)
        {
            return (v2.Service, ProtocolGeneration.V2);
        }

        var v1 = await FindServiceAsync(device, SonyServiceIds.V1, cancellationToken).ConfigureAwait(false);
        if (v1.Service is not null)
        {
            _logger.LogWarning(
                "{DeviceName} does not advertise the V2 service but advertises V1; V2 commands must not be sent to it",
                device.Name);
            return (v1.Service, ProtocolGeneration.V1);
        }

        var lastError = v1.Error != BluetoothError.Success ? v1.Error : v2.Error;
        throw new TransportException(lastError == BluetoothError.Success
            ? $"{device.Name} does not advertise a Sony control service (V2 {SonyServiceIds.V2} or V1 {SonyServiceIds.V1})."
            : $"Looking up the Sony control service on {device.Name} failed: {DescribeBluetoothError(lastError)}.");
    }

    private async Task<(RfcommDeviceService? Service, BluetoothError Error)> FindServiceAsync(
        BluetoothDevice device,
        Guid serviceUuid,
        CancellationToken cancellationToken)
    {
        var serviceId = RfcommServiceId.FromUuid(serviceUuid);
        var error = BluetoothError.Success;

        // Uncached queries SDP on the live device; the cached lookup lets a device that is momentarily
        // unreachable still produce a meaningful socket error instead of "service not found".
        foreach (var cacheMode in new[] { BluetoothCacheMode.Uncached, BluetoothCacheMode.Cached })
        {
            var result = await device.GetRfcommServicesForIdAsync(serviceId, cacheMode)
                .AsTask(cancellationToken)
                .ConfigureAwait(false);

            _logger.LogDebug(
                "{CacheMode} lookup of service {ServiceUuid} on {DeviceName}: {Error}, {Count} service(s)",
                cacheMode,
                serviceUuid,
                device.Name,
                result.Error,
                result.Services.Count);

            if (result.Error == BluetoothError.Success && result.Services.Count > 0)
            {
                for (var i = 1; i < result.Services.Count; i++)
                {
                    result.Services[i].Dispose();
                }

                return (result.Services[0], BluetoothError.Success);
            }

            if (result.Error == BluetoothError.Success)
            {
                // The live device answered and does not have this service; the cache cannot know better.
                return (null, BluetoothError.Success);
            }

            error = result.Error;
        }

        return (null, error);
    }

    private async Task RequestAccessAsync(RfcommDeviceService service, CancellationToken cancellationToken)
    {
        var status = await service.RequestAccessAsync().AsTask(cancellationToken).ConfigureAwait(false);
        if (status == DeviceAccessStatus.Allowed)
        {
            return;
        }

        throw new TransportException(status switch
        {
            DeviceAccessStatus.DeniedByUser =>
                $"Access to {_deviceName} was denied by the user; allow it in Settings > Privacy & security.",
            DeviceAccessStatus.DeniedBySystem =>
                $"Access to {_deviceName} was denied by Windows (policy or privacy settings).",
            _ => $"Access to {_deviceName} was not granted ({status}).",
        });
    }

    private static string DescribeBluetoothError(BluetoothError error) => error switch
    {
        BluetoothError.RadioNotAvailable => "the Bluetooth radio is off or missing",
        BluetoothError.DeviceNotConnected => "the headset is not in range or is switched off",
        BluetoothError.ResourceInUse => "the service is in use by another application",
        BluetoothError.DisabledByPolicy => "Bluetooth is disabled by policy",
        BluetoothError.DisabledByUser => "Bluetooth access is disabled in Windows privacy settings",
        BluetoothError.ConsentRequired => "the user has not granted consent to use the device",
        BluetoothError.NotSupported or BluetoothError.TransportNotSupported => "the operation is not supported by this adapter",
        _ => $"Bluetooth error {error}",
    };

    // Send/receive after dispose report "link down" per the ITransport contract, so a reader loop racing
    // shutdown only ever has to handle TransportException.
    private StreamSocket GetConnectedSocket()
    {
        var socket = _socket;
        return !_disposed && _isConnected && socket is not null
            ? socket
            : throw new TransportException(_disposed
                ? $"The link to {_deviceName} was closed because the transport was disposed."
                : $"The link to {_deviceName} is not connected.");
    }

    private TransportException Fail(string operation, Exception error)
    {
        _isConnected = false;
        if (_disposed)
        {
            // Pending I/O failing because Dispose closed the socket is a normal shutdown, not an error.
            _logger.LogDebug(error, "{Operation} ended because the transport was disposed", operation);
            return new TransportException($"{operation} failed: the Bluetooth link was closed.", error);
        }

        var transportError = TransportErrors.FromPlatformError(operation, error);
        _logger.LogError(error, "{Message}", transportError.Message);
        return transportError;
    }

    private void ThrowIfDisposedDuringConnect() => ObjectDisposedException.ThrowIf(_disposed, this);

    // Dispose and a failing connect may both release; Exchange makes sure each object is disposed once.
    private void ReleaseResources()
    {
        _isConnected = false;
        Interlocked.Exchange(ref _socket, null)?.Dispose();
        Interlocked.Exchange(ref _service, null)?.Dispose();
        Interlocked.Exchange(ref _device, null)?.Dispose();
    }
}
