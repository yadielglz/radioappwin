using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using RadioApp.Data;
using RadioApp.Models;
using RadioApp.Services;
using Xunit;

namespace RadioApp.Tests;

public class LibraryPlaysetRotationTests : IDisposable
{
	private readonly string _tempDir;
	private readonly string _dbPath;
	private readonly RadioDbContextFactory _dbFactory;

	public LibraryPlaysetRotationTests()
	{
		_tempDir = Path.Combine(Path.GetTempPath(), "RadioAppTests_" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(_tempDir);
		_dbPath = Path.Combine(_tempDir, "radioapp-test.db");
		_dbFactory = new RadioDbContextFactory(_dbPath);
	}

	[Fact]
	public void Database_CreatesTablesVerifiesIntegrityAndBackupsCleanly()
	{
		using RadioLibraryContext ctx = _dbFactory.CreateContext();
		var (ok, msg) = ctx.VerifyIntegrity();
		Assert.True(ok, msg);

		string backupFile = ctx.CreateBackup(Path.Combine(_tempDir, "backup.db"));
		Assert.True(File.Exists(backupFile));
	}

	[Fact]
	public void PlaysetService_ManualSmartPlaysetsQueuePersistenceAndM3u8RoundTrip()
	{
		var items = new List<LibraryItem>
		{
			new LibraryItem { FilePath = Path.Combine(_tempDir, "t1.mp3"), Title = "Track One", Artist = "Artist A", Category = "Music", Tags = "90s, Rock", Bpm = 122, Duration = TimeSpan.FromSeconds(200), FileExistsCached = true },
			new LibraryItem { FilePath = Path.Combine(_tempDir, "t2.mp3"), Title = "Track Two", Artist = "Artist B", Category = "Music", Tags = "90s, Pop", Bpm = 128, Duration = TimeSpan.FromSeconds(180), FileExistsCached = true },
			new LibraryItem { FilePath = Path.Combine(_tempDir, "t3.mp3"), Title = "Track Three", Artist = "Artist C", Category = "Sweeper", Tags = "Jingle", Bpm = 90, Duration = TimeSpan.FromSeconds(8), FileExistsCached = true }
		};

		using (RadioLibraryContext ctx = _dbFactory.CreateContext())
		{
			ctx.LibraryItems.AddRange(items);
			ctx.SaveChanges();
		}

		var service = new PlaysetService(_dbFactory);

		// 1. Manual Playset CRUD + Track Order
		Playset manual = service.SavePlayset(new Playset { Name = "Drive Time", Kind = PlaysetKind.Manual, Description = "Afternoon show" });
		service.AddTracksToPlayset(manual.Id, new[] { items[1].Id, items[0].Id });

		List<LibraryItem> manualTracks = service.ResolvePlaysetTracks(manual, items);
		Assert.Equal(2, manualTracks.Count);
		Assert.Equal("Track Two", manualTracks[0].Title);
		Assert.Equal("Track One", manualTracks[1].Title);

		// 2. Smart Rule Playset
		var smart = new Playset { Name = "Upbeat 90s", Kind = PlaysetKind.SmartRule };
		smart.SetRule(new SmartPlaysetRule
		{
			CategoryFilter = "Music",
			RequiredTags = "90s",
			MinBpm = 120,
			MaxBpm = 130,
			SortBy = "Bpm"
		});
		service.SavePlayset(smart);

		List<LibraryItem> smartTracks = service.ResolvePlaysetTracks(smart, items);
		Assert.Equal(2, smartTracks.Count);
		Assert.Equal(122, smartTracks[0].Bpm);
		Assert.Equal(128, smartTracks[1].Bpm);

		// 3. Queue Persistence
		var autoSet = new HashSet<LibraryItem> { items[1] };
		service.SavePersistedQueue(new[] { items[0], items[1] }, autoSet);
		var restored = service.LoadPersistedQueue(items);
		Assert.Equal(2, restored.Count);
		Assert.Equal("Track One", restored[0].Item.Title);
		Assert.False(restored[0].IsAutoDj);
		Assert.True(restored[1].IsAutoDj);

		// 4. M3U8 Export & Parse Round-trip
		string m3uPath = Path.Combine(_tempDir, "drive.m3u8");
		PlaysetService.ExportM3u8(m3uPath, "Drive Time", manualTracks);
		List<string> parsedPaths = PlaysetService.ParsePlaylistFile(m3uPath);
		Assert.Equal(2, parsedPaths.Count);
		Assert.Equal(items[1].FilePath, parsedPaths[0]);
	}

	[Fact]
	public void RotationService_EnforcesArtistSeparationAndOvernightHourlyRules()
	{
		var rotation = new RotationService(seed: 42);
		var settings = new AppSettings { AutoDjArtistSeparationMinutes = 60, AutoDjTrackSeparationHours = 2 };
		DateTime now = new DateTime(2026, 11, 22, 14, 0, 0, DateTimeKind.Utc);

		var lib = new List<LibraryItem>
		{
			new LibraryItem { Id = 1, Title = "Song 1", Artist = "Band A", Category = "Music", FileExistsCached = true },
			new LibraryItem { Id = 2, Title = "Song 2", Artist = "Band A", Category = "Music", FileExistsCached = true },
			new LibraryItem { Id = 3, Title = "Song 3", Artist = "Band B", Category = "Music", FileExistsCached = true },
			new LibraryItem { Id = 4, Title = "Song 4", Artist = "Band C", Category = "Music", FileExistsCached = true }
		};

		rotation.RecordPlayed(lib[0], now.AddMinutes(-5));
		LibraryItem? picked = rotation.PickNextTrack(lib, settings, utcNow: now);
		Assert.NotNull(picked);
		Assert.NotEqual("Band A", picked!.Artist);

		// Overnight wrap hourly tag rule
		string serialized = RotationService.SerializeHourlyTagRules(new[]
		{
			new HourlyTagRuleItem { StartTime = "06:00", EndTime = "18:00", Tag = "Daytime" },
			new HourlyTagRuleItem { StartTime = "22:00", EndTime = "06:00", Tag = "LateNight" }
		});
		ActiveRotationInfo late = RotationService.GetActiveRotation(serialized, "Default", new TimeSpan(2, 30, 0));
		Assert.Equal("LateNight", late.Tag);

		ActiveRotationInfo day = RotationService.GetActiveRotation(serialized, "Default", new TimeSpan(10, 0, 0));
		Assert.Equal("Daytime", day.Tag);

		ActiveRotationInfo fallback = RotationService.GetActiveRotation(serialized, "Default", new TimeSpan(19, 0, 0));
		Assert.Equal("Default", fallback.Tag);
	}

	[Fact]
	public void AppSettingsService_ProtectsPasswordAndRecoversFromBackup()
	{
		string settingsPath = Path.Combine(_tempDir, "settings.json");
		var svc = new AppSettingsService(settingsPath);
		var original = new AppSettings
		{
			StreamPassword = "SuperSecretRadioPassword!",
			TransitionMode = TransitionMode.AutoMix,
			AutoMixMaxTempoStretchPercent = 5.5
		};

		svc.Save(original);
		string rawJson = File.ReadAllText(settingsPath);
		Assert.DoesNotContain("SuperSecretRadioPassword!", rawJson);

		AppSettings loaded = svc.Load();
		Assert.Equal("SuperSecretRadioPassword!", loaded.StreamPassword);
		Assert.Equal(5.5, loaded.AutoMixMaxTempoStretchPercent);

		// Save a second time to create .bak, then corrupt primary file
		svc.Save(loaded);
		File.WriteAllText(settingsPath, "{ corrupted json !!!");
		AppSettings recovered = svc.Load();
		Assert.Equal("SuperSecretRadioPassword!", recovered.StreamPassword);
	}

	public void Dispose()
	{
		try { Directory.Delete(_tempDir, recursive: true); } catch { }
	}
}
