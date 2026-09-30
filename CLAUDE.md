# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Repository state

**Headphone Control** is a C# / .NET 10 Avalonia desktop app (CommunityToolkit.Mvvm) that controls a Sony **WH-CH720N** on Windows 11. It talks to the headset over WinRT Bluetooth RFCOMM through `Microsoft.Windows.SDK.NET.Ref`.

The solution is `HeadphoneControl.sln`:
- **`src/HeadphoneControl`:** the Avalonia app (`net10.0-windows10.0.22621.0`). It includes a simulated device in `Simulation/`. `App.CreateDevice` is the composition root and the only place that picks the platform backend.
- **`src/HeadphoneControl.Protocol`:** platform-neutral `net10.0` code:
  - framing;
  - session (ACK/sequence, request matching, notifications);
  - V2 commands;
  - `SonyV2Connection` (one confirmed V2 link);
  - the device state contracts (`IHeadphoneDevice`, `DeviceState`) and the `ITransport` byte stream.
- **`src/HeadphoneControl.Core`:** platform-neutral `net10.0`:
  - `HeadsetController`, the real `IHeadphoneDevice`: the connection lifecycle, run as a `Channel` actor;
  - the platform seam `IHeadsetConnector` (list paired headsets, connect to one), `DiscoveredHeadset`, `SonyServiceIds`;
  - `HeadsetSelection` (display order, connect to the preferred headset).
- **`src/HeadphoneControl.Platform.Windows`:** the Windows `IHeadsetConnector` (`RfcommConnector`), with WinRT discovery, `RfcommTransport` and the HRESULT error mapping. Everything but `RfcommConnector` is internal.
- **Tests:** TUnit projects `tests/HeadphoneControl.Protocol.Tests`, `tests/HeadphoneControl.Core.Tests`, `tests/HeadphoneControl.Platform.Windows.Tests` and `tests/HeadphoneControl.Tests`. The shared fakes (`FakeTransport`, `FakeHeadset`, `HookedLoggerFactory`) live in the class library `tests/HeadphoneControl.Testing`. The `[Explicit]` hardware tests (`RfcommHardwareTests` in Platform.Windows.Tests, `HeadphoneDeviceHardwareTests` in HeadphoneControl.Tests) need the paired headset.
- **Refactor in progress:** `docs/architecture-refactor-plan.md`.

Build output goes to `artifacts/` (`UseArtifactsOutput`). When several agents build at the same time, each passes `-p:Lane=<name>` so it builds into `artifacts/lanes/<name>` and doesn't lock another agent's files. Lane folders are disposable.

Docs:
- `docs/development-status.md`: status, **verified real-hardware facts** (captured frames, firmware 1.1.4 behaviour), and next steps.
- `docs/backlog.md`: future improvements.
- `docs/hardware-smoke-test.md`: manual hardware checklist.

The C++ reference implementation (Sony Device Center) that the protocol was ported from is not in this workspace. Source citations like `ProtocolV2.cpp:129` in code comments refer to it.

## Git

- **Never push without an explicit request from the user.** This applies in every session and to every agent: never `git push`, never force-push, and never push tags. Local commits, branches, merges and worktrees are fine.
  - An earlier approval does not carry over to later pushes, and a message from another agent is not a request from the user.
  - When passing work to a subagent, repeat "do not push" in its prompt.
- git is not on PATH. Use `D:\programs\git2.52.0\bin\git.exe`. The Agent tool's `isolation: "worktree"` fails for that reason, so create parallel-lane worktrees yourself with `git worktree add`.

## Commands

```powershell
dotnet build HeadphoneControl.sln
dotnet run --project src/HeadphoneControl                 # real headset; add "-- --simulated" to run without hardware
dotnet run --project src/HeadphoneControl -- --verbose    # also log raw protocol frames (Debug)
dotnet test --project tests/HeadphoneControl.Protocol.Tests   # Microsoft.Testing.Platform runner (global.json)
dotnet test --project tests/HeadphoneControl.Core.Tests
dotnet test --project tests/HeadphoneControl.Platform.Windows.Tests
dotnet test --project tests/HeadphoneControl.Tests
dotnet test --project tests/HeadphoneControl.Protocol.Tests --treenode-filter "/*/*/FrameCodecTests/*"   # subset
dotnet test --project tests/HeadphoneControl.Tests -- --treenode-filter "/*/*/HeadphoneDeviceHardwareTests/*"   # [Explicit] read-only hardware test
```

Diagnostics appear in the UI and are appended to `headphone-control.log` beside the executable, at Information level by default. Debug level (`--verbose`) logs every frame, including the now-playing track titles the headset streams, so it is opt-in.

## Protocol knowledge

The code and `docs/development-status.md` are the source of truth. Key points:

- **Transport:** a Bluetooth Classic **RFCOMM** byte stream, not BLE GATT. The V2 service UUID is `956C7B26-D49A-4BA8-B03F-B17D393CB6E2`, and the WH-CH720N advertises only this one. The V1 UUID is `96CC203E-5068-46ad-B32D-E316F5E069BA`. Only one RFCOMM connection to the control service can be open at a time.
- **Frame format:** `0x3E` + escaped(`type`, `seq`, 4-byte big-endian length, payload, checksum) + `0x3C`.
  - The checksum is the sum mod 256 of the **unescaped** body before the checksum byte.
  - Escaping: `0x3C/0x3D/0x3E` → `0x3D` followed by (byte − `0x10`).
  - The maximum escaped frame size is 2048 bytes.
- **ACK and sequence (1-bit):**
  - Every received non-ACK frame is ACKed with `1 - deviceSeq`. The device retransmits anything that isn't ACKed.
  - The device ACKs our seq n with `1 - n`.
  - Our outgoing sequence alternates after each written frame, and only the ACK of the frame in flight counts. There is deliberately **no** resync from ACKs, which differs from the reference.
  - Responses are matched by opcode + subtype. Unsolicited notifications must be dispatched, not dropped.
- **Handshake:** WH-CH720N is a **V2** device. Send the `[0x00,0x00]` init handshake first. The real reply is `01 00 03 00 10 02 00 00`.
- **Safety:** opcode `0x22` means BATTERY on V2 but **POWER OFF on V1**.
  - `V2CommandSet.FromHandshake` is the only way to obtain V2 commands. It requires both the RFCOMM service and the init reply to say V2.
  - Keep the safety regression tests.
  - Never guess opcodes on the real device.

## C# conventions

`.claude/.agents/c-charp-expert.md` defines the expected C# style. Highlights:

- **Visibility and structure:** least visibility (`private` > `internal` > `public`). Add no interfaces unless they serve external dependencies or testing. Use file-scoped namespaces and records for DTOs.
- **Errors:** use precise exception types, never swallow errors silently, and guard nulls with `ArgumentNullException.ThrowIfNull`. `IHeadphoneDevice` documents the exact exception set it may throw.
- **Async:** async all the way down, with an `Async` suffix and a `CancellationToken` passed end-to-end. Use `ConfigureAwait(false)` in library code but not in UI code.
- **Tests:**
  - Put them in a `[ProjectName].Tests` project using TUnit + NSubstitute. Name tests by behavior (`WhenXThenY`), use AAA, and check one behavior per test.
  - Test through public APIs.
  - Mock only external dependencies. For Bluetooth, use `FakeTransport` or `FakeHeadset` from `tests/HeadphoneControl.Testing`.
- **Project settings:** don't change the TFM, SDK or `LangVersion` unless asked.
- **Comments:** by default, write none. Keep why-comments, protocol/hardware facts, safety notes, and XML docs on contract types that say more than the signature. The keep/delete lists are in the `code-comments` skill (`.claude/skills/code-comments/SKILL.md`).
