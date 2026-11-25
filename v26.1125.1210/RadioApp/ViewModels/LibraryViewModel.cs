using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Data;
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

public sealed class LibraryViewModel : ViewModelBase, IDisposable
{
	private readonly RadioDbContextFactory _dbFactory;
	private readonly AppSettings _settings;
	private readonly AppSettingsService _settingsService;
	private readonly PlaysetService _playsetService;
	private readonly RotationService _rotationService;
	private readonly BackgroundAnalysisService _analysisService;
	private readonly AudioPlaybackService _audioService;

	private readonly List<FileSystemWatcher> _watchers = new List<FileSystemWatcher>();
	private readonly DispatcherTimer _refreshDebounceTimer;
	private readonly DispatcherTimer _searchDebounceTimer;
	private readonly Queue<string> _recentSweepers = new Queue<string>();
	private readonly Queue<string> _recentRadioIds = new Queue<string>();

	private string _filterText = string.Empty;
	private string _selectedCategory = "All categories";
	private string _selectedStatusFilter = "All";
	private string _importStatus = "Ready";
	private string _analysisStatus = "Analyzer idle";
	private bool _isAnalyzing;
	private bool _isImporting;
	private bool _backgroundRefreshEnabled;
	private bool _isBackgroundSyncRunning;
	private bool _pendingBackgroundRefresh;
	private LibraryItem? _selectedItem;
	private Playset? _selectedPlayset;
	private string? _selectedWatchedFolder;
	private bool _isPflPlaying;

	// Multi-select & bulk edit state
	private readonly List<LibraryItem> _multiSelection = new List<LibraryItem>();
	private string _bulkCategory = "Music";
	private string _bulkTagToAdd = string.Empty;

	public ObservableCollection<LibraryItem> Items { get; } = new ObservableCollection<LibraryItem>();
	public ICollectionView ItemsView { get; }
	public ObservableCollection<string> CategoryOptions { get; } = new ObservableCollection<string>();
	public ObservableCollection<string> StatusFilterOptions { get; } = new ObservableCollection<string> { "All", "Available", "Missing", "Unanalyzed" };
	public ObservableCollection<string> WatchedFolders { get; } = new ObservableCollection<string>();
	public ObservableCollection<Playset> Playsets { get; } = new ObservableCollection<Playset>();
	public ObservableCollection<LibraryItem> ActivePlaysetTracks { get; } = new ObservableCollection<LibraryItem>();

	public PlaysetService PlaysetService => _playsetService;
	public RotationService RotationService => _rotationService;

	public string FilterText
	{
		get => _filterText;
		set
		{
			if (SetProperty(ref _filterText, value ?? string.Empty))
			{
				_searchDebounceTimer.Stop();
				_searchDebounceTimer.Start();
			}
		}
	}

	public string SelectedCategory
	{
		get => _selectedCategory;
		set
		{
			if (SetProperty(ref _selectedCategory, value ?? "All categories"))
			{
				ApplyFilter();
			}
		}
	}

	public string SelectedStatusFilter
	{
		get => _selectedStatusFilter;
		set
		{
			if (SetProperty(ref _selectedStatusFilter, value ?? "All"))
			{
				ApplyFilter();
			}
		}
	}

	public Playset? SelectedPlayset
	{
		get => _selectedPlayset;
		set
		{
			if (SetProperty(ref _selectedPlayset, value))
			{
				RefreshActivePlaysetTracks();
				ApplyFilter();
				OnPropertyChanged(nameof(IsPlaysetViewActive));
				OnPropertyChanged(nameof(IsManualPlaysetSelected));
				RelayCommand.InvalidateAll();
			}
		}
	}

	public bool IsPlaysetViewActive => SelectedPlayset != null;
	public bool IsManualPlaysetSelected => SelectedPlayset?.Kind == PlaysetKind.Manual;

	public LibraryItem? SelectedItem
	{
		get => _selectedItem;
		set
		{
			if (SetProperty(ref _selectedItem, value))
			{
				OnPropertyChanged(nameof(HasSelectedItem));
				RelayCommand.InvalidateAll();
			}
		}
	}

	public bool HasSelectedItem => SelectedItem != null;
	public int MultiSelectedCount => _multiSelection.Count;
	public bool HasMultiSelection => _multiSelection.Count > 1;

	public string BulkCategory { get => _bulkCategory; set => SetProperty(ref _bulkCategory, value); }
	public string BulkTagToAdd { get => _bulkTagToAdd; set => SetProperty(ref _bulkTagToAdd, value); }

	public string? SelectedWatchedFolder
	{
		get => _selectedWatchedFolder;
		set
		{
			if (SetProperty(ref _selectedWatchedFolder, value))
			{
				RelayCommand.InvalidateAll();
			}
		}
	}

	public string ImportStatus { get => _importStatus; set => SetProperty(ref _importStatus, value); }
	public string AnalysisStatus { get => _analysisStatus; set => SetProperty(ref _analysisStatus, value); }
	public bool IsAnalyzing { get => _isAnalyzing; private set => SetProperty(ref _isAnalyzing, value); }
	public bool IsPflPlaying { get => _isPflPlaying; private set => SetProperty(ref _isPflPlaying, value); }

	public bool IsImporting
	{
		get => _isImporting;
		private set
		{
			if (SetProperty(ref _isImporting, value))
			{
				RelayCommand.InvalidateAll();
			}
		}
	}

	public bool BackgroundRefreshEnabled
	{
		get => _backgroundRefreshEnabled;
		set
		{
			if (SetProperty(ref _backgroundRefreshEnabled, value))
			{
				_settings.EnableLibraryBackgroundRefresh = value;
				_settingsService.Save(_settings);
				_refreshDebounceTimer.Stop();
				RestartFolderWatchers();
				ImportStatus = value ? "Background folder watcher enabled." : "Background folder watcher paused.";
			}
		}
	}

	public int TotalItemCount => Items.Count;
	public int VisibleItemCount => ItemsView.Cast<object>().Count();
	public int MissingItemCount => Items.Count(i => !i.FileExistsCached);
	public int UnanalyzedItemCount => Items.Count(i => !i.IsAnalyzed);
	public string LibrarySummary => SelectedPlayset != null
		? $"{SelectedPlayset.Name}: {VisibleItemCount:N0} tracks"
		: $"{VisibleItemCount:N0} visible / {TotalItemCount:N0} total";
	public Visibility EmptyStateVisibility => VisibleItemCount == 0 ? Visibility.Visible : Visibility.Collapsed;
	public string EmptyStateMessage => TotalItemCount == 0
		? "Your library is empty. Import tracks or folders (or drag & drop files here) to get started."
		: "No tracks match your current filter or playset criteria.";

	// Library Commands
	public ICommand RefreshCommand { get; }
	public ICommand ImportTrackCommand { get; }
	public ICommand ImportFolderCommand { get; }
	public ICommand ClearFilterCommand { get; }
	public ICommand ShowAllTracksCommand { get; }
	public ICommand SaveSelectedItemCommand { get; }
	public ICommand WriteTagsToFileCommand { get; }
	public ICommand AuditionSelectedCommand { get; }
	public ICommand StopAuditionCommand { get; }
	public ICommand ReanalyzeSelectedCommand { get; }
	public ICommand AnalyzeAllUnanalyzedCommand { get; }
	public ICommand VerifyFilesHealthCommand { get; }
	public ICommand RemoveMissingFilesCommand { get; }
	public ICommand RelocateFolderCommand { get; }
	public ICommand FindDuplicatesCommand { get; }

	// Bulk Edit Commands
	public ICommand BulkApplyCategoryCommand { get; }
	public ICommand BulkAddTagCommand { get; }
	public ICommand BulkAddToPlaysetCommand { get; }

	// Watched Folder Commands
	public ICommand AddWatchedFolderCommand { get; }
	public ICommand RemoveWatchedFolderCommand { get; }
	public ICommand RescanWatchedFoldersCommand { get; }

	// Playset Commands
	public ICommand CreateManualPlaysetCommand { get; }
	public ICommand CreateSmartPlaysetCommand { get; }
	public ICommand EditSelectedPlaysetCommand { get; }
	public ICommand DeleteSelectedPlaysetCommand { get; }
	public ICommand RemoveSelectedFromPlaysetCommand { get; }
	public ICommand MovePlaysetTrackUpCommand { get; }
	public ICommand MovePlaysetTrackDownCommand { get; }
	public ICommand ImportPlaylistFileCommand { get; }
	public ICommand ExportSelectedPlaysetCommand { get; }

	public event Action? LibraryChanged;
	public event Action<LibraryItem>? TrackAnalysisUpdated;

	public LibraryViewModel(
		RadioDbContextFactory dbFactory,
		AppSettings settings,
		AppSettingsService settingsService,
		PlaysetService playsetService,
		RotationService rotationService,
		BackgroundAnalysisService analysisService,
		AudioPlaybackService audioService)
	{
		_dbFactory = dbFactory;
		_settings = settings;
		_settingsService = settingsService;
		_playsetService = playsetService;
		_rotationService = rotationService;
		_analysisService = analysisService;
		_audioService = audioService;
		_backgroundRefreshEnabled = settings.EnableLibraryBackgroundRefresh;

		ItemsView = CollectionViewSource.GetDefaultView(Items);
		ItemsView.SortDescriptions.Add(new SortDescription(nameof(LibraryItem.Title), ListSortDirection.Ascending));

		RefreshCommand = new RelayCommand(Refresh);
		ImportTrackCommand = new RelayCommand(ImportTrack);
		ImportFolderCommand = new RelayCommand(async () => await ImportFolderAsync(), () => !IsImporting);
		ClearFilterCommand = new RelayCommand(ClearFilter);
		ShowAllTracksCommand = new RelayCommand(() => { SelectedPlayset = null; ClearFilter(); });
		SaveSelectedItemCommand = new RelayCommand(() => { if (SelectedItem != null) PersistItemUpdate(SelectedItem); }, () => SelectedItem != null);
		WriteTagsToFileCommand = new RelayCommand(WriteSelectedTagsToDisk, () => SelectedItem != null);
		AuditionSelectedCommand = new RelayCommand(AuditionSelected, () => SelectedItem != null);
		StopAuditionCommand = new RelayCommand(StopAudition);
		ReanalyzeSelectedCommand = new RelayCommand(ReanalyzeSelection, () => SelectedItem != null || _multiSelection.Count > 0);
		AnalyzeAllUnanalyzedCommand = new RelayCommand(() =>
		{
			_analysisService.EnqueueUnanalyzed(Items.ToList(), forceReanalyze: false);
			ImportStatus = "Queued unanalyzed tracks for background AutoMix analysis.";
		});
		VerifyFilesHealthCommand = new RelayCommand(async () => await VerifyFileHealthAsync());
		RemoveMissingFilesCommand = new RelayCommand(RemoveMissingFiles);
		RelocateFolderCommand = new RelayCommand(RelocateMovedFolder);
		FindDuplicatesCommand = new RelayCommand(FilterToDuplicates);

		BulkApplyCategoryCommand = new RelayCommand(BulkApplyCategory, () => _multiSelection.Count > 0);
		BulkAddTagCommand = new RelayCommand(BulkAddTag, () => _multiSelection.Count > 0 && !string.IsNullOrWhiteSpace(BulkTagToAdd));
		BulkAddToPlaysetCommand = new RelayCommand(p =>
		{
			if (p is Playset ps) AddSelectionToPlayset(ps);
		}, _ => _multiSelection.Count > 0 || SelectedItem != null);

		AddWatchedFolderCommand = new RelayCommand(AddWatchedFolderFromPicker);
		RemoveWatchedFolderCommand = new RelayCommand(RemoveSelectedWatchedFolder, () => !string.IsNullOrWhiteSpace(SelectedWatchedFolder));
		RescanWatchedFoldersCommand = new RelayCommand(async () => await RunWatchedFolderRefreshAsync(reportStatus: true));

		CreateManualPlaysetCommand = new RelayCommand(() => CreatePlayset(PlaysetKind.Manual));
		CreateSmartPlaysetCommand = new RelayCommand(() => CreatePlayset(PlaysetKind.SmartRule));
		EditSelectedPlaysetCommand = new RelayCommand(EditSelectedPlayset, () => SelectedPlayset != null);
		DeleteSelectedPlaysetCommand = new RelayCommand(DeleteSelectedPlayset, () => SelectedPlayset != null);
		RemoveSelectedFromPlaysetCommand = new RelayCommand(RemoveSelectedFromPlayset, () => IsManualPlaysetSelected && SelectedItem != null);
		MovePlaysetTrackUpCommand = new RelayCommand(() => MovePlaysetTrack(-1), () => IsManualPlaysetSelected && SelectedItem != null);
		MovePlaysetTrackDownCommand = new RelayCommand(() => MovePlaysetTrack(1), () => IsManualPlaysetSelected && SelectedItem != null);
		ImportPlaylistFileCommand = new RelayCommand(ImportPlaylistFile);
		ExportSelectedPlaysetCommand = new RelayCommand(ExportSelectedPlayset, () => SelectedPlayset != null);

		_refreshDebounceTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2.0) };
		_refreshDebounceTimer.Tick += async (_, _) =>
		{
			_refreshDebounceTimer.Stop();
			await RunWatchedFolderRefreshAsync(reportStatus: false);
		};

		_searchDebounceTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(120) };
		_searchDebounceTimer.Tick += (_, _) =>
		{
			_searchDebounceTimer.Stop();
			ApplyFilter();
		};

		_analysisService.TrackAnalyzed += OnBackgroundTrackAnalyzed;
		_analysisService.ProgressChanged += OnBackgroundAnalysisProgress;

		ReloadMonitoringSettings();
		Refresh();
	}

	public void UpdateMultiSelection(IEnumerable<LibraryItem> selected)
	{
		_multiSelection.Clear();
		_multiSelection.AddRange(selected.Distinct());
		OnPropertyChanged(nameof(MultiSelectedCount));
		OnPropertyChanged(nameof(HasMultiSelection));
		RelayCommand.InvalidateAll();
	}

	public IReadOnlyList<LibraryItem> GetEffectiveSelection()
	{
		if (_multiSelection.Count > 0) return _multiSelection.ToList();
		if (SelectedItem != null) return new[] { SelectedItem };
		return Array.Empty<LibraryItem>();
	}

	public void Refresh()
	{
		using RadioLibraryContext ctx = _dbFactory.CreateContext();
		List<LibraryItem> dbItems = ctx.LibraryItems.OrderBy(i => i.Title).ToList();
		Items.Clear();
		foreach (LibraryItem item in dbItems)
		{
			Items.Add(item);
		}
		_rotationService.ResetDecks();
		RefreshCategoryOptions();
		RefreshPlaysets();
		ApplyFilter();

		if (_settings.AutoAnalyzeLibraryInBackground)
		{
			_analysisService.ConfigureSilenceThreshold(_settings.GaplessSilenceThresholdDb);
			_analysisService.EnqueueUnanalyzed(Items.ToList());
		}
	}

	public void RefreshPlaysets()
	{
		int? keepId = SelectedPlayset?.Id;
		List<Playset> list = _playsetService.GetAllPlaysets(Items);
		Playsets.Clear();
		foreach (Playset ps in list)
		{
			Playsets.Add(ps);
		}
		if (keepId.HasValue)
		{
			_selectedPlayset = Playsets.FirstOrDefault(p => p.Id == keepId.Value);
			OnPropertyChanged(nameof(SelectedPlayset));
			RefreshActivePlaysetTracks();
		}
	}

	public void ReloadMonitoringSettings()
	{
		_backgroundRefreshEnabled = _settings.EnableLibraryBackgroundRefresh;
		OnPropertyChanged(nameof(BackgroundRefreshEnabled));
		WatchedFolders.Clear();
		foreach (string folder in _settings.WatchedLibraryFolders
			.Where(p => !string.IsNullOrWhiteSpace(p))
			.Select(NormalizePath)
			.Distinct(StringComparer.OrdinalIgnoreCase)
			.OrderBy(p => p))
		{
			WatchedFolders.Add(folder);
		}
		SelectedWatchedFolder = WatchedFolders.FirstOrDefault();
		RestartFolderWatchers();
	}

	public void PersistItemUpdate(LibraryItem item)
	{
		if (item.Id <= 0) return;
		try
		{
			using RadioLibraryContext ctx = _dbFactory.CreateContext();
			LibraryItem? dbItem = ctx.LibraryItems.Find(item.Id);
			if (dbItem != null)
			{
				dbItem.Title = item.Title;
				dbItem.Artist = item.Artist;
				dbItem.Album = item.Album;
				dbItem.Category = item.Category;
				dbItem.Tags = item.Tags;
				dbItem.StartCueMs = item.StartCueMs;
				dbItem.IntroCueMs = item.IntroCueMs;
				dbItem.OutroCueMs = item.OutroCueMs;
				dbItem.EndCueMs = item.EndCueMs;
				ctx.SaveChanges();
			}
			_rotationService.ResetDecks();
			RefreshCategoryOptions();
			RefreshPlaysets();
			ImportStatus = $"Saved changes to '{item.Title}'.";
		}
		catch (Exception ex)
		{
			LogService.Warn("Library", $"Failed to save item edit: {ex.Message}");
		}
	}

	public void MarkItemPlayed(LibraryItem? item)
	{
		if (item == null) return;
		DateTime now = DateTime.UtcNow;
		item.LastPlayedAtUtc = now;
		item.PlayCount++;
		_rotationService.RecordPlayed(item, now);

		if (item.Id > 0)
		{
			Task.Run(() =>
			{
				try
				{
					using RadioLibraryContext ctx = _dbFactory.CreateContext();
					LibraryItem? dbItem = ctx.LibraryItems.Find(item.Id);
					if (dbItem != null)
					{
						dbItem.LastPlayedAtUtc = now;
						dbItem.PlayCount = item.PlayCount;
						ctx.SaveChanges();
					}
				}
				catch { }
			});
		}
	}

	public LibraryItem? GetNextTrack(
		IEnumerable<string>? preferredCategories = null,
		string? tag = null,
		IEnumerable<LibraryItem>? currentlyQueued = null)
	{
		return _rotationService.PickNextTrack(Items, _settings, preferredCategories, tag, currentlyQueued);
	}

	public LibraryItem? GetNextTrackForRotation(
		ActiveRotationInfo rotation,
		IEnumerable<LibraryItem>? currentlyQueued = null,
		TimeSpan? remainingToHourEnd = null,
		bool strictNoHourSpillover = false)
	{
		IReadOnlyList<LibraryItem>? playsetTracks = null;
		if (rotation.SourceKind == RotationSourceKind.Playset && rotation.PlaysetId.HasValue)
		{
			Playset? ps = Playsets.FirstOrDefault(p => p.Id == rotation.PlaysetId.Value);
			if (ps != null)
			{
				playsetTracks = _playsetService.ResolvePlaysetTracks(ps, Items);
			}
		}
		return _rotationService.PickTrackForRotation(
			Items,
			_settings,
			rotation,
			playsetTracks,
			currentlyQueued,
			remainingToHourEnd: remainingToHourEnd,
			strictNoHourSpillover: strictNoHourSpillover);
	}

	public List<LibraryItem> SimulateRotationNextTracks(ActiveRotationInfo rotation, int count = 6)
	{
		var sim = new RotationService();
		IReadOnlyList<LibraryItem>? playsetTracks = null;
		if (rotation.SourceKind == RotationSourceKind.Playset && rotation.PlaysetId.HasValue)
		{
			Playset? ps = Playsets.FirstOrDefault(p => p.Id == rotation.PlaysetId.Value);
			if (ps != null) playsetTracks = _playsetService.ResolvePlaysetTracks(ps, Items);
		}

		var picked = new List<LibraryItem>();
		for (int i = 0; i < count; i++)
		{
			LibraryItem? item = sim.PickTrackForRotation(Items, _settings, rotation, playsetTracks, picked);
			if (item == null) break;
			picked.Add(item);
			sim.RecordPlayed(item);
		}
		return picked;
	}

	public List<string> GetAllDistinctTags()
	{
		return Items
			.SelectMany(i => (i.Tags ?? string.Empty).Split(new[] { ',', ';', '|' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
			.Where(t => !string.IsNullOrWhiteSpace(t))
			.Distinct(StringComparer.OrdinalIgnoreCase)
			.OrderBy(t => t, StringComparer.OrdinalIgnoreCase)
			.ToList();
	}

	public List<string> GetAllDistinctCategories()
	{
		return Items
			.Select(i => string.IsNullOrWhiteSpace(i.Category) ? "Uncategorized" : i.Category.Trim())
			.Distinct(StringComparer.OrdinalIgnoreCase)
			.OrderBy(c => c, StringComparer.OrdinalIgnoreCase)
			.ToList();
	}

	public int GetPlaysetTrackCount(int playsetId)
	{
		Playset? ps = Playsets.FirstOrDefault(p => p.Id == playsetId);
		return ps == null ? 0 : _playsetService.ResolvePlaysetTracks(ps, Items).Count;
	}

	public CuePoolDiagnostics EvaluateSweeperPool() =>
		ImagingPolicyService.EvaluateCuePool(
			Items,
			_settings.SweeperFolder,
			_settings.SweeperCategoryNames,
			_settings.SweeperMinDurationSeconds,
			_settings.SweeperMaxDurationSeconds,
			isStationId: false,
			excludeFolderPath: _settings.RadioIdFolder,
			excludeCategoryCsv: _settings.RadioIdCategoryNames);

	public CuePoolDiagnostics EvaluateStationIdPool() =>
		ImagingPolicyService.EvaluateCuePool(
			Items,
			_settings.RadioIdFolder,
			_settings.RadioIdCategoryNames,
			_settings.RadioIdMinDurationSeconds,
			_settings.RadioIdMaxDurationSeconds,
			isStationId: true,
			excludeFolderPath: _settings.SweeperFolder,
			excludeCategoryCsv: _settings.SweeperCategoryNames);

	public LibraryItem? GetRandomSweeper(string folderPath, TimeSpan minDur, TimeSpan maxDur)
	{
		CuePoolDiagnostics diag = ImagingPolicyService.EvaluateCuePool(
			Items, folderPath, _settings.SweeperCategoryNames, minDur.TotalSeconds, maxDur.TotalSeconds, isStationId: false,
			excludeFolderPath: _settings.RadioIdFolder, excludeCategoryCsv: _settings.RadioIdCategoryNames);
		if (diag.Eligible.Count > 0)
		{
			return PickWithRecentHistory(diag.Eligible, _recentSweepers);
		}
		return ScanFolderForShortCue(folderPath, "Sweeper", minDur, maxDur, _recentSweepers);
	}

	public LibraryItem? GetRandomRadioId(string folderPath, TimeSpan minDur, TimeSpan maxDur)
	{
		CuePoolDiagnostics diag = ImagingPolicyService.EvaluateCuePool(
			Items, folderPath, _settings.RadioIdCategoryNames, minDur.TotalSeconds, maxDur.TotalSeconds, isStationId: true,
			excludeFolderPath: _settings.SweeperFolder, excludeCategoryCsv: _settings.SweeperCategoryNames);
		if (diag.Eligible.Count > 0)
		{
			return PickWithRecentHistory(diag.Eligible, _recentRadioIds);
		}
		return ScanFolderForShortCue(folderPath, "StationId", minDur, maxDur, _recentRadioIds);
	}

	public void DeleteItems(IEnumerable<LibraryItem> items)
	{
		List<LibraryItem> targets = items.Distinct().ToList();
		if (targets.Count == 0) return;

		string msg = targets.Count == 1
			? $"Remove '{targets[0].Title}' from the library?\n(Audio files on disk will NOT be deleted.)"
			: $"Remove {targets.Count} selected tracks from the library?\n(Audio files on disk will NOT be deleted.)";

		if (MessageBox.Show(Application.Current?.MainWindow, msg, "Remove from Library", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
		{
			return;
		}

		List<int> ids = targets.Where(t => t.Id > 0).Select(t => t.Id).ToList();
		using (RadioLibraryContext ctx = _dbFactory.CreateContext())
		{
			List<LibraryItem> dbTargets = ctx.LibraryItems.Where(i => ids.Contains(i.Id)).ToList();
			ctx.LibraryItems.RemoveRange(dbTargets);
			ctx.SaveChanges();
		}

		foreach (LibraryItem t in targets) Items.Remove(t);
		_multiSelection.Clear();
		SelectedItem = null;
		_rotationService.ResetDecks();
		RefreshCategoryOptions();
		RefreshPlaysets();
		NotifySummaryChanged();
		ImportStatus = $"Removed {targets.Count} item(s) from library.";
		LibraryChanged?.Invoke();
	}

	public void ClearAll()
	{
		using (RadioLibraryContext ctx = _dbFactory.CreateContext())
		{
			ctx.LibraryItems.RemoveRange(ctx.LibraryItems);
			ctx.SaveChanges();
		}
		Items.Clear();
		ActivePlaysetTracks.Clear();
		_multiSelection.Clear();
		SelectedItem = null;
		_rotationService.ResetDecks();
		RefreshCategoryOptions();
		RefreshPlaysets();
		NotifySummaryChanged();
		ImportStatus = "Library cleared.";
		LibraryChanged?.Invoke();
	}

	public async Task ImportDroppedPathsAsync(string[] droppedPaths)
	{
		if (droppedPaths == null || droppedPaths.Length == 0 || IsImporting) return;
		var options = new ImportOptions { Category = "Music", UseInferredCategory = true, AnalyzeAfterImport = true };
		IsImporting = true;
		ImportStatus = "Importing dropped files...";
		try
		{
			var filesToScan = new List<string>();
			foreach (string p in droppedPaths)
			{
				if (Directory.Exists(p))
				{
					filesToScan.AddRange(SafeEnumerateAudioFiles(p));
				}
				else if (File.Exists(p) && IsAudioFilePath(p))
				{
					filesToScan.Add(NormalizePath(p));
				}
			}

			var existingPaths = Items.Select(i => i.FilePath).ToHashSet(StringComparer.OrdinalIgnoreCase);
			List<LibraryItem> added = await Task.Run(() => ImportFileList(filesToScan, options, existingPaths, null));
			CommitImportedItems(added, options.AnalyzeAfterImport);
			ImportStatus = $"Imported {added.Count} dropped track(s).";
		}
		finally
		{
			IsImporting = false;
		}
	}

	public async Task RunWatchedFolderRefreshAsync(bool reportStatus)
	{
		if (_isBackgroundSyncRunning)
		{
			_pendingBackgroundRefresh = true;
			return;
		}

		List<string> folders = WatchedFolders.Where(Directory.Exists).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
		if (folders.Count == 0)
		{
			if (reportStatus) ImportStatus = "No watched folders configured.";
			return;
		}

		_isBackgroundSyncRunning = true;
		_pendingBackgroundRefresh = false;
		if (reportStatus) ImportStatus = "Scanning watched folders...";

		try
		{
			var (added, updated, removed, invalid) = await Task.Run(() => SyncWatchedFolders(folders));
			if (added > 0 || updated > 0 || removed > 0)
			{
				Refresh();
				LibraryChanged?.Invoke();
				ImportStatus = $"Library sync: +{added} / ~{updated} / -{removed}" + (invalid > 0 ? $" ({invalid} skipped)" : "");
			}
			else if (reportStatus)
			{
				ImportStatus = "Library is up to date.";
			}
		}
		catch (Exception ex)
		{
			ImportStatus = "Folder sync failed: " + ex.Message;
			LogService.Error("LibrarySync", "Watched folder sync failed", ex);
		}
		finally
		{
			_isBackgroundSyncRunning = false;
			if (_pendingBackgroundRefresh)
			{
				_pendingBackgroundRefresh = false;
				ScheduleBackgroundRefresh();
			}
		}
	}

	public Playset SaveQueueAsPlayset(string name, IEnumerable<LibraryItem> queueTracks)
	{
		Playset ps = _playsetService.SavePlayset(new Playset
		{
			Name = name,
			Kind = PlaysetKind.Manual,
			Description = $"Saved from playout queue on {DateTime.Now:g}"
		});
		List<int> ids = queueTracks.Where(i => i.Id > 0).Select(i => i.Id).ToList();
		_playsetService.ReplacePlaysetTracks(ps.Id, ids);
		RefreshPlaysets();
		ImportStatus = $"Saved playset '{name}' with {ids.Count} track(s).";
		return ps;
	}

	public void AddSelectionToPlayset(Playset playset)
	{
		if (playset.Kind != PlaysetKind.Manual)
		{
			ImportStatus = $"'{playset.Name}' is a Smart Playset (tracks are matched automatically by rules).";
			return;
		}
		IReadOnlyList<LibraryItem> sel = GetEffectiveSelection();
		if (sel.Count == 0) return;

		_playsetService.AddTracksToPlayset(playset.Id, sel.Select(i => i.Id));
		RefreshPlaysets();
		ImportStatus = $"Added {sel.Count} track(s) to playset '{playset.Name}'.";
	}

	private void CreatePlayset(PlaysetKind kind)
	{
		var ps = new Playset
		{
			Name = kind == PlaysetKind.SmartRule ? "New Smart Playset" : "New Playset",
			Kind = kind,
			AccentColor = kind == PlaysetKind.SmartRule ? "#8B5CF6" : "#0284C7"
		};
		var editor = new PlaysetEditorWindow(ps, CategoryOptions.ToList())
		{
			Owner = Application.Current?.MainWindow
		};
		if (editor.ShowDialog() == true)
		{
			_playsetService.SavePlayset(ps);
			RefreshPlaysets();
			SelectedPlayset = Playsets.FirstOrDefault(p => p.Id == ps.Id);
			ImportStatus = $"Created playset '{ps.Name}'.";
		}
	}

	private void EditSelectedPlayset()
	{
		if (SelectedPlayset == null) return;
		var editor = new PlaysetEditorWindow(SelectedPlayset, CategoryOptions.ToList())
		{
			Owner = Application.Current?.MainWindow
		};
		if (editor.ShowDialog() == true)
		{
			_playsetService.SavePlayset(SelectedPlayset);
			RefreshPlaysets();
			ApplyFilter();
			ImportStatus = $"Updated playset '{SelectedPlayset.Name}'.";
		}
	}

	private void DeleteSelectedPlayset()
	{
		if (SelectedPlayset == null) return;
		string name = SelectedPlayset.Name;
		if (MessageBox.Show(Application.Current?.MainWindow,
			$"Delete playset '{name}'?\n(Tracks remain safe in your library.)",
			"Delete Playset", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
		{
			return;
		}
		_playsetService.DeletePlayset(SelectedPlayset.Id);
		SelectedPlayset = null;
		RefreshPlaysets();
		ImportStatus = $"Deleted playset '{name}'.";
	}

	private void RemoveSelectedFromPlayset()
	{
		if (SelectedPlayset == null || SelectedPlayset.Kind != PlaysetKind.Manual || SelectedItem == null) return;
		ActivePlaysetTracks.Remove(SelectedItem);
		_playsetService.ReplacePlaysetTracks(SelectedPlayset.Id, ActivePlaysetTracks.Select(t => t.Id));
		RefreshPlaysets();
		ApplyFilter();
	}

	private void MovePlaysetTrack(int delta)
	{
		if (SelectedPlayset == null || SelectedPlayset.Kind != PlaysetKind.Manual || SelectedItem == null) return;
		int idx = ActivePlaysetTracks.IndexOf(SelectedItem);
		int target = idx + delta;
		if (idx < 0 || target < 0 || target >= ActivePlaysetTracks.Count) return;

		LibraryItem item = ActivePlaysetTracks[idx];
		ActivePlaysetTracks.Move(idx, target);
		_playsetService.ReplacePlaysetTracks(SelectedPlayset.Id, ActivePlaysetTracks.Select(t => t.Id));
		SelectedItem = item;
		RefreshPlaysets();
	}

	private void ImportPlaylistFile()
	{
		var dialog = new OpenFileDialog
		{
			Title = "Import Playlist / Playset",
			Filter = "Playlists (*.m3u8;*.m3u;*.pls;*.json)|*.m3u8;*.m3u;*.pls;*.json|All files|*.*"
		};
		if (dialog.ShowDialog() != true) return;

		List<string> paths = PlaysetService.ParsePlaylistFile(dialog.FileName);
		if (paths.Count == 0)
		{
			ImportStatus = "Playlist file contained no valid track entries.";
			return;
		}

		var byPath = Items.ToDictionary(i => NormalizePath(i.FilePath), i => i, StringComparer.OrdinalIgnoreCase);
		var resolvedIds = new List<int>();
		var newItems = new List<LibraryItem>();

		foreach (string p in paths)
		{
			string norm = NormalizePath(p);
			if (byPath.TryGetValue(norm, out LibraryItem? existing))
			{
				resolvedIds.Add(existing.Id);
			}
			else if (File.Exists(norm) && IsAudioFilePath(norm))
			{
				AudioProbeResult probe = AudioFileProbeCore.Probe(norm);
				if (probe.IsPlayable)
				{
					LibraryItem created = CreateLibraryItem(norm, probe.Duration, null);
					newItems.Add(created);
					byPath[norm] = created;
				}
			}
		}

		if (newItems.Count > 0)
		{
			CommitImportedItems(newItems, analyze: true);
			resolvedIds.AddRange(newItems.Select(i => i.Id));
		}

		string playsetName = Path.GetFileNameWithoutExtension(dialog.FileName);
		Playset ps = _playsetService.SavePlayset(new Playset
		{
			Name = playsetName,
			Kind = PlaysetKind.Manual,
			Description = $"Imported from {Path.GetFileName(dialog.FileName)}"
		});
		_playsetService.ReplacePlaysetTracks(ps.Id, resolvedIds);
		RefreshPlaysets();
		SelectedPlayset = Playsets.FirstOrDefault(p => p.Id == ps.Id);
		ImportStatus = $"Imported playset '{playsetName}' ({resolvedIds.Count} tracks, {newItems.Count} newly added).";
	}

	private void ExportSelectedPlayset()
	{
		if (SelectedPlayset == null) return;
		var dialog = new SaveFileDialog
		{
			Title = "Export Playset as M3U8",
			Filter = "Extended M3U8 Playlist (*.m3u8)|*.m3u8",
			FileName = SanitizeFileName(SelectedPlayset.Name) + ".m3u8"
		};
		if (dialog.ShowDialog() != true) return;

		List<LibraryItem> tracks = _playsetService.ResolvePlaysetTracks(SelectedPlayset, Items);
		PlaysetService.ExportM3u8(dialog.FileName, SelectedPlayset.Name, tracks);
		ImportStatus = $"Exported '{SelectedPlayset.Name}' ({tracks.Count} tracks) to {Path.GetFileName(dialog.FileName)}.";
	}

	private void RefreshActivePlaysetTracks()
	{
		ActivePlaysetTracks.Clear();
		if (SelectedPlayset == null) return;
		foreach (LibraryItem item in _playsetService.ResolvePlaysetTracks(SelectedPlayset, Items))
		{
			ActivePlaysetTracks.Add(item);
		}
	}

	private void BulkApplyCategory()
	{
		IReadOnlyList<LibraryItem> sel = GetEffectiveSelection();
		if (sel.Count == 0 || string.IsNullOrWhiteSpace(BulkCategory)) return;
		string cat = BulkCategory.Trim();

		using (RadioLibraryContext ctx = _dbFactory.CreateContext())
		{
			List<int> ids = sel.Select(i => i.Id).ToList();
			foreach (LibraryItem dbItem in ctx.LibraryItems.Where(i => ids.Contains(i.Id)))
			{
				dbItem.Category = cat;
			}
			ctx.SaveChanges();
		}

		foreach (LibraryItem item in sel) item.Category = cat;
		_rotationService.ResetDecks();
		RefreshCategoryOptions();
		RefreshPlaysets();
		ImportStatus = $"Updated category to '{cat}' on {sel.Count} track(s).";
	}

	private void BulkAddTag()
	{
		IReadOnlyList<LibraryItem> sel = GetEffectiveSelection();
		string tag = BulkTagToAdd.Trim();
		if (sel.Count == 0 || string.IsNullOrWhiteSpace(tag)) return;

		using (RadioLibraryContext ctx = _dbFactory.CreateContext())
		{
			List<int> ids = sel.Select(i => i.Id).ToList();
			var dbMap = ctx.LibraryItems.Where(i => ids.Contains(i.Id)).ToDictionary(i => i.Id);
			foreach (LibraryItem item in sel)
			{
				var existing = item.Tags.Split(new[] { ',', ';', '|' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
				if (!existing.Any(t => string.Equals(t, tag, StringComparison.OrdinalIgnoreCase)))
				{
					existing.Add(tag);
					string merged = string.Join(", ", existing);
					item.Tags = merged;
					if (dbMap.TryGetValue(item.Id, out LibraryItem? dbItem))
					{
						dbItem.Tags = merged;
					}
				}
			}
			ctx.SaveChanges();
		}
		BulkTagToAdd = string.Empty;
		RefreshPlaysets();
		ImportStatus = $"Added tag '{tag}' to {sel.Count} track(s).";
	}

	private void WriteSelectedTagsToDisk()
	{
		if (SelectedItem == null || !File.Exists(SelectedItem.FilePath)) return;
		try
		{
			using TagLib.File tagFile = TagLib.File.Create(SelectedItem.FilePath);
			tagFile.Tag.Title = SelectedItem.Title;
			tagFile.Tag.Performers = string.IsNullOrWhiteSpace(SelectedItem.Artist) ? Array.Empty<string>() : new[] { SelectedItem.Artist };
			tagFile.Tag.Album = SelectedItem.Album;
			tagFile.Save();
			PersistItemUpdate(SelectedItem);
			ImportStatus = $"Wrote ID3/metadata tags to disk for '{SelectedItem.Title}'.";
		}
		catch (Exception ex)
		{
			ImportStatus = $"Tag write failed: {ex.Message}";
		}
	}

	private void AuditionSelected()
	{
		if (SelectedItem == null) return;
		TimeSpan start = TimeSpan.FromMilliseconds(SelectedItem.EffectiveStartCueMs);
		IsPflPlaying = _audioService.PlayPfl(SelectedItem.FilePath, start);
		ImportStatus = IsPflPlaying ? $"Auditioning (PFL): {SelectedItem.Title}" : "Could not audition selected track.";
	}

	private void StopAudition()
	{
		_audioService.StopPfl();
		IsPflPlaying = false;
	}

	private void ReanalyzeSelection()
	{
		IReadOnlyList<LibraryItem> sel = GetEffectiveSelection();
		if (sel.Count == 0) return;
		_analysisService.ConfigureSilenceThreshold(_settings.GaplessSilenceThresholdDb);
		_analysisService.EnqueueUnanalyzed(sel, forceReanalyze: true);
		ImportStatus = $"Queued {sel.Count} track(s) for fresh AutoMix BPM/LUFS/Cue analysis.";
	}

	private async Task VerifyFileHealthAsync()
	{
		ImportStatus = "Checking file availability across library...";
		List<LibraryItem> snapshot = Items.ToList();
		var missingIds = new List<int>();
		var presentIds = new List<int>();

		await Task.Run(() =>
		{
			foreach (LibraryItem item in snapshot)
			{
				bool exists = !string.IsNullOrWhiteSpace(item.FilePath) && File.Exists(item.FilePath);
				if (exists) presentIds.Add(item.Id);
				else missingIds.Add(item.Id);
			}

			using RadioLibraryContext ctx = _dbFactory.CreateContext();
			foreach (LibraryItem dbItem in ctx.LibraryItems)
			{
				dbItem.FileExistsCached = !missingIds.Contains(dbItem.Id);
			}
			ctx.SaveChanges();
		});

		var missingSet = missingIds.ToHashSet();
		foreach (LibraryItem item in snapshot)
		{
			item.FileExistsCached = !missingSet.Contains(item.Id);
		}
		NotifySummaryChanged();
		ImportStatus = $"Health check complete: {presentIds.Count:N0} available, {missingIds.Count:N0} missing.";
	}

	private void RemoveMissingFiles()
	{
		List<LibraryItem> missing = Items.Where(i => !i.FileExistsCached).ToList();
		if (missing.Count == 0)
		{
			ImportStatus = "No missing tracks to remove.";
			return;
		}
		DeleteItems(missing);
	}

	private void RelocateMovedFolder()
	{
		var oldDialog = new OpenFolderDialog { Title = "Select NEW location of moved audio folder" };
		if (oldDialog.ShowDialog() != true) return;
		string newRoot = NormalizePath(oldDialog.FolderName);

		var newFilesByName = SafeEnumerateAudioFiles(newRoot)
			.GroupBy(p => Path.GetFileName(p), StringComparer.OrdinalIgnoreCase)
			.ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

		int relinked = 0;
		using (RadioLibraryContext ctx = _dbFactory.CreateContext())
		{
			foreach (LibraryItem item in Items.Where(i => !i.FileExistsCached))
			{
				string fileName = Path.GetFileName(item.FilePath);
				if (newFilesByName.TryGetValue(fileName, out string? foundPath))
				{
					item.FilePath = foundPath;
					item.FileExistsCached = true;
					LibraryItem? dbItem = ctx.LibraryItems.Find(item.Id);
					if (dbItem != null)
					{
						dbItem.FilePath = foundPath;
						dbItem.FileExistsCached = true;
					}
					relinked++;
				}
			}
			if (relinked > 0) ctx.SaveChanges();
		}

		NotifySummaryChanged();
		ImportStatus = relinked > 0
			? $"Relocated and repaired {relinked} missing track(s)."
			: "No matching filenames found for missing tracks in that folder.";
	}

	private void FilterToDuplicates()
	{
		var dupKeys = Items
			.GroupBy(i => $"{i.Artist?.Trim().ToLowerInvariant()}|{i.Title?.Trim().ToLowerInvariant()}")
			.Where(g => g.Count() > 1)
			.Select(g => g.Key)
			.ToHashSet();

		if (dupKeys.Count == 0)
		{
			ImportStatus = "No duplicate Artist + Title entries detected.";
			return;
		}

		SelectedPlayset = null;
		ItemsView.Filter = entry => entry is LibraryItem item &&
			dupKeys.Contains($"{item.Artist?.Trim().ToLowerInvariant()}|{item.Title?.Trim().ToLowerInvariant()}");
		ItemsView.Refresh();
		NotifySummaryChanged();
		ImportStatus = $"Showing {VisibleItemCount} duplicate entries across {dupKeys.Count} title group(s).";
	}

	private void ApplyFilter()
	{
		string search = FilterText.Trim();
		string category = SelectedCategory;
		string status = SelectedStatusFilter;
		HashSet<int>? playsetIds = SelectedPlayset != null
			? ActivePlaysetTracks.Select(t => t.Id).ToHashSet()
			: null;

		ItemsView.Filter = entry =>
		{
			if (entry is not LibraryItem item) return false;
			if (playsetIds != null && !playsetIds.Contains(item.Id)) return false;

			if (status == "Available" && !item.FileExistsCached) return false;
			if (status == "Missing" && item.FileExistsCached) return false;
			if (status == "Unanalyzed" && item.IsAnalyzed) return false;

			string itemCat = string.IsNullOrWhiteSpace(item.Category) ? "Uncategorized" : item.Category;
			if (!string.Equals(category, "All categories", StringComparison.OrdinalIgnoreCase) &&
				!string.Equals(itemCat, category, StringComparison.OrdinalIgnoreCase))
			{
				return false;
			}

			if (string.IsNullOrWhiteSpace(search)) return true;
			return Contains(item.Title, search) || Contains(item.Artist, search) ||
				   Contains(item.Album, search) || Contains(item.Category, search) ||
				   Contains(item.Tags, search) || Contains(item.FilePath, search);
		};

		ItemsView.Refresh();
		NotifySummaryChanged();
	}

	private void ClearFilter()
	{
		_filterText = string.Empty;
		OnPropertyChanged(nameof(FilterText));
		_selectedCategory = "All categories";
		OnPropertyChanged(nameof(SelectedCategory));
		_selectedStatusFilter = "All";
		OnPropertyChanged(nameof(SelectedStatusFilter));
		ApplyFilter();
	}

	private void ImportTrack()
	{
		var dialog = new OpenFileDialog
		{
			Filter = AudioFileProbeCore.OpenFileDialogFilter,
			Multiselect = true
		};
		if (dialog.ShowDialog() != true || dialog.FileNames.Length == 0) return;

		ImportOptions? options = PromptImportOptions(InferCategory(dialog.FileNames[0]), allowInference: dialog.FileNames.Length > 1);
		if (options == null) return;

		var existing = Items.Select(i => i.FilePath).ToHashSet(StringComparer.OrdinalIgnoreCase);
		List<LibraryItem> imported = ImportFileList(dialog.FileNames, options, existing, null);
		CommitImportedItems(imported, options.AnalyzeAfterImport);

		string? dir = Path.GetDirectoryName(dialog.FileNames[0]);
		if (!string.IsNullOrWhiteSpace(dir)) AddWatchedFolder(dir);
		ImportStatus = $"Imported {imported.Count} track(s).";
	}

	private async Task ImportFolderAsync()
	{
		var dialog = new OpenFolderDialog { Title = "Import audio folder", Multiselect = false };
		if (dialog.ShowDialog() != true) return;

		ImportOptions? options = PromptImportOptions(InferCategory(dialog.FolderName), allowInference: true);
		if (options == null) return;

		IsImporting = true;
		ImportStatus = "Scanning folder...";
		try
		{
			var existing = Items.Select(i => i.FilePath).ToHashSet(StringComparer.OrdinalIgnoreCase);
			var progress = new Progress<string>(s => ImportStatus = s);
			List<string> files = await Task.Run(() => SafeEnumerateAudioFiles(dialog.FolderName).ToList());
			List<LibraryItem> imported = await Task.Run(() => ImportFileList(files, options, existing, progress));

			CommitImportedItems(imported, options.AnalyzeAfterImport);
			AddWatchedFolder(dialog.FolderName);
			ImportStatus = $"Imported {imported.Count} track(s) from {Path.GetFileName(dialog.FolderName)}.";
		}
		catch (Exception ex)
		{
			ImportStatus = "Import failed: " + ex.Message;
		}
		finally
		{
			IsImporting = false;
		}
	}

	private void CommitImportedItems(List<LibraryItem> imported, bool analyze)
	{
		if (imported.Count == 0) return;
		using (RadioLibraryContext ctx = _dbFactory.CreateContext())
		{
			ctx.LibraryItems.AddRange(imported);
			ctx.SaveChanges();
		}
		foreach (LibraryItem item in imported) Items.Add(item);
		_rotationService.ResetDecks();
		RefreshCategoryOptions();
		RefreshPlaysets();
		ApplyFilter();
		LibraryChanged?.Invoke();

		if (analyze)
		{
			_analysisService.ConfigureSilenceThreshold(_settings.GaplessSilenceThresholdDb);
			_analysisService.EnqueueUnanalyzed(imported);
		}
	}

	private static List<LibraryItem> ImportFileList(
		IReadOnlyList<string> files,
		ImportOptions options,
		HashSet<string> existingPaths,
		IProgress<string>? progress)
	{
		var list = new List<LibraryItem>();
		for (int i = 0; i < files.Count; i++)
		{
			string norm = NormalizePath(files[i]);
			progress?.Report($"Importing {i + 1}/{files.Count}: {Path.GetFileName(norm)}");
			if (!existingPaths.Add(norm)) continue;
			AudioProbeResult probe = AudioFileProbeCore.Probe(norm);
			if (probe.IsPlayable)
			{
				list.Add(CreateLibraryItem(norm, probe.Duration, options));
			}
		}
		return list;
	}

	private (int Added, int Updated, int Removed, int Invalid) SyncWatchedFolders(IReadOnlyList<string> watchedFolders)
	{
		using RadioLibraryContext ctx = _dbFactory.CreateContext();
		var tracked = ctx.LibraryItems.AsEnumerable()
			.Where(i => watchedFolders.Any(f => IsPathUnderFolder(i.FilePath, f)))
			.ToDictionary(i => NormalizePath(i.FilePath), StringComparer.OrdinalIgnoreCase);

		var onDisk = watchedFolders
			.SelectMany(SafeEnumerateAudioFiles)
			.Distinct(StringComparer.OrdinalIgnoreCase)
			.ToDictionary(p => p, GetSourceModifiedAtUtc, StringComparer.OrdinalIgnoreCase);

		int added = 0, updated = 0, removed = 0, invalid = 0;
		foreach (LibraryItem item in tracked.Values)
		{
			string norm = NormalizePath(item.FilePath);
			if (!onDisk.Remove(norm, out DateTime? modUtc))
			{
				ctx.LibraryItems.Remove(item);
				removed++;
			}
			else if (item.SourceModifiedAtUtc != modUtc)
			{
				AudioProbeResult probe = AudioFileProbeCore.Probe(norm);
				if (!probe.IsPlayable) { invalid++; continue; }
				UpdateItemMetadataFromFile(item, norm, probe.Duration, modUtc);
				item.AnalysisVersion = 0; // Re-trigger AutoMix analysis after file modification
				updated++;
			}
		}

		foreach (var kvp in onDisk)
		{
			AudioProbeResult probe = AudioFileProbeCore.Probe(kvp.Key);
			if (!probe.IsPlayable) { invalid++; continue; }
			ctx.LibraryItems.Add(CreateLibraryItem(kvp.Key, probe.Duration, null));
			added++;
		}

		if (added > 0 || updated > 0 || removed > 0) ctx.SaveChanges();
		return (added, updated, removed, invalid);
	}

	private void OnBackgroundTrackAnalyzed(LibraryItem analyzedRef, TrackAnalysisResult result)
	{
		Application.Current?.Dispatcher.BeginInvoke(() =>
		{
			LibraryItem? uiItem = Items.FirstOrDefault(i => i.Id == analyzedRef.Id);
			if (uiItem != null)
			{
				result.ApplyTo(uiItem);
				TrackAnalysisUpdated?.Invoke(uiItem);
			}
			OnPropertyChanged(nameof(UnanalyzedItemCount));
		});
	}

	private void OnBackgroundAnalysisProgress(AnalysisProgressSnapshot snap)
	{
		Application.Current?.Dispatcher.BeginInvoke(() =>
		{
			IsAnalyzing = snap.IsRunning;
			AnalysisStatus = snap.IsRunning
				? $"Analyzing {snap.Completed}/{snap.Total}: {snap.CurrentTrackTitle}"
				: $"AutoMix analysis complete ({Items.Count(i => i.IsAnalyzed):N0}/{Items.Count:N0} tracks ready)";
		});
	}

	private void AddWatchedFolderFromPicker()
	{
		var dialog = new OpenFolderDialog { Title = "Add watched library folder", Multiselect = false };
		if (dialog.ShowDialog() == true)
		{
			AddWatchedFolder(dialog.FolderName);
			_ = RunWatchedFolderRefreshAsync(reportStatus: true);
		}
	}

	private void AddWatchedFolder(string folderPath)
	{
		if (string.IsNullOrWhiteSpace(folderPath) || !Directory.Exists(folderPath)) return;
		string norm = NormalizePath(folderPath);
		if (WatchedFolders.Any(f => string.Equals(f, norm, StringComparison.OrdinalIgnoreCase))) return;
		WatchedFolders.Add(norm);
		_settings.WatchedLibraryFolders = WatchedFolders.OrderBy(f => f).ToList();
		_settingsService.Save(_settings);
		RestartFolderWatchers();
	}

	private void RemoveSelectedWatchedFolder()
	{
		if (string.IsNullOrWhiteSpace(SelectedWatchedFolder)) return;
		string removed = SelectedWatchedFolder;
		WatchedFolders.Remove(removed);
		SelectedWatchedFolder = WatchedFolders.FirstOrDefault();
		_settings.WatchedLibraryFolders = WatchedFolders.ToList();
		_settingsService.Save(_settings);
		RestartFolderWatchers();
		ImportStatus = $"Stopped watching '{removed}'.";
	}

	private void RestartFolderWatchers()
	{
		foreach (FileSystemWatcher w in _watchers) { w.EnableRaisingEvents = false; w.Dispose(); }
		_watchers.Clear();
		if (!BackgroundRefreshEnabled) return;

		foreach (string folder in WatchedFolders.Where(Directory.Exists))
		{
			try
			{
				var watcher = new FileSystemWatcher(folder)
				{
					IncludeSubdirectories = true,
					NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.Size | NotifyFilters.LastWrite
				};
				watcher.Created += (_, e) => { if (IsAudioFilePath(e.FullPath)) ScheduleBackgroundRefresh(); };
				watcher.Changed += (_, e) => { if (IsAudioFilePath(e.FullPath)) ScheduleBackgroundRefresh(); };
				watcher.Deleted += (_, e) => { if (IsAudioFilePath(e.FullPath)) ScheduleBackgroundRefresh(); };
				watcher.Renamed += (_, e) => { if (IsAudioFilePath(e.FullPath) || IsAudioFilePath(e.OldFullPath)) ScheduleBackgroundRefresh(); };
				watcher.EnableRaisingEvents = true;
				_watchers.Add(watcher);
			}
			catch (Exception ex)
			{
				LogService.Warn("Watcher", $"Could not watch '{folder}': {ex.Message}");
			}
		}
	}

	private void ScheduleBackgroundRefresh()
	{
		Application.Current?.Dispatcher.BeginInvoke(() =>
		{
			if (BackgroundRefreshEnabled && !IsImporting)
			{
				_pendingBackgroundRefresh = true;
				_refreshDebounceTimer.Stop();
				_refreshDebounceTimer.Start();
			}
		});
	}

	private void RefreshCategoryOptions()
	{
		string selected = SelectedCategory;
		List<string> cats = Items
			.Select(i => string.IsNullOrWhiteSpace(i.Category) ? "Uncategorized" : i.Category.Trim())
			.Distinct(StringComparer.OrdinalIgnoreCase)
			.OrderBy(c => c)
			.ToList();

		CategoryOptions.Clear();
		CategoryOptions.Add("All categories");
		foreach (string c in cats) CategoryOptions.Add(c);
		if (!CategoryOptions.Any(o => string.Equals(o, selected, StringComparison.OrdinalIgnoreCase)))
		{
			_selectedCategory = "All categories";
			OnPropertyChanged(nameof(SelectedCategory));
		}
	}

	private void NotifySummaryChanged()
	{
		OnPropertyChanged(nameof(TotalItemCount));
		OnPropertyChanged(nameof(VisibleItemCount));
		OnPropertyChanged(nameof(MissingItemCount));
		OnPropertyChanged(nameof(UnanalyzedItemCount));
		OnPropertyChanged(nameof(LibrarySummary));
		OnPropertyChanged(nameof(EmptyStateVisibility));
		OnPropertyChanged(nameof(EmptyStateMessage));
	}

	private static ImportOptions? PromptImportOptions(string defaultCategory, bool allowInference)
	{
		var options = new ImportOptions
		{
			Category = string.IsNullOrWhiteSpace(defaultCategory) ? "Music" : defaultCategory,
			UseInferredCategory = allowInference,
			AnalyzeAfterImport = true
		};
		var win = new ImportOptionsWindow(options) { Owner = Application.Current?.MainWindow };
		return win.ShowDialog() == true ? options : null;
	}

	private LibraryItem? ScanFolderForShortCue(string folderPath, string category, TimeSpan minDur, TimeSpan maxDur, Queue<string> recent)
	{
		if (string.IsNullOrWhiteSpace(folderPath) || !Directory.Exists(folderPath))
		{
			return null;
		}
		var list = SafeEnumerateAudioFiles(folderPath).OrderBy(_ => Random.Shared.Next()).Take(40).ToList();
		var playable = new List<LibraryItem>();
		foreach (string file in list)
		{
			AudioProbeResult probe = AudioFileProbeCore.Probe(file);
			if (probe.IsPlayable && probe.Duration >= minDur && probe.Duration <= maxDur)
			{
				LibraryItem item = CreateLibraryItem(file, probe.Duration, null);
				item.Category = category;
				playable.Add(item);
			}
		}
		return playable.Count > 0 ? PickWithRecentHistory(playable, recent) : null;
	}

	private static LibraryItem PickWithRecentHistory(IReadOnlyList<LibraryItem> candidates, Queue<string> recent)
	{
		var avail = candidates.Where(c => !recent.Any(r => string.Equals(r, c.FilePath, StringComparison.OrdinalIgnoreCase))).ToList();
		if (avail.Count == 0) avail = candidates.ToList();
		LibraryItem picked = avail[Random.Shared.Next(avail.Count)];
		recent.Enqueue(picked.FilePath);
		while (recent.Count > 6) recent.Dequeue();
		return picked;
	}

	private static LibraryItem CreateLibraryItem(string filePath, TimeSpan duration, ImportOptions? options)
	{
		var item = new LibraryItem
		{
			FilePath = filePath,
			Category = (options == null || options.UseInferredCategory)
				? InferCategory(filePath)
				: (string.IsNullOrWhiteSpace(options.Category) ? "Uncategorized" : options.Category.Trim()),
			Tags = options?.Tags ?? string.Empty,
			DateAdded = DateTime.UtcNow,
			FileExistsCached = true
		};
		UpdateItemMetadataFromFile(item, filePath, duration, GetSourceModifiedAtUtc(filePath));
		return item;
	}

	private static void UpdateItemMetadataFromFile(LibraryItem item, string filePath, TimeSpan verifiedDuration, DateTime? modUtc)
	{
		item.FilePath = filePath;
		item.Duration = verifiedDuration;
		item.SourceModifiedAtUtc = modUtc;
		item.FileExistsCached = true;
		try
		{
			using TagLib.File tagFile = TagLib.File.Create(filePath);
			item.Title = string.IsNullOrWhiteSpace(tagFile.Tag.Title) ? Path.GetFileNameWithoutExtension(filePath) : tagFile.Tag.Title;
			item.Artist = tagFile.Tag.FirstPerformer ?? string.Join(", ", tagFile.Tag.Performers ?? Enumerable.Empty<string>());
			item.Album = tagFile.Tag.Album ?? string.Empty;
			if (item.Duration <= TimeSpan.Zero) item.Duration = tagFile.Properties.Duration;
		}
		catch
		{
			item.Title = Path.GetFileNameWithoutExtension(filePath);
		}
	}

	private static IEnumerable<string> SafeEnumerateAudioFiles(string folderPath)
	{
		try
		{
			return Directory.EnumerateFiles(folderPath, "*.*", SearchOption.AllDirectories)
				.Where(IsAudioFilePath)
				.Select(NormalizePath)
				.ToList();
		}
		catch
		{
			return Array.Empty<string>();
		}
	}

	private static bool IsAudioFilePath(string path) =>
		!string.IsNullOrWhiteSpace(path) &&
		AudioFileProbeCore.SupportedExtensions.Contains(Path.GetExtension(path).ToLowerInvariant());

	private static bool IsPathUnderFolder(string filePath, string folderPath)
	{
		try
		{
			string f = NormalizePath(filePath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
			string d = NormalizePath(folderPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
			return f.StartsWith(d, StringComparison.OrdinalIgnoreCase);
		}
		catch { return false; }
	}

	private static string InferCategory(string path)
	{
		string p = path.ToLowerInvariant();
		if (p.Contains("commercial") || p.Contains("\\ads\\") || p.Contains("\\ad\\")) return "Commercial";
		if (p.Contains("station id") || p.Contains("stationid") || p.Contains("\\ids\\")) return "StationId";
		if (p.Contains("sweeper") || p.Contains("jingle")) return "Sweeper";
		if (p.Contains("promo")) return "Promo";
		if (p.Contains("voice")) return "VoiceTrack";
		return "Music";
	}

	private static bool Contains(string? source, string search) =>
		!string.IsNullOrWhiteSpace(source) && source.Contains(search, StringComparison.OrdinalIgnoreCase);

	private static string NormalizePath(string path)
	{
		try { return Path.GetFullPath(path); } catch { return path; }
	}

	private static DateTime? GetSourceModifiedAtUtc(string filePath)
	{
		try { return File.GetLastWriteTimeUtc(filePath); } catch { return null; }
	}

	private static string SanitizeFileName(string name)
	{
		foreach (char c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
		return string.IsNullOrWhiteSpace(name) ? "playset" : name.Trim();
	}

	public void Dispose()
	{
		_refreshDebounceTimer.Stop();
		_searchDebounceTimer.Stop();
		_analysisService.TrackAnalyzed -= OnBackgroundTrackAnalyzed;
		_analysisService.ProgressChanged -= OnBackgroundAnalysisProgress;
		foreach (FileSystemWatcher w in _watchers) { w.EnableRaisingEvents = false; w.Dispose(); }
		_watchers.Clear();
	}
}
