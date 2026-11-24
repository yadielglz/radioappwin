using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using RadioApp.Data;
using RadioApp.Models;

namespace RadioApp.Services;

public sealed class PlaysetService
{
	private readonly RadioDbContextFactory _dbFactory;

	public PlaysetService(RadioDbContextFactory dbFactory)
	{
		_dbFactory = dbFactory;
	}

	public List<Playset> GetAllPlaysets(IReadOnlyList<LibraryItem> allLibraryItems)
	{
		using RadioLibraryContext ctx = _dbFactory.CreateContext();
		List<Playset> playsets = ctx.Playsets
			.Include(p => p.Entries)
			.ThenInclude(e => e.LibraryItem)
			.OrderBy(p => p.Name)
			.ToList();

		foreach (Playset ps in playsets)
		{
			ps.Entries = ps.Entries.OrderBy(e => e.SortOrder).ToList();
			if (ps.Kind == PlaysetKind.SmartRule)
			{
				List<LibraryItem> matched = EvaluateSmartPlayset(ps.ParseRule(), allLibraryItems);
				ps.TrackCount = matched.Count;
				ps.TotalDuration = matched.Aggregate(TimeSpan.Zero, (acc, i) => acc + i.Duration);
			}
			else
			{
				List<LibraryItem> valid = ps.Entries.Where(e => e.LibraryItem != null).Select(e => e.LibraryItem!).ToList();
				ps.TrackCount = valid.Count;
				ps.TotalDuration = valid.Aggregate(TimeSpan.Zero, (acc, i) => acc + i.Duration);
			}
		}

		return playsets;
	}

	public List<LibraryItem> ResolvePlaysetTracks(Playset playset, IReadOnlyList<LibraryItem> allLibraryItems, bool shuffle = false)
	{
		List<LibraryItem> result;
		if (playset.Kind == PlaysetKind.SmartRule)
		{
			result = EvaluateSmartPlayset(playset.ParseRule(), allLibraryItems);
		}
		else
		{
			using RadioLibraryContext ctx = _dbFactory.CreateContext();
			List<int> ids = ctx.PlaysetEntries
				.Where(e => e.PlaysetId == playset.Id)
				.OrderBy(e => e.SortOrder)
				.Select(e => e.LibraryItemId)
				.ToList();

			var lookup = allLibraryItems.ToDictionary(i => i.Id, i => i);
			result = new List<LibraryItem>(ids.Count);
			foreach (int id in ids)
			{
				if (lookup.TryGetValue(id, out LibraryItem? item))
				{
					result.Add(item);
				}
			}
		}

		if (shuffle && result.Count > 1)
		{
			ShuffleInPlace(result);
		}
		return result;
	}

	public static List<LibraryItem> EvaluateSmartPlayset(SmartPlaysetRule rule, IEnumerable<LibraryItem> allItems)
	{
		DateTime nowUtc = DateTime.UtcNow;
		IEnumerable<LibraryItem> query = allItems;

		if (!string.IsNullOrWhiteSpace(rule.CategoryFilter) &&
			!string.Equals(rule.CategoryFilter, "All", StringComparison.OrdinalIgnoreCase) &&
			!string.Equals(rule.CategoryFilter, "All categories", StringComparison.OrdinalIgnoreCase))
		{
			string[] cats = SplitTokens(rule.CategoryFilter);
			query = query.Where(i => cats.Any(c => string.Equals(i.Category?.Trim(), c, StringComparison.OrdinalIgnoreCase)));
		}

		if (!string.IsNullOrWhiteSpace(rule.RequiredTags))
		{
			string[] req = SplitTokens(rule.RequiredTags);
			query = query.Where(i =>
			{
				string[] itemTags = SplitTokens(i.Tags);
				return req.All(r => itemTags.Any(t => string.Equals(t, r, StringComparison.OrdinalIgnoreCase)));
			});
		}

		if (!string.IsNullOrWhiteSpace(rule.AnyTags))
		{
			string[] any = SplitTokens(rule.AnyTags);
			query = query.Where(i =>
			{
				string[] itemTags = SplitTokens(i.Tags);
				return any.Any(r => itemTags.Any(t => string.Equals(t, r, StringComparison.OrdinalIgnoreCase)));
			});
		}

		if (!string.IsNullOrWhiteSpace(rule.ExcludedTags))
		{
			string[] excl = SplitTokens(rule.ExcludedTags);
			query = query.Where(i =>
			{
				string[] itemTags = SplitTokens(i.Tags);
				return !excl.Any(e => itemTags.Any(t => string.Equals(t, e, StringComparison.OrdinalIgnoreCase)));
			});
		}

		if (rule.MinBpm.HasValue)
		{
			query = query.Where(i => i.Bpm.HasValue && i.Bpm.Value >= rule.MinBpm.Value);
		}
		if (rule.MaxBpm.HasValue)
		{
			query = query.Where(i => i.Bpm.HasValue && i.Bpm.Value <= rule.MaxBpm.Value);
		}
		if (rule.MinDurationSeconds.HasValue)
		{
			query = query.Where(i => i.Duration.TotalSeconds >= rule.MinDurationSeconds.Value);
		}
		if (rule.MaxDurationSeconds.HasValue)
		{
			query = query.Where(i => i.Duration.TotalSeconds <= rule.MaxDurationSeconds.Value);
		}
		if (!string.IsNullOrWhiteSpace(rule.FolderContains))
		{
			query = query.Where(i => i.FilePath.Contains(rule.FolderContains.Trim(), StringComparison.OrdinalIgnoreCase));
		}
		if (rule.OnlyNeverPlayed)
		{
			query = query.Where(i => i.PlayCount == 0 && !i.LastPlayedAtUtc.HasValue);
		}
		if (rule.NotPlayedWithinHours.HasValue && rule.NotPlayedWithinHours.Value > 0)
		{
			TimeSpan minAge = TimeSpan.FromHours(rule.NotPlayedWithinHours.Value);
			query = query.Where(i => !i.LastPlayedAtUtc.HasValue || (nowUtc - i.LastPlayedAtUtc.Value) >= minAge);
		}

		List<LibraryItem> list = query.ToList();
		list = (rule.SortBy ?? "Title") switch
		{
			"Artist" => list.OrderBy(i => i.Artist).ThenBy(i => i.Title).ToList(),
			"Bpm" => list.OrderBy(i => i.Bpm ?? 999.0).ThenBy(i => i.Title).ToList(),
			"DateAddedDesc" => list.OrderByDescending(i => i.DateAdded).ToList(),
			"LeastPlayed" => list.OrderBy(i => i.PlayCount).ThenBy(i => i.LastPlayedAtUtc ?? DateTime.MinValue).ToList(),
			"Random" => ShuffleCopy(list),
			_ => list.OrderBy(i => i.Title).ToList()
		};

		if (rule.MaxTracks > 0 && list.Count > rule.MaxTracks)
		{
			list = list.Take(rule.MaxTracks).ToList();
		}

		return list;
	}

	public Playset SavePlayset(Playset playset)
	{
		using RadioLibraryContext ctx = _dbFactory.CreateContext();
		playset.UpdatedAtUtc = DateTime.UtcNow;
		if (playset.Id <= 0)
		{
			playset.CreatedAtUtc = DateTime.UtcNow;
			var entity = new Playset
			{
				Name = playset.Name.Trim(),
				Description = playset.Description.Trim(),
				Kind = playset.Kind,
				AccentColor = string.IsNullOrWhiteSpace(playset.AccentColor) ? "#0284C7" : playset.AccentColor,
				TransitionModeOverride = playset.TransitionModeOverride,
				RuleJson = playset.RuleJson,
				CreatedAtUtc = playset.CreatedAtUtc,
				UpdatedAtUtc = playset.UpdatedAtUtc
			};
			ctx.Playsets.Add(entity);
			ctx.SaveChanges();
			playset.Id = entity.Id;
		}
		else
		{
			Playset? existing = ctx.Playsets.Find(playset.Id);
			if (existing != null)
			{
				existing.Name = playset.Name.Trim();
				existing.Description = playset.Description.Trim();
				existing.Kind = playset.Kind;
				existing.AccentColor = string.IsNullOrWhiteSpace(playset.AccentColor) ? "#0284C7" : playset.AccentColor;
				existing.TransitionModeOverride = playset.TransitionModeOverride;
				existing.RuleJson = playset.RuleJson;
				existing.UpdatedAtUtc = playset.UpdatedAtUtc;
				ctx.SaveChanges();
			}
		}
		return playset;
	}

	public void DeletePlayset(int playsetId)
	{
		using RadioLibraryContext ctx = _dbFactory.CreateContext();
		Playset? ps = ctx.Playsets.Find(playsetId);
		if (ps != null)
		{
			ctx.Playsets.Remove(ps);
			ctx.SaveChanges();
		}
	}

	public void AddTracksToPlayset(int playsetId, IEnumerable<int> libraryItemIds)
	{
		using RadioLibraryContext ctx = _dbFactory.CreateContext();
		Playset? ps = ctx.Playsets.Find(playsetId);
		if (ps == null || ps.Kind != PlaysetKind.Manual)
		{
			return;
		}
		int nextOrder = ctx.PlaysetEntries.Where(e => e.PlaysetId == playsetId).Select(e => (int?)e.SortOrder).Max() ?? -1;
		foreach (int itemId in libraryItemIds)
		{
			if (itemId <= 0) continue;
			nextOrder++;
			ctx.PlaysetEntries.Add(new PlaysetEntry
			{
				PlaysetId = playsetId,
				LibraryItemId = itemId,
				SortOrder = nextOrder
			});
		}
		ps.UpdatedAtUtc = DateTime.UtcNow;
		ctx.SaveChanges();
	}

	public void ReplacePlaysetTracks(int playsetId, IEnumerable<int> orderedLibraryItemIds)
	{
		using RadioLibraryContext ctx = _dbFactory.CreateContext();
		Playset? ps = ctx.Playsets.Find(playsetId);
		if (ps == null || ps.Kind != PlaysetKind.Manual)
		{
			return;
		}
		var existing = ctx.PlaysetEntries.Where(e => e.PlaysetId == playsetId).ToList();
		ctx.PlaysetEntries.RemoveRange(existing);
		int order = 0;
		foreach (int itemId in orderedLibraryItemIds)
		{
			if (itemId <= 0) continue;
			ctx.PlaysetEntries.Add(new PlaysetEntry
			{
				PlaysetId = playsetId,
				LibraryItemId = itemId,
				SortOrder = order++
			});
		}
		ps.UpdatedAtUtc = DateTime.UtcNow;
		ctx.SaveChanges();
	}

	public void SavePersistedQueue(IEnumerable<LibraryItem> queue, ISet<LibraryItem>? autoDjItems = null)
	{
		try
		{
			using RadioLibraryContext ctx = _dbFactory.CreateContext();
			ctx.PersistedQueueItems.RemoveRange(ctx.PersistedQueueItems);
			int order = 0;
			foreach (LibraryItem item in queue)
			{
				ctx.PersistedQueueItems.Add(new PersistedQueueItem
				{
					SortOrder = order++,
					LibraryItemId = item.Id > 0 ? item.Id : null,
					FilePath = item.FilePath,
					Title = item.Title,
					Artist = item.Artist,
					Album = item.Album,
					Category = item.Category,
					DurationTicks = item.Duration.Ticks,
					IsAutoDj = autoDjItems != null && autoDjItems.Contains(item)
				});
			}
			ctx.SaveChanges();
		}
		catch (Exception ex)
		{
			LogService.Warn("QueuePersistence", $"Failed to persist queue: {ex.Message}");
		}
	}

	public List<(LibraryItem Item, bool IsAutoDj)> LoadPersistedQueue(IReadOnlyList<LibraryItem> libraryItems)
	{
		var results = new List<(LibraryItem, bool)>();
		try
		{
			using RadioLibraryContext ctx = _dbFactory.CreateContext();
			List<PersistedQueueItem> rows = ctx.PersistedQueueItems.OrderBy(q => q.SortOrder).ToList();
			var byId = libraryItems.Where(i => i.Id > 0).ToDictionary(i => i.Id, i => i);
			var byPath = new Dictionary<string, LibraryItem>(StringComparer.OrdinalIgnoreCase);
			foreach (LibraryItem item in libraryItems)
			{
				if (!string.IsNullOrWhiteSpace(item.FilePath) && !byPath.ContainsKey(item.FilePath))
				{
					byPath[item.FilePath] = item;
				}
			}

			foreach (PersistedQueueItem row in rows)
			{
				LibraryItem? resolved = null;
				if (row.LibraryItemId.HasValue && byId.TryGetValue(row.LibraryItemId.Value, out LibraryItem? matchById))
				{
					resolved = matchById;
				}
				else if (!string.IsNullOrWhiteSpace(row.FilePath) && byPath.TryGetValue(row.FilePath, out LibraryItem? matchByPath))
				{
					resolved = matchByPath;
				}
				else if (!string.IsNullOrWhiteSpace(row.FilePath) && File.Exists(row.FilePath))
				{
					resolved = new LibraryItem
					{
						FilePath = row.FilePath,
						Title = row.Title,
						Artist = row.Artist,
						Album = row.Album,
						Category = row.Category,
						Duration = TimeSpan.FromTicks(row.DurationTicks)
					};
				}

				if (resolved != null)
				{
					results.Add((resolved, row.IsAutoDj));
				}
			}
		}
		catch (Exception ex)
		{
			LogService.Warn("QueuePersistence", $"Failed to load persisted queue: {ex.Message}");
		}
		return results;
	}

	public static void ExportM3u8(string targetFilePath, string playsetName, IEnumerable<LibraryItem> tracks)
	{
		var sb = new StringBuilder();
		sb.AppendLine("#EXTM3U");
		sb.AppendLine($"#PLAYLIST:{playsetName}");
		foreach (LibraryItem item in tracks)
		{
			int seconds = Math.Max(0, (int)Math.Round(item.Duration.TotalSeconds));
			string display = string.IsNullOrWhiteSpace(item.Artist) ? item.Title : $"{item.Artist} - {item.Title}";
			sb.AppendLine($"#EXTINF:{seconds},{display}");
			sb.AppendLine(item.FilePath);
		}
		File.WriteAllText(targetFilePath, sb.ToString(), Encoding.UTF8);
	}

	public static List<string> ParsePlaylistFile(string playlistPath)
	{
		var paths = new List<string>();
		if (!File.Exists(playlistPath))
		{
			return paths;
		}

		string baseDir = Path.GetDirectoryName(playlistPath) ?? string.Empty;
		string ext = Path.GetExtension(playlistPath).ToLowerInvariant();
		string[] lines = File.ReadAllLines(playlistPath);

		if (ext == ".pls")
		{
			foreach (string raw in lines)
			{
				string line = raw.Trim();
				if (line.StartsWith("File", StringComparison.OrdinalIgnoreCase))
				{
					int eq = line.IndexOf('=');
					if (eq > 0 && eq + 1 < line.Length)
					{
						string candidate = ResolveRelative(baseDir, line.Substring(eq + 1).Trim());
						if (!string.IsNullOrWhiteSpace(candidate)) paths.Add(candidate);
					}
				}
			}
		}
		else if (ext == ".json")
		{
			try
			{
				using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(playlistPath));
				if (doc.RootElement.TryGetProperty("tracks", out JsonElement arr) && arr.ValueKind == JsonValueKind.Array)
				{
					foreach (JsonElement el in arr.EnumerateArray())
					{
						string? p = el.ValueKind == JsonValueKind.String ? el.GetString() :
							(el.TryGetProperty("filePath", out JsonElement fp) ? fp.GetString() : null);
						if (!string.IsNullOrWhiteSpace(p)) paths.Add(ResolveRelative(baseDir, p));
					}
				}
			}
			catch
			{
			}
		}
		else // .m3u / .m3u8
		{
			foreach (string raw in lines)
			{
				string line = raw.Trim();
				if (string.IsNullOrWhiteSpace(line) || line.StartsWith("#", StringComparison.Ordinal))
				{
					continue;
				}
				paths.Add(ResolveRelative(baseDir, line));
			}
		}

		return paths;
	}

	private static string ResolveRelative(string baseDir, string candidate)
	{
		try
		{
			return Path.IsPathRooted(candidate) ? Path.GetFullPath(candidate) : Path.GetFullPath(Path.Combine(baseDir, candidate));
		}
		catch
		{
			return candidate;
		}
	}

	private static string[] SplitTokens(string? raw)
	{
		if (string.IsNullOrWhiteSpace(raw)) return Array.Empty<string>();
		return raw.Split(new[] { ',', ';', '|' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
	}

	private static void ShuffleInPlace<T>(IList<T> list)
	{
		for (int i = list.Count - 1; i > 0; i--)
		{
			int j = Random.Shared.Next(i + 1);
			(list[i], list[j]) = (list[j], list[i]);
		}
	}

	private static List<T> ShuffleCopy<T>(List<T> source)
	{
		var copy = new List<T>(source);
		ShuffleInPlace(copy);
		return copy;
	}
}
