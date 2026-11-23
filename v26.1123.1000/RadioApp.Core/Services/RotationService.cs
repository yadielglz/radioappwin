using System;
using System.Collections.Generic;
using System.Linq;
using RadioApp.Models;

namespace RadioApp.Services;

public sealed record ActiveRotationInfo(string Tag, string Description);

/// <summary>
/// Broadcast anti-repeat rotation engine, hourly tag rule parser/serializer,
/// and Top-of-Hour sync helper.
/// </summary>
public sealed class RotationService
{
	private readonly Random _random;
	private readonly Dictionary<string, List<int>> _rotationDecks = new Dictionary<string, List<int>>(StringComparer.OrdinalIgnoreCase);
	private readonly Queue<int> _recentPlayedItemIds = new Queue<int>();
	private readonly Queue<(string Artist, DateTime PlayedAtUtc)> _recentArtists = new Queue<(string Artist, DateTime PlayedAtUtc)>();
	private readonly Queue<(string Title, DateTime PlayedAtUtc)> _recentTitles = new Queue<(string Title, DateTime PlayedAtUtc)>();

	public RotationService(int? seed = null)
	{
		_random = seed.HasValue ? new Random(seed.Value) : new Random();
	}

	public void ResetDecks()
	{
		_rotationDecks.Clear();
	}

	public void RecordPlayed(LibraryItem item, DateTime? utcNow = null)
	{
		DateTime now = utcNow ?? DateTime.UtcNow;
		if (item.Id > 0)
		{
			_recentPlayedItemIds.Enqueue(item.Id);
			while (_recentPlayedItemIds.Count > 250) _recentPlayedItemIds.Dequeue();
		}
		if (!string.IsNullOrWhiteSpace(item.Artist))
		{
			_recentArtists.Enqueue((item.Artist.Trim(), now));
			while (_recentArtists.Count > 60) _recentArtists.Dequeue();
		}
		if (!string.IsNullOrWhiteSpace(item.Title))
		{
			_recentTitles.Enqueue((item.Title.Trim(), now));
			while (_recentTitles.Count > 60) _recentTitles.Dequeue();
		}
	}

	public LibraryItem? PickNextTrack(
		IReadOnlyList<LibraryItem> library,
		AppSettings settings,
		IEnumerable<string>? preferredCategories = null,
		string? tag = null,
		IEnumerable<LibraryItem>? currentlyQueued = null,
		DateTime? utcNow = null)
	{
		List<LibraryItem> candidates;
		string poolKey;

		if (!string.IsNullOrWhiteSpace(tag))
		{
			string normalizedTag = tag.Trim().ToLowerInvariant();
			poolKey = "tag:" + normalizedTag;
			candidates = library.Where(item =>
				IsMusicCandidate(item) &&
				item.Tags.Split(new[] { ',', ';', '|' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
					.Any(t => string.Equals(t, normalizedTag, StringComparison.OrdinalIgnoreCase))).ToList();

			if (candidates.Count == 0)
			{
				poolKey = "cat:music_fallback";
				candidates = library.Where(IsMusicCandidate).ToList();
			}
		}
		else if (preferredCategories != null && preferredCategories.Any())
		{
			string[] normalized = preferredCategories
				.Where(c => !string.IsNullOrWhiteSpace(c))
				.Select(c => c.Trim().ToLowerInvariant())
				.ToArray();
			poolKey = "cat:" + string.Join(",", normalized.OrderBy(c => c));
			candidates = library.Where(item =>
				normalized.Contains((item.Category ?? string.Empty).Trim().ToLowerInvariant())).ToList();
		}
		else
		{
			poolKey = "cat:music_default";
			candidates = library.Where(IsMusicCandidate).ToList();
		}

		if (candidates.Count == 0) return null;

		// Prefer cached-available files without blocking the UI thread on File.Exists!
		List<LibraryItem> available = candidates.Where(c => c.FileExistsCached).ToList();
		if (available.Count > 0) candidates = available;
		if (candidates.Count == 1) return candidates[0];

		var queuedIds = new HashSet<int>();
		var queuedArtists = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		var queuedTitles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

		if (currentlyQueued != null)
		{
			foreach (LibraryItem q in currentlyQueued)
			{
				if (q.Id > 0) queuedIds.Add(q.Id);
				if (!string.IsNullOrWhiteSpace(q.Artist)) queuedArtists.Add(q.Artist.Trim());
				if (!string.IsNullOrWhiteSpace(q.Title)) queuedTitles.Add(q.Title.Trim());
			}
		}

		DateTime now = utcNow ?? DateTime.UtcNow;
		int trackSepCount = Math.Max(1, (int)(candidates.Count * 0.75));
		TimeSpan trackSepTime = TimeSpan.FromHours(Math.Max(0, settings.AutoDjTrackSeparationHours));
		TimeSpan artistSepTime = TimeSpan.FromMinutes(Math.Max(0, settings.AutoDjArtistSeparationMinutes));

		int distinctArtists = candidates
			.Select(c => c.Artist)
			.Where(a => !string.IsNullOrWhiteSpace(a))
			.Distinct(StringComparer.OrdinalIgnoreCase)
			.Count();
		bool enforceArtistSep = settings.AutoDjArtistSeparationMinutes > 0 && distinctArtists > 2;

		if (!_rotationDecks.TryGetValue(poolKey, out List<int>? deck))
		{
			deck = new List<int>();
			_rotationDecks[poolKey] = deck;
		}

		var candidateIds = new HashSet<int>(candidates.Select(c => c.Id));
		deck.RemoveAll(id => !candidateIds.Contains(id));
		if (deck.Count == 0)
		{
			deck.AddRange(candidates.Select(c => c.Id));
			Shuffle(deck);
		}

		var map = new Dictionary<int, LibraryItem>(candidates.Count);
		foreach (LibraryItem c in candidates)
		{
			if (!map.ContainsKey(c.Id)) map[c.Id] = c;
		}

		// Pass 1: Strict separation (track + title + artist)
		for (int i = 0; i < deck.Count; i++)
		{
			if (!map.TryGetValue(deck[i], out LibraryItem? item)) continue;
			if (queuedIds.Contains(item.Id)) continue;
			if (queuedTitles.Contains(item.Title.Trim())) continue;
			if (enforceArtistSep && !string.IsNullOrWhiteSpace(item.Artist) && queuedArtists.Contains(item.Artist.Trim())) continue;
			if (IsTrackRecent(item, trackSepCount, trackSepTime, now)) continue;
			if (enforceArtistSep && IsArtistRecent(item.Artist, artistSepTime, now)) continue;

			deck.RemoveAt(i);
			return item;
		}

		// Pass 2: Relax artist separation & halve track cooldown
		for (int i = 0; i < deck.Count; i++)
		{
			if (!map.TryGetValue(deck[i], out LibraryItem? item)) continue;
			if (queuedIds.Contains(item.Id) || queuedTitles.Contains(item.Title.Trim())) continue;
			if (IsTrackRecent(item, Math.Max(1, trackSepCount / 2), TimeSpan.FromMinutes(trackSepTime.TotalMinutes / 2.0), now)) continue;

			deck.RemoveAt(i);
			return item;
		}

		// Pass 3: Exhaustion reset — pick least-recently-played not in queue
		deck.Clear();
		deck.AddRange(candidates.Select(c => c.Id));
		Shuffle(deck);

		LibraryItem? fallback = candidates
			.Where(c => !queuedIds.Contains(c.Id))
			.OrderBy(c => c.LastPlayedAtUtc ?? DateTime.MinValue)
			.ThenBy(c => c.PlayCount)
			.FirstOrDefault() ?? candidates[0];

		deck.Remove(fallback.Id);
		return fallback;
	}

	public static bool IsMusicCandidate(LibraryItem item)
	{
		return string.Equals(item.Category, "Music", StringComparison.OrdinalIgnoreCase) ||
			   string.Equals(item.Category, "Song", StringComparison.OrdinalIgnoreCase) ||
			   string.Equals(item.Category, "Uncategorized", StringComparison.OrdinalIgnoreCase) ||
			   string.IsNullOrWhiteSpace(item.Category);
	}

	public static List<HourlyTagRuleItem> ParseHourlyTagRules(string? raw)
	{
		var list = new List<HourlyTagRuleItem>();
		if (string.IsNullOrWhiteSpace(raw)) return list;

		string[] lines = raw.Split(new[] { "\r\n", "\n", ";" }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
		foreach (string line in lines)
		{
			string[] parts = line.Split('=', 2, StringSplitOptions.TrimEntries);
			if (parts.Length != 2 || string.IsNullOrWhiteSpace(parts[1])) continue;
			string[] range = parts[0].Split('-', 2, StringSplitOptions.TrimEntries);
			if (range.Length != 2) continue;
			if (TryParseRuleTime(range[0], out TimeSpan s) && TryParseRuleTime(range[1], out TimeSpan e))
			{
				list.Add(new HourlyTagRuleItem
				{
					StartTime = $"{(int)s.TotalHours:00}:{s.Minutes:00}",
					EndTime = $"{(int)e.TotalHours:00}:{e.Minutes:00}",
					Tag = parts[1].Trim()
				});
			}
		}
		return list;
	}

	public static string SerializeHourlyTagRules(IEnumerable<HourlyTagRuleItem> rules)
	{
		return string.Join("; ", rules
			.Where(r => !string.IsNullOrWhiteSpace(r.Tag))
			.Select(r => $"{r.StartTime.Trim()}-{r.EndTime.Trim()}={r.Tag.Trim()}"));
	}

	public static ActiveRotationInfo GetActiveRotation(string? hourlyRulesRaw, string? defaultTag, TimeSpan localTimeOfDay)
	{
		foreach (HourlyTagRuleItem rule in ParseHourlyTagRules(hourlyRulesRaw))
		{
			if (TryParseRuleTime(rule.StartTime, out TimeSpan start) &&
				TryParseRuleTime(rule.EndTime, out TimeSpan end) &&
				IsTimeInRule(localTimeOfDay, start, end))
			{
				return new ActiveRotationInfo(rule.Tag, $"Rotation: {rule.Tag} ({rule.StartTime}-{rule.EndTime})");
			}
		}

		string cleanDefault = (defaultTag ?? string.Empty).Trim();
		if (!string.IsNullOrWhiteSpace(cleanDefault))
		{
			return new ActiveRotationInfo(cleanDefault, $"Rotation: {cleanDefault} (default)");
		}
		return new ActiveRotationInfo(string.Empty, "Rotation: unrestricted");
	}

	/// <summary>
	/// Computes the next Top-of-Hour target timestamp (e.g. minute :00) and whether a Top-of-Hour sync
	/// trigger window has been entered.
	/// </summary>
	public static DateTime GetNextTopOfHourTarget(DateTime localNow, int targetMinute)
	{
		int min = Math.Clamp(targetMinute, 0, 59);
		DateTime candidate = new DateTime(localNow.Year, localNow.Month, localNow.Day, localNow.Hour, min, 0);
		if (candidate <= localNow)
		{
			candidate = candidate.AddHours(1);
		}
		return candidate;
	}

	public static bool TryParseRuleTime(string text, out TimeSpan value)
	{
		text = text.Trim();
		if (int.TryParse(text, out int hour) && hour >= 0 && hour <= 24)
		{
			value = TimeSpan.FromHours(hour == 24 ? 0 : hour);
			return true;
		}
		return TimeSpan.TryParse(text, out value);
	}

	private static bool IsTimeInRule(TimeSpan now, TimeSpan start, TimeSpan end)
	{
		if (end > start) return now >= start && now < end;
		return now >= start || now < end;
	}

	private bool IsTrackRecent(LibraryItem item, int maxRecentCount, TimeSpan minTime, DateTime now)
	{
		if (item.Id > 0 && _recentPlayedItemIds.Reverse().Take(maxRecentCount).Contains(item.Id)) return true;
		if (minTime > TimeSpan.Zero && item.LastPlayedAtUtc.HasValue && (now - item.LastPlayedAtUtc.Value) < minTime) return true;
		if (minTime > TimeSpan.Zero && !string.IsNullOrWhiteSpace(item.Title))
		{
			string t = item.Title.Trim();
			if (_recentTitles.Any(x => string.Equals(x.Title, t, StringComparison.OrdinalIgnoreCase) && (now - x.PlayedAtUtc) < minTime))
			{
				return true;
			}
		}
		return false;
	}

	private bool IsArtistRecent(string? artist, TimeSpan minTime, DateTime now)
	{
		if (string.IsNullOrWhiteSpace(artist) || minTime <= TimeSpan.Zero) return false;
		string a = artist.Trim();
		return _recentArtists.Any(x => string.Equals(x.Artist, a, StringComparison.OrdinalIgnoreCase) && (now - x.PlayedAtUtc) < minTime);
	}

	private void Shuffle<T>(IList<T> list)
	{
		for (int i = list.Count - 1; i > 0; i--)
		{
			int j = _random.Next(i + 1);
			(list[i], list[j]) = (list[j], list[i]);
		}
	}
}
