using System;
using System.IO;
using System.Threading;
using NAudio.Wave;
using RadioApp.Audio;
using RadioApp.Models;
using RadioApp.Services;
using Xunit;

namespace RadioApp.Tests;

public class CloudAndUpdaterTests
{
	private sealed class FiniteSineSampleProvider : ISampleProvider
	{
		private readonly int _totalFrames;
		private int _framesRead;
		public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(44100, 2);

		public FiniteSineSampleProvider(double durationSeconds)
		{
			_totalFrames = (int)(44100 * durationSeconds);
		}

		public int Read(float[] buffer, int offset, int count)
		{
			int framesRequested = count / 2;
			int remaining = _totalFrames - _framesRead;
			if (remaining <= 0) return 0;
			int toWrite = Math.Min(framesRequested, remaining);
			for (int i = 0; i < toWrite; i++)
			{
				float s = 0.25f * MathF.Sin(2f * MathF.PI * 440f * (_framesRead + i) / 44100f);
				buffer[offset + i * 2] = s;
				buffer[offset + i * 2 + 1] = s;
			}
			_framesRead += toWrite;
			return toWrite * 2;
		}
	}

	[Fact]
	public void HeadlessBroadcastClockPlayer_PumpsMasterDspBroadcastTap_WithoutHardware_AndRaisesStoppedOnEof()
	{
		var source = new FiniteSineSampleProvider(0.12); // 120ms audio
		long broadcastBytesTapped = 0;
		var dsp = new MasterDspProcessor(source, pcm => Interlocked.Add(ref broadcastBytesTapped, pcm.Length));

		using var clock = new HeadlessBroadcastClockPlayer();
		using var stoppedSignal = new ManualResetEventSlim(false);
		Exception? playbackError = null;
		clock.PlaybackStopped += (_, e) =>
		{
			playbackError = e.Exception;
			stoppedSignal.Set();
		};

		clock.Init(dsp.ToWaveProvider16());
		clock.Play();

		bool completed = stoppedSignal.Wait(TimeSpan.FromSeconds(3));
		Assert.True(completed, "HeadlessBroadcastClockPlayer should finish finite stream and raise PlaybackStopped.");
		Assert.Null(playbackError);
		Assert.Equal(PlaybackState.Stopped, clock.PlaybackState);

		// 0.12s * 44100 * 2ch * 2bytes ~= 21,168 bytes tapped to the broadcast encoder
		long bytes = Interlocked.Read(ref broadcastBytesTapped);
		Assert.InRange(bytes, 18000, 25000);
	}

	[Fact]
	public void HeadlessBroadcastClockPlayer_SupportsPauseResumeAndExplicitStop()
	{
		var source = new FiniteSineSampleProvider(2.0);
		long broadcastBytesTapped = 0;
		var dsp = new MasterDspProcessor(source, pcm => Interlocked.Add(ref broadcastBytesTapped, pcm.Length));

		using var clock = new HeadlessBroadcastClockPlayer();
		clock.Init(dsp.ToWaveProvider16());
		clock.Play();
		Thread.Sleep(70);
		Assert.Equal(PlaybackState.Playing, clock.PlaybackState);
		Assert.True(Interlocked.Read(ref broadcastBytesTapped) > 0);

		clock.Pause();
		Assert.Equal(PlaybackState.Paused, clock.PlaybackState);
		Thread.Sleep(50);
		long pausedBytes = Interlocked.Read(ref broadcastBytesTapped);
		Thread.Sleep(60);
		Assert.Equal(pausedBytes, Interlocked.Read(ref broadcastBytesTapped));

		clock.Play();
		Assert.Equal(PlaybackState.Playing, clock.PlaybackState);
		Thread.Sleep(70);
		Assert.True(Interlocked.Read(ref broadcastBytesTapped) > pausedBytes);

		clock.Stop();
		Assert.Equal(PlaybackState.Stopped, clock.PlaybackState);
	}

	[Fact]
	public void CloudKeepAliveService_UpdatesStateSafelyOnAnyPlatform()
	{
		using var keepAlive = new CloudKeepAliveService();
		keepAlive.Apply(keepSystemAwake: true, elevatePriority: true);
		Assert.True(keepAlive.IsKeepAwakeActive);
		Assert.True(keepAlive.IsHighPriorityActive);

		keepAlive.Apply(keepSystemAwake: false, elevatePriority: false);
		Assert.False(keepAlive.IsKeepAwakeActive);
		Assert.False(keepAlive.IsHighPriorityActive);
	}

	[Theory]
	[InlineData("26.1120.1000", "26.1123.1000", true)]
	[InlineData("v26.1120.1000", "v26.1123.1000", true)]
	[InlineData("26.1123.1000", "26.1123.1000", false)]
	[InlineData("26.1123.1000", "26.1120.1000", false)]
	[InlineData("26.1123.1000", "v26.1201.0001", true)]
	public void GitUpdateService_IsNewerVersion_ComparesCalVerAccurately(string current, string candidate, bool expectedNewer)
	{
		Assert.Equal(expectedNewer, GitUpdateService.IsNewerVersion(current, candidate));
	}

	[Fact]
	public void GitUpdateService_ParseReleasesJson_SelectsLatestWin10ReleaseAndSkipsDraftsAndWin7()
	{
		string sampleJson = """
		[
		  {
		    "tag_name": "v26.9999.0000",
		    "name": "Draft Future Build",
		    "draft": true,
		    "body": "Should be ignored because draft=true",
		    "html_url": "https://github.com/yadielglz/radioappwin/releases/tag/v26.9999.0000",
		    "assets": []
		  },
		  {
		    "tag_name": "v26.1205.1000-win7",
		    "name": "Windows 7 Legacy Build",
		    "draft": false,
		    "body": "Should be ignored because tag contains win7",
		    "html_url": "https://github.com/yadielglz/radioappwin/releases/tag/v26.1205.1000-win7",
		    "assets": [
		      { "name": "RadioApp-26.1205.1000-Windows7-x64.msi", "browser_download_url": "https://example.com/win7.msi", "size": 1000, "content_type": "application/octet-stream" }
		    ]
		  },
		  {
		    "tag_name": "v26.1123.1000",
		    "name": "RadioApp Studio 26.1123.1000 (Windows 10/11 x64)",
		    "draft": false,
		    "body": "Cloud PC RDP continuity + Git self-updater.",
		    "html_url": "https://github.com/yadielglz/radioappwin/releases/tag/v26.1123.1000",
		    "published_at": "2026-11-23T18:00:00Z",
		    "assets": [
		      { "name": "RadioApp-26.1123.1000-Win64-Setup.msi", "browser_download_url": "https://example.com/setup.msi", "size": 65000000, "content_type": "application/octet-stream" },
		      { "name": "RadioApp-26.1123.1000-Win64-Portable.zip", "browser_download_url": "https://example.com/portable.zip", "size": 64000000, "content_type": "application/zip" }
		    ]
		  },
		  {
		    "tag_name": "v26.1120.1000",
		    "name": "RadioApp Studio 26.1120.1000",
		    "draft": false,
		    "body": "Older release",
		    "html_url": "https://github.com/yadielglz/radioappwin/releases/tag/v26.1120.1000",
		    "assets": []
		  }
		]
		""";

		UpdateCheckResult result = GitUpdateService.ParseReleasesJson(sampleJson, "26.1120.1000");

		Assert.True(result.Success);
		Assert.True(result.UpdateAvailable);
		Assert.Equal("26.1123.1000", result.LatestVersion);
		Assert.Equal("v26.1123.1000", result.TagName);
		Assert.NotNull(result.MsiAsset);
		Assert.Equal("RadioApp-26.1123.1000-Win64-Setup.msi", result.MsiAsset!.Name);
		Assert.NotNull(result.PortableZipAsset);
		Assert.Equal("RadioApp-26.1123.1000-Win64-Portable.zip", result.PortableZipAsset!.Name);

		UpdateCheckResult upToDate = GitUpdateService.ParseReleasesJson(sampleJson, "26.1123.1000");
		Assert.True(upToDate.Success);
		Assert.False(upToDate.UpdateAvailable);
		Assert.Contains("up to date", upToDate.Message, StringComparison.OrdinalIgnoreCase);
	}

	[Fact]
	public void GitUpdateService_FindGitRoot_FindsRepositoryRootFromNestedFolder()
	{
		string? root = GitUpdateService.FindGitRoot(AppContext.BaseDirectory);
		Assert.NotNull(root);
		Assert.True(Directory.Exists(Path.Combine(root!, ".git")));
	}

	[Fact]
	public void AppSettings_CloudAndUpdaterDefaults_AreEnabledOutOfTheBox()
	{
		var settings = new AppSettings();
		Assert.True(settings.CloudUnattendedModeEnabled);
		Assert.True(settings.AutoFallbackToHeadlessClock);
		Assert.True(settings.KeepSystemAwakeWhileRunning);
		Assert.True(settings.ElevateProcessPriority);
		Assert.True(settings.AutoCheckForUpdatesOnStartup);
		Assert.Equal(AppVersionInfo.DefaultGitHubRepository, settings.GitUpdateRepository);
		Assert.Equal("26.1125.1200", AppVersionInfo.Version);
		Assert.Equal(TransitionMode.RadioGapless, settings.TransitionMode);
	}

	[Fact]
	public void GitUpdateService_ParsesPrivateAssetApiUrl_AndAppSettingsProtectsGitToken()
	{
		string json = """
		[
		  {
		    "tag_name": "v26.1125.1200",
		    "name": "Private Release",
		    "draft": false,
		    "assets": [
		      {
		        "name": "RadioApp-26.1125.1200-Win64-Setup.msi",
		        "url": "https://api.github.com/repos/yadielglz/radioappwin/releases/assets/999",
		        "browser_download_url": "https://github.com/yadielglz/radioappwin/releases/download/v26.1125.1200/RadioApp-26.1125.1200-Win64-Setup.msi",
		        "size": 69000000,
		        "content_type": "application/x-msdownload"
		      }
		    ]
		  }
		]
		""";

		UpdateCheckResult res = GitUpdateService.ParseReleasesJson(json, "26.1124.1500");
		Assert.True(res.Success);
		Assert.NotNull(res.MsiAsset);
		Assert.Equal("https://api.github.com/repos/yadielglz/radioappwin/releases/assets/999", res.MsiAsset!.ApiUrl);

		string tmpSettings = Path.Combine(Path.GetTempPath(), $"radioapp-token-test-{Guid.NewGuid():N}.json");
		string tmpBackup = tmpSettings + ".export.json";
		try
		{
			var svc = new AppSettingsService(tmpSettings);
			var s = new AppSettings { GitUpdateToken = "github_pat_secret_12345" };
			svc.Save(s);

			string rawDisk = File.ReadAllText(tmpSettings);
			Assert.DoesNotContain("github_pat_secret_12345", rawDisk);

			AppSettings reloaded = svc.Load();
			Assert.Equal("github_pat_secret_12345", reloaded.GitUpdateToken);

			svc.ExportBackup(reloaded, tmpBackup);
			string rawBackup = File.ReadAllText(tmpBackup);
			Assert.DoesNotContain("github_pat_secret_12345", rawBackup);
		}
		finally
		{
			try { File.Delete(tmpSettings); } catch { }
			try { File.Delete(tmpBackup); } catch { }
		}
	}

	[Fact]
	public void GitUpdateService_ValidatesMsiAndZipPackages_AndBuildsElevatedUpdaterScripts()
	{
		string tmpDir = Path.Combine(Path.GetTempPath(), $"radioapp-pkg-test-{Guid.NewGuid():N}");
		Directory.CreateDirectory(tmpDir);
		try
		{
			string fakeHtmlMsi = Path.Combine(tmpDir, "bad.msi");
			File.WriteAllText(fakeHtmlMsi, new string('x', 8192));
			Assert.False(GitUpdateService.ValidateDownloadedPackage(fakeHtmlMsi, out string badMsiErr));
			Assert.Contains("not a valid Windows Installer", badMsiErr);

			string validMsi = Path.Combine(tmpDir, "RadioApp-26.1125.1200-Win64-Setup.msi");
			byte[] msiBytes = new byte[8192];
			byte[] oleMagic = { 0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1 };
			Array.Copy(oleMagic, msiBytes, oleMagic.Length);
			File.WriteAllBytes(validMsi, msiBytes);
			Assert.True(GitUpdateService.ValidateDownloadedPackage(validMsi, out _));

			string validZip = Path.Combine(tmpDir, "RadioApp-26.1125.1200-Win64-Portable.zip");
			byte[] zipBytes = new byte[8192];
			zipBytes[0] = 0x50; zipBytes[1] = 0x4B; zipBytes[2] = 0x03; zipBytes[3] = 0x04;
			File.WriteAllBytes(validZip, zipBytes);
			Assert.True(GitUpdateService.ValidateDownloadedPackage(validZip, out _));

			// Verify MSI script elevates with -Verb RunAs, uses /qb! and /l*v logging, and checks ExitCode
			string msiScript = GitUpdateService.BuildMsiUpdaterScript(validMsi, 12345, @"C:\Program Files\RadioApp", tmpDir);
			Assert.Contains("-Verb RunAs", msiScript);
			Assert.Contains("/qb!", msiScript);
			Assert.Contains("/l*v", msiScript);
			Assert.Contains("last-msi-install.log", msiScript);
			Assert.Contains("$exitCode -eq 0 -or $exitCode -eq 3010", msiScript);

			// Verify ZIP script stages safely and self-elevates via -Verb RunAs when destination is protected
			string zipScript = GitUpdateService.BuildZipUpdaterScript(validZip, 12345, @"C:\Program Files\RadioApp", tmpDir);
			Assert.Contains("Expand-Archive", zipScript);
			Assert.Contains("-Verb RunAs", zipScript);
			Assert.Contains("$needsElevation", zipScript);
		}
		finally
		{
			try { Directory.Delete(tmpDir, true); } catch { }
		}
	}

	[Fact]
	public void AppSettingsService_MigratesLegacyAutoMixDefaultToRadioGaplessOnce()
	{
		string tmpPath = Path.Combine(Path.GetTempPath(), $"radioapp-migrate-{Guid.NewGuid():N}.json");
		try
		{
			// Simulate legacy v26.1124 settings.json without SettingsSchemaVersion and TransitionMode = 0 (AutoMix)
			File.WriteAllText(tmpPath, """{ "TransitionMode": 0, "GaplessPlaybackEnabled": true }""");
			var svc = new AppSettingsService(tmpPath);

			AppSettings migrated = svc.Load();
			Assert.Equal(TransitionMode.RadioGapless, migrated.TransitionMode);
			Assert.Equal(2, migrated.SettingsSchemaVersion);

			// If user explicitly chooses AutoMix after migration, it MUST be preserved!
			migrated.TransitionMode = TransitionMode.AutoMix;
			svc.Save(migrated);
			AppSettings reloaded = svc.Load();
			Assert.Equal(TransitionMode.AutoMix, reloaded.TransitionMode);
		}
		finally
		{
			try { File.Delete(tmpPath); } catch { }
			try { File.Delete(tmpPath + ".bak"); } catch { }
		}
	}
}
