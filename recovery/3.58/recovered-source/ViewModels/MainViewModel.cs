using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using Microsoft.Win32;
using RadioApp.Helpers;
using RadioApp.Models;
using RadioApp.Services;

namespace RadioApp.ViewModels;

public class MainViewModel : ViewModelBase, IDisposable
{
	private sealed record AutoDjTagRule(TimeSpan Start, TimeSpan End, string Tag);

	private sealed record AutoDjRotationStatus(string Tag, string Description);

	private readonly AudioPlaybackService _audioService;

	private readonly StreamingService _streamingService;

	private readonly AppSettingsService _settingsService;

	private readonly AppSettings _settings;

	private readonly HashSet<LibraryItem> _autoDjQueuedItems = new HashSet<LibraryItem>();

	private readonly DispatcherTimer _clockTimer;

	private readonly DispatcherTimer _automationTimer;

	private string _streamStatus = "Disconnected";

	private string _activityStatus = "Ready";

	private string _headerStatus = string.Empty;

	private double _crossfade;

	private bool _autoConnectStreamOnStartup;

	private bool _autoScheduleEnabled;

	private bool _autoDjFillCurrentHour = true;

	private int _autoDjQueueDepth = 5;

	private string _autoDjDefaultTag = string.Empty;

	private string _autoDjHourlyTagRules = string.Empty;

	private bool _gaplessPlaybackEnabled = true;

	private double _gaplessSilenceThresholdDb = -42.0;

	private int _gaplessSilenceHoldMilliseconds = 650;

	private double _gaplessScanWindowSeconds = 20.0;

	private bool _sweepersEnabled;

	private string _sweeperFolder = string.Empty;

	private int _sweeperMinSongs = 3;

	private int _sweeperMaxSongs = 6;

	private int _songsUntilSweeper;

	private bool _radioIdsEnabled;

	private string _radioIdFolder = string.Empty;

	private int _radioIdMinute;

	private int _radioIdToleranceBeforeMinutes = 5;

	private int _radioIdToleranceAfterMinutes = 10;

	private int _selectedOutputDeviceNumber = -1;

	private int _selectedInputDeviceNumber = -1;

	private bool _audioNormalizationEnabled;

	private double _normalizationTargetDb = -3.0;

	private double _normalizationMaxGainDb = 9.0;

	private bool _audioLimiterEnabled = true;

	private bool _minimizeToTray = true;

	private bool _closeToTray = true;

	private bool _startMinimizedToTray;

	private DateTime _lastRadioIdHour = DateTime.MinValue;

	private DateTime _lastAutoDjHour;

	private string _lastAutoDjRotationTag = string.Empty;

	private LibraryItem? _currentProgramItem;

	private LibraryItem? _preloadedProgramItem;

	private LibraryItem? _pendingRadioId;

	private DateTime _pendingRadioIdTime = DateTime.MinValue;

	private LibraryItem? _selectedQueueItem;

	private string _queueStatus = "Queue empty";

	private string _automationCue = "Automation idle";

	private string _nextQueueCue = "Next: none";

	private string _nextRadioIdCue = "ID off";

	private string _nextSweeperCue = "Jingles off";

	private string _rotationCue = "Rotation: unrestricted";

	private int _songsSinceCommercial;

	private int _songsSinceSweeper;

	private bool _isUpdatingQueueStatus;

	private bool _startupInitialized;

	private bool _isConnectingStream;

	private bool _streamConnectionDesired;

	private bool _streamReconnectScheduled;

	private bool _streamStallRecoveryStarted;

	private bool _disposed;

	private int _streamReconnectAttempt;

	private DateTimeOffset? _streamConnectedAtUtc;

	private CancellationTokenSource? _streamReconnectCancellation;

	public DeckViewModel DeckA { get; }

	public DeckViewModel DeckB { get; }

	public DeckViewModel ProgramPlayer => DeckA;

	public LibraryViewModel Library { get; }

	public ScheduleViewModel Schedule { get; }

	public StreamConfig StreamConfig { get; }

	public string StreamEndpointText
	{
		get
		{
			if (StreamConfig.ServerType == StreamServerType.Shoutcast)
			{
				if (StreamConfig.ShoutcastStreamId > 0)
				{
					return $"SHOUTcast v2 source ID: {StreamConfig.ShoutcastStreamId}";
				}
				return "SHOUTcast v1 source connection";
			}
			return "Icecast mountpoint: " + StreamConfig.FullPath;
		}
	}

	public ObservableCollection<LibraryItem> Queue { get; }

	public ObservableCollection<AudioDeviceInfo> OutputAudioDevices { get; }

	public ObservableCollection<AudioDeviceInfo> InputAudioDevices { get; }

	public ICommand ConnectStreamCommand { get; }

	public ICommand StreamProgramCommand { get; }

	public ICommand DisconnectStreamCommand { get; }

	public ICommand StreamDeckACommand { get; }

	public ICommand StreamDeckBCommand { get; }

	public ICommand LoadSelectedToProgramCommand { get; }

	public ICommand LoadSelectedToDeckACommand { get; }

	public ICommand LoadSelectedToDeckBCommand { get; }

	public ICommand EnqueueSelectedCommand { get; }

	public ICommand EnqueueRandomCommand { get; }

	public ICommand PlayNextCommand { get; }

	public ICommand RemoveQueueItemCommand { get; }

	public ICommand ClearQueueCommand { get; }

	public ICommand RandomizeDeckACommand { get; }

	public ICommand RandomizeDeckBCommand { get; }

	public ICommand AutoScheduleNowCommand { get; }

	public ICommand SelectSweeperFolderCommand { get; }

	public ICommand SelectRadioIdFolderCommand { get; }

	public ICommand RefreshAudioDevicesCommand { get; }

	public ICommand ClearLibraryCommand { get; }

	public ICommand ResetAppCommand { get; }

	public string StreamStatus
	{
		get
		{
			return _streamStatus;
		}
		set
		{
			if (SetProperty(ref _streamStatus, value, "StreamStatus"))
			{
				UpdateHeaderStatus();
			}
		}
	}

	public string HeaderStatus
	{
		get
		{
			return _headerStatus;
		}
		set
		{
			SetProperty(ref _headerStatus, value, "HeaderStatus");
		}
	}

	public string ActivityStatus
	{
		get
		{
			return _activityStatus;
		}
		set
		{
			if (SetProperty(ref _activityStatus, value, "ActivityStatus"))
			{
				UpdateHeaderStatus();
			}
		}
	}

	public double Crossfade
	{
		get
		{
			return _crossfade;
		}
		set
		{
			if (SetProperty(ref _crossfade, value, "Crossfade"))
			{
				UpdateCrossfadeVolumes();
			}
		}
	}

	public bool AutoConnectStreamOnStartup
	{
		get
		{
			return _autoConnectStreamOnStartup;
		}
		set
		{
			if (SetProperty(ref _autoConnectStreamOnStartup, value, "AutoConnectStreamOnStartup"))
			{
				SaveGeneralSettings();
			}
		}
	}

	public bool AutoScheduleEnabled
	{
		get
		{
			return _autoScheduleEnabled;
		}
		set
		{
			if (SetProperty(ref _autoScheduleEnabled, value, "AutoScheduleEnabled"))
			{
				SaveAutomationSettings();
				EnsureAutoDjQueue();
			}
		}
	}

	public int AutoDjQueueDepth
	{
		get
		{
			return _autoDjQueueDepth;
		}
		set
		{
			if (SetProperty(ref _autoDjQueueDepth, Math.Max(1, value), "AutoDjQueueDepth"))
			{
				SaveAutomationSettings();
				EnsureAutoDjQueue();
			}
		}
	}

	public bool AutoDjFillCurrentHour
	{
		get
		{
			return _autoDjFillCurrentHour;
		}
		set
		{
			if (SetProperty(ref _autoDjFillCurrentHour, value, "AutoDjFillCurrentHour"))
			{
				SaveAutomationSettings();
				EnsureAutoDjQueue();
			}
		}
	}

	public string AutoDjDefaultTag
	{
		get
		{
			return _autoDjDefaultTag;
		}
		set
		{
			if (SetProperty(ref _autoDjDefaultTag, value, "AutoDjDefaultTag"))
			{
				SaveAutomationSettings();
				EnsureAutoDjQueue();
				UpdateAutomationCue();
			}
		}
	}

	public string AutoDjHourlyTagRules
	{
		get
		{
			return _autoDjHourlyTagRules;
		}
		set
		{
			if (SetProperty(ref _autoDjHourlyTagRules, value, "AutoDjHourlyTagRules"))
			{
				SaveAutomationSettings();
				EnsureAutoDjQueue();
				UpdateAutomationCue();
			}
		}
	}

	public bool GaplessPlaybackEnabled
	{
		get
		{
			return _gaplessPlaybackEnabled;
		}
		set
		{
			if (SetProperty(ref _gaplessPlaybackEnabled, value, "GaplessPlaybackEnabled"))
			{
				SaveAutomationSettings();
				RefreshGaplessPreload();
			}
		}
	}

	public double GaplessSilenceThresholdDb
	{
		get
		{
			return _gaplessSilenceThresholdDb;
		}
		set
		{
			if (SetProperty(ref _gaplessSilenceThresholdDb, Math.Clamp(value, -72.0, -12.0), "GaplessSilenceThresholdDb"))
			{
				SaveAutomationSettings();
				_audioService.ApplyAudioSettings(_settings);
			}
		}
	}

	public int GaplessSilenceHoldMilliseconds
	{
		get
		{
			return _gaplessSilenceHoldMilliseconds;
		}
		set
		{
			if (SetProperty(ref _gaplessSilenceHoldMilliseconds, Math.Clamp(value, 100, 3000), "GaplessSilenceHoldMilliseconds"))
			{
				SaveAutomationSettings();
				_audioService.ApplyAudioSettings(_settings);
			}
		}
	}

	public double GaplessScanWindowSeconds
	{
		get
		{
			return _gaplessScanWindowSeconds;
		}
		set
		{
			if (SetProperty(ref _gaplessScanWindowSeconds, Math.Clamp(value, 3.0, 60.0), "GaplessScanWindowSeconds"))
			{
				SaveAutomationSettings();
				_audioService.ApplyAudioSettings(_settings);
			}
		}
	}

	public bool SweepersEnabled
	{
		get
		{
			return _sweepersEnabled;
		}
		set
		{
			if (SetProperty(ref _sweepersEnabled, value, "SweepersEnabled"))
			{
				SaveAutomationSettings();
				UpdateAutomationCue();
				EnsureAutoDjQueue();
			}
		}
	}

	public string SweeperFolder
	{
		get
		{
			return _sweeperFolder;
		}
		set
		{
			if (SetProperty(ref _sweeperFolder, value, "SweeperFolder"))
			{
				SaveAutomationSettings();
				UpdateAutomationCue();
				EnsureAutoDjQueue();
			}
		}
	}

	public int SweeperMinSongs
	{
		get
		{
			return _sweeperMinSongs;
		}
		set
		{
			int sanitized = Math.Max(1, value);
			if (SetProperty(ref _sweeperMinSongs, sanitized, "SweeperMinSongs") && _sweeperMaxSongs < sanitized)
			{
				SweeperMaxSongs = sanitized;
			}
			SaveAutomationSettings();
			UpdateAutomationCue();
			EnsureAutoDjQueue();
		}
	}

	public int SweeperMaxSongs
	{
		get
		{
			return _sweeperMaxSongs;
		}
		set
		{
			int sanitized = Math.Max(SweeperMinSongs, value);
			if (SetProperty(ref _sweeperMaxSongs, sanitized, "SweeperMaxSongs"))
			{
				SaveAutomationSettings();
				UpdateAutomationCue();
				EnsureAutoDjQueue();
			}
		}
	}

	public bool RadioIdsEnabled
	{
		get
		{
			return _radioIdsEnabled;
		}
		set
		{
			if (SetProperty(ref _radioIdsEnabled, value, "RadioIdsEnabled"))
			{
				if (!value)
				{
					ClearPendingRadioId();
				}
				SaveAutomationSettings();
				UpdateAutomationCue();
				EnsureAutoDjQueue();
			}
		}
	}

	public string RadioIdFolder
	{
		get
		{
			return _radioIdFolder;
		}
		set
		{
			if (SetProperty(ref _radioIdFolder, value, "RadioIdFolder"))
			{
				ClearPendingRadioId();
				SaveAutomationSettings();
				UpdateAutomationCue();
				EnsureAutoDjQueue();
			}
		}
	}

	public int RadioIdMinute
	{
		get
		{
			return _radioIdMinute;
		}
		set
		{
			if (SetProperty(ref _radioIdMinute, Math.Clamp(value, 0, 59), "RadioIdMinute"))
			{
				ClearPendingRadioId();
				SaveAutomationSettings();
				UpdateAutomationCue();
				EnsureAutoDjQueue();
			}
		}
	}

	public int RadioIdToleranceBeforeMinutes
	{
		get
		{
			return _radioIdToleranceBeforeMinutes;
		}
		set
		{
			if (SetProperty(ref _radioIdToleranceBeforeMinutes, Math.Max(0, value), "RadioIdToleranceBeforeMinutes"))
			{
				ClearPendingRadioId();
				SaveAutomationSettings();
				UpdateAutomationCue();
				EnsureAutoDjQueue();
			}
		}
	}

	public int RadioIdToleranceAfterMinutes
	{
		get
		{
			return _radioIdToleranceAfterMinutes;
		}
		set
		{
			if (SetProperty(ref _radioIdToleranceAfterMinutes, Math.Max(0, value), "RadioIdToleranceAfterMinutes"))
			{
				ClearPendingRadioId();
				SaveAutomationSettings();
				UpdateAutomationCue();
				EnsureAutoDjQueue();
			}
		}
	}

	public int SelectedOutputDeviceNumber
	{
		get
		{
			return _selectedOutputDeviceNumber;
		}
		set
		{
			if (SetProperty(ref _selectedOutputDeviceNumber, value, "SelectedOutputDeviceNumber"))
			{
				SaveAudioSettings();
			}
		}
	}

	public int SelectedInputDeviceNumber
	{
		get
		{
			return _selectedInputDeviceNumber;
		}
		set
		{
			if (SetProperty(ref _selectedInputDeviceNumber, value, "SelectedInputDeviceNumber"))
			{
				SaveAudioSettings();
			}
		}
	}

	public bool AudioNormalizationEnabled
	{
		get
		{
			return _audioNormalizationEnabled;
		}
		set
		{
			if (SetProperty(ref _audioNormalizationEnabled, value, "AudioNormalizationEnabled"))
			{
				SaveAudioSettings();
			}
		}
	}

	public double NormalizationTargetDb
	{
		get
		{
			return _normalizationTargetDb;
		}
		set
		{
			if (SetProperty(ref _normalizationTargetDb, Math.Clamp(value, -24.0, 0.0), "NormalizationTargetDb"))
			{
				SaveAudioSettings();
			}
		}
	}

	public double NormalizationMaxGainDb
	{
		get
		{
			return _normalizationMaxGainDb;
		}
		set
		{
			if (SetProperty(ref _normalizationMaxGainDb, Math.Clamp(value, 0.0, 24.0), "NormalizationMaxGainDb"))
			{
				SaveAudioSettings();
			}
		}
	}

	public bool AudioLimiterEnabled
	{
		get
		{
			return _audioLimiterEnabled;
		}
		set
		{
			if (SetProperty(ref _audioLimiterEnabled, value, "AudioLimiterEnabled"))
			{
				SaveAudioSettings();
			}
		}
	}

	public bool MinimizeToTray
	{
		get
		{
			return _minimizeToTray;
		}
		set
		{
			if (SetProperty(ref _minimizeToTray, value, "MinimizeToTray"))
			{
				SaveGeneralSettings();
			}
		}
	}

	public bool CloseToTray
	{
		get
		{
			return _closeToTray;
		}
		set
		{
			if (SetProperty(ref _closeToTray, value, "CloseToTray"))
			{
				SaveGeneralSettings();
			}
		}
	}

	public bool StartMinimizedToTray
	{
		get
		{
			return _startMinimizedToTray;
		}
		set
		{
			if (SetProperty(ref _startMinimizedToTray, value, "StartMinimizedToTray"))
			{
				SaveGeneralSettings();
			}
		}
	}

	public LibraryItem? SelectedQueueItem
	{
		get
		{
			return _selectedQueueItem;
		}
		set
		{
			if (SetProperty(ref _selectedQueueItem, value, "SelectedQueueItem"))
			{
				CommandManager.InvalidateRequerySuggested();
			}
		}
	}

	public string QueueStatus
	{
		get
		{
			return _queueStatus;
		}
		set
		{
			SetProperty(ref _queueStatus, value, "QueueStatus");
		}
	}

	public string AutomationCue
	{
		get
		{
			return _automationCue;
		}
		set
		{
			SetProperty(ref _automationCue, value, "AutomationCue");
		}
	}

	public string NextQueueCue
	{
		get
		{
			return _nextQueueCue;
		}
		set
		{
			SetProperty(ref _nextQueueCue, value, "NextQueueCue");
		}
	}

	public string NextRadioIdCue
	{
		get
		{
			return _nextRadioIdCue;
		}
		set
		{
			SetProperty(ref _nextRadioIdCue, value, "NextRadioIdCue");
		}
	}

	public string NextSweeperCue
	{
		get
		{
			return _nextSweeperCue;
		}
		set
		{
			SetProperty(ref _nextSweeperCue, value, "NextSweeperCue");
		}
	}

	public string RotationCue
	{
		get
		{
			return _rotationCue;
		}
		set
		{
			SetProperty(ref _rotationCue, value, "RotationCue");
		}
	}

	public MainViewModel()
	{
		_audioService = new AudioPlaybackService();
		_streamingService = new StreamingService();
		_settingsService = new AppSettingsService();
		_settings = _settingsService.Load();
		DeckA = new DeckViewModel(_audioService, DeckId.A);
		DeckB = new DeckViewModel(_audioService, DeckId.B);
		Library = new LibraryViewModel(_settings, _settingsService);
		Schedule = new ScheduleViewModel();
		StreamConfig = new StreamConfig();
		ApplySettings(_settings);
		ApplyStreamSettings(_settings);
		Queue = new ObservableCollection<LibraryItem>();
		OutputAudioDevices = new ObservableCollection<AudioDeviceInfo>();
		InputAudioDevices = new ObservableCollection<AudioDeviceInfo>();
		RefreshAudioDevices();
		_audioService.ApplyAudioSettings(_settings);
		_songsUntilSweeper = PickSweeperInterval();
		DeckA.PropertyChanged += OnDeckPropertyChanged;
		DeckB.PropertyChanged += OnDeckPropertyChanged;
		ConnectStreamCommand = new RelayCommand(async delegate
		{
			_streamConnectionDesired = true;
			CancelStreamReconnect();
			await ConnectStreamAsync(scheduleRetryOnFailure: true);
		});
		StreamProgramCommand = new RelayCommand(delegate
		{
			StartPublishingProgram(reportNotConnected: true);
		});
		DisconnectStreamCommand = new RelayCommand(delegate
		{
			_streamConnectionDesired = false;
			CancelStreamReconnect();
			_streamingService.Disconnect();
			_streamConnectedAtUtc = null;
			_streamStallRecoveryStarted = false;
			StreamStatus = "Disconnected";
		});
		StreamDeckACommand = StreamProgramCommand;
		StreamDeckBCommand = new RelayCommand(delegate
		{
			ActivityStatus = "Deck B is no longer part of the program output.";
		});
		LoadSelectedToProgramCommand = new RelayCommand(delegate
		{
			LoadSelectedLibraryItemToProgram();
		});
		LoadSelectedToDeckACommand = LoadSelectedToProgramCommand;
		LoadSelectedToDeckBCommand = new RelayCommand(delegate
		{
			EnqueueSelectedLibraryItem();
		});
		EnqueueSelectedCommand = new RelayCommand(delegate
		{
			EnqueueSelectedLibraryItem();
		}, (object? _) => Library.SelectedItem != null);
		EnqueueRandomCommand = new RelayCommand(delegate
		{
			EnqueueAutomationItem();
		});
		PlayNextCommand = new RelayCommand(delegate
		{
			PlayNextFromQueue();
		}, (object? _) => Queue.Count > 0);
		RemoveQueueItemCommand = new RelayCommand(delegate
		{
			RemoveSelectedQueueItem();
		}, (object? _) => SelectedQueueItem != null);
		ClearQueueCommand = new RelayCommand(delegate
		{
			ClearQueue();
		}, (object? _) => Queue.Count > 0);
		RandomizeDeckACommand = new RelayCommand(delegate
		{
			LoadRandomToProgram(play: true);
		});
		RandomizeDeckBCommand = EnqueueRandomCommand;
		AutoScheduleNowCommand = EnqueueRandomCommand;
		SelectSweeperFolderCommand = new RelayCommand(delegate
		{
			SelectSweeperFolder();
		});
		SelectRadioIdFolderCommand = new RelayCommand(delegate
		{
			SelectRadioIdFolder();
		});
		RefreshAudioDevicesCommand = new RelayCommand(delegate
		{
			RefreshAudioDevices();
		});
		ClearLibraryCommand = new RelayCommand(delegate
		{
			ClearLibrary();
		});
		ResetAppCommand = new RelayCommand(delegate
		{
			ResetApp();
		});
		Schedule.ScheduleDue += OnScheduleDue;
		Library.LibraryChanged += OnLibraryChanged;
		StreamConfig.PropertyChanged += OnStreamConfigPropertyChanged;
		_audioService.ProgramOutputAvailable += _streamingService.EnqueueProgramOutput;
		_streamingService.StreamFailed += OnStreamFailed;
		_audioService.PlaybackUpdated += OnPlaybackUpdated;
		_audioService.TrackEnded += OnTrackEnded;
		_audioService.PreloadedTrackStarted += OnPreloadedTrackStarted;
		_audioService.PlaybackFailed += OnPlaybackFailed;
		_clockTimer = new DispatcherTimer
		{
			Interval = TimeSpan.FromSeconds(1.0)
		};
		_clockTimer.Tick += delegate
		{
			if (_streamingService.IsConnected)
			{
				StreamStatus = _streamingService.GetConnectionSummary();
				CheckStreamHealth();
			}
			UpdateHeaderStatus();
		};
		_clockTimer.Start();
		_lastAutoDjHour = GetCurrentHour();
		_lastAutoDjRotationTag = GetActiveAutoDjTag();
		_automationTimer = new DispatcherTimer
		{
			Interval = TimeSpan.FromSeconds(20.0)
		};
		_automationTimer.Tick += delegate
		{
			MaintainAutoDjHour();
		};
		_automationTimer.Start();
		UpdateCrossfadeVolumes();
		UpdateAutomationCue();
		UpdateHeaderStatus();
		EnsureAutoDjQueue();
	}

	public async Task InitializeAsync()
	{
		if (!_startupInitialized)
		{
			_startupInitialized = true;
			if (Library.BackgroundRefreshEnabled)
			{
				await Library.RunWatchedFolderRefreshAsync(reportStatus: false);
			}
			if (AutoConnectStreamOnStartup)
			{
				_streamConnectionDesired = true;
				await ConnectStreamAsync(scheduleRetryOnFailure: true);
			}
		}
	}

	private async Task ConnectStreamAsync(bool scheduleRetryOnFailure)
	{
		if (_isConnectingStream || _disposed || !_streamConnectionDesired)
		{
			return;
		}
		_isConnectingStream = true;
		try
		{
			NormalizeStreamConfig();
			StreamStatus = "Connecting...";
			if (!(await _streamingService.ConnectAsync(StreamConfig)))
			{
				StreamStatus = "Failed to connect";
				if (scheduleRetryOnFailure)
				{
					ScheduleStreamReconnect("connection was rejected");
				}
				return;
			}
			_streamingService.StartProgramOutputStream();
			_streamConnectedAtUtc = DateTimeOffset.UtcNow;
			_streamStallRecoveryStarted = false;
			StreamStatus = _streamingService.GetConnectionSummary();
		}
		catch (Exception ex)
		{
			StreamStatus = "Disconnected — connection error: " + ex.Message;
			if (scheduleRetryOnFailure)
			{
				ScheduleStreamReconnect(ex.Message);
			}
		}
		finally
		{
			_isConnectingStream = false;
		}
	}

	private void StartPublishingProgram(bool reportNotConnected = false)
	{
		if (!_streamingService.IsConnected)
		{
			if (reportNotConnected)
			{
				StreamStatus = "Not connected";
			}
			return;
		}
		try
		{
			_streamingService.StartProgramOutputStream();
			StreamStatus = _streamingService.GetConnectionSummary();
		}
		catch (Exception ex)
		{
			StreamStatus = "Disconnected — stream error: " + ex.Message;
		}
	}

	private void LoadSelectedLibraryItemToProgram()
	{
		if (Library.SelectedItem == null)
		{
			ActivityStatus = "Select a library item first.";
		}
		else if (PlayLibraryItem(Library.SelectedItem))
		{
			ActivityStatus = "Loaded " + Library.SelectedItem.Title + " to the program player.";
		}
	}

	private void LoadRandomToProgram(bool play)
	{
		LibraryItem item = Library.GetRandomItem("Music", "Song", "Uncategorized");
		if (item == null)
		{
			ActivityStatus = "Import music into the library first.";
			return;
		}
		ProgramPlayer.LoadLibraryItem(item);
		if (ProgramPlayer.IsTrackReady)
		{
			if (play)
			{
				ProgramPlayer.PlayCommand.Execute(null);
			}
			ActivityStatus = "Randomized program player: " + item.Title;
		}
	}

	private void EnqueueSelectedLibraryItem()
	{
		if (Library.SelectedItem == null)
		{
			ActivityStatus = "Select a library item first.";
			return;
		}
		Queue.Add(Library.SelectedItem);
		UpdateQueueStatus();
		RefreshGaplessPreload();
		ActivityStatus = "Queued " + Library.SelectedItem.Title + ".";
		CommandManager.InvalidateRequerySuggested();
	}

	private void EnqueueAutomationItem()
	{
		LibraryItem item = PickNextAutomationItem();
		if (item == null)
		{
			ActivityStatus = "No library items available for automation.";
			return;
		}
		Queue.Add(item);
		MarkAutoDjQueued(item);
		UpdateQueueStatus();
		RefreshGaplessPreload();
		ActivityStatus = "Queued auto pick: " + item.Title;
		CommandManager.InvalidateRequerySuggested();
	}

	private void PlayNextFromQueue()
	{
		if (Queue.Count == 0)
		{
			QueueStatus = "Queue empty";
			ActivityStatus = "Queue is empty.";
			return;
		}
		LibraryItem item = Queue[0];
		Queue.RemoveAt(0);
		UnmarkAutoDjQueued(item);
		ClearGaplessPreload();
		if (PlayLibraryItem(item))
		{
			EnsureAutoDjQueue();
			UpdateQueueStatus();
			RefreshGaplessPreload();
			ActivityStatus = "Now playing: " + item.Title;
			CommandManager.InvalidateRequerySuggested();
		}
	}

	private void RemoveSelectedQueueItem()
	{
		if (SelectedQueueItem != null)
		{
			Queue.Remove(SelectedQueueItem);
			UnmarkAutoDjQueued(SelectedQueueItem);
			SelectedQueueItem = null;
			UpdateQueueStatus();
			EnsureAutoDjQueue();
			RefreshGaplessPreload();
			CommandManager.InvalidateRequerySuggested();
		}
	}

	private void ClearQueue()
	{
		Queue.Clear();
		_autoDjQueuedItems.Clear();
		SelectedQueueItem = null;
		UpdateQueueStatus();
		EnsureAutoDjQueue();
		ClearGaplessPreload();
		CommandManager.InvalidateRequerySuggested();
	}

	private void SelectSweeperFolder()
	{
		OpenFolderDialog dialog = new OpenFolderDialog
		{
			Title = "Select sweeper or station bumper folder",
			Multiselect = false
		};
		if (dialog.ShowDialog() == true)
		{
			SweeperFolder = dialog.FolderName;
			SweepersEnabled = true;
			_songsUntilSweeper = PickSweeperInterval();
			ActivityStatus = "Sweeper folder set: " + Path.GetFileName(SweeperFolder);
		}
	}

	private void SelectRadioIdFolder()
	{
		OpenFolderDialog dialog = new OpenFolderDialog
		{
			Title = "Select radio ID folder",
			Multiselect = false
		};
		if (dialog.ShowDialog() == true)
		{
			RadioIdFolder = dialog.FolderName;
			RadioIdsEnabled = true;
			ActivityStatus = "Radio ID folder set: " + Path.GetFileName(RadioIdFolder);
		}
	}

	private bool PlayLibraryItem(LibraryItem item)
	{
		ProgramPlayer.LoadLibraryItem(item);
		if (!ProgramPlayer.IsTrackReady)
		{
			return false;
		}
		if (ProgramPlayer.PlayCommand.CanExecute(null))
		{
			ProgramPlayer.PlayCommand.Execute(null);
		}
		_currentProgramItem = item;
		if (IsRadioId(item, RadioIdFolder))
		{
			MarkRadioIdServed();
		}
		if (GaplessPlaybackEnabled)
		{
			PreparePostTrackQueue();
		}
		RefreshGaplessPreload();
		return true;
	}

	private LibraryItem? PickNextAutomationItem()
	{
		_songsSinceCommercial++;
		if (_songsSinceCommercial >= 6)
		{
			LibraryItem commercial = Library.GetRandomItem("Commercial", "Commercials", "Ad", "Ads");
			if (commercial != null)
			{
				_songsSinceCommercial = 0;
				return commercial;
			}
		}
		string activeTag = GetActiveAutoDjTag();
		if (!string.IsNullOrWhiteSpace(activeTag))
		{
			LibraryItem taggedItem = Library.GetRandomMusicByTag(activeTag);
			if (taggedItem != null)
			{
				return taggedItem;
			}
		}
		return Library.GetRandomItem("Music", "Song", "Uncategorized");
	}

	private void OnScheduleDue(ScheduleItem item)
	{
		if (item.IsFolderSource || (item.EventType == ScheduleEventType.Playlist && Directory.Exists(item.FilePath)))
		{
			BuildScheduledFolderBlock(item);
			return;
		}
		LibraryItem scheduledItem = new LibraryItem
		{
			FilePath = item.FilePath,
			Title = (string.IsNullOrWhiteSpace(item.Title) ? item.EventType.ToString() : item.Title),
			Category = item.EventType.ToString(),
			DateAdded = DateTime.UtcNow
		};
		if (item.IsMain)
		{
			ProgramPlayer.StopCommand.Execute(null);
			if (!string.IsNullOrWhiteSpace(item.FilePath))
			{
				PlayLibraryItem(scheduledItem);
			}
		}
		else if (!string.IsNullOrWhiteSpace(item.FilePath))
		{
			Queue.Insert(0, scheduledItem);
			UpdateQueueStatus();
			if (!ProgramPlayer.IsPlaying)
			{
				PlayNextFromQueue();
			}
			else
			{
				RefreshGaplessPreload();
			}
		}
	}

	private void BuildScheduledFolderBlock(ScheduleItem item)
	{
		List<LibraryItem> tracks = (from _ in GetLibraryItemsInFolder(item.FilePath)
			where !_.IsSweeperCue && !_.IsStationIdCue
			orderby Random.Shared.Next()
			select _).ToList();
		if (tracks.Count == 0)
		{
			ActivityStatus = "Scheduled folder has no imported music tracks.";
			return;
		}
		Queue.Clear();
		_autoDjQueuedItems.Clear();
		SelectedQueueItem = null;
		ClearGaplessPreload();
		TimeSpan target = TimeSpan.FromMinutes(Math.Max(1, item.BlockDurationMinutes));
		TimeSpan coverage = TimeSpan.Zero;
		int guard = 0;
		while (coverage < target && guard < 500)
		{
			LibraryItem track = tracks[guard % tracks.Count];
			if (coverage > TimeSpan.Zero && coverage + GetPlannedDuration(track) - target > TimeSpan.FromMinutes(2.0))
			{
				break;
			}
			Queue.Add(track);
			coverage += GetPlannedDuration(track);
			guard++;
			if (TryAppendPlannedSweeper(track))
			{
				coverage += GetPlannedDuration(Queue.Last());
			}
			if (TryInsertProjectedRadioId())
			{
				coverage += GetPlannedDuration(Queue.Last());
			}
			if (guard % tracks.Count == 0)
			{
				tracks = tracks.OrderBy((LibraryItem _) => Random.Shared.Next()).ToList();
			}
		}
		UpdateQueueStatus();
		RefreshGaplessPreload();
		if (!ProgramPlayer.IsPlaying && Queue.Count > 0)
		{
			PlayNextFromQueue();
		}
		ActivityStatus = $"Scheduled folder loaded {Queue.Count} item(s) for {Math.Round(coverage.TotalMinutes)} min.";
		CommandManager.InvalidateRequerySuggested();
	}

	private IEnumerable<LibraryItem> GetLibraryItemsInFolder(string folderPath)
	{
		if (string.IsNullOrWhiteSpace(folderPath))
		{
			return Enumerable.Empty<LibraryItem>();
		}
		return Library.Items.Where((LibraryItem item) => IsPathUnderFolder(item.FilePath, folderPath));
	}

	private static bool IsPathUnderFolder(string filePath, string folderPath)
	{
		try
		{
			string normalizedFile = Path.GetFullPath(filePath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
			string normalizedFolder = Path.GetFullPath(folderPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
			return normalizedFile.StartsWith(normalizedFolder, StringComparison.OrdinalIgnoreCase);
		}
		catch
		{
			return false;
		}
	}

	private void OnDeckPropertyChanged(object? sender, PropertyChangedEventArgs e)
	{
		if (e.PropertyName == "Volume")
		{
			UpdateCrossfadeVolumes();
		}
	}

	private void OnStreamConfigPropertyChanged(object? sender, PropertyChangedEventArgs e)
	{
		SaveStreamSettings();
		OnPropertyChanged("StreamEndpointText");
	}

	private void OnLibraryChanged()
	{
		EnsureAutoDjQueue();
	}

	private void UpdateCrossfadeVolumes()
	{
		DeckA.SetVolumeFactor(1.0 - Crossfade);
		DeckB.SetVolumeFactor(Crossfade);
	}

	private void OnPlaybackUpdated(DeckId deckId, DeckPlaybackState state, TimeSpan position, TimeSpan duration)
	{
		if (deckId == DeckId.A)
		{
			ProgramPlayer.UpdateProgress(position, duration, state);
			UpdateHeaderStatus();
		}
		else
		{
			DeckB.UpdateProgress(position, duration, state);
		}
	}

	private void OnTrackEnded(DeckId deckId)
	{
		if (deckId != DeckId.A)
		{
			return;
		}
		PreparePostTrackQueue();
		EnsureAutoDjQueue();
		if (Queue.Count > 0)
		{
			PlayNextFromQueue();
		}
		else if (AutoScheduleEnabled)
		{
			LibraryItem item = PickNextAutomationItem();
			if (item != null && PlayLibraryItem(item))
			{
				ActivityStatus = "Auto loaded: " + item.Title;
			}
		}
	}

	private void OnPreloadedTrackStarted(DeckId deckId, string filePath)
	{
		if (deckId == DeckId.A && _preloadedProgramItem != null)
		{
			LibraryItem item = _preloadedProgramItem;
			_preloadedProgramItem = null;
			if (Queue.Count > 0 && string.Equals(Queue[0].FilePath, filePath, StringComparison.OrdinalIgnoreCase))
			{
				UnmarkAutoDjQueued(Queue[0]);
				Queue.RemoveAt(0);
			}
			_currentProgramItem = item;
			ProgramPlayer.SetTrackFromPreload(item);
			if (IsRadioId(item, RadioIdFolder))
			{
				MarkRadioIdServed();
			}
			UpdateQueueStatus();
			PreparePostTrackQueue();
			EnsureAutoDjQueue();
			RefreshGaplessPreload();
			ActivityStatus = "Now playing: " + item.Title;
			CommandManager.InvalidateRequerySuggested();
		}
	}

	private void OnPlaybackFailed(DeckId deckId, string message)
	{
		ActivityStatus = "Playback error: " + message;
	}

	private void OnStreamFailed(string message)
	{
		Dispatcher dispatcher = Application.Current?.Dispatcher;
		if (dispatcher != null && !dispatcher.CheckAccess())
		{
			dispatcher.BeginInvoke((Action)delegate
			{
				OnStreamFailed(message);
			});
		}
		else
		{
			StreamStatus = "Disconnected — stream error: " + message;
			_streamConnectedAtUtc = null;
			_streamStallRecoveryStarted = false;
			ScheduleStreamReconnect(message);
		}
	}

	private void CheckStreamHealth()
	{
		DateTimeOffset now = DateTimeOffset.UtcNow;
		DateTimeOffset? lastWrite = _streamingService.LastSuccessfulWriteUtc;
		if (_streamReconnectAttempt > 0 && _streamConnectedAtUtc.HasValue && lastWrite.HasValue && now - _streamConnectedAtUtc.Value >= TimeSpan.FromSeconds(30.0) && now - lastWrite.Value < TimeSpan.FromSeconds(5.0))
		{
			_streamReconnectAttempt = 0;
		}
		bool encoderNeverStarted = !lastWrite.HasValue && _streamConnectedAtUtc.HasValue && now - _streamConnectedAtUtc.Value >= TimeSpan.FromSeconds(20.0);
		bool writesStalled = lastWrite.HasValue && now - lastWrite.Value >= TimeSpan.FromSeconds(20.0);
		if (!_streamStallRecoveryStarted && (encoderNeverStarted || writesStalled))
		{
			_streamStallRecoveryStarted = true;
			string reason = encoderNeverStarted ? "encoder did not begin publishing" : "no successful server write for 20 seconds";
			_streamingService.Disconnect();
			StreamStatus = "Disconnected — stream stalled: " + reason;
			ScheduleStreamReconnect(reason);
		}
	}

	private async void ScheduleStreamReconnect(string reason)
	{
		if (!_streamConnectionDesired || _disposed || _streamReconnectScheduled)
		{
			return;
		}
		_streamReconnectScheduled = true;
		int attempt = ++_streamReconnectAttempt;
		int[] delays = new int[5] { 2, 5, 10, 20, 30 };
		int delaySeconds = delays[Math.Min(attempt - 1, delays.Length - 1)];
		CancellationTokenSource cancellation = new CancellationTokenSource();
		_streamReconnectCancellation = cancellation;
		StreamStatus = $"Disconnected — retrying in {delaySeconds}s (attempt {attempt}): {reason}";
		try
		{
			await Task.Delay(TimeSpan.FromSeconds(delaySeconds), cancellation.Token);
			if (!_streamConnectionDesired || _disposed)
			{
				return;
			}
			_streamReconnectScheduled = false;
			StreamStatus = $"Reconnecting... (attempt {attempt})";
			await ConnectStreamAsync(scheduleRetryOnFailure: true);
		}
		catch (OperationCanceledException)
		{
		}
		finally
		{
			if (ReferenceEquals(_streamReconnectCancellation, cancellation))
			{
				_streamReconnectCancellation = null;
				_streamReconnectScheduled = false;
			}
			cancellation.Dispose();
		}
	}

	private void CancelStreamReconnect()
	{
		CancellationTokenSource? cancellation = _streamReconnectCancellation;
		_streamReconnectCancellation = null;
		_streamReconnectScheduled = false;
		cancellation?.Cancel();
		_streamReconnectAttempt = 0;
	}

	private void PreparePostTrackQueue()
	{
		LibraryItem nextCue = Queue.FirstOrDefault();
		if ((nextCue == null || (!nextCue.IsStationIdCue && !nextCue.IsSweeperCue)) && ShouldInjectSweeper())
		{
			LibraryItem sweeper = PickSweeper();
			ResetSweeperInterval();
			if (sweeper != null)
			{
				Queue.Insert(0, sweeper);
				UpdateQueueStatus();
				ActivityStatus = "Inserted sweeper: " + sweeper.Title;
			}
		}
	}

	private void RefreshGaplessPreload()
	{
		if (!GaplessPlaybackEnabled || !ProgramPlayer.IsPlaying || Queue.Count == 0)
		{
			ClearGaplessPreload();
			UpdateAutomationCue();
			return;
		}
		LibraryItem next = Queue[0];
		if ((_preloadedProgramItem == null || !string.Equals(_preloadedProgramItem.FilePath, next.FilePath, StringComparison.OrdinalIgnoreCase)) && _audioService.PreloadNext(DeckId.A, next.FilePath, next.IsStationIdCue))
		{
			_preloadedProgramItem = next;
			UpdateAutomationCue();
		}
	}

	private void ClearGaplessPreload()
	{
		_preloadedProgramItem = null;
		_audioService.ClearPreloadedNext(DeckId.A);
	}

	private void UpdateQueueStatus()
	{
		if (!_isUpdatingQueueStatus)
		{
			_isUpdatingQueueStatus = true;
			try
			{
				TryInsertProjectedRadioId();
			}
			finally
			{
				_isUpdatingQueueStatus = false;
			}
		}
		QueueStatus = ((Queue.Count == 0) ? "Queue empty" : $"{Queue.Count} item(s) queued");
		UpdateAutomationCue();
	}

	private void ApplySettings(AppSettings settings)
	{
		_autoConnectStreamOnStartup = settings.AutoConnectStreamOnStartup;
		_sweepersEnabled = settings.SweepersEnabled;
		_sweeperFolder = settings.SweeperFolder;
		_sweeperMinSongs = Math.Max(1, settings.SweeperMinSongs);
		_sweeperMaxSongs = Math.Max(_sweeperMinSongs, settings.SweeperMaxSongs);
		_radioIdsEnabled = settings.RadioIdsEnabled;
		_radioIdFolder = settings.RadioIdFolder;
		_radioIdMinute = Math.Clamp(settings.RadioIdMinute, 0, 59);
		_radioIdToleranceBeforeMinutes = Math.Max(0, settings.RadioIdToleranceBeforeMinutes);
		_radioIdToleranceAfterMinutes = Math.Max(0, settings.RadioIdToleranceAfterMinutes);
		_gaplessPlaybackEnabled = settings.GaplessPlaybackEnabled;
		_gaplessSilenceThresholdDb = Math.Clamp(settings.GaplessSilenceThresholdDb, -72.0, -12.0);
		_gaplessSilenceHoldMilliseconds = Math.Clamp(settings.GaplessSilenceHoldMilliseconds, 100, 3000);
		_gaplessScanWindowSeconds = Math.Clamp(settings.GaplessScanWindowSeconds, 3.0, 60.0);
		_autoScheduleEnabled = settings.AutoDjEnabled;
		_autoDjFillCurrentHour = settings.AutoDjFillCurrentHour;
		_autoDjQueueDepth = Math.Max(1, settings.AutoDjQueueDepth);
		_autoDjDefaultTag = settings.AutoDjDefaultTag;
		_autoDjHourlyTagRules = settings.AutoDjHourlyTagRules;
		_selectedOutputDeviceNumber = settings.OutputDeviceNumber;
		_selectedInputDeviceNumber = settings.InputDeviceNumber;
		_audioNormalizationEnabled = settings.AudioNormalizationEnabled;
		_normalizationTargetDb = Math.Clamp(settings.NormalizationTargetDb, -24.0, 0.0);
		_normalizationMaxGainDb = Math.Clamp(settings.NormalizationMaxGainDb, 0.0, 24.0);
		_audioLimiterEnabled = settings.AudioLimiterEnabled;
		_minimizeToTray = settings.MinimizeToTray;
		_closeToTray = settings.CloseToTray;
		_startMinimizedToTray = settings.StartMinimizedToTray;
	}

	private void ApplyStreamSettings(AppSettings settings)
	{
		StreamConfig.ServerType = settings.StreamServerType;
		StreamConfig.Host = (string.IsNullOrWhiteSpace(settings.StreamHost) ? "localhost" : settings.StreamHost);
		StreamConfig.Port = ((settings.StreamPort <= 0) ? 8000 : settings.StreamPort);
		StreamConfig.Mount = ((settings.StreamServerType == StreamServerType.Icecast && string.IsNullOrWhiteSpace(settings.StreamMount)) ? "radio" : settings.StreamMount);
		StreamConfig.Username = (string.IsNullOrWhiteSpace(settings.StreamUsername) ? "source" : settings.StreamUsername);
		StreamConfig.Password = settings.StreamPassword;
		StreamConfig.Bitrate = ((settings.StreamBitrate <= 0) ? 128 : settings.StreamBitrate);
		StreamConfig.ShoutcastStreamId = Math.Max(0, settings.StreamShoutcastStreamId);
	}

	private void SaveAutomationSettings()
	{
		_settings.SweepersEnabled = SweepersEnabled;
		_settings.SweeperFolder = SweeperFolder;
		_settings.SweeperMinSongs = SweeperMinSongs;
		_settings.SweeperMaxSongs = SweeperMaxSongs;
		_settings.RadioIdsEnabled = RadioIdsEnabled;
		_settings.RadioIdFolder = RadioIdFolder;
		_settings.RadioIdMinute = RadioIdMinute;
		_settings.RadioIdToleranceBeforeMinutes = RadioIdToleranceBeforeMinutes;
		_settings.RadioIdToleranceAfterMinutes = RadioIdToleranceAfterMinutes;
		_settings.GaplessPlaybackEnabled = GaplessPlaybackEnabled;
		_settings.GaplessSilenceThresholdDb = GaplessSilenceThresholdDb;
		_settings.GaplessSilenceHoldMilliseconds = GaplessSilenceHoldMilliseconds;
		_settings.GaplessScanWindowSeconds = GaplessScanWindowSeconds;
		_settings.AutoDjEnabled = AutoScheduleEnabled;
		_settings.AutoDjFillCurrentHour = AutoDjFillCurrentHour;
		_settings.AutoDjQueueDepth = AutoDjQueueDepth;
		_settings.AutoDjDefaultTag = AutoDjDefaultTag;
		_settings.AutoDjHourlyTagRules = AutoDjHourlyTagRules;
		_settingsService.Save(_settings);
	}

	private void SaveGeneralSettings()
	{
		_settings.AutoConnectStreamOnStartup = AutoConnectStreamOnStartup;
		_settings.MinimizeToTray = MinimizeToTray;
		_settings.CloseToTray = CloseToTray;
		_settings.StartMinimizedToTray = StartMinimizedToTray;
		_settings.EnableLibraryBackgroundRefresh = Library.BackgroundRefreshEnabled;
		_settingsService.Save(_settings);
	}

	private void SaveAudioSettings()
	{
		_settings.OutputDeviceNumber = SelectedOutputDeviceNumber;
		_settings.InputDeviceNumber = SelectedInputDeviceNumber;
		_settings.AudioNormalizationEnabled = AudioNormalizationEnabled;
		_settings.NormalizationTargetDb = NormalizationTargetDb;
		_settings.NormalizationMaxGainDb = NormalizationMaxGainDb;
		_settings.AudioLimiterEnabled = AudioLimiterEnabled;
		_settingsService.Save(_settings);
		_audioService.ApplyAudioSettings(_settings);
	}

	private void SaveStreamSettings()
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
		_settingsService.Save(_settings);
	}

	private void NormalizeStreamConfig()
	{
		if (string.IsNullOrWhiteSpace(StreamConfig.Host))
		{
			StreamConfig.Host = "localhost";
		}
		if (StreamConfig.Port <= 0)
		{
			StreamConfig.Port = 8000;
		}
		if (StreamConfig.ServerType == StreamServerType.Icecast && (string.IsNullOrWhiteSpace(StreamConfig.Mount) || StreamConfig.Mount.Trim() == "/"))
		{
			StreamConfig.Mount = "radio";
		}
		if (string.IsNullOrWhiteSpace(StreamConfig.Username))
		{
			StreamConfig.Username = "source";
		}
		if (StreamConfig.Bitrate <= 0)
		{
			StreamConfig.Bitrate = 128;
		}
	}

	private void ClearLibrary()
	{
		if (MessageBox.Show(Application.Current?.MainWindow, "Clear every item from the library? Audio files on disk will not be deleted.", "Clear Library", MessageBoxButton.YesNo, MessageBoxImage.Exclamation) == MessageBoxResult.Yes)
		{
			Queue.Clear();
			_autoDjQueuedItems.Clear();
			SelectedQueueItem = null;
			ClearGaplessPreload();
			Library.ClearAll();
			UpdateQueueStatus();
			ActivityStatus = "Library cleared.";
			CommandManager.InvalidateRequerySuggested();
		}
	}

	private void ResetApp()
	{
		if (MessageBox.Show(Application.Current?.MainWindow, "Reset app settings, stream server, automation, queue, and library? Audio files on disk will not be deleted.", "Reset RadioApp", MessageBoxButton.YesNo, MessageBoxImage.Exclamation) == MessageBoxResult.Yes)
		{
			_streamConnectionDesired = false;
			CancelStreamReconnect();
			_streamingService.Disconnect();
			_streamConnectedAtUtc = null;
			_streamStallRecoveryStarted = false;
			StreamStatus = "Disconnected";
			ProgramPlayer.StopCommand.Execute(null);
			Queue.Clear();
			_autoDjQueuedItems.Clear();
			SelectedQueueItem = null;
			ClearGaplessPreload();
			Library.ClearAll();
			CopySettings(new AppSettings(), _settings);
			ApplySettings(_settings);
			ApplyStreamSettings(_settings);
			Library.ReloadMonitoringSettings();
			NotifySettingsChanged();
			_settingsService.Save(_settings);
			_audioService.ApplyAudioSettings(_settings);
			RefreshAudioDevices();
			UpdateQueueStatus();
			UpdateAutomationCue();
			ActivityStatus = "App reset complete.";
			CommandManager.InvalidateRequerySuggested();
		}
	}

	private static void CopySettings(AppSettings source, AppSettings target)
	{
		target.AutoConnectStreamOnStartup = source.AutoConnectStreamOnStartup;
		target.MinimizeToTray = source.MinimizeToTray;
		target.CloseToTray = source.CloseToTray;
		target.StartMinimizedToTray = source.StartMinimizedToTray;
		target.EnableLibraryBackgroundRefresh = source.EnableLibraryBackgroundRefresh;
		List<string> watchedLibraryFolders = source.WatchedLibraryFolders;
		int count = watchedLibraryFolders.Count;
		List<string> list = new List<string>(count);
		CollectionsMarshal.SetCount(list, count);
		Span<string> span = CollectionsMarshal.AsSpan(list);
		int num = 0;
		Span<string> span2 = CollectionsMarshal.AsSpan(watchedLibraryFolders);
		span2.CopyTo(span.Slice(num, span2.Length));
		num += span2.Length;
		target.WatchedLibraryFolders = list;
		target.SweepersEnabled = source.SweepersEnabled;
		target.SweeperFolder = source.SweeperFolder;
		target.SweeperMinSongs = source.SweeperMinSongs;
		target.SweeperMaxSongs = source.SweeperMaxSongs;
		target.RadioIdsEnabled = source.RadioIdsEnabled;
		target.RadioIdFolder = source.RadioIdFolder;
		target.RadioIdMinute = source.RadioIdMinute;
		target.RadioIdToleranceBeforeMinutes = source.RadioIdToleranceBeforeMinutes;
		target.RadioIdToleranceAfterMinutes = source.RadioIdToleranceAfterMinutes;
		target.GaplessPlaybackEnabled = source.GaplessPlaybackEnabled;
		target.GaplessSilenceThresholdDb = source.GaplessSilenceThresholdDb;
		target.GaplessSilenceHoldMilliseconds = source.GaplessSilenceHoldMilliseconds;
		target.GaplessScanWindowSeconds = source.GaplessScanWindowSeconds;
		target.AutoDjEnabled = source.AutoDjEnabled;
		target.AutoDjFillCurrentHour = source.AutoDjFillCurrentHour;
		target.AutoDjQueueDepth = source.AutoDjQueueDepth;
		target.AutoDjDefaultTag = source.AutoDjDefaultTag;
		target.AutoDjHourlyTagRules = source.AutoDjHourlyTagRules;
		target.OutputDeviceNumber = source.OutputDeviceNumber;
		target.InputDeviceNumber = source.InputDeviceNumber;
		target.AudioNormalizationEnabled = source.AudioNormalizationEnabled;
		target.NormalizationTargetDb = source.NormalizationTargetDb;
		target.NormalizationMaxGainDb = source.NormalizationMaxGainDb;
		target.AudioLimiterEnabled = source.AudioLimiterEnabled;
		target.StreamServerType = source.StreamServerType;
		target.StreamHost = source.StreamHost;
		target.StreamPort = source.StreamPort;
		target.StreamMount = source.StreamMount;
		target.StreamUsername = source.StreamUsername;
		target.StreamPassword = source.StreamPassword;
		target.StreamBitrate = source.StreamBitrate;
		target.StreamShoutcastStreamId = source.StreamShoutcastStreamId;
	}

	private void NotifySettingsChanged()
	{
		OnPropertyChanged("AutoConnectStreamOnStartup");
		OnPropertyChanged("AutoScheduleEnabled");
		OnPropertyChanged("AutoDjFillCurrentHour");
		OnPropertyChanged("AutoDjQueueDepth");
		OnPropertyChanged("AutoDjDefaultTag");
		OnPropertyChanged("AutoDjHourlyTagRules");
		OnPropertyChanged("GaplessPlaybackEnabled");
		OnPropertyChanged("GaplessSilenceThresholdDb");
		OnPropertyChanged("GaplessSilenceHoldMilliseconds");
		OnPropertyChanged("GaplessScanWindowSeconds");
		OnPropertyChanged("SweepersEnabled");
		OnPropertyChanged("SweeperFolder");
		OnPropertyChanged("SweeperMinSongs");
		OnPropertyChanged("SweeperMaxSongs");
		OnPropertyChanged("RadioIdsEnabled");
		OnPropertyChanged("RadioIdFolder");
		OnPropertyChanged("RadioIdMinute");
		OnPropertyChanged("RadioIdToleranceBeforeMinutes");
		OnPropertyChanged("RadioIdToleranceAfterMinutes");
		OnPropertyChanged("SelectedOutputDeviceNumber");
		OnPropertyChanged("SelectedInputDeviceNumber");
		OnPropertyChanged("AudioNormalizationEnabled");
		OnPropertyChanged("NormalizationTargetDb");
		OnPropertyChanged("NormalizationMaxGainDb");
		OnPropertyChanged("AudioLimiterEnabled");
		OnPropertyChanged("MinimizeToTray");
		OnPropertyChanged("CloseToTray");
		OnPropertyChanged("StartMinimizedToTray");
	}

	private void RefreshAudioDevices()
	{
		OutputAudioDevices.Clear();
		foreach (AudioDeviceInfo device in AudioPlaybackService.GetOutputDevices())
		{
			OutputAudioDevices.Add(device);
		}
		InputAudioDevices.Clear();
		foreach (AudioDeviceInfo device2 in AudioPlaybackService.GetInputDevices())
		{
			InputAudioDevices.Add(device2);
		}
		if (!OutputAudioDevices.Any((AudioDeviceInfo audioDeviceInfo) => audioDeviceInfo.DeviceNumber == SelectedOutputDeviceNumber))
		{
			SelectedOutputDeviceNumber = -1;
		}
		if (!InputAudioDevices.Any((AudioDeviceInfo audioDeviceInfo) => audioDeviceInfo.DeviceNumber == SelectedInputDeviceNumber))
		{
			SelectedInputDeviceNumber = -1;
		}
	}

	private void UpdateAutomationCue()
	{
		LibraryItem next = _preloadedProgramItem ?? Queue.FirstOrDefault();
		string nextText = ((next == null) ? "Next: none" : ("Next: " + next.Title));
		string gaplessText = ((_preloadedProgramItem == null) ? "not preloaded" : "preloaded");
		string idText = GetRadioIdCueText();
		string sweeperText = GetSweeperCueText();
		AutoDjRotationStatus rotation = GetActiveAutoDjRotation();
		_ = rotation.Tag;
		AutomationCue = $"{nextText} ({gaplessText}) | {idText} | {sweeperText}";
		NextQueueCue = ((next == null) ? "Next: none" : $"Next: {next.Title} ({gaplessText})");
		NextRadioIdCue = idText;
		NextSweeperCue = sweeperText;
		RotationCue = rotation.Description;
	}

	private string GetActiveAutoDjTag()
	{
		return GetActiveAutoDjRotation().Tag;
	}

	private AutoDjRotationStatus GetActiveAutoDjRotation()
	{
		TimeSpan now = DateTime.Now.TimeOfDay;
		foreach (AutoDjTagRule rule in ParseAutoDjTagRules())
		{
			if (IsTimeInRule(now, rule.Start, rule.End))
			{
				return new AutoDjRotationStatus(rule.Tag, $"Rotation: {rule.Tag} ({FormatRuleTime(rule.Start)}-{FormatRuleTime(rule.End)})");
			}
		}
		string defaultTag = AutoDjDefaultTag.Trim();
		if (!string.IsNullOrWhiteSpace(defaultTag))
		{
			return new AutoDjRotationStatus(defaultTag, "Rotation: " + defaultTag + " (default)");
		}
		return new AutoDjRotationStatus(string.Empty, "Rotation: unrestricted");
	}

	private IEnumerable<AutoDjTagRule> ParseAutoDjTagRules()
	{
		string[] lines = AutoDjHourlyTagRules.Split(new string[3] { "\r\n", "\n", ";" }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
		string[] array = lines;
		for (int i = 0; i < array.Length; i++)
		{
			string[] parts = array[i].Split('=', 2, StringSplitOptions.TrimEntries);
			if (parts.Length == 2 && !string.IsNullOrWhiteSpace(parts[1]))
			{
				string[] range = parts[0].Split('-', 2, StringSplitOptions.TrimEntries);
				if (range.Length == 2 && TryParseRuleTime(range[0], out var start) && TryParseRuleTime(range[1], out var end))
				{
					yield return new AutoDjTagRule(start, end, parts[1].Trim());
				}
			}
		}
	}

	private static bool TryParseRuleTime(string text, out TimeSpan value)
	{
		if (int.TryParse(text, out var hour) && hour >= 0 && hour <= 24)
		{
			value = TimeSpan.FromHours((hour != 24) ? hour : 0);
			return true;
		}
		return TimeSpan.TryParse(text, out value);
	}

	private static bool IsTimeInRule(TimeSpan now, TimeSpan start, TimeSpan end)
	{
		if (!(end > start))
		{
			if (!(now >= start))
			{
				return now < end;
			}
			return true;
		}
		if (now >= start)
		{
			return now < end;
		}
		return false;
	}

	private static string FormatRuleTime(TimeSpan value)
	{
		return $"{(int)value.TotalHours:00}:{value.Minutes:00}";
	}

	private string GetRadioIdCueText()
	{
		if (!RadioIdsEnabled)
		{
			return "ID off";
		}
		var queuedAt = Queue.Select((LibraryItem item, int index) => new { item, index }).FirstOrDefault(entry => IsRadioId(entry.item, RadioIdFolder));
		if (queuedAt != null)
		{
			return $"Next ID queued in {queuedAt.index + 1} item(s)";
		}
		DateTime now = DateTime.Now;
		DateTime nextId = GetNextRadioIdTime(now);
		LibraryItem? libraryItem = EnsurePendingRadioId(nextId);
		TimeSpan until = nextId - now;
		if (libraryItem != null)
		{
			return $"ID pending :{RadioIdMinute:00} in {FormatClockSpan(until)}";
		}
		return "No ID file found";
	}

	private LibraryItem? EnsurePendingRadioId(DateTime nextId)
	{
		if (!RadioIdsEnabled)
		{
			ClearPendingRadioId();
			return null;
		}
		if (_pendingRadioId != null && _pendingRadioIdTime == nextId)
		{
			return _pendingRadioId;
		}
		_pendingRadioId = PickRadioId();
		_pendingRadioIdTime = ((_pendingRadioId == null) ? DateTime.MinValue : nextId);
		return _pendingRadioId;
	}

	private void ClearPendingRadioId()
	{
		_pendingRadioId = null;
		_pendingRadioIdTime = DateTime.MinValue;
	}

	private string GetSweeperCueText()
	{
		if (!SweepersEnabled)
		{
			return "Jingles off";
		}
		if (!string.IsNullOrWhiteSpace(SweeperFolder) && !Directory.Exists(SweeperFolder))
		{
			return "Jingle folder unavailable";
		}
		int songsRemaining = Math.Max(0, _songsUntilSweeper - _songsSinceSweeper);
		var queuedAt = Queue.Select((LibraryItem item, int index) => new { item, index }).FirstOrDefault(entry => IsSweeper(entry.item, SweeperFolder));
		if (queuedAt != null)
		{
			return $"Next jingle queued in {queuedAt.index + 1} item(s)";
		}
		if (songsRemaining > 0)
		{
			return $"Next jingle in {songsRemaining} song(s)";
		}
		return "Next jingle due";
	}

	private void UpdateHeaderStatus()
	{
		DateTime now = DateTime.Now;
		TimeSpan untilHour = new DateTime(now.Year, now.Month, now.Day, now.Hour, 0, 0).AddHours(1.0) - now;
		TimeSpan remaining = ((ProgramPlayer.Duration > ProgramPlayer.Position) ? (ProgramPlayer.Duration - ProgramPlayer.Position) : TimeSpan.Zero);
		string trackText = (ProgramPlayer.IsTrackReady ? ("Track left " + FormatClockSpan(remaining)) : "No track loaded");
		HeaderStatus = $"{now:h:mm:ss tt} | {trackText} | Next hour in {FormatClockSpan(untilHour)} | Stream: {StreamStatus} | {ActivityStatus}";
	}

	private static string FormatClockSpan(TimeSpan value)
	{
		if (!(value.TotalHours >= 1.0))
		{
			return value.ToString("m\\:ss");
		}
		return value.ToString("h\\:mm\\:ss");
	}

	private void EnsureAutoDjQueue()
	{
		if (!AutoScheduleEnabled)
		{
			return;
		}
		int added = 0;
		if (TryInsertProjectedRadioId())
		{
			added++;
		}
		int guard = 0;
		while (ShouldAutoDjAddTrack() && guard < 100)
		{
			LibraryItem item = PickNextAutomationItem();
			if (item == null || (AutoDjFillCurrentHour && WouldOverfillCurrentHour(item) && Queue.Count > 0))
			{
				break;
			}
			Queue.Add(item);
			MarkAutoDjQueued(item);
			added++;
			guard++;
			if (TryAppendPlannedSweeper(item))
			{
				added++;
			}
			if (TryInsertProjectedRadioId())
			{
				added++;
			}
		}
		if (added > 0)
		{
			UpdateQueueStatus();
			RefreshGaplessPreload();
			ActivityStatus = (AutoDjFillCurrentHour ? $"Auto DJ added {added} track(s) for the hour." : $"Auto DJ added {added} track(s).");
			CommandManager.InvalidateRequerySuggested();
		}
	}

	private bool ShouldAutoDjAddTrack()
	{
		if (Queue.Any((LibraryItem item) => IsRadioId(item, RadioIdFolder)))
		{
			return false;
		}
		if (!AutoDjFillCurrentHour)
		{
			return Queue.Count < AutoDjQueueDepth;
		}
		return GetQueueCoverage() < GetAutoDjTargetCoverage() - TimeSpan.FromSeconds(30.0);
	}

	private void MarkAutoDjQueued(LibraryItem item)
	{
		if (!item.IsSweeperCue && !item.IsStationIdCue)
		{
			_autoDjQueuedItems.Add(item);
		}
	}

	private void UnmarkAutoDjQueued(LibraryItem item)
	{
		_autoDjQueuedItems.Remove(item);
	}

	private void RemoveQueuedAutoDjItemsForExpiredRotation()
	{
		if (_autoDjQueuedItems.Count == 0)
		{
			return;
		}
		bool removedPreload = false;
		for (int index = Queue.Count - 1; index >= 0; index--)
		{
			LibraryItem item = Queue[index];
			if (_autoDjQueuedItems.Remove(item))
			{
				if (_preloadedProgramItem == item)
				{
					removedPreload = true;
				}
				Queue.RemoveAt(index);
			}
		}
		if (removedPreload)
		{
			ClearGaplessPreload();
		}
		UpdateQueueStatus();
		RefreshGaplessPreload();
		CommandManager.InvalidateRequerySuggested();
	}

	private void MaintainAutoDjHour()
	{
		UpdateHeaderStatus();
		UpdateAutomationCue();
		if (!AutoScheduleEnabled)
		{
			return;
		}
		DateTime currentHour = GetCurrentHour();
		if (currentHour != _lastAutoDjHour)
		{
			_lastAutoDjHour = currentHour;
			string currentRotationTag = GetActiveAutoDjTag();
			if (!string.Equals(currentRotationTag, _lastAutoDjRotationTag, StringComparison.OrdinalIgnoreCase))
			{
				RemoveQueuedAutoDjItemsForExpiredRotation();
				_lastAutoDjRotationTag = currentRotationTag;
			}
			EnsureAutoDjQueue();
		}
		else if (AutoDjFillCurrentHour && ShouldAutoDjAddTrack())
		{
			EnsureAutoDjQueue();
		}
		else if (TryInsertProjectedRadioId())
		{
			UpdateQueueStatus();
			RefreshGaplessPreload();
			CommandManager.InvalidateRequerySuggested();
		}
	}

	private static DateTime GetCurrentHour()
	{
		DateTime now = DateTime.Now;
		return new DateTime(now.Year, now.Month, now.Day, now.Hour, 0, 0);
	}

	private TimeSpan GetQueueCoverage()
	{
		return Queue.Aggregate(TimeSpan.Zero, (TimeSpan total, LibraryItem item) => total + ((item.Duration > TimeSpan.Zero) ? item.Duration : TimeSpan.FromMinutes(3.0)));
	}

	private bool TryAppendPlannedSweeper(LibraryItem previousItem)
	{
		if (!SweepersEnabled || previousItem.IsSweeperCue || previousItem.IsStationIdCue)
		{
			return false;
		}
		_songsSinceSweeper++;
		if (_songsSinceSweeper < _songsUntilSweeper)
		{
			return false;
		}
		LibraryItem sweeper = PickSweeper();
		ResetSweeperInterval();
		if (sweeper == null)
		{
			ActivityStatus = "No jingle files found; check sweeper folder access and audio duration.";
			UpdateAutomationCue();
			return false;
		}
		Queue.Add(sweeper);
		return true;
	}

	private bool TryInsertProjectedRadioId()
	{
		if (!RadioIdsEnabled)
		{
			return false;
		}
		DateTime nextId = GetNextRadioIdTime(DateTime.Now);
		DateTime windowStart = nextId.AddMinutes(-RadioIdToleranceBeforeMinutes);
		DateTime windowEnd = nextId.AddMinutes(RadioIdToleranceAfterMinutes);
		if (Queue.Any((LibraryItem item) => IsRadioId(item, RadioIdFolder)) || HasQueuedRadioIdForWindow(windowStart, windowEnd))
		{
			ClearPendingRadioId();
			return false;
		}
		LibraryItem radioId = EnsurePendingRadioId(nextId);
		if (radioId == null)
		{
			return false;
		}
		int? insertIndex = GetRadioIdInsertIndex(windowStart, windowEnd);
		if (!insertIndex.HasValue)
		{
			return false;
		}
		Queue.Insert(insertIndex.Value, radioId);
		RemoveQueueItemsAfter(insertIndex.Value);
		ClearPendingRadioId();
		return true;
	}

	private void RemoveQueueItemsAfter(int index)
	{
		while (Queue.Count > index + 1)
		{
			UnmarkAutoDjQueued(Queue[index + 1]);
			Queue.RemoveAt(index + 1);
		}
	}

	private int? GetRadioIdInsertIndex(DateTime windowStart, DateTime windowEnd)
	{
		DateTime cursor = DateTime.Now + GetCurrentRemaining();
		for (int i = 0; i < Queue.Count; i++)
		{
			DateTime itemEnd = cursor + GetPlannedDuration(Queue[i]);
			if (cursor <= windowEnd && itemEnd >= windowStart)
			{
				return i + 1;
			}
			cursor = itemEnd;
		}
		if (!(cursor >= windowStart) || !(cursor <= windowEnd))
		{
			return null;
		}
		return Queue.Count;
	}

	private DateTime GetNextRadioIdTime(DateTime now)
	{
		DateTime nextId = new DateTime(now.Year, now.Month, now.Day, now.Hour, RadioIdMinute, 0);
		if (nextId.AddMinutes(RadioIdToleranceAfterMinutes) < now)
		{
			nextId = nextId.AddHours(1.0);
		}
		DateTime scheduledHour = new DateTime(nextId.Year, nextId.Month, nextId.Day, nextId.Hour, 0, 0);
		if (_lastRadioIdHour == scheduledHour)
		{
			nextId = nextId.AddHours(1.0);
		}
		return nextId;
	}

	private bool HasQueuedRadioIdForWindow(DateTime windowStart, DateTime windowEnd)
	{
		DateTime cursor = DateTime.Now + GetCurrentRemaining();
		foreach (LibraryItem item in Queue)
		{
			TimeSpan duration = GetPlannedDuration(item);
			if (IsRadioId(item, RadioIdFolder) && cursor >= windowStart && cursor <= windowEnd)
			{
				return true;
			}
			cursor += duration;
		}
		return false;
	}

	private TimeSpan GetCurrentAndQueueRemaining()
	{
		return GetCurrentRemaining() + GetQueueCoverage();
	}

	private TimeSpan GetCurrentRemaining()
	{
		if (ProgramPlayer.IsPlaying && ProgramPlayer.Duration > ProgramPlayer.Position)
		{
			return ProgramPlayer.Duration - ProgramPlayer.Position;
		}
		return TimeSpan.Zero;
	}

	private static TimeSpan GetPlannedDuration(LibraryItem item)
	{
		if (!(item.Duration > TimeSpan.Zero))
		{
			return TimeSpan.FromMinutes(3.0);
		}
		return item.Duration;
	}

	private TimeSpan GetAutoDjTargetCoverage()
	{
		DateTime now = DateTime.Now;
		TimeSpan timeSpan = new DateTime(now.Year, now.Month, now.Day, now.Hour, 0, 0).AddHours(1.0) - now;
		TimeSpan currentRemaining = GetCurrentRemaining();
		TimeSpan target = timeSpan - currentRemaining;
		if (!(target > TimeSpan.Zero))
		{
			return TimeSpan.Zero;
		}
		return target;
	}

	private bool WouldOverfillCurrentHour(LibraryItem item)
	{
		TimeSpan target = GetAutoDjTargetCoverage();
		TimeSpan queueCoverage = GetQueueCoverage();
		TimeSpan overage = queueCoverage + GetPlannedDuration(item) - target;
		if (!(queueCoverage >= target))
		{
			return overage > TimeSpan.FromMinutes(2.0);
		}
		return true;
	}

	private bool ShouldInjectSweeper()
	{
		if (!SweepersEnabled || IsSweeper(_currentProgramItem, SweeperFolder) || IsRadioId(_currentProgramItem, RadioIdFolder))
		{
			return false;
		}
		_songsSinceSweeper++;
		return _songsSinceSweeper >= _songsUntilSweeper;
	}

	private LibraryItem? PickSweeper()
	{
		LibraryItem sweeper = Library.GetRandomSweeper(SweeperFolder, TimeSpan.FromSeconds(3.0), TimeSpan.FromSeconds(10.0));
		if (sweeper != null)
		{
			return AsCueItem(sweeper, "Sweeper");
		}
		return null;
	}

	private bool ShouldInjectRadioId()
	{
		if (!RadioIdsEnabled || IsSweeper(_currentProgramItem, SweeperFolder) || IsRadioId(_currentProgramItem, RadioIdFolder))
		{
			return false;
		}
		DateTime now = DateTime.Now;
		DateTime scheduledAt = new DateTime(now.Year, now.Month, now.Day, now.Hour, RadioIdMinute, 0);
		DateTime windowStart = scheduledAt.AddMinutes(-RadioIdToleranceBeforeMinutes);
		DateTime windowEnd = scheduledAt.AddMinutes(RadioIdToleranceAfterMinutes);
		if (now < windowStart || now > windowEnd)
		{
			return false;
		}
		DateTime scheduledHour = new DateTime(scheduledAt.Year, scheduledAt.Month, scheduledAt.Day, scheduledAt.Hour, 0, 0);
		return _lastRadioIdHour != scheduledHour;
	}

	private LibraryItem? PickRadioId()
	{
		LibraryItem radioId = Library.GetRandomRadioId(RadioIdFolder, TimeSpan.FromSeconds(3.0), TimeSpan.FromSeconds(12.0));
		if (radioId != null)
		{
			return AsCueItem(radioId, "StationId");
		}
		return null;
	}

	private static LibraryItem AsCueItem(LibraryItem item, string category)
	{
		if (string.Equals(item.Category, category, StringComparison.OrdinalIgnoreCase))
		{
			return item;
		}
		return new LibraryItem
		{
			Id = item.Id,
			FilePath = item.FilePath,
			Title = item.Title,
			Artist = item.Artist,
			Album = item.Album,
			Category = category,
			Tags = item.Tags,
			Duration = item.Duration,
			DateAdded = item.DateAdded
		};
	}

	private void ResetSweeperInterval()
	{
		_songsSinceSweeper = 0;
		_songsUntilSweeper = PickSweeperInterval();
	}

	private int PickSweeperInterval()
	{
		int min = Math.Max(1, SweeperMinSongs);
		int max = Math.Max(min, SweeperMaxSongs);
		return Random.Shared.Next(min, max + 1);
	}

	private void ResetRadioIdInterval()
	{
		MarkRadioIdServed();
	}

	private void MarkRadioIdServed()
	{
		DateTime now = DateTime.Now;
		DateTime scheduledAt = new DateTime(now.Year, now.Month, now.Day, now.Hour, RadioIdMinute, 0);
		DateTime nextScheduledAt = scheduledAt.AddHours(1.0);
		if (now >= nextScheduledAt.AddMinutes(-RadioIdToleranceBeforeMinutes))
		{
			scheduledAt = nextScheduledAt;
		}
		_lastRadioIdHour = new DateTime(scheduledAt.Year, scheduledAt.Month, scheduledAt.Day, scheduledAt.Hour, 0, 0);
	}

	private static bool IsSweeper(LibraryItem? item, string sweeperFolder)
	{
		if (item == null)
		{
			return false;
		}
		if (!string.IsNullOrWhiteSpace(sweeperFolder))
		{
			try
			{
				string normalizedFile = Path.GetFullPath(item.FilePath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
				string normalizedFolder = Path.GetFullPath(sweeperFolder).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
				if (normalizedFile.StartsWith(normalizedFolder, StringComparison.OrdinalIgnoreCase))
				{
					return true;
				}
			}
			catch
			{
			}
		}
		if (!string.Equals(item.Category, "Sweeper", StringComparison.OrdinalIgnoreCase))
		{
			return string.Equals(item.Category, "Sweep", StringComparison.OrdinalIgnoreCase);
		}
		return true;
	}

	private static bool IsRadioId(LibraryItem? item, string radioIdFolder)
	{
		if (item == null)
		{
			return false;
		}
		if (!string.IsNullOrWhiteSpace(radioIdFolder))
		{
			try
			{
				string normalizedFile = Path.GetFullPath(item.FilePath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
				string normalizedFolder = Path.GetFullPath(radioIdFolder).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
				if (normalizedFile.StartsWith(normalizedFolder, StringComparison.OrdinalIgnoreCase))
				{
					return true;
				}
			}
			catch
			{
			}
		}
		if (!string.Equals(item.Category, "StationId", StringComparison.OrdinalIgnoreCase) && !string.Equals(item.Category, "Station ID", StringComparison.OrdinalIgnoreCase))
		{
			return string.Equals(item.Category, "ID", StringComparison.OrdinalIgnoreCase);
		}
		return true;
	}

	public void Dispose()
	{
		_disposed = true;
		_streamConnectionDesired = false;
		CancelStreamReconnect();
		_clockTimer.Stop();
		_automationTimer.Stop();
		Schedule.ScheduleDue -= OnScheduleDue;
		Library.LibraryChanged -= OnLibraryChanged;
		StreamConfig.PropertyChanged -= OnStreamConfigPropertyChanged;
		_audioService.ProgramOutputAvailable -= _streamingService.EnqueueProgramOutput;
		_streamingService.StreamFailed -= OnStreamFailed;
		_audioService.PlaybackUpdated -= OnPlaybackUpdated;
		_audioService.TrackEnded -= OnTrackEnded;
		_audioService.PreloadedTrackStarted -= OnPreloadedTrackStarted;
		_audioService.PlaybackFailed -= OnPlaybackFailed;
		_streamingService.Dispose();
		_audioService.Dispose();
		Library.Dispose();
		Schedule.Dispose();
	}
}
