using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows.Input;
using Microsoft.Win32;
using RadioApp.Helpers;
using RadioApp.Models;
using RadioApp.Services;
using TagLib;

namespace RadioApp.ViewModels;

public class DeckViewModel : ViewModelBase
{
	private readonly AudioPlaybackService _playbackService;

	private string _trackPath = string.Empty;

	private string _trackTitle = "No track loaded";

	private string _trackArtist = string.Empty;

	private string _trackAlbum = string.Empty;

	private double _volume = 0.75;

	private double _volumeFactor = 1.0;

	private double _tempo = 1.0;

	private double _playbackProgress;

	private bool _updatingPlaybackProgress;

	private string _positionText = "00:00 / 00:00";

	private DeckPlaybackState _playbackState;

	private bool _isTrackReady;

	private TimeSpan _position;

	private TimeSpan _duration;

	public DeckId DeckId { get; }

	public string TrackPath
	{
		get
		{
			return _trackPath;
		}
		private set
		{
			SetProperty(ref _trackPath, value, "TrackPath");
		}
	}

	public string TrackTitle
	{
		get
		{
			return _trackTitle;
		}
		private set
		{
			SetProperty(ref _trackTitle, value, "TrackTitle");
		}
	}

	public string TrackArtist
	{
		get
		{
			return _trackArtist;
		}
		private set
		{
			SetProperty(ref _trackArtist, value, "TrackArtist");
		}
	}

	public string TrackAlbum
	{
		get
		{
			return _trackAlbum;
		}
		private set
		{
			SetProperty(ref _trackAlbum, value, "TrackAlbum");
		}
	}

	public double Volume
	{
		get
		{
			return _volume;
		}
		set
		{
			SetProperty(ref _volume, value, "Volume");
			UpdateServiceVolume();
		}
	}

	public double VolumeFactor
	{
		get
		{
			return _volumeFactor;
		}
		set
		{
			SetProperty(ref _volumeFactor, value, "VolumeFactor");
			UpdateServiceVolume();
		}
	}

	public double PlaybackProgress
	{
		get
		{
			return _playbackProgress;
		}
		set
		{
			double clamped = Math.Clamp(value, 0.0, 100.0);
			if (SetProperty(ref _playbackProgress, clamped, "PlaybackProgress") && !_updatingPlaybackProgress && Duration > TimeSpan.Zero)
			{
				SeekToPercent(clamped);
			}
		}
	}

	public double Tempo
	{
		get
		{
			return _tempo;
		}
		private set
		{
			SetProperty(ref _tempo, value, "Tempo");
		}
	}

	public bool IsPlaying => _playbackState == DeckPlaybackState.Playing;

	public TimeSpan Position
	{
		get
		{
			return _position;
		}
		private set
		{
			SetProperty(ref _position, value, "Position");
			OnPropertyChanged("ElapsedFormatted");
			OnPropertyChanged("RemainingFormatted");
			OnPropertyChanged("IsEndingSoon");
		}
	}

	public TimeSpan Duration
	{
		get
		{
			return _duration;
		}
		private set
		{
			SetProperty(ref _duration, value, "Duration");
			OnPropertyChanged("TotalDurationFormatted");
			OnPropertyChanged("RemainingFormatted");
			OnPropertyChanged("IsEndingSoon");
		}
	}

	public string ElapsedFormatted => Position.TotalHours >= 1.0 ? Position.ToString("h\\:mm\\:ss") : Position.ToString("m\\:ss");

	public string RemainingFormatted
	{
		get
		{
			if (Duration <= TimeSpan.Zero || Duration <= Position)
			{
				return "-0:00";
			}
			TimeSpan rem = Duration - Position;
			return "-" + (rem.TotalHours >= 1.0 ? rem.ToString("h\\:mm\\:ss") : rem.ToString("m\\:ss"));
		}
	}

	public string TotalDurationFormatted => Duration.TotalHours >= 1.0 ? Duration.ToString("h\\:mm\\:ss") : Duration.ToString("m\\:ss");

	public bool IsEndingSoon => IsPlaying && Duration > TimeSpan.Zero && (Duration - Position).TotalSeconds <= 15.0;

	public bool IsTrackReady
	{
		get
		{
			return _isTrackReady;
		}
		private set
		{
			SetProperty(ref _isTrackReady, value, "IsTrackReady");
		}
	}

	public string PlaybackStateText => _playbackState.ToString();

	public string PositionText
	{
		get
		{
			return _positionText;
		}
		private set
		{
			SetProperty(ref _positionText, value, "PositionText");
		}
	}

	public ICommand LoadTrackCommand { get; }

	public ICommand PlayCommand { get; }

	public ICommand PauseCommand { get; }

	public ICommand StopCommand { get; }

	public ICommand CueCommand { get; }

	public ICommand EjectCommand { get; }

	public ICommand NormalTempoCommand { get; }

	public ICommand FastTempoCommand { get; }

	public DeckViewModel(AudioPlaybackService playbackService, DeckId deckId)
	{
		_playbackService = playbackService;
		DeckId = deckId;
		LoadTrackCommand = new RelayCommand(delegate
		{
			LoadTrack();
		});
		PlayCommand = new RelayCommand(delegate
		{
			Play();
		}, (object? _) => IsTrackReady);
		PauseCommand = new RelayCommand(delegate
		{
			Pause();
		}, (object? _) => _playbackState == DeckPlaybackState.Playing);
		StopCommand = new RelayCommand(delegate
		{
			Stop();
		}, (object? _) => _playbackState != DeckPlaybackState.Stopped);
		CueCommand = new RelayCommand(delegate
		{
			_playbackService.Seek(DeckId, TimeSpan.Zero);
			Pause();
		}, (object? _) => IsTrackReady);
		EjectCommand = new RelayCommand(delegate
		{
			Stop();
			TrackPath = string.Empty;
			TrackTitle = "No track loaded";
			TrackArtist = string.Empty;
			TrackAlbum = string.Empty;
			IsTrackReady = false;
			Position = TimeSpan.Zero;
			Duration = TimeSpan.Zero;
			PlaybackProgress = 0.0;
		}, (object? _) => IsTrackReady);
		NormalTempoCommand = new RelayCommand(delegate
		{
			SetTempo(1.0);
		});
		FastTempoCommand = new RelayCommand(delegate
		{
			SetTempo(1.1);
		});
	}

	public void UpdateProgress(TimeSpan position, TimeSpan duration, DeckPlaybackState state)
	{
		if (_playbackState != state)
		{
			_playbackState = state;
			OnPropertyChanged("IsPlaying");
			OnPropertyChanged("PlaybackStateText");
			CommandManager.InvalidateRequerySuggested();
		}
		_updatingPlaybackProgress = true;
		PlaybackProgress = ((duration.TotalSeconds > 0.0) ? (position.TotalSeconds / duration.TotalSeconds * 100.0) : 0.0);
		_updatingPlaybackProgress = false;
		Position = position;
		Duration = duration;
		PositionText = $"{position:hh\\:mm\\:ss} / {duration:hh\\:mm\\:ss}";
	}

	private void LoadTrack()
	{
		OpenFileDialog dialog = new OpenFileDialog
		{
			Filter = "Audio and video files|*.mp3;*.mp4;*.flac;*.wav;*.wma|All files|*.*"
		};
		if (dialog.ShowDialog() == true)
		{
			ApplyTrackMetadata(dialog.FileName, null, null, null);
			IsTrackReady = _playbackService.LoadTrack(DeckId, dialog.FileName);
			ResetTempo();
			CommandManager.InvalidateRequerySuggested();
		}
	}

	public void LoadTrackFromSchedule(string filePath, string title)
	{
		if (!string.IsNullOrWhiteSpace(filePath))
		{
			ApplyTrackMetadata(filePath, title, null, null);
			IsTrackReady = _playbackService.LoadTrack(DeckId, TrackPath);
			ResetTempo();
			CommandManager.InvalidateRequerySuggested();
		}
	}

	public void LoadLibraryItem(LibraryItem item)
	{
		if (!string.IsNullOrWhiteSpace(item.FilePath))
		{
			ApplyTrackMetadata(item.FilePath, item.Title, item.Artist, item.Album);
			IsTrackReady = _playbackService.LoadTrack(DeckId, TrackPath, item.IsStationIdCue);
			ResetTempo();
			CommandManager.InvalidateRequerySuggested();
		}
	}

	public void SetTrackFromPreload(string filePath, string title)
	{
		ApplyTrackMetadata(filePath, title, null, null);
		IsTrackReady = true;
		ResetTempo();
		CommandManager.InvalidateRequerySuggested();
	}

	public void SetTrackFromPreload(LibraryItem item)
	{
		ApplyTrackMetadata(item.FilePath, item.Title, item.Artist, item.Album);
		IsTrackReady = true;
		ResetTempo();
		CommandManager.InvalidateRequerySuggested();
	}

	public void SetVolumeFactor(double factor)
	{
		VolumeFactor = Math.Clamp(factor, 0.0, 1.0);
	}

	public void SeekToPercent(double percent)
	{
		if (IsTrackReady && !(Duration <= TimeSpan.Zero))
		{
			double clamped = Math.Clamp(percent, 0.0, 100.0);
			TimeSpan target = TimeSpan.FromSeconds(Duration.TotalSeconds * clamped / 100.0);
			_playbackService.Seek(DeckId, target);
		}
	}

	private void UpdateServiceVolume()
	{
		_playbackService.SetVolume(DeckId, _volume * _volumeFactor);
	}

	private void SetTempo(double tempo)
	{
		Tempo = tempo;
		_playbackService.SetTempo(DeckId, tempo);
	}

	private void ResetTempo()
	{
		SetTempo(1.0);
	}

	private void ApplyTrackMetadata(string filePath, string? title, string? artist, string? album)
	{
		TrackPath = filePath;
		string resolvedTitle = title;
		string resolvedArtist = artist;
		string resolvedAlbum = album;
		if (string.IsNullOrWhiteSpace(resolvedTitle) || string.IsNullOrWhiteSpace(resolvedArtist) || string.IsNullOrWhiteSpace(resolvedAlbum))
		{
			try
			{
				using TagLib.File tagFile = TagLib.File.Create(filePath);
				resolvedTitle = (string.IsNullOrWhiteSpace(resolvedTitle) ? tagFile.Tag.Title : resolvedTitle);
				string text;
				if (!string.IsNullOrWhiteSpace(resolvedArtist))
				{
					text = resolvedArtist;
				}
				else
				{
					text = tagFile.Tag.FirstPerformer;
					if (text == null)
					{
						IEnumerable<string> performers = tagFile.Tag.Performers;
						text = string.Join(", ", performers ?? Enumerable.Empty<string>());
					}
				}
				resolvedArtist = text;
				resolvedAlbum = (string.IsNullOrWhiteSpace(resolvedAlbum) ? tagFile.Tag.Album : resolvedAlbum);
			}
			catch
			{
			}
		}
		TrackTitle = (string.IsNullOrWhiteSpace(resolvedTitle) ? Path.GetFileName(filePath) : resolvedTitle);
		TrackArtist = (string.IsNullOrWhiteSpace(resolvedArtist) ? "Unknown artist" : resolvedArtist);
		TrackAlbum = (string.IsNullOrWhiteSpace(resolvedAlbum) ? "Unknown album" : resolvedAlbum);
	}

	private void Play()
	{
		_playbackService.Play(DeckId);
	}

	private void Pause()
	{
		_playbackService.Pause(DeckId);
	}

	private void Stop()
	{
		_playbackService.Stop(DeckId);
	}
}
