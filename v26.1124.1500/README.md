# RadioApp Studio — v26.1124.1500 (Windows 10/11 x64)

Complete 24/7 Cloud PC-ready broadcast automation, Visual 24-Hour Daypart Rotation Manager, Sweeper & Station ID Threshold Editor with strict once-per-hourset enforcement, FM Radio Sweeper transition engine, Apple Music-grade AutoMix, Playset & Library manager, programmable 8-pad Cartwall, built-in Git / GitHub Self-Updater (with Private Repo authentication), and Icecast/SHOUTcast live streaming console for Windows 10 and Windows 11.

## Solution Architecture

- `RadioApp.Core` (`net8.0`): Cross-platform broadcast engine, `RotationService` (24h `RotationBlock` daypart resolver with days-of-week, overnight wrap-around, priority, Playset/Tag/Category sources, coverage/gap analyzer & templates — strictly excluding imaging cues from music picks), `ImagingPolicyService` (Sweeper & Station ID duration threshold evaluator, cross-pool isolation, cooldown & adjacent-imaging spacing, intro-overlay auto-fit, and strict once-per-hourset Station ID enforcement), `TransitionPlanner` (two-phase FM radio sweeper transitions + Apple Music-grade AutoMix & Smart Segue), `HeadlessBroadcastClockPlayer`, `CloudKeepAliveService`, `GitUpdateService` (private GitHub Releases + DPAPI PAT/env/`gh` auth), EBU R128 LUFS & BPM/beat-phase audio analyzer, WSOLA pitch-preserving time-stretcher, 4th-order Linkwitz-Riley (LR24) bass-swap crossover, Lookahead true-peak limiter, SQLite EF Core data layer with WAL mode, Playsets & Smart Rules, DPAPI credential protection, rotating logger, and Icecast/SHOUTcast streamer with live ICY metadata pushing.
- `RadioApp` (`net8.0-windows10.0.17763.0`): Unified dark-studio WPF application with interactive 24-hour `RotationTimelineControl`, 60-minute `StationIdWindowBarControl`, live Rotation Simulator, live Sweeper/ID pool diagnostics & test fire buttons, Playout Queue visible projection of Sweepers (`SWP`/`OVL`) and Station IDs (`ID`), `SystemEvents.SessionSwitch` RDP disconnect failover, thread-pool `System.Timers.Timer` scheduling/automation, custom Waveform & Cue Marker timeline, Stereo Peak/RMS/LUFS & Limiter GR meter, 8-pad F1–F8 Cartwall overlay mixer, decoupled Local Studio Monitor vs. Broadcast Master Gain, live Mic Talk-Over ducking, PFL Audition preview, 3-pane Library & Playset manager, Broadcast Schedule, and 8-section Left-Rail Settings.
- `RadioApp.Tests` (`net8.0`): xUnit Rotation, Imaging Policy, Sweeper Transition, Cloud Headless Clock, Git Updater, DSP, AutoMix, Playset, DB, and Settings test suite.
- `Installer/`: WiX v4 MSI installer (`Product.wxs`) and `build-installer.cmd`.

## New in v26.1124.1500

1. **Strict Once-Per-Hour / Once-Per-Hourset Station ID Enforcement**:
   - **Pre-Top-of-Hour Bucket Resolution**: Station IDs airing inside an early tolerance window (e.g., `13:57` for the `14:00 ± 5m` window) now accurately mark the **`14:00` hourset** served (instead of `13:00`), completely eliminating back-to-back Station ID loops at top of hour.
   - **Zero `:30`/`:40` Leaks & Drifted-ID Auto-Pruning**: Excludes folder-matched Station IDs and Sweepers from Auto-DJ music rotations, isolates Sweeper vs. Station ID pools, purges stale projected IDs on daypart switches or app restarts, caps straddling-track pull-in distance, and automatically prunes any projected ID that drifts outside its hourly window.
   - **Persisted Served Hour Bucket**: Remembers the last served Station ID hourset across restarts (`LastServedRadioIdHourBucket`) and never stacks another ID while one is currently on air.
2. **Broadcast FM Radio Sweeper Transitions**:
   - **Let Songs Finish Naturally**: `Song → Sweeper` ignores AutoMix's `-6.5s` rhythmic beat-mix anchor and plays songs cleanly to `EffectiveEnd` (with a tight `0.15s–0.25s` micro-kiss on Cold/Rhythmic endings, or gliding gently only into the final quiet tail of genuine Fade endings).
   - **Intelligent Sweep-Out into Next Song**: `Sweeper → Next Song` adapts to both the sweeper's trailing reverb/SFX tail and the next song's intro (`Ramp`/`Fade`/`Rhythmic` sweep vs. crisp `Cold` punch), using a click-free full-punch broadcast gain curve (`~25ms` incoming attack) and accurate folder/custom-category classification.
3. **Private GitHub Repository Updater**:
   - Supports DPAPI-encrypted GitHub Personal Access Token (`GitUpdateToken`), `GITHUB_TOKEN`/`GH_TOKEN` environment variables, `gh auth token` CLI fallback, and authenticated GitHub release asset API downloads.

## Build & Test

```powershell
# Run full unit test suite
dotnet test v26.1124.1500\RadioApp.Tests\RadioApp.Tests.csproj -c Release

# Build full solution
dotnet build v26.1124.1500\RadioApp.sln -c Release

# Publish self-contained Windows 10/11 x64 binary & MSI installer
v26.1124.1500\Installer\build-installer.cmd
```
