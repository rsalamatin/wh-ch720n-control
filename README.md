# Headphone Control

A Windows 11 desktop app that controls a Sony **WH-CH720N** directly over Bluetooth, without the Sony phone app.

It is built with C# / .NET 10, Avalonia UI and CommunityToolkit.Mvvm.

## Features

- **Connection:** press **Connect** to open the control channel. The `⋯` menu holds Refresh now, Cancel operation, Disconnect, the firmware version and codec, and Diagnostics.
- **Status:** battery level and charging in the header; firmware version and active codec in the `⋯` menu.
- **Noise control:** Noise cancelling, Ambient sound or Off.
  - Ambient level 1–20.
  - Focus on voice.
  - The ambient options appear only in Ambient mode.
- **Equalizer:**
  - The 9 presets and a custom curve (Clear Bass plus 5 bands, −10..+10), and a reset to flat.
  - After a preset change, the app reads back the band curve the headset chose.
- **DSEE** upscaling on/off.
- **Compact layout:** the window fits its content, each section collapses to a one-line summary, and only failures and warnings are shown, in an info bar.
- **Smooth editing:** slider and band drags are debounced, and an edit replaced by a newer one is never sent.
- **Live updates:** changes made with the headset's own buttons show up in the app immediately.
- **Tray:** minimizing hides the window to a tray icon. Click it, or choose Open, to bring the window back; Exit quits. The tooltip shows the connection and battery level. While connected, the icon also carries a battery gauge, which turns red at 20 % or less.
- **Low-battery notification:** a Windows toast appears when the battery reaches 20 % or less and the headset isn't charging. It is shown once per discharge: again only after charging, a level of 25 % or more, or a reconnect. The level only updates when the headset reports it or on Refresh, because there is no periodic refresh yet.
- **Firmware update check:** the app compares the firmware the headset reports with the newest version listed in `firmware.json` in this repository. The `⋯` menu shows the result next to the firmware version (`up to date` or `1.2.0 available`), and a toast announces each new version once. The check runs at most once a day while a headset is connected; **Firmware updates** in the `⋯` menu has Check now, a link to Sony's download page, and a switch for the automatic check. This is the only network request the app makes: a plain download of that file, with no data about you or the headset. The app never installs firmware; use the Sony | Sound Connect app for that. The file is maintained by hand, so a new Sony release shows up only after `firmware.json` is updated.
- **Theme:** `⋯` → Theme switches between the Windows setting, Light and Dark. The choice is saved in `%APPDATA%\HeadphoneControl\settings.json`.
- **Diagnostics:** a separate window (`⋯` → Diagnostics…) with Copy all, Clear and Open log folder. The log is also written to `headphone-control.log` beside the executable. The file is capped at 10 MB; when it is full it is cleared and logging starts over in the same file.

## Requirements

- Windows 11 and the .NET 10 SDK.
- A WH-CH720N **paired** in Windows Bluetooth settings.
- Only one app can hold the headset's control channel at a time. Close the Sony app on your phone if connecting fails.

## Build and run

```powershell
dotnet build HeadphoneControl.sln                         # output goes to artifacts/
dotnet run --project src/HeadphoneControl                 # connect to the paired headset
dotnet run --project src/HeadphoneControl -- --simulated  # simulated headset, no hardware needed
dotnet run --project src/HeadphoneControl -- --verbose    # also log every protocol frame
```

| Switch | Effect |
|---|---|
| `--simulated` | Uses an in-memory WH-CH720N that speaks the real frame protocol, so framing, the session, the handshake and the V2 check all run as they do against the headset. |
| `--simulated-connect-failure` | The simulated headset fails its first connect. |
| `--simulated-battery=<0..100>` | The simulated headset reports this battery level (default 80). Use 20 or less to see the low-battery notification. |
| `--firmware-manifest=<url>` | Reads the firmware manifest from this http(s) URL instead of the repository's `firmware.json`. |
| `--verbose` | Debug logging, including raw protocol frames. Off by default, because the headset streams now-playing track titles over the same channel. |

## How it works

- **Transport:** the headset's control channel is a Bluetooth Classic **RFCOMM** byte stream (Sony service UUID `956C7B26-D49A-4BA8-B03F-B17D393CB6E2`), opened through the WinRT `Windows.Devices.Bluetooth.Rfcomm` API.
- **Protocol:** on top of the byte stream the app speaks Sony's framed V2 protocol: escaped frames with a checksum, 1-bit ACK/sequence, and request/response plus notifications.
- **Safety:** commands are sent only after both the RFCOMM service and the init handshake confirm a V2 device. Opcode `0x22` reads the battery on V2 but **powers off** V1 devices.
- **Structure:**
  - `HeadsetController` (in Core) owns the connection lifecycle as a single actor. It serializes every operation, and a newer value read from the headset is never overwritten by an older one.
  - The platform plugs in below it through `IHeadsetConnector` (list paired headsets, connect to one). Windows is the only backend; the simulator is a second one.
  - `App.CreateDevice` is the only place that picks the backend.

## Project layout

```
src/
  HeadphoneControl/                    Avalonia app (net10.0-windows): views, view models, simulator, composition root
  HeadphoneControl.Core/               platform-neutral (net10.0): HeadsetController, IHeadsetConnector seam, headset selection;
                                       Protocol/: framing, session, V2 commands, SonyV2Connection
  HeadphoneControl.Platform.Windows/   WinRT RFCOMM discovery, transport and connector; toast notifications
  HeadphoneControl.FirmwareUpdates/    platform-neutral (net10.0): looks up the newest firmware in firmware.json and compares versions
tests/
  HeadphoneControl.Testing/                 shared fakes: FakeTransport, FakeHeadset (class library, not a test project)
  HeadphoneControl.Core.Tests/              HeadsetController, headset selection, service ids; Protocol/: protocol, session and connection tests
  HeadphoneControl.Platform.Windows.Tests/  RFCOMM transport and error mapping; [Explicit] RFCOMM hardware tests
  HeadphoneControl.FirmwareUpdates.Tests/   manifest download and version comparison, against a stub HTTP handler
  HeadphoneControl.Tests/                   view model, simulator, diagnostics; [Explicit] end-to-end hardware test
firmware.json                  newest known firmware per headset model, read by the app's update check
docs/
  development-status.md        status, verified hardware facts, next steps
  backlog.md                   future improvements
  hardware-smoke-test.md       manual checklist for the real headset
```

## Tests

```powershell
dotnet test --project tests/HeadphoneControl.Core.Tests
dotnet test --project tests/HeadphoneControl.Platform.Windows.Tests
dotnet test --project tests/HeadphoneControl.FirmwareUpdates.Tests
dotnet test --project tests/HeadphoneControl.Tests
```

The tests use TUnit with the Microsoft.Testing.Platform runner, which `global.json` configures. They need no hardware.

The `[Explicit]` hardware tests need the paired headset and only read from it. Run them one at a time:

```powershell
dotnet test --project tests/HeadphoneControl.Platform.Windows.Tests -- --treenode-filter "/*/*/RfcommHardwareTests/*"
dotnet test --project tests/HeadphoneControl.Tests -- --treenode-filter "/*/*/HeadphoneDeviceHardwareTests/*"
```

`docs/hardware-smoke-test.md` walks through these tests, a manual app run and common connection failures.

## Known limitations

- **No auto power-off setting.** It was removed because the WH-CH720N (firmware 1.1.4) never answers the query.
- **Windows only.** The platform seam exists, but there is no Linux backend.
- **No auto-connect.** Connect is manual on every start. Only the theme is saved, and closing the window quits instead of hiding to the tray; see `docs/backlog.md`.
- **Single device:** the app connects to the first paired Sony headset in this order: a WH-CH720N first, then V2 devices before V1, then by name. There is no device picker yet.
- **Only AAC verified:** codec detection has been checked with AAC only.
