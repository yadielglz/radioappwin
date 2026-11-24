# RadioApp Studio — v26.1124.1000 (Windows 10/11 x64)

Complete 24/7 Cloud PC-ready broadcast automation, Visual 24-Hour Daypart Rotation Manager, Sweeper & Station ID Threshold Editor with strict compliance enforcement, Apple Music-grade AutoMix transition engine, Playset & Library manager, programmable 8-pad Cartwall, built-in Git / GitHub Self-Updater, and Icecast/SHOUTcast live streaming console for Windows 10 and Windows 11.

## Solution Architecture

- `RadioApp.Core` (`net8.0`): Cross-platform broadcast engine, `RotationService` (24h `RotationBlock` daypart resolver with days-of-week, overnight wrap-around, priority, Playset/Tag/Category sources, coverage/gap analyzer & templates), `ImagingPolicyService` (Sweeper & Station ID duration threshold evaluator, cooldown & adjacent-imaging spacing, intro-overlay auto-fit, and strict Station ID window/deadline enforcement), `HeadlessBroadcastClockPlayer`, `CloudKeepAliveService`, `GitUpdateService`, EBU R128 LUFS & BPM/beat-phase audio analyzer, WSOLA pitch-preserving time-stretcher, 4th-order Linkwitz-Riley (LR24) bass-swap crossover, AutoMix & Smart Segue planner, Lookahead true-peak limiter, SQLite EF Core data layer with WAL mode, Playsets & Smart Rules, DPAPI credential protection, rotating logger, and Icecast/SHOUTcast streamer with live ICY metadata pushing.
- `RadioApp` (`net8.0-windows10.0.17763.0`): Unified dark-studio WPF application with interactive 24-hour `RotationTimelineControl`, 60-minute `StationIdWindowBarControl`, live Rotation Simulator, live Sweeper/ID pool diagnostics & test fire buttons, `SystemEvents.SessionSwitch` RDP disconnect failover, thread-pool `System.Timers.Timer` scheduling/automation, custom Waveform & Cue Marker timeline, Stereo Peak/RMS/LUFS & Limiter GR meter, 8-pad F1–F8 Cartwall overlay mixer, decoupled Local Studio Monitor vs. Broadcast Master Gain, live Mic Talk-Over ducking, PFL Audition preview, 3-pane Library & Playset manager, Broadcast Schedule, and 8-section Left-Rail Settings.
- `RadioApp.Tests` (`net8.0`): xUnit Rotation, Imaging Policy, Cloud Headless Clock, Git Updater, DSP, AutoMix, Playset, DB, and Settings test suite.
- `Installer/`: WiX v4 MSI installer (`Product.wxs`) and `build-installer.cmd`.

## New in v26.1124.1000

1. **Intuitive Visual 24-Hour Rotation Manager**:
   - **Interactive 24-Hour Rotation Clock** (`RotationTimelineControl`): Color-coded daypart timeline with day-of-week preview (`Mon`–`Sun`), live **NOW** playhead needle, uncovered-gap diagnostics, and click-to-edit block selection.
   - **Rich `RotationBlock` Dayparts**: Supports **Tag**, **Category**, **Playset** (Manual or Smart), or **All Music** sources with dropdown pickers populated live from your library, **Day-of-Week** checkboxes (`Every Day`, `Weekdays`, `Weekends`, or custom), overnight wrap-around (`22:00–02:00`), **Priority (1–100)** overlap resolution, color swatches, live **Matching Track Count** badges (flags empty blocks in red), and optional per-block **Artist Separation** & **Sweeper Cadence** overrides.
   - **One-Click Broadcast Templates & Live Simulator**: Load *Broadcast Dayparts*, *Weekday / Weekend*, or *24/7 All Music* in one click, and run **Simulate Next 6 Picks** to preview rotation + sweeper behavior before going live.
   - **Automatic Legacy Migration**: Existing `AutoDjHourlyTagRules` migrate seamlessly into visual `RotationBlock`s.
2. **Sweeper & Station ID Threshold Editor & Strict Enforcement**:
   - **Visual Duration Thresholds & Live Pool Audit**: Configurable Min/Max duration sliders for Sweepers (`0.5s–60s`) and Station IDs (`0.5s–60s`), matched category lists, and real-time `"X ready • filtered: Y short, Z long"` diagnostics plus **Test Sweeper Now** and **Test Station ID Now** buttons.
   - **Single-Pass Accurate Sweeper Enforcement**: Eliminates song double-counting, enforces **Min Time Cooldown (minutes)** and **Adjacent-Imaging Spacing** (never stacks sweepers back-to-back with Station IDs or commercials), and supports 3 playback modes: **Queue Between Tracks**, **Overlay Over Track Intro** (with configurable ducking dB), and **Smart Auto** (overlays only when the incoming track's intro cue fits the sweeper).
   - **Visual 60-Minute Station ID Window & Compliance Modes**: Interactive `:00–:60` window bar with **Target Minute**, **Early Window (-min)**, and **Late Deadline (+min)** sliders plus 3 enforcement modes: **Queue In Window**, **Hard Interrupt At Deadline** (guarantees legal IDs are never missed even during long songs), and **Overlay At Target** (ducks music and airs the ID right on target without cutting the song).

## Inherited Core Capabilities (`v26.1120.1000` – `v26.1123.1000`)

- **Cloud PC & RDP Continuity**: Zero-drop automatic failover to `HeadlessBroadcastClockPlayer` on RDP disconnect, OS keep-awake (`SetThreadExecutionState`), `AboveNormal` priority, and thread-pool automation timers.
- **Built-In Git & GitHub Self-Updater**: One-click MSI / Portable ZIP updates from GitHub Releases + direct local `git fetch` / `git pull`.
- **Apple Music-Grade AutoMix & Gapless Engine**: WSOLA beat/phrase time-stretching, Linkwitz-Riley LR24 180 Hz bass-swap crossover, EBU R128 loudness matching, and 5ms lookahead true-peak limiter (-1.0 dBTP).

## Build & Test

```powershell
# Run full unit test suite
dotnet test v26.1124.1000\RadioApp.Tests\RadioApp.Tests.csproj -c Release

# Build full solution
dotnet build v26.1124.1000\RadioApp.sln -c Release

# Publish self-contained Windows 10/11 x64 binary & MSI installer
v26.1124.1000\Installer\build-installer.cmd
```
