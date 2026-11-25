# RadioApp Studio — v26.1125.1210 (Windows 10/11 x64)

Complete 24/7 Cloud PC-ready broadcast automation, Broadcast Hour Back-Timing & Rotation-Boundary Planner, FM Radio Gapless Transition Engine, Visual 24-Hour Daypart Rotation Manager, Sweeper & Station ID Threshold Editor with strict once-per-hourset enforcement, Playset & Library manager, programmable 8-pad Cartwall, self-elevating Git / GitHub Self-Updater (with Private Repo authentication), and Icecast/SHOUTcast live streaming console for Windows 10 and Windows 11.

## New in v26.1125.1210

1. **Broadcast Hour Back-Timing (`XX:59:59` Fill) & Zero Old-Rotation Spillover**:
   - **Smart Best-Fit & 2-Track Lookahead (`RotationService`)**: When approaching `:59:59` (`<= 9.5` minutes remaining), Auto-DJ selects track durations to land cleanly right at `:59:59`. When a rotation change occurs at top of hour (e.g. `12:00`), `strictNoHourSpillover` stops adding old-rotation songs once no track fits before `12:00` so old-rotation songs **never** play past the rotation change.
   - **Next-Hour Lead-In Pre-Queuing**: Once the current hour is filled cleanly to `59:59`, Auto-DJ pre-queues lead-in tracks from the **next hour's rotation** (`12:00`) right behind the boundary, and `MaintainAutoDjHour()` preserves those pre-queued next-rotation tracks and the top-of-hour Station ID when `12:00:00` arrives.
2. **Exact Rotation-Handoff & Anti-Early Top-of-Hour Station ID Placement**:
   - **Rotation Boundary Priority**: When rotation changes at `12:00`, the `12:00` Station ID is placed strictly at the handoff between the last `11:00` song and the first `12:00` song — never early at `11:58` with `11:00` songs still playing past `12:00`.
   - **Post-`59:59` Belongs to Next Hourset**: If a final track finishes slightly past `59:59` (e.g. `12:00:30`–`12:02:00`), the Station ID plays right after it as the opener of the `12:00` hourset (strictly once per hourset). Tightened default early tolerance (`RadioIdToleranceBeforeMinutes = 1`) prevents premature `:55–:58` firing.

## Build & Test

```powershell
# Run full unit test suite
dotnet test v26.1125.1210\RadioApp.Tests\RadioApp.Tests.csproj -c Release

# Build full solution
dotnet build v26.1125.1210\RadioApp.sln -c Release

# Publish self-contained Windows 10/11 x64 binary & MSI installer
v26.1125.1210\Installer\build-installer.cmd
```
