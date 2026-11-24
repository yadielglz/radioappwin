using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using Microsoft.Win32;
using RadioApp.Audio;
using RadioApp.Data;
using RadioApp.Helpers;
using RadioApp.Models;
using RadioApp.Services;
using RadioApp.Views;

namespace RadioApp.ViewModels;

public sealed class QueueDisplayItem : ViewModelBase
{
	private string _projectedStartText = "--:--";
	private bool _isPreloadedNext;

	public LibraryItem Item { get; }
	public string Title => Item.Title;
	public string Artist => Item.Artist;
	public string Category => Item.Category;
	public string DurationText => Item.DurationText;
	public string BpmText => Item.BpmText;
	public string QueueCueLabel => Item.QueueCueLabel;
	public bool IsStationIdCue => Item.IsStationIdCue;
	public bool IsSweeperCue => Item.IsSweeperCue;
	public bool IsCommercialCue => Item.IsCommercialCue;

	public string ProjectedStartText
	{
		get => _projectedStartText;
		set => SetProperty(ref _projectedStartText, value);
	}

	public bool IsPreloadedNext
	{
		get => _isPreloadedNext;
		set => SetProperty(ref _isPreloadedNext, value);
	}

	public QueueDisplayItem(LibraryItem item) => Item = item;
}

public sealed class CartPadViewModel : ViewModelBase
{
	public CartPadConfig Config { get; }

	public int SlotIndex => Config.SlotIndex;
	public string Hotkey => Config.Hotkey;
	public string Label { get => Config.Label; set { Config.Label = value; OnPropertyChanged(); } }
	public string FilePath { get => Config.FilePath; set { Config.FilePath = value; OnPropertyChanged(); OnPropertyChanged(nameof(IsAssigned)); OnPropertyChanged(nameof(FileNameText)); } }
	public string ColorHex { get => Config.ColorHex; set { Config.ColorHex = value; OnPropertyChanged(); } }
	public bool DuckMusic { get => Config.DuckMusic; set { Config.DuckMusic = value; OnPropertyChanged(); } }
	public double DuckDb { get => Config.DuckDb; set { Config.DuckDb = value; OnPropertyChanged(); } }
	public double Volume { get => Config.Volume; set { Config.Volume = value; OnPropertyChanged(); } }
	public bool IsAssigned => !string.IsNullOrWhiteSpace(Config.FilePath) && File.Exists(Config.FilePath);
	public string FileNameText => IsAssigned ? Path.GetFileNameWithoutExtension(Config.FilePath) : "Right-click to assign";

	public CartPadViewModel(CartPadConfig config) => Config = config;
}

public sealed class MainViewModel : ViewModelBase, IDisposable
{
	private readonly RadioDbContextFactory _dbFactory;
	private readonly AudioPlaybackService _audioService;
	private readonly StreamingService _streamingService;
	private readonly AppSettingsService _settingsService;
	private readonly PlaysetService _playsetService;
	private readonly RotationService _rotationService;
	private readonly BackgroundAnalysisService _analysisService;
	private readonly CloudKeepAliveService _cloudKeepAlive;
	private readonly GitUpdateService _updateService;
	private readonly AppSettings _settings;

	private readonly HashSet<LibraryItem> _autoDjQueuedItems = new HashSet<LibraryItem>();
	private readonly System.Timers.Timer _clockTimer;
	private readonly System.Timers.Timer _automationTimer;

	private int _selectedWorkspaceIndex;
	private int _selectedSettingsSectionIndex;
	private string _settingsSearchText = string.Empty;

	// Live Studio HUD Telemetry
	private string _wallClockText = DateTime.Now.ToString("HH:mm:ss");
	private string _topOfHourCountdownText = "T-00:00";
	private string _streamStatus = "Disconnected";
	private string _activityStatus = "Studio Ready";
	private string _streamTestStatus = string.Empty;
	private bool _isStreamLive;

	// Cloud PC / Audio Clock Telemetry
	private string _activeAudioClockText = "HARDWARE AUDIO";
	private bool _isHeadlessClockActive;
	private bool _launchOnWindowsLogin;

	// Git / GitHub Self-Updater State
	private UpdateCheckResult? _latestUpdateResult;
	private string _updateStatusText = $"Running RadioApp Studio v{AppVersionInfo.Version}. Click Check for Updates to query GitHub.";
	private string _updateReleaseNotes = string.Empty;
	private string _latestRemoteVersionText = $"v{AppVersionInfo.Version}";
	private string _localGitStatusText = "Not checked yet.";
	private bool _updateAvailable;
	private bool _isCheckingOrDownloadingUpdate;
	private double _updateDownloadPercent;

	// Live VU / LUFS Metering
	private double _meterLeftDb = -72.0;
	private double _meterRightDb = -72.0;
	private double _limiterGrDb;
	private string _momentaryLufsText = "-70.0 LUFS";
	private string _limiterGrText = "0.0 dB GR";
	private bool _isClipping;

	// Decoupled Studio Monitor vs Broadcast Master Gain & Mic Talk-Over
	private double _localMonitorVolume = 0.85;
	private bool _localMonitorMuted;
	private double _broadcastMasterGainDb;
	private bool _micTalkOverActive;
	private double _micDuckDb = -12.0;

	// Automation, Transition & Queue State
	private LibraryItem? _currentProgramItem;
	private LibraryItem? _preloadedProgramItem;
	private QueueDisplayItem? _selectedQueueItem;
	private Playset? _selectedQuickPlayset;
	private TransitionMode? _activePlaysetTransitionOverride;
	private string _queueStatus = "Queue empty";
	private string _queueTotalDurationText = "0:00";
	private string _nextQueueCue = "Up Next: none";
	private string _nextRadioIdCue = "ID off";
	private string _nextSweeperCue = "Sweepers off";
	private string _rotationCue = "Rotation: unrestricted";

	private int _songsUntilSweeper = 4;
	private int _songsSinceSweeper;
	private int _songsSinceCommercial;
	private DateTime? _lastSweeperPlayedUtc;
	private LibraryItem? _pendingOverlaySweeper;
	private DateTime _lastRadioIdHour = DateTime.MinValue;
	private DateTime _lastTopOfHourSyncHandled = DateTime.MinValue;
	private DateTime _lastAutoDjHour;
	private string _lastActiveRotationKey = string.Empty;
	private TimeSpan _lastProgramPosition = TimeSpan.Zero;
	private TimeSpan _programStallDuration = TimeSpan.Zero;
	private bool _isUpdatingQueue;
	private bool _startupInitialized;
	private bool _disposed;

	// Visual Rotation Manager & Imaging Diagnostics State
	private RotationBlock? _selectedRotationBlock;
	private DayOfWeek _rotationPreviewDay = DateTime.Now.DayOfWeek;
	private int _rotationTimelineRevision;
	private string _rotationCoverageSummary = string.Empty;
	private string _sweeperPoolStatusText = string.Empty;
	private string _radioIdPoolStatusText = string.Empty;
	private string _radioIdWindowStatusText = string.Empty;

	// Stream Connection State
	private bool _isConnectingStream;
	private bool _streamConnectionDesired;
	private bool _streamReconnectScheduled;
	private bool _streamStallRecoveryStarted;
	private int _streamReconnectAttempt;
	private DateTimeOffset? _streamConnectedAtUtc;
	private CancellationTokenSource? _streamReconnectCancellation;

	// Exposed Sub-ViewModels & Collections
	public DeckViewModel ProgramPlayer { get; }
	public LibraryViewModel Library { get; }
	public ScheduleViewModel Schedule { get; }
	public StreamConfig StreamConfig { get; } = new StreamConfig();
	public AppSettings Settings => _settings;

	public ObservableCollection<QueueDisplayItem> Queue { get; } = new ObservableCollection<QueueDisplayItem>();
	public ObservableCollection<CartPadViewModel> CartPads { get; } = new ObservableCollection<CartPadViewModel>();
	public ObservableCollection<AudioDeviceInfo> OutputAudioDevices { get; } = new ObservableCollection<AudioDeviceInfo>();
	public ObservableCollection<AudioDeviceInfo> InputAudioDevices { get; } = new ObservableCollection<AudioDeviceInfo>();
	public ObservableCollection<HourlyTagRuleItem> HourlyTagRules { get; } = new ObservableCollection<HourlyTagRuleItem>();
	public ObservableCollection<RotationBlock> RotationBlocks { get; } = new ObservableCollection<RotationBlock>();
	public ObservableCollection<string> AvailableRotationTags { get; } = new ObservableCollection<string>();
	public ObservableCollection<string> AvailableRotationCategories { get; } = new ObservableCollection<string>();
	public ObservableCollection<string> RotationSimulationResults { get; } = new ObservableCollection<string>();

	public Array TransitionModeOptions => Enum.GetValues(typeof(TransitionMode));
	public Array CrossfadeCurveOptions => Enum.GetValues(typeof(CrossfadeCurve));
	public Array SweeperPlaybackModeOptions => Enum.GetValues(typeof(SweeperPlaybackMode));
	public Array StationIdEnforcementModeOptions => Enum.GetValues(typeof(StationIdEnforcementMode));
	public Array RotationSourceKindOptions => Enum.GetValues(typeof(RotationSourceKind));
	public Array DayOfWeekOptions => Enum.GetValues(typeof(DayOfWeek));

	public string AppVersionText => $"v{AppVersionInfo.Version}";
	public string WindowTitle => AppVersionInfo.DisplayTitle;

	public int SelectedWorkspaceIndex
	{
		get => _selectedWorkspaceIndex;
		set => SetProperty(ref _selectedWorkspaceIndex, value);
	}

	public int SelectedSettingsSectionIndex
	{
		get => _selectedSettingsSectionIndex;
		set => SetProperty(ref _selectedSettingsSectionIndex, value);
	}

	public string SettingsSearchText
	{
		get => _settingsSearchText;
		set => SetProperty(ref _settingsSearchText, value ?? string.Empty);
	}

	public string WallClockText { get => _wallClockText; private set => SetProperty(ref _wallClockText, value); }
	public string TopOfHourCountdownText { get => _topOfHourCountdownText; private set => SetProperty(ref _topOfHourCountdownText, value); }
	public string StreamStatus { get => _streamStatus; private set => SetProperty(ref _streamStatus, value); }
	public string ActivityStatus { get => _activityStatus; set => SetProperty(ref _activityStatus, value); }
	public string StreamTestStatus { get => _streamTestStatus; private set => SetProperty(ref _streamTestStatus, value); }
	public bool IsStreamLive { get => _isStreamLive; private set => SetProperty(ref _isStreamLive, value); }

	public double MeterLeftDb { get => _meterLeftDb; private set => SetProperty(ref _meterLeftDb, value); }
	public double MeterRightDb { get => _meterRightDb; private set => SetProperty(ref _meterRightDb, value); }
	public double LimiterGrDb { get => _limiterGrDb; private set => SetProperty(ref _limiterGrDb, value); }
	public string MomentaryLufsText { get => _momentaryLufsText; private set => SetProperty(ref _momentaryLufsText, value); }
	public string LimiterGrText { get => _limiterGrText; private set => SetProperty(ref _limiterGrText, value); }
	public bool IsClipping { get => _isClipping; private set => SetProperty(ref _isClipping, value); }

	public QueueDisplayItem? SelectedQueueItem
	{
		get => _selectedQueueItem;
		set
		{
			if (SetProperty(ref _selectedQueueItem, value)) RelayCommand.InvalidateAll();
		}
	}

	public Playset? SelectedQuickPlayset
	{
		get => _selectedQuickPlayset;
		set
		{
			if (SetProperty(ref _selectedQuickPlayset, value)) RelayCommand.InvalidateAll();
		}
	}

	public string QueueStatus { get => _queueStatus; private set => SetProperty(ref _queueStatus, value); }
	public string QueueTotalDurationText { get => _queueTotalDurationText; private set => SetProperty(ref _queueTotalDurationText, value); }
	public string NextQueueCue { get => _nextQueueCue; private set => SetProperty(ref _nextQueueCue, value); }
	public string NextRadioIdCue { get => _nextRadioIdCue; private set => SetProperty(ref _nextRadioIdCue, value); }
	public string NextSweeperCue { get => _nextSweeperCue; private set => SetProperty(ref _nextSweeperCue, value); }
	public string RotationCue { get => _rotationCue; private set => SetProperty(ref _rotationCue, value); }

	public string StreamEndpointText => StreamConfig.ServerType == StreamServerType.Shoutcast
		? (StreamConfig.ShoutcastStreamId > 0 ? $"SHOUTcast v2 source ID #{StreamConfig.ShoutcastStreamId}" : "SHOUTcast v1 source")
		: $"Icecast mountpoint: {StreamConfig.FullPath}";

	// Studio Audio Controls (Decoupled Monitor vs Broadcast Gain!)
	public double LocalMonitorVolume
	{
		get => _localMonitorVolume;
		set
		{
			double clamped = Math.Clamp(value, 0.0, 1.0);
			if (SetProperty(ref _localMonitorVolume, clamped))
			{
				_settings.LocalMonitorVolume = clamped;
				_audioService.SetMonitorState(_localMonitorVolume, _localMonitorMuted);
				SaveSettingsSafe();
			}
		}
	}

	public bool LocalMonitorMuted
	{
		get => _localMonitorMuted;
		set
		{
			if (SetProperty(ref _localMonitorMuted, value))
			{
				_settings.LocalMonitorMuted = value;
				_audioService.SetMonitorState(_localMonitorVolume, _localMonitorMuted);
				SaveSettingsSafe();
				ActivityStatus = value ? "Local studio speakers muted (live stream unaffected)." : "Local studio speakers unmuted.";
			}
		}
	}

	public double BroadcastMasterGainDb
	{
		get => _broadcastMasterGainDb;
		set
		{
			double clamped = Math.Clamp(value, -18.0, 12.0);
			if (SetProperty(ref _broadcastMasterGainDb, clamped))
			{
				_settings.BroadcastMasterGainDb = clamped;
				ApplyLiveAudioSettings();
				SaveSettingsSafe();
			}
		}
	}

	public bool MicTalkOverActive
	{
		get => _micTalkOverActive;
		set
		{
			if (_micTalkOverActive == value) return;
			bool ok = _audioService.SetMicTalkOver(value, _settings.InputDeviceNumber, _micDuckDb);
			if (ok)
			{
				_micTalkOverActive = value;
				OnPropertyChanged();
				ActivityStatus = value ? "MIC LIVE — Talk-Over active (music ducked)." : "Mic Talk-Over off.";
			}
		}
	}

	public double MicDuckDb
	{
		get => _micDuckDb;
		set
		{
			double clamped = Math.Clamp(value, -24.0, -3.0);
			if (SetProperty(ref _micDuckDb, clamped))
			{
				_settings.MicDuckDb = clamped;
				ApplyLiveAudioSettings();
				SaveSettingsSafe();
			}
		}
	}

	// Settings Properties (Live-Bound with Instant Apply)
	public bool AutoConnectStreamOnStartup { get => _settings.AutoConnectStreamOnStartup; set { if (_settings.AutoConnectStreamOnStartup != value) { _settings.AutoConnectStreamOnStartup = value; OnPropertyChanged(); SaveSettingsSafe(); } } }
	public bool MinimizeToTray { get => _settings.MinimizeToTray; set { if (_settings.MinimizeToTray != value) { _settings.MinimizeToTray = value; OnPropertyChanged(); SaveSettingsSafe(); } } }
	public bool CloseToTray { get => _settings.CloseToTray; set { if (_settings.CloseToTray != value) { _settings.CloseToTray = value; OnPropertyChanged(); SaveSettingsSafe(); } } }
	public bool StartMinimizedToTray { get => _settings.StartMinimizedToTray; set { if (_settings.StartMinimizedToTray != value) { _settings.StartMinimizedToTray = value; OnPropertyChanged(); SaveSettingsSafe(); } } }
	public bool RestoreQueueOnStartup { get => _settings.RestoreQueueOnStartup; set { if (_settings.RestoreQueueOnStartup != value) { _settings.RestoreQueueOnStartup = value; OnPropertyChanged(); SaveSettingsSafe(); } } }
	public bool AutoStartPlaybackOnStartup { get => _settings.AutoStartPlaybackOnStartup; set { if (_settings.AutoStartPlaybackOnStartup != value) { _settings.AutoStartPlaybackOnStartup = value; OnPropertyChanged(); SaveSettingsSafe(); } } }
	public bool AutoAnalyzeLibraryInBackground { get => _settings.AutoAnalyzeLibraryInBackground; set { if (_settings.AutoAnalyzeLibraryInBackground != value) { _settings.AutoAnalyzeLibraryInBackground = value; OnPropertyChanged(); SaveSettingsSafe(); } } }

	// Cloud PC / Unattended RDP Continuity Properties
	public string ActiveAudioClockText { get => _activeAudioClockText; private set => SetProperty(ref _activeAudioClockText, value); }
	public bool IsHeadlessClockActive { get => _isHeadlessClockActive; private set => SetProperty(ref _isHeadlessClockActive, value); }
	public bool CloudUnattendedModeEnabled { get => _settings.CloudUnattendedModeEnabled; set { if (_settings.CloudUnattendedModeEnabled != value) { _settings.CloudUnattendedModeEnabled = value; OnPropertyChanged(); ApplyLiveAudioSettings(); ApplyCloudKeepAlive(); SaveSettingsSafe(); } } }
	public bool AutoFallbackToHeadlessClock { get => _settings.AutoFallbackToHeadlessClock; set { if (_settings.AutoFallbackToHeadlessClock != value) { _settings.AutoFallbackToHeadlessClock = value; OnPropertyChanged(); ApplyLiveAudioSettings(); SaveSettingsSafe(); } } }
	public bool KeepSystemAwakeWhileRunning { get => _settings.KeepSystemAwakeWhileRunning; set { if (_settings.KeepSystemAwakeWhileRunning != value) { _settings.KeepSystemAwakeWhileRunning = value; OnPropertyChanged(); ApplyCloudKeepAlive(); SaveSettingsSafe(); } } }
	public bool ElevateProcessPriority { get => _settings.ElevateProcessPriority; set { if (_settings.ElevateProcessPriority != value) { _settings.ElevateProcessPriority = value; OnPropertyChanged(); ApplyCloudKeepAlive(); SaveSettingsSafe(); } } }
	public bool LaunchOnWindowsLogin
	{
		get => _launchOnWindowsLogin;
		set
		{
			if (_launchOnWindowsLogin != value && WindowsStartupService.SetAutoStart(value))
			{
				_launchOnWindowsLogin = value;
				OnPropertyChanged();
				ActivityStatus = value ? "Enabled automatic launch on Windows login." : "Disabled automatic launch on Windows login.";
			}
		}
	}

	// Git / GitHub Self-Updater Properties
	public string GitUpdateRepository { get => _settings.GitUpdateRepository; set { string v = string.IsNullOrWhiteSpace(value) ? AppVersionInfo.DefaultGitHubRepository : value.Trim(); if (_settings.GitUpdateRepository != v) { _settings.GitUpdateRepository = v; OnPropertyChanged(); SaveSettingsSafe(); } } }
	public bool AutoCheckForUpdatesOnStartup { get => _settings.AutoCheckForUpdatesOnStartup; set { if (_settings.AutoCheckForUpdatesOnStartup != value) { _settings.AutoCheckForUpdatesOnStartup = value; OnPropertyChanged(); SaveSettingsSafe(); } } }
	public string LocalGitRepoPath { get => _settings.LocalGitRepoPath; set { if (_settings.LocalGitRepoPath != (value ?? string.Empty)) { _settings.LocalGitRepoPath = value ?? string.Empty; OnPropertyChanged(); SaveSettingsSafe(); } } }

	public string UpdateStatusText { get => _updateStatusText; private set => SetProperty(ref _updateStatusText, value); }
	public string UpdateReleaseNotes { get => _updateReleaseNotes; private set => SetProperty(ref _updateReleaseNotes, value); }
	public string LatestRemoteVersionText { get => _latestRemoteVersionText; private set => SetProperty(ref _latestRemoteVersionText, value); }
	public string LocalGitStatusText { get => _localGitStatusText; private set => SetProperty(ref _localGitStatusText, value); }
	public bool UpdateAvailable { get => _updateAvailable; private set { if (SetProperty(ref _updateAvailable, value)) RelayCommand.InvalidateAll(); } }
	public bool IsCheckingOrDownloadingUpdate { get => _isCheckingOrDownloadingUpdate; private set { if (SetProperty(ref _isCheckingOrDownloadingUpdate, value)) RelayCommand.InvalidateAll(); } }
	public double UpdateDownloadPercent { get => _updateDownloadPercent; private set => SetProperty(ref _updateDownloadPercent, value); }
	public bool HasMsiUpdateAsset => _latestUpdateResult?.MsiAsset != null;
	public bool HasZipUpdateAsset => _latestUpdateResult?.PortableZipAsset != null;

	public event Action? RequestGracefulUpdateExit;

	// Transition & AutoMix Settings
	public bool GaplessPlaybackEnabled { get => _settings.GaplessPlaybackEnabled; set { if (_settings.GaplessPlaybackEnabled != value) { _settings.GaplessPlaybackEnabled = value; OnPropertyChanged(); ApplyLiveAudioSettings(); RefreshGaplessPreload(); SaveSettingsSafe(); } } }
	public TransitionMode TransitionMode { get => _settings.TransitionMode; set { if (_settings.TransitionMode != value) { _settings.TransitionMode = value; OnPropertyChanged(); ApplyLiveAudioSettings(); UpdateTransitionPreviewText(); SaveSettingsSafe(); } } }
	public CrossfadeCurve CrossfadeCurve { get => _settings.CrossfadeCurve; set { if (_settings.CrossfadeCurve != value) { _settings.CrossfadeCurve = value; OnPropertyChanged(); ApplyLiveAudioSettings(); UpdateTransitionPreviewText(); SaveSettingsSafe(); } } }
	public double AutoMixMaxTempoStretchPercent { get => _settings.AutoMixMaxTempoStretchPercent; set { double c = Math.Clamp(value, 0.0, 12.0); if (Math.Abs(_settings.AutoMixMaxTempoStretchPercent - c) > 0.01) { _settings.AutoMixMaxTempoStretchPercent = c; OnPropertyChanged(); ApplyLiveAudioSettings(); UpdateTransitionPreviewText(); SaveSettingsSafe(); } } }
	public bool AutoMixBassSwapEnabled { get => _settings.AutoMixBassSwapEnabled; set { if (_settings.AutoMixBassSwapEnabled != value) { _settings.AutoMixBassSwapEnabled = value; OnPropertyChanged(); ApplyLiveAudioSettings(); UpdateTransitionPreviewText(); SaveSettingsSafe(); } } }
	public double AutoMixBassSwapHz { get => _settings.AutoMixBassSwapHz; set { double c = Math.Clamp(value, 80.0, 400.0); if (Math.Abs(_settings.AutoMixBassSwapHz - c) > 0.5) { _settings.AutoMixBassSwapHz = c; OnPropertyChanged(); ApplyLiveAudioSettings(); SaveSettingsSafe(); } } }
	public int ClassicCrossfadeMs { get => _settings.ClassicCrossfadeMs; set { int c = Math.Clamp(value, 100, 12000); if (_settings.ClassicCrossfadeMs != c) { _settings.ClassicCrossfadeMs = c; OnPropertyChanged(); ApplyLiveAudioSettings(); UpdateTransitionPreviewText(); SaveSettingsSafe(); } } }
	public int ManualSkipCrossfadeMs { get => _settings.ManualSkipCrossfadeMs; set { int c = Math.Clamp(value, 100, 4000); if (_settings.ManualSkipCrossfadeMs != c) { _settings.ManualSkipCrossfadeMs = c; OnPropertyChanged(); ApplyLiveAudioSettings(); SaveSettingsSafe(); } } }
	public int CommercialGaplessMs { get => _settings.CommercialGaplessMs; set { int c = Math.Clamp(value, 0, 500); if (_settings.CommercialGaplessMs != c) { _settings.CommercialGaplessMs = c; OnPropertyChanged(); ApplyLiveAudioSettings(); SaveSettingsSafe(); } } }
	public double GaplessSilenceThresholdDb { get => _settings.GaplessSilenceThresholdDb; set { double c = Math.Clamp(value, -66.0, -24.0); if (Math.Abs(_settings.GaplessSilenceThresholdDb - c) > 0.1) { _settings.GaplessSilenceThresholdDb = c; OnPropertyChanged(); _analysisService.ConfigureSilenceThreshold(c); SaveSettingsSafe(); } } }

	// Audio Routing & DSP Settings
	public int SelectedOutputDeviceNumber { get => _settings.OutputDeviceNumber; set { if (_settings.OutputDeviceNumber != value) { _settings.OutputDeviceNumber = value; OnPropertyChanged(); ApplyLiveAudioSettings(); SaveSettingsSafe(); } } }
	public int SelectedPflOutputDeviceNumber { get => _settings.PflOutputDeviceNumber; set { if (_settings.PflOutputDeviceNumber != value) { _settings.PflOutputDeviceNumber = value; OnPropertyChanged(); ApplyLiveAudioSettings(); SaveSettingsSafe(); } } }
	public int SelectedInputDeviceNumber { get => _settings.InputDeviceNumber; set { if (_settings.InputDeviceNumber != value) { _settings.InputDeviceNumber = value; OnPropertyChanged(); SaveSettingsSafe(); } } }
	public bool AudioNormalizationEnabled { get => _settings.AudioNormalizationEnabled; set { if (_settings.AudioNormalizationEnabled != value) { _settings.AudioNormalizationEnabled = value; OnPropertyChanged(); ApplyLiveAudioSettings(); SaveSettingsSafe(); } } }
	public double NormalizationTargetDb { get => _settings.NormalizationTargetDb; set { double c = Math.Clamp(value, -24.0, -6.0); if (Math.Abs(_settings.NormalizationTargetDb - c) > 0.1) { _settings.NormalizationTargetDb = c; OnPropertyChanged(); ApplyLiveAudioSettings(); SaveSettingsSafe(); } } }
	public double NormalizationMaxGainDb { get => _settings.NormalizationMaxGainDb; set { double c = Math.Clamp(value, 0.0, 24.0); if (Math.Abs(_settings.NormalizationMaxGainDb - c) > 0.1) { _settings.NormalizationMaxGainDb = c; OnPropertyChanged(); ApplyLiveAudioSettings(); SaveSettingsSafe(); } } }
	public double TruePeakCeilingDb { get => _settings.TruePeakCeilingDb; set { double c = Math.Clamp(value, -6.0, -0.1); if (Math.Abs(_settings.TruePeakCeilingDb - c) > 0.05) { _settings.TruePeakCeilingDb = c; OnPropertyChanged(); ApplyLiveAudioSettings(); SaveSettingsSafe(); } } }
	public bool AudioLimiterEnabled { get => _settings.AudioLimiterEnabled; set { if (_settings.AudioLimiterEnabled != value) { _settings.AudioLimiterEnabled = value; OnPropertyChanged(); ApplyLiveAudioSettings(); SaveSettingsSafe(); } } }

	// Auto-DJ, Rotation Manager & Imaging Threshold Settings
	public bool AutoScheduleEnabled { get => _settings.AutoDjEnabled; set { if (_settings.AutoDjEnabled != value) { _settings.AutoDjEnabled = value; OnPropertyChanged(); SaveSettingsSafe(); EnsureAutoDjQueue(); } } }
	public bool AutoDjFillCurrentHour { get => _settings.AutoDjFillCurrentHour; set { if (_settings.AutoDjFillCurrentHour != value) { _settings.AutoDjFillCurrentHour = value; OnPropertyChanged(); SaveSettingsSafe(); EnsureAutoDjQueue(); } } }
	public int AutoDjQueueDepth { get => _settings.AutoDjQueueDepth; set { int c = Math.Clamp(value, 1, 50); if (_settings.AutoDjQueueDepth != c) { _settings.AutoDjQueueDepth = c; OnPropertyChanged(); SaveSettingsSafe(); EnsureAutoDjQueue(); } } }
	public RotationSourceKind RotationDefaultSourceKind { get => _settings.RotationDefaultSourceKind; set { if (_settings.RotationDefaultSourceKind != value) { _settings.RotationDefaultSourceKind = value; OnPropertyChanged(); OnPropertyChanged(nameof(IsDefaultRotationPlayset)); OnPropertyChanged(nameof(IsDefaultRotationText)); SaveSettingsSafe(); UpdateAutomationCues(); } } }
	public bool IsDefaultRotationPlayset => RotationDefaultSourceKind == RotationSourceKind.Playset;
	public bool IsDefaultRotationText => RotationDefaultSourceKind == RotationSourceKind.Tag || RotationDefaultSourceKind == RotationSourceKind.Category;
	public string AutoDjDefaultTag { get => _settings.AutoDjDefaultTag; set { if (_settings.AutoDjDefaultTag != (value ?? string.Empty)) { _settings.AutoDjDefaultTag = value ?? string.Empty; OnPropertyChanged(); SaveSettingsSafe(); UpdateAutomationCues(); } } }
	public int? RotationDefaultPlaysetId { get => _settings.RotationDefaultPlaysetId; set { if (_settings.RotationDefaultPlaysetId != value) { _settings.RotationDefaultPlaysetId = value; OnPropertyChanged(); SaveSettingsSafe(); UpdateAutomationCues(); } } }
	public int AutoDjArtistSeparationMinutes { get => _settings.AutoDjArtistSeparationMinutes; set { int c = Math.Clamp(value, 0, 300); if (_settings.AutoDjArtistSeparationMinutes != c) { _settings.AutoDjArtistSeparationMinutes = c; OnPropertyChanged(); SaveSettingsSafe(); } } }
	public int AutoDjTrackSeparationHours { get => _settings.AutoDjTrackSeparationHours; set { int c = Math.Clamp(value, 0, 72); if (_settings.AutoDjTrackSeparationHours != c) { _settings.AutoDjTrackSeparationHours = c; OnPropertyChanged(); SaveSettingsSafe(); } } }
	public int AutoDjCommercialIntervalSongs { get => _settings.AutoDjCommercialIntervalSongs; set { int c = Math.Clamp(value, 0, 50); if (_settings.AutoDjCommercialIntervalSongs != c) { _settings.AutoDjCommercialIntervalSongs = c; OnPropertyChanged(); SaveSettingsSafe(); } } }

	public RotationBlock? SelectedRotationBlock
	{
		get => _selectedRotationBlock;
		set
		{
			if (SetProperty(ref _selectedRotationBlock, value))
			{
				OnPropertyChanged(nameof(HasSelectedRotationBlock));
				RelayCommand.InvalidateAll();
			}
		}
	}
	public bool HasSelectedRotationBlock => _selectedRotationBlock != null;

	public DayOfWeek RotationPreviewDay
	{
		get => _rotationPreviewDay;
		set
		{
			if (SetProperty(ref _rotationPreviewDay, value))
			{
				RefreshRotationCoverageAndMatches();
			}
		}
	}

	public int RotationTimelineRevision { get => _rotationTimelineRevision; private set => SetProperty(ref _rotationTimelineRevision, value); }
	public string RotationCoverageSummary { get => _rotationCoverageSummary; private set => SetProperty(ref _rotationCoverageSummary, value); }

	// Sweeper Threshold & Enforcement Properties
	public bool SweepersEnabled { get => _settings.SweepersEnabled; set { if (_settings.SweepersEnabled != value) { _settings.SweepersEnabled = value; OnPropertyChanged(); SaveSettingsSafe(); UpdateAutomationCues(); RefreshImagingDiagnostics(); } } }
	public string SweeperFolder { get => _settings.SweeperFolder; set { if (_settings.SweeperFolder != (value ?? string.Empty)) { _settings.SweeperFolder = value ?? string.Empty; OnPropertyChanged(); SaveSettingsSafe(); UpdateAutomationCues(); RefreshImagingDiagnostics(); } } }
	public int SweeperMinSongs { get => _settings.SweeperMinSongs; set { int c = Math.Clamp(value, 1, 30); _settings.SweeperMinSongs = c; if (_settings.SweeperMaxSongs < c) _settings.SweeperMaxSongs = c; _songsUntilSweeper = Math.Clamp(_songsUntilSweeper, c, _settings.SweeperMaxSongs); OnPropertyChanged(); OnPropertyChanged(nameof(SweeperMaxSongs)); SaveSettingsSafe(); UpdateAutomationCues(); } }
	public int SweeperMaxSongs { get => _settings.SweeperMaxSongs; set { int c = Math.Clamp(value, _settings.SweeperMinSongs, 40); _settings.SweeperMaxSongs = c; _songsUntilSweeper = Math.Clamp(_songsUntilSweeper, _settings.SweeperMinSongs, c); OnPropertyChanged(); SaveSettingsSafe(); UpdateAutomationCues(); } }
	public double SweeperMinDurationSeconds { get => _settings.SweeperMinDurationSeconds; set { double c = Math.Round(Math.Clamp(value, 0.5, 60.0), 1); _settings.SweeperMinDurationSeconds = c; if (_settings.SweeperMaxDurationSeconds < c) _settings.SweeperMaxDurationSeconds = c; OnPropertyChanged(); OnPropertyChanged(nameof(SweeperMaxDurationSeconds)); SaveSettingsSafe(); RefreshImagingDiagnostics(); } }
	public double SweeperMaxDurationSeconds { get => _settings.SweeperMaxDurationSeconds; set { double c = Math.Round(Math.Clamp(value, _settings.SweeperMinDurationSeconds, 120.0), 1); _settings.SweeperMaxDurationSeconds = c; OnPropertyChanged(); SaveSettingsSafe(); RefreshImagingDiagnostics(); } }
	public double SweeperMinCooldownMinutes { get => _settings.SweeperMinCooldownMinutes; set { double c = Math.Round(Math.Clamp(value, 0.0, 60.0), 1); if (Math.Abs(_settings.SweeperMinCooldownMinutes - c) > 0.01) { _settings.SweeperMinCooldownMinutes = c; OnPropertyChanged(); SaveSettingsSafe(); } } }
	public SweeperPlaybackMode SweeperPlaybackMode { get => _settings.SweeperPlaybackMode; set { if (_settings.SweeperPlaybackMode != value) { _settings.SweeperPlaybackMode = value; OnPropertyChanged(); SaveSettingsSafe(); UpdateAutomationCues(); } } }
	public double SweeperOverlayDuckDb { get => _settings.SweeperOverlayDuckDb; set { double c = Math.Round(Math.Clamp(value, -24.0, -2.0), 1); if (Math.Abs(_settings.SweeperOverlayDuckDb - c) > 0.01) { _settings.SweeperOverlayDuckDb = c; OnPropertyChanged(); SaveSettingsSafe(); } } }
	public bool SweeperAvoidAdjacentImaging { get => _settings.SweeperAvoidAdjacentImaging; set { if (_settings.SweeperAvoidAdjacentImaging != value) { _settings.SweeperAvoidAdjacentImaging = value; OnPropertyChanged(); SaveSettingsSafe(); } } }
	public string SweeperCategoryNames { get => _settings.SweeperCategoryNames; set { if (_settings.SweeperCategoryNames != (value ?? string.Empty)) { _settings.SweeperCategoryNames = value ?? string.Empty; OnPropertyChanged(); SaveSettingsSafe(); RefreshImagingDiagnostics(); } } }
	public string SweeperPoolStatusText { get => _sweeperPoolStatusText; private set => SetProperty(ref _sweeperPoolStatusText, value); }

	// Station ID Threshold & Enforcement Properties
	public bool RadioIdsEnabled { get => _settings.RadioIdsEnabled; set { if (_settings.RadioIdsEnabled != value) { _settings.RadioIdsEnabled = value; OnPropertyChanged(); SaveSettingsSafe(); UpdateAutomationCues(); RefreshImagingDiagnostics(); } } }
	public string RadioIdFolder { get => _settings.RadioIdFolder; set { if (_settings.RadioIdFolder != (value ?? string.Empty)) { _settings.RadioIdFolder = value ?? string.Empty; OnPropertyChanged(); SaveSettingsSafe(); UpdateAutomationCues(); RefreshImagingDiagnostics(); } } }
	public int RadioIdMinute { get => _settings.RadioIdMinute; set { int c = Math.Clamp(value, 0, 59); _settings.RadioIdMinute = c; OnPropertyChanged(); SaveSettingsSafe(); UpdateAutomationCues(); RefreshImagingDiagnostics(); } }
	public int RadioIdToleranceBeforeMinutes { get => _settings.RadioIdToleranceBeforeMinutes; set { int c = Math.Clamp(value, 0, 30); _settings.RadioIdToleranceBeforeMinutes = c; OnPropertyChanged(); SaveSettingsSafe(); UpdateAutomationCues(); RefreshImagingDiagnostics(); } }
	public int RadioIdToleranceAfterMinutes { get => _settings.RadioIdToleranceAfterMinutes; set { int c = Math.Clamp(value, 0, 30); _settings.RadioIdToleranceAfterMinutes = c; OnPropertyChanged(); SaveSettingsSafe(); UpdateAutomationCues(); RefreshImagingDiagnostics(); } }
	public double RadioIdMinDurationSeconds { get => _settings.RadioIdMinDurationSeconds; set { double c = Math.Round(Math.Clamp(value, 0.5, 60.0), 1); _settings.RadioIdMinDurationSeconds = c; if (_settings.RadioIdMaxDurationSeconds < c) _settings.RadioIdMaxDurationSeconds = c; OnPropertyChanged(); OnPropertyChanged(nameof(RadioIdMaxDurationSeconds)); SaveSettingsSafe(); RefreshImagingDiagnostics(); } }
	public double RadioIdMaxDurationSeconds { get => _settings.RadioIdMaxDurationSeconds; set { double c = Math.Round(Math.Clamp(value, _settings.RadioIdMinDurationSeconds, 120.0), 1); _settings.RadioIdMaxDurationSeconds = c; OnPropertyChanged(); SaveSettingsSafe(); RefreshImagingDiagnostics(); } }
	public StationIdEnforcementMode RadioIdEnforcementMode { get => _settings.RadioIdEnforcementMode; set { if (_settings.RadioIdEnforcementMode != value) { _settings.RadioIdEnforcementMode = value; OnPropertyChanged(); SaveSettingsSafe(); UpdateAutomationCues(); RefreshImagingDiagnostics(); } } }
	public double RadioIdOverlayDuckDb { get => _settings.RadioIdOverlayDuckDb; set { double c = Math.Round(Math.Clamp(value, -24.0, -2.0), 1); if (Math.Abs(_settings.RadioIdOverlayDuckDb - c) > 0.01) { _settings.RadioIdOverlayDuckDb = c; OnPropertyChanged(); SaveSettingsSafe(); } } }
	public string RadioIdCategoryNames { get => _settings.RadioIdCategoryNames; set { if (_settings.RadioIdCategoryNames != (value ?? string.Empty)) { _settings.RadioIdCategoryNames = value ?? string.Empty; OnPropertyChanged(); SaveSettingsSafe(); RefreshImagingDiagnostics(); } } }
	public string RadioIdPoolStatusText { get => _radioIdPoolStatusText; private set => SetProperty(ref _radioIdPoolStatusText, value); }
	public string RadioIdWindowStatusText { get => _radioIdWindowStatusText; private set => SetProperty(ref _radioIdWindowStatusText, value); }

	public bool TopOfHourSyncEnabled { get => _settings.TopOfHourSyncEnabled; set { if (_settings.TopOfHourSyncEnabled != value) { _settings.TopOfHourSyncEnabled = value; OnPropertyChanged(); SaveSettingsSafe(); } } }
	public int TopOfHourTargetMinute { get => _settings.TopOfHourTargetMinute; set { int c = Math.Clamp(value, 0, 59); if (_settings.TopOfHourTargetMinute != c) { _settings.TopOfHourTargetMinute = c; OnPropertyChanged(); SaveSettingsSafe(); } } }

	public bool SilenceWatchdogEnabled { get => _settings.SilenceWatchdogEnabled; set { if (_settings.SilenceWatchdogEnabled != value) { _settings.SilenceWatchdogEnabled = value; OnPropertyChanged(); SaveSettingsSafe(); } } }
	public int SilenceWatchdogTimeoutSeconds { get => _settings.SilenceWatchdogTimeoutSeconds; set { int c = Math.Clamp(value, 3, 60); _settings.SilenceWatchdogTimeoutSeconds = c; OnPropertyChanged(); SaveSettingsSafe(); } }
	public double SilenceWatchdogThresholdDb { get => _settings.SilenceWatchdogThresholdDb; set { double c = Math.Clamp(value, -72.0, -24.0); _settings.SilenceWatchdogThresholdDb = c; OnPropertyChanged(); ApplyLiveAudioSettings(); SaveSettingsSafe(); } }
	public int? EmergencyFallbackPlaysetId { get => _settings.EmergencyFallbackPlaysetId; set { _settings.EmergencyFallbackPlaysetId = value; OnPropertyChanged(); SaveSettingsSafe(); } }

	public bool NowPlayingFileExportEnabled { get => _settings.NowPlayingFileExportEnabled; set { _settings.NowPlayingFileExportEnabled = value; OnPropertyChanged(); SaveSettingsSafe(); } }
	public string NowPlayingFilePath { get => _settings.NowPlayingFilePath; set { _settings.NowPlayingFilePath = value ?? string.Empty; OnPropertyChanged(); SaveSettingsSafe(); } }
	public string NowPlayingFormat { get => _settings.NowPlayingFormat; set { _settings.NowPlayingFormat = string.IsNullOrWhiteSpace(value) ? "{artist} - {title}" : value; OnPropertyChanged(); SaveSettingsSafe(); } }

	// Commands
	public ICommand ConnectStreamCommand { get; }
	public ICommand DisconnectStreamCommand { get; }
	public ICommand ToggleStreamCommand { get; }
	public ICommand TestStreamConnectionCommand { get; }
	public ICommand TestAudioOutputCommand { get; }
	public ICommand TestPflOutputCommand { get; }
	public ICommand ToggleMonitorMuteCommand { get; }
	public ICommand ToggleMicTalkOverCommand { get; }
	public ICommand SwitchToHeadlessClockNowCommand { get; }

	public ICommand CheckForUpdatesCommand { get; }
	public ICommand DownloadAndInstallMsiUpdateCommand { get; }
	public ICommand DownloadAndInstallZipUpdateCommand { get; }
	public ICommand OpenReleasePageCommand { get; }
	public ICommand OpenUpdatesSettingsCommand { get; }
	public ICommand OpenRotationManagerCommand { get; }
	public ICommand OpenImagingSettingsCommand { get; }
	public ICommand GitFetchLocalRepoCommand { get; }
	public ICommand GitPullLocalRepoCommand { get; }
	public ICommand BrowseLocalGitRepoCommand { get; }

	public ICommand LoadSelectedToProgramCommand { get; }
	public ICommand EnqueueSelectedCommand { get; }
	public ICommand EnqueueRandomCommand { get; }
	public ICommand PlayNextCommand { get; }
	public ICommand RemoveQueueItemCommand { get; }
	public ICommand MoveQueueItemUpCommand { get; }
	public ICommand MoveQueueItemDownCommand { get; }
	public ICommand ClearQueueCommand { get; }
	public ICommand RandomizeDeckCommand { get; }

	public ICommand LoadPlaysetReplaceCommand { get; }
	public ICommand LoadPlaysetAppendCommand { get; }
	public ICommand LoadPlaysetShuffledCommand { get; }
	public ICommand SaveQueueAsPlaysetCommand { get; }

	public ICommand TriggerCartPadCommand { get; }
	public ICommand EditCartPadCommand { get; }
	public ICommand StopAllOverlaysCommand { get; }

	public ICommand SelectSweeperFolderCommand { get; }
	public ICommand SelectRadioIdFolderCommand { get; }
	public ICommand TestSweeperNowCommand { get; }
	public ICommand TestStationIdNowCommand { get; }
	public ICommand AddRotationBlockCommand { get; }
	public ICommand DuplicateRotationBlockCommand { get; }
	public ICommand RemoveRotationBlockCommand { get; }
	public ICommand ApplyRotationPresetCommand { get; }
	public ICommand SetSelectedBlockDaysCommand { get; }
	public ICommand SetSelectedBlockColorCommand { get; }
	public ICommand SimulateRotationCommand { get; }
	public ICommand RefreshRotationAndImagingCommand { get; }
	public ICommand AddHourlyRuleCommand { get; }
	public ICommand RemoveHourlyRuleCommand { get; }
	public ICommand SaveHourlyRulesCommand { get; }
	public ICommand RefreshAudioDevicesCommand { get; }

	public ICommand BackupDatabaseCommand { get; }
	public ICommand VerifyDatabaseCommand { get; }
	public ICommand OpenLogsFolderCommand { get; }
	public ICommand ExportSettingsCommand { get; }
	public ICommand ImportSettingsCommand { get; }
	public ICommand ClearLibraryCommand { get; }
	public ICommand ResetAppCommand { get; }
	public ICommand SwitchWorkspaceCommand { get; }

	public MainViewModel()
	{
		LogService.PruneOldLogs();
		LogService.Info("App", $"Starting RadioApp Studio v{AppVersionInfo.Version}");

		_dbFactory = new RadioDbContextFactory();
		_settingsService = new AppSettingsService();
		_settings = _settingsService.Load();
		_audioService = new AudioPlaybackService();
		_streamingService = new StreamingService();
		_playsetService = new PlaysetService(_dbFactory);
		_rotationService = new RotationService();
		_analysisService = new BackgroundAnalysisService(_dbFactory);
		_cloudKeepAlive = new CloudKeepAliveService();
		_updateService = new GitUpdateService();
		_launchOnWindowsLogin = WindowsStartupService.IsAutoStartEnabled();

		ProgramPlayer = new DeckViewModel(_audioService);
		Library = new LibraryViewModel(_dbFactory, _settings, _settingsService, _playsetService, _rotationService, _analysisService, _audioService);
		Schedule = new ScheduleViewModel(_dbFactory, () => Library.Playsets.ToList());

		HydrateFromSettings();
		RefreshAudioDevices();
		ApplyLiveAudioSettings();
		ApplyCloudKeepAlive();
		_songsUntilSweeper = PickSweeperInterval();

		// Wire Commands
		ConnectStreamCommand = new RelayCommand(async () =>
		{
			_streamConnectionDesired = true;
			CancelStreamReconnect();
			await ConnectStreamAsync(scheduleRetryOnFailure: StreamConfig.AutoReconnectEnabled);
		});
		DisconnectStreamCommand = new RelayCommand(DisconnectStream);
		ToggleStreamCommand = new RelayCommand(async () =>
		{
			if (_streamingService.IsConnected || _isConnectingStream) DisconnectStream();
			else
			{
				_streamConnectionDesired = true;
				CancelStreamReconnect();
				await ConnectStreamAsync(scheduleRetryOnFailure: StreamConfig.AutoReconnectEnabled);
			}
		});
		TestStreamConnectionCommand = new RelayCommand(async () => await TestStreamConnectionAsync());
		TestAudioOutputCommand = new RelayCommand(() => AudioPlaybackService.PlayTestTone(SelectedOutputDeviceNumber));
		TestPflOutputCommand = new RelayCommand(() => AudioPlaybackService.PlayTestTone(SelectedPflOutputDeviceNumber));
		ToggleMonitorMuteCommand = new RelayCommand(() => LocalMonitorMuted = !LocalMonitorMuted);
		ToggleMicTalkOverCommand = new RelayCommand(() => MicTalkOverActive = !MicTalkOverActive);
		SwitchToHeadlessClockNowCommand = new RelayCommand(() =>
		{
			SelectedOutputDeviceNumber = AppVersionInfo.HeadlessCloudClockDeviceNumber;
			_audioService.ForceHeadlessFailover("Manual Cloud Clock mode");
			ActivityStatus = "Switched to Headless Cloud Broadcast Clock (24/7 RDP-Safe).";
		});

		CheckForUpdatesCommand = new RelayCommand(async () => await CheckForUpdatesAsync(silent: false), () => !IsCheckingOrDownloadingUpdate);
		DownloadAndInstallMsiUpdateCommand = new RelayCommand(async () => await DownloadAndInstallUpdateAsync(preferZip: false), () => !IsCheckingOrDownloadingUpdate && HasMsiUpdateAsset);
		DownloadAndInstallZipUpdateCommand = new RelayCommand(async () => await DownloadAndInstallUpdateAsync(preferZip: true), () => !IsCheckingOrDownloadingUpdate && HasZipUpdateAsset);
		OpenReleasePageCommand = new RelayCommand(() =>
		{
			string url = _latestUpdateResult?.HtmlUrl ?? $"https://github.com/{GitUpdateRepository}/releases";
			try { Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true }); } catch { }
		});
		OpenUpdatesSettingsCommand = new RelayCommand(() =>
		{
			SelectedWorkspaceIndex = 3;
			SelectedSettingsSectionIndex = 7;
		});
		OpenRotationManagerCommand = new RelayCommand(() =>
		{
			RefreshRotationCatalogOptions();
			RefreshRotationCoverageAndMatches();
			SelectedWorkspaceIndex = 3;
			SelectedSettingsSectionIndex = 3;
		});
		OpenImagingSettingsCommand = new RelayCommand(() =>
		{
			RefreshImagingDiagnostics();
			SelectedWorkspaceIndex = 3;
			SelectedSettingsSectionIndex = 4;
		});
		GitFetchLocalRepoCommand = new RelayCommand(async () => await FetchLocalGitAsync(), () => !IsCheckingOrDownloadingUpdate);
		GitPullLocalRepoCommand = new RelayCommand(async () => await PullLocalGitAsync(), () => !IsCheckingOrDownloadingUpdate);
		BrowseLocalGitRepoCommand = new RelayCommand(() =>
		{
			var dlg = new OpenFolderDialog { Title = "Select local Git repository folder" };
			if (dlg.ShowDialog() == true) LocalGitRepoPath = dlg.FolderName;
		});

		LoadSelectedToProgramCommand = new RelayCommand(LoadSelectedLibraryItemToProgram, () => Library.SelectedItem != null);
		EnqueueSelectedCommand = new RelayCommand(EnqueueSelectedLibraryItems, () => Library.GetEffectiveSelection().Count > 0);
		EnqueueRandomCommand = new RelayCommand(EnqueueAutomationItem);
		PlayNextCommand = new RelayCommand(PlayNextFromQueue, () => Queue.Count > 0);
		RemoveQueueItemCommand = new RelayCommand(RemoveSelectedQueueItem, () => SelectedQueueItem != null);
		MoveQueueItemUpCommand = new RelayCommand(() => MoveSelectedQueueItem(-1), () => SelectedQueueItem != null && Queue.IndexOf(SelectedQueueItem) > 0);
		MoveQueueItemDownCommand = new RelayCommand(() => MoveSelectedQueueItem(1), () => SelectedQueueItem != null && Queue.IndexOf(SelectedQueueItem) < Queue.Count - 1);
		ClearQueueCommand = new RelayCommand(ClearQueue, () => Queue.Count > 0);
		RandomizeDeckCommand = new RelayCommand(() => LoadRandomToProgram(play: true));

		LoadPlaysetReplaceCommand = new RelayCommand(p => LoadPlaysetToQueue(p as Playset ?? SelectedQuickPlayset ?? Library.SelectedPlayset, replace: true, shuffle: false));
		LoadPlaysetAppendCommand = new RelayCommand(p => LoadPlaysetToQueue(p as Playset ?? SelectedQuickPlayset ?? Library.SelectedPlayset, replace: false, shuffle: false));
		LoadPlaysetShuffledCommand = new RelayCommand(p => LoadPlaysetToQueue(p as Playset ?? SelectedQuickPlayset ?? Library.SelectedPlayset, replace: true, shuffle: true));
		SaveQueueAsPlaysetCommand = new RelayCommand(SaveCurrentQueueAsPlayset, () => Queue.Count > 0);

		TriggerCartPadCommand = new RelayCommand(p => { if (p is CartPadViewModel pad) TriggerCartPad(pad); });
		EditCartPadCommand = new RelayCommand(p => { if (p is CartPadViewModel pad) EditCartPad(pad); });
		StopAllOverlaysCommand = new RelayCommand(() =>
		{
			_audioService.StopAllOverlays();
			ActivityStatus = "Stopped all cartwall overlays.";
		});

		SelectSweeperFolderCommand = new RelayCommand(SelectSweeperFolder);
		SelectRadioIdFolderCommand = new RelayCommand(SelectRadioIdFolder);
		TestSweeperNowCommand = new RelayCommand(TestSweeperNow);
		TestStationIdNowCommand = new RelayCommand(TestStationIdNow);
		AddRotationBlockCommand = new RelayCommand(AddRotationBlock);
		DuplicateRotationBlockCommand = new RelayCommand(DuplicateSelectedRotationBlock, () => SelectedRotationBlock != null);
		RemoveRotationBlockCommand = new RelayCommand(p => RemoveRotationBlock(p as RotationBlock ?? SelectedRotationBlock), p => (p as RotationBlock ?? SelectedRotationBlock) != null);
		ApplyRotationPresetCommand = new RelayCommand(p => ApplyRotationPreset(p?.ToString() ?? string.Empty));
		SetSelectedBlockDaysCommand = new RelayCommand(p => SetSelectedBlockDays(p?.ToString() ?? string.Empty), _ => SelectedRotationBlock != null);
		SetSelectedBlockColorCommand = new RelayCommand(p => { if (SelectedRotationBlock != null && p is string hex) { SelectedRotationBlock.ColorHex = hex; } }, _ => SelectedRotationBlock != null);
		SimulateRotationCommand = new RelayCommand(RunRotationSimulation);
		RefreshRotationAndImagingCommand = new RelayCommand(() =>
		{
			RefreshRotationCatalogOptions();
			RefreshRotationCoverageAndMatches();
			RefreshImagingDiagnostics();
			ActivityStatus = "Refreshed rotation coverage and sweeper/ID pool diagnostics.";
		});

		AddHourlyRuleCommand = new RelayCommand(() =>
		{
			HourlyTagRules.Add(new HourlyTagRuleItem { StartTime = "09:00", EndTime = "12:00", Tag = "Daytime" });
			PersistHourlyRules();
		});
		RemoveHourlyRuleCommand = new RelayCommand(p =>
		{
			if (p is HourlyTagRuleItem rule)
			{
				HourlyTagRules.Remove(rule);
				PersistHourlyRules();
			}
		});
		SaveHourlyRulesCommand = new RelayCommand(PersistHourlyRules);
		RefreshAudioDevicesCommand = new RelayCommand(RefreshAudioDevices);

		BackupDatabaseCommand = new RelayCommand(BackupDatabase);
		VerifyDatabaseCommand = new RelayCommand(VerifyDatabase);
		OpenLogsFolderCommand = new RelayCommand(OpenLogsFolder);
		ExportSettingsCommand = new RelayCommand(ExportSettingsBackup);
		ImportSettingsCommand = new RelayCommand(ImportSettingsBackup);
		ClearLibraryCommand = new RelayCommand(ClearLibrary);
		ResetAppCommand = new RelayCommand(ResetApp);
		SwitchWorkspaceCommand = new RelayCommand(p =>
		{
			if (p != null && int.TryParse(p.ToString(), out int idx)) SelectedWorkspaceIndex = idx;
		});

		// Subscribe Events
		Schedule.ScheduleDue += OnScheduleDue;
		Library.LibraryChanged += OnLibraryChanged;
		Library.TrackAnalysisUpdated += OnTrackAnalysisUpdated;
		StreamConfig.PropertyChanged += OnStreamConfigPropertyChanged;
		ProgramPlayer.ManualFileLoaded += item =>
		{
			_currentProgramItem = item;
			ExportNowPlaying(item);
			RefreshGaplessPreload();
		};
		_audioService.ProgramOutputAvailable += _streamingService.EnqueueProgramOutput;
		_audioService.PlaybackUpdated += OnPlaybackUpdated;
		_audioService.TelemetryUpdated += OnTelemetryUpdated;
		_audioService.OutputEngineStatusChanged += (statusText, isHeadless) =>
		{
			ActiveAudioClockText = statusText;
			IsHeadlessClockActive = isHeadless;
			if (isHeadless)
			{
				ActivityStatus = $"Audio clock failover active: {statusText}";
			}
		};
		_audioService.TrackEnded += OnTrackEnded;
		_audioService.PreloadedTrackStarted += OnPreloadedTrackStarted;
		_audioService.PlaybackFailed += msg => ActivityStatus = "Playback error: " + msg;
		_streamingService.StreamFailed += OnStreamFailed;

		// 1-second RDP-Safe Studio Clock, Stream Health, Watchdog, Station ID Enforcement & Top-of-Hour Timer
		_clockTimer = new System.Timers.Timer(1000.0) { AutoReset = true };
		_clockTimer.Elapsed += (_, _) => Application.Current?.Dispatcher.BeginInvoke(() => OnClockTick(null, EventArgs.Empty));
		_clockTimer.Start();

		// 15-second RDP-Safe Automation & Rotation Maintenance Timer
		_lastAutoDjHour = GetCurrentHour();
		_lastActiveRotationKey = BuildRotationKey(GetActiveRotationNow());
		_automationTimer = new System.Timers.Timer(15000.0) { AutoReset = true };
		_automationTimer.Elapsed += (_, _) => Application.Current?.Dispatcher.BeginInvoke(MaintainAutoDjHour);
		_automationTimer.Start();

		RestorePersistedQueueIfEnabled();
		RefreshRotationCatalogOptions();
		RefreshRotationCoverageAndMatches();
		RefreshImagingDiagnostics();
		EnsureAutoDjQueue();
		UpdateQueueTelemetry();
		ActiveAudioClockText = _audioService.ActiveClockStatusText;
		IsHeadlessClockActive = _audioService.IsUsingHeadlessClock;
	}

	public async Task InitializeAsync()
	{
		if (_startupInitialized) return;
		_startupInitialized = true;

		if (Library.BackgroundRefreshEnabled)
		{
			await Library.RunWatchedFolderRefreshAsync(reportStatus: false);
		}
		if (AutoStartPlaybackOnStartup && !ProgramPlayer.IsPlaying)
		{
			EnsureAutoDjQueue();
			if (Queue.Count > 0)
			{
				PlayNextFromQueue();
			}
			else
			{
				LoadRandomToProgram(play: true);
			}
		}
		if (AutoConnectStreamOnStartup)
		{
			_streamConnectionDesired = true;
			await ConnectStreamAsync(scheduleRetryOnFailure: StreamConfig.AutoReconnectEnabled);
		}
		if (AutoCheckForUpdatesOnStartup)
		{
			_ = CheckForUpdatesAsync(silent: true);
		}
	}

	public void TriggerCartPadBySlot(int slotIndex)
	{
		CartPadViewModel? pad = CartPads.FirstOrDefault(p => p.SlotIndex == slotIndex);
		if (pad != null) TriggerCartPad(pad);
	}

	public void MoveQueueItem(int oldIndex, int newIndex)
	{
		if (oldIndex < 0 || newIndex < 0 || oldIndex >= Queue.Count || newIndex >= Queue.Count || oldIndex == newIndex) return;
		Queue.Move(oldIndex, newIndex);
		UpdateQueueTelemetry();
		PersistQueueSafe();
		RefreshGaplessPreload();
	}

	private void TriggerCartPad(CartPadViewModel pad)
	{
		if (!pad.IsAssigned)
		{
			EditCartPad(pad);
			return;
		}
		if (_audioService.TriggerCartPad(pad.Config))
		{
			ActivityStatus = $"Fired Cart [{pad.Hotkey}] {pad.Label}";
		}
	}

	private void EditCartPad(CartPadViewModel pad)
	{
		var editor = new CartPadEditorWindow(pad.Config) { Owner = Application.Current?.MainWindow };
		if (editor.ShowDialog() == true)
		{
			pad.Label = pad.Config.Label;
			pad.FilePath = pad.Config.FilePath;
			pad.ColorHex = pad.Config.ColorHex;
			pad.DuckMusic = pad.Config.DuckMusic;
			pad.DuckDb = pad.Config.DuckDb;
			pad.Volume = pad.Config.Volume;
			SaveSettingsSafe();
			ActivityStatus = $"Updated Cart [{pad.Hotkey}] {pad.Label}.";
		}
	}

	private void LoadPlaysetToQueue(Playset? playset, bool replace, bool shuffle)
	{
		if (playset == null)
		{
			ActivityStatus = "Select a playset first.";
			return;
		}
		List<LibraryItem> tracks = _playsetService.ResolvePlaysetTracks(playset, Library.Items, shuffle);
		if (tracks.Count == 0)
		{
			ActivityStatus = $"Playset '{playset.Name}' has no matching tracks.";
			return;
		}

		if (replace)
		{
			Queue.Clear();
			_autoDjQueuedItems.Clear();
			ClearGaplessPreload();
			_activePlaysetTransitionOverride = playset.TransitionModeOverride;
			ApplyLiveAudioSettings();
		}

		foreach (LibraryItem t in tracks) Queue.Add(new QueueDisplayItem(t));
		UpdateQueueTelemetry();
		PersistQueueSafe();
		RefreshGaplessPreload();

		if (!ProgramPlayer.IsPlaying && Queue.Count > 0)
		{
			PlayNextFromQueue();
		}
		ActivityStatus = $"Loaded playset '{playset.Name}' ({tracks.Count} tracks{(shuffle ? ", shuffled" : "")}).";
		RelayCommand.InvalidateAll();
	}

	private void SaveCurrentQueueAsPlayset()
	{
		if (Queue.Count == 0) return;
		var ps = new Playset
		{
			Name = $"Queue Set {DateTime.Now:MMM dd HH:mm}",
			Kind = PlaysetKind.Manual,
			Description = $"Saved from playout queue ({Queue.Count} tracks)"
		};
		var editor = new PlaysetEditorWindow(ps, Library.CategoryOptions.ToList()) { Owner = Application.Current?.MainWindow };
		if (editor.ShowDialog() == true)
		{
			_playsetService.SavePlayset(ps);
			_playsetService.ReplacePlaysetTracks(ps.Id, Queue.Select(q => q.Item.Id).Where(id => id > 0));
			Library.RefreshPlaysets();
			ActivityStatus = $"Saved playset '{ps.Name}' with {Queue.Count} tracks.";
		}
	}

	private void LoadSelectedLibraryItemToProgram()
	{
		if (Library.SelectedItem == null) return;
		if (PlayLibraryItem(Library.SelectedItem, smoothCrossfadeIfPlaying: true))
		{
			ActivityStatus = $"Loaded '{Library.SelectedItem.Title}' to Program Deck.";
		}
	}

	private void EnqueueSelectedLibraryItems()
	{
		IReadOnlyList<LibraryItem> selected = Library.GetEffectiveSelection();
		if (selected.Count == 0) return;
		foreach (LibraryItem item in selected)
		{
			Queue.Add(new QueueDisplayItem(item));
		}
		UpdateQueueTelemetry();
		PersistQueueSafe();
		RefreshGaplessPreload();
		ActivityStatus = selected.Count == 1
			? $"Queued '{selected[0].Title}'."
			: $"Queued {selected.Count} selected tracks.";
		RelayCommand.InvalidateAll();
	}

	private void EnqueueAutomationItem()
	{
		LibraryItem? item = PickNextAutomationItem();
		if (item == null)
		{
			ActivityStatus = "No library tracks available for Auto-DJ.";
			return;
		}
		Queue.Add(new QueueDisplayItem(item));
		if (!item.IsSweeperCue && !item.IsStationIdCue) _autoDjQueuedItems.Add(item);
		UpdateQueueTelemetry();
		PersistQueueSafe();
		RefreshGaplessPreload();
		ActivityStatus = $"Auto-picked '{item.Title}'.";
		RelayCommand.InvalidateAll();
	}

	private void PlayNextFromQueue()
	{
		if (Queue.Count == 0)
		{
			ActivityStatus = "Queue is empty.";
			return;
		}

		// If track is already preloaded and playing, perform an Apple Music-style smooth manual crossfade!
		if (ProgramPlayer.IsPlaying && _preloadedProgramItem != null &&
			string.Equals(Queue[0].Item.FilePath, _preloadedProgramItem.FilePath, StringComparison.OrdinalIgnoreCase) &&
			_audioService.TriggerSmoothSkipToPreloaded())
		{
			ActivityStatus = $"Crossfading to '{_preloadedProgramItem.Title}'...";
			return;
		}

		QueueDisplayItem nextEntry = Queue[0];
		Queue.RemoveAt(0);
		_autoDjQueuedItems.Remove(nextEntry.Item);
		ClearGaplessPreload();

		if (PlayLibraryItem(nextEntry.Item, smoothCrossfadeIfPlaying: ProgramPlayer.IsPlaying))
		{
			EnsureAutoDjQueue();
			UpdateQueueTelemetry();
			PersistQueueSafe();
			RefreshGaplessPreload();
			ActivityStatus = $"Now playing: {nextEntry.Item.Title}";
		}
		RelayCommand.InvalidateAll();
	}

	private void LoadRandomToProgram(bool play)
	{
		LibraryItem? item = Library.GetNextTrack(new[] { "Music", "Song", "Uncategorized" }, currentlyQueued: Queue.Select(q => q.Item));
		if (item == null)
		{
			ActivityStatus = "Import music into your library first.";
			return;
		}
		if (PlayLibraryItem(item, smoothCrossfadeIfPlaying: ProgramPlayer.IsPlaying, autoPlay: play))
		{
			ActivityStatus = $"Loaded random track: {item.Title}";
		}
	}

	private bool PlayLibraryItem(LibraryItem item, bool smoothCrossfadeIfPlaying = false, bool autoPlay = true)
	{
		bool wasPlaying = ProgramPlayer.IsPlaying;
		if (!ProgramPlayer.LoadLibraryItem(item, smoothCrossfadeIfPlaying && wasPlaying))
		{
			return false;
		}
		if (autoPlay && !ProgramPlayer.IsPlaying)
		{
			_audioService.Play();
		}

		_currentProgramItem = item;
		Library.MarkItemPlayed(item);
		ExportNowPlaying(item);

		HandleProgramTrackStarted(item);
		RefreshGaplessPreload();
		UpdateQueueTelemetry();
		return true;
	}

	private void RemoveSelectedQueueItem()
	{
		if (SelectedQueueItem == null) return;
		_autoDjQueuedItems.Remove(SelectedQueueItem.Item);
		Queue.Remove(SelectedQueueItem);
		SelectedQueueItem = null;
		UpdateQueueTelemetry();
		EnsureAutoDjQueue();
		PersistQueueSafe();
		RefreshGaplessPreload();
		RelayCommand.InvalidateAll();
	}

	private void MoveSelectedQueueItem(int delta)
	{
		if (SelectedQueueItem == null) return;
		int idx = Queue.IndexOf(SelectedQueueItem);
		int target = idx + delta;
		if (idx < 0 || target < 0 || target >= Queue.Count) return;
		QueueDisplayItem item = SelectedQueueItem;
		Queue.Move(idx, target);
		SelectedQueueItem = item;
		UpdateQueueTelemetry();
		PersistQueueSafe();
		RefreshGaplessPreload();
		RelayCommand.InvalidateAll();
	}

	private void ClearQueue()
	{
		Queue.Clear();
		_autoDjQueuedItems.Clear();
		SelectedQueueItem = null;
		_activePlaysetTransitionOverride = null;
		ClearGaplessPreload();
		UpdateQueueTelemetry();
		PersistQueueSafe();
		EnsureAutoDjQueue();
		RelayCommand.InvalidateAll();
	}

	private void OnPlaybackUpdated(DeckPlaybackState state, TimeSpan position, TimeSpan duration)
	{
		ProgramPlayer.UpdateProgress(position, duration, state);
	}

	private void OnTelemetryUpdated(AudioTelemetrySnapshot snap)
	{
		MeterLeftDb = snap.PeakLeftDb;
		MeterRightDb = snap.PeakRightDb;
		LimiterGrDb = snap.LimiterGainReductionDb;
		MomentaryLufsText = $"{snap.MomentaryLufs:0.0} LUFS";
		LimiterGrText = snap.LimiterGainReductionDb > 0.1 ? $"-{snap.LimiterGainReductionDb:0.0} dB GR" : "0.0 dB GR";
		IsClipping = snap.IsClipping;
	}

	private void OnTrackEnded()
	{
		EnsureAutoDjQueue();
		if (Queue.Count > 0)
		{
			PlayNextFromQueue();
		}
		else if (AutoScheduleEnabled)
		{
			LibraryItem? item = PickNextAutomationItem();
			if (item != null) PlayLibraryItem(item);
		}
	}

	private void OnPreloadedTrackStarted(string filePath, TransitionPlan? plan)
	{
		LibraryItem? item = _preloadedProgramItem;
		_preloadedProgramItem = null;

		if (Queue.Count > 0 && string.Equals(Queue[0].Item.FilePath, filePath, StringComparison.OrdinalIgnoreCase))
		{
			item ??= Queue[0].Item;
			_autoDjQueuedItems.Remove(Queue[0].Item);
			Queue.RemoveAt(0);
		}

		if (item == null) return;

		_currentProgramItem = item;
		Library.MarkItemPlayed(item);
		ExportNowPlaying(item);
		ProgramPlayer.SetTrackFromAutoMixPreload(item);

		HandleProgramTrackStarted(item);
		EnsureAutoDjQueue();
		UpdateQueueTelemetry();
		PersistQueueSafe();
		RefreshGaplessPreload();

		string transitionNote = plan != null ? $" [{plan.HumanSummary}]" : string.Empty;
		ActivityStatus = $"Now playing: {item.Title}{transitionNote}";
		RelayCommand.InvalidateAll();
	}

	private void OnTrackAnalysisUpdated(LibraryItem updated)
	{
		ProgramPlayer.RefreshCurrentItemAnalysis(updated);
		if (_preloadedProgramItem != null && _preloadedProgramItem.Id == updated.Id)
		{
			RefreshGaplessPreload(forceReload: true);
		}
		UpdateTransitionPreviewText();
	}

	private void OnClockTick(object? sender, EventArgs e)
	{
		DateTime now = DateTime.Now;
		WallClockText = now.ToString("HH:mm:ss");
		DateTime nextHour = new DateTime(now.Year, now.Month, now.Day, now.Hour, 0, 0).AddHours(1);
		TimeSpan untilHour = nextHour - now;
		TopOfHourCountdownText = $"T-{untilHour:mm\\:ss}";

		IsStreamLive = _streamingService.IsConnected;
		if (_streamingService.IsConnected)
		{
			StreamStatus = _streamingService.GetConnectionSummary();
			CheckStreamHealth();
		}

		CheckDeadAirWatchdog();
		EnforceStationIdPolicy(now, allowLiveInterruptOrOverlay: true);
		CheckTopOfHourSync(now);
		RecalculateProjectedQueueAirtimes(now);
	}

	private void CheckDeadAirWatchdog()
	{
		if (!SilenceWatchdogEnabled || !ProgramPlayer.IsPlaying || _currentProgramItem == null)
		{
			_programStallDuration = TimeSpan.Zero;
			_lastProgramPosition = TimeSpan.Zero;
			return;
		}

		int timeoutSec = Math.Max(3, SilenceWatchdogTimeoutSeconds);

		// 1. Real Audio-Level Silence Check (catches corrupted silent audio files or muted decoder failures!)
		bool audioDeadAir = _audioService.LatestTelemetry.ConsecutiveSilenceSeconds >= timeoutSec;

		// 2. Transport Stall Check (catches frozen wave device/stream)
		TimeSpan pos = ProgramPlayer.Position;
		if (pos == _lastProgramPosition && ProgramPlayer.Duration > TimeSpan.FromSeconds(5))
		{
			_programStallDuration += TimeSpan.FromSeconds(1);
		}
		else
		{
			_lastProgramPosition = pos;
			_programStallDuration = TimeSpan.Zero;
		}
		bool transportStalled = _programStallDuration.TotalSeconds >= timeoutSec;

		if (audioDeadAir || transportStalled)
		{
			_programStallDuration = TimeSpan.Zero;
			string cause = audioDeadAir ? "audio silence detected" : "transport stall detected";
			LogService.Warn("Watchdog", $"Dead-air recovery triggered ({cause}) on '{_currentProgramItem.Title}'");
			ActivityStatus = $"Dead-air watchdog: {cause} — auto-recovering!";

			if (Queue.Count > 0)
			{
				PlayNextFromQueue();
			}
			else if (EmergencyFallbackPlaysetId.HasValue)
			{
				Playset? fallbackPs = Library.Playsets.FirstOrDefault(p => p.Id == EmergencyFallbackPlaysetId.Value);
				if (fallbackPs != null) LoadPlaysetToQueue(fallbackPs, replace: true, shuffle: true);
			}
			else if (AutoScheduleEnabled)
			{
				LibraryItem? next = PickNextAutomationItem();
				if (next != null) PlayLibraryItem(next);
			}
		}
	}

	private void CheckTopOfHourSync(DateTime localNow)
	{
		if (!TopOfHourSyncEnabled) return;
		DateTime target = new DateTime(localNow.Year, localNow.Month, localNow.Day, localNow.Hour, Math.Clamp(TopOfHourTargetMinute, 0, 59), 0);
		if (Math.Abs((localNow - target).TotalSeconds) <= 2.0 && _lastTopOfHourSyncHandled != target)
		{
			_lastTopOfHourSyncHandled = target;
			DateTime targetHourBucket = new DateTime(target.Year, target.Month, target.Day, target.Hour, 0, 0);
			if (RadioIdsEnabled && _lastRadioIdHour != targetHourBucket)
			{
				LibraryItem? id = PickEligibleRadioId();
				if (id != null)
				{
					if (RadioIdEnforcementMode == StationIdEnforcementMode.OverlayAtTarget && ProgramPlayer.IsPlaying)
					{
						_audioService.TriggerOverlay(id.FilePath, true, RadioIdOverlayDuckDb, 1.0);
						MarkRadioIdServed(target);
						ActivityStatus = $"Top-of-Hour Sync (:{TopOfHourTargetMinute:00}) fired overlay Station ID '{id.Title}'.";
					}
					else
					{
						Queue.Insert(0, new QueueDisplayItem(id));
						RefreshGaplessPreload(forceReload: true);
						if (ProgramPlayer.IsPlaying)
						{
							PlayNextFromQueue();
						}
						MarkRadioIdServed(target);
						ActivityStatus = $"Top-of-Hour Sync (:{TopOfHourTargetMinute:00}) fired Station ID '{id.Title}'.";
					}
				}
			}
		}
	}

	private void OnScheduleDue(ScheduleItem item)
	{
		if (item.EventType == ScheduleEventType.Playset && item.PlaysetId.HasValue)
		{
			Playset? ps = Library.Playsets.FirstOrDefault(p => p.Id == item.PlaysetId.Value);
			if (ps != null)
			{
				LoadPlaysetToQueue(ps, replace: true, shuffle: false);
				ActivityStatus = $"Scheduled playset '{ps.Name}' started.";
				return;
			}
		}

		if (item.IsFolderSource || (item.EventType == ScheduleEventType.Playlist && Directory.Exists(item.FilePath)))
		{
			BuildScheduledFolderBlock(item);
			return;
		}

		if (string.IsNullOrWhiteSpace(item.FilePath)) return;
		var scheduledItem = new LibraryItem
		{
			FilePath = item.FilePath,
			Title = string.IsNullOrWhiteSpace(item.Title) ? item.EventType.ToString() : item.Title,
			Category = item.EventType.ToString(),
			DateAdded = DateTime.UtcNow
		};

		if (item.IsMain)
		{
			PlayLibraryItem(scheduledItem, smoothCrossfadeIfPlaying: ProgramPlayer.IsPlaying);
		}
		else
		{
			Queue.Insert(0, new QueueDisplayItem(scheduledItem));
			UpdateQueueTelemetry();
			if (!ProgramPlayer.IsPlaying) PlayNextFromQueue();
			else RefreshGaplessPreload(forceReload: true);
		}
	}

	private void BuildScheduledFolderBlock(ScheduleItem item)
	{
		string normFolder = Path.GetFullPath(item.FilePath).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
		List<LibraryItem> tracks = Library.Items
			.Where(i => !i.IsSweeperCue && !i.IsStationIdCue &&
						i.FilePath.StartsWith(normFolder, StringComparison.OrdinalIgnoreCase))
			.OrderBy(i => i.LastPlayedAtUtc ?? DateTime.MinValue)
			.ThenBy(i => i.PlayCount)
			.ThenBy(_ => Random.Shared.Next())
			.ToList();

		if (tracks.Count == 0)
		{
			ActivityStatus = "Scheduled folder contains no imported tracks.";
			return;
		}

		Queue.Clear();
		_autoDjQueuedItems.Clear();
		ClearGaplessPreload();

		TimeSpan target = TimeSpan.FromMinutes(Math.Max(1, item.BlockDurationMinutes));
		TimeSpan coverage = TimeSpan.Zero;
		int idx = 0;
		while (coverage < target && idx < 400)
		{
			LibraryItem t = tracks[idx % tracks.Count];
			Queue.Add(new QueueDisplayItem(t));
			coverage += t.Duration > TimeSpan.Zero ? t.Duration : TimeSpan.FromMinutes(3);
			idx++;
		}
		UpdateQueueTelemetry();
		PersistQueueSafe();
		RefreshGaplessPreload();
		if (!ProgramPlayer.IsPlaying && Queue.Count > 0) PlayNextFromQueue();
		ActivityStatus = $"Scheduled folder loaded {Queue.Count} tracks ({coverage.TotalMinutes:0} min).";
	}

	private void RefreshGaplessPreload(bool forceReload = false)
	{
		if (!GaplessPlaybackEnabled || !ProgramPlayer.IsPlaying || Queue.Count == 0)
		{
			ClearGaplessPreload();
			UpdateTransitionPreviewText();
			return;
		}

		LibraryItem next = Queue[0].Item;
		if (forceReload || _preloadedProgramItem == null || !string.Equals(_preloadedProgramItem.FilePath, next.FilePath, StringComparison.OrdinalIgnoreCase))
		{
			if (_audioService.PreloadNext(next))
			{
				_preloadedProgramItem = next;
			}
		}
		UpdateTransitionPreviewText();
		UpdateAutomationCues();
	}

	private void ClearGaplessPreload()
	{
		_preloadedProgramItem = null;
		_audioService.ClearPreloadedNext();
	}

	private void UpdateTransitionPreviewText()
	{
		TransitionPlan? plan = _audioService.PlannedNextTransition;
		if (plan != null)
		{
			ProgramPlayer.NextTransitionSummary = plan.HumanSummary;
		}
		else if (!GaplessPlaybackEnabled)
		{
			ProgramPlayer.NextTransitionSummary = "Gapless Off";
		}
		else
		{
			ProgramPlayer.NextTransitionSummary = $"{TransitionMode} Ready";
		}
	}

	private void UpdateQueueTelemetry()
	{
		if (!_isUpdatingQueue)
		{
			_isUpdatingQueue = true;
			try
			{
				TryInsertProjectedRadioId();
			}
			finally
			{
				_isUpdatingQueue = false;
			}
		}

		QueueStatus = Queue.Count == 0 ? "Queue empty" : $"{Queue.Count} queued";
		TimeSpan total = Queue.Aggregate(TimeSpan.Zero, (acc, q) => acc + (q.Item.Duration > TimeSpan.Zero ? q.Item.Duration : TimeSpan.FromMinutes(3)));
		QueueTotalDurationText = total.TotalHours >= 1.0 ? total.ToString("h\\:mm\\:ss") : total.ToString("m\\:ss");

		for (int i = 0; i < Queue.Count; i++)
		{
			Queue[i].IsPreloadedNext = i == 0 && _preloadedProgramItem != null;
		}

		RecalculateProjectedQueueAirtimes(DateTime.Now);
		UpdateAutomationCues();
	}

	private void RecalculateProjectedQueueAirtimes(DateTime now)
	{
		TimeSpan remaining = ProgramPlayer.IsPlaying && ProgramPlayer.Duration > ProgramPlayer.Position
			? ProgramPlayer.Duration - ProgramPlayer.Position
			: TimeSpan.Zero;
		DateTime cursor = now + remaining;
		foreach (QueueDisplayItem entry in Queue)
		{
			entry.ProjectedStartText = cursor.ToString("HH:mm:ss");
			cursor += entry.Item.Duration > TimeSpan.Zero ? entry.Item.Duration : TimeSpan.FromMinutes(3);
		}
	}

	private void UpdateAutomationCues()
	{
		LibraryItem? next = _preloadedProgramItem ?? Queue.FirstOrDefault()?.Item;
		NextQueueCue = next == null ? "Up Next: none" : $"{next.Title} ({(_preloadedProgramItem != null ? "preloaded" : "queued")})";

		StationIdDecision idDecision = EvaluateCurrentStationIdDecision(DateTime.Now);
		RadioIdWindowStatusText = idDecision.StatusSummary;
		NextRadioIdCue = !RadioIdsEnabled
			? "ID off"
			: Queue.Any(q => IsRadioId(q.Item, RadioIdFolder))
				? $"ID queued (:{RadioIdMinute:00})"
				: idDecision.AlreadyServedThisHour
					? $"ID aired • next {idDecision.TargetTimeLocal:HH:mm}"
					: $"ID {idDecision.TargetTimeLocal:HH:mm} ({RadioIdEnforcementMode})";

		string swpModeTag = SweeperPlaybackMode == SweeperPlaybackMode.OverlayOverIntro
			? "overlay"
			: SweeperPlaybackMode == SweeperPlaybackMode.SmartAuto
				? "auto"
				: "queue";
		NextSweeperCue = !SweepersEnabled
			? "Sweepers off"
			: _pendingOverlaySweeper != null
				? $"Sweeper overlay ready ({_pendingOverlaySweeper.Title})"
				: $"Next in {Math.Max(0, _songsUntilSweeper - _songsSinceSweeper)} song(s) [{swpModeTag}]";

		RotationCue = GetActiveRotationNow().Description;
	}

	private void EnsureAutoDjQueue()
	{
		if (!AutoScheduleEnabled) return;
		int added = 0;
		int guard = 0;
		while (ShouldAutoDjAddTrack() && guard < 60)
		{
			LibraryItem? item = PickNextAutomationItem();
			if (item == null) break;
			Queue.Add(new QueueDisplayItem(item));
			if (!ImagingPolicyService.IsImagingOrCommercial(item, _settings))
			{
				_autoDjQueuedItems.Add(item);
			}
			added++;
			guard++;
		}
		if (added > 0)
		{
			UpdateQueueTelemetry();
			PersistQueueSafe();
			RefreshGaplessPreload();
			RelayCommand.InvalidateAll();
		}
	}

	private bool ShouldAutoDjAddTrack()
	{
		if (Queue.Any(q => IsRadioId(q.Item, RadioIdFolder))) return false;
		if (!AutoDjFillCurrentHour) return Queue.Count < AutoDjQueueDepth;

		DateTime now = DateTime.Now;
		TimeSpan remHour = new DateTime(now.Year, now.Month, now.Day, now.Hour, 0, 0).AddHours(1) - now;
		TimeSpan curRem = ProgramPlayer.IsPlaying && ProgramPlayer.Duration > ProgramPlayer.Position ? ProgramPlayer.Duration - ProgramPlayer.Position : TimeSpan.Zero;
		TimeSpan queueDur = Queue.Aggregate(TimeSpan.Zero, (acc, q) => acc + (q.Item.Duration > TimeSpan.Zero ? q.Item.Duration : TimeSpan.FromMinutes(3)));
		return (curRem + queueDur) < remHour - TimeSpan.FromSeconds(30) && Queue.Count < 35;
	}

	private LibraryItem? PickNextAutomationItem()
	{
		IEnumerable<LibraryItem> queued = Queue.Select(q => q.Item);
		if (AutoDjCommercialIntervalSongs > 0)
		{
			_songsSinceCommercial++;
			if (_songsSinceCommercial >= AutoDjCommercialIntervalSongs)
			{
				LibraryItem? ad = Library.GetNextTrack(new[] { "Commercial", "Commercials", "Ad", "Ads" }, currentlyQueued: queued);
				if (ad != null)
				{
					_songsSinceCommercial = 0;
					return ad;
				}
			}
		}

		ActiveRotationInfo active = GetActiveRotationNow();
		LibraryItem? picked = Library.GetNextTrackForRotation(active, queued);
		return picked ?? Library.GetNextTrack(new[] { "Music", "Song", "Uncategorized" }, currentlyQueued: queued);
	}

	/// <summary>
	/// Single source of truth called strictly ONCE when a track begins playing on the program deck.
	/// Eliminates sweeper double-counting, executes intro overlays cleanly, enforces cooldowns &amp;
	/// adjacent-imaging spacing, and records Station ID service accurately.
	/// </summary>
	private void HandleProgramTrackStarted(LibraryItem item)
	{
		if (IsRadioId(item, RadioIdFolder))
		{
			MarkRadioIdServed();
			_pendingOverlaySweeper = null;
			return;
		}

		if (ImagingPolicyService.IsImagingOrCommercial(item, _settings))
		{
			if (ImagingPolicyService.MatchesFolderOrCategory(item, SweeperFolder, ImagingPolicyService.ParseCategoryList(SweeperCategoryNames, "Sweeper", "Sweep", "Jingle")))
			{
				_songsSinceSweeper = 0;
				_lastSweeperPlayedUtc = DateTime.UtcNow;
				_songsUntilSweeper = PickSweeperInterval();
			}
			return;
		}

		// Regular music track started: if an overlay sweeper was armed for this incoming intro, fire it now!
		if (_pendingOverlaySweeper != null)
		{
			LibraryItem overlaySwp = _pendingOverlaySweeper;
			_pendingOverlaySweeper = null;
			if (_audioService.TriggerOverlay(overlaySwp.FilePath, true, SweeperOverlayDuckDb, 1.0))
			{
				_songsSinceSweeper = 0;
				_lastSweeperPlayedUtc = DateTime.UtcNow;
				_songsUntilSweeper = PickSweeperInterval();
				ActivityStatus = $"Sweeper overlay: '{overlaySwp.Title}' over '{item.Title}' ({SweeperOverlayDuckDb:0.0} dB duck).";
				return;
			}
		}

		if (!SweepersEnabled) return;

		_songsSinceSweeper++;
		LibraryItem? nextQueued = Queue.FirstOrDefault()?.Item;
		StationIdDecision idDecision = EvaluateCurrentStationIdDecision(DateTime.Now);
		bool stationIdDueSoon = RadioIdsEnabled &&
								!idDecision.AlreadyServedThisHour &&
								(idDecision.Action != StationIdActionKind.None ||
								 Math.Abs((idDecision.WindowStartLocal - DateTime.Now).TotalMinutes) <= 2.5);

		if (!ImagingPolicyService.ShouldTriggerSweeper(
			_songsSinceSweeper,
			_songsUntilSweeper,
			DateTime.UtcNow,
			_lastSweeperPlayedUtc,
			_settings,
			item,
			nextQueued,
			stationIdDueSoon))
		{
			return;
		}

		LibraryItem? swp = PickEligibleSweeper();
		if (swp == null) return;

		if (ImagingPolicyService.ShouldPlaySweeperAsOverlay(swp, nextQueued, _settings))
		{
			_pendingOverlaySweeper = swp;
			LogService.Info("Imaging", $"Armed intro-overlay sweeper '{swp.Title}' for upcoming track '{nextQueued?.Title}'");
		}
		else
		{
			_songsSinceSweeper = 0;
			_lastSweeperPlayedUtc = DateTime.UtcNow;
			_songsUntilSweeper = PickSweeperInterval();
			Queue.Insert(0, new QueueDisplayItem(swp));
			LogService.Info("Imaging", $"Queued sweeper '{swp.Title}' ({swp.Duration.TotalSeconds:0.0}s) next");
		}
	}

	private StationIdDecision EvaluateCurrentStationIdDecision(DateTime localNow)
	{
		TimeSpan curRem = ProgramPlayer.IsPlaying && ProgramPlayer.Duration > ProgramPlayer.Position
			? ProgramPlayer.Duration - ProgramPlayer.Position
			: TimeSpan.Zero;
		DateTime? lastServed = _lastRadioIdHour == DateTime.MinValue ? null : _lastRadioIdHour;
		return ImagingPolicyService.EvaluateStationId(
			localNow,
			lastServed,
			_settings,
			curRem,
			Queue.Select(q => q.Item).ToList(),
			item => IsRadioId(item, RadioIdFolder));
	}

	private bool TryInsertProjectedRadioId() => EnforceStationIdPolicy(DateTime.Now, allowLiveInterruptOrOverlay: false);

	private bool EnforceStationIdPolicy(DateTime localNow, bool allowLiveInterruptOrOverlay)
	{
		StationIdDecision decision = EvaluateCurrentStationIdDecision(localNow);
		RadioIdWindowStatusText = decision.StatusSummary;

		if (!RadioIdsEnabled || decision.Action == StationIdActionKind.None)
		{
			return false;
		}

		if (decision.Action == StationIdActionKind.InsertIntoQueue)
		{
			LibraryItem? id = PickEligibleRadioId();
			if (id == null) return false;
			int idx = Math.Clamp(decision.InsertIndex, 0, Queue.Count);
			Queue.Insert(idx, new QueueDisplayItem(id));
			if (idx == 0) RefreshGaplessPreload(forceReload: true);
			LogService.Info("Imaging", $"Scheduled Station ID '{id.Title}' at queue slot #{idx + 1} for {decision.TargetTimeLocal:HH:mm} window.");
			return true;
		}

		if (!allowLiveInterruptOrOverlay) return false;

		if (decision.Action == StationIdActionKind.FireOverlayNow)
		{
			LibraryItem? id = PickEligibleRadioId();
			if (id == null) return false;
			MarkRadioIdServed(decision.TargetTimeLocal);
			if (ProgramPlayer.IsPlaying)
			{
				_audioService.TriggerOverlay(id.FilePath, true, RadioIdOverlayDuckDb, 1.0);
				ActivityStatus = $"Station ID overlay aired: '{id.Title}' ({RadioIdOverlayDuckDb:0.0} dB duck).";
			}
			else
			{
				PlayLibraryItem(id, smoothCrossfadeIfPlaying: false, autoPlay: true);
			}
			UpdateAutomationCues();
			return true;
		}

		if (decision.Action == StationIdActionKind.FireHardInterruptNow)
		{
			LibraryItem? id = PickEligibleRadioId();
			if (id == null) return false;
			MarkRadioIdServed(decision.TargetTimeLocal);

			// Remove any already-queued Station ID further down so it doesn't double-play
			for (int i = Queue.Count - 1; i >= 0; i--)
			{
				if (IsRadioId(Queue[i].Item, RadioIdFolder)) Queue.RemoveAt(i);
			}

			Queue.Insert(0, new QueueDisplayItem(id));
			RefreshGaplessPreload(forceReload: true);
			if (ProgramPlayer.IsPlaying)
			{
				PlayNextFromQueue();
			}
			ActivityStatus = $"Station ID deadline enforced ({decision.WindowEndLocal:HH:mm}): airing '{id.Title}'.";
			LogService.Info("Imaging", $"Hard-deadline enforced Station ID '{id.Title}' at {localNow:HH:mm:ss}.");
			return true;
		}

		return false;
	}

	private LibraryItem? PickEligibleSweeper() =>
		Library.GetRandomSweeper(
			SweeperFolder,
			TimeSpan.FromSeconds(SweeperMinDurationSeconds),
			TimeSpan.FromSeconds(SweeperMaxDurationSeconds));

	private LibraryItem? PickEligibleRadioId() =>
		Library.GetRandomRadioId(
			RadioIdFolder,
			TimeSpan.FromSeconds(RadioIdMinDurationSeconds),
			TimeSpan.FromSeconds(RadioIdMaxDurationSeconds));

	private void MaintainAutoDjHour()
	{
		UpdateAutomationCues();
		RotationTimelineRevision++;
		if (!AutoScheduleEnabled) return;

		DateTime currentHour = GetCurrentHour();
		ActiveRotationInfo activeNow = GetActiveRotationNow();
		string activeKey = BuildRotationKey(activeNow);

		if (currentHour != _lastAutoDjHour || !string.Equals(activeKey, _lastActiveRotationKey, StringComparison.OrdinalIgnoreCase))
		{
			_lastAutoDjHour = currentHour;
			if (!string.Equals(activeKey, _lastActiveRotationKey, StringComparison.OrdinalIgnoreCase))
			{
				_lastActiveRotationKey = activeKey;
				for (int i = Queue.Count - 1; i >= 0; i--)
				{
					if (_autoDjQueuedItems.Remove(Queue[i].Item)) Queue.RemoveAt(i);
				}
				ClearGaplessPreload();
				ActivityStatus = $"Switched active daypart — {activeNow.Description}";
			}
		}
		EnsureAutoDjQueue();
	}

	private void ExportNowPlaying(LibraryItem item)
	{
		if (item.IsSweeperCue || item.IsStationIdCue) return;
		string formatted = FormatTrackTemplate(NowPlayingFormat, item);

		// 1. Push Live Track Metadata to Icecast / SHOUTcast!
		if (_streamingService.IsConnected && StreamConfig.PushTrackMetadata)
		{
			string streamMeta = FormatTrackTemplate(StreamConfig.MetadataFormat, item);
			_ = _streamingService.UpdateMetadataAsync(StreamConfig, streamMeta);
		}

		// 2. Write Local Now Playing Text File
		if (NowPlayingFileExportEnabled)
		{
			Task.Run(() =>
			{
				try
				{
					string path = string.IsNullOrWhiteSpace(NowPlayingFilePath)
						? Path.Combine(RadioLibraryContext.GetDefaultAppDataFolder(), "nowplaying.txt")
						: NowPlayingFilePath;
					string? dir = Path.GetDirectoryName(path);
					if (!string.IsNullOrWhiteSpace(dir)) Directory.CreateDirectory(dir);
					File.WriteAllText(path, formatted);
				}
				catch { }
			});
		}
	}

	private static string FormatTrackTemplate(string? template, LibraryItem item)
	{
		string fmt = string.IsNullOrWhiteSpace(template) ? "{artist} - {title}" : template;
		return fmt
			.Replace("{artist}", item.Artist ?? string.Empty, StringComparison.OrdinalIgnoreCase)
			.Replace("{title}", item.Title ?? string.Empty, StringComparison.OrdinalIgnoreCase)
			.Replace("{album}", item.Album ?? string.Empty, StringComparison.OrdinalIgnoreCase)
			.Replace("{category}", item.Category ?? string.Empty, StringComparison.OrdinalIgnoreCase)
			.Replace("{duration}", item.DurationText ?? string.Empty, StringComparison.OrdinalIgnoreCase)
			.Trim();
	}

	private async Task ConnectStreamAsync(bool scheduleRetryOnFailure)
	{
		if (_isConnectingStream || _disposed || !_streamConnectionDesired) return;
		_isConnectingStream = true;
		try
		{
			NormalizeStreamConfig();
			StreamStatus = "Connecting...";
			if (!await _streamingService.ConnectAsync(StreamConfig))
			{
				StreamStatus = "Connection rejected";
				if (scheduleRetryOnFailure) ScheduleStreamReconnect("rejected");
				return;
			}
			_streamingService.StartProgramOutputStream();
			_streamConnectedAtUtc = DateTimeOffset.UtcNow;
			_streamStallRecoveryStarted = false;
			IsStreamLive = true;
			StreamStatus = _streamingService.GetConnectionSummary();

			if (_currentProgramItem != null && StreamConfig.PushTrackMetadata)
			{
				_ = _streamingService.UpdateMetadataAsync(StreamConfig, FormatTrackTemplate(StreamConfig.MetadataFormat, _currentProgramItem));
			}
		}
		catch (Exception ex)
		{
			IsStreamLive = false;
			StreamStatus = $"Disconnected — {ex.Message}";
			if (scheduleRetryOnFailure) ScheduleStreamReconnect(ex.Message);
		}
		finally
		{
			_isConnectingStream = false;
		}
	}

	private void DisconnectStream()
	{
		_streamConnectionDesired = false;
		CancelStreamReconnect();
		_streamingService.Disconnect();
		_streamConnectedAtUtc = null;
		_streamStallRecoveryStarted = false;
		IsStreamLive = false;
		StreamStatus = "Disconnected";
	}

	private async Task TestStreamConnectionAsync()
	{
		NormalizeStreamConfig();
		StreamTestStatus = "Testing connection...";
		var (ok, message) = await StreamingService.TestConnectionAsync(StreamConfig);
		StreamTestStatus = (ok ? "PASS: " : "FAIL: ") + message;
	}

	private void OnStreamFailed(string message)
	{
		Application.Current?.Dispatcher.BeginInvoke(() =>
		{
			IsStreamLive = false;
			if (!_streamConnectionDesired)
			{
				StreamStatus = "Disconnected";
				return;
			}
			StreamStatus = $"Stream dropped: {message}";
			if (StreamConfig.AutoReconnectEnabled) ScheduleStreamReconnect(message);
		});
	}

	private void CheckStreamHealth()
	{
		if (!_streamConnectionDesired || !_streamingService.IsConnected) return;
		DateTimeOffset now = DateTimeOffset.UtcNow;
		DateTimeOffset? lastWrite = _streamingService.LastSuccessfulWriteUtc;

		if (_streamReconnectAttempt > 0 && _streamConnectedAtUtc.HasValue && lastWrite.HasValue &&
			now - _streamConnectedAtUtc.Value >= TimeSpan.FromSeconds(30) &&
			now - lastWrite.Value < TimeSpan.FromSeconds(5))
		{
			_streamReconnectAttempt = 0;
		}

		bool stalled = (!lastWrite.HasValue && _streamConnectedAtUtc.HasValue && now - _streamConnectedAtUtc.Value >= TimeSpan.FromSeconds(20)) ||
					   (lastWrite.HasValue && now - lastWrite.Value >= TimeSpan.FromSeconds(20));

		if (!_streamStallRecoveryStarted && stalled)
		{
			_streamStallRecoveryStarted = true;
			_streamingService.Disconnect();
			IsStreamLive = false;
			StreamStatus = "Stream stalled — reconnecting...";
			if (StreamConfig.AutoReconnectEnabled) ScheduleStreamReconnect("write stall > 20s");
		}
	}

	private async void ScheduleStreamReconnect(string reason)
	{
		if (!_streamConnectionDesired || _disposed || _streamReconnectScheduled) return;
		_streamReconnectScheduled = true;
		int attempt = ++_streamReconnectAttempt;
		int[] delays = { 2, 5, 10, 20, 30 };
		int delaySec = delays[Math.Min(attempt - 1, delays.Length - 1)];

		var cts = new CancellationTokenSource();
		_streamReconnectCancellation = cts;
		StreamStatus = $"Reconnecting in {delaySec}s (attempt {attempt})...";
		try
		{
			await Task.Delay(TimeSpan.FromSeconds(delaySec), cts.Token);
			if (!_streamConnectionDesired || _disposed) return;
			_streamReconnectScheduled = false;
			await ConnectStreamAsync(scheduleRetryOnFailure: true);
		}
		catch (OperationCanceledException) { }
		finally
		{
			if (ReferenceEquals(_streamReconnectCancellation, cts))
			{
				_streamReconnectCancellation = null;
				_streamReconnectScheduled = false;
			}
			cts.Dispose();
		}
	}

	private void CancelStreamReconnect()
	{
		_streamReconnectCancellation?.Cancel();
		_streamReconnectCancellation = null;
		_streamReconnectScheduled = false;
		_streamReconnectAttempt = 0;
	}

	private void PersistHourlyRules()
	{
		_settings.AutoDjHourlyTagRules = RotationService.SerializeHourlyTagRules(HourlyTagRules);
		SaveSettingsSafe();
		UpdateAutomationCues();
		ActivityStatus = "Saved hourly rotation tag schedule.";
	}

	private void BackupDatabase()
	{
		var dialog = new SaveFileDialog
		{
			Title = "Backup RadioApp SQLite Database",
			Filter = "SQLite Database (*.db)|*.db",
			FileName = $"radioapp-backup-{DateTime.Now:yyyyMMdd-HHmm}.db"
		};
		if (dialog.ShowDialog() != true) return;
		try
		{
			using RadioLibraryContext ctx = _dbFactory.CreateContext();
			string saved = ctx.CreateBackup(dialog.FileName);
			ActivityStatus = $"Database backed up to {Path.GetFileName(saved)}.";
		}
		catch (Exception ex)
		{
			ActivityStatus = $"Database backup failed: {ex.Message}";
		}
	}

	private void VerifyDatabase()
	{
		using RadioLibraryContext ctx = _dbFactory.CreateContext();
		var (_, msg) = ctx.VerifyIntegrity();
		ActivityStatus = msg;
		MessageBox.Show(Application.Current?.MainWindow, msg, "Database Integrity Check", MessageBoxButton.OK, MessageBoxImage.Information);
	}

	private void OpenLogsFolder()
	{
		try
		{
			Process.Start(new ProcessStartInfo { FileName = LogService.LogFolder, UseShellExecute = true });
		}
		catch (Exception ex)
		{
			ActivityStatus = $"Could not open logs folder: {ex.Message}";
		}
	}

	private void ExportSettingsBackup()
	{
		var dialog = new SaveFileDialog
		{
			Title = "Export RadioApp Settings Backup",
			Filter = "JSON Settings (*.json)|*.json",
			FileName = $"radioapp-settings-{DateTime.Now:yyyyMMdd}.json"
		};
		if (dialog.ShowDialog() != true) return;
		_settingsService.ExportBackup(_settings, dialog.FileName);
		ActivityStatus = $"Exported settings backup (passwords excluded for safety).";
	}

	private void ImportSettingsBackup()
	{
		var dialog = new OpenFileDialog
		{
			Title = "Import RadioApp Settings Backup",
			Filter = "JSON Settings (*.json)|*.json"
		};
		if (dialog.ShowDialog() != true) return;
		AppSettings? imported = _settingsService.ImportBackup(dialog.FileName);
		if (imported == null)
		{
			ActivityStatus = "Invalid settings backup file.";
			return;
		}
		CopyIntoSettings(imported);
		HydrateFromSettings();
		ApplyLiveAudioSettings();
		Library.ReloadMonitoringSettings();
		SaveSettingsSafe();
		NotifyAllPropertiesChanged();
		ActivityStatus = "Imported settings backup successfully.";
	}

	private void ClearLibrary()
	{
		if (MessageBox.Show(Application.Current?.MainWindow,
			"Clear every item from the library?\nAudio files on disk will NOT be deleted.",
			"Clear Library", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
		{
			return;
		}
		Queue.Clear();
		_autoDjQueuedItems.Clear();
		ClearGaplessPreload();
		Library.ClearAll();
		PersistQueueSafe();
		UpdateQueueTelemetry();
		ActivityStatus = "Library cleared.";
	}

	private void ResetApp()
	{
		if (MessageBox.Show(Application.Current?.MainWindow,
			"Factory reset ALL settings, stream credentials, cartwall pads, automation rules, queue, and library?\nAudio files on disk will NOT be deleted.",
			"Factory Reset RadioApp Studio", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
		{
			return;
		}
		DisconnectStream();
		_audioService.Stop();
		_audioService.StopAllOverlays();
		_audioService.StopMicTalkOver();
		Queue.Clear();
		_autoDjQueuedItems.Clear();
		ClearGaplessPreload();
		Library.ClearAll();

		CopyIntoSettings(new AppSettings());
		HydrateFromSettings();
		ApplyLiveAudioSettings();
		Library.ReloadMonitoringSettings();
		SaveSettingsSafe();
		PersistQueueSafe();
		NotifyAllPropertiesChanged();
		UpdateQueueTelemetry();
		ActivityStatus = "Factory reset complete — all defaults restored.";
	}

	private void CopyIntoSettings(AppSettings source)
	{
		AppSettings clone = source.DeepClone();
		foreach (var prop in typeof(AppSettings).GetProperties())
		{
			if (prop.CanRead && prop.CanWrite)
			{
				prop.SetValue(_settings, prop.GetValue(clone));
			}
		}
	}

	private void HydrateFromSettings()
	{
		_localMonitorVolume = Math.Clamp(_settings.LocalMonitorVolume, 0.0, 1.0);
		_localMonitorMuted = _settings.LocalMonitorMuted;
		_broadcastMasterGainDb = Math.Clamp(_settings.BroadcastMasterGainDb, -18.0, 12.0);
		_micDuckDb = Math.Clamp(_settings.MicDuckDb, -24.0, -3.0);

		StreamConfig.ServerType = _settings.StreamServerType;
		StreamConfig.Host = string.IsNullOrWhiteSpace(_settings.StreamHost) ? "localhost" : _settings.StreamHost;
		StreamConfig.Port = _settings.StreamPort <= 0 ? 8000 : _settings.StreamPort;
		StreamConfig.Mount = _settings.StreamMount;
		StreamConfig.Username = string.IsNullOrWhiteSpace(_settings.StreamUsername) ? "source" : _settings.StreamUsername;
		StreamConfig.Password = _settings.StreamPassword;
		StreamConfig.Bitrate = _settings.StreamBitrate <= 0 ? 128 : _settings.StreamBitrate;
		StreamConfig.ShoutcastStreamId = Math.Max(0, _settings.StreamShoutcastStreamId);
		StreamConfig.StationName = _settings.StreamStationName;
		StreamConfig.StationGenre = _settings.StreamStationGenre;
		StreamConfig.StationUrl = _settings.StreamStationUrl;
		StreamConfig.StationDescription = _settings.StreamStationDescription;
		StreamConfig.PushTrackMetadata = _settings.StreamPushMetadata;
		StreamConfig.MetadataFormat = _settings.StreamMetadataFormat;
		StreamConfig.AutoReconnectEnabled = _settings.StreamAutoReconnect;

		CartPads.Clear();
		foreach (CartPadConfig cfg in _settings.CartPads.OrderBy(c => c.SlotIndex))
		{
			CartPads.Add(new CartPadViewModel(cfg));
		}

		HourlyTagRules.Clear();
		foreach (HourlyTagRuleItem rule in RotationService.ParseHourlyTagRules(_settings.AutoDjHourlyTagRules))
		{
			HourlyTagRules.Add(rule);
		}

		LoadRotationBlocksFromSettings();
	}

	private void OnStreamConfigPropertyChanged(object? sender, PropertyChangedEventArgs e)
	{
		NormalizeStreamConfig();
		_settings.StreamServerType = StreamConfig.ServerType;
		_settings.StreamHost = StreamConfig.Host;
		_settings.StreamPort = StreamConfig.Port;
		_settings.StreamMount = StreamConfig.Mount;
		_settings.StreamUsername = StreamConfig.Username;
		_settings.StreamPassword = StreamConfig.Password;
		_settings.StreamBitrate = StreamConfig.Bitrate;
		_settings.StreamShoutcastStreamId = StreamConfig.ShoutcastStreamId;
		_settings.StreamStationName = StreamConfig.StationName;
		_settings.StreamStationGenre = StreamConfig.StationGenre;
		_settings.StreamStationUrl = StreamConfig.StationUrl;
		_settings.StreamStationDescription = StreamConfig.StationDescription;
		_settings.StreamPushMetadata = StreamConfig.PushTrackMetadata;
		_settings.StreamMetadataFormat = StreamConfig.MetadataFormat;
		_settings.StreamAutoReconnect = StreamConfig.AutoReconnectEnabled;
		SaveSettingsSafe();
		OnPropertyChanged(nameof(StreamEndpointText));
	}

	private void NormalizeStreamConfig()
	{
		if (string.IsNullOrWhiteSpace(StreamConfig.Host)) StreamConfig.Host = "localhost";
		if (StreamConfig.Port <= 0) StreamConfig.Port = 8000;
		if (StreamConfig.ServerType == StreamServerType.Icecast && (string.IsNullOrWhiteSpace(StreamConfig.Mount) || StreamConfig.Mount.Trim() == "/")) StreamConfig.Mount = "radio";
		if (string.IsNullOrWhiteSpace(StreamConfig.Username)) StreamConfig.Username = "source";
		if (StreamConfig.Bitrate <= 0) StreamConfig.Bitrate = 128;
	}

	private void RefreshAudioDevices()
	{
		OutputAudioDevices.Clear();
		foreach (AudioDeviceInfo d in AudioPlaybackService.GetOutputDevices()) OutputAudioDevices.Add(d);
		InputAudioDevices.Clear();
		foreach (AudioDeviceInfo d in AudioPlaybackService.GetInputDevices()) InputAudioDevices.Add(d);

		if (!OutputAudioDevices.Any(d => d.DeviceNumber == SelectedOutputDeviceNumber)) SelectedOutputDeviceNumber = -1;
		if (!OutputAudioDevices.Any(d => d.DeviceNumber == SelectedPflOutputDeviceNumber)) SelectedPflOutputDeviceNumber = -1;
		if (!InputAudioDevices.Any(d => d.DeviceNumber == SelectedInputDeviceNumber)) SelectedInputDeviceNumber = -1;
	}

	private void ApplyLiveAudioSettings()
	{
		_audioService.ApplyAudioSettings(_settings, _activePlaysetTransitionOverride);
		UpdateTransitionPreviewText();
	}

	private void SaveSettingsSafe()
	{
		try { _settingsService.Save(_settings); }
		catch (Exception ex) { LogService.Warn("Settings", $"Save failed: {ex.Message}"); }
	}

	private void PersistQueueSafe()
	{
		if (_settings.RestoreQueueOnStartup)
		{
			_playsetService.SavePersistedQueue(Queue.Select(q => q.Item), _autoDjQueuedItems);
		}
	}

	private void RestorePersistedQueueIfEnabled()
	{
		if (!_settings.RestoreQueueOnStartup) return;
		var restored = _playsetService.LoadPersistedQueue(Library.Items);
		foreach (var (item, isAuto) in restored)
		{
			Queue.Add(new QueueDisplayItem(item));
			if (isAuto) _autoDjQueuedItems.Add(item);
		}
		if (restored.Count > 0)
		{
			ActivityStatus = $"Restored {restored.Count} queued track(s) from last session.";
		}
	}

	private void SelectSweeperFolder()
	{
		var dialog = new OpenFolderDialog { Title = "Select sweeper / jingle folder" };
		if (dialog.ShowDialog() == true)
		{
			SweeperFolder = dialog.FolderName;
			SweepersEnabled = true;
			ActivityStatus = $"Sweeper folder set: {Path.GetFileName(SweeperFolder)}";
		}
	}

	private void SelectRadioIdFolder()
	{
		var dialog = new OpenFolderDialog { Title = "Select Station ID folder" };
		if (dialog.ShowDialog() == true)
		{
			RadioIdFolder = dialog.FolderName;
			RadioIdsEnabled = true;
			ActivityStatus = $"Station ID folder set: {Path.GetFileName(RadioIdFolder)}";
		}
	}

	private void OnLibraryChanged()
	{
		RefreshRotationCatalogOptions();
		RefreshRotationCoverageAndMatches();
		RefreshImagingDiagnostics();
		EnsureAutoDjQueue();
	}

	private ActiveRotationInfo GetActiveRotationNow() =>
		RotationService.ResolveActiveRotation(_settings, DateTime.Now, Library.Playsets);

	private static string BuildRotationKey(ActiveRotationInfo info) =>
		$"{info.SourceKind}:{info.SourceValue}:{info.PlaysetId}:{info.ActiveBlock?.Id}";

	private int PickSweeperInterval()
	{
		ActiveRotationInfo active = GetActiveRotationNow();
		int minSongs = Math.Max(1, active.SweeperMinSongsOverride ?? SweeperMinSongs);
		int maxSongs = Math.Max(minSongs, active.SweeperMaxSongsOverride ?? SweeperMaxSongs);
		return Random.Shared.Next(minSongs, maxSongs + 1);
	}

	private static DateTime GetCurrentHour()
	{
		DateTime n = DateTime.Now;
		return new DateTime(n.Year, n.Month, n.Day, n.Hour, 0, 0);
	}

	private void MarkRadioIdServed(DateTime? targetTime = null)
	{
		DateTime refTime = targetTime ?? DateTime.Now;
		_lastRadioIdHour = new DateTime(refTime.Year, refTime.Month, refTime.Day, refTime.Hour, 0, 0);
	}

	private bool IsRadioId(LibraryItem? item, string folder)
	{
		if (item == null) return false;
		string[] cats = ImagingPolicyService.ParseCategoryList(_settings.RadioIdCategoryNames, "StationId", "Station ID", "ID");
		return ImagingPolicyService.MatchesFolderOrCategory(item, folder, cats) || item.IsStationIdCue;
	}

	// =========================================================================
	// ROTATION MANAGER & IMAGING DIAGNOSTICS METHODS
	// =========================================================================

	private void LoadRotationBlocksFromSettings()
	{
		foreach (RotationBlock existing in RotationBlocks)
		{
			existing.PropertyChanged -= OnRotationBlockPropertyChanged;
		}
		RotationBlocks.Clear();

		List<RotationBlock> effective = RotationService.GetEffectiveBlocks(_settings);
		if (_settings.RotationBlocks.Count == 0 && effective.Count > 0)
		{
			_settings.RotationBlocks = effective;
		}

		foreach (RotationBlock block in effective)
		{
			block.PropertyChanged += OnRotationBlockPropertyChanged;
			RotationBlocks.Add(block);
		}

		SelectedRotationBlock = RotationBlocks.FirstOrDefault();
	}

	private void OnRotationBlockPropertyChanged(object? sender, PropertyChangedEventArgs e)
	{
		if (e.PropertyName == nameof(RotationBlock.CachedMatchCount) ||
			e.PropertyName == nameof(RotationBlock.MatchCountText) ||
			e.PropertyName == nameof(RotationBlock.HasZeroMatches) ||
			e.PropertyName == nameof(RotationBlock.CachedPlaysetName))
		{
			return;
		}
		PersistRotationBlocks();
	}

	private void PersistRotationBlocks()
	{
		_settings.RotationBlocks = RotationBlocks.ToList();
		SaveSettingsSafe();
		RefreshRotationCoverageAndMatches();
		UpdateAutomationCues();
	}

	private void AddRotationBlock()
	{
		string[] palette = { "#0284C7", "#10B981", "#F59E0B", "#8B5CF6", "#EC4899", "#06B6D4", "#EF4444" };
		var block = new RotationBlock
		{
			Name = $"Daypart {RotationBlocks.Count + 1}",
			StartTime = "09:00",
			EndTime = "13:00",
			DaysMask = RotationDayMask.All,
			SourceKind = RotationSourceKind.Tag,
			SourceValue = AvailableRotationTags.FirstOrDefault() ?? string.Empty,
			ColorHex = palette[RotationBlocks.Count % palette.Length],
			Priority = 10,
			Enabled = true
		};
		block.PropertyChanged += OnRotationBlockPropertyChanged;
		RotationBlocks.Add(block);
		SelectedRotationBlock = block;
		PersistRotationBlocks();
		ActivityStatus = $"Added rotation block '{block.Name}'.";
	}

	private void DuplicateSelectedRotationBlock()
	{
		if (SelectedRotationBlock == null) return;
		RotationBlock copy = SelectedRotationBlock.Clone();
		copy.Id = Guid.NewGuid().ToString("N");
		copy.Name = copy.Name + " (Copy)";
		copy.PropertyChanged += OnRotationBlockPropertyChanged;
		RotationBlocks.Add(copy);
		SelectedRotationBlock = copy;
		PersistRotationBlocks();
		ActivityStatus = $"Duplicated rotation block '{copy.Name}'.";
	}

	private void RemoveRotationBlock(RotationBlock? block)
	{
		if (block == null) return;
		block.PropertyChanged -= OnRotationBlockPropertyChanged;
		RotationBlocks.Remove(block);
		if (ReferenceEquals(SelectedRotationBlock, block))
		{
			SelectedRotationBlock = RotationBlocks.FirstOrDefault();
		}
		PersistRotationBlocks();
		ActivityStatus = $"Removed rotation block '{block.Name}'.";
	}

	private void ApplyRotationPreset(string presetKey)
	{
		List<RotationBlock> preset = RotationService.CreatePresetBlocks(presetKey);
		if (preset.Count == 0) return;

		if (RotationBlocks.Count > 0 &&
			MessageBox.Show(Application.Current?.MainWindow,
				"Replace your current rotation blocks with this broadcast template?\n(You can customize every block right after.)",
				"Apply Rotation Template", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
		{
			return;
		}

		foreach (RotationBlock existing in RotationBlocks)
		{
			existing.PropertyChanged -= OnRotationBlockPropertyChanged;
		}
		RotationBlocks.Clear();
		foreach (RotationBlock b in preset)
		{
			b.PropertyChanged += OnRotationBlockPropertyChanged;
			RotationBlocks.Add(b);
		}
		SelectedRotationBlock = RotationBlocks.FirstOrDefault();
		PersistRotationBlocks();
		ActivityStatus = $"Loaded rotation template ({preset.Count} daypart blocks).";
	}

	private void SetSelectedBlockDays(string mode)
	{
		if (SelectedRotationBlock == null) return;
		SelectedRotationBlock.DaysMask = mode.ToLowerInvariant() switch
		{
			"weekdays" => RotationDayMask.Weekdays,
			"weekends" => RotationDayMask.Weekends,
			_ => RotationDayMask.All
		};
	}

	private void RefreshRotationCatalogOptions()
	{
		AvailableRotationTags.Clear();
		foreach (string t in Library.GetAllDistinctTags()) AvailableRotationTags.Add(t);

		AvailableRotationCategories.Clear();
		foreach (string c in Library.GetAllDistinctCategories()) AvailableRotationCategories.Add(c);
	}

	private void RefreshRotationCoverageAndMatches()
	{
		foreach (RotationBlock block in RotationBlocks)
		{
			if (block.SourceKind == RotationSourceKind.Playset && block.PlaysetId.HasValue)
			{
				block.CachedPlaysetName = Library.Playsets.FirstOrDefault(p => p.Id == block.PlaysetId.Value)?.Name ?? string.Empty;
			}
			else
			{
				block.CachedPlaysetName = string.Empty;
			}
			block.CachedMatchCount = RotationService.CountMatchingTracks(block, Library.Items, Library.GetPlaysetTrackCount);
		}

		RotationCoverageReport report = RotationService.AnalyzeCoverage(RotationBlocks, RotationPreviewDay);
		RotationCoverageSummary = report.SummaryText;
		RotationTimelineRevision++;
	}

	private void RunRotationSimulation()
	{
		ActiveRotationInfo active = GetActiveRotationNow();
		List<LibraryItem> picks = Library.SimulateRotationNextTracks(active, count: 6);
		RotationSimulationResults.Clear();
		if (picks.Count == 0)
		{
			RotationSimulationResults.Add("No eligible music tracks found in library for the active rotation.");
			return;
		}

		int swpCounter = 0;
		int swpTarget = Math.Max(1, active.SweeperMinSongsOverride ?? SweeperMinSongs);
		for (int i = 0; i < picks.Count; i++)
		{
			LibraryItem t = picks[i];
			string tags = string.IsNullOrWhiteSpace(t.Tags) ? t.Category : $"{t.Category} • #{t.Tags}";
			RotationSimulationResults.Add($"{i + 1}. {t.Artist} — {t.Title}  [{t.DurationText}]  ({tags})");
			swpCounter++;
			if (SweepersEnabled && swpCounter >= swpTarget && i < picks.Count - 1)
			{
				swpCounter = 0;
				string mode = SweeperPlaybackMode == SweeperPlaybackMode.QueueBetweenTracks ? "QUEUED SWEEPER" : "INTRO OVERLAY SWEEPER";
				RotationSimulationResults.Add($"   ↳ [{mode} fires here • {SweeperMinDurationSeconds:0.0}s–{SweeperMaxDurationSeconds:0.0}s]");
			}
		}
		ActivityStatus = $"Simulated {picks.Count} picks for {active.Description}";
	}

	private void RefreshImagingDiagnostics()
	{
		CuePoolDiagnostics swpDiag = Library.EvaluateSweeperPool();
		SweeperPoolStatusText = swpDiag.SummaryText;

		CuePoolDiagnostics idDiag = Library.EvaluateStationIdPool();
		RadioIdPoolStatusText = idDiag.SummaryText;

		StationIdDecision idDecision = EvaluateCurrentStationIdDecision(DateTime.Now);
		RadioIdWindowStatusText = idDecision.StatusSummary;
	}

	private void TestSweeperNow()
	{
		LibraryItem? swp = PickEligibleSweeper();
		if (swp == null)
		{
			ActivityStatus = $"No eligible sweeper found matching {SweeperMinDurationSeconds:0.0}s–{SweeperMaxDurationSeconds:0.0}s threshold.";
			RefreshImagingDiagnostics();
			return;
		}

		if (ProgramPlayer.IsPlaying)
		{
			_audioService.TriggerOverlay(swp.FilePath, true, SweeperOverlayDuckDb, 1.0);
			ActivityStatus = $"Test Sweeper overlay: '{swp.Title}' ({swp.Duration.TotalSeconds:0.1}s, duck {SweeperOverlayDuckDb:0.0} dB).";
		}
		else
		{
			PlayLibraryItem(swp, smoothCrossfadeIfPlaying: false, autoPlay: true);
			ActivityStatus = $"Test Sweeper playing: '{swp.Title}' ({swp.Duration.TotalSeconds:0.1}s).";
		}
	}

	private void TestStationIdNow()
	{
		LibraryItem? id = PickEligibleRadioId();
		if (id == null)
		{
			ActivityStatus = $"No eligible Station ID found matching {RadioIdMinDurationSeconds:0.0}s–{RadioIdMaxDurationSeconds:0.0}s threshold.";
			RefreshImagingDiagnostics();
			return;
		}

		if (ProgramPlayer.IsPlaying)
		{
			_audioService.TriggerOverlay(id.FilePath, true, RadioIdOverlayDuckDb, 1.0);
			ActivityStatus = $"Test Station ID overlay: '{id.Title}' ({id.Duration.TotalSeconds:0.1}s, duck {RadioIdOverlayDuckDb:0.0} dB).";
		}
		else
		{
			PlayLibraryItem(id, smoothCrossfadeIfPlaying: false, autoPlay: true);
			ActivityStatus = $"Test Station ID playing: '{id.Title}' ({id.Duration.TotalSeconds:0.1}s).";
		}
	}

	private void ApplyCloudKeepAlive()
	{
		bool active = CloudUnattendedModeEnabled && KeepSystemAwakeWhileRunning;
		bool highPrio = CloudUnattendedModeEnabled && ElevateProcessPriority;
		_cloudKeepAlive.Apply(active, highPrio);
	}

	private async Task CheckForUpdatesAsync(bool silent)
	{
		if (IsCheckingOrDownloadingUpdate) return;
		IsCheckingOrDownloadingUpdate = true;
		UpdateDownloadPercent = 0;
		if (!silent) UpdateStatusText = $"Checking GitHub ({GitUpdateRepository}) for updates...";

		try
		{
			UpdateCheckResult result = await _updateService.CheckForGitHubUpdateAsync(GitUpdateRepository);
			_latestUpdateResult = result;
			UpdateAvailable = result.UpdateAvailable;
			LatestRemoteVersionText = $"v{result.LatestVersion}";
			UpdateStatusText = result.Message;
			UpdateReleaseNotes = string.IsNullOrWhiteSpace(result.ReleaseNotes)
				? (result.Success ? $"Release: {result.ReleaseTitle}" : string.Empty)
				: result.ReleaseNotes;
			OnPropertyChanged(nameof(HasMsiUpdateAsset));
			OnPropertyChanged(nameof(HasZipUpdateAsset));

			if (result.UpdateAvailable)
			{
				ActivityStatus = $"New version available on GitHub: v{result.LatestVersion}!";
			}
		}
		finally
		{
			IsCheckingOrDownloadingUpdate = false;
			RelayCommand.InvalidateAll();
		}
	}

	private async Task DownloadAndInstallUpdateAsync(bool preferZip)
	{
		if (_latestUpdateResult == null || IsCheckingOrDownloadingUpdate) return;
		GitHubReleaseAssetInfo? asset = preferZip
			? (_latestUpdateResult.PortableZipAsset ?? _latestUpdateResult.MsiAsset)
			: (_latestUpdateResult.MsiAsset ?? _latestUpdateResult.PortableZipAsset);

		if (asset == null)
		{
			UpdateStatusText = "No downloadable MSI or Portable ZIP asset found in this GitHub Release.";
			return;
		}

		string kind = asset.Name.EndsWith(".msi", StringComparison.OrdinalIgnoreCase) ? "MSI installer" : "Portable ZIP update";
		if (MessageBox.Show(
			Application.Current?.MainWindow,
			$"Download and install RadioApp Studio v{_latestUpdateResult.LatestVersion} ({kind})?\n\n" +
			"Your queue, settings, and library database will be preserved automatically. RadioApp will restart to complete installation.",
			"Install Update from GitHub",
			MessageBoxButton.YesNo,
			MessageBoxImage.Question) != MessageBoxResult.Yes)
		{
			return;
		}

		IsCheckingOrDownloadingUpdate = true;
		UpdateDownloadPercent = 0;
		try
		{
			var progress = new Progress<UpdateDownloadProgress>(p =>
			{
				UpdateDownloadPercent = p.Percent;
				UpdateStatusText = p.StatusText;
			});

			string downloadedFile = await _updateService.DownloadAssetAsync(asset, progress);
			PersistQueueSafe();
			SaveSettingsSafe();

			bool launched = GitUpdateService.LaunchDownloadedUpdate(
				downloadedFile,
				Environment.ProcessId,
				AppContext.BaseDirectory);

			if (launched)
			{
				UpdateStatusText = "Launching updater and restarting RadioApp Studio...";
				RequestGracefulUpdateExit?.Invoke();
			}
			else
			{
				UpdateStatusText = $"Downloaded to {downloadedFile}, but could not launch installer automatically.";
			}
		}
		catch (Exception ex)
		{
			LogService.Error("Updater", "Download/install failed", ex);
			UpdateStatusText = $"Update download failed: {ex.Message}";
		}
		finally
		{
			IsCheckingOrDownloadingUpdate = false;
			RelayCommand.InvalidateAll();
		}
	}

	private async Task FetchLocalGitAsync()
	{
		if (IsCheckingOrDownloadingUpdate) return;
		IsCheckingOrDownloadingUpdate = true;
		LocalGitStatusText = "Running git fetch --all --tags...";
		try
		{
			LocalGitSyncResult res = await GitUpdateService.FetchFromLocalGitAsync(LocalGitRepoPath);
			LocalGitStatusText = res.Summary;
			if (res.IsGitRepo && string.IsNullOrWhiteSpace(LocalGitRepoPath))
			{
				LocalGitRepoPath = res.RepoRoot;
			}
			ActivityStatus = res.Summary;
		}
		finally
		{
			IsCheckingOrDownloadingUpdate = false;
			RelayCommand.InvalidateAll();
		}
	}

	private async Task PullLocalGitAsync()
	{
		if (IsCheckingOrDownloadingUpdate) return;
		IsCheckingOrDownloadingUpdate = true;
		LocalGitStatusText = "Running git pull --ff-only...";
		try
		{
			LocalGitSyncResult res = await GitUpdateService.PullFromLocalGitAsync(LocalGitRepoPath);
			LocalGitStatusText = res.Summary;
			ActivityStatus = res.Summary;
		}
		finally
		{
			IsCheckingOrDownloadingUpdate = false;
			RelayCommand.InvalidateAll();
		}
	}

	private void NotifyAllPropertiesChanged() => OnPropertyChanged(string.Empty);

	public void Dispose()
	{
		_disposed = true;
		PersistQueueSafe();
		_streamConnectionDesired = false;
		CancelStreamReconnect();
		_clockTimer.Stop();
		_clockTimer.Dispose();
		_automationTimer.Stop();
		_automationTimer.Dispose();
		_cloudKeepAlive.Dispose();
		_streamingService.Dispose();
		_audioService.Dispose();
		_analysisService.Dispose();
		Library.Dispose();
		Schedule.Dispose();
		LogService.Info("App", "RadioApp Studio shut down cleanly.");
	}
}
