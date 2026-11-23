# RadioApp Studio — v26.1120.1000 (Windows 10/11 x64)

Complete broadcast automation, Apple Music-grade AutoMix transition engine, Playset & Library manager, programmable 8-pad Cartwall, and Icecast/SHOUTcast live streaming console for Windows 10 and Windows 11.

## Solution Architecture

- `RadioApp.Core` (`net8.0`): Cross-platform broadcast engine, EBU R128 LUFS & BPM/beat-phase audio analyzer, WSOLA pitch-preserving time-stretcher, 4th-order Linkwitz-Riley (LR24) bass-swap crossover, AutoMix & Smart Segue planner, Lookahead true-peak limiter, SQLite EF Core data layer with WAL mode, Playsets & Smart Rules, Anti-repeat rotation engine, DPAPI credential protection, rotating logger, and Icecast/SHOUTcast streamer with live ICY metadata pushing.
- `RadioApp` (`net8.0-windows10.0.17763.0`): Unified dark-studio WPF application with custom interactive Waveform & Cue Marker timeline, Stereo Peak/RMS/LUFS & Limiter GR meter, 8-pad F1–F8 Cartwall overlay mixer, decoupled Local Studio Monitor vs. Broadcast Master Gain, live Mic Talk-Over ducking, PFL Audition preview, 3-pane Library & Playset manager, Broadcast Schedule, and 6-section Left-Rail Settings.
- `RadioApp.Tests` (`net8.0`): xUnit DSP, AutoMix, Playset, Rotation, DB, and Settings test suite.
- `Installer/`: WiX v4 MSI installer (`Product.wxs`) and `build-installer.cmd`.

## Key Highlights in v26.1120.1000

1. **Apple Music-Grade AutoMix & Gapless Engine**:
   - Four transition modes: **AutoMix (Beat & Phrase-Matched)**, **Smart Segue (Dynamic Energy Match)**, **True Gapless (LAME/Xing sample-accurate 0ms)**, and **Classic Crossfade**.
   - Pitch-preserving **WSOLA** time-stretcher (±6% tempo lock + half/double-time matching) + **Linkwitz-Riley LR24 Bass-Swap EQ** (180 Hz) so overlapping kick drums never clash.
   - True **EBU R128 / ITU-R BS.1770-4** integrated loudness matching + **5ms Lookahead True-Peak Brickwall Limiter** (-1.0 dBTP ceiling).
   - Broadcast Category Transition Matrix protects Station IDs, Commercials, and VoiceTracks from beat-stretching.
   - Smooth click-free crossfade even on manual **Mix Next** skips.
   - Background low-priority SQLite-cached track analysis (never freezes the UI on load/preload).
2. **Playsets & Library Management**:
   - First-class **Manual Playsets** and **Smart Rule Playsets** (filter by category, tags, BPM range, duration, folder, never played) with M3U/M3U8/PLS/JSON import & export and Scheduler integration.
   - **Persistent Playout Queue** survives restarts/crashes and displays projected airtimes (`HH:mm:ss`).
   - 3-pane Library Manager with debounced search, multi-select Bulk Action bar, drag-and-drop import, cached non-blocking file health verification, duplicate finder, folder relocator, TagLib# file tag write-back, visual Cue Point overrides, and dedicated **PFL Audition**.
   - Expanded Windows 10/11 format support: `.mp3`, `.wav`, `.aiff`/`.aif`, `.flac`, `.m4a`, `.aac`, `.wma`.
3. **Broadcast Reliability & Studio Console**:
   - Decoupled **Local Monitor Mute/Volume** vs. **Broadcast Master Gain** (muting studio speakers never mutes live listeners).
   - Real **8-Pad Cartwall (F1–F8)** + **Live Mic Talk-Over** mixed directly onto the broadcast bus with smooth auto-ducking.
   - Real **Audio-Level RMS Silence + Transport Stall Dead-Air Watchdog** & implemented **Top-of-Hour Sync**.
   - Live **Icecast/SHOUTcast Track Metadata Pushing**, customizable station headers, DPAPI password encryption, and TCP Test Connection probe.
   - Rotating crash & diagnostic logs safely stored in `%APPDATA%\RadioApp\logs\`.

## Build & Test

```powershell
# Run unit & DSP test suite
dotnet test v26.1120.1000\RadioApp.Tests\RadioApp.Tests.csproj -c Release

# Build full solution
dotnet build v26.1120.1000\RadioApp.sln -c Release

# Publish self-contained Windows 10/11 x64 binary & MSI installer
v26.1120.1000\Installer\build-installer.cmd
```
