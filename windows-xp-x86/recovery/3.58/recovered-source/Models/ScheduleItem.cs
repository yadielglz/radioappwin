using System;

namespace RadioApp.Models;

public class ScheduleItem
{
	public int Id { get; set; }

	public string Title { get; set; } = string.Empty;

	public ScheduleEventType EventType { get; set; }

	public string FilePath { get; set; } = string.Empty;

	public DateTime ScheduledAt { get; set; }

	public TimeSpan? RepeatInterval { get; set; }

	public ScheduleRepeatMode RepeatMode { get; set; }

	public int ToleranceBeforeMinutes { get; set; }

	public int ToleranceAfterMinutes { get; set; } = 2;

	public bool IsMain { get; set; }

	public bool Enabled { get; set; } = true;

	public DeckId TargetDeck { get; set; }

	public bool IsFolderSource { get; set; }

	public int BlockDurationMinutes { get; set; } = 60;
}
