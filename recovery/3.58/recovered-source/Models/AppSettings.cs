using System.Collections.Generic;

namespace RadioApp.Models;

public class AppSettings
{
	public bool AutoConnectStreamOnStartup { get; set; }

	public bool MinimizeToTray { get; set; } = true;

	public bool CloseToTray { get; set; } = true;

	public bool StartMinimizedToTray { get; set; }

	public bool EnableLibraryBackgroundRefresh { get; set; } = true;

	public List<string> WatchedLibraryFolders { get; set; } = new List<string>();

	public bool SweepersEnabled { get; set; }

	public string SweeperFolder { get; set; } = string.Empty;

	public int SweeperMinSongs { get; set; } = 3;

	public int SweeperMaxSongs { get; set; } = 6;

	public bool RadioIdsEnabled { get; set; }

	public string RadioIdFolder { get; set; } = string.Empty;

	public int RadioIdMinute { get; set; }

	public int RadioIdToleranceBeforeMinutes { get; set; } = 5;

	public int RadioIdToleranceAfterMinutes { get; set; } = 10;

	public bool GaplessPlaybackEnabled { get; set; } = true;

	public double GaplessSilenceThresholdDb { get; set; } = -42.0;

	public int GaplessSilenceHoldMilliseconds { get; set; } = 650;

	public double GaplessScanWindowSeconds { get; set; } = 20.0;

	public bool AutoDjEnabled { get; set; }

	public bool AutoDjFillCurrentHour { get; set; } = true;

	public int AutoDjQueueDepth { get; set; } = 5;

	public string AutoDjDefaultTag { get; set; } = string.Empty;

	public string AutoDjHourlyTagRules { get; set; } = string.Empty;

	public int AutoDjArtistSeparationMinutes { get; set; } = 45;

	public int AutoDjTrackSeparationHours { get; set; } = 2;

	public int AutoDjCommercialIntervalSongs { get; set; } = 6;

	public bool SilenceWatchdogEnabled { get; set; } = true;

	public int SilenceWatchdogTimeoutSeconds { get; set; } = 6;

	public bool TopOfHourSyncEnabled { get; set; } = true;

	public int TopOfHourTargetMinute { get; set; }

	public bool NowPlayingFileExportEnabled { get; set; } = true;

	public string NowPlayingFilePath { get; set; } = string.Empty;

	public string NowPlayingFormat { get; set; } = "{artist} - {title}";

	public int OutputDeviceNumber { get; set; } = -1;

	public int InputDeviceNumber { get; set; } = -1;

	public bool AudioNormalizationEnabled { get; set; } = true;

	public double NormalizationTargetDb { get; set; } = -14.0;

	public double NormalizationMaxGainDb { get; set; } = 9.0;

	public bool AudioLimiterEnabled { get; set; } = true;

	public StreamServerType StreamServerType { get; set; }

	public string StreamHost { get; set; } = "localhost";

	public int StreamPort { get; set; } = 8000;

	public string StreamMount { get; set; } = "radio";

	public string StreamUsername { get; set; } = "source";

	public string StreamPassword { get; set; } = string.Empty;

	public int StreamBitrate { get; set; } = 128;

	public int StreamShoutcastStreamId { get; set; } = 1;
}
