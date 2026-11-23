using System;
using System.IO;
using System.Linq;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using RadioApp.Helpers;
using RadioApp.Models;
using RadioApp.Services;

namespace RadioApp.ViewModels;

public sealed class DeckViewModel : ViewModelBase
{
	private readonly AudioPlaybackService _playbackService;

	private LibraryItem? _currentItem;
	private string _trackPath = string.Empty;
	private string _trackTitle = "No track loaded";
	private string _trackArtist = "Select a track, playset, or enable Auto-DJ";
	private string _trackAlbum = string.Empty;
	private string _trackCategory = "IDLE";
	private string _trackBpmText = "—";
	private string _trackLoudnessText = "—";
	private string _nextTransitionSummary = "AutoMix ready";
	private BitmapImage? _albumArt;
	private byte[]? _waveformPeaks;
	private double? _introSeconds;
	private double? _mixOutSeconds;
	private double? _endSeconds;

	private double _tempo = 1.0;
	private double _playbackProgress;
	private bool _updatingProgress;
	private DeckPlaybackState _playbackState = DeckPlaybackState.Stopped;
	private bool _isTrackReady;
	private TimeSpan _position;
	private TimeSpan _duration;

	public LibraryItem? CurrentItem => _currentItem;

	public string TrackPath { get => _trackPath; private set => SetProperty(ref _trackPath, value); }
	public string TrackTitle { get => _trackTitle; private set => SetProperty(ref _trackTitle, value); }
	public string TrackArtist { get => _trackArtist; private set => SetProperty(ref _trackArtist, value); }
	public string TrackAlbum { get => _trackAlbum; private set => SetProperty(ref _trackAlbum, value); }
	public string TrackCategory { get => _trackCategory; private set => SetProperty(ref _trackCategory, value); }
	public string TrackBpmText { get => _trackBpmText; private set => SetProperty(ref _trackBpmText, value); }
	public string TrackLoudnessText { get => _trackLoudnessText; private set => SetProperty(ref _trackLoudnessText, value); }
	public string NextTransitionSummary { get => _nextTransitionSummary; set => SetProperty(ref _nextTransitionSummary, value); }
	public BitmapImage? AlbumArt { get => _albumArt; private set => SetProperty(ref _albumArt, value); }
	public byte[]? WaveformPeaks { get => _waveformPeaks; private set => SetProperty(ref _waveformPeaks, value); }
	public double? IntroSeconds { get => _introSeconds; private set => SetProperty(ref _introSeconds, value); }
	public double? MixOutSeconds { get => _mixOutSeconds; private set => SetProperty(ref _mixOutSeconds, value); }
	public double? EndSeconds { get => _endSeconds; private set => SetProperty(ref _endSeconds, value); }

	public double Tempo { get => _tempo; private set => SetProperty(ref _tempo, value); }
	public bool IsPlaying => _playbackState == DeckPlaybackState.Playing;
	public bool IsTrackReady { get => _isTrackReady; private set => SetProperty(ref _isTrackReady, value); }
	public string PlaybackStateText => _playbackState.ToString().ToUpperInvariant();

	public double DurationSeconds => Duration.TotalSeconds;

	public double PlaybackProgress
	{
		get => _playbackProgress;
		set
		{
			double clamped = Math.Clamp(value, 0.0, 100.0);
			if (SetProperty(ref _playbackProgress, clamped) && !_updatingProgress && Duration > TimeSpan.Zero)
			{
				SeekToPercent(clamped);
			}
		}
	}

	public TimeSpan Position
	{
		get => _position;
		private set
		{
			if (SetProperty(ref _position, value))
			{
				OnPropertyChanged(nameof(ElapsedFormatted));
				OnPropertyChanged(nameof(RemainingFormatted));
				OnPropertyChanged(nameof(IntroCountdownText));
				OnPropertyChanged(nameof(HasIntroCountdown));
				OnPropertyChanged(nameof(IsEndingSoon));
				OnPropertyChanged(nameof(IsCriticalEnding));
			}
		}
	}

	public TimeSpan Duration
	{
		get => _duration;
		private set
		{
			if (SetProperty(ref _duration, value))
			{
				OnPropertyChanged(nameof(DurationSeconds));
				OnPropertyChanged(nameof(TotalDurationFormatted));
				OnPropertyChanged(nameof(RemainingFormatted));
				OnPropertyChanged(nameof(IsEndingSoon));
				OnPropertyChanged(nameof(IsCriticalEnding));
			}
		}
	}

	public string ElapsedFormatted => FormatTime(Position);

	public string RemainingFormatted
	{
		get
		{
			if (Duration <= TimeSpan.Zero || Duration <= Position) return "-0:00";
			return "-" + FormatTime(Duration - Position);
		}
	}

	public string TotalDurationFormatted => FormatTime(Duration);

	public bool HasIntroCountdown => IsPlaying && IntroSeconds.HasValue && IntroSeconds.Value > 0.5 && Position.TotalSeconds < IntroSeconds.Value;

	public string IntroCountdownText =>
		HasIntroCountdown && IntroSeconds.HasValue
			? $"INTRO -{Math.Max(0.0, IntroSeconds.Value - Position.TotalSeconds):0.0}s"
			: string.Empty;

	public bool IsEndingSoon => IsPlaying && Duration > TimeSpan.Zero && (Duration - Position).TotalSeconds <= 25.0 && (Duration - Position).TotalSeconds > 10.0;
	public bool IsCriticalEnding => IsPlaying && Duration > TimeSpan.Zero && (Duration - Position).TotalSeconds <= 10.0;

	public ICommand LoadFileCommand { get; }
	public ICommand PlayCommand { get; }
	public ICommand PauseCommand { get; }
	public ICommand PlayPauseToggleCommand { get; }
	public ICommand StopCommand { get; }
	public ICommand CueCommand { get; }
	public ICommand EjectCommand { get; }
	public ICommand SeekPercentCommand { get; }
	public ICommand NormalTempoCommand { get; }
	public ICommand FastTempoCommand { get; }
	public ICommand SlowTempoCommand { get; }

	public event Action<LibraryItem>? ManualFileLoaded;

	public DeckViewModel(AudioPlaybackService playbackService)
	{
		_playbackService = playbackService;

		LoadFileCommand = new RelayCommand(BrowseAndLoadFile);
		PlayCommand = new RelayCommand(() => _playbackService.Play(), () => IsTrackReady);
		PauseCommand = new RelayCommand(() => _playbackService.Pause(), () => IsPlaying);
		PlayPauseToggleCommand = new RelayCommand(() =>
		{
			if (IsPlaying) _playbackService.Pause();
			else if (IsTrackReady) _playbackService.Play();
		}, () => IsTrackReady);
		StopCommand = new RelayCommand(() => _playbackService.Stop(), () => _playbackState != DeckPlaybackState.Stopped);
		CueCommand = new RelayCommand(() =>
		{
			TimeSpan cueStart = _currentItem != null ? TimeSpan.FromMilliseconds(_currentItem.EffectiveStartCueMs) : TimeSpan.Zero;
			_playbackService.Seek(cueStart);
			_playbackService.Pause();
		}, () => IsTrackReady);
		EjectCommand = new RelayCommand(Eject, () => IsTrackReady);
		SeekPercentCommand = new RelayCommand(p =>
		{
			if (p is double percent) SeekToPercent(percent);
		});
		NormalTempoCommand = new RelayCommand(() => SetTempo(1.0));
		FastTempoCommand = new RelayCommand(() => SetTempo(1.06));
		SlowTempoCommand = new RelayCommand(() => SetTempo(0.95));
	}

	public void UpdateProgress(TimeSpan position, TimeSpan duration, DeckPlaybackState state)
	{
		if (_playbackState != state)
		{
			_playbackState = state;
			OnPropertyChanged(nameof(IsPlaying));
			OnPropertyChanged(nameof(PlaybackStateText));
			RelayCommand.InvalidateAll();
		}
		_updatingProgress = true;
		PlaybackProgress = duration.TotalSeconds > 0.0 ? Math.Clamp(position.TotalSeconds / duration.TotalSeconds * 100.0, 0.0, 100.0) : 0.0;
		_updatingProgress = false;
		Position = position;
		Duration = duration;
	}

	public bool LoadLibraryItem(LibraryItem item, bool smoothCrossfadeIfPlaying = false)
	{
		if (string.IsNullOrWhiteSpace(item.FilePath)) return false;
		bool loaded = _playbackService.LoadTrack(item, smoothCrossfadeIfPlaying);
		if (loaded)
		{
			ApplyLibraryItemMetadata(item);
			IsTrackReady = true;
			SetTempo(1.0);
			RelayCommand.InvalidateAll();
		}
		return loaded;
	}

	public void SetTrackFromAutoMixPreload(LibraryItem item)
	{
		ApplyLibraryItemMetadata(item);
		IsTrackReady = true;
		Tempo = 1.0;
		RelayCommand.InvalidateAll();
	}

	public void RefreshCurrentItemAnalysis(LibraryItem updated)
	{
		if (_currentItem != null && _currentItem.Id == updated.Id)
		{
			ApplyLibraryItemMetadata(updated);
		}
	}

	public void SeekToPercent(double percent)
	{
		if (!IsTrackReady || Duration <= TimeSpan.Zero) return;
		double clamped = Math.Clamp(percent, 0.0, 100.0);
		_playbackService.Seek(TimeSpan.FromSeconds(Duration.TotalSeconds * clamped / 100.0));
	}

	private void BrowseAndLoadFile()
	{
		var dialog = new OpenFileDialog { Filter = AudioFileProbeCore.OpenFileDialogFilter };
		if (dialog.ShowDialog() != true) return;

		AudioProbeResult probe = AudioFileProbeCore.Probe(dialog.FileName);
		if (!probe.IsPlayable)
		{
			return;
		}
		var item = new LibraryItem
		{
			FilePath = dialog.FileName,
			Title = Path.GetFileNameWithoutExtension(dialog.FileName),
			Category = "Music",
			Duration = probe.Duration
		};
		EnrichFromTags(item);
		if (LoadLibraryItem(item, smoothCrossfadeIfPlaying: IsPlaying))
		{
			ManualFileLoaded?.Invoke(item);
		}
	}

	private void Eject()
	{
		_playbackService.Stop();
		_currentItem = null;
		TrackPath = string.Empty;
		TrackTitle = "No track loaded";
		TrackArtist = "Select a track, playset, or enable Auto-DJ";
		TrackAlbum = string.Empty;
		TrackCategory = "IDLE";
		TrackBpmText = "—";
		TrackLoudnessText = "—";
		AlbumArt = null;
		WaveformPeaks = null;
		IntroSeconds = null;
		MixOutSeconds = null;
		EndSeconds = null;
		IsTrackReady = false;
		Position = TimeSpan.Zero;
		Duration = TimeSpan.Zero;
		_updatingProgress = true;
		PlaybackProgress = 0.0;
		_updatingProgress = false;
		RelayCommand.InvalidateAll();
	}

	private void SetTempo(double tempo)
	{
		Tempo = Math.Clamp(tempo, 0.85, 1.18);
		_playbackService.SetManualTempo(Tempo);
	}

	private void ApplyLibraryItemMetadata(LibraryItem item)
	{
		_currentItem = item;
		TrackPath = item.FilePath;
		TrackTitle = string.IsNullOrWhiteSpace(item.Title) ? Path.GetFileNameWithoutExtension(item.FilePath) : item.Title;
		TrackArtist = string.IsNullOrWhiteSpace(item.Artist) ? "Unknown Artist" : item.Artist;
		TrackAlbum = string.IsNullOrWhiteSpace(item.Album) ? string.Empty : item.Album;
		TrackCategory = string.IsNullOrWhiteSpace(item.Category) ? "MUSIC" : item.Category.ToUpperInvariant();
		TrackBpmText = item.Bpm.HasValue && item.Bpm.Value > 0 ? $"{item.Bpm.Value:0.0} BPM" : "Analyzing...";
		TrackLoudnessText = item.LoudnessLufs.HasValue ? $"{item.LoudnessLufs.Value:0.0} LUFS" : "—";
		WaveformPeaks = item.DecodedWaveformPeaks;
		IntroSeconds = item.IntroCueMs.HasValue ? item.IntroCueMs.Value / 1000.0 : null;
		MixOutSeconds = item.EffectiveMixOutMs > 0 ? item.EffectiveMixOutMs / 1000.0 : null;
		EndSeconds = item.EffectiveEndCueMs > 0 ? item.EffectiveEndCueMs / 1000.0 : null;
		AlbumArt = TryExtractAlbumArt(item.FilePath);
	}

	private static void EnrichFromTags(LibraryItem item)
	{
		try
		{
			using TagLib.File tagFile = TagLib.File.Create(item.FilePath);
			if (!string.IsNullOrWhiteSpace(tagFile.Tag.Title)) item.Title = tagFile.Tag.Title;
			string artist = tagFile.Tag.FirstPerformer ?? string.Join(", ", tagFile.Tag.Performers ?? Enumerable.Empty<string>());
			if (!string.IsNullOrWhiteSpace(artist)) item.Artist = artist;
			if (!string.IsNullOrWhiteSpace(tagFile.Tag.Album)) item.Album = tagFile.Tag.Album;
		}
		catch
		{
		}
	}

	private static BitmapImage? TryExtractAlbumArt(string filePath)
	{
		if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath)) return null;
		try
		{
			using TagLib.File tagFile = TagLib.File.Create(filePath);
			TagLib.IPicture? pic = tagFile.Tag.Pictures?.FirstOrDefault();
			if (pic == null || pic.Data == null || pic.Data.Count == 0) return null;

			using var ms = new MemoryStream(pic.Data.Data);
			var bmp = new BitmapImage();
			bmp.BeginInit();
			bmp.CacheOption = BitmapCacheOption.OnLoad;
			bmp.DecodePixelWidth = 160;
			bmp.StreamSource = ms;
			bmp.EndInit();
			bmp.Freeze();
			return bmp;
		}
		catch
		{
			return null;
		}
	}

	private static string FormatTime(TimeSpan ts) =>
		ts.TotalHours >= 1.0 ? ts.ToString("h\\:mm\\:ss") : ts.ToString("m\\:ss");
}
