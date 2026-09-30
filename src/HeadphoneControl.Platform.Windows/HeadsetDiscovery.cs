using HeadphoneControl.Core;
using HeadphoneControl.Protocol.Transport;
using Microsoft.Extensions.Logging;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.Rfcomm;
using Windows.Devices.Enumeration;

namespace HeadphoneControl.Platform.Windows;

/// <summary>Finds paired Bluetooth devices that advertise a Sony control service.</summary>
internal sealed class HeadsetDiscovery
{
    private readonly ILogger<HeadsetDiscovery> _logger;

    public HeadsetDiscovery(ILogger<HeadsetDiscovery> logger)
    {
        ArgumentNullException.ThrowIfNull(logger);
        _logger = logger;
    }

    /// <summary>
    /// Lists paired devices exposing the Sony V2 or V1 RFCOMM service, WH-CH720N first. Uses the SDP cache, so
    /// headsets that are paired but currently off are listed too.
    /// </summary>
    /// <exception cref="TransportException">Windows could not enumerate paired Bluetooth devices.</exception>
    public async Task<IReadOnlyList<DiscoveredHeadset>> FindPairedHeadsetsAsync(CancellationToken cancellationToken)
    {
        DeviceInformationCollection paired;
        try
        {
            paired = await DeviceInformation
                .FindAllAsync(BluetoothDevice.GetDeviceSelectorFromPairingState(true))
                .AsTask(cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (TransportErrors.IsPlatformError(ex))
        {
            var error = TransportErrors.FromPlatformError("Listing paired Bluetooth devices", ex);
            _logger.LogError(ex, "{Message}", error.Message);
            throw error;
        }

        _logger.LogInformation("Found {Count} paired Bluetooth device(s)", paired.Count);

        var headsets = new List<DiscoveredHeadset>();
        foreach (var info in paired)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var headset = await ProbeAsync(info, cancellationToken).ConfigureAwait(false);
            if (headset is not null)
            {
                headsets.Add(headset);
            }
        }

        var ordered = HeadsetSelection.OrderForDisplay(headsets);
        _logger.LogInformation(
            "Sony headsets: {Headsets}",
            ordered.Count == 0 ? "none" : string.Join(", ", ordered.Select(h => $"{h.Name} ({h.Generation})")));
        return ordered;
    }

    private async Task<DiscoveredHeadset?> ProbeAsync(DeviceInformation info, CancellationToken cancellationToken)
    {
        try
        {
            using var device = await BluetoothDevice.FromIdAsync(info.Id).AsTask(cancellationToken).ConfigureAwait(false);
            if (device is null)
            {
                _logger.LogDebug("Skipping {Name}: not a classic Bluetooth device", info.Name);
                return null;
            }

            foreach (var serviceUuid in new[] { SonyServiceIds.V2, SonyServiceIds.V1 })
            {
                var result = await device
                    .GetRfcommServicesForIdAsync(RfcommServiceId.FromUuid(serviceUuid), BluetoothCacheMode.Cached)
                    .AsTask(cancellationToken)
                    .ConfigureAwait(false);
                var found = result.Error == BluetoothError.Success && result.Services.Count > 0;
                foreach (var service in result.Services)
                {
                    service.Dispose();
                }

                if (found)
                {
                    var generation = SonyServiceIds.GetGeneration(serviceUuid);
                    _logger.LogInformation(
                        "{Name} ({DeviceId}) advertises the Sony {Generation} service",
                        device.Name,
                        device.DeviceId,
                        generation);
                    return new DiscoveredHeadset(device.Name, device.DeviceId, generation);
                }

                if (result.Error != BluetoothError.Success)
                {
                    _logger.LogDebug("Service lookup on {Name} returned {Error}", device.Name, result.Error);
                }
            }

            _logger.LogDebug("Skipping {Name}: no Sony control service", device.Name);
            return null;
        }
        catch (Exception ex) when (TransportErrors.IsPlatformError(ex))
        {
            // One misbehaving paired device must not hide the others; the failure is logged, not swallowed.
            _logger.LogWarning(ex, "Skipping {Name}: probing its services failed", info.Name);
            return null;
        }
    }
}
