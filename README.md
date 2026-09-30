# Headphone Control

A Windows 11 desktop app that controls a Sony **WH-CH720N** directly over Bluetooth, without the Sony phone app.

It is built with C# / .NET 10, Avalonia UI and CommunityToolkit.Mvvm.

## Features

- **Status:** battery level and charging, firmware version, active codec.
- **Noise control:** Noise cancelling, Ambient sound or Off.
  - Ambient level 1–20.
  - Focus on voice.
  - The ambient controls are greyed out outside Ambient mode.
- **Equalizer:**
  - The 9 presets and a custom curve (Clear Bass plus 5 bands, −10..+10).
  - After a preset change, the app reads back the band curve the headset chose.
- **DSEE** upscaling on/off.
- **Live updates:** changes made with the headset's own buttons show up in the app immediately.
- **Diagnostics:** a panel in the app, also written to `headphone-control.log` beside the executable.

## Requirements

- Windows 11 and the .NET 10 SDK.
- A WH-CH720N **paired** in Windows Bluetooth settings.
- Only one app can hold the headset's control channel at a time. Close the Sony app on your phone if connecting fails.

## Run

```powershell
dotnet run --project src/HeadphoneControl                 # connect to the paired headset
dotnet run --project src/HeadphoneControl -- --simulated  # simulated headset, no hardware needed
dotnet run --project src/HeadphoneControl -- --verbose    # also log every protocol frame
```

| Switch | Effect |
|---|---|
| `--simulated` | Uses an in-memory simulated headset. |
| `--simulated-connect-failure` | The simulated headset fails its first connect. |
| `--verbose` | Debug logging, including raw protocol frames. Off by default, because the headset streams now-playing track titles over the same channel. |

## How it works

- **Transport:** the headset's control channel is a Bluetooth Classic **RFCOMM** byte stream (Sony service UUID `956C7B26-D49A-4BA8-B03F-B17D393CB6E2`), opened through the WinRT `Windows.Devices.Bluetooth.Rfcomm` API.
- **Protocol:** on top of the byte stream the app speaks Sony's framed V2 protocol: escaped frames with a checksum, 1-bit ACK/sequence, and request/response plus notifications.
- **Safety:** commands are sent only after both the RFCOMM service and the init handshake confirm a V2 device. Opcode `0x22` reads the battery on V2 but **powers off** V1 devices.

## Project layout

```
src/
  HeadphoneControl/                    Avalonia app (net10.0-windows): views, view models, simulator, composition root
  HeadphoneControl.Protocol/           platform-neutral (net10.0): framing, session, V2 commands, HeadphoneDevice
  HeadphoneControl.Core/               platform-neutral (net10.0): IHeadsetConnector seam, headset selection
  HeadphoneControl.Platform.Windows/   WinRT RFCOMM discovery, transport and connector
tests/
  HeadphoneControl.Protocol.Tests/          protocol, session and device tests (FakeTransport / FakeHeadset)
  HeadphoneControl.Core.Tests/              headset selection and service ids
  HeadphoneControl.Platform.Windows.Tests/  RFCOMM transport and error mapping; [Explicit] RFCOMM hardware tests
  HeadphoneControl.Tests/                   view model, simulator, diagnostics; [Explicit] end-to-end hardware test
docs/
  development-status.md        status, verified hardware facts, next steps
  backlog.md                   future improvements
  hardware-smoke-test.md       manual checklist for the real headset
```

## Tests

```powershell
dotnet test --project tests/HeadphoneControl.Protocol.Tests
dotnet test --project tests/HeadphoneControl.Core.Tests
dotnet test --project tests/HeadphoneControl.Platform.Windows.Tests
dotnet test --project tests/HeadphoneControl.Tests
```

The tests use TUnit with the Microsoft.Testing.Platform runner, which `global.json` configures. They need no hardware.

The `[Explicit]` hardware tests need the paired headset and only read from it. Run them one at a time:

```powershell
dotnet test --project tests/HeadphoneControl.Tests -- --treenode-filter "/*/*/HeadphoneDeviceHardwareTests/*"
```

## Known limitations

- **No auto power-off setting.** It was removed because the WH-CH720N (firmware 1.1.4) never answers the query.
- **Single device:** the app connects to the first paired Sony headset. There is no device picker yet.
- **Only AAC verified:** codec detection has been checked with AAC only.
