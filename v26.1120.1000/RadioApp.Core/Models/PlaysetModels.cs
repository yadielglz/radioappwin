using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations.Schema;
using System.Text.Json;

namespace RadioApp.Models;

public class Playset
{
	public int Id { get; set; }

	public string Name { get; set; } = string.Empty;

	public string Description { get; set; } = string.Empty;

	public PlaysetKind Kind { get; set; } = PlaysetKind.Manual;

	public string AccentColor { get; set; } = "#0284C7";

	public TransitionMode? TransitionModeOverride { get; set; }

	public string RuleJson { get; set; } = string.Empty;

	public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;

	public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;

	public List<PlaysetEntry> Entries { get; set; } = new List<PlaysetEntry>();

	[NotMapped]
	public int TrackCount { get; set; }

	[NotMapped]
	public TimeSpan TotalDuration { get; set; }

	[NotMapped]
	public string KindLabel => Kind == PlaysetKind.SmartRule ? "SMART" : "SET";

	[NotMapped]
	public string SummaryText
	{
		get
		{
			string dur = TotalDuration.TotalHours >= 1.0
				? TotalDuration.ToString("h\\:mm\\:ss")
				: TotalDuration.ToString("m\\:ss");
			return $"{TrackCount} tracks • {dur}";
		}
	}

	public SmartPlaysetRule ParseRule()
	{
		if (string.IsNullOrWhiteSpace(RuleJson))
		{
			return new SmartPlaysetRule();
		}
		try
		{
			return JsonSerializer.Deserialize<SmartPlaysetRule>(RuleJson) ?? new SmartPlaysetRule();
		}
		catch
		{
			return new SmartPlaysetRule();
		}
	}

	public void SetRule(SmartPlaysetRule rule)
	{
		RuleJson = JsonSerializer.Serialize(rule);
		UpdatedAtUtc = DateTime.UtcNow;
	}
}

public class PlaysetEntry
{
	public int Id { get; set; }

	public int PlaysetId { get; set; }

	public int LibraryItemId { get; set; }

	public int SortOrder { get; set; }

	public int? CustomTransitionMs { get; set; }

	public string Notes { get; set; } = string.Empty;

	public Playset? Playset { get; set; }

	public LibraryItem? LibraryItem { get; set; }
}

public class SmartPlaysetRule
{
	public string CategoryFilter { get; set; } = string.Empty;

	public string RequiredTags { get; set; } = string.Empty;

	public string AnyTags { get; set; } = string.Empty;

	public string ExcludedTags { get; set; } = string.Empty;

	public double? MinBpm { get; set; }

	public double? MaxBpm { get; set; }

	public int? MinDurationSeconds { get; set; }

	public int? MaxDurationSeconds { get; set; }

	public string FolderContains { get; set; } = string.Empty;

	public bool OnlyNeverPlayed { get; set; }

	public int? NotPlayedWithinHours { get; set; }

	public string SortBy { get; set; } = "Title";

	public int MaxTracks { get; set; } = 200;
}

public class PersistedQueueItem
{
	public int Id { get; set; }

	public int SortOrder { get; set; }

	public int? LibraryItemId { get; set; }

	public string FilePath { get; set; } = string.Empty;

	public string Title { get; set; } = string.Empty;

	public string Artist { get; set; } = string.Empty;

	public string Album { get; set; } = string.Empty;

	public string Category { get; set; } = "Music";

	public long DurationTicks { get; set; }

	public bool IsAutoDj { get; set; }

	public int? SourcePlaysetId { get; set; }
}

public class CartPadConfig
{
	public int SlotIndex { get; set; }

	public string Label { get; set; } = string.Empty;

	public string FilePath { get; set; } = string.Empty;

	public string ColorHex { get; set; } = "#8B5CF6";

	public bool DuckMusic { get; set; } = true;

	public double DuckDb { get; set; } = -9.0;

	public double Volume { get; set; } = 1.0;

	public string Hotkey => $"F{SlotIndex + 1}";

	public static List<CartPadConfig> CreateDefaults()
	{
		string[] labels = { "STATION ID", "SWEEPER 1", "SWEEPER 2", "JINGLE DROP", "PROMO SPOT", "NEWS INTRO", "FX STINGER", "OUTRO BED" };
		string[] colors = { "#0284C7", "#8B5CF6", "#7C3AED", "#EC4899", "#F59E0B", "#10B981", "#06B6D4", "#EF4444" };
		var list = new List<CartPadConfig>(8);
		for (int i = 0; i < 8; i++)
		{
			list.Add(new CartPadConfig
			{
				SlotIndex = i,
				Label = labels[i],
				ColorHex = colors[i],
				DuckMusic = true,
				DuckDb = -9.0,
				Volume = 1.0
			});
		}
		return list;
	}
}

public class HourlyTagRuleItem
{
	public string StartTime { get; set; } = "06:00";

	public string EndTime { get; set; } = "12:00";

	public string Tag { get; set; } = string.Empty;
}
