# RadioApp recovery audit

## Fixed in this build

### Broadcast freeze and false-connected state

- Removed abandoned channel reads that could consume and discard later audio
  after silence, pauses, or track transitions.
- Replaced the unbounded PCM queue with a bounded real-time queue. If the
  encoder/server falls behind, stale audio is dropped instead of accumulating
  latency and memory indefinitely.
- Removed unconditional delays after successful audio reads. Audio is now sent
  as it arrives; silence is generated only when no program buffer arrives for
  100 ms.
- Added a 15-second connection timeout, a 10-second socket send timeout, TCP
  keepalive, and keepalive tuning where the operating system supports it.
- Made connection state visible across threads and report failures only after
  the connection has been marked disconnected.
- Added live stream health text: encoded bytes sent and time since the last
  successful server write. A 15-second write gap is shown as a stalled
  connection rather than healthy publishing.
- Separated streaming health from general playback/library activity messages,
  so unrelated UI events can no longer overwrite the server status.
- Prevented overlapping Connect operations and added an explicit Disconnect
  command.

### Application lifecycle and durability

- Dispose the streamer, audio devices, timers, database contexts, and file
  watchers when the application actually exits.
- Save settings atomically and retain `settings.json.bak`; load the backup if
  the primary JSON file is corrupt.

## Remaining risks and follow-up work

1. The app reports and detects a failed stream but does not automatically
   reconnect. A retry policy should be made configurable before enabling it.
2. The stream password is stored in plaintext in `%APPDATA%\RadioApp\settings.json`
   and the legacy Icecast/SHOUTcast source protocols are not wrapped in TLS.
3. Stream diagnostics are visible in the UI but are not yet written to a
   dedicated rotating log.
4. The SQLite library database has schema patching but no scheduled backup or
   corruption-recovery workflow.
5. The recovered/decompiled code still emits nullable-flow warnings. They are
   non-blocking but should be reviewed before enabling warnings-as-errors.
6. A real Windows end-to-end soak test against the station server is still
   required. The Linux recovery environment can compile and publish WPF but
   cannot run the Windows audio stack or native LAME streaming path.
