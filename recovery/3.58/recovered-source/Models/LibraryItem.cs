using System;
using System.ComponentModel.DataAnnotations.Schema;
using System.IO;

namespace RadioApp.Models;

public class LibraryItem
{
	public int Id { get; set; }

	public string FilePath { get; set; } = string.Empty;

	public string Title { get; set; } = string.Empty;

	public string Artist { get; set; } = string.Empty;

	public string Album { get; set; } = string.Empty;

	public string Category { get; set; } = string.Empty;

	public string Tags { get; set; } = string.Empty;

	public TimeSpan Duration { get; set; }

	public DateTime DateAdded { get; set; } = DateTime.UtcNow;

	public DateTime? SourceModifiedAtUtc { get; set; }

	[NotMapped]
	public string DurationText => Duration.ToString(Duration.TotalHours >= 1.0 ? "h\\:mm\\:ss" : "m\\:ss");

	[NotMapped]
	public string FileName => Path.GetFileName(FilePath);

	[NotMapped]
	public string FolderPath => Path.GetDirectoryName(FilePath) ?? string.Empty;

	[NotMapped]
	public string DateAddedText => DateAdded.ToLocalTime().ToString("g");

	[NotMapped]
	public string FileStatus => File.Exists(FilePath) ? "Available" : "Missing";

	[NotMapped]
	public bool IsSweeperCue
	{
		get
		{
			if (!string.Equals(Category, "Sweeper", StringComparison.OrdinalIgnoreCase))
			{
				return string.Equals(Category, "Sweep", StringComparison.OrdinalIgnoreCase);
			}
			return true;
		}
	}

	[NotMapped]
	public bool IsStationIdCue
	{
		get
		{
			if (!string.Equals(Category, "StationId", StringComparison.OrdinalIgnoreCase) && !string.Equals(Category, "Station ID", StringComparison.OrdinalIgnoreCase))
			{
				return string.Equals(Category, "ID", StringComparison.OrdinalIgnoreCase);
			}
			return true;
		}
	}

	[NotMapped]
	public string QueueCueLabel
	{
		get
		{
			if (!IsStationIdCue)
			{
				if (!IsSweeperCue)
				{
					return string.Empty;
				}
				return "SWP";
			}
			return "ID";
		}
	}
}
