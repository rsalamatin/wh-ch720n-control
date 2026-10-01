# Development status

Last updated: 2026-10-01. The solution builds with 0 warnings. 530 non-Explicit tests pass: 301 in `HeadphoneControl.Protocol.Tests`, 86 in `HeadphoneControl.Core.Tests`, 22 in `HeadphoneControl.Platform.Windows.Tests` and 121 in `HeadphoneControl.Tests`.

## How the work was organised

The work was split into phases so that parallel agents could work at the same time:

- **Phase 0** set up the solution scaffold and froze the contracts.
- **Phase 1** ran four tracks in parallel.
- **Phase 2** integrates the tracks, one step after another.

A separate review agent reviewed each track, and every required fix has been applied. Comments follow `.claude/skills/code-comments/SKILL.md`.

Git is not installed, so the agents could not use worktrees. Instead, each agent only edited its own folders and built with `-p:Lane=<name>`, which puts its output in `artifacts/lanes/<name>`.

## Done

| Area | Location | Notes |
|---|---|---|
| Contracts | `src/HeadphoneControl.Protocol/{Transport,Devices}` | `ITransport`, `TransportConnection`, `DeviceState`, and `IHeadphoneDevice` (which documents the exact exception set it may throw). |
| Framing | `Protocol/Framing` | Escaping, checksum, the 2048-byte cap, and resync after bad frames. |
| Session | `Protocol/Session/ProtocolSession.cs` | 1-bit seq/ACK; every frame type is ACKed; duplicates are dropped; replies are matched by opcode and subtype; no stale-reply buffer; notifications and the `Disconnected` event are raised on a dispatch task; one timeout covers the write and the wait. |
| V2 commands | `Protocol/Commands` | Builders and parsers for battery, NC/ambient, EQ, DSEE, firmware and codec, plus applying notifications. Auto power-off was removed on 2026-09-28 (see below). **Safety:** `V2CommandSet.FromHandshake(transportGeneration, initReply)` is the only way to get the command set, and it requires both the RFCOMM service and the init reply to say V2 (reply of 8 bytes with byte 2 equal to 0x03). |
| RFCOMM transport | `src/HeadphoneControl.Platform.Windows` (seam and selection rule in `src/HeadphoneControl.Core`) | `RfcommConnector` (the Windows `IHeadsetConnector`), `HeadsetDiscovery` (paired devices from the SDP cache), `RfcommTransport` (reports `DetectedGeneration`), error mapping. **Verified on the real WH-CH720N:** discovery, connect, and the init handshake. |
| UI | `src/HeadphoneControl/{Views,ViewModels,Simulation,Diagnostics,Resources}` | MVVM bound to `IHeadphoneDevice`: binding, strings and status text only. Debounce, coalescing and the pending-edit rule live in `HeadsetController`. Compact flyout layout (redesigned 2026-09-29): the window follows its content height (`SizeToContent`), sections collapse to a one-line summary, ambient options appear only in Ambient mode, only failures and warnings show (in an info bar), and Refresh, device info and Diagnostics live in the `⋯` menu. The diagnostics journal opens in its own window and is written to `headphone-control.log` (capped at 1 MB, then rolled over to `headphone-control.log.1`; if the roll fails, for example because another process holds the file, logging continues and the file grows past the cap). `--simulated` and `--simulated-connect-failure` use `Simulation/SimulatedHeadsetConnector`, a byte-level headset behind the same `HeadsetController`. |
| Real device | `Core/HeadsetController.cs`, `Protocol/Devices/SonyV2Connection.cs` | `SonyV2Connection` is one confirmed V2 link; `HeadsetController` owns the connection lifecycle as a `Channel` actor (State pattern: `Disconnected`/`Connecting`/`Connected`/`Failed` records) and serializes every operation. Every received frame carries a receive ordinal, and a value is applied only if its frame is newer than the one that last set that setting, so a notification queued behind an operation cannot undo a newer SET. Setters are debounced on request (`EditPacing`) and coalesced per `SettingGroup`. Connects, sends the handshake, gates V2 on both signals, then refreshes state. A query that goes unanswered or comes back malformed leaves that setting null instead of failing the connection. Setters complete on the device's ACK. The EQ preset setter then re-reads the EQ, because the headset sends no EQ echo. Notifications are applied and link loss is handled. `App.CreateDevice` uses this device unless `--simulated` is passed; `--verbose` enables frame-level logging. Tests are in `tests/HeadphoneControl.Core.Tests` and `tests/HeadphoneControl.Protocol.Tests/Devices` (a `FakeHeadset` replays the real init reply). |

## Real-hardware facts (WH-CH720N)

- **Services:** the headset advertises only the V2 service `956C7B26-...`, as an "SPP Serial Port". The V1 UUID is not present.
- **Handshake:**
  - TX `3E0C000000000200000E3C`
  - RX `ACK(seq1)` followed by DATA_MDR seq0 with payload `01 00 03 00 10 02 00 00`.
- **Retransmission:** the headset retransmits any frame that is not ACKed.
- **Single connection:** only one RFCOMM connection to the control service can be open at a time. A second one fails with 0x80072740.
- **Full read through the device layer** on 2026-09-28, firmware 1.1.4. Every GET below is ACKed before its reply arrives:

  | Query | Reply payload | Decoded |
  |---|---|---|
  | `22 00` battery | `23 00 25 00` | 37 %, not charging |
  | `66 17` noise control | `67 17 01 01 01 00 08` | Ambient, focus on voice off, level 8 |
  | `56 00` EQ | `57 00 10 06 09 0A 0F 11 11 13` | Bright; clear bass −1; bands 0, 5, 7, 7, 9 |
  | `E6 01` DSEE | `E7 01 00` | off |
  | `04 02` firmware | `05 02 05 "1.1.4"` | 1.1.4 |
  | `12 02` codec | `13 02 02` | AAC |
  | `26 05` auto power-off | **no reply**, only an ACK | feature removed |

  The reference says auto power-off was "verified against WH-CH720N" (`Client/Constants.h:31`), but this firmware doesn't answer the query. Finding the right inquiry would need a packet capture from the Sony app; don't guess at opcodes on the device. **At the user's request, auto power-off was removed from every layer on 2026-09-28**: UI, view model, `IHeadphoneDevice`, `DeviceState`, `V2CommandSet` and the simulator. That also removed the 2 s query timeout from every connect. To restore it, start from `ProtocolV2.cpp:13-15, 246-271` in the reference.
- **Manual UI session on 2026-09-28:**
  - **Notification echoes:** after each noise-control SET the headset sends `69 17 01 …` about 0.5 s later. After each DSEE SET it sends `E9 01 xx`, which confirms the DSEE notify opcode.
  - **No EQ echo:** nothing arrives after an EQ preset SET, so the app re-reads the EQ itself.
  - **Unknown frames:** `E5 01 00` and `15 03 0x` also arrive after a DSEE change. They are ignored.
  - **Second session:**
    - Re-reading the EQ after a preset change works: Bright returns `57 00 10 06 09 0A 0F 11 11 13`, and Bass Boost returns `57 00 16 06 11 0A 0A 0A 0A 0A`.
    - Connect now reports success only after every setting has been read.
    - The headset streams now-playing metadata as `A5 01 …` and `A9 01 02 <len> <UTF-8 title> …`. The app ignores these frames, but at Debug level the raw bytes, **including track titles**, are written to the log. For that reason the default log level is now Information. Run with `--verbose` to get frame dumps when debugging the protocol.
  - **Third session** (all fixes applied): the user confirmed that every manual check passes, including Focus on Voice and the physical NC/Ambient button. The app connects in about 0.6 s after the handshake now that the auto power-off query is gone.
- **Read-only hardware test:** `HeadphoneDeviceHardwareTests` (`[Explicit]`) runs the whole app path and prints the log.

## Next steps

**Done on 2026-09-28:**
- The real device is wired in through `RfcommConnector` (now in `HeadphoneControl.Platform.Windows`) and `App.CreateDevice`.
- Two review rounds were completed and their fixes applied.
- Hardware sessions: connect, noise control, EQ and DSEE all work on the real headset. The DSEE notify opcode and the echo after each SET are confirmed.

**Remaining:**
0. ~~Architecture refactor~~: all steps done on 2026-10-01 (see `docs/architecture-refactor-plan.md`). Run the hardware smoke test (`docs/hardware-smoke-test.md`) once on the real headset to confirm.
1. On hardware, check the codec byte map for codecs other than AAC. Focus on Voice and the headset button were confirmed on 2026-09-28.
2. ~~Update `README.md`~~. Rewritten on 2026-09-28 (RFCOMM, switches, tests, layout, limitations).

**Review follow-ups (done 2026-09-28):**
- **EQ unknown:** after a failed re-read, `Equalizer` is set to null stamped with the ACK's receive ordinal, so a late `0x57`/`0x59` received after the ACK is kept and an older one is not. The UI then says "Preset applied, but the equalizer could not be read back. Press Refresh."
- **Sequence handling, a deliberate departure from the reference:** `ProtocolSession` no longer resyncs its outgoing sequence from ACKs. The reference does (`SonyProtocolSession.cpp:199`). Here the sequence simply alternates once each frame is on the wire, and only the ACK `1 - seq` of the frame in flight counts. A late ACK is ignored and logged at Debug. This is safe because the WH-CH720N ACKs seq n with 1−n, so resyncing from the matching ACK adds nothing, and resyncing from a late ACK caused sequence reuse (a silently dropped command). It was verified on hardware.
- **Link-drop test:** `WhenLinkDropsAfterTheLastConnectQueryThenStateSettlesNotConnected` is a smoke test. The race it guards is closed structurally: the actor handles the link-lost message only after the connect operation finishes. It still describes BLE GATT and needs to describe RFCOMM, `--simulated`, `--verbose`, and how to run the tests.
