# RadioApp Studio — v26.1123.1000 (Windows 10/11 x64)

Complete 24/7 Cloud PC-ready broadcast automation, Apple Music-grade AutoMix transition engine, Playset & Library manager, programmable 8-pad Cartwall, built-in Git / GitHub Self-Updater, and Icecast/SHOUTcast live streaming console for Windows 10 and Windows 11.

## Solution Architecture

- `RadioApp.Core` (`net8.0`): Cross-platform broadcast engine, `HeadlessBroadcastClockPlayer` (drift-compensated software audio clock for soundcard-free Cloud PC operation), `CloudKeepAliveService` (Win32 execution-state & priority guard), `GitUpdateService` (GitHub Releases checker/downloader/installer + direct local `git fetch`/`git pull`), EBU R128 LUFS & BPM/beat-phase audio analyzer, WSOLA pitch-preserving time-stretcher, 4th-order Linkwitz-Riley (LR24) bass-swap crossover, AutoMix & Smart Segue planner, Lookahead true-peak limiter, SQLite EF Core data layer with WAL mode, Playsets & Smart Rules, Anti-repeat rotation engine, DPAPI credential protection, rotating logger, and Icecast/SHOUTcast streamer with live ICY metadata pushing.
- `RadioApp` (`net8.0-windows10.0.17763.0`): Unified dark-studio WPF application with `SystemEvents.SessionSwitch` RDP disconnect failover, thread-pool `System.Timers.Timer` scheduling/automation (never pauses when RDP disconnects), Windows Login Auto-Start registration, custom interactive Waveform & Cue Marker timeline, Stereo Peak/RMS/LUFS & Limiter GR meter, 8-pad F1–F8 Cartwall overlay mixer, decoupled Local Studio Monitor vs. Broadcast Master Gain, live Mic Talk-Over ducking, PFL Audition preview, 3-pane Library & Playset manager, Broadcast Schedule, and 7-section Left-Rail Settings.
- `RadioApp.Tests` (`net8.0`): xUnit DSP, AutoMix, Cloud Headless Clock, Git Updater, Playset, Rotation, DB, and Settings test suite.
- `Installer/`: WiX v4 MSI installer (`Product.wxs`) and `build-installer.cmd`.

## New in v26.1123.1000

1. **Cloud PC & Unattended RDP Broadcast Continuity**:
   - **Zero-Drop RDP Disconnect Failover**: Subscribes to `SystemEvents.SessionSwitch` (`RemoteDisconnect` / `ConsoleDisconnect`) and hardware `PlaybackStopped` faults to proactively hot-swap the master audio output from Windows `Remote Audio` (`WaveOutEvent`) to `HeadlessBroadcastClockPlayer` without interrupting the AutoMix engine, Master DSP bus, or Icecast/SHOUTcast encoder.
   - **Automatic Hardware Re-Attach**: When you reconnect via RDP, RadioApp detects `RemoteConnect` and cleanly restores your configured studio/RDP output device after audio endpoints initialize.
   - **Selectable Headless Broadcast Clock**: You can also explicitly select **"Headless Cloud Broadcast Clock (RDP-Safe, No Soundcard)"** in *Audio Routing & DSP* or click *Switch to Headless Clock Now* to run purely headless 24/7.
   - **OS Keep-Awake & Priority Protection**: Uses Win32 `SetThreadExecutionState(ES_CONTINUOUS | ES_SYSTEM_REQUIRED | ES_AWAYMODE_REQUIRED)` and `AboveNormal` process priority so the Cloud PC never suspends or throttles the audio pipeline.
   - **RDP-Safe Background Timers**: Core automation, dead-air watchdog, top-of-hour sync, and scheduled events run on `System.Timers.Timer` thread-pool clocks instead of WPF `DispatcherTimer`, so automation keeps firing accurately even while the RDP desktop is disconnected.
   - **Unattended Reboot Recovery**: Supports *Launch on Windows Login* (`HKCU\...\Run`), *Restore Saved Queue*, *Auto-Start Playout on Startup*, and *Auto-Connect Live Stream*.
2. **Built-In Git & GitHub Self-Updater**:
   - **GitHub Releases Integration**: Checks `yadielglz/radioappwin` (configurable) on startup or on demand via *Help → Check for Updates from GitHub* or *Settings → Cloud PC & Git Updates*, surfacing an unobtrusive **Update Ready** badge in the top HUD.
   - **One-Click MSI & Portable ZIP Updates**: Downloads release assets with live progress reporting into `%LOCALAPPDATA%\RadioApp\updates\`, preserves your queue, library SQLite DB, and settings, applies the update (passive MSI upgrade or portable ZIP replacement), and relaunches RadioApp automatically.
   - **Direct Local Git Sync (`git fetch` / `git pull`)**: For Cloud PCs running from a git checkout, inspect ahead/behind commit status and execute `git fetch` or fast-forward `git pull` directly from Settings.

## Core Studio Capabilities (Inherited & Refined from v26.1120.1000)

- **Apple Music-Grade AutoMix & Gapless Engine**: Beat & phrase-matched WSOLA time-stretching (±6%), Linkwitz-Riley LR24 180 Hz bass-swap crossover, EBU R128 loudness matching, 5ms lookahead true-peak limiter (-1.0 dBTP), and broadcast category protection.
- **Playsets & Library Management**: Manual & Smart Rule Playsets, M3U/M3U8/PLS/JSON import/export, persistent playout queue with projected airtimes, 3-pane Library Manager with bulk actions, TagLib# write-back, and PFL audition.
- **Broadcast Console**: Decoupled Local Monitor vs. Broadcast Master Gain, 8-pad Cartwall (F1–F8), Live Mic Talk-Over ducking, Audio-Level RMS Silence Watchdog, Top-of-Hour Sync, and Icecast/SHOUTcast streaming with live ICY metadata & DPAPI password protection.

## Build & Test

```powershell
# Run unit, DSP, Cloud Clock & Git Updater test suite
dotnet test v26.1123.1000\RadioApp.Tests\RadioApp.Tests.csproj -c Release

# Build full solution
dotnet build v26.1123.1000\RadioApp.sln -c Release

# Publish self-contained Windows 10/11 x64 binary & MSI installer
v26.1123.1000\Installer\build-installer.cmd
```
