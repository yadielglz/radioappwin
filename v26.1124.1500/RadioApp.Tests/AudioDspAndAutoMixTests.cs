using System;
using System.IO;
using System.Text;
using NAudio.Wave;
using RadioApp.Audio;
using RadioApp.Models;
using Xunit;

namespace RadioApp.Tests;

public class AudioDspAndAutoMixTests
{
	private sealed class SyntheticStereoProvider : ISampleProvider
	{
		private readonly float[] _samples;
		private int _pos;

		public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(44100, 2);

		public SyntheticStereoProvider(float[] stereoSamples)
		{
			_samples = stereoSamples;
		}

		public void Seek(TimeSpan position)
		{
			int frame = (int)Math.Round(position.TotalSeconds * 44100.0);
			_pos = Math.Clamp(frame * 2, 0, _samples.Length);
		}

		public int Read(float[] buffer, int offset, int count)
		{
			int remaining = _samples.Length - _pos;
			if (remaining <= 0) return 0;
			int toCopy = Math.Min(count, remaining);
			Array.Copy(_samples, _pos, buffer, offset, toCopy);
			_pos += toCopy;
			return toCopy;
		}
	}

	private static float[] GenerateSineStereo(double seconds, double freqHz, float amplitude, int sampleRate = 44100)
	{
		int frames = (int)(seconds * sampleRate);
		float[] data = new float[frames * 2];
		for (int f = 0; f < frames; f++)
		{
			float s = (float)(Math.Sin(2.0 * Math.PI * freqHz * f / sampleRate) * amplitude);
			data[f * 2] = s;
			data[f * 2 + 1] = s;
		}
		return data;
	}

	private static float[] GenerateBeatTrack(double seconds, double bpm, bool fadeOutTail = false, int sampleRate = 44100)
	{
		int frames = (int)(seconds * sampleRate);
		float[] data = new float[frames * 2];
		double beatPeriod = 60.0 / bpm;

		for (int f = 0; f < frames; f++)
		{
			double t = (double)f / sampleRate;
			double beatPhase = t % beatPeriod;
			// Punchy 70Hz kick envelope on every beat
			double env = Math.Exp(-beatPhase * 28.0);
			double kick = Math.Sin(2.0 * Math.PI * 75.0 * t) * env * 0.75;
			double pad = Math.Sin(2.0 * Math.PI * 440.0 * t) * 0.08;
			double masterEnv = 1.0;
			if (fadeOutTail && t > seconds - 5.0)
			{
				masterEnv = Math.Clamp((seconds - t) / 5.0, 0.0, 1.0);
			}
			float s = (float)((kick + pad) * masterEnv);
			data[f * 2] = s;
			data[f * 2 + 1] = s;
		}
		return data;
	}

	[Fact]
	public void LinkwitzRileyCrossover_LowPlusHigh_SumsToUnityMagnitude()
	{
		var crossover = new LinkwitzRileyCrossover(44100, 180.0);
		float[] sine = GenerateSineStereo(1.0, 180.0, 0.5f);
		double sumInSq = 0;
		double sumOutSq = 0;

		// Skip initial 1000-frame filter settling
		for (int f = 0; f < sine.Length / 2; f++)
		{
			float l = sine[f * 2];
			float r = sine[f * 2 + 1];
			crossover.SplitFrame(l, r, out float lowL, out float lowR, out float highL, out float highR);
			if (f > 2000)
			{
				sumInSq += l * l;
				double recombined = lowL + highL;
				sumOutSq += recombined * recombined;
			}
		}

		double ratioDb = 10.0 * Math.Log10(sumOutSq / sumInSq);
		// LR24 low + high sum at crossover frequency is theoretically 0.0 dB!
		Assert.InRange(ratioDb, -0.25, 0.25);
	}

	[Fact]
	public void AudioTrackAnalyzer_DetectsBpmLufsAndFadeProfile()
	{
		float[] beatAudio = GenerateBeatTrack(seconds: 22.0, bpm: 124.0, fadeOutTail: true);
		var provider = new SyntheticStereoProvider(beatAudio);

		TrackAnalysisResult result = AudioTrackAnalyzer.Analyze(provider);

		Assert.InRange(result.Bpm, 121.0, 127.0);
		Assert.True(result.BpmConfidence > 0.35, $"Expected strong BPM confidence, got {result.BpmConfidence}");
		Assert.InRange(result.LoudnessLufs, -26.0, -4.0);
		Assert.Equal(TrackEdgeProfile.Fade, result.OutroProfile);
		Assert.False(string.IsNullOrWhiteSpace(result.WaveformPeaksBase64));
		Assert.Equal(200, Convert.FromBase64String(result.WaveformPeaksBase64).Length);
	}

	[Fact]
	public void WsolaTimeStretcher_SlowDownAndSpeedUp_AdjustsDurationCleanly()
	{
		float[] audio = GenerateSineStereo(seconds: 2.0, freqHz: 440.0, amplitude: 0.4f);

		// Speed up 1.06x -> shorter output
		var fastStretcher = new WsolaTimeStretcher(new SyntheticStereoProvider(audio));
		fastStretcher.SetTempo(1.06);
		int fastSamples = DrainAndVerifyClean(fastStretcher);
		double fastSeconds = fastSamples / (2.0 * 44100.0);
		Assert.InRange(fastSeconds, 1.75, 1.98);

		// Slow down 0.94x -> longer output
		var slowStretcher = new WsolaTimeStretcher(new SyntheticStereoProvider(audio));
		slowStretcher.SetTempo(0.94);
		int slowSamples = DrainAndVerifyClean(slowStretcher);
		double slowSeconds = slowSamples / (2.0 * 44100.0);
		Assert.InRange(slowSeconds, 2.04, 2.25);
	}

	[Fact]
	public void Mp3GaplessHeaderParser_ExtractsLameEncoderDelayAndPadding()
	{
		// Construct minimal synthetic Xing + LAME header with delay=576 (0x240) and padding=1536 (0x600)
		byte[] fakeMp3 = new byte[512];
		Encoding.ASCII.GetBytes("Xing").CopyTo(fakeMp3, 40);
		Encoding.ASCII.GetBytes("LAME3.100").CopyTo(fakeMp3, 160);
		int delay = 576;
		int padding = 1536;
		fakeMp3[181] = (byte)(delay >> 4);
		fakeMp3[182] = (byte)(((delay & 0x0F) << 4) | ((padding >> 8) & 0x0F));
		fakeMp3[183] = (byte)(padding & 0xFF);

		using var ms = new MemoryStream(fakeMp3);
		Mp3GaplessInfo info = Mp3GaplessHeaderParser.Parse(ms);

		Assert.True(info.HasLameTag);
		Assert.Equal(576 + 529, info.EncoderDelaySamples);
		Assert.Equal(1536, info.EncoderPaddingSamples);
	}

	[Fact]
	public void TransitionPlanner_AutoMix_BeatSyncsCompatibleMusicAndProtectsStationIds()
	{
		var settings = TransitionSettings.FromAppSettings(new AppSettings());

		var songA = new TrackCueSnapshot("a.mp3", "Music", TimeSpan.FromSeconds(180), null, null, null, null,
			0, 172000, 179000, 122.0, 0.85, 250, 171500, -13.0, -0.8, TrackEdgeProfile.Ramp, TrackEdgeProfile.Rhythmic, 0, 0);
		var songB = new TrackCueSnapshot("b.mp3", "Music", TimeSpan.FromSeconds(190), null, null, null, null,
			0, 182000, 189000, 126.0, 0.90, 300, 181000, -14.5, -1.0, TrackEdgeProfile.Ramp, TrackEdgeProfile.Fade, 0, 0);
		var stationId = new TrackCueSnapshot("id.mp3", "StationId", TimeSpan.FromSeconds(6), null, null, null, null,
			0, 5800, 6000, 124.0, 0.80, 0, 5800, -14.0, -1.0, TrackEdgeProfile.Cold, TrackEdgeProfile.Cold, 0, 0);

		TransitionPlan musicPlan = TransitionPlanner.ComputePlan(songA, songB, settings);
		Assert.Equal("AutoMixBeatSync", musicPlan.AppliedStrategy);
		Assert.True(musicPlan.UseBeatSync);
		Assert.True(musicPlan.UseBassSwapEq);
		Assert.InRange(musicPlan.CrossfadeDuration.TotalSeconds, 3.0, 10.0);

		TransitionPlan idPlan = TransitionPlanner.ComputePlan(songA, stationId, settings);
		Assert.Equal("VoiceCleanBreak", idPlan.AppliedStrategy);
		Assert.False(idPlan.UseBeatSync);
		Assert.True(idPlan.CrossfadeDuration.TotalSeconds <= 0.25);

		TransitionPlan manualPlan = TransitionPlanner.ComputePlan(songA, songB, settings, isManualSkip: true, currentPosition: TimeSpan.FromSeconds(45));
		Assert.Equal("ManualCrossfade", manualPlan.AppliedStrategy);
		Assert.Equal(TimeSpan.FromSeconds(45), manualPlan.OutgoingMixStart);
	}

	[Fact]
	public void AutoMixSampleProvider_ExecutesSeamlessTransitionAndRaisesTrackStarted()
	{
		float[] aData = GenerateSineStereo(4.0, 220.0, 0.4f);
		float[] bData = GenerateSineStereo(4.0, 440.0, 0.4f);
		var pA = new SyntheticStereoProvider(aData);
		var pB = new SyntheticStereoProvider(bData);

		var cueA = new TrackCueSnapshot("a.wav", "Music", TimeSpan.FromSeconds(4), null, null, null, null,
			0, 3000, 4000, 120.0, 0.9, 0, 3000, -14.0, -1.0, TrackEdgeProfile.Ramp, TrackEdgeProfile.Fade, 0, 0);
		var cueB = new TrackCueSnapshot("b.wav", "Music", TimeSpan.FromSeconds(4), null, null, null, null,
			0, 3000, 4000, 122.0, 0.9, 0, 3000, -14.0, -1.0, TrackEdgeProfile.Ramp, TrackEdgeProfile.Fade, 0, 0);

		var autoMix = new AutoMixSampleProvider();
		autoMix.UpdateSettings(TransitionSettings.FromAppSettings(new AppSettings
		{
			TransitionMode = TransitionMode.SmartSegue,
			AutoMixBassSwapEnabled = true
		}));

		string? startedTrack = null;
		autoMix.PreloadedTrackStarted += (path, _) => startedTrack = path;

		autoMix.SetCurrent(new ManagedTrackStream(cueA, pA, seekAction: pA.Seek));
		autoMix.SetNext(new ManagedTrackStream(cueB, pB, seekAction: pB.Seek));

		int totalSamples = DrainAndVerifyClean(autoMix);
		Assert.Equal("b.wav", startedTrack);
		// Crossfade means total duration is strictly less than 4 + 4 = 8 seconds, and strictly > 5 seconds
		double totalSec = totalSamples / (2.0 * 44100.0);
		Assert.InRange(totalSec, 5.5, 7.9);
	}

	[Fact]
	public void MasterDspProcessor_EnforcesTruePeakCeilingAndDecouplesMonitorMuteFromBroadcastTap()
	{
		// Overloaded +12 dB sine wave (amplitude 4.0f!)
		float[] hotAudio = GenerateSineStereo(0.5, 1000.0, 4.0f);
		var source = new SyntheticStereoProvider(hotAudio);

		long broadcastBytesCaptured = 0;
		short broadcastMaxSample = 0;
		var dsp = new MasterDspProcessor(source, pcm =>
		{
			broadcastBytesCaptured += pcm.Length;
			for (int i = 0; i + 1 < pcm.Length; i += 2)
			{
				short s = (short)(pcm[i] | (pcm[i + 1] << 8));
				if (Math.Abs(s) > broadcastMaxSample) broadcastMaxSample = (short)Math.Min(short.MaxValue, Math.Abs((int)s));
			}
		});
		dsp.Configure(masterGainDb: 0.0, limiterEnabled: true, truePeakCeilingDb: -1.0, silenceThresholdDb: -50.0);

		// Mute local studio monitor completely!
		var monitor = new LocalMonitorVolumeProvider(dsp);
		monitor.SetMonitorState(volume: 0.0, muted: true);

		float[] buf = new float[2048];
		float maxMonitorSample = 0f;
		int read;
		int block = 0;
		while ((read = monitor.Read(buf, 0, buf.Length)) > 0)
		{
			block++;
			if (block > 6) // After smooth mute ramp finishes
			{
				for (int i = 0; i < read; i++)
				{
					maxMonitorSample = Math.Max(maxMonitorSample, Math.Abs(buf[i]));
				}
			}
		}

		// Local monitor is silent, while Broadcast Tap still received full limited audio under -1.0 dBTP!
		Assert.True(maxMonitorSample < 0.01f, $"Monitor should be muted, got {maxMonitorSample}");
		Assert.True(broadcastBytesCaptured > 80000);
		float ceilingLinear = AudioTrackAnalyzer.DbToLinear(-1.0);
		Assert.True(broadcastMaxSample <= (short)(ceilingLinear * 32767.0) + 8, $"Limiter exceeded ceiling: {broadcastMaxSample}");
		Assert.True(dsp.LatestTelemetry.LimiterGainReductionDb > 6.0);
	}

	[Fact]
	public void MasterDspProcessor_TracksRealAudioSilenceForDeadAirWatchdog()
	{
		float[] silence = new float[44100 * 2 * 2]; // 2.0 seconds of digital zero
		var dsp = new MasterDspProcessor(new SyntheticStereoProvider(silence));
		dsp.Configure(0.0, true, -1.0, silenceThresholdDb: -50.0);

		float[] buf = new float[4096];
		while (dsp.Read(buf, 0, buf.Length) > 0) { }

		Assert.InRange(dsp.LatestTelemetry.ConsecutiveSilenceSeconds, 1.9, 2.1);
	}

	[Fact]
	public void TransitionPlanner_AutoMix_MatchesDoubleTimeAndHalfTimeBpms()
	{
		var settings = TransitionSettings.FromAppSettings(new AppSettings());
		var halfTime = new TrackCueSnapshot("half.mp3", "Music", TimeSpan.FromSeconds(180), null, null, null, null,
			0, 172000, 179000, 64.0, 0.80, 200, 171500, -14.0, -1.0, TrackEdgeProfile.Ramp, TrackEdgeProfile.Fade, 0, 0);
		var fullTime = new TrackCueSnapshot("full.mp3", "Music", TimeSpan.FromSeconds(180), null, null, null, null,
			0, 172000, 179000, 128.0, 0.85, 200, 171500, -14.0, -1.0, TrackEdgeProfile.Ramp, TrackEdgeProfile.Fade, 0, 0);

		TransitionPlan plan = TransitionPlanner.ComputePlan(halfTime, fullTime, settings);
		Assert.Equal("AutoMixBeatSync", plan.AppliedStrategy);
		Assert.True(plan.UseBeatSync);
		Assert.InRange(plan.OutgoingMixTempo, 0.99, 1.01);
		Assert.InRange(plan.IncomingStartTempo, 0.99, 1.01);
	}

	[Fact]
	public void AutoMixSampleProvider_TrueGapless_ZeroOverlapExactTotalLength()
	{
		float[] aData = GenerateSineStereo(2.0, 220.0, 0.4f);
		float[] bData = GenerateSineStereo(2.0, 440.0, 0.4f);
		var pA = new SyntheticStereoProvider(aData);
		var pB = new SyntheticStereoProvider(bData);

		var cueA = new TrackCueSnapshot("a.wav", "Music", TimeSpan.FromSeconds(2), 0, null, null, 2000,
			0, 2000, 2000, null, null, null, null, -14.0, -1.0, TrackEdgeProfile.Cold, TrackEdgeProfile.Cold, 0, 0);
		var cueB = new TrackCueSnapshot("b.wav", "Music", TimeSpan.FromSeconds(2), 0, null, null, 2000,
			0, 2000, 2000, null, null, null, null, -14.0, -1.0, TrackEdgeProfile.Cold, TrackEdgeProfile.Cold, 0, 0);

		var autoMix = new AutoMixSampleProvider();
		autoMix.UpdateSettings(TransitionSettings.FromAppSettings(new AppSettings
		{
			TransitionMode = TransitionMode.TrueGapless
		}));

		autoMix.SetCurrent(new ManagedTrackStream(cueA, pA, seekAction: pA.Seek));
		autoMix.SetNext(new ManagedTrackStream(cueB, pB, seekAction: pB.Seek));

		int totalSamples = DrainAndVerifyClean(autoMix);
		double totalSec = totalSamples / (2.0 * 44100.0);
		// True Gapless 0ms seam: 2.0s + 2.0s = 4.00s exact!
		Assert.InRange(totalSec, 3.98, 4.02);
	}

	[Fact]
	public void AutoMixSampleProvider_ManualCrossfadeMidTrack_TransitionsImmediatelyAndSmoothly()
	{
		float[] aData = GenerateSineStereo(10.0, 220.0, 0.4f);
		float[] bData = GenerateSineStereo(3.0, 440.0, 0.4f);
		var pA = new SyntheticStereoProvider(aData);
		var pB = new SyntheticStereoProvider(bData);

		var cueA = new TrackCueSnapshot("a.wav", "Music", TimeSpan.FromSeconds(10), 0, null, null, 10000,
			0, 9000, 10000, null, null, null, null, -14.0, -1.0, TrackEdgeProfile.Cold, TrackEdgeProfile.Fade, 0, 0);
		var cueB = new TrackCueSnapshot("b.wav", "Music", TimeSpan.FromSeconds(3), 0, null, null, 3000,
			0, 2500, 3000, null, null, null, null, -14.0, -1.0, TrackEdgeProfile.Cold, TrackEdgeProfile.Fade, 0, 0);

		var autoMix = new AutoMixSampleProvider();
		autoMix.UpdateSettings(TransitionSettings.FromAppSettings(new AppSettings { ManualSkipCrossfadeMs = 500 }));
		autoMix.SetCurrent(new ManagedTrackStream(cueA, pA, seekAction: pA.Seek));
		autoMix.SetNext(new ManagedTrackStream(cueB, pB, seekAction: pB.Seek));

		// Read 1 second of track A, then manually trigger smooth crossfade!
		float[] warm = new float[44100 * 2];
		autoMix.Read(warm, 0, warm.Length);

		string? started = null;
		autoMix.PreloadedTrackStarted += (path, _) => started = path;
		bool triggered = autoMix.TriggerManualCrossfade();
		Assert.True(triggered);
		Assert.Equal("b.wav", started);

		int remainingSamples = DrainAndVerifyClean(autoMix);
		double remainingSec = remainingSamples / (2.0 * 44100.0);
		Assert.InRange(remainingSec, 2.8, 3.2);
	}

	[Fact]
	public void BroadcastMixerBus_DucksProgramAudioWhileCartwallOverlayPlays()
	{
		float[] programData = GenerateSineStereo(1.0, 220.0, 0.5f);
		float[] cartSilenceOverlay = new float[44100 * 2 / 2]; // 0.5s overlay with DuckProgram=true (-12 dB)
		var mixer = new BroadcastMixerBus(new SyntheticStereoProvider(programData));
		mixer.AddOverlay(new OverlayChannel("cart:0", new SyntheticStereoProvider(cartSilenceOverlay), 1.0f, duckProgram: true, duckDb: -12.0));

		float[] buf = new float[44100 * 2]; // read full 1.0s
		int read = mixer.Read(buf, 0, buf.Length);
		Assert.Equal(buf.Length, read);

		// Measure peak around 0.25s (while ducked) vs 0.90s (after overlay ended & released)
		float duckedPeak = 0f;
		for (int i = 10000 * 2; i < 15000 * 2; i++) duckedPeak = Math.Max(duckedPeak, Math.Abs(buf[i]));

		float releasedPeak = 0f;
		for (int i = 39000 * 2; i < 43000 * 2; i++) releasedPeak = Math.Max(releasedPeak, Math.Abs(buf[i]));

		Assert.True(duckedPeak < 0.20f, $"Program should be ducked ~-12 dB (~0.125), got {duckedPeak}");
		Assert.True(releasedPeak > 0.38f, $"Program should recover after cart ends, got {releasedPeak}");
	}

	[Fact]
	public void AutoMixSampleProvider_SongToSweeper_PreservesFullSongDurationWithFullPunchEntry()
	{
		// 10s rhythmic song (AutoMixOutMs at 3.5s = -6.5s early) -> 4s sweeper
		float[] songData = GenerateSineStereo(10.0, 220.0, 0.4f);
		float[] sweeperData = GenerateSineStereo(4.0, 880.0, 0.5f);
		var pSong = new SyntheticStereoProvider(songData);
		var pSwp = new SyntheticStereoProvider(sweeperData);

		var cueSong = new TrackCueSnapshot("song.wav", "Music", TimeSpan.FromSeconds(10), null, null, null, null,
			0, 3500, 10000, 120.0, 0.9, 0, 3500, -14.0, -1.0, TrackEdgeProfile.Ramp, TrackEdgeProfile.Rhythmic, 0, 0);
		var cueSwp = new TrackCueSnapshot("swp.wav", "Sweeper", TimeSpan.FromSeconds(4), null, null, null, null,
			0, 3000, 4000, null, null, null, null, -14.0, -1.0, TrackEdgeProfile.Cold, TrackEdgeProfile.Fade, 0, 0);

		var autoMix = new AutoMixSampleProvider();
		autoMix.UpdateSettings(TransitionSettings.FromAppSettings(new AppSettings
		{
			TransitionMode = TransitionMode.AutoMix,
			GaplessPlaybackEnabled = true
		}));

		TransitionPlan? executedPlan = null;
		autoMix.PreloadedTrackStarted += (_, plan) => executedPlan = plan;

		autoMix.SetCurrent(new ManagedTrackStream(cueSong, pSong, seekAction: pSong.Seek));
		autoMix.SetNext(new ManagedTrackStream(cueSwp, pSwp, seekAction: pSwp.Seek));

		int totalSamples = DrainAndVerifyClean(autoMix);
		double totalSec = totalSamples / (2.0 * 44100.0);

		Assert.NotNull(executedPlan);
		Assert.Equal("SweeperEntry", executedPlan!.AppliedStrategy);
		Assert.True(executedPlan.FullPunchIncoming);
		// Full 10s song + 4s sweeper minus only ~0.25s end-kiss = ~13.75s (NOT 7.5s!)
		Assert.InRange(totalSec, 13.5, 14.0);
	}

	private static int DrainAndVerifyClean(ISampleProvider provider)
	{
		float[] buf = new float[2048];
		int total = 0;
		int read;
		while ((read = provider.Read(buf, 0, buf.Length)) > 0)
		{
			for (int i = 0; i < read; i++)
			{
				Assert.False(float.IsNaN(buf[i]), "DSP emitted NaN sample");
				Assert.False(float.IsInfinity(buf[i]), "DSP emitted Infinity sample");
			}
			total += read;
			if (total > 44100 * 2 * 30) break; // safety guard
		}
		return total;
	}
}
