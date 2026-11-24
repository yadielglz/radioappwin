using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations.Schema;
using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;

namespace RadioApp.Models;

public class LibraryItem : INotifyPropertyChanged
{
	private string _title = string.Empty;
	private string _artist = string.Empty;
	private string _album = string.Empty;
	private string _category = "Music";
	private string _tags = string.Empty;
	private int? _startCueMs;
	private int? _introCueMs;
	private int? _outroCueMs;
	private int? _endCueMs;
	private DateTime? _lastPlayedAtUtc;
	private int _playCount;
	private double? _bpm;
	private double? _bpmConfidence;
	private double? _loudnessLufs;
	private double? _truePeakDb;
	private int? _autoCueInMs;
	private int? _autoMixOutMs;
	private int? _autoCueOutMs;
	private int? _firstBeatMs;
	private int? _outroBeatMs;
	private TrackEdgeProfile _introProfile;
	private TrackEdgeProfile _outroProfile;
	private int? _encoderDelaySamples;
	private int? _encoderPaddingSamples;
	private string? _waveformPeaksBase64;
	private int _analysisVersion;
	private bool _fileExistsCached = true;

	public int Id { get; set; }

	public string FilePath { get; set; } = string.Empty;

	public string Title
	{
		get => _title;
		set => SetProperty(ref _title, value ?? string.Empty);
	}

	public string Artist
	{
		get => _artist;
		set => SetProperty(ref _artist, value ?? string.Empty);
	}

	public string Album
	{
		get => _album;
		set => SetProperty(ref _album, value ?? string.Empty);
	}

	public string Category
	{
		get => _category;
		set
		{
			if (SetProperty(ref _category, value ?? string.Empty))
			{
				OnPropertyChanged(nameof(IsSweeperCue));
				OnPropertyChanged(nameof(IsStationIdCue));
				OnPropertyChanged(nameof(IsCommercialCue));
				OnPropertyChanged(nameof(QueueCueLabel));
			}
		}
	}

	public string Tags
	{
		get => _tags;
		set => SetProperty(ref _tags, value ?? string.Empty);
	}

	public TimeSpan Duration { get; set; }

	public DateTime DateAdded { get; set; } = DateTime.UtcNow;

	public DateTime? SourceModifiedAtUtc { get; set; }

	public DateTime? LastPlayedAtUtc
	{
		get => _lastPlayedAtUtc;
		set
		{
			if (SetProperty(ref _lastPlayedAtUtc, value))
			{
				OnPropertyChanged(nameof(LastPlayedText));
			}
		}
	}

	public int PlayCount
	{
		get => _playCount;
		set => SetProperty(ref _playCount, value);
	}

	public int? StartCueMs
	{
		get => _startCueMs;
		set
		{
			if (SetProperty(ref _startCueMs, value))
			{
				OnPropertyChanged(nameof(StartCueText));
				OnPropertyChanged(nameof(EffectiveStartCueMs));
			}
		}
	}

	public int? IntroCueMs
	{
		get => _introCueMs;
		set
		{
			if (SetProperty(ref _introCueMs, value))
			{
				OnPropertyChanged(nameof(IntroCue));
				OnPropertyChanged(nameof(IntroCueText));
			}
		}
	}

	public int? OutroCueMs
	{
		get => _outroCueMs;
		set
		{
			if (SetProperty(ref _outroCueMs, value))
			{
				OnPropertyChanged(nameof(OutroCue));
				OnPropertyChanged(nameof(OutroCueText));
				OnPropertyChanged(nameof(EffectiveMixOutMs));
			}
		}
	}

	public int? EndCueMs
	{
		get => _endCueMs;
		set
		{
			if (SetProperty(ref _endCueMs, value))
			{
				OnPropertyChanged(nameof(EndCueText));
				OnPropertyChanged(nameof(EffectiveEndCueMs));
			}
		}
	}

	public double? Bpm
	{
		get => _bpm;
		set
		{
			if (SetProperty(ref _bpm, value))
			{
				OnPropertyChanged(nameof(BpmText));
			}
		}
	}

	public double? BpmConfidence
	{
		get => _bpmConfidence;
		set => SetProperty(ref _bpmConfidence, value);
	}

	public int? FirstBeatMs
	{
		get => _firstBeatMs;
		set => SetProperty(ref _firstBeatMs, value);
	}

	public int? OutroBeatMs
	{
		get => _outroBeatMs;
		set => SetProperty(ref _outroBeatMs, value);
	}

	public double? LoudnessLufs
	{
		get => _loudnessLufs;
		set
		{
			if (SetProperty(ref _loudnessLufs, value))
			{
				OnPropertyChanged(nameof(LoudnessText));
			}
		}
	}

	public double? TruePeakDb
	{
		get => _truePeakDb;
		set => SetProperty(ref _truePeakDb, value);
	}

	public int? AutoCueInMs
	{
		get => _autoCueInMs;
		set
		{
			if (SetProperty(ref _autoCueInMs, value))
			{
				OnPropertyChanged(nameof(EffectiveStartCueMs));
			}
		}
	}

	public int? AutoMixOutMs
	{
		get => _autoMixOutMs;
		set
		{
			if (SetProperty(ref _autoMixOutMs, value))
			{
				OnPropertyChanged(nameof(EffectiveMixOutMs));
			}
		}
	}

	public int? AutoCueOutMs
	{
		get => _autoCueOutMs;
		set
		{
			if (SetProperty(ref _autoCueOutMs, value))
			{
				OnPropertyChanged(nameof(EffectiveEndCueMs));
			}
		}
	}

	public TrackEdgeProfile IntroProfile
	{
		get => _introProfile;
		set
		{
			if (SetProperty(ref _introProfile, value))
			{
				OnPropertyChanged(nameof(EdgeProfileText));
			}
		}
	}

	public TrackEdgeProfile OutroProfile
	{
		get => _outroProfile;
		set
		{
			if (SetProperty(ref _outroProfile, value))
			{
				OnPropertyChanged(nameof(EdgeProfileText));
			}
		}
	}

	public int? EncoderDelaySamples
	{
		get => _encoderDelaySamples;
		set => SetProperty(ref _encoderDelaySamples, value);
	}

	public int? EncoderPaddingSamples
	{
		get => _encoderPaddingSamples;
		set => SetProperty(ref _encoderPaddingSamples, value);
	}

	public string? WaveformPeaksBase64
	{
		get => _waveformPeaksBase64;
		set => SetProperty(ref _waveformPeaksBase64, value);
	}

	public int AnalysisVersion
	{
		get => _analysisVersion;
		set
		{
			if (SetProperty(ref _analysisVersion, value))
			{
				OnPropertyChanged(nameof(IsAnalyzed));
			}
		}
	}

	public bool FileExistsCached
	{
		get => _fileExistsCached;
		set
		{
			if (SetProperty(ref _fileExistsCached, value))
			{
				OnPropertyChanged(nameof(FileStatus));
			}
		}
	}

	[NotMapped]
	public bool IsAnalyzed => AnalysisVersion >= AppVersionInfo.CurrentAnalysisVersion;

	[NotMapped]
	public int EffectiveStartCueMs => StartCueMs ?? AutoCueInMs ?? 0;

	[NotMapped]
	public int EffectiveMixOutMs => OutroCueMs ?? AutoMixOutMs ?? EffectiveEndCueMs;

	[NotMapped]
	public int EffectiveEndCueMs => EndCueMs ?? AutoCueOutMs ?? (int)Math.Max(0, Duration.TotalMilliseconds);

	[NotMapped]
	public TimeSpan? IntroCue => IntroCueMs.HasValue ? TimeSpan.FromMilliseconds(IntroCueMs.Value) : null;

	[NotMapped]
	public TimeSpan? OutroCue => OutroCueMs.HasValue ? TimeSpan.FromMilliseconds(OutroCueMs.Value) : null;

	[NotMapped]
	public string StartCueText
	{
		get => FormatSeconds(StartCueMs);
		set => StartCueMs = ParseSeconds(value);
	}

	[NotMapped]
	public string IntroCueText
	{
		get => FormatSeconds(IntroCueMs);
		set => IntroCueMs = ParseSeconds(value);
	}

	[NotMapped]
	public string OutroCueText
	{
		get => FormatSeconds(OutroCueMs);
		set => OutroCueMs = ParseSeconds(value);
	}

	[NotMapped]
	public string EndCueText
	{
		get => FormatSeconds(EndCueMs);
		set => EndCueMs = ParseSeconds(value);
	}

	[NotMapped]
	public string BpmText => Bpm.HasValue && Bpm.Value > 0 ? $"{Bpm.Value:0.0}" : "—";

	[NotMapped]
	public string LoudnessText => LoudnessLufs.HasValue ? $"{LoudnessLufs.Value:0.0} LUFS" : "—";

	[NotMapped]
	public string EdgeProfileText => IntroProfile == TrackEdgeProfile.Unknown && OutroProfile == TrackEdgeProfile.Unknown
		? "Unanalyzed"
		: $"In: {IntroProfile} / Out: {OutroProfile}";

	[NotMapped]
	public string LastPlayedText => LastPlayedAtUtc.HasValue ? LastPlayedAtUtc.Value.ToLocalTime().ToString("g") : "Never";

	[NotMapped]
	public string DurationText => Duration.ToString(Duration.TotalHours >= 1.0 ? "h\\:mm\\:ss" : "m\\:ss");

	[NotMapped]
	public string FileName => string.IsNullOrWhiteSpace(FilePath) ? string.Empty : Path.GetFileName(FilePath);

	[NotMapped]
	public string FolderPath => string.IsNullOrWhiteSpace(FilePath) ? string.Empty : (Path.GetDirectoryName(FilePath) ?? string.Empty);

	[NotMapped]
	public string DateAddedText => DateAdded.ToLocalTime().ToString("g");

	[NotMapped]
	public string FileStatus => FileExistsCached ? "Available" : "Missing";

	[NotMapped]
	public bool IsSweeperCue =>
		string.Equals(Category, "Sweeper", StringComparison.OrdinalIgnoreCase) ||
		string.Equals(Category, "Sweep", StringComparison.OrdinalIgnoreCase) ||
		string.Equals(Category, "Jingle", StringComparison.OrdinalIgnoreCase);

	[NotMapped]
	public bool IsStationIdCue =>
		string.Equals(Category, "StationId", StringComparison.OrdinalIgnoreCase) ||
		string.Equals(Category, "Station ID", StringComparison.OrdinalIgnoreCase) ||
		string.Equals(Category, "ID", StringComparison.OrdinalIgnoreCase);

	[NotMapped]
	public bool IsCommercialCue =>
		string.Equals(Category, "Commercial", StringComparison.OrdinalIgnoreCase) ||
		string.Equals(Category, "Commercials", StringComparison.OrdinalIgnoreCase) ||
		string.Equals(Category, "Ad", StringComparison.OrdinalIgnoreCase) ||
		string.Equals(Category, "Ads", StringComparison.OrdinalIgnoreCase) ||
		string.Equals(Category, "Promo", StringComparison.OrdinalIgnoreCase);

	[NotMapped]
	public bool IsSpokenWordCue =>
		IsStationIdCue || IsCommercialCue ||
		string.Equals(Category, "VoiceTrack", StringComparison.OrdinalIgnoreCase);

	[NotMapped]
	public string QueueCueLabel
	{
		get
		{
			if (IsStationIdCue) return "ID";
			if (IsSweeperCue) return "SWP";
			if (IsCommercialCue) return "AD";
			if (string.Equals(Category, "VoiceTrack", StringComparison.OrdinalIgnoreCase)) return "VT";
			return string.Empty;
		}
	}

	[NotMapped]
	public byte[]? DecodedWaveformPeaks
	{
		get
		{
			if (string.IsNullOrWhiteSpace(WaveformPeaksBase64))
			{
				return null;
			}
			try
			{
				return Convert.FromBase64String(WaveformPeaksBase64);
			}
			catch
			{
				return null;
			}
		}
	}

	public event PropertyChangedEventHandler? PropertyChanged;

	private static string FormatSeconds(int? ms) =>
		ms.HasValue ? (ms.Value / 1000.0).ToString("0.0", CultureInfo.InvariantCulture) + "s" : string.Empty;

	private static int? ParseSeconds(string? text)
	{
		if (string.IsNullOrWhiteSpace(text))
		{
			return null;
		}
		string cleaned = text.Trim().TrimEnd('s', 'S');
		if (double.TryParse(cleaned, NumberStyles.Float, CultureInfo.InvariantCulture, out double seconds) && seconds >= 0)
		{
			return (int)Math.Round(seconds * 1000.0);
		}
		return null;
	}

	protected bool SetProperty<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
	{
		if (EqualityComparer<T>.Default.Equals(field, value))
		{
			return false;
		}
		field = value;
		OnPropertyChanged(propertyName);
		return true;
	}

	protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
	{
		PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
	}
}
