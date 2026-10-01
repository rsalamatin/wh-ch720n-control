# Backlog: future improvements

These ideas come from the original design plan (`plan.md`, deleted on 2026-09-28). The plan is out of date wherever it differs from the code; `docs/development-status.md` describes what exists.

## App features
- **System tray.**
  - Avalonia `TrayIcon` with the battery level in its tooltip.
  - Menu: Noise cancelling / Ambient / Off, Open, Reconnect, Exit.
  - Closing the window hides it to the tray. Only Exit disconnects.
- **Settings persistence.** Save to `%APPDATA%\HeadphoneControl\settings.json` with System.Text.Json source generation: last device, window bounds, "start minimized", and the last ambient level.
- **Auto-connect** on startup to the last device, or to the first paired WH-CH720N.
- **Single instance.** A second launch brings the running window to the front.
- **Periodic refresh.**
  - Battery every 30 s. Other settings rotate about every 5 s, so changes made outside the app are picked up without a notification.
  - A slow reply must never lead to two requests in flight at once.
- **Auto-reconnect.**
  - After link loss, retry with backoff: 1, 2, 4, 8, 16, then 30 s.
  - A manual Disconnect stops the retries.
  - A plain timeout does not count as link loss.
- **Self-contained publish.** `dotnet publish -r win-x64 --self-contained` produces a portable folder. No installer.
- **App icon.** The windows still use the Avalonia logo; replace it with a headphone icon (`.ico`).
- **Remember collapsed sections** along with the other persisted settings.

## Safety hardening
- **Outgoing command allowlist (`CommandGuard` inside `ProtocolSession`).**
  - A DATA_MDR payload is sent only if it matches a known command exactly: opcode, subtype, length, and every variable byte within range.
  - Anything else is rejected before it reaches the socket.
  - This adds defence in depth on top of `V2CommandSet.FromHandshake`, the generation gate.
- **Write pacing.** Keep consecutive setter writes at least about 300 ms apart, to protect the headset's settings storage. `HeadsetController` already debounces slider edits and drops edits that a newer one replaced; pacing would go there too.
- **Never implement** firmware update, factory reset, power off, or pairing and multipoint management.

## Protocol session
Found in the refactor reviews; both predate the refactor and live in `ProtocolSession`:
- **Late reply taken as the answer to a newer request.** A late reply to an earlier timed-out request (e.g. `0x57`) that arrives while the next request with the same opcode is pending is matched as that request's reply, so the result is stale.
- **Reply lost between timeout and cleanup.** A reply that arrives after a request timed out, but before its `finally` clears `_pendingResponse`, is consumed by the request that already threw, so it is lost.

## Testing
- **Trace record and replay.**
  - Record real RFCOMM sessions to `.jsonl`.
  - Replay them in unit tests, so hardware behaviour is checked without the headset.
- **Headless UI tests** (Avalonia.Headless). Click real controls and assert on the bytes the simulator receives.
- **Safety fixture for hardware write tests.**
  - Before the first write, back up the settings to `artifacts/headset-backup.json`.
  - Restore after each test class, and also at the start of the next run after a crash.
  - Limit each run to about 100 writes.
  - Require at least 20 % battery.
  - Mark every hardware test `[NotInParallel("headset")]`.
- **Soak test.** Run for about 60 minutes: a read every 10 s and at most one write per minute, then check for read-back mismatches and memory growth.
- **Process smoke test.** `--simulated --smoke` exits 0 once it is connected, for CI.
