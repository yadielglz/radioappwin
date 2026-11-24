using System;
using System.Collections.Generic;
using System.Linq;
using RadioApp.Models;
using RadioApp.Services;
using Xunit;

namespace RadioApp.Tests;

public class RotationAndImagingTests
{
	private static LibraryItem MakeItem(int id, string title, string artist, string category, string tags, double durationSec, string path, double introCueSec = 0)
	{
		return new LibraryItem
		{
			Id = id,
			Title = title,
			Artist = artist,
			Category = category,
			Tags = tags,
			Duration = TimeSpan.FromSeconds(durationSec),
			FilePath = path,
			FileExistsCached = true,
			IntroCueMs = introCueSec > 0 ? (int)Math.Round(introCueSec * 1000.0) : null
		};
	}

	[Fact]
	public void ImagingPolicyService_EvaluateCuePool_EnforcesMinAndMaxDurationThresholdsAccurately()
	{
		var library = new List<LibraryItem>
		{
			MakeItem(1, "Too Short Drop", "", "Sweeper", "", 0.6, "/sweepers/short.mp3"),
			MakeItem(2, "Good Sweeper 1", "", "Sweeper", "", 3.5, "/sweepers/good1.mp3"),
			MakeItem(3, "Good Jingle 2", "", "Jingle", "", 8.0, "/audio/jingle2.mp3"),
			MakeItem(4, "Too Long Promo", "", "Sweeper", "", 22.0, "/sweepers/long.mp3"),
			MakeItem(5, "Missing Sweeper", "", "Sweeper", "", 4.0, "/sweepers/missing.mp3"),
			MakeItem(6, "Normal Song", "Artist A", "Music", "pop", 210.0, "/music/song.mp3")
		};
		library[4].FileExistsCached = false;

		CuePoolDiagnostics diag = ImagingPolicyService.EvaluateCuePool(
			library,
			folderPath: "/sweepers",
			categoryCsv: "Sweeper,Sweep,Jingle",
			minSeconds: 1.0,
			maxSeconds: 12.0,
			isStationId: false);

		Assert.Equal(5, diag.TotalFolderOrCategoryMatches);
		Assert.Equal(2, diag.Eligible.Count);
		Assert.Equal(1, diag.TooShortCount);
		Assert.Equal(1, diag.TooLongCount);
		Assert.Equal(1, diag.MissingFileCount);
		Assert.Contains("2 Sweeper(s) ready", diag.SummaryText);
	}

	[Fact]
	public void ImagingPolicyService_ShouldTriggerSweeper_EnforcesSongCountCooldownAndAdjacentGuard()
	{
		var settings = new AppSettings
		{
			SweepersEnabled = true,
			SweeperMinSongs = 3,
			SweeperMaxSongs = 5,
			SweeperMinCooldownMinutes = 5.0,
			SweeperAvoidAdjacentImaging = true
		};

		LibraryItem song1 = MakeItem(1, "Song 1", "A", "Music", "", 180, "/m/1.mp3");
		LibraryItem song2 = MakeItem(2, "Song 2", "B", "Music", "", 180, "/m/2.mp3");
		LibraryItem stationId = MakeItem(3, "Legal ID", "", "StationId", "", 6, "/id/legal.mp3");

		DateTime now = new DateTime(2026, 11, 24, 10, 0, 0, DateTimeKind.Utc);

		// Not enough songs yet
		Assert.False(ImagingPolicyService.ShouldTriggerSweeper(2, 3, now, null, settings, song1, song2, stationIdDueSoon: false));

		// Cooldown active (only 2 minutes since last sweeper)
		Assert.False(ImagingPolicyService.ShouldTriggerSweeper(4, 3, now, now.AddMinutes(-2), settings, song1, song2, stationIdDueSoon: false));

		// Adjacent Station ID queued next -> blocked by adjacent imaging guard
		Assert.False(ImagingPolicyService.ShouldTriggerSweeper(4, 3, now, now.AddMinutes(-10), settings, song1, stationId, stationIdDueSoon: false));

		// Station ID due soon -> blocked by adjacent imaging guard
		Assert.False(ImagingPolicyService.ShouldTriggerSweeper(4, 3, now, now.AddMinutes(-10), settings, song1, song2, stationIdDueSoon: true));

		// All thresholds satisfied -> triggers!
		Assert.True(ImagingPolicyService.ShouldTriggerSweeper(3, 3, now, now.AddMinutes(-8), settings, song1, song2, stationIdDueSoon: false));
	}

	[Fact]
	public void ImagingPolicyService_ShouldPlaySweeperAsOverlay_HonorsSmartAutoIntroCueFit()
	{
		var settings = new AppSettings { SweeperPlaybackMode = SweeperPlaybackMode.SmartAuto };
		LibraryItem sweeper = MakeItem(1, "4s Sweeper", "", "Sweeper", "", 4.0, "/swp/4s.mp3");
		LibraryItem longIntroTrack = MakeItem(2, "Long Intro Song", "A", "Music", "", 210, "/m/long.mp3", introCueSec: 8.5);
		LibraryItem coldStartTrack = MakeItem(3, "Cold Vocal Song", "B", "Music", "", 190, "/m/cold.mp3", introCueSec: 1.0);

		Assert.True(ImagingPolicyService.ShouldPlaySweeperAsOverlay(sweeper, longIntroTrack, settings));
		Assert.False(ImagingPolicyService.ShouldPlaySweeperAsOverlay(sweeper, coldStartTrack, settings));

		settings.SweeperPlaybackMode = SweeperPlaybackMode.QueueBetweenTracks;
		Assert.False(ImagingPolicyService.ShouldPlaySweeperAsOverlay(sweeper, longIntroTrack, settings));

		settings.SweeperPlaybackMode = SweeperPlaybackMode.OverlayOverIntro;
		Assert.True(ImagingPolicyService.ShouldPlaySweeperAsOverlay(sweeper, coldStartTrack, settings));
	}

	[Fact]
	public void ImagingPolicyService_EvaluateStationId_EnforcesQueueInWindowHardDeadlineAndOverlayModes()
	{
		var queue = new List<LibraryItem>
		{
			MakeItem(1, "Track 1", "A", "Music", "", 180, "/m/1.mp3"),
			MakeItem(2, "Track 2", "B", "Music", "", 240, "/m/2.mp3")
		};
		static bool IsId(LibraryItem i) => i.IsStationIdCue;

		// 1. QueueInWindow: at 09:53 with 3m remaining on current song (ends 09:56, inside 09:55..10:05 window) -> Insert at index 0
		var queueSettings = new AppSettings
		{
			RadioIdsEnabled = true,
			RadioIdMinute = 0,
			RadioIdToleranceBeforeMinutes = 5,
			RadioIdToleranceAfterMinutes = 5,
			RadioIdEnforcementMode = StationIdEnforcementMode.QueueInWindow
		};
		StationIdDecision d1 = ImagingPolicyService.EvaluateStationId(
			new DateTime(2026, 11, 24, 9, 53, 0),
			null,
			queueSettings,
			TimeSpan.FromMinutes(3),
			queue,
			IsId);
		Assert.Equal(StationIdActionKind.InsertIntoQueue, d1.Action);
		Assert.Equal(0, d1.InsertIndex);

		// 2. HardInterruptAtDeadline: at 10:04:59 with a long track still running and ID not yet aired -> FireHardInterruptNow!
		var hardSettings = new AppSettings
		{
			RadioIdsEnabled = true,
			RadioIdMinute = 0,
			RadioIdToleranceBeforeMinutes = 5,
			RadioIdToleranceAfterMinutes = 5,
			RadioIdEnforcementMode = StationIdEnforcementMode.HardInterruptAtDeadline
		};
		StationIdDecision d2 = ImagingPolicyService.EvaluateStationId(
			new DateTime(2026, 11, 24, 10, 4, 59),
			null,
			hardSettings,
			TimeSpan.FromMinutes(4),
			queue,
			IsId);
		Assert.Equal(StationIdActionKind.FireHardInterruptNow, d2.Action);

		// 3. OverlayAtTarget: at 10:00:05 -> FireOverlayNow without interrupting song!
		var overlaySettings = new AppSettings
		{
			RadioIdsEnabled = true,
			RadioIdMinute = 0,
			RadioIdToleranceBeforeMinutes = 5,
			RadioIdToleranceAfterMinutes = 5,
			RadioIdEnforcementMode = StationIdEnforcementMode.OverlayAtTarget
		};
		StationIdDecision d3 = ImagingPolicyService.EvaluateStationId(
			new DateTime(2026, 11, 24, 10, 0, 5),
			null,
			overlaySettings,
			TimeSpan.FromMinutes(2),
			queue,
			IsId);
		Assert.Equal(StationIdActionKind.FireOverlayNow, d3.Action);

		// 4. Already served for 10:00 hour -> Action.None
		StationIdDecision d4 = ImagingPolicyService.EvaluateStationId(
			new DateTime(2026, 11, 24, 10, 1, 0),
			new DateTime(2026, 11, 24, 10, 0, 0),
			overlaySettings,
			TimeSpan.FromMinutes(2),
			queue,
			IsId);
		Assert.Equal(StationIdActionKind.None, d4.Action);

		// 5. Long song straddling right across a tight tolerance window (11:53:54 -> 12:03:52 across 11:57..12:03)
		// Must still project Station ID at the boundary closest to 12:00 (after the song at 12:03:52 vs before at 11:53:54)!
		var tightSettings = new AppSettings
		{
			RadioIdsEnabled = true,
			RadioIdMinute = 0,
			RadioIdToleranceBeforeMinutes = 3,
			RadioIdToleranceAfterMinutes = 3,
			RadioIdEnforcementMode = StationIdEnforcementMode.QueueInWindow
		};
		var straddleQueue = new List<LibraryItem>
		{
			MakeItem(10, "La Distancia", "A", "Music", "", 214, "/m/10.mp3"),
			MakeItem(11, "Sonido Bestial", "B", "Music", "", 598, "/m/11.mp3") // 9m58s spanning 11:53:54 -> 12:03:52
		};
		StationIdDecision d5 = ImagingPolicyService.EvaluateStationId(
			new DateTime(2026, 11, 24, 11, 49, 0),
			null,
			tightSettings,
			TimeSpan.FromSeconds(80), // current ends 11:50:20 -> La Distancia ends 11:53:54 -> Sonido Bestial ends 12:03:52
			straddleQueue,
			IsId);
		Assert.Equal(StationIdActionKind.InsertIntoQueue, d5.Action);
		Assert.Equal(2, d5.InsertIndex); // 12:03:52 is closer to 12:00 than 11:53:54
	}

	[Fact]
	public void RotationService_ResolvesDayOfWeekOvernightWrapAndPriorityTieBreaking()
	{
		var settings = new AppSettings
		{
			RotationBlocks = new List<RotationBlock>
			{
				new RotationBlock
				{
					Name = "Everyday Base",
					StartTime = "00:00",
					EndTime = "00:00",
					DaysMask = RotationDayMask.All,
					SourceKind = RotationSourceKind.Tag,
					SourceValue = "main",
					Priority = 10,
					Enabled = true
				},
				new RotationBlock
				{
					Name = "Friday Night Dance",
					StartTime = "22:00",
					EndTime = "02:00",
					DaysMask = RotationDayMask.Friday,
					SourceKind = RotationSourceKind.Tag,
					SourceValue = "dance",
					Priority = 30,
					Enabled = true,
					ArtistSeparationOverrideMinutes = 15
				}
			}
		};

		// Friday 23:15 -> Friday Night Dance wins by priority
		DateTime fridayLate = new DateTime(2026, 11, 27, 23, 15, 0); // 2026-11-27 is Friday
		ActiveRotationInfo friInfo = RotationService.ResolveActiveRotation(settings, fridayLate);
		Assert.Equal("dance", friInfo.SourceValue);
		Assert.Equal(15, friInfo.ArtistSeparationOverrideMinutes);

		// Saturday 01:15 -> still inside Friday's overnight 22:00-02:00 block!
		DateTime saturdayEarly = new DateTime(2026, 11, 28, 1, 15, 0); // Saturday
		ActiveRotationInfo satEarlyInfo = RotationService.ResolveActiveRotation(settings, saturdayEarly);
		Assert.Equal("dance", satEarlyInfo.SourceValue);

		// Saturday 03:00 -> back to Everyday Base
		DateTime saturdayLater = new DateTime(2026, 11, 28, 3, 0, 0);
		ActiveRotationInfo satLaterInfo = RotationService.ResolveActiveRotation(settings, saturdayLater);
		Assert.Equal("main", satLaterInfo.SourceValue);
	}

	[Fact]
	public void RotationService_AnalyzeCoverage_DetectsGapsAndFull24HourTemplates()
	{
		List<RotationBlock> fullPreset = RotationService.CreatePresetBlocks("broadcast_dayparts");
		RotationCoverageReport fullReport = RotationService.AnalyzeCoverage(fullPreset, DayOfWeek.Monday);
		Assert.True(fullReport.CoveragePercent >= 99.9);
		Assert.Empty(fullReport.GapRanges);

		var partialBlocks = new List<RotationBlock>
		{
			new RotationBlock { Name = "Morning", StartTime = "06:00", EndTime = "12:00", DaysMask = RotationDayMask.All, SourceKind = RotationSourceKind.AllMusic }
		};
		RotationCoverageReport partialReport = RotationService.AnalyzeCoverage(partialBlocks, DayOfWeek.Monday);
		Assert.InRange(partialReport.CoveragePercent, 24.0, 26.0);
		Assert.Equal(2, partialReport.GapRanges.Count);
		Assert.Contains("00:00–06:00", partialReport.GapRanges);
		Assert.Contains("12:00–24:00", partialReport.GapRanges);
	}

	[Fact]
	public void RotationService_PickTrackForRotation_SupportsPlaysetCategoryAndLegacyMigration()
	{
		var library = new List<LibraryItem>
		{
			MakeItem(1, "Rock Hit", "Band 1", "Rock", "rock", 200, "/m/1.mp3"),
			MakeItem(2, "Chill Wave", "DJ Chill", "Music", "chill", 210, "/m/2.mp3"),
			MakeItem(3, "Playset Gem", "Indie Star", "Music", "indie", 195, "/m/3.mp3")
		};

		var rotationSvc = new RotationService(seed: 42);
		var settings = new AppSettings { AutoDjHourlyTagRules = "06:00-18:00=chill" };

		// Legacy migration produces RotationBlocks automatically
		List<RotationBlock> migrated = RotationService.GetEffectiveBlocks(settings);
		Assert.Single(migrated);
		Assert.Equal("chill", migrated[0].SourceValue);

		// Category rotation pick
		var catRot = new ActiveRotationInfo("", "Rock Hour", RotationSourceKind.Category, "Rock");
		LibraryItem? rockPick = rotationSvc.PickTrackForRotation(library, settings, catRot);
		Assert.NotNull(rockPick);
		Assert.Equal(1, rockPick!.Id);

		// Playset rotation pick
		var psRot = new ActiveRotationInfo("", "Evening Playset", RotationSourceKind.Playset, "", PlaysetId: 99);
		LibraryItem? psPick = rotationSvc.PickTrackForRotation(library, settings, psRot, resolvedPlaysetTracks: new[] { library[2] });
		Assert.NotNull(psPick);
		Assert.Equal(3, psPick!.Id);
	}
}
