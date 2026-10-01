# Architecture refactor plan

Agreed on 2026-09-29. **All steps are done** (2026-10-01): 1 and 2a on 2026-09-30; 2b, 4 and 3 on 2026-10-01. Every step was reviewed and its fixes applied.

**What was done on 2026-10-01:**
- **Step 2b:** `HeadsetController` (Core) replaces `HeadphoneDevice`; `ITransport` is a connected stream.
  - **S1/S2 fix:** receive-order stamps. The session numbers every received frame (`ReceivedPayload.Ordinal`); replies and ACKs return it (`Received<T>`), and `HeadsetController.Claim` applies a value only if its frame is newer than the one that last set that setting. A notification queued behind an operation can no longer undo a newer SET or a preset re-read.
  - A throwing `StateChanged` handler is logged and never fails an operation. `EqualizerState` compares band values.
- **Step 4:** merged; `--simulated` runs the byte-level simulator behind `HeadsetController`.
- **Step 3:** `HeadsetController` owns debounce (`EditPacing`), coalescing per `SettingGroup` (a replaced edit completes as `EditOutcome.Superseded` and sends nothing) and `HasPendingEdit`, with `StateChanged` raised when a group's last pending edit ends. `MainViewModel` keeps binding, strings and status text. Picking Manual sends the curve on screen as a custom equalizer.
- The backlog candidates found in the reviews moved to `backlog.md` (section "Protocol session").
- **Still to do:** run `docs/hardware-smoke-test.md` once on the real headset.

**Goal:** make the app pluggable so another platform (Linux/BlueZ) *can* be added later, and move the headset control logic out of the UI into its own layer. **Linux itself is out of scope for now**: only the seam is built.

## Findings from the design review

- `HeadphoneControl.Protocol` is already platform-neutral (`net10.0`), and `HeadphoneDevice` only depends on a `Func<CancellationToken, Task<TransportConnection>>`.
- The Windows lock-in comes from `Bluetooth/` living inside the app project, which forces the app onto `net10.0-windows10.0.22621.0`.
- Platform-neutral facts and rules sit in the Windows folder: `SonyServiceIds`, `DiscoveredHeadset`, and `HeadsetDiscovery.OrderForDisplay`.
- `App.CreateDevice` builds `RfcommConnector` directly, so there is no platform seam.
- `RfcommConnector` silently picks the first headset (`FirstOrDefault()`), so listing headsets and connecting to one are mixed together.
- **`MainViewModel` is doing the controller's job.** It owns:
  - the single-operation gate (`_operationGate`), which duplicates `HeadphoneDevice._operationLock`;
  - debouncing, and dropping edits that a newer edit has replaced (`SettingGroup`);
  - the rule that a pending edit stops incoming device state from overwriting the UI;
  - sorting failures into the documented exception set.

  Several backlog items (tray menu, auto-reconnect, periodic refresh, write pacing) need the same logic. The tray menu will be a second UI consumer, so this logic can't live in one view model.
- `HeadphoneDevice` keeps four fields in sync by hand: `_session`, `_commands`, `_linkLost` and `_state.Connection`. That's why it needs two locks and careful race comments.

## Target layering

```
HeadphoneControl.Protocol          net10.0            framing, session, V2CommandSet (unchanged)
HeadphoneControl.Core              net10.0            IHeadsetConnector, DiscoveredHeadset, SonyServiceIds,
                                                      SonyV2Connection (one link), HeadsetController (lifecycle)
HeadphoneControl.Platform.Windows  net10.0-windows…   WinRT discovery, RfcommTransport, TransportErrors
HeadphoneControl.Platform.Linux    (later)            BlueZ: D-Bus Profile1 / AF_BLUETOOTH RFCOMM socket
HeadphoneControl (Avalonia)        see Step 1         view models bind to HeadsetController
```

### Platform seam

```csharp
public interface IHeadsetConnector
{
    Task<IReadOnlyList<DiscoveredHeadset>> FindPairedAsync(CancellationToken ct);
    Task<TransportConnection> ConnectAsync(DiscoveredHeadset headset, CancellationToken ct);
}
```

- **Why an interface:** it hides an external dependency and will have several implementations (Windows, the simulator, later Linux).
- **Slimmer transport:** `ITransport` shrinks to a connected stream: send, receive, dispose. `ConnectAsync` and `IsConnected` move to the connector, since `RfcommTransport` already refuses a second connect.
- **Choosing the platform:** the composition root in `App` picks the connector. That is the only place with platform knowledge.

### Connection lifecycle as a state machine (State pattern, limited scope)

Settings (battery, EQ, noise control) stay as the immutable `DeviceState` snapshot, because they are data. Only the connection lifecycle becomes a state machine, and each state *owns* the resources that are valid in it:

```csharp
abstract record LinkState;
sealed record Disconnected : LinkState;
sealed record Connecting(DiscoveredHeadset Target, CancellationTokenSource Cts) : LinkState;
sealed record Connected(SonyV2Connection Link) : LinkState;          // only way to reach a session
sealed record Reconnecting(int Attempt, DateTimeOffset NextTry) : LinkState;   // later, with auto-reconnect
sealed record Failed(Exception Cause) : LinkState;
```

- **`SonyV2Connection`:** today's `HeadphoneDevice` minus its lifecycle. It exists only after the handshake and V2 check pass, and it dies with the link, so setters can't be called in a "not connected" state.
- **`HeadsetController`:** owns the transitions and reads a single-reader `Channel` (actor style) that carries both user intents and device events (link lost, notifications). Processing them in order removes the `_linkLost` flag and the two locks.
- **Controller responsibilities:** coalescing edits (latest wins per setting group), serializing operations, and later reconnect backoff, polling, and write pacing.
- **Records, not GoF:** use records plus `switch`, not classic GoF state objects with virtual methods. With about five states and most operations being "only in Connected", polymorphism would add classes without removing branches.

## Steps

Each step keeps every test green (440 on 2026-09-28) and gets a review pass before it counts as done.

1. **Extract `HeadphoneControl.Platform.Windows`.** No behaviour change.
   - Create `HeadphoneControl.Core` (`net10.0`).
   - Move `SonyServiceIds`, `DiscoveredHeadset` and the ordering rule from `HeadsetDiscovery.OrderForDisplay` to Core.
   - Add `IHeadsetConnector`, and turn `RfcommConnector` + `HeadsetDiscovery` into its Windows implementation.
   - Move `Bluetooth/` into the new Windows project, and move the Windows tests with it.
   - **App TFM:** for now the app stays `net10.0-windows…` and references the Windows project. A Linux build would later multi-target the app (`net10.0;net10.0-windows…`) with a conditional `ProjectReference` and `#if WINDOWS` only in the composition root. The user approved changing the TFM only when that happens.
   - **Done (2026-09-30):**
     - Choosing which headset to connect to now lives in `HeadsetSelection.ConnectPreferredAsync` in Core.
     - `HeadphoneDeviceHardwareTests` stays in `HeadphoneControl.Tests`, because it uses the app's logging.
     - **Deliberate change:** the 20 s connect timeout now applies to each connector call. Listing and then connecting can take up to 40 s.
     - **Before Linux:** the "not found" message in Core still names Windows Bluetooth settings. Reword it when Linux is added.
     - **Deferred to 2b:** slimming `ITransport`.
2. **Split `HeadphoneDevice`** into `SonyV2Connection` (one link) and `HeadsetController` (the state machine above). Most of the `FakeHeadset`/`FakeTransport` tests carry over to the connection.
   - ⚠️ Carry over every hardware-verified edge case one by one. Do not rewrite from scratch. The cases:
     - the EQ re-read after a preset SET, including the late `0x57`/`0x59` rule;
     - a link that drops mid-connect (connect reports Failed);
     - late ACKs (no sequence resync);
     - connect succeeding only after every setting is read;
     - an unanswered query leaving that setting null.
   - **2a done (2026-09-30):**
     - `SonyV2Connection` (`Protocol/Devices`) is one link. It exists only after the handshake and the V2 check, and exposes `ReadAllAsync` → `DeviceSettings`, the setters, `TryApplyNotification`, the `NotificationReceived`/`LinkLost` events and the `IsLinkLost` latch.
     - `HeadphoneDevice` still owns the lifecycle and delegates to it.
     - Its exception set is documented exactly. 2b maps exceptions from that list.
   - **2b:** `HeadsetController` replaces `HeadphoneDevice`'s lifecycle. Its `Channel` actor takes notifications and link loss as messages, which removes `_operationLock`, `_stateLock` and the reference-check guards. `ITransport` slimming happens here too.
   - **Open decisions for 2b, from the 2a review:**
     - Should `SonyV2Connection` and `DeviceSettings` become `internal` (with `InternalsVisibleTo` for the tests), or stay public once they move to Core? That depends on whether `LinkState` is public.
     - Consider a `Task Completion` on the connection that completes on link loss. The actor could then await it and post a single message, with no gap between checking and subscribing.
     - Once `HeadphoneDevice` is gone, the connection must be the only thing that logs "Link to {Name} lost".
3. **Thin out `MainViewModel`.**
   - Move the operation gate, `SettingGroup` coalescing and the edit-suppression rule into the controller.
   - The view model keeps only binding, strings and status text.
   - Remove the duplicate operation gate.
   - **Bug to fix here (found in the Step 4 review):**
     - Picking "Manual" in the preset list sends `SetEqualizerPresetAsync(Manual)`, and `V2CommandSet.SetEqualizerPreset` rejects that with `ArgumentException`. This fails on the real device too.
     - **Fixed:** selecting Manual sends the curve on screen as a custom equalizer, which is what Manual means on the device.
4. **(Optional) Simulator as a backend.** Build a byte-level `SimulatedHeadset` behind `IHeadsetConnector` (already in `backlog.md`). `--simulated` then exercises the real protocol stack and checks the seam.

## Later: Linux (not now)

- **Backend:** a `HeadphoneControl.Platform.Linux` project using BlueZ. Start with a throwaway spike on real hardware: connect to the V2 UUID, send the init handshake, and confirm the reply `01 00 03 00 10 02 00 00`.
- ⚠️ **Safety:** the backend must set `ServiceGeneration` from the UUID it actually connected to, and never default to V2. `V2CommandSet.FromHandshake` relies on both signals being trustworthy, because opcode 0x22 powers off V1 devices.
- **Paths:** logs and settings go to XDG paths (`~/.local/state`, `~/.config`), not beside the executable or `%APPDATA%`.
