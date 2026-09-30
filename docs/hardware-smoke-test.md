# Hardware smoke test: RFCOMM transport (WH-CH720N)

A manual check of `src/HeadphoneControl.Platform.Windows` against a real headset. The probe sends only the read-only V2
init handshake. It never sends battery (`0x22`) or SET commands.

## 1. Prepare

1. Pair the WH-CH720N in **Settings > Bluetooth & devices** and make sure it shows as *Connected* (or at least
   *Paired* and switched on, within range).
2. Close the Sony | Headphones Connect app on your phone, or turn off Bluetooth on the phone. The headset allows only
   one host to use its control channel.
3. Close any other instance of Headphone Control or other Sony tools on this PC.
4. Optional: `Get-PnpDevice | Where-Object FriendlyName -like '*WH-CH720N*'` should list a
   `BTHENUM\{956C7B26-D49A-4BA8-B03F-B17D393CB6E2}...` node (the Sony V2 service, shown as an SPP serial port).

## 2. Run the explicit hardware tests

```powershell
dotnet test --project tests/HeadphoneControl.Platform.Windows.Tests -- --treenode-filter "/*/*/RfcommHardwareTests/*" --output Detailed
```

Expected: 3 tests pass.

| Test | Proves |
| --- | --- |
| `WhenPairedWhCh720nThenV2ServiceIsFound` | Discovery lists the headset with `Generation = V2` |
| `WhenConnectingToPairedWhCh720nThenConnectionReportsV2Service` | The RFCOMM socket opens on the V2 service UUID |
| `WhenV2InitHandshakeIsSentThenHeadsetReplies` | Bytes flow both ways |

The handshake sends `3E0C000000000200000E3C`. A reply like the following (captured 2026-09-27) is good:

```
3E 01 01 00000000 02 3C                                  ACK, seq 1
3E 0C 00 00000008 01 00 03 00 10 02 00 00 2A 3C          DATA_MDR seq 0, payload 01 00 03 00 10 02 00 00
(the DATA_MDR frame repeats because the probe does not ACK it)
```

## 3. Run the app

`dotnet run --project src/HeadphoneControl`, then check `headphone-control.log` beside the executable for:

- `Found N paired Bluetooth device(s)`
- `WH-CH720N (Bluetooth#Bluetooth...) advertises the Sony V2 service`
- `Opening RFCOMM socket to WH-CH720N: host (..), service {956c7b26-...}`
- `Connected to WH-CH720N over the Sony V2 RFCOMM service`

A warning `... does not advertise the V2 service but advertises V1` means that V2 commands must not be sent. Stop and
report it.

## 4. Common failures

| Message contains | Likely cause / fix |
| --- | --- |
| `Sony headsets: none` | The headset is not paired, or pairing is stale. Remove it and pair it again. |
| `not in range or is switched off` | Turn the headset on and bring it closer. |
| `did not respond` / `refused the connection` | The phone app or another device holds the control channel. Disconnect the phone. |
| `already open by another connection on this PC` | Another test, app instance or Sony tool is connected. Close it and retry. |
| `access denied` / `denied by the user` | Check **Settings > Privacy & security > Other devices** (and *Bluetooth* app access), then re-run. |
| `Bluetooth radio is off` | Turn Bluetooth on. |
| `re-pair the headset` | The SDP record is missing. Remove the device in Windows and pair it again. |
