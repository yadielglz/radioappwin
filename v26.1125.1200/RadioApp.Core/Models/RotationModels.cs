using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;

namespace RadioApp.Models;

/// <summary>
/// A rich, visual broadcast daypart rotation block supporting days-of-week,
/// Tag / Category / Playset / AllMusic sources, priority tie-breaking, color coding,
/// and optional per-daypart separation &amp; sweeper cadence overrides.
/// </summary>
public class RotationBlock : INotifyPropertyChanged
{
	private string _id = Guid.NewGuid().ToString("N");
	private string _name = "Daypart Block";
	private string _startTime = "06:00";
	private string _endTime = "12:00";
	private RotationDayMask _daysMask = RotationDayMask.All;
	private RotationSourceKind _sourceKind = RotationSourceKind.Tag;
	private string _sourceValue = string.Empty;
	private int? _playsetId;
	private string _colorHex = "#0284C7";
	private int _priority = 10;
	private bool _enabled = true;
	private int? _artistSeparationOverrideMinutes;
	private int? _sweeperMinSongsOverride;
	private int? _sweeperMaxSongsOverride;

	private int _cachedMatchCount = -1;
	private string _cachedPlaysetName = string.Empty;

	public string Id
	{
		get => _id;
		set => SetProperty(ref _id, string.IsNullOrWhiteSpace(value) ? Guid.NewGuid().ToString("N") : value);
	}

	public string Name
	{
		get => _name;
		set => SetProperty(ref _name, value ?? string.Empty);
	}

	public string StartTime
	{
		get => _startTime;
		set
		{
			if (SetProperty(ref _startTime, value ?? "00:00"))
			{
				OnPropertyChanged(nameof(TimeRangeDisplay));
			}
		}
	}

	public string EndTime
	{
		get => _endTime;
		set
		{
			if (SetProperty(ref _endTime, value ?? "23:59"))
			{
				OnPropertyChanged(nameof(TimeRangeDisplay));
			}
		}
	}

	public RotationDayMask DaysMask
	{
		get => _daysMask;
		set
		{
			if (SetProperty(ref _daysMask, value))
			{
				NotifyDayProperties();
			}
		}
	}

	public RotationSourceKind SourceKind
	{
		get => _sourceKind;
		set
		{
			if (SetProperty(ref _sourceKind, value))
			{
				OnPropertyChanged(nameof(SourceSummary));
				OnPropertyChanged(nameof(IsTagSource));
				OnPropertyChanged(nameof(IsCategorySource));
				OnPropertyChanged(nameof(IsPlaysetSource));
			}
		}
	}

	/// <summary>
	/// Tag name (when SourceKind=Tag) or Category name (when SourceKind=Category).
	/// </summary>
	public string SourceValue
	{
		get => _sourceValue;
		set
		{
			if (SetProperty(ref _sourceValue, value ?? string.Empty))
			{
				OnPropertyChanged(nameof(SourceSummary));
			}
		}
	}

	public int? PlaysetId
	{
		get => _playsetId;
		set
		{
			if (SetProperty(ref _playsetId, value))
			{
				OnPropertyChanged(nameof(SourceSummary));
			}
		}
	}

	public string ColorHex
	{
		get => _colorHex;
		set => SetProperty(ref _colorHex, string.IsNullOrWhiteSpace(value) ? "#0284C7" : value);
	}

	public int Priority
	{
		get => _priority;
		set => SetProperty(ref _priority, Math.Clamp(value, 1, 100));
	}

	public bool Enabled
	{
		get => _enabled;
		set => SetProperty(ref _enabled, value);
	}

	public int? ArtistSeparationOverrideMinutes
	{
		get => _artistSeparationOverrideMinutes;
		set => SetProperty(ref _artistSeparationOverrideMinutes, value.HasValue ? Math.Clamp(value.Value, 0, 360) : null);
	}

	public int? SweeperMinSongsOverride
	{
		get => _sweeperMinSongsOverride;
		set => SetProperty(ref _sweeperMinSongsOverride, value.HasValue ? Math.Clamp(value.Value, 1, 30) : null);
	}

	public int? SweeperMaxSongsOverride
	{
		get => _sweeperMaxSongsOverride;
		set => SetProperty(ref _sweeperMaxSongsOverride, value.HasValue ? Math.Clamp(value.Value, 1, 30) : null);
	}

	// Convenience Day-of-Week Toggle Properties (Not serialized — backed by DaysMask)
	[JsonIgnore] public bool Mon { get => DaysMask.HasFlag(RotationDayMask.Monday); set => SetDayFlag(RotationDayMask.Monday, value); }
	[JsonIgnore] public bool Tue { get => DaysMask.HasFlag(RotationDayMask.Tuesday); set => SetDayFlag(RotationDayMask.Tuesday, value); }
	[JsonIgnore] public bool Wed { get => DaysMask.HasFlag(RotationDayMask.Wednesday); set => SetDayFlag(RotationDayMask.Wednesday, value); }
	[JsonIgnore] public bool Thu { get => DaysMask.HasFlag(RotationDayMask.Thursday); set => SetDayFlag(RotationDayMask.Thursday, value); }
	[JsonIgnore] public bool Fri { get => DaysMask.HasFlag(RotationDayMask.Friday); set => SetDayFlag(RotationDayMask.Friday, value); }
	[JsonIgnore] public bool Sat { get => DaysMask.HasFlag(RotationDayMask.Saturday); set => SetDayFlag(RotationDayMask.Saturday, value); }
	[JsonIgnore] public bool Sun { get => DaysMask.HasFlag(RotationDayMask.Sunday); set => SetDayFlag(RotationDayMask.Sunday, value); }

	[JsonIgnore] public bool IsTagSource => SourceKind == RotationSourceKind.Tag;
	[JsonIgnore] public bool IsCategorySource => SourceKind == RotationSourceKind.Category;
	[JsonIgnore] public bool IsPlaysetSource => SourceKind == RotationSourceKind.Playset;

	[JsonIgnore]
	public string TimeRangeDisplay => $"{StartTime} – {EndTime}";

	[JsonIgnore]
	public string DaysSummary
	{
		get
		{
			if (DaysMask == RotationDayMask.All) return "Every Day";
			if (DaysMask == RotationDayMask.Weekdays) return "Mon–Fri";
			if (DaysMask == RotationDayMask.Weekends) return "Sat–Sun";
			if (DaysMask == RotationDayMask.None) return "No Days";
			var parts = new List<string>(7);
			if (Mon) parts.Add("Mon");
			if (Tue) parts.Add("Tue");
			if (Wed) parts.Add("Wed");
			if (Thu) parts.Add("Thu");
			if (Fri) parts.Add("Fri");
			if (Sat) parts.Add("Sat");
			if (Sun) parts.Add("Sun");
			return string.Join(", ", parts);
		}
	}

	[JsonIgnore]
	public string SourceSummary => SourceKind switch
	{
		RotationSourceKind.AllMusic => "All Music",
		RotationSourceKind.Tag => string.IsNullOrWhiteSpace(SourceValue) ? "Tag: (unset)" : $"Tag: #{SourceValue}",
		RotationSourceKind.Category => string.IsNullOrWhiteSpace(SourceValue) ? "Category: (unset)" : $"Category: {SourceValue}",
		RotationSourceKind.Playset => !string.IsNullOrWhiteSpace(_cachedPlaysetName)
			? $"Playset: {_cachedPlaysetName}"
			: (PlaysetId.HasValue ? $"Playset #{PlaysetId.Value}" : "Playset: (unset)"),
		_ => "All Music"
	};

	[JsonIgnore]
	public int CachedMatchCount
	{
		get => _cachedMatchCount;
		set
		{
			if (SetProperty(ref _cachedMatchCount, value))
			{
				OnPropertyChanged(nameof(MatchCountText));
				OnPropertyChanged(nameof(HasZeroMatches));
			}
		}
	}

	[JsonIgnore]
	public string CachedPlaysetName
	{
		get => _cachedPlaysetName;
		set
		{
			if (SetProperty(ref _cachedPlaysetName, value ?? string.Empty))
			{
				OnPropertyChanged(nameof(SourceSummary));
			}
		}
	}

	[JsonIgnore]
	public string MatchCountText => _cachedMatchCount < 0
		? ""
		: _cachedMatchCount == 0
			? "0 tracks (empty!)"
			: _cachedMatchCount == 1
				? "1 track"
				: $"{_cachedMatchCount} tracks";

	[JsonIgnore]
	public bool HasZeroMatches => _cachedMatchCount == 0;

	public static RotationDayMask FromDayOfWeek(DayOfWeek dow) => dow switch
	{
		DayOfWeek.Sunday => RotationDayMask.Sunday,
		DayOfWeek.Monday => RotationDayMask.Monday,
		DayOfWeek.Tuesday => RotationDayMask.Tuesday,
		DayOfWeek.Wednesday => RotationDayMask.Wednesday,
		DayOfWeek.Thursday => RotationDayMask.Thursday,
		DayOfWeek.Friday => RotationDayMask.Friday,
		DayOfWeek.Saturday => RotationDayMask.Saturday,
		_ => RotationDayMask.All
	};

	public RotationBlock Clone()
	{
		return new RotationBlock
		{
			Id = Id,
			Name = Name,
			StartTime = StartTime,
			EndTime = EndTime,
			DaysMask = DaysMask,
			SourceKind = SourceKind,
			SourceValue = SourceValue,
			PlaysetId = PlaysetId,
			ColorHex = ColorHex,
			Priority = Priority,
			Enabled = Enabled,
			ArtistSeparationOverrideMinutes = ArtistSeparationOverrideMinutes,
			SweeperMinSongsOverride = SweeperMinSongsOverride,
			SweeperMaxSongsOverride = SweeperMaxSongsOverride
		};
	}

	private void SetDayFlag(RotationDayMask flag, bool on)
	{
		RotationDayMask next = on ? (DaysMask | flag) : (DaysMask & ~flag);
		DaysMask = next;
	}

	private void NotifyDayProperties()
	{
		OnPropertyChanged(nameof(Mon));
		OnPropertyChanged(nameof(Tue));
		OnPropertyChanged(nameof(Wed));
		OnPropertyChanged(nameof(Thu));
		OnPropertyChanged(nameof(Fri));
		OnPropertyChanged(nameof(Sat));
		OnPropertyChanged(nameof(Sun));
		OnPropertyChanged(nameof(DaysSummary));
	}

	public event PropertyChangedEventHandler? PropertyChanged;

	private bool SetProperty<T>(ref T field, T value, [CallerMemberName] string? prop = null)
	{
		if (EqualityComparer<T>.Default.Equals(field, value)) return false;
		field = value;
		OnPropertyChanged(prop);
		return true;
	}

	private void OnPropertyChanged([CallerMemberName] string? prop = null) =>
		PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(prop));
}

public sealed record RotationCoverageReport(
	DayOfWeek Day,
	double CoveragePercent,
	int ActiveBlockCount,
	IReadOnlyList<string> GapRanges,
	IReadOnlyList<string> OverlapSummaries,
	string SummaryText);

public sealed record CuePoolDiagnostics(
	IReadOnlyList<LibraryItem> Eligible,
	int TotalFolderOrCategoryMatches,
	int TooShortCount,
	int TooLongCount,
	int MissingFileCount,
	double MinSeconds,
	double MaxSeconds,
	string SummaryText);

public enum StationIdActionKind
{
	None,
	InsertIntoQueue,
	FireHardInterruptNow,
	FireOverlayNow
}

public sealed record StationIdDecision(
	StationIdActionKind Action,
	int InsertIndex,
	DateTime TargetTimeLocal,
	DateTime WindowStartLocal,
	DateTime WindowEndLocal,
	bool AlreadyServedThisHour,
	string StatusSummary);
