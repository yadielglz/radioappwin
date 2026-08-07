using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Threading;
using Microsoft.Win32;
using RadioApp.Data;
using RadioApp.Helpers;
using RadioApp.Models;
using RadioApp.Services;
using RadioApp.Views;
using TagLib;

namespace RadioApp.ViewModels;

public class LibraryViewModel : ViewModelBase, IDisposable
{
	private sealed record FolderImportResult(IList<LibraryItem> ImportedItems, int Skipped, int Invalid);

	private sealed record LibrarySyncResult(int Added, int Updated, int Removed, int Invalid)
	{
		public bool HasChanges
		{
			get
			{
				if (Added <= 0 && Updated <= 0)
				{
					return Removed > 0;
				}
				return true;
			}
		}

		public string ToStatusText()
		{
			if (!HasChanges && Invalid == 0)
			{
				return "Library is already in sync.";
			}
			return $"Library sync: +{Added} / ~{Updated} / -{Removed}" + ((Invalid > 0) ? $" / {Invalid} skipped" : string.Empty);
		}
	}

	private readonly RadioLibraryContext _context;

	private readonly AppSettings _settings;

	private readonly AppSettingsService _settingsService;

	private readonly Random _random = new Random();

	private readonly Queue<string> _recentSweepers = new Queue<string>();

	private readonly Queue<string> _recentRadioIds = new Queue<string>();

	private readonly List<FileSystemWatcher> _watchers = new List<FileSystemWatcher>();

	private readonly DispatcherTimer _refreshDebounceTimer;

	private string _filterText = string.Empty;

	private string _selectedCategory = "All categories";

	private string _importStatus = "Ready";

	private LibraryItem? _selectedItem;

	private string? _selectedWatchedFolder;

	private bool _isImporting;

	private bool _backgroundRefreshEnabled;

	private bool _isBackgroundSyncRunning;

	private bool _pendingBackgroundRefresh;

	private static readonly string[] SupportedAudioExtensions = AudioFileProbe.SupportedExtensions;

	public ObservableCollection<LibraryItem> Items { get; }

	public ICollectionView ItemsView { get; }

	public ObservableCollection<string> CategoryOptions { get; } = new ObservableCollection<string>();

	public ObservableCollection<string> WatchedFolders { get; }

	public ICommand RefreshCommand { get; }

	public ICommand ImportTrackCommand { get; }

	public ICommand ImportFolderCommand { get; }

	public ICommand DeleteSelectedCommand { get; }

	public ICommand FilterCommand { get; }

	public ICommand ClearFilterCommand { get; }

	public ICommand SaveChangesCommand { get; }

	public ICommand AddWatchedFolderCommand { get; }

	public ICommand RemoveWatchedFolderCommand { get; }

	public ICommand RescanWatchedFoldersCommand { get; }

	public string FilterText
	{
		get
		{
			return _filterText;
		}
		set
		{
			if (SetProperty(ref _filterText, value, "FilterText"))
			{
				ApplyFilter();
			}
		}
	}

	public string SelectedCategory
	{
		get
		{
			return _selectedCategory;
		}
		set
		{
			if (SetProperty(ref _selectedCategory, value ?? "All categories", "SelectedCategory"))
			{
				ApplyFilter();
			}
		}
	}

	public int TotalItemCount => Items.Count;

	public int VisibleItemCount => ItemsView.Cast<object>().Count();

	public string LibrarySummary => $"{VisibleItemCount:N0} visible / {TotalItemCount:N0} total";

	public Visibility EmptyStateVisibility => VisibleItemCount == 0 ? Visibility.Visible : Visibility.Collapsed;

	public string EmptyStateMessage => TotalItemCount == 0 ? "The library is empty. Import a track or folder to begin." : "No tracks match the current search and category.";

	public string ImportStatus
	{
		get
		{
			return _importStatus;
		}
		set
		{
			SetProperty(ref _importStatus, value, "ImportStatus");
		}
	}

	public bool IsImporting
	{
		get
		{
			return _isImporting;
		}
		private set
		{
			if (SetProperty(ref _isImporting, value, "IsImporting"))
			{
				CommandManager.InvalidateRequerySuggested();
			}
		}
	}

	public bool BackgroundRefreshEnabled
	{
		get
		{
			return _backgroundRefreshEnabled;
		}
		set
		{
			if (SetProperty(ref _backgroundRefreshEnabled, value, "BackgroundRefreshEnabled"))
			{
				_settings.EnableLibraryBackgroundRefresh = value;
				_settingsService.Save(_settings);
				_refreshDebounceTimer.Stop();
				RestartFolderWatchers();
				ImportStatus = (value ? "Background library refresh enabled." : "Background library refresh paused.");
			}
		}
	}

	public LibraryItem? SelectedItem
	{
		get
		{
			return _selectedItem;
		}
		set
		{
			if (SetProperty(ref _selectedItem, value, "SelectedItem"))
			{
				CommandManager.InvalidateRequerySuggested();
			}
		}
	}

	public string? SelectedWatchedFolder
	{
		get
		{
			return _selectedWatchedFolder;
		}
		set
		{
			if (SetProperty(ref _selectedWatchedFolder, value, "SelectedWatchedFolder"))
			{
				CommandManager.InvalidateRequerySuggested();
			}
		}
	}

	public event Action? LibraryChanged;

	public LibraryViewModel(AppSettings settings, AppSettingsService settingsService)
	{
		_settings = settings;
		_settingsService = settingsService;
		_context = new RadioLibraryContext();
		_context.EnsureDatabase();
		_backgroundRefreshEnabled = settings.EnableLibraryBackgroundRefresh;
		Items = new ObservableCollection<LibraryItem>(_context.LibraryItems.OrderBy((LibraryItem item) => item.Title).ToList());
		ItemsView = CollectionViewSource.GetDefaultView(Items);
		ItemsView.SortDescriptions.Add(new SortDescription("Title", ListSortDirection.Ascending));
		Items.CollectionChanged += OnItemsCollectionChanged;
		RefreshCategoryOptions();
		WatchedFolders = new ObservableCollection<string>(from path in settings.WatchedLibraryFolders.Where((string path) => !string.IsNullOrWhiteSpace(path)).Select(NormalizePath).Distinct<string>(StringComparer.OrdinalIgnoreCase)
			orderby path
			select path);
		RefreshCommand = new RelayCommand(delegate
		{
			Refresh();
		});
		ImportTrackCommand = new RelayCommand(delegate
		{
			ImportTrack();
		});
		ImportFolderCommand = new RelayCommand(async delegate
		{
			await ImportFolderAsync();
		}, (object? _) => !IsImporting);
		DeleteSelectedCommand = new RelayCommand(delegate
		{
			DeleteSelected();
		}, (object? _) => SelectedItem != null);
		FilterCommand = new RelayCommand(delegate
		{
			ApplyFilter();
		});
		ClearFilterCommand = new RelayCommand(delegate
		{
			ClearFilter();
		});
		SaveChangesCommand = new RelayCommand(delegate
		{
			SaveChanges();
		});
		AddWatchedFolderCommand = new RelayCommand(delegate
		{
			AddWatchedFolderFromPicker();
		});
		RemoveWatchedFolderCommand = new RelayCommand(delegate
		{
			RemoveSelectedWatchedFolder();
		}, (object? _) => !string.IsNullOrWhiteSpace(SelectedWatchedFolder));
		RescanWatchedFoldersCommand = new RelayCommand(async delegate
		{
			await RunWatchedFolderRefreshAsync(reportStatus: true);
		});
		_refreshDebounceTimer = new DispatcherTimer
		{
			Interval = TimeSpan.FromSeconds(2.0)
		};
		_refreshDebounceTimer.Tick += async delegate
		{
			_refreshDebounceTimer.Stop();
			await RunWatchedFolderRefreshAsync(reportStatus: false);
		};
		RestartFolderWatchers();
	}

	public void Refresh()
	{
		_context.ChangeTracker.Clear();
		RefreshView();
	}

	public void ReloadMonitoringSettings()
	{
		_backgroundRefreshEnabled = _settings.EnableLibraryBackgroundRefresh;
		OnPropertyChanged("BackgroundRefreshEnabled");
		WatchedFolders.Clear();
		foreach (string folder in from path in _settings.WatchedLibraryFolders.Where((string path) => !string.IsNullOrWhiteSpace(path)).Select(NormalizePath).Distinct<string>(StringComparer.OrdinalIgnoreCase)
			orderby path
			select path)
		{
			WatchedFolders.Add(folder);
		}
		SelectedWatchedFolder = WatchedFolders.FirstOrDefault();
		RestartFolderWatchers();
	}

	public void SaveChanges()
	{
		_context.SaveChanges();
		RefreshCategoryOptions();
		ApplyFilter();
	}

	public void AddLibraryItem(LibraryItem item)
	{
		_context.LibraryItems.Add(item);
		_context.SaveChanges();
		Items.Add(item);
		this.LibraryChanged?.Invoke();
	}

	public void ClearAll()
	{
		_context.LibraryItems.RemoveRange(_context.LibraryItems);
		_context.SaveChanges();
		Items.Clear();
		SelectedItem = null;
		ImportStatus = "Library cleared.";
		this.LibraryChanged?.Invoke();
	}

	public void DeleteItems(IEnumerable<LibraryItem> items)
	{
		List<LibraryItem> selectedItems = items.Distinct().ToList();
		if (selectedItems.Count == 0)
		{
			return;
		}
		string message = ((selectedItems.Count == 1) ? ("Remove '" + selectedItems[0].Title + "' from the library?") : $"Remove {selectedItems.Count} selected item(s) from the library?");
		if (MessageBox.Show(Application.Current?.MainWindow, message, "Delete Library Item", MessageBoxButton.YesNo, MessageBoxImage.Exclamation) != MessageBoxResult.Yes)
		{
			return;
		}
		_context.LibraryItems.RemoveRange(selectedItems);
		_context.SaveChanges();
		foreach (LibraryItem item in selectedItems)
		{
			Items.Remove(item);
		}
		SelectedItem = null;
		ImportStatus = ((selectedItems.Count == 1) ? "Removed selected library item." : $"Removed {selectedItems.Count} library items.");
		this.LibraryChanged?.Invoke();
	}

	public LibraryItem? GetRandomItem(params string[] preferredCategories)
	{
		IQueryable<LibraryItem> query = _context.LibraryItems.AsQueryable();
		if (preferredCategories.Length != 0)
		{
			string[] normalized = (from category in preferredCategories
				where !string.IsNullOrWhiteSpace(category)
				select category.ToLowerInvariant()).ToArray();
			query = query.Where((LibraryItem item) => Enumerable.Contains(normalized, item.Category.ToLower()));
		}
		List<LibraryItem> candidates = query.ToList();
		if (candidates.Count != 0)
		{
			return candidates[_random.Next(candidates.Count)];
		}
		return null;
	}

	public LibraryItem? GetRandomMusicByTag(string tag)
	{
		if (string.IsNullOrWhiteSpace(tag))
		{
			return GetRandomItem("Music", "Song", "Uncategorized");
		}
		string normalizedTag = tag.Trim().ToLowerInvariant();
		List<LibraryItem> candidates = (from item in _context.LibraryItems.AsEnumerable()
			where IsMusicCandidate(item) && item.Tags.Split(new char[3] { ',', ';', '|' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Any((string itemTag) => string.Equals(itemTag, normalizedTag, StringComparison.OrdinalIgnoreCase))
			select item).ToList();
		if (candidates.Count != 0)
		{
			return candidates[_random.Next(candidates.Count)];
		}
		return null;
	}

	public LibraryItem? GetRandomSweeper(string folderPath, TimeSpan minimumDuration, TimeSpan maximumDuration)
	{
		List<LibraryItem> candidates = (from item2 in _context.LibraryItems.AsEnumerable()
			where IsSweeperCandidate(item2, folderPath, minimumDuration, maximumDuration)
			select item2).ToList();
		if (candidates.Count > 0)
		{
			return PickWithRecentHistory(candidates, _recentSweepers, (LibraryItem libraryItem) => libraryItem.FilePath);
		}
		if (!Directory.Exists(folderPath))
		{
			return GetRandomItem("Sweeper", "Sweep");
		}
		List<string> list = (from _ in Directory.EnumerateFiles(folderPath, "*.*", SearchOption.AllDirectories)
			where Enumerable.Contains(SupportedAudioExtensions, Path.GetExtension(_).ToLowerInvariant())
			orderby _random.Next()
			select _).ToList();
		List<LibraryItem> playableItems = new List<LibraryItem>();
		foreach (string file in list)
		{
			AudioProbeResult probe = AudioFileProbe.Probe(file);
			if (probe.IsPlayable && !(probe.Duration < minimumDuration) && !(probe.Duration > TimeSpan.FromSeconds(30.0)))
			{
				LibraryItem item = CreateLibraryItem(Path.GetFullPath(file), probe.Duration);
				item.Category = "Sweeper";
				playableItems.Add(item);
			}
		}
		List<LibraryItem> preferredItems = playableItems.Where((LibraryItem libraryItem) => libraryItem.Duration <= maximumDuration).ToList();
		List<LibraryItem> selectableItems = ((preferredItems.Count > 0) ? preferredItems : playableItems);
		if (selectableItems.Count != 0)
		{
			return PickWithRecentHistory(selectableItems, _recentSweepers, (LibraryItem libraryItem) => libraryItem.FilePath);
		}
		return null;
	}

	public LibraryItem? GetRandomRadioId(string folderPath, TimeSpan minimumDuration, TimeSpan maximumDuration)
	{
		List<LibraryItem> candidates = (from item2 in _context.LibraryItems.AsEnumerable()
			where IsRadioIdCandidate(item2, folderPath, minimumDuration, maximumDuration)
			select item2).ToList();
		if (candidates.Count > 0)
		{
			return PickWithRecentHistory(candidates, _recentRadioIds, (LibraryItem libraryItem) => libraryItem.FilePath);
		}
		if (!Directory.Exists(folderPath))
		{
			return GetRandomItem("StationId", "Station ID", "ID");
		}
		List<string> list = (from _ in Directory.EnumerateFiles(folderPath, "*.*", SearchOption.AllDirectories)
			where Enumerable.Contains(SupportedAudioExtensions, Path.GetExtension(_).ToLowerInvariant())
			orderby _random.Next()
			select _).ToList();
		List<LibraryItem> playableItems = new List<LibraryItem>();
		foreach (string file in list)
		{
			AudioProbeResult probe = AudioFileProbe.Probe(file);
			if (probe.IsPlayable && !(probe.Duration < minimumDuration))
			{
				LibraryItem item = CreateLibraryItem(Path.GetFullPath(file), probe.Duration);
				item.Category = "StationId";
				playableItems.Add(item);
			}
		}
		List<LibraryItem> preferredItems = playableItems.Where((LibraryItem libraryItem) => libraryItem.Duration <= maximumDuration).ToList();
		List<LibraryItem> selectableItems = ((preferredItems.Count > 0) ? preferredItems : playableItems);
		if (selectableItems.Count != 0)
		{
			return PickWithRecentHistory(selectableItems, _recentRadioIds, (LibraryItem libraryItem) => libraryItem.FilePath);
		}
		return null;
	}

	public async Task RunWatchedFolderRefreshAsync(bool reportStatus)
	{
		if (_isBackgroundSyncRunning)
		{
			_pendingBackgroundRefresh = true;
			return;
		}
		List<string> watchedFolders = WatchedFolders.Where(Directory.Exists).Distinct<string>(StringComparer.OrdinalIgnoreCase).ToList();
		if (watchedFolders.Count == 0)
		{
			if (reportStatus)
			{
				ImportStatus = "No watched folders configured.";
			}
			return;
		}
		_isBackgroundSyncRunning = true;
		_pendingBackgroundRefresh = false;
		if (reportStatus)
		{
			ImportStatus = "Refreshing watched folders...";
		}
		try
		{
			LibrarySyncResult result = await Task.Run(() => SyncWatchedFolders(watchedFolders));
			_context.ChangeTracker.Clear();
			RefreshView();
			if (result.HasChanges)
			{
				this.LibraryChanged?.Invoke();
			}
			ImportStatus = result.ToStatusText();
		}
		catch (Exception ex)
		{
			ImportStatus = "Background refresh failed: " + ex.Message;
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

	private static bool IsMusicCandidate(LibraryItem item)
	{
		if (!string.Equals(item.Category, "Music", StringComparison.OrdinalIgnoreCase) && !string.Equals(item.Category, "Song", StringComparison.OrdinalIgnoreCase))
		{
			return string.Equals(item.Category, "Uncategorized", StringComparison.OrdinalIgnoreCase);
		}
		return true;
	}

	private void ApplyFilter()
	{
		string search = FilterText.Trim();
		string category = SelectedCategory;
		ItemsView.Filter = delegate(object entry)
		{
			if (entry is not LibraryItem item)
			{
				return false;
			}
			string itemCategory = string.IsNullOrWhiteSpace(item.Category) ? "Uncategorized" : item.Category;
			bool categoryMatches = string.Equals(category, "All categories", StringComparison.OrdinalIgnoreCase) || string.Equals(itemCategory, category, StringComparison.OrdinalIgnoreCase);
			if (!categoryMatches)
			{
				return false;
			}
			return string.IsNullOrWhiteSpace(search) || Contains(item.Title, search) || Contains(item.Artist, search) || Contains(item.Album, search) || Contains(item.Category, search) || Contains(item.Tags, search) || Contains(item.FilePath, search);
		};
		ItemsView.Refresh();
		NotifyLibraryViewChanged();
	}

	private void ClearFilter()
	{
		FilterText = string.Empty;
		SelectedCategory = "All categories";
		ApplyFilter();
	}

	private void ImportTrack()
	{
		OpenFileDialog dialog = new OpenFileDialog
		{
			Filter = "Playable audio files|*.mp3;*.wav;*.aiff;*.aif|All files|*.*"
		};
		if (dialog.ShowDialog() != true)
		{
			return;
		}
		ImportOptions options = GetImportOptions(InferCategory(dialog.FileName), allowInference: false);
		if (options != null)
		{
			string importResult = AddImportedFile(dialog.FileName, options);
			SaveChanges();
			string folderPath = Path.GetDirectoryName(dialog.FileName);
			if (!string.IsNullOrWhiteSpace(folderPath))
			{
				AddWatchedFolder(folderPath);
			}
			ImportStatus = importResult;
			this.LibraryChanged?.Invoke();
		}
	}

	private async Task ImportFolderAsync()
	{
		OpenFolderDialog dialog = new OpenFolderDialog
		{
			Title = "Import audio folder",
			Multiselect = false
		};
		if (dialog.ShowDialog() != true)
		{
			return;
		}
		ImportOptions options = GetImportOptions(InferCategory(dialog.FolderName), allowInference: true);
		if (options == null)
		{
			return;
		}
		IsImporting = true;
		ImportStatus = "Scanning folder...";
		try
		{
			HashSet<string> existingPaths = _context.LibraryItems.Select((LibraryItem libraryItem) => libraryItem.FilePath).ToHashSet<string>(StringComparer.OrdinalIgnoreCase);
			Progress<string> progress = new Progress<string>(delegate(string status)
			{
				ImportStatus = status;
			});
			FolderImportResult result = await Task.Run(() => BuildFolderImport(dialog.FolderName, options, existingPaths, progress));
			foreach (LibraryItem item in result.ImportedItems)
			{
				_context.LibraryItems.Add(item);
				Items.Add(item);
			}
			_context.SaveChanges();
			AddWatchedFolder(dialog.FolderName);
			ImportStatus = $"Imported {result.ImportedItems.Count}, skipped {result.Skipped} duplicate(s), rejected {result.Invalid} invalid file(s).";
			this.LibraryChanged?.Invoke();
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

	private static FolderImportResult BuildFolderImport(string folderPath, ImportOptions options, HashSet<string> existingPaths, IProgress<string> progress)
	{
		List<string> files = (from path in Directory.EnumerateFiles(folderPath, "*.*", SearchOption.AllDirectories)
			where Enumerable.Contains(SupportedAudioExtensions, Path.GetExtension(path).ToLowerInvariant())
			select path).ToList();
		progress.Report((files.Count == 0) ? "No playable audio files found." : $"Found {files.Count} file(s). Starting import...");
		List<LibraryItem> importedItems = new List<LibraryItem>();
		int skipped = 0;
		int invalid = 0;
		int processed = 0;
		foreach (string file in files)
		{
			processed++;
			progress.Report($"Importing {processed}/{files.Count}: {Path.GetFileName(file)}");
			string normalizedPath = Path.GetFullPath(file);
			if (!existingPaths.Add(normalizedPath))
			{
				skipped++;
				continue;
			}
			AudioProbeResult probe = AudioFileProbe.Probe(normalizedPath);
			if (!probe.IsPlayable)
			{
				invalid++;
			}
			else
			{
				importedItems.Add(CreateLibraryItem(normalizedPath, probe.Duration, options));
			}
		}
		return new FolderImportResult(importedItems, skipped, invalid);
	}

	private LibrarySyncResult SyncWatchedFolders(IList<string> watchedFolders)
	{
		using RadioLibraryContext context = new RadioLibraryContext();
		context.EnsureDatabase();
		Dictionary<string, LibraryItem> dictionary = (from item in context.LibraryItems.AsEnumerable()
			where watchedFolders.Any((string folder) => IsPathUnderFolder(item.FilePath, folder))
			select item).ToDictionary<LibraryItem, string>((LibraryItem item) => NormalizePath(item.FilePath), StringComparer.OrdinalIgnoreCase);
		Dictionary<string, DateTime?> filesOnDisk = watchedFolders.SelectMany((string folder) => SafeEnumerateAudioFiles(folder)).Distinct<string>(StringComparer.OrdinalIgnoreCase).ToDictionary((Func<string, string>)((string path) => path), (Func<string, DateTime?>)GetSourceModifiedAtUtc, (IEqualityComparer<string>?)StringComparer.OrdinalIgnoreCase);
		int added = 0;
		int updated = 0;
		int removed = 0;
		int invalid = 0;
		foreach (LibraryItem trackedItem in dictionary.Values)
		{
			string normalizedPath = NormalizePath(trackedItem.FilePath);
			if (!filesOnDisk.Remove(normalizedPath, out var sourceModifiedAtUtc))
			{
				context.LibraryItems.Remove(trackedItem);
				removed++;
			}
			else if (!(trackedItem.SourceModifiedAtUtc == sourceModifiedAtUtc))
			{
				AudioProbeResult probe = AudioFileProbe.Probe(normalizedPath);
				if (!probe.IsPlayable)
				{
					invalid++;
					continue;
				}
				UpdateLibraryItemFromFile(trackedItem, normalizedPath, probe.Duration, sourceModifiedAtUtc);
				updated++;
			}
		}
		foreach (KeyValuePair<string, DateTime?> file in filesOnDisk)
		{
			AudioProbeResult probe2 = AudioFileProbe.Probe(file.Key);
			if (!probe2.IsPlayable)
			{
				invalid++;
				continue;
			}
			context.LibraryItems.Add(CreateLibraryItem(file.Key, probe2.Duration));
			added++;
		}
		if (added > 0 || updated > 0 || removed > 0)
		{
			context.SaveChanges();
		}
		return new LibrarySyncResult(added, updated, removed, invalid);
	}

	private IEnumerable<string> SafeEnumerateAudioFiles(string folderPath)
	{
		try
		{
			return (from path in Directory.EnumerateFiles(folderPath, "*.*", SearchOption.AllDirectories)
				where Enumerable.Contains(SupportedAudioExtensions, Path.GetExtension(path).ToLowerInvariant())
				select path).Select(NormalizePath).ToList();
		}
		catch
		{
			return Array.Empty<string>();
		}
	}

	private void UpdateLibraryItemFromFile(LibraryItem item, string filePath, TimeSpan verifiedDuration, DateTime? sourceModifiedAtUtc)
	{
		item.FilePath = filePath;
		item.Duration = verifiedDuration;
		item.SourceModifiedAtUtc = sourceModifiedAtUtc;
		try
		{
			using TagLib.File tagFile = TagLib.File.Create(filePath);
			item.Title = (string.IsNullOrWhiteSpace(tagFile.Tag.Title) ? Path.GetFileName(filePath) : tagFile.Tag.Title);
			string text = tagFile.Tag.FirstPerformer;
			if (text == null)
			{
				IEnumerable<string> performers = tagFile.Tag.Performers;
				text = string.Join(", ", performers ?? Enumerable.Empty<string>());
			}
			item.Artist = text;
			item.Album = tagFile.Tag.Album ?? string.Empty;
			if (item.Duration <= TimeSpan.Zero)
			{
				item.Duration = tagFile.Properties.Duration;
			}
		}
		catch
		{
			item.Title = Path.GetFileName(filePath);
			item.Artist = string.Empty;
			item.Album = string.Empty;
		}
	}

	private void DeleteSelected()
	{
		IEnumerable<LibraryItem> items;
		if (SelectedItem != null)
		{
			IEnumerable<LibraryItem> enumerable = new _003C_003Ez__ReadOnlySingleElementList<LibraryItem>(SelectedItem);
			items = enumerable;
		}
		else
		{
			items = Enumerable.Empty<LibraryItem>();
		}
		DeleteItems(items);
	}

	private void AddWatchedFolderFromPicker()
	{
		OpenFolderDialog dialog = new OpenFolderDialog
		{
			Title = "Add watched library folder",
			Multiselect = false
		};
		if (dialog.ShowDialog() == true)
		{
			AddWatchedFolder(dialog.FolderName);
			RunWatchedFolderRefreshAsync(reportStatus: true);
		}
	}

	private void AddWatchedFolder(string folderPath)
	{
		if (!string.IsNullOrWhiteSpace(folderPath) && Directory.Exists(folderPath))
		{
			string normalizedPath = NormalizePath(folderPath);
			if (!WatchedFolders.Any((string path) => string.Equals(path, normalizedPath, StringComparison.OrdinalIgnoreCase)))
			{
				WatchedFolders.Add(normalizedPath);
				SortWatchedFolders();
				SaveWatchedFolders();
				RestartFolderWatchers();
			}
		}
	}

	private void RemoveSelectedWatchedFolder()
	{
		if (!string.IsNullOrWhiteSpace(SelectedWatchedFolder))
		{
			string selected = SelectedWatchedFolder;
			WatchedFolders.Remove(selected);
			SelectedWatchedFolder = WatchedFolders.FirstOrDefault();
			SaveWatchedFolders();
			RestartFolderWatchers();
			ImportStatus = "Stopped watching " + selected + ".";
		}
	}

	private void SortWatchedFolders()
	{
		List<string> list = WatchedFolders.OrderBy((string result) => result).ToList();
		WatchedFolders.Clear();
		foreach (string path in list)
		{
			WatchedFolders.Add(path);
		}
		if (SelectedWatchedFolder == null)
		{
			string text = (SelectedWatchedFolder = WatchedFolders.FirstOrDefault());
		}
	}

	private void SaveWatchedFolders()
	{
		_settings.WatchedLibraryFolders = WatchedFolders.ToList();
		_settingsService.Save(_settings);
	}

	private void RestartFolderWatchers()
	{
		foreach (FileSystemWatcher watcher2 in _watchers)
		{
			watcher2.EnableRaisingEvents = false;
			watcher2.Dispose();
		}
		_watchers.Clear();
		if (!BackgroundRefreshEnabled)
		{
			return;
		}
		foreach (string item in WatchedFolders.Where(Directory.Exists))
		{
			FileSystemWatcher watcher = new FileSystemWatcher(item)
			{
				IncludeSubdirectories = true,
				NotifyFilter = (NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.Size | NotifyFilters.LastWrite | NotifyFilters.CreationTime)
			};
			watcher.Changed += OnWatchedFolderChanged;
			watcher.Created += OnWatchedFolderChanged;
			watcher.Deleted += OnWatchedFolderChanged;
			watcher.Renamed += OnWatchedFolderRenamed;
			watcher.EnableRaisingEvents = true;
			_watchers.Add(watcher);
		}
	}

	private void OnWatchedFolderChanged(object sender, FileSystemEventArgs e)
	{
		if (IsAudioFilePath(e.FullPath))
		{
			ScheduleBackgroundRefresh();
		}
	}

	private void OnWatchedFolderRenamed(object sender, RenamedEventArgs e)
	{
		if (IsAudioFilePath(e.FullPath) || IsAudioFilePath(e.OldFullPath))
		{
			ScheduleBackgroundRefresh();
		}
	}

	private void ScheduleBackgroundRefresh()
	{
		if (BackgroundRefreshEnabled && !IsImporting)
		{
			_pendingBackgroundRefresh = true;
			_refreshDebounceTimer.Stop();
			_refreshDebounceTimer.Start();
		}
	}

	private void RefreshView()
	{
		Items.Clear();
		foreach (LibraryItem item in _context.LibraryItems.OrderBy((LibraryItem i) => i.Title))
		{
			Items.Add(item);
		}
		RefreshCategoryOptions();
		ApplyFilter();
	}

	private void OnItemsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
	{
		RefreshCategoryOptions();
		NotifyLibraryViewChanged();
	}

	private void RefreshCategoryOptions()
	{
		string selected = SelectedCategory;
		List<string> categories = Items.Select((LibraryItem item) => string.IsNullOrWhiteSpace(item.Category) ? "Uncategorized" : item.Category.Trim()).Distinct<string>(StringComparer.OrdinalIgnoreCase).OrderBy((string category) => category).ToList();
		CategoryOptions.Clear();
		CategoryOptions.Add("All categories");
		foreach (string category in categories)
		{
			CategoryOptions.Add(category);
		}
		if (!CategoryOptions.Any((string option) => string.Equals(option, selected, StringComparison.OrdinalIgnoreCase)))
		{
			_selectedCategory = "All categories";
			OnPropertyChanged("SelectedCategory");
		}
	}

	private void NotifyLibraryViewChanged()
	{
		OnPropertyChanged("TotalItemCount");
		OnPropertyChanged("VisibleItemCount");
		OnPropertyChanged("LibrarySummary");
		OnPropertyChanged("EmptyStateVisibility");
		OnPropertyChanged("EmptyStateMessage");
	}

	private static bool Contains(string? value, string search)
	{
		return !string.IsNullOrWhiteSpace(value) && value.Contains(search, StringComparison.OrdinalIgnoreCase);
	}

	private static ImportOptions? GetImportOptions(string defaultCategory, bool allowInference)
	{
		ImportOptions options = new ImportOptions
		{
			Category = (string.IsNullOrWhiteSpace(defaultCategory) ? "Music" : defaultCategory),
			UseInferredCategory = allowInference
		};
		if (new ImportOptionsWindow(options)
		{
			Owner = Application.Current?.MainWindow
		}.ShowDialog() != true)
		{
			return null;
		}
		return options;
	}

	private string AddImportedFile(string filePath, ImportOptions options)
	{
		string normalizedPath = NormalizePath(filePath);
		if (_context.LibraryItems.Any((LibraryItem libraryItem) => libraryItem.FilePath == normalizedPath))
		{
			return "Skipped duplicate track.";
		}
		AudioProbeResult probe = AudioFileProbe.Probe(normalizedPath);
		if (!probe.IsPlayable)
		{
			return "Skipped invalid file: " + probe.ErrorMessage;
		}
		LibraryItem item = CreateLibraryItem(normalizedPath, probe.Duration, options);
		_context.LibraryItems.Add(item);
		Items.Add(item);
		return "Imported 1 track.";
	}

	private static LibraryItem CreateLibraryItem(string filePath, TimeSpan verifiedDuration, ImportOptions? options = null)
	{
		DateTime? sourceModifiedAtUtc = GetSourceModifiedAtUtc(filePath);
		try
		{
			using TagLib.File tagFile = TagLib.File.Create(filePath);
			LibraryItem obj = new LibraryItem
			{
				FilePath = filePath,
				Title = (string.IsNullOrWhiteSpace(tagFile.Tag.Title) ? Path.GetFileName(filePath) : tagFile.Tag.Title)
			};
			string text = tagFile.Tag.FirstPerformer;
			if (text == null)
			{
				IEnumerable<string> performers = tagFile.Tag.Performers;
				text = string.Join(", ", performers ?? Enumerable.Empty<string>());
			}
			obj.Artist = text;
			obj.Album = tagFile.Tag.Album ?? string.Empty;
			obj.Category = ResolveCategory(filePath, options);
			obj.Tags = options?.Tags ?? string.Empty;
			obj.Duration = ((verifiedDuration > TimeSpan.Zero) ? verifiedDuration : tagFile.Properties.Duration);
			obj.DateAdded = DateTime.UtcNow;
			obj.SourceModifiedAtUtc = sourceModifiedAtUtc;
			return obj;
		}
		catch
		{
			return new LibraryItem
			{
				FilePath = filePath,
				Title = Path.GetFileName(filePath),
				Category = ResolveCategory(filePath, options),
				Tags = (options?.Tags ?? string.Empty),
				Duration = verifiedDuration,
				DateAdded = DateTime.UtcNow,
				SourceModifiedAtUtc = sourceModifiedAtUtc
			};
		}
	}

	private static string ResolveCategory(string filePath, ImportOptions? options)
	{
		if (options == null || options.UseInferredCategory)
		{
			return InferCategory(filePath);
		}
		if (!string.IsNullOrWhiteSpace(options.Category))
		{
			return options.Category.Trim();
		}
		return "Uncategorized";
	}

	private static bool IsSweeperCandidate(LibraryItem item, string folderPath, TimeSpan minimumDuration, TimeSpan maximumDuration)
	{
		if (item.Duration < minimumDuration || item.Duration > maximumDuration)
		{
			return false;
		}
		if (!string.IsNullOrWhiteSpace(folderPath) && IsPathUnderFolder(item.FilePath, folderPath))
		{
			return true;
		}
		if (!string.Equals(item.Category, "Sweeper", StringComparison.OrdinalIgnoreCase))
		{
			return string.Equals(item.Category, "Sweep", StringComparison.OrdinalIgnoreCase);
		}
		return true;
	}

	private static bool IsRadioIdCandidate(LibraryItem item, string folderPath, TimeSpan minimumDuration, TimeSpan maximumDuration)
	{
		if (item.Duration < minimumDuration || item.Duration > maximumDuration)
		{
			return false;
		}
		if (!string.IsNullOrWhiteSpace(folderPath) && IsPathUnderFolder(item.FilePath, folderPath))
		{
			return true;
		}
		if (!string.Equals(item.Category, "StationId", StringComparison.OrdinalIgnoreCase) && !string.Equals(item.Category, "Station ID", StringComparison.OrdinalIgnoreCase))
		{
			return string.Equals(item.Category, "ID", StringComparison.OrdinalIgnoreCase);
		}
		return true;
	}

	private static bool IsPathUnderFolder(string filePath, string folderPath)
	{
		try
		{
			string normalizedFile = NormalizePath(filePath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
			string normalizedFolder = NormalizePath(folderPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
			return normalizedFile.StartsWith(normalizedFolder, StringComparison.OrdinalIgnoreCase);
		}
		catch
		{
			return false;
		}
	}

	private static string InferCategory(string filePath)
	{
		string text = filePath.ToLowerInvariant();
		if (text.Contains("commercial") || text.Contains("\\ads\\") || text.Contains("\\ad\\"))
		{
			return "Commercial";
		}
		if (text.Contains("station id") || text.Contains("stationid") || text.Contains("\\ids\\"))
		{
			return "StationId";
		}
		if (text.Contains("sweeper") || text.Contains("jingle"))
		{
			return "Sweeper";
		}
		return "Music";
	}

	private LibraryItem PickWithRecentHistory(IList<LibraryItem> candidates, Queue<string> recentHistory, Func<LibraryItem, string> keySelector)
	{
		List<LibraryItem> available = candidates.Where((LibraryItem item) => !IsRecentlyUsed(recentHistory, keySelector(item))).ToList();
		if (available.Count == 0)
		{
			available = candidates.ToList();
		}
		LibraryItem selected = available[_random.Next(available.Count)];
		RememberRecent(recentHistory, keySelector(selected));
		return selected;
	}

	private static bool IsRecentlyUsed(Queue<string> recentHistory, string key)
	{
		return recentHistory.Any((string recent) => string.Equals(recent, key, StringComparison.OrdinalIgnoreCase));
	}

	private static void RememberRecent(Queue<string> recentHistory, string key)
	{
		if (!string.IsNullOrWhiteSpace(key))
		{
			recentHistory.Enqueue(key);
			while (recentHistory.Count > 5)
			{
				recentHistory.Dequeue();
			}
		}
	}

	private static bool IsAudioFilePath(string path)
	{
		if (string.IsNullOrWhiteSpace(path))
		{
			return false;
		}
		return Enumerable.Contains(SupportedAudioExtensions, Path.GetExtension(path).ToLowerInvariant());
	}

	private static string NormalizePath(string path)
	{
		return Path.GetFullPath(path);
	}

	private static DateTime? GetSourceModifiedAtUtc(string filePath)
	{
		try
		{
			return System.IO.File.GetLastWriteTimeUtc(filePath);
		}
		catch
		{
			return null;
		}
	}

	public void Dispose()
	{
		Items.CollectionChanged -= OnItemsCollectionChanged;
		_refreshDebounceTimer.Stop();
		foreach (FileSystemWatcher watcher in _watchers)
		{
			watcher.EnableRaisingEvents = false;
			watcher.Dispose();
		}
		_watchers.Clear();
		_context.Dispose();
	}
}
