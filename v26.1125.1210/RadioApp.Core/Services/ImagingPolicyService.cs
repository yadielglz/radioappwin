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
	/// Optional exclusion folder/categories prevent Station IDs from bleeding into the Sweeper pool (and vice versa).
	/// </summary>
	public static CuePoolDiagnostics EvaluateCuePool(
		IReadOnlyList<LibraryItem> library,
		string folderPath,
		string categoryCsv,
		double minSeconds,
		double maxSeconds,
		bool isStationId,
		string? excludeFolderPath = null,
		string? excludeCategoryCsv = null)
	{
		double minSec = Math.Clamp(minSeconds, 0.2, 120.0);
		double maxSec = Math.Clamp(Math.Max(minSec, maxSeconds), minSec, 180.0);
		string[] categories = isStationId
			? ParseCategoryList(categoryCsv, "StationId", "Station ID", "ID")
			: ParseCategoryList(categoryCsv, "Sweeper", "Sweep", "Jingle");

		bool hasExcludeFolder = !string.IsNullOrWhiteSpace(excludeFolderPath) &&
			!string.Equals(NormalizeFolder(excludeFolderPath), NormalizeFolder(folderPath), StringComparison.OrdinalIgnoreCase);
		string[] excludeCats = !string.IsNullOrWhiteSpace(excludeCategoryCsv)
			? ParseCategoryList(excludeCategoryCsv)
			: Array.Empty<string>();

		var eligible = new List<LibraryItem>();
		int totalMatches = 0;
		int tooShort = 0;
		int tooLong = 0;
		int missing = 0;

		foreach (LibraryItem item in library)
		{
			if (!MatchesFolderOrCategory(item, folderPath, categories)) continue;

			// Prevent Station IDs from ever being picked as Sweepers (e.g. nested folders or overlapping imports)
			if (!isStationId && (item.IsStationIdCue ||
				(hasExcludeFolder && MatchesFolderOrCategory(item, excludeFolderPath!, Array.Empty<string>())) ||
				(excludeCats.Length > 0 && MatchesFolderOrCategory(item, string.Empty, excludeCats))))
			{
				continue;
			}
			if (isStationId && (item.IsSweeperCue ||
				(hasExcludeFolder && MatchesFolderOrCategory(item, excludeFolderPath!, Array.Empty<string>()))))
			{
				continue;
			}

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
	/// Resolves which hourly Station ID bucket (yyyy-MM-dd HH:00:00) a Station ID played at
	/// <paramref name="playTimeLocal"/> satisfies. Critically handles pre-top-of-hour windows:
	/// e.g. an ID airing at 13:57 for the 14:00 ± 5m window MUST mark the 14:00 hour bucket served
	/// (NOT 13:00), preventing back-to-back Station ID loops before top of hour!
	/// </summary>
	public static DateTime ResolveStationIdHourBucket(DateTime playTimeLocal, AppSettings settings)
	{
		int targetMin = Math.Clamp(settings.RadioIdMinute, 0, 59);
		int beforeMin = Math.Clamp(settings.RadioIdToleranceBeforeMinutes, 0, 30);
		int afterMin = Math.Clamp(settings.RadioIdToleranceAfterMinutes, 0, 30);

		DateTime baseHour = new DateTime(playTimeLocal.Year, playTimeLocal.Month, playTimeLocal.Day, playTimeLocal.Hour, 0, 0);
		DateTime[] candidates =
		{
			baseHour.AddHours(-1).AddMinutes(targetMin),
			baseHour.AddMinutes(targetMin),
			baseHour.AddHours(1).AddMinutes(targetMin)
		};

		DateTime? matchedTarget = null;
		double bestDistSec = double.MaxValue;
		foreach (DateTime candidate in candidates)
		{
			DateTime winStart = candidate.AddMinutes(-beforeMin);
			DateTime winEnd = candidate.AddMinutes(afterMin);
			if (playTimeLocal >= winStart && playTimeLocal <= winEnd)
			{
				double dist = Math.Abs((playTimeLocal - candidate).TotalSeconds);
				if (dist < bestDistSec)
				{
					bestDistSec = dist;
					matchedTarget = candidate;
				}
			}
		}

		DateTime resolved = matchedTarget ?? candidates.OrderBy(t => Math.Abs((playTimeLocal - t).TotalSeconds)).First();
		return new DateTime(resolved.Year, resolved.Month, resolved.Day, resolved.Hour, 0, 0);
	}

	/// <summary>
	/// Checks whether an already-projected Station ID in the Playout Queue is still aligned with
	/// its intended hourly window (allowing small straddle tolerance, never drifting 15-30m early).
	/// </summary>
	public static bool IsProjectedTimeWithinStationIdWindow(
		DateTime projectedAirTimeLocal,
		StationIdDecision decision,
		AppSettings settings)
	{
		int beforeMin = Math.Clamp(settings.RadioIdToleranceBeforeMinutes, 0, 30);
		int afterMin = Math.Clamp(settings.RadioIdToleranceAfterMinutes, 0, 30);
		double maxAllowanceSec = Math.Max(beforeMin, afterMin) * 60.0 + 300.0;
		return (projectedAirTimeLocal >= decision.WindowStartLocal && projectedAirTimeLocal <= decision.WindowEndLocal) ||
			   Math.Abs((projectedAirTimeLocal - decision.TargetTimeLocal).TotalSeconds) <= maxAllowanceSec;
	}

	/// <summary>
	/// Evaluates the Station ID window and enforcement policy (QueueInWindow, HardInterruptAtDeadline, OverlayAtTarget).
	/// Guarantees strict ONCE-PER-HOUR / ONCE-PER-HOURSET compliance: never double-fires back-to-back and never
	/// pulls an ID 20–30 minutes early into :30 or :40 marks.
	/// </summary>
	public static StationIdDecision EvaluateStationId(
		DateTime localNow,
		DateTime? lastServedHourBucket,
		AppSettings settings,
		TimeSpan currentTrackRemaining,
		IReadOnlyList<LibraryItem> queue,
		Func<LibraryItem, bool> isRadioIdPredicate,
		bool isCurrentlyPlayingStationId = false,
		int? rotationBoundaryQueueIndex = null)
	{
		int targetMin = Math.Clamp(settings.RadioIdMinute, 0, 59);
		int beforeMin = Math.Clamp(settings.RadioIdToleranceBeforeMinutes, 0, 30);
		int afterMin = Math.Clamp(settings.RadioIdToleranceAfterMinutes, 0, 30);

		DateTime target = new DateTime(localNow.Year, localNow.Month, localNow.Day, localNow.Hour, targetMin, 0);
		DateTime winStart = target.AddMinutes(-beforeMin);
		DateTime winEnd = target.AddMinutes(afterMin);

		// If the current hour's window has completely passed (or was already served), advance to next hour
		DateTime currentBucket = new DateTime(target.Year, target.Month, target.Day, target.Hour, 0, 0);
		bool alreadyServedCurrent = lastServedHourBucket.HasValue && lastServedHourBucket.Value >= currentBucket;
		if (alreadyServedCurrent || localNow > winEnd)
		{
			target = target.AddHours(1);
			winStart = target.AddMinutes(-beforeMin);
			winEnd = target.AddMinutes(afterMin);
			currentBucket = new DateTime(target.Year, target.Month, target.Day, target.Hour, 0, 0);
			alreadyServedCurrent = lastServedHourBucket.HasValue && lastServedHourBucket.Value >= currentBucket;
		}

		string windowText = $"{winStart:HH:mm}–{winEnd:HH:mm}";
		if (!settings.RadioIdsEnabled)
		{
			return new StationIdDecision(StationIdActionKind.None, -1, target, winStart, winEnd, alreadyServedCurrent, "Station IDs disabled");
		}

		// Never stack another Station ID while one is currently playing on-air!
		if (isCurrentlyPlayingStationId)
		{
			DateTime nextDisplayTarget = alreadyServedCurrent ? target.AddHours(1) : target;
			return new StationIdDecision(
				StationIdActionKind.None,
				-1,
				nextDisplayTarget,
				nextDisplayTarget.AddMinutes(-beforeMin),
				nextDisplayTarget.AddMinutes(afterMin),
				true,
				$"Station ID on air • Next target {nextDisplayTarget:HH:mm}");
		}

		bool alreadyQueued = queue.Any(isRadioIdPredicate);
		if (alreadyServedCurrent)
		{
			DateTime nextTarget = target.AddHours(1);
			DateTime nextStart = nextTarget.AddMinutes(-beforeMin);
			DateTime nextEnd = nextTarget.AddMinutes(afterMin);
			return new StationIdDecision(
				StationIdActionKind.None,
				-1,
				nextTarget,
				nextStart,
				nextEnd,
				true,
				$"Served • Next target {nextTarget:HH:mm} ({nextStart:HH:mm}–{nextEnd:HH:mm})");
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
			localNow >= winEnd.AddSeconds(-2) &&
			localNow <= winEnd)
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

		DateTime currentEnd = localNow + (currentTrackRemaining > TimeSpan.Zero ? currentTrackRemaining : TimeSpan.Zero);

		// Rotation-Change Handoff Priority:
		// When a top-of-hour RotationBlock change occurs (e.g. 12:00), place the Station ID strictly at the
		// exact handoff boundary between the outgoing rotation and the incoming rotation — NEVER prematurely
		// at 11:57/11:58 while outgoing-rotation songs are still queued behind it!
		if (targetMin == 0 && rotationBoundaryQueueIndex.HasValue &&
			rotationBoundaryQueueIndex.Value >= 0 && rotationBoundaryQueueIndex.Value <= queue.Count)
		{
			DateTime boundaryTime = currentEnd;
			for (int i = 0; i < rotationBoundaryQueueIndex.Value; i++)
			{
				TimeSpan dur = queue[i].Duration > TimeSpan.Zero ? queue[i].Duration : TimeSpan.FromMinutes(3);
				boundaryTime += dur;
			}
			double boundaryDistSec = Math.Abs((boundaryTime - target).TotalSeconds);
			double maxBoundaryDistSec = Math.Max(Math.Max(beforeMin, afterMin), 3) * 60.0 + 300.0;
			if (boundaryDistSec <= maxBoundaryDistSec)
			{
				return new StationIdDecision(
					StationIdActionKind.InsertIntoQueue,
					rotationBoundaryQueueIndex.Value,
					target,
					winStart,
					winEnd,
					false,
					$"Rotation handoff ID at slot #{rotationBoundaryQueueIndex.Value + 1} for {target:HH:mm} ({windowText})");
			}
		}

		// Scan ALL candidate boundaries (slot 0 = end of current track, plus slots 1..queue.Count).
		// For Top-of-Hour IDs (targetMin == 0), strongly prefer boundaries at/after 59:00 (including past 59:59,
		// where the ID opens the next hourset!) rather than firing prematurely at 55:00-58:59.
		double ScoreBoundary(DateTime boundary)
		{
			double dist = Math.Abs((boundary - target).TotalSeconds);
			if (targetMin == 0 && boundary < target.AddSeconds(-60))
			{
				// Penalize early pre-59:00 firing so 11:59:45 or 12:01:15 beats 11:57:30!
				dist += 420.0;
			}
			return dist;
		}

		int bestIdx = -1;
		double bestScore = double.MaxValue;
		int straddleIdx = -1;
		double straddleDistanceSec = double.MaxValue;
		double maxStraddleDistSec = Math.Max(beforeMin, afterMin) * 60.0 + 300.0;

		if (currentEnd >= winStart && currentEnd <= winEnd)
		{
			bestIdx = 0;
			bestScore = ScoreBoundary(currentEnd);
		}

		DateTime cursor = currentEnd;
		for (int i = 0; i < queue.Count; i++)
		{
			DateTime trackStart = cursor;
			TimeSpan dur = queue[i].Duration > TimeSpan.Zero ? queue[i].Duration : TimeSpan.FromMinutes(3);
			DateTime trackEnd = trackStart + dur;
			cursor = trackEnd;

			if (trackEnd >= winStart && trackEnd <= winEnd)
			{
				double score = ScoreBoundary(trackEnd);
				if (score < bestScore)
				{
					bestScore = score;
					bestIdx = i + 1;
				}
			}
			else if (trackStart < winStart && trackEnd > winEnd)
			{
				// Long track spans completely across the window (e.g. 11:53:54 -> 12:03:52 across 11:57-12:03).
				// For top-of-hour IDs, prefer placing AFTER the straddling track (opening the next hourset!)
				// rather than firing early before 59:00 whenever after-track is within maxStraddleDistSec.
				double distBefore = Math.Abs((trackStart - target).TotalSeconds);
				double distAfter = Math.Abs((trackEnd - target).TotalSeconds);
				int candidateIdx;
				double candidateDist;
				if (targetMin == 0 && trackStart < target.AddSeconds(-60) && distAfter <= maxStraddleDistSec)
				{
					candidateIdx = i + 1;
					candidateDist = distAfter;
				}
				else
				{
					candidateIdx = distBefore <= distAfter ? i : i + 1;
					candidateDist = Math.Min(distBefore, distAfter);
				}

				if (candidateDist <= maxStraddleDistSec && candidateDist < straddleDistanceSec)
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
		// in QueueInWindow mode, put ID at index 0 so it plays as soon as the current track finishes
		// (counted as part of the next hourset once past 59:59!).
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
