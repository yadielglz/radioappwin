using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using RadioApp.Models;

namespace RadioApp.Services;

/// <summary>
/// Deterministic Sweeper &amp; Station ID threshold evaluator and broadcast enforcement engine.
/// Evaluates duration thresholds, folder/category pool health, cooldowns, adjacent-imaging
/// protection, intro-overlay eligibility, and strict Station ID window/deadline compliance.
/// </summary>
public static class ImagingPolicyService
{
	public static string[] ParseCategoryList(string? csv, params string[] defaults)
	{
		if (string.IsNullOrWhiteSpace(csv)) return defaults;
		string[] parts = csv.Split(new[] { ',', ';', '|' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
		return parts.Length > 0 ? parts : defaults;
	}

	public static bool MatchesFolderOrCategory(LibraryItem item, string folderPath, string[] categories)
	{
		if (!string.IsNullOrWhiteSpace(folderPath))
		{
			string normFolder = NormalizeFolder(folderPath);
			string normItem = NormalizePathSeparators(item.FilePath ?? string.Empty);
			if (!string.IsNullOrEmpty(normFolder) &&
				normItem.StartsWith(normFolder, StringComparison.OrdinalIgnoreCase))
			{
				return true;
			}
		}

		string cat = (item.Category ?? string.Empty).Trim();
		if (!string.IsNullOrEmpty(cat) && categories.Any(c => string.Equals(c, cat, StringComparison.OrdinalIgnoreCase)))
		{
			return true;
		}
		return false;
	}

	/// <summary>
	/// Audits the library against the configured Sweeper or Station ID folder, category, and min/max duration thresholds.
	/// </summary>
	public static CuePoolDiagnostics EvaluateCuePool(
		IReadOnlyList<LibraryItem> library,
		string folderPath,
		string categoryCsv,
		double minSeconds,
		double maxSeconds,
		bool isStationId)
	{
		double minSec = Math.Clamp(minSeconds, 0.2, 120.0);
		double maxSec = Math.Clamp(Math.Max(minSec, maxSeconds), minSec, 180.0);
		string[] categories = isStationId
			? ParseCategoryList(categoryCsv, "StationId", "Station ID", "ID")
			: ParseCategoryList(categoryCsv, "Sweeper", "Sweep", "Jingle");

		var eligible = new List<LibraryItem>();
		int totalMatches = 0;
		int tooShort = 0;
		int tooLong = 0;
		int missing = 0;

		foreach (LibraryItem item in library)
		{
			if (!MatchesFolderOrCategory(item, folderPath, categories)) continue;
			totalMatches++;

			if (!item.FileExistsCached)
			{
				missing++;
				continue;
			}

			double dur = item.Duration.TotalSeconds;
			if (dur > 0 && dur < minSec)
			{
				tooShort++;
				continue;
			}
			if (dur > maxSec)
			{
				tooLong++;
				continue;
			}

			eligible.Add(item);
		}

		string label = isStationId ? "Station ID" : "Sweeper";
		string summary;
		if (totalMatches == 0)
		{
			summary = string.IsNullOrWhiteSpace(folderPath)
				? $"0 {label}s in library (pick a folder or tag tracks as '{categories[0]}')."
				: $"0 {label}s indexed in library (will scan folder on demand).";
		}
		else if (eligible.Count == 0)
		{
			summary = $"0 / {totalMatches} {label}s pass {minSec:0.0}s–{maxSec:0.0}s threshold ({tooShort} too short, {tooLong} too long, {missing} missing).";
		}
		else
		{
			string filtered = (tooShort + tooLong + missing) > 0
				? $" • filtered: {tooShort} short, {tooLong} long" + (missing > 0 ? $", {missing} missing" : "")
				: " • all files healthy";
			summary = $"{eligible.Count} {label}(s) ready ({minSec:0.0}s–{maxSec:0.0}s){filtered}";
		}

		return new CuePoolDiagnostics(eligible, totalMatches, tooShort, tooLong, missing, minSec, maxSec, summary);
	}

	/// <summary>
	/// Single source of truth for whether a Sweeper should be triggered right now.
	/// Enforces song count threshold, minimum time cooldown, and adjacent-imaging spacing.
	/// </summary>
	public static bool ShouldTriggerSweeper(
		int songsSinceLastSweeper,
		int targetSongInterval,
		DateTime utcNow,
		DateTime? lastSweeperPlayedUtc,
		AppSettings settings,
		LibraryItem? currentItem,
		LibraryItem? nextQueuedItem,
		bool stationIdDueSoon)
	{
		if (!settings.SweepersEnabled) return false;
		if (songsSinceLastSweeper < Math.Max(1, targetSongInterval)) return false;

		// Enforce minimum time cooldown between sweepers (e.g. don't fire every 2 minutes on short songs)
		if (settings.SweeperMinCooldownMinutes > 0 && lastSweeperPlayedUtc.HasValue)
		{
			double elapsedMin = (utcNow - lastSweeperPlayedUtc.Value).TotalMinutes;
			if (elapsedMin < settings.SweeperMinCooldownMinutes)
			{
				return false;
			}
		}

		// Adjacent imaging guard: never stack Sweepers back-to-back with IDs, Commercials, or other Sweepers
		if (settings.SweeperAvoidAdjacentImaging)
		{
			if (IsImagingOrCommercial(currentItem, settings)) return false;
			if (IsImagingOrCommercial(nextQueuedItem, settings)) return false;
			if (stationIdDueSoon) return false;
		}

		return true;
	}

	/// <summary>
	/// Determines whether a sweeper should overlay over the incoming track's intro (ducking the music)
	/// rather than playing standalone between tracks.
	/// </summary>
	public static bool ShouldPlaySweeperAsOverlay(LibraryItem sweeper, LibraryItem? incomingTrack, AppSettings settings)
	{
		if (incomingTrack == null || IsImagingOrCommercial(incomingTrack, settings))
		{
			return false;
		}

		switch (settings.SweeperPlaybackMode)
		{
			case SweeperPlaybackMode.OverlayOverIntro:
				return true;

			case SweeperPlaybackMode.SmartAuto:
				double sweeperDur = sweeper.Duration.TotalSeconds;
				double introCue = (incomingTrack.IntroCueMs ?? 0) / 1000.0;
				// Overlay cleanly only when the incoming song has a known instrumental intro longer than the sweeper
				return introCue >= 3.0 && sweeperDur > 0 && introCue >= (sweeperDur + 0.5);

			case SweeperPlaybackMode.QueueBetweenTracks:
			default:
				return false;
		}
	}

	/// <summary>
	/// Evaluates the Station ID window and enforcement policy (QueueInWindow, HardInterruptAtDeadline, OverlayAtTarget).
	/// Guarantees deterministic compliance so legal/top-of-hour IDs are never silently skipped.
	/// </summary>
	public static StationIdDecision EvaluateStationId(
		DateTime localNow,
		DateTime? lastServedHourBucket,
		AppSettings settings,
		TimeSpan currentTrackRemaining,
		IReadOnlyList<LibraryItem> queue,
		Func<LibraryItem, bool> isRadioIdPredicate)
	{
		int targetMin = Math.Clamp(settings.RadioIdMinute, 0, 59);
		int beforeMin = Math.Clamp(settings.RadioIdToleranceBeforeMinutes, 0, 30);
		int afterMin = Math.Clamp(settings.RadioIdToleranceAfterMinutes, 0, 30);

		DateTime target = new DateTime(localNow.Year, localNow.Month, localNow.Day, localNow.Hour, targetMin, 0);
		DateTime winStart = target.AddMinutes(-beforeMin);
		DateTime winEnd = target.AddMinutes(afterMin);

		// If the current hour's window has completely passed (or was already served), advance to next hour
		DateTime currentBucket = new DateTime(target.Year, target.Month, target.Day, target.Hour, 0, 0);
		bool alreadyServedCurrent = lastServedHourBucket.HasValue && lastServedHourBucket.Value == currentBucket;
		if (alreadyServedCurrent || localNow > winEnd)
		{
			target = target.AddHours(1);
			winStart = target.AddMinutes(-beforeMin);
			winEnd = target.AddMinutes(afterMin);
			currentBucket = new DateTime(target.Year, target.Month, target.Day, target.Hour, 0, 0);
			alreadyServedCurrent = lastServedHourBucket.HasValue && lastServedHourBucket.Value == currentBucket;
		}

		string windowText = $"{winStart:HH:mm}–{winEnd:HH:mm}";
		if (!settings.RadioIdsEnabled)
		{
			return new StationIdDecision(StationIdActionKind.None, -1, target, winStart, winEnd, alreadyServedCurrent, "Station IDs disabled");
		}

		bool alreadyQueued = queue.Any(isRadioIdPredicate);
		if (alreadyServedCurrent)
		{
			return new StationIdDecision(StationIdActionKind.None, -1, target, winStart, winEnd, true, $"Served • Next target {target:HH:mm} ({windowText})");
		}

		// Mode 1: OverlayAtTarget — duck music and fire ID directly at target minute without cutting song
		if (settings.RadioIdEnforcementMode == StationIdEnforcementMode.OverlayAtTarget)
		{
			if (localNow >= target && localNow <= winEnd)
			{
				return new StationIdDecision(
					StationIdActionKind.FireOverlayNow,
					-1,
					target,
					winStart,
					winEnd,
					false,
					$"Firing Overlay Station ID at {target:HH:mm}");
			}
			TimeSpan eta = target - localNow;
			return new StationIdDecision(
				StationIdActionKind.None,
				-1,
				target,
				winStart,
				winEnd,
				false,
				$"Overlay ID at {target:HH:mm} (in {Math.Max(0, (int)Math.Ceiling(eta.TotalMinutes))}m)");
		}

		// Mode 2: HardInterruptAtDeadline — if we reached the end of the tolerance window (or exact target when tolerance=0) and still haven't aired it, enforce hard crossfade now!
		if (settings.RadioIdEnforcementMode == StationIdEnforcementMode.HardInterruptAtDeadline &&
			localNow >= winEnd.AddSeconds(-2))
		{
			return new StationIdDecision(
				StationIdActionKind.FireHardInterruptNow,
				0,
				target,
				winStart,
				winEnd,
				false,
				$"Deadline reached ({winEnd:HH:mm}) — enforcing Station ID now!");
		}

		// If already queued inside the upcoming playout, don't insert a duplicate
		if (alreadyQueued)
		{
			return new StationIdDecision(
				StationIdActionKind.None,
				-1,
				target,
				winStart,
				winEnd,
				false,
				$"ID queued for {target:HH:mm} window ({windowText})");
		}

		// Check if the currently playing track ends inside the window (insert at position 0!)
		DateTime cursor = localNow + (currentTrackRemaining > TimeSpan.Zero ? currentTrackRemaining : TimeSpan.Zero);
		if (cursor >= winStart && cursor <= winEnd)
		{
			return new StationIdDecision(
				StationIdActionKind.InsertIntoQueue,
				0,
				target,
				winStart,
				winEnd,
				false,
				$"Queuing ID next for {target:HH:mm} window ({windowText})");
		}

		// Otherwise scan projected queue track boundaries to find the closest transition inside [winStart, winEnd],
		// AND track any single long song that straddles across [winStart, winEnd] so Station IDs never vanish!
		int bestIdx = -1;
		double bestDistanceSec = double.MaxValue;
		int straddleIdx = -1;
		double straddleDistanceSec = double.MaxValue;

		for (int i = 0; i < queue.Count; i++)
		{
			DateTime trackStart = cursor;
			TimeSpan dur = queue[i].Duration > TimeSpan.Zero ? queue[i].Duration : TimeSpan.FromMinutes(3);
			DateTime trackEnd = trackStart + dur;
			cursor = trackEnd;

			if (trackEnd >= winStart && trackEnd <= winEnd)
			{
				double dist = Math.Abs((trackEnd - target).TotalSeconds);
				if (dist < bestDistanceSec)
				{
					bestDistanceSec = dist;
					bestIdx = i + 1;
				}
			}
			else if (trackStart < winStart && trackEnd > winEnd)
			{
				// Long track spans completely across the window (e.g. 11:53:54 -> 12:03:52 across 11:57-12:03).
				// Pick whichever boundary (before track = i, or after track = i+1) is closest to target!
				double distBefore = Math.Abs((trackStart - target).TotalSeconds);
				double distAfter = Math.Abs((trackEnd - target).TotalSeconds);
				int candidateIdx = distBefore <= distAfter ? i : i + 1;
				double candidateDist = Math.Min(distBefore, distAfter);
				if (candidateDist < straddleDistanceSec)
				{
					straddleDistanceSec = candidateDist;
					straddleIdx = candidateIdx;
				}
			}
		}

		int chosenIdx = bestIdx >= 0 ? bestIdx : straddleIdx;
		if (chosenIdx >= 0)
		{
			return new StationIdDecision(
				StationIdActionKind.InsertIntoQueue,
				chosenIdx,
				target,
				winStart,
				winEnd,
				false,
				$"Projected ID at slot #{chosenIdx + 1} for {target:HH:mm} ({windowText})");
		}

		// If we're ALREADY inside the window right now and current track ends AFTER winEnd:
		// in QueueInWindow mode, put ID at index 0 so it plays as soon as the current track finishes.
		if (localNow >= winStart && localNow <= winEnd)
		{
			return new StationIdDecision(
				StationIdActionKind.InsertIntoQueue,
				0,
				target,
				winStart,
				winEnd,
				false,
				$"Inside {windowText} window — queuing ID next");
		}

		TimeSpan untilWin = winStart - localNow;
		return new StationIdDecision(
			StationIdActionKind.None,
			-1,
			target,
			winStart,
			winEnd,
			false,
			$"Next ID {target:HH:mm} (window {windowText}, in {Math.Max(0, (int)Math.Ceiling(untilWin.TotalMinutes))}m)");
	}

	public static bool IsImagingOrCommercial(LibraryItem? item, AppSettings settings)
	{
		if (item == null) return false;
		if (item.IsSweeperCue || item.IsStationIdCue || item.IsCommercialCue) return true;
		string[] swpCats = ParseCategoryList(settings.SweeperCategoryNames, "Sweeper", "Sweep", "Jingle");
		string[] idCats = ParseCategoryList(settings.RadioIdCategoryNames, "StationId", "Station ID", "ID");
		return MatchesFolderOrCategory(item, settings.SweeperFolder, swpCats) ||
			   MatchesFolderOrCategory(item, settings.RadioIdFolder, idCats);
	}

	/// <summary>
	/// Resolves the effective broadcast category for audio transition planning, honoring
	/// configured Sweeper and Station ID folders/categories even when a file was indexed as "Music".
	/// </summary>
	public static string ResolveEffectiveCategory(LibraryItem item, AppSettings? settings)
	{
		if (settings != null)
		{
			string[] idCats = ParseCategoryList(settings.RadioIdCategoryNames, "StationId", "Station ID", "ID");
			if (item.IsStationIdCue || MatchesFolderOrCategory(item, settings.RadioIdFolder, idCats))
			{
				return "StationId";
			}

			string[] swpCats = ParseCategoryList(settings.SweeperCategoryNames, "Sweeper", "Sweep", "Jingle");
			if (item.IsSweeperCue || MatchesFolderOrCategory(item, settings.SweeperFolder, swpCats))
			{
				return "Sweeper";
			}
		}

		return item.Category;
	}

	private static string NormalizePathSeparators(string path) =>
		path.Trim().Replace('\\', '/');

	private static string NormalizeFolder(string folder)
	{
		string trimmed = NormalizePathSeparators(folder).TrimEnd('/');
		return string.IsNullOrEmpty(trimmed) ? string.Empty : trimmed + "/";
	}
}
