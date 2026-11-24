using System;
using System.Collections.Generic;
using System.Linq;
using RadioApp.Audio;
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

	[Fact]
	public void TransitionPlanner_SongToSweeper_LetsRhythmicAndColdSongsFinishWithoutCuttingEarly()
	{
		var settings = TransitionSettings.FromAppSettings(new AppSettings
		{
			TransitionMode = TransitionMode.AutoMix,
			GaplessPlaybackEnabled = true
		});

		// Rhythmic Salsa track where AudioTrackAnalyzer set AutoMixOutMs = 6.5s BEFORE EffectiveEnd
		var rhythmicSong = new TrackCueSnapshot(
			"marc_anthony.mp3", "Music", TimeSpan.FromSeconds(240),
			null, null, null, null,
			AutoCueInMs: 0, AutoMixOutMs: 232500, AutoCueOutMs: 239000,
			Bpm: 96.0, BpmConfidence: 0.90, FirstBeatMs: 200, OutroBeatMs: 232000,
			LoudnessLufs: -12.5, TruePeakDb: -0.5,
			IntroProfile: TrackEdgeProfile.Ramp, OutroProfile: TrackEdgeProfile.Rhythmic, 0, 0);

		var coldSong = rhythmicSong with { FilePath = "cold_end.mp3", OutroProfile = TrackEdgeProfile.Cold };

		// Genuine fade-out song with a 3.5s trailing fade (235.5s -> 239.0s)
		var fadeSong = rhythmicSong with
		{
			FilePath = "fade_end.mp3",
			AutoMixOutMs = 235500,
			AutoCueOutMs = 239000,
			OutroProfile = TrackEdgeProfile.Fade
		};

		var sweeper = new TrackCueSnapshot(
			"variedad_z93.mp3", "Sweeper", TimeSpan.FromSeconds(7.1),
			null, null, null, null,
			AutoCueInMs: 0, AutoMixOutMs: 5900, AutoCueOutMs: 7100,
			Bpm: 98.0, BpmConfidence: 0.70, FirstBeatMs: 0, OutroBeatMs: 6000,
			LoudnessLufs: -13.0, TruePeakDb: -0.8,
			IntroProfile: TrackEdgeProfile.Cold, OutroProfile: TrackEdgeProfile.Fade, 0, 0);

		// 1. Rhythmic song MUST finish all the way to 239.0s (within 0.30s) — NEVER cut at 232.5s (-6.5s)!
		TransitionPlan rhythmicPlan = TransitionPlanner.ComputePlan(rhythmicSong, sweeper, settings);
		Assert.Equal("SweeperEntry", rhythmicPlan.AppliedStrategy);
		Assert.False(rhythmicPlan.UseBeatSync);
		Assert.True(rhythmicPlan.FullPunchIncoming);
		Assert.InRange(rhythmicPlan.OutgoingMixStart.TotalSeconds, 238.70, 238.90);
		Assert.InRange(rhythmicPlan.CrossfadeDuration.TotalSeconds, 0.20, 0.30);

		// 2. Cold song finishes right at 239.0s with an ultra-tight 0.15s end-kiss
		TransitionPlan coldPlan = TransitionPlanner.ComputePlan(coldSong, sweeper, settings);
		Assert.Equal("SweeperEntry", coldPlan.AppliedStrategy);
		Assert.InRange(coldPlan.OutgoingMixStart.TotalSeconds, 238.80, 238.95);
		Assert.InRange(coldPlan.CrossfadeDuration.TotalSeconds, 0.10, 0.20);

		// 3. Fade song glides sweeper in ONLY near the very end of the quiet fade tail (never earlier than 237.5s)
		TransitionPlan fadePlan = TransitionPlanner.ComputePlan(fadeSong, sweeper, settings);
		Assert.Equal("SweeperEntry", fadePlan.AppliedStrategy);
		Assert.True(fadePlan.FullPunchIncoming);
		Assert.InRange(fadePlan.OutgoingMixStart.TotalSeconds, 237.50, 238.30);
		Assert.InRange(fadePlan.CrossfadeDuration.TotalSeconds, 0.70, 1.45);
	}

	[Fact]
	public void TransitionPlanner_SweeperToSong_IntelligentlySweepsIntoRampOrPunchesIntoColdStart()
	{
		var settings = TransitionSettings.FromAppSettings(new AppSettings
		{
			TransitionMode = TransitionMode.AutoMix,
			GaplessPlaybackEnabled = true
		});

		var producedSweeper = new TrackCueSnapshot(
			"z93_sweep.mp3", "Sweeper", TimeSpan.FromSeconds(7.1),
			null, null, null, null,
			AutoCueInMs: 0, AutoMixOutMs: 5800, AutoCueOutMs: 7100,
			Bpm: null, BpmConfidence: null, FirstBeatMs: null, OutroBeatMs: null,
			LoudnessLufs: -13.0, TruePeakDb: -0.8,
			IntroProfile: TrackEdgeProfile.Cold, OutroProfile: TrackEdgeProfile.Fade, 0, 0);

		var shortSweeper = producedSweeper with
		{
			FilePath = "short_2_4s.mp3",
			Duration = TimeSpan.FromSeconds(2.4),
			AutoMixOutMs = 1700,
			AutoCueOutMs = 2400
		};

		var rampNextSong = new TrackCueSnapshot(
			"next_ramp.mp3", "Music", TimeSpan.FromSeconds(210),
			null, IntroCueMs: 6000, null, null,
			AutoCueInMs: 0, AutoMixOutMs: 203000, AutoCueOutMs: 209000,
			Bpm: 100.0, BpmConfidence: 0.85, FirstBeatMs: 150, OutroBeatMs: 203000,
			LoudnessLufs: -13.0, TruePeakDb: -0.8,
			IntroProfile: TrackEdgeProfile.Ramp, OutroProfile: TrackEdgeProfile.Rhythmic, 0, 0);

		var coldNextSong = rampNextSong with
		{
			FilePath = "next_cold.mp3",
			IntroCueMs = null,
			IntroProfile = TrackEdgeProfile.Cold
		};

		// 1. Produced sweeper -> Song with ramp/intro: smooth ~1.25s radio sweep riding the sweeper's tail
		TransitionPlan sweepPlan = TransitionPlanner.ComputePlan(producedSweeper, rampNextSong, settings);
		Assert.Equal("SweeperSweepOut", sweepPlan.AppliedStrategy);
		Assert.False(sweepPlan.UseBeatSync);
		Assert.True(sweepPlan.FullPunchIncoming);
		Assert.InRange(sweepPlan.CrossfadeDuration.TotalSeconds, 1.0, 1.35);
		Assert.InRange(sweepPlan.OutgoingMixStart.TotalSeconds, 5.75, 6.10);

		// 2. Produced sweeper -> Cold vocal/hit song: crisp ~0.30s punch at very end of sweeper
		TransitionPlan punchPlan = TransitionPlanner.ComputePlan(producedSweeper, coldNextSong, settings);
		Assert.Equal("SweeperSweepOut", punchPlan.AppliedStrategy);
		Assert.True(punchPlan.FullPunchIncoming);
		Assert.InRange(punchPlan.CrossfadeDuration.TotalSeconds, 0.20, 0.35);
		Assert.InRange(punchPlan.OutgoingMixStart.TotalSeconds, 6.75, 6.95);

		// 3. Short 2.4s sweeper -> Ramp song: capped to <= 25% of sweeper length (<= 0.60s) so voice is never stepped on
		TransitionPlan shortPlan = TransitionPlanner.ComputePlan(shortSweeper, rampNextSong, settings);
		Assert.Equal("SweeperSweepOut", shortPlan.AppliedStrategy);
		Assert.True(shortPlan.CrossfadeDuration.TotalSeconds <= 0.61);
	}

	[Fact]
	public void ImagingPolicyService_ResolveEffectiveCategory_ClassifiesFolderMatchedSweepersAndIds()
	{
		var settings = new AppSettings
		{
			SweeperFolder = "/media/Sweepers",
			RadioIdFolder = "/media/StationIDs",
			SweeperCategoryNames = "Sweeper,Sweep,Jingle",
			RadioIdCategoryNames = "StationId,Station ID,ID"
		};

		// File indexed as "Music" in DB, but located inside SweeperFolder!
		LibraryItem folderSweeper = MakeItem(50, "Variedad Z93", "", "Music", "", 7.1, "/media/Sweepers/Variedad_Z93.mp3");
		LibraryItem folderId = MakeItem(51, "Legal ID", "", "Music", "", 6.0, "/media/StationIDs/Legal_ID.mp3");
		LibraryItem normalSong = MakeItem(52, "Vivir Mi Vida", "Marc Anthony", "Music", "salsa", 252.0, "/media/Music/Vivir.mp3");

		string swpCat = ImagingPolicyService.ResolveEffectiveCategory(folderSweeper, settings);
		string idCat = ImagingPolicyService.ResolveEffectiveCategory(folderId, settings);
		string songCat = ImagingPolicyService.ResolveEffectiveCategory(normalSong, settings);

		Assert.Equal("Sweeper", swpCat);
		Assert.Equal("StationId", idCat);
		Assert.Equal("Music", songCat);

		TrackCueSnapshot swpCue = TrackCueSnapshot.FromLibraryItem(folderSweeper, swpCat);
		TrackCueSnapshot idCue = TrackCueSnapshot.FromLibraryItem(folderId, idCat);
		Assert.True(swpCue.IsSweeper);
		Assert.True(idCue.IsStationId);
	}
}
