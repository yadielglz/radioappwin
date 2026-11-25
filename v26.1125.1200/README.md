# RadioApp Studio — v26.1125.1200 (Windows 10/11 x64)

Complete 24/7 Cloud PC-ready broadcast automation, FM Radio Gapless Transition Engine, Visual 24-Hour Daypart Rotation Manager, Sweeper & Station ID Threshold Editor with strict once-per-hourset enforcement, Playset & Library manager, programmable 8-pad Cartwall, self-elevating Git / GitHub Self-Updater (with Private Repo authentication), and Icecast/SHOUTcast live streaming console for Windows 10 and Windows 11.

## Solution Architecture

- `RadioApp.Core` (`net8.0`): Cross-platform broadcast engine, `TransitionPlanner` (FM `RadioGapless` default mode, two-phase broadcast sweeper transitions, tightened `SmartSegue`, `TrueGapless`, and optional `AutoMix` / `ClassicCrossfade`), `RotationService`, `ImagingPolicyService` (strict once-per-hourset Station ID enforcement & cross-pool isolation), `HeadlessBroadcastClockPlayer`, `CloudKeepAliveService`, `GitUpdateService` (magic-byte package verification + UAC-elevated MSI/ZIP updater with full logging & auto-relaunch), EBU R128 LUFS & BPM/beat-phase audio analyzer, Lookahead true-peak limiter, SQLite EF Core WAL database, Playsets & Smart Rules, DPAPI credential protection, rotating logger, and Icecast/SHOUTcast streamer.
- `RadioApp` (`net8.0-windows10.0.17763.0`): Unified dark-studio WPF application with interactive 24-hour `RotationTimelineControl`, 60-minute `StationIdWindowBarControl`, live Rotation Simulator, Playout Queue visible projection of Sweepers (`SWP`/`OVL`) and Station IDs (`ID`), `SystemEvents.SessionSwitch` RDP disconnect failover, thread-pool `System.Timers.Timer` automation, Waveform & Cue Marker timeline, Stereo Peak/RMS/LUFS & Limiter GR meter, 8-pad F1–F8 Cartwall, Local Studio Monitor vs. Broadcast Master Gain, Mic Talk-Over ducking, PFL Audition, Library & Playsets, Schedule, and Left-Rail Settings.
- `RadioApp.Tests` (`net8.0`): xUnit test suite covering Radio Gapless transitions, Sweeper transitions, Station ID enforcement, Rotation, Cloud Headless Clock, Git Updater elevation/validation, DSP, Playset, DB, and Settings.
- `Installer/`: WiX v4 MSI installer (`Product.wxs` with `AllowSameVersionUpgrades="yes"`) and `build-installer.cmd`.

## New in v26.1125.1200

1. **Fixed In-App GitHub Updater (UAC Elevation, Validation & Logging)**:
   - **Root Cause Fixed**: Previously, `msiexec /passive` ran without Administrator elevation (`-Verb RunAs`) against the per-machine `C:\Program Files\RadioApp` install inside a hidden PowerShell window, silently failing with exit code `1603`/`1925` and leaving the previous version installed.
   - **Elevated MSI & ZIP Installers**: MSI updates now validate OLE/ZIP package headers before shutdown, cleanly stop audio/stream and call `Application.Current.Shutdown()`, launch `msiexec.exe /i ... /qb! /norestart /l*v ...` with **`-Verb RunAs`** (proper UAC elevation), log full diagnostics to `%LOCALAPPDATA%\RadioApp\updates\last-update.log` & `last-msi-install.log`, show an actionable message on any non-zero exit code, and automatically relaunch the updated app. Portable ZIP updates stage in temp and auto-elevate if writing to `Program Files`.
2. **True FM Radio Gapless Transition Engine (`TransitionMode.RadioGapless` — New Default)**:
   - **Radio Station Feel (No Unwanted Crossfades)**: Trims dead silence at start/end via `AutoCueInMs`/`AutoCueOutMs` and LAME gapless headers, lets Cold/Rhythmic song endings finish completely with a tight `0.06s–0.18s` zero-gap full-punch handoff (no DJ beat-warping or crossfading), and tight-segues (`1.1s–2.0s` full-punch incoming) ONLY inside genuine multi-second fade-out tails.
   - **Automatic One-Time Upgrade Migration**: Existing installs on the legacy `AutoMix` default automatically migrate once to `RadioGapless` (while keeping `TrueGapless`, tightened `SmartSegue`, `AutoMix`, and `ClassicCrossfade` selectable in Settings).

## Build & Test

```powershell
# Run full unit test suite
dotnet test v26.1125.1200\RadioApp.Tests\RadioApp.Tests.csproj -c Release

# Build full solution
dotnet build v26.1125.1200\RadioApp.sln -c Release

# Publish self-contained Windows 10/11 x64 binary & MSI installer
v26.1125.1200\Installer\build-installer.cmd
```
