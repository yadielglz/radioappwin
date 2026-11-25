using System;
using System.Collections.Generic;
using NAudio.Wave;
using RadioApp.Models;

namespace RadioApp.Audio;

public sealed class TrackAnalysisResult
{
	public TimeSpan Duration { get; set; }
	public double LoudnessLufs { get; set; } = -14.0;
	public double TruePeakDb { get; set; } = -1.0;
	public double Bpm { get; set; }
	public double BpmConfidence { get; set; }
	public int FirstBeatMs { get; set; }
	public int OutroBeatMs { get; set; }
	public int AutoCueInMs { get; set; }
	public int AutoMixOutMs { get; set; }
	public int AutoCueOutMs { get; set; }
	public TrackEdgeProfile IntroProfile { get; set; } = TrackEdgeProfile.Unknown;
	public TrackEdgeProfile OutroProfile { get; set; } = TrackEdgeProfile.Unknown;
	public int EncoderDelaySamples { get; set; }
	public int EncoderPaddingSamples { get; set; }
	public string WaveformPeaksBase64 { get; set; } = string.Empty;

	public void ApplyTo(LibraryItem item)
	{
		item.LoudnessLufs = Math.Round(LoudnessLufs, 2);
		item.TruePeakDb = Math.Round(TruePeakDb, 2);
		item.Bpm = Bpm > 0 ? Math.Round(Bpm, 1) : null;
		item.BpmConfidence = Math.Round(BpmConfidence, 3);
		item.FirstBeatMs = FirstBeatMs;
		item.OutroBeatMs = OutroBeatMs;
		item.AutoCueInMs = AutoCueInMs;
		item.AutoMixOutMs = AutoMixOutMs;
		item.AutoCueOutMs = AutoCueOutMs;
		item.IntroProfile = IntroProfile;
		item.OutroProfile = OutroProfile;
		item.EncoderDelaySamples = EncoderDelaySamples;
		item.EncoderPaddingSamples = EncoderPaddingSamples;
		item.WaveformPeaksBase64 = WaveformPeaksBase64;
		item.AnalysisVersion = AppVersionInfo.CurrentAnalysisVersion;
		if (Duration > TimeSpan.Zero)
		{
			item.Duration = Duration;
		}
	}
}

/// <summary>
/// Single-pass streaming broadcast audio analyzer.
/// Computes EBU R128 integrated LUFS, sample/true peak, 50ms RMS energy envelope,
/// intro/outro acoustic profiles (Cold/Ramp/Fade/Rhythmic), BPM + beat-phase grid,
/// and a 200-bar waveform preview without loading the full decoded file into memory.
/// </summary>
public static class AudioTrackAnalyzer
{
	private const int WaveformBins = 200;
	private const double RmsWindowSeconds = 0.05; // 50ms RMS frames
	private const double OnsetHopSeconds = 0.01;  // 10ms onset flux hop for BPM detection

	public static TrackAnalysisResult Analyze(ISampleProvider stereoProvider, double silenceThresholdDb = -44.0)
	{
		int sampleRate = stereoProvider.WaveFormat.SampleRate;
		int channels = stereoProvider.WaveFormat.Channels;
		if (channels != 2)
		{
			throw new ArgumentException("AudioTrackAnalyzer expects a 2-channel stereo ISampleProvider.", nameof(stereoProvider));
		}

		var kFilter = new EbuR128KWeightingFilter(sampleRate);
		var bassFilter = new StereoBiquadFilter();
		bassFilter.ConfigureLowPass(sampleRate, 200.0);

		int rmsBlockFrames = Math.Max(1, (int)Math.Round(sampleRate * RmsWindowSeconds));
		int onsetHopFrames = Math.Max(1, (int)Math.Round(sampleRate * OnsetHopSeconds));
		int lufsSubBlockFrames = Math.Max(1, (int)Math.Round(sampleRate * 0.1)); // 100ms sub-blocks -> 4 sub-blocks = 400ms EBU R128 gating block

		var rmsDbFrames = new List<double>(4096);
		var lufsSubBlockMeans = new List<double>(2048);
		var onsetStrengths = new List<float>(16384);
		var coarsePeaks = new List<float>(4096);

		double rmsSumSq = 0.0;
		int rmsCount = 0;

		double lufsSubSumSq = 0.0;
		int lufsSubCount = 0;

		double onsetBassSumSq = 0.0;
		double onsetFullSumSq = 0.0;
		int onsetCount = 0;
		double prevOnsetEnergy = 0.0;

		float coarsePeak = 0f;
		float globalPeak = 1e-6f;
		long totalFrames = 0;

		float[] readBuffer = new float[8192];
		while (true)
		{
			int samplesRead = stereoProvider.Read(readBuffer, 0, readBuffer.Length);
			if (samplesRead <= 0)
			{
				break;
			}

			int framesRead = samplesRead / 2;
			for (int f = 0; f < framesRead; f++)
			{
				float l = readBuffer[f * 2];
				float r = readBuffer[f * 2 + 1];

				float absPeak = Math.Max(Math.Abs(l), Math.Abs(r));
				if (absPeak > globalPeak) globalPeak = absPeak;
				if (absPeak > coarsePeak) coarsePeak = absPeak;

				double monoSq = 0.5 * (l * l + r * r);
				rmsSumSq += monoSq;
				rmsCount++;

				kFilter.ProcessFrame(l, r, out double kL, out double kR);
				// BS.1770 channel-summed mean square (stereo channel weights = 1.0 each)
				lufsSubSumSq += (kL * kL + kR * kR);
				lufsSubCount++;

				bassFilter.ProcessFrame(l, r, out double bL, out double bR);
				onsetBassSumSq += 0.5 * (bL * bL + bR * bR);
				onsetFullSumSq += monoSq;
				onsetCount++;

				totalFrames++;

				if (rmsCount >= rmsBlockFrames)
				{
					double rms = Math.Sqrt(rmsSumSq / rmsCount);
					rmsDbFrames.Add(LinearToDb(rms));
					coarsePeaks.Add(coarsePeak);
					rmsSumSq = 0.0;
					rmsCount = 0;
					coarsePeak = 0f;
				}

				if (lufsSubCount >= lufsSubBlockFrames)
				{
					lufsSubBlockMeans.Add(lufsSubSumSq / lufsSubCount);
					lufsSubSumSq = 0.0;
					lufsSubCount = 0;
				}

				if (onsetCount >= onsetHopFrames)
				{
					// Weight low-frequency percussive energy (kick/bass) + full-band transient energy
					double bassRms = Math.Sqrt(onsetBassSumSq / onsetCount);
					double fullRms = Math.Sqrt(onsetFullSumSq / onsetCount);
					double combinedEnergy = 0.65 * bassRms + 0.35 * fullRms;
					double flux = Math.Max(0.0, combinedEnergy - prevOnsetEnergy);
					prevOnsetEnergy = prevOnsetEnergy * 0.6 + combinedEnergy * 0.4;
					onsetStrengths.Add((float)flux);
					onsetBassSumSq = 0.0;
					onsetFullSumSq = 0.0;
					onsetCount = 0;
				}
			}
		}

		// Flush trailing partial blocks
		if (rmsCount > 0)
		{
			double rms = Math.Sqrt(rmsSumSq / rmsCount);
			rmsDbFrames.Add(LinearToDb(rms));
			coarsePeaks.Add(coarsePeak);
		}
		if (lufsSubCount > 0)
		{
			lufsSubBlockMeans.Add(lufsSubSumSq / lufsSubCount);
		}

		TimeSpan duration = TimeSpan.FromSeconds((double)totalFrames / sampleRate);
		double integratedLufs = ComputeEbuR128IntegratedLufs(lufsSubBlockMeans);
		double truePeakDb = LinearToDb(globalPeak);

		var (cueInMs, mixOutMs, cueOutMs, introProfile, outroProfile) =
			AnalyzeEnergyEnvelope(rmsDbFrames, duration, integratedLufs, silenceThresholdDb);

		var (bpm, confidence, firstBeatMs, outroBeatMs) =
			EstimateBpmAndPhase(onsetStrengths, duration, cueInMs, mixOutMs);

		string waveformBase64 = EncodeWaveformBins(coarsePeaks, WaveformBins);

		return new TrackAnalysisResult
		{
			Duration = duration,
			LoudnessLufs = integratedLufs,
			TruePeakDb = truePeakDb,
			Bpm = bpm,
			BpmConfidence = confidence,
			FirstBeatMs = firstBeatMs,
			OutroBeatMs = outroBeatMs,
			AutoCueInMs = cueInMs,
			AutoMixOutMs = mixOutMs,
			AutoCueOutMs = cueOutMs,
			IntroProfile = introProfile,
			OutroProfile = outroProfile,
			WaveformPeaksBase64 = waveformBase64
		};
	}

	private static double ComputeEbuR128IntegratedLufs(List<double> subBlocks100ms)
	{
		if (subBlocks100ms.Count == 0)
		{
			return -24.0;
		}

		// Build overlapping 400ms blocks (4 consecutive 100ms sub-blocks, 75% overlap)
		var blockPowers = new List<double>(subBlocks100ms.Count);
		if (subBlocks100ms.Count < 4)
		{
			double sum = 0;
			foreach (double v in subBlocks100ms) sum += v;
			blockPowers.Add(sum / subBlocks100ms.Count);
		}
		else
		{
			for (int i = 0; i <= subBlocks100ms.Count - 4; i++)
			{
				double mean = 0.25 * (subBlocks100ms[i] + subBlocks100ms[i + 1] + subBlocks100ms[i + 2] + subBlocks100ms[i + 3]);
				blockPowers.Add(mean);
			}
		}

		// Absolute gate: -70 LUFS => power > 10^((-70 + 0.691)/10)
		double absGatePower = Math.Pow(10.0, (-70.0 + 0.691) / 10.0);
		double absSum = 0.0;
		int absCount = 0;
		foreach (double p in blockPowers)
		{
			if (p > absGatePower)
			{
				absSum += p;
				absCount++;
			}
		}
		if (absCount == 0)
		{
			return -40.0;
		}

		double ungatedLufs = -0.691 + 10.0 * Math.Log10(absSum / absCount);
		// Relative gate: -10 LU below ungated loudness
		double relGateLufs = ungatedLufs - 10.0;
		double relGatePower = Math.Pow(10.0, (relGateLufs + 0.691) / 10.0);

		double relSum = 0.0;
		int relCount = 0;
		foreach (double p in blockPowers)
		{
			if (p > absGatePower && p > relGatePower)
			{
				relSum += p;
				relCount++;
			}
		}

		if (relCount == 0)
		{
			return Math.Clamp(ungatedLufs, -60.0, 0.0);
		}

		double integrated = -0.691 + 10.0 * Math.Log10(relSum / relCount);
		return Math.Clamp(integrated, -60.0, 0.0);
	}

	private static (int CueInMs, int MixOutMs, int CueOutMs, TrackEdgeProfile Intro, TrackEdgeProfile Outro)
		AnalyzeEnergyEnvelope(List<double> rmsDb, TimeSpan duration, double integratedLufs, double silenceThresholdDb)
	{
		int totalMs = (int)Math.Round(duration.TotalMilliseconds);
		if (rmsDb.Count < 4 || totalMs < 500)
		{
			return (0, totalMs, totalMs, TrackEdgeProfile.Cold, TrackEdgeProfile.Cold);
		}

		double frameMs = RmsWindowSeconds * 1000.0;
		double effectiveSilenceDb = Math.Clamp(silenceThresholdDb, -66.0, -24.0);
		// Perceptual mix-out energy floor relative to track body loudness
		double mixTriggerDb = Math.Clamp(integratedLufs - 16.0, effectiveSilenceDb + 6.0, -18.0);
		double bodyEnergyDb = Math.Clamp(integratedLufs - 4.0, -34.0, -6.0);

		// 1. Sustained CueIn: require 2 consecutive 50ms frames above silence threshold (ignores single-sample clicks)
		int cueInFrame = 0;
		int maxHeadFrame = Math.Min(rmsDb.Count - 2, (int)(15000.0 / frameMs));
		for (int i = 0; i < maxHeadFrame; i++)
		{
			if (rmsDb[i] >= effectiveSilenceDb && rmsDb[i + 1] >= effectiveSilenceDb)
			{
				cueInFrame = Math.Max(0, i - 1);
				break;
			}
		}
		int cueInMs = (int)Math.Round(cueInFrame * frameMs);

		// 2. Sustained CueOut: walk backward from end to find last sustained audible frame
		int cueOutFrame = rmsDb.Count - 1;
		int minTailFrame = Math.Max(cueInFrame + 4, rmsDb.Count - (int)(35000.0 / frameMs));
		for (int i = rmsDb.Count - 1; i >= minTailFrame; i--)
		{
			if (rmsDb[i] >= effectiveSilenceDb)
			{
				cueOutFrame = Math.Min(rmsDb.Count - 1, i + 2);
				break;
			}
		}
		int cueOutMs = Math.Min(totalMs, (int)Math.Round((cueOutFrame + 1) * frameMs));

		// 3. Classify Intro profile (first 4 seconds after CueIn)
		TrackEdgeProfile introProfile = ClassifyIntro(rmsDb, cueInFrame, bodyEnergyDb, frameMs);

		// 4. Locate optimal MixOut point & classify Outro profile (last 25 seconds before CueOut)
		int tailScanStart = Math.Max(cueInFrame + 10, cueOutFrame - (int)(25000.0 / frameMs));
		int mixOutFrame = cueOutFrame;
		for (int i = cueOutFrame; i >= tailScanStart; i--)
		{
			if (rmsDb[i] >= mixTriggerDb)
			{
				mixOutFrame = Math.Min(cueOutFrame, i + 2);
				break;
			}
		}

		TrackEdgeProfile outroProfile = ClassifyOutro(rmsDb, cueOutFrame, mixOutFrame, bodyEnergyDb, frameMs);
		int mixOutMs = Math.Clamp((int)Math.Round(mixOutFrame * frameMs), cueInMs + 250, cueOutMs);

		// For long rhythmic outros that maintain energy, allow AutoMix to pull MixOut up to 6s earlier
		if (outroProfile == TrackEdgeProfile.Rhythmic && cueOutMs - cueInMs > 30000)
		{
			int earlierMix = Math.Max(cueInMs + 15000, cueOutMs - 6500);
			mixOutMs = Math.Min(mixOutMs, earlierMix);
		}

		return (cueInMs, mixOutMs, cueOutMs, introProfile, outroProfile);
	}

	private static TrackEdgeProfile ClassifyIntro(List<double> rmsDb, int cueInFrame, double bodyEnergyDb, double frameMs)
	{
		int halfSecFrames = Math.Max(2, (int)(500.0 / frameMs));
		int fourSecFrames = Math.Max(8, (int)(4000.0 / frameMs));
		if (cueInFrame + halfSecFrames >= rmsDb.Count)
		{
			return TrackEdgeProfile.Cold;
		}

		double immediatePeak = double.NegativeInfinity;
		for (int i = cueInFrame; i < Math.Min(rmsDb.Count, cueInFrame + halfSecFrames); i++)
		{
			if (rmsDb[i] > immediatePeak) immediatePeak = rmsDb[i];
		}

		if (immediatePeak >= bodyEnergyDb - 3.5)
		{
			return TrackEdgeProfile.Cold;
		}

		// Measure rise time to near-body level
		int reachFrame = -1;
		int limit = Math.Min(rmsDb.Count, cueInFrame + fourSecFrames);
		for (int i = cueInFrame; i < limit; i++)
		{
			if (rmsDb[i] >= bodyEnergyDb - 4.0)
			{
				reachFrame = i;
				break;
			}
		}

		if (reachFrame < 0)
		{
			return TrackEdgeProfile.Fade;
		}

		double riseMs = (reachFrame - cueInFrame) * frameMs;
		return riseMs <= 800.0 ? TrackEdgeProfile.Cold : TrackEdgeProfile.Ramp;
	}

	private static TrackEdgeProfile ClassifyOutro(List<double> rmsDb, int cueOutFrame, int mixOutFrame, double bodyEnergyDb, double frameMs)
	{
		int fadeTailFrames = cueOutFrame - mixOutFrame;
		double fadeTailSeconds = fadeTailFrames * frameMs / 1000.0;

		if (fadeTailSeconds >= 2.2)
		{
			return TrackEdgeProfile.Fade;
		}

		// Check slope across the final 6 seconds before CueOut
		int windowFrames = Math.Max(8, (int)(6000.0 / frameMs));
		int start = Math.Max(0, cueOutFrame - windowFrames);
		if (cueOutFrame - start < 12)
		{
			return TrackEdgeProfile.Cold;
		}

		int mid = (start + cueOutFrame) / 2;
		double firstHalfAvg = AverageDb(rmsDb, start, mid);
		double secondHalfAvg = AverageDb(rmsDb, mid, cueOutFrame);
		double dropDb = firstHalfAvg - secondHalfAvg;

		if (dropDb >= 6.5)
		{
			return TrackEdgeProfile.Fade;
		}

		// Check rhythmic variance in last 6 seconds
		double mean = AverageDb(rmsDb, start, cueOutFrame);
		double variance = 0.0;
		int count = cueOutFrame - start;
		for (int i = start; i < cueOutFrame; i++)
		{
			double d = rmsDb[i] - mean;
			variance += d * d;
		}
		double stdDev = Math.Sqrt(variance / Math.Max(1, count));

		if (secondHalfAvg >= bodyEnergyDb - 7.0 && stdDev >= 1.8 && fadeTailSeconds < 1.2)
		{
			return TrackEdgeProfile.Rhythmic;
		}

		return fadeTailSeconds < 1.4 ? TrackEdgeProfile.Cold : TrackEdgeProfile.Fade;
	}

	private static double AverageDb(List<double> values, int start, int end)
	{
		if (end <= start) return -60.0;
		double sum = 0.0;
		for (int i = start; i < end; i++) sum += values[i];
		return sum / (end - start);
	}

	private static (double Bpm, double Confidence, int FirstBeatMs, int OutroBeatMs)
		EstimateBpmAndPhase(List<float> onsets, TimeSpan duration, int cueInMs, int mixOutMs)
	{
		if (onsets.Count < 400 || duration.TotalSeconds < 12.0)
		{
			return (0.0, 0.0, cueInMs, mixOutMs);
		}

		// Analyze up to 90 seconds from the middle body of the track for clean tempo lock
		int hopRate = (int)Math.Round(1.0 / OnsetHopSeconds); // 100 Hz
		int maxAnalyzeFrames = Math.Min(onsets.Count, 90 * hopRate);
		int startIdx = Math.Max(0, (onsets.Count - maxAnalyzeFrames) / 2);
		int n = Math.Min(maxAnalyzeFrames, onsets.Count - startIdx);

		// Mean-subtract and normalize onset slice
		double mean = 0.0;
		for (int i = 0; i < n; i++) mean += onsets[startIdx + i];
		mean /= n;

		float[] centered = new float[n];
		double energy = 0.0;
		for (int i = 0; i < n; i++)
		{
			float v = Math.Max(0f, onsets[startIdx + i] - (float)(mean * 0.8));
			centered[i] = v;
			energy += v * v;
		}
		if (energy < 1e-6)
		{
			return (0.0, 0.0, cueInMs, mixOutMs);
		}

		// Autocorrelation over BPM range [65, 180]
		int minLag = (int)Math.Floor(60.0 * hopRate / 180.0); // ~33 (180 BPM)
		int maxLag = (int)Math.Ceiling(60.0 * hopRate / 65.0); // ~93 (65 BPM)
		double[] acf = new double[maxLag + 1];

		double bestScore = 0.0;
		int bestLag = 0;
		double scoreSum = 0.0;
		int scoreCount = 0;

		for (int lag = minLag; lag <= maxLag; lag++)
		{
			double sum = 0.0;
			int limit = n - lag;
			for (int i = 0; i < limit; i++)
			{
				sum += centered[i] * centered[i + lag];
			}
			// Harmonic reinforcement (2x beat period) for stronger pulse certainty
			double harmonic = 0.0;
			int lag2 = lag * 2;
			if (lag2 < n)
			{
				int lim2 = n - lag2;
				for (int i = 0; i < lim2; i++)
				{
					harmonic += centered[i] * centered[i + lag2];
				}
				harmonic /= Math.Max(1, lim2);
			}
			double baseCorr = sum / Math.Max(1, limit);
			double bpm = 60.0 * hopRate / lag;
			// Gentle perceptual tempo prior peaking around 120 BPM (prevents half/double-time errors)
			double prior = Math.Exp(-0.5 * Math.Pow(Math.Log(bpm / 120.0) / 0.42, 2.0));
			double score = (baseCorr + 0.5 * harmonic) * (0.7 + 0.3 * prior);
			acf[lag] = score;
			scoreSum += score;
			scoreCount++;
			if (score > bestScore)
			{
				bestScore = score;
				bestLag = lag;
			}
		}

		if (bestLag <= 0 || bestScore <= 1e-9)
		{
			return (0.0, 0.0, cueInMs, mixOutMs);
		}

		// Parabolic interpolation around bestLag for sub-frame BPM accuracy
		double refinedLag = bestLag;
		if (bestLag > minLag && bestLag < maxLag)
		{
			double y0 = acf[bestLag - 1];
			double y1 = acf[bestLag];
			double y2 = acf[bestLag + 1];
			double denom = (y0 - 2.0 * y1 + y2);
			if (Math.Abs(denom) > 1e-12)
			{
				double delta = Math.Clamp(0.5 * (y0 - y2) / denom, -0.5, 0.5);
				refinedLag = bestLag + delta;
			}
		}

		double detectedBpm = 60.0 * hopRate / refinedLag;
		double avgScore = scoreSum / Math.Max(1, scoreCount);
		double ratio = bestScore / Math.Max(1e-9, avgScore);
		double confidence = Math.Clamp((ratio - 1.15) / 2.2, 0.0, 1.0);

		// Estimate beat phase in intro window (first 20s after CueIn) and outro window (last 20s before MixOut)
		int firstBeatMs = FindBeatAnchorMs(onsets, hopRate, refinedLag, cueInMs, Math.Min((int)duration.TotalMilliseconds, cueInMs + 20000), preferEarliest: true);
		int outroBeatMs = FindBeatAnchorMs(onsets, hopRate, refinedLag, Math.Max(cueInMs, mixOutMs - 20000), mixOutMs, preferEarliest: false);

		return (detectedBpm, confidence, firstBeatMs, outroBeatMs);
	}

	private static int FindBeatAnchorMs(List<float> onsets, int hopRate, double beatLag, int windowStartMs, int windowEndMs, bool preferEarliest)
	{
		int start = Math.Clamp((int)(windowStartMs / 1000.0 * hopRate), 0, Math.Max(0, onsets.Count - 1));
		int end = Math.Clamp((int)(windowEndMs / 1000.0 * hopRate), start, onsets.Count);
		int period = Math.Max(1, (int)Math.Round(beatLag));
		if (end - start < period * 2)
		{
			return preferEarliest ? windowStartMs : windowEndMs;
		}

		// Find phase offset [0..period-1] maximizing periodic onset alignment
		double bestPhaseScore = -1.0;
		int bestPhase = 0;
		for (int phase = 0; phase < period; phase++)
		{
			double score = 0.0;
			int hits = 0;
			for (double idx = start + phase; idx < end; idx += beatLag)
			{
				int i = (int)Math.Round(idx);
				if (i >= 0 && i < onsets.Count)
				{
					score += onsets[i];
					hits++;
				}
			}
			if (hits > 0 && score / hits > bestPhaseScore)
			{
				bestPhaseScore = score / hits;
				bestPhase = phase;
			}
		}

		if (preferEarliest)
		{
			return (int)Math.Round((start + bestPhase) * 1000.0 / hopRate);
		}

		// Latest aligned beat <= end
		double lastBeat = start + bestPhase;
		while (lastBeat + beatLag <= end)
		{
			lastBeat += beatLag;
		}
		return (int)Math.Round(lastBeat * 1000.0 / hopRate);
	}

	private static string EncodeWaveformBins(List<float> coarsePeaks, int binCount)
	{
		if (coarsePeaks.Count == 0)
		{
			return string.Empty;
		}

		float maxPeak = 0.001f;
		foreach (float p in coarsePeaks)
		{
			if (p > maxPeak) maxPeak = p;
		}

		byte[] bins = new byte[binCount];
		for (int b = 0; b < binCount; b++)
		{
			int from = (int)((long)b * coarsePeaks.Count / binCount);
			int to = Math.Max(from + 1, (int)((long)(b + 1) * coarsePeaks.Count / binCount));
			float peak = 0f;
			for (int i = from; i < to && i < coarsePeaks.Count; i++)
			{
				if (coarsePeaks[i] > peak) peak = coarsePeaks[i];
			}
			double normalized = Math.Clamp(peak / maxPeak, 0.0, 1.0);
			// Mild visual compression so quieter sections remain readable
			double visual = Math.Pow(normalized, 0.72);
			bins[b] = (byte)Math.Clamp((int)Math.Round(visual * 255.0), 0, 255);
		}
		return Convert.ToBase64String(bins);
	}

	public static double LinearToDb(double linear)
	{
		return 20.0 * Math.Log10(Math.Max(1e-6, linear));
	}

	public static float DbToLinear(double db)
	{
		return (float)Math.Pow(10.0, db / 20.0);
	}
}
