using System.Collections.Generic;
using System.Text.Json;

namespace RadioApp.Models;

public class AppSettings
{
	// General & Lifecycle
	public bool AutoConnectStreamOnStartup { get; set; }
	public bool AutoStartPlaybackOnStartup { get; set; }
	public bool MinimizeToTray { get; set; } = true;
	public bool CloseToTray { get; set; } = true;
	public bool StartMinimizedToTray { get; set; }
	public bool RestoreQueueOnStartup { get; set; } = true;

	// Cloud PC / Unattended RDP Continuity
	public bool CloudUnattendedModeEnabled { get; set; } = true;
	public bool AutoFallbackToHeadlessClock { get; set; } = true;
	public bool KeepSystemAwakeWhileRunning { get; set; } = true;
	public bool ElevateProcessPriority { get; set; } = true;

	// Git / GitHub Self-Updater
	public string GitUpdateRepository { get; set; } = AppVersionInfo.DefaultGitHubRepository;
	public bool AutoCheckForUpdatesOnStartup { get; set; } = true;
	public string LocalGitRepoPath { get; set; } = string.Empty;

	// Library & Analysis
	public bool EnableLibraryBackgroundRefresh { get; set; } = true;
	public bool AutoAnalyzeLibraryInBackground { get; set; } = true;
	public List<string> WatchedLibraryFolders { get; set; } = new List<string>();

	// Sweepers & Station IDs — Thresholds & Enforcement
	public bool SweepersEnabled { get; set; }
	public string SweeperFolder { get; set; } = string.Empty;
	public int SweeperMinSongs { get; set; } = 3;
	public int SweeperMaxSongs { get; set; } = 6;
	public double SweeperMinDurationSeconds { get; set; } = 1.0;
	public double SweeperMaxDurationSeconds { get; set; } = 15.0;
	public double SweeperMinCooldownMinutes { get; set; } = 4.0;
	public SweeperPlaybackMode SweeperPlaybackMode { get; set; } = SweeperPlaybackMode.QueueBetweenTracks;
	public bool SweeperIntroOverlayEnabled { get; set; } = true;
	public double SweeperOverlayDuckDb { get; set; } = -9.0;
	public bool SweeperAvoidAdjacentImaging { get; set; } = true;
	public string SweeperCategoryNames { get; set; } = "Sweeper,Sweep,Jingle";

	public bool RadioIdsEnabled { get; set; }
	public string RadioIdFolder { get; set; } = string.Empty;
	public int RadioIdMinute { get; set; }
	public int RadioIdToleranceBeforeMinutes { get; set; } = 5;
	public int RadioIdToleranceAfterMinutes { get; set; } = 5;
	public double RadioIdMinDurationSeconds { get; set; } = 1.5;
	public double RadioIdMaxDurationSeconds { get; set; } = 20.0;
	public StationIdEnforcementMode RadioIdEnforcementMode { get; set; } = StationIdEnforcementMode.HardInterruptAtDeadline;
	public double RadioIdOverlayDuckDb { get; set; } = -10.0;
	public string RadioIdCategoryNames { get; set; } = "StationId,Station ID,ID";

	// Gapless & Apple Music-Grade AutoMix Engine
	public bool GaplessPlaybackEnabled { get; set; } = true;
	public TransitionMode TransitionMode { get; set; } = TransitionMode.AutoMix;
	public CrossfadeCurve CrossfadeCurve { get; set; } = CrossfadeCurve.BassSwapEq;
	public double AutoMixMaxTempoStretchPercent { get; set; } = 6.0;
	public bool AutoMixBassSwapEnabled { get; set; } = true;
	public double AutoMixBassSwapHz { get; set; } = 180.0;
	public int ClassicCrossfadeMs { get; set; } = 4000;
	public int ManualSkipCrossfadeMs { get; set; } = 850;
	public int CommercialGaplessMs { get; set; } = 150;
	public double GaplessSilenceThresholdDb { get; set; } = -42.0;
	public int GaplessSilenceHoldMilliseconds { get; set; } = 650;
	public double GaplessScanWindowSeconds { get; set; } = 25.0;

	// Auto-DJ & Visual Rotation Manager
	public bool AutoDjEnabled { get; set; }
	public bool AutoDjFillCurrentHour { get; set; } = true;
	public int AutoDjQueueDepth { get; set; } = 8;
	public RotationSourceKind RotationDefaultSourceKind { get; set; } = RotationSourceKind.AllMusic;
	public string AutoDjDefaultTag { get; set; } = string.Empty;
	public int? RotationDefaultPlaysetId { get; set; }
	public string AutoDjHourlyTagRules { get; set; } = string.Empty;
	public List<RotationBlock> RotationBlocks { get; set; } = new List<RotationBlock>();
	public int AutoDjArtistSeparationMinutes { get; set; } = 45;
	public int AutoDjTrackSeparationHours { get; set; } = 2;
	public int AutoDjCommercialIntervalSongs { get; set; } = 6;

	// Watchdog & Top-of-Hour
	public bool SilenceWatchdogEnabled { get; set; } = true;
	public int SilenceWatchdogTimeoutSeconds { get; set; } = 6;
	public double SilenceWatchdogThresholdDb { get; set; } = -50.0;
	public int? EmergencyFallbackPlaysetId { get; set; }

	public bool TopOfHourSyncEnabled { get; set; } = true;
	public int TopOfHourTargetMinute { get; set; }

	// Metadata Export
	public bool NowPlayingFileExportEnabled { get; set; } = true;
	public string NowPlayingFilePath { get; set; } = string.Empty;
	public string NowPlayingFormat { get; set; } = "{artist} - {title}";

	// Audio Devices, Routing & Broadcast DSP
	public int OutputDeviceNumber { get; set; } = -1;
	public int PflOutputDeviceNumber { get; set; } = -1;
	public int InputDeviceNumber { get; set; } = -1;
	public double LocalMonitorVolume { get; set; } = 0.85;
	public bool LocalMonitorMuted { get; set; }
	public double BroadcastMasterGainDb { get; set; }
	public bool MicTalkOverEnabled { get; set; }
	public double MicDuckDb { get; set; } = -12.0;

	public bool AudioNormalizationEnabled { get; set; } = true;
	public double NormalizationTargetDb { get; set; } = -14.0;
	public double NormalizationMaxGainDb { get; set; } = 9.0;
	public double TruePeakCeilingDb { get; set; } = -1.0;
	public bool AudioLimiterEnabled { get; set; } = true;

	// Stream Publishing
	public StreamServerType StreamServerType { get; set; } = StreamServerType.Icecast;
	public string StreamHost { get; set; } = "localhost";
	public int StreamPort { get; set; } = 8000;
	public string StreamMount { get; set; } = "radio";
	public string StreamUsername { get; set; } = "source";
	public string StreamPassword { get; set; } = string.Empty;
	public string StreamPasswordEncrypted { get; set; } = string.Empty;
	public int StreamBitrate { get; set; } = 128;
	public int StreamShoutcastStreamId { get; set; } = 1;
	public string StreamStationName { get; set; } = "RadioApp Studio";
	public string StreamStationGenre { get; set; } = "Radio";
	public string StreamStationUrl { get; set; } = "https://radioapp.local";
	public string StreamStationDescription { get; set; } = "Live Broadcast via RadioApp Studio";
	public bool StreamPushMetadata { get; set; } = true;
	public string StreamMetadataFormat { get; set; } = "{artist} - {title}";
	public bool StreamAutoReconnect { get; set; } = true;

	// Cartwall Pads
	public List<CartPadConfig> CartPads { get; set; } = CartPadConfig.CreateDefaults();

	public AppSettings DeepClone()
	{
		string json = JsonSerializer.Serialize(this);
		return JsonSerializer.Deserialize<AppSettings>(json) ?? new AppSettings();
	}
}
