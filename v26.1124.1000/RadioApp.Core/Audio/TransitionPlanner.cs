using System;
using RadioApp.Models;

namespace RadioApp.Audio;

public sealed record TrackCueSnapshot(
	string FilePath,
	string Category,
	TimeSpan Duration,
	int? StartCueMs,
	int? IntroCueMs,
	int? OutroCueMs,
	int? EndCueMs,
	int? AutoCueInMs,
	int? AutoMixOutMs,
	int? AutoCueOutMs,
	double? Bpm,
	double? BpmConfidence,
	int? FirstBeatMs,
	int? OutroBeatMs,
	double? LoudnessLufs,
	double? TruePeakDb,
	TrackEdgeProfile IntroProfile,
	TrackEdgeProfile OutroProfile,
	int EncoderDelaySamples,
	int EncoderPaddingSamples)
{
	public bool IsSweeper =>
		string.Equals(Category, "Sweeper", StringComparison.OrdinalIgnoreCase) ||
		string.Equals(Category, "Sweep", StringComparison.OrdinalIgnoreCase) ||
		string.Equals(Category, "Jingle", StringComparison.OrdinalIgnoreCase);

	public bool IsStationId =>
		string.Equals(Category, "StationId", StringComparison.OrdinalIgnoreCase) ||
		string.Equals(Category, "Station ID", StringComparison.OrdinalIgnoreCase) ||
		string.Equals(Category, "ID", StringComparison.OrdinalIgnoreCase);

	public bool IsCommercial =>
		string.Equals(Category, "Commercial", StringComparison.OrdinalIgnoreCase) ||
		string.Equals(Category, "Commercials", StringComparison.OrdinalIgnoreCase) ||
		string.Equals(Category, "Ad", StringComparison.OrdinalIgnoreCase) ||
		string.Equals(Category, "Ads", StringComparison.OrdinalIgnoreCase) ||
		string.Equals(Category, "Promo", StringComparison.OrdinalIgnoreCase);

	public bool IsSpokenWord =>
		IsStationId || IsCommercial ||
		string.Equals(Category, "VoiceTrack", StringComparison.OrdinalIgnoreCase);

	public bool IsMusic => !IsSweeper && !IsSpokenWord;

	public TimeSpan EffectiveStart => TimeSpan.FromMilliseconds(Math.Max(0, StartCueMs ?? AutoCueInMs ?? 0));

	public TimeSpan EffectiveEnd
	{
		get
		{
			double totalMs = Duration.TotalMilliseconds;
			double raw = EndCueMs ?? AutoCueOutMs ?? totalMs;
			return TimeSpan.FromMilliseconds(Math.Clamp(raw, EffectiveStart.TotalMilliseconds + 50.0, Math.Max(50.0, totalMs)));
		}
	}

	public TimeSpan EffectiveMixOut
	{
		get
		{
			double raw = OutroCueMs ?? AutoMixOutMs ?? EffectiveEnd.TotalMilliseconds;
			return TimeSpan.FromMilliseconds(Math.Clamp(raw, EffectiveStart.TotalMilliseconds + 50.0, EffectiveEnd.TotalMilliseconds));
		}
	}

	public static TrackCueSnapshot FromLibraryItem(LibraryItem item, string? effectiveCategoryOverride = null)
	{
		string effectiveCategory = !string.IsNullOrWhiteSpace(effectiveCategoryOverride)
			? effectiveCategoryOverride
			: item.Category;

		return new TrackCueSnapshot(
			item.FilePath,
			effectiveCategory,
			item.Duration,
			item.StartCueMs,
			item.IntroCueMs,
			item.OutroCueMs,
			item.EndCueMs,
			item.AutoCueInMs,
			item.AutoMixOutMs,
			item.AutoCueOutMs,
			item.Bpm,
			item.BpmConfidence,
			item.FirstBeatMs,
			item.OutroBeatMs,
			item.LoudnessLufs,
			item.TruePeakDb,
			item.IntroProfile,
			item.OutroProfile,
			item.EncoderDelaySamples ?? 0,
			item.EncoderPaddingSamples ?? 0);
	}
}

public sealed record TransitionSettings(
	bool GaplessEnabled,
	TransitionMode Mode,
	CrossfadeCurve Curve,
	double MaxTempoStretchPercent,
	bool BassSwapEnabled,
	double BassSwapHz,
	int ClassicCrossfadeMs,
	int ManualSkipCrossfadeMs,
	int CommercialGaplessMs,
	double LoudnessTargetLufs,
	double MaxGainDb,
	bool NormalizationEnabled)
{
	public static TransitionSettings FromAppSettings(AppSettings s)
	{
		return new TransitionSettings(
			s.GaplessPlaybackEnabled,
			s.TransitionMode,
			s.CrossfadeCurve,
			Math.Clamp(s.AutoMixMaxTempoStretchPercent, 0.0, 12.0),
			s.AutoMixBassSwapEnabled,
			Math.Clamp(s.AutoMixBassSwapHz, 80.0, 400.0),
			Math.Clamp(s.ClassicCrossfadeMs, 100, 12000),
			Math.Clamp(s.ManualSkipCrossfadeMs, 100, 4000),
			Math.Clamp(s.CommercialGaplessMs, 0, 500),
			Math.Clamp(s.NormalizationTargetDb, -24.0, -6.0),
			Math.Clamp(s.NormalizationMaxGainDb, 0.0, 24.0),
			s.AudioNormalizationEnabled);
	}
}

public sealed record TransitionPlan(
	string AppliedStrategy,
	TimeSpan OutgoingMixStart,
	TimeSpan IncomingStart,
	TimeSpan CrossfadeDuration,
	CrossfadeCurve Curve,
	bool UseBeatSync,
	double OutgoingMixTempo,
	double IncomingStartTempo,
	double TempoGlideBackSeconds,
	bool UseBassSwapEq,
	double BassSwapHz,
	string HumanSummary,
	bool FullPunchIncoming = false);

/// <summary>
/// Apple Music-grade AutoMix & Broadcast Transition Planner.
/// Computes optimal transition timing, curve, beat-sync tempo alignment, and bass-swap EQ
/// while respecting explicit per-track cues and broadcast category rules.
/// </summary>
public static class TransitionPlanner
{
	public static TransitionPlan ComputePlan(
		TrackCueSnapshot outgoing,
		TrackCueSnapshot incoming,
		TransitionSettings settings,
		bool isManualSkip = false,
		TimeSpan? currentPosition = null,
		TransitionMode? playsetModeOverride = null)
	{
		TimeSpan incomingStart = incoming.EffectiveStart;
		TimeSpan outgoingEnd = outgoing.EffectiveEnd;

		// 1. Live manual skip: always perform a smooth, click-free micro-crossfade immediately
		if (isManualSkip)
		{
			TimeSpan pos = currentPosition ?? TimeSpan.Zero;
			TimeSpan remaining = outgoingEnd > pos ? outgoingEnd - pos : TimeSpan.FromMilliseconds(100);
			double manualSec = Math.Min(settings.ManualSkipCrossfadeMs / 1000.0, Math.Max(0.08, remaining.TotalSeconds));
			return new TransitionPlan(
				"ManualCrossfade",
				pos,
				incomingStart,
				TimeSpan.FromSeconds(manualSec),
				CrossfadeCurve.EqualPower,
				false,
				1.0,
				1.0,
				0.0,
				false,
				settings.BassSwapHz,
				$"Manual Crossfade • {manualSec:0.0}s");
		}

		// 2. Gapless disabled -> clean end-to-start handoff
		if (!settings.GaplessEnabled)
		{
			return new TransitionPlan(
				"Disabled",
				outgoingEnd,
				incomingStart,
				TimeSpan.Zero,
				CrossfadeCurve.Linear,
				false,
				1.0,
				1.0,
				0.0,
				false,
				settings.BassSwapHz,
				"End-to-End");
		}

		TransitionMode effectiveMode = playsetModeOverride ?? settings.Mode;

		// 3. True Gapless mode -> exact 0ms sample-accurate seam
		if (effectiveMode == TransitionMode.TrueGapless)
		{
			return new TransitionPlan(
				"TrueGapless",
				outgoingEnd,
				incomingStart,
				TimeSpan.Zero,
				CrossfadeCurve.Linear,
				false,
				1.0,
				1.0,
				0.0,
				false,
				settings.BassSwapHz,
				"True Gapless • 0ms");
		}

		// 4. Broadcast Category Rules Matrix (never beat-stretch spoken word, commercials, or station IDs)
		if (outgoing.IsSpokenWord || incoming.IsSpokenWord)
		{
			double fadeSec = (outgoing.IsCommercial && incoming.IsCommercial)
				? settings.CommercialGaplessMs / 1000.0
				: 0.18;
			fadeSec = ClampFadeToTracks(fadeSec, outgoing, incoming);
			TimeSpan start = ClampMixStart(outgoingEnd - TimeSpan.FromSeconds(fadeSec), outgoing);
			string label = outgoing.IsStationId || incoming.IsStationId
				? $"Clean ID Break • {fadeSec:0.00}s"
				: $"Tight Spot Segue • {fadeSec:0.00}s";
			return new TransitionPlan(
				"VoiceCleanBreak",
				start,
				incomingStart,
				TimeSpan.FromSeconds(fadeSec),
				CrossfadeCurve.EqualPower,
				false,
				1.0,
				1.0,
				0.0,
				false,
				settings.BassSwapHz,
				label);
		}

		if (outgoing.IsSweeper || incoming.IsSweeper)
		{
			return BuildBroadcastSweeperPlan(outgoing, incoming, settings);
		}

		// 5. Classic Fixed Crossfade mode
		if (effectiveMode == TransitionMode.ClassicCrossfade)
		{
			double fadeSec = ClampFadeToTracks(settings.ClassicCrossfadeMs / 1000.0, outgoing, incoming);
			TimeSpan start = ClampMixStart(outgoingEnd - TimeSpan.FromSeconds(fadeSec), outgoing);
			bool useBassSwap = settings.Curve == CrossfadeCurve.BassSwapEq && settings.BassSwapEnabled && fadeSec >= 1.5;
			return new TransitionPlan(
				"ClassicCrossfade",
				start,
				incomingStart,
				TimeSpan.FromSeconds(fadeSec),
				settings.Curve,
				false,
				1.0,
				1.0,
				0.0,
				useBassSwap,
				settings.BassSwapHz,
				$"Crossfade • {fadeSec:0.0}s");
		}

		// 6. AutoMix (Apple Music iOS 26-style Beat & Phrase-Matched Mixing)
		if (effectiveMode == TransitionMode.AutoMix &&
			TryBuildBeatMatchedAutoMix(outgoing, incoming, settings, out TransitionPlan? autoMixPlan) &&
			autoMixPlan != null)
		{
			return autoMixPlan;
		}

		// 7. Smart Segue (Dynamic Energy-Aware Crossfade)
		return BuildSmartSeguePlan(outgoing, incoming, settings);
	}

	public static float ComputeStaticReplayGain(TrackCueSnapshot track, TransitionSettings settings)
	{
		if (!settings.NormalizationEnabled || !track.LoudnessLufs.HasValue)
		{
			return 1.0f;
		}

		double deltaDb = settings.LoudnessTargetLufs - track.LoudnessLufs.Value;
		deltaDb = Math.Clamp(deltaDb, -18.0, settings.MaxGainDb);

		// Respect true-peak headroom so quiet-LUFS tracks with sharp transients don't slam the limiter
		if (track.TruePeakDb.HasValue)
		{
			double maxSafeBoostDb = -1.0 - track.TruePeakDb.Value;
			if (deltaDb > maxSafeBoostDb + 3.0)
			{
				deltaDb = maxSafeBoostDb + 3.0; // Allow up to 3 dB gentle limiter kiss, prevent harsh over-boosting
			}
		}

		return AudioTrackAnalyzer.DbToLinear(deltaDb);
	}

	/// <summary>
	/// Broadcast FM radio sweeper transition planner:
	/// 1) Song -&gt; Sweeper: ALWAYS lets the song finish naturally (never chops rhythmic songs 6.5s early
	///    at AutoMixOutMs!). Cold/Rhythmic endings play all the way to EffectiveEnd with a tight micro-kiss;
	///    genuine Fade endings glide into the sweeper only inside the final quiet fade tail.
	/// 2) Sweeper -&gt; Next Song: Intelligently sweeps the sweeper's trailing reverb/SFX tail into the next song's
	///    intro based on BOTH the sweeper's outro tail and the incoming song's intro profile (Ramp/Fade/Rhythmic vs Cold),
	///    while protecting the sweeper's spoken voice and launching the next song at full musical punch.
	/// </summary>
	private static TransitionPlan BuildBroadcastSweeperPlan(
		TrackCueSnapshot outgoing,
		TrackCueSnapshot incoming,
		TransitionSettings settings)
	{
		TimeSpan incomingStart = incoming.EffectiveStart;
		TimeSpan outgoingEnd = outgoing.EffectiveEnd;

		// Case 1: Song -> Sweeper (LET THE SONG FINISH!)
		if (!outgoing.IsSweeper && incoming.IsSweeper)
		{
			double sweeperDur = Math.Max(0.2, (incoming.EffectiveEnd - incoming.EffectiveStart).TotalSeconds);

			// 1A. Respect explicit operator-set manual Outro cue if present
			if (outgoing.OutroCueMs.HasValue)
			{
				TimeSpan manualAnchor = ClampMixStart(TimeSpan.FromMilliseconds(outgoing.OutroCueMs.Value), outgoing);
				double remainingSec = Math.Max(0.15, (outgoingEnd - manualAnchor).TotalSeconds);
				double fadeSec = ClampFadeToTracks(Math.Min(remainingSec, Math.Min(1.20, sweeperDur * 0.25)), outgoing, incoming);
				return new TransitionPlan(
					"SweeperEntry",
					manualAnchor,
					incomingStart,
					TimeSpan.FromSeconds(fadeSec),
					CrossfadeCurve.EqualPower,
					false,
					1.0,
					1.0,
					0.0,
					false,
					settings.BassSwapHz,
					$"Cue Outro → Sweeper • {fadeSec:0.0}s",
					FullPunchIncoming: true);
			}

			// 1B. Confirmed gradual Fade-out tail: glide sweeper in ONLY near the end of the fade tail
			if (outgoing.OutroProfile == TrackEdgeProfile.Fade)
			{
				double fadeTailSec = Math.Max(0.0, (outgoingEnd - outgoing.EffectiveMixOut).TotalSeconds);
				if (fadeTailSec >= 0.80)
				{
					double desiredTailOverlap = Math.Clamp(fadeTailSec * 0.45, 0.70, 1.40);
					double safeCap = Math.Min(fadeTailSec, Math.Min(desiredTailOverlap, Math.Max(0.25, sweeperDur * 0.25)));
					double fadeSec = ClampFadeToTracks(safeCap, outgoing, incoming);
					TimeSpan start = ClampMixStart(outgoingEnd - TimeSpan.FromSeconds(fadeSec), outgoing);
					return new TransitionPlan(
						"SweeperEntry",
						start,
						incomingStart,
						TimeSpan.FromSeconds(fadeSec),
						CrossfadeCurve.EqualPower,
						false,
						1.0,
						1.0,
						0.0,
						false,
						settings.BassSwapHz,
						$"Fade Finish → Sweeper • {fadeSec:0.0}s",
						FullPunchIncoming: true);
				}
			}

			// 1C. Cold, Rhythmic, or Unknown ending (Salsa, Pop, Dance, Cold hits):
			// NEVER anchor on AutoMixOutMs (-6.5s)! Play the song ALL THE WAY to EffectiveEnd!
			double endKissSec = outgoing.OutroProfile == TrackEdgeProfile.Cold ? 0.15 : 0.25;
			double tightFadeSec = ClampFadeToTracks(endKissSec, outgoing, incoming);
			TimeSpan finishStart = ClampMixStart(outgoingEnd - TimeSpan.FromSeconds(tightFadeSec), outgoing);
			return new TransitionPlan(
				"SweeperEntry",
				finishStart,
				incomingStart,
				TimeSpan.FromSeconds(tightFadeSec),
				CrossfadeCurve.EqualPower,
				false,
				1.0,
				1.0,
				0.0,
				false,
				settings.BassSwapHz,
				$"Song Finish → Sweeper • {tightFadeSec:0.00}s",
				FullPunchIncoming: true);
		}

		// Case 2: Sweeper -> Next Song (INTELLIGENT RADIO SWEEP!)
		if (outgoing.IsSweeper && !incoming.IsSweeper)
		{
			double sweeperDur = Math.Max(0.2, (outgoingEnd - outgoing.EffectiveStart).TotalSeconds);
			bool incomingIsColdStart = incoming.IntroProfile == TrackEdgeProfile.Cold && (incoming.IntroCueMs ?? 0) < 1500;
			bool incomingHasLongRamp = (incoming.IntroCueMs ?? 0) >= 2500 ||
									   incoming.IntroProfile is TrackEdgeProfile.Ramp or TrackEdgeProfile.Fade;

			double desiredSweepSec;
			if (outgoing.OutroCueMs.HasValue)
			{
				desiredSweepSec = Math.Clamp((outgoingEnd - TimeSpan.FromMilliseconds(outgoing.OutroCueMs.Value)).TotalSeconds, 0.15, 2.50);
			}
			else if (incomingIsColdStart)
			{
				// Next song starts immediately with vocals or a hard hit — crisp broadcast punch at the very end!
				desiredSweepSec = outgoing.OutroProfile == TrackEdgeProfile.Fade ? 0.30 : 0.20;
			}
			else if (outgoing.OutroProfile == TrackEdgeProfile.Cold)
			{
				// Dry voice sweeper with no reverb tail — let the last syllable clear before launching the song
				desiredSweepSec = incomingHasLongRamp ? 0.38 : 0.25;
			}
			else if (outgoing.OutroProfile == TrackEdgeProfile.Fade)
			{
				// Produced FM sweeper with reverb/SFX tail — sweep its tail right over the next song's intro!
				desiredSweepSec = incomingHasLongRamp ? 1.25 : 0.90;
				double measuredTailSec = Math.Max(0.0, (outgoingEnd - outgoing.EffectiveMixOut).TotalSeconds);
				if (measuredTailSec >= 0.35)
				{
					desiredSweepSec = Math.Min(desiredSweepSec, Math.Max(0.65, measuredTailSec));
				}
			}
			else
			{
				// Rhythmic or Unknown sweeper outro
				desiredSweepSec = incomingHasLongRamp ? 0.95 : 0.65;
			}

			// Never step on the sweeper's spoken voice on shorter sweepers
			double maxSweeperFraction = outgoing.OutroProfile == TrackEdgeProfile.Fade ? 0.25 : 0.22;
			double maxSweeperSec = Math.Max(0.15, sweeperDur * maxSweeperFraction);
			double fadeSec = ClampFadeToTracks(Math.Min(desiredSweepSec, maxSweeperSec), outgoing, incoming);
			TimeSpan start = ClampMixStart(outgoingEnd - TimeSpan.FromSeconds(fadeSec), outgoing);
			string label = incomingIsColdStart
				? $"Radio Punch → Song • {fadeSec:0.00}s"
				: $"Radio Sweep → Song • {fadeSec:0.0}s";

			return new TransitionPlan(
				"SweeperSweepOut",
				start,
				incomingStart,
				TimeSpan.FromSeconds(fadeSec),
				CrossfadeCurve.EqualPower,
				false,
				1.0,
				1.0,
				0.0,
				false,
				settings.BassSwapHz,
				label,
				FullPunchIncoming: true);
		}

		// Case 3: Sweeper -> Sweeper back-to-back
		double backToBackSec = ClampFadeToTracks(0.20, outgoing, incoming);
		TimeSpan backToBackStart = ClampMixStart(outgoingEnd - TimeSpan.FromSeconds(backToBackSec), outgoing);
		return new TransitionPlan(
			"SweeperSegue",
			backToBackStart,
			incomingStart,
			TimeSpan.FromSeconds(backToBackSec),
			CrossfadeCurve.EqualPower,
			false,
			1.0,
			1.0,
			0.0,
			false,
			settings.BassSwapHz,
			$"Sweeper Segue • {backToBackSec:0.00}s",
			FullPunchIncoming: true);
	}

	private static bool TryBuildBeatMatchedAutoMix(
		TrackCueSnapshot outgoing,
		TrackCueSnapshot incoming,
		TransitionSettings settings,
		out TransitionPlan? plan)
	{
		plan = null;

		if (!outgoing.Bpm.HasValue || !incoming.Bpm.HasValue ||
			outgoing.Bpm.Value < 55.0 || incoming.Bpm.Value < 55.0)
		{
			return false;
		}

		double outConf = outgoing.BpmConfidence ?? 0.0;
		double inConf = incoming.BpmConfidence ?? 0.0;
		if (outConf < 0.35 || inConf < 0.35)
		{
			return false;
		}

		// Preserve genuine dramatic cold-stop -> cold-start pairings instead of forcing a mashup
		if (outgoing.OutroProfile == TrackEdgeProfile.Cold && incoming.IntroProfile == TrackEdgeProfile.Cold)
		{
			return false;
		}

		// Check direct, half-time, and double-time BPM compatibility
		double outBpm = outgoing.Bpm.Value;
		double inBpm = incoming.Bpm.Value;
		double effectiveInBpm = inBpm;

		double directRatio = outBpm / inBpm;
		double doubleRatio = outBpm / (inBpm * 2.0);
		double halfRatio = outBpm / (inBpm * 0.5);

		double bestRatio = directRatio;
		if (Math.Abs(doubleRatio - 1.0) < Math.Abs(bestRatio - 1.0))
		{
			bestRatio = doubleRatio;
			effectiveInBpm = inBpm * 2.0;
		}
		if (Math.Abs(halfRatio - 1.0) < Math.Abs(bestRatio - 1.0))
		{
			bestRatio = halfRatio;
			effectiveInBpm = inBpm * 0.5;
		}

		double maxStretch = Math.Max(0.01, settings.MaxTempoStretchPercent / 100.0);
		double totalDeltaRatio = Math.Abs(bestRatio - 1.0);
		if (totalDeltaRatio > maxStretch * 1.5)
		{
			return false;
		}

		// Split tempo shift gracefully between outgoing & incoming (max ±maxStretch each) so pitch/feel stays invisible
		double targetBpm = 0.5 * (outBpm + effectiveInBpm);
		double outgoingTempo = Math.Clamp(targetBpm / outBpm, 1.0 - maxStretch, 1.0 + maxStretch);
		double incomingTempo = Math.Clamp((outBpm * outgoingTempo) / effectiveInBpm, 1.0 - maxStretch, 1.0 + maxStretch);

		// Musical phrase length: 16 beats (4 bars) or 8 beats (2 bars) depending on profiles and track lengths
		double matchedBpm = outBpm * outgoingTempo;
		double beatSec = 60.0 / matchedBpm;
		int mixBeats = (outgoing.OutroProfile == TrackEdgeProfile.Rhythmic || outgoing.OutroProfile == TrackEdgeProfile.Fade) &&
					   incoming.IntroProfile != TrackEdgeProfile.Cold
			? 16
			: 8;

		double desiredMixSec = mixBeats * beatSec;
		double clampedMixSec = ClampFadeToTracks(desiredMixSec, outgoing, incoming);
		// Re-quantize clamped duration to nearest even number of beats
		int actualBeats = Math.Max(4, ((int)Math.Floor(clampedMixSec / beatSec) / 2) * 2);
		double finalMixSec = Math.Min(clampedMixSec, actualBeats * beatSec);
		if (finalMixSec < 1.8)
		{
			return false;
		}

		// Beat-phase alignment: align outgoing start to beat grid near MixOut, and incoming start to its FirstBeat
		double outBeatPeriodMs = 60000.0 / outBpm;
		double desiredOutStartMs = outgoing.EffectiveEnd.TotalMilliseconds - (finalMixSec * 1000.0);
		if (outgoing.OutroCueMs.HasValue)
		{
			desiredOutStartMs = Math.Min(desiredOutStartMs, outgoing.OutroCueMs.Value);
		}
		else if (outgoing.AutoMixOutMs.HasValue)
		{
			// Blend between AutoMixOut and End-MixSec so fade tails finish cleanly right at the beat end
			desiredOutStartMs = Math.Min(desiredOutStartMs, outgoing.AutoMixOutMs.Value - (finalMixSec * 250.0));
		}

		if (outgoing.OutroBeatMs.HasValue && outgoing.OutroBeatMs.Value > 0)
		{
			double steps = Math.Round((desiredOutStartMs - outgoing.OutroBeatMs.Value) / outBeatPeriodMs);
			desiredOutStartMs = outgoing.OutroBeatMs.Value + steps * outBeatPeriodMs;
		}

		TimeSpan outgoingStart = ClampMixStart(TimeSpan.FromMilliseconds(desiredOutStartMs), outgoing);

		// Align incoming track to its first audible beat (within first 2s of EffectiveStart)
		TimeSpan incomingStart = incoming.EffectiveStart;
		if (!incoming.StartCueMs.HasValue && incoming.FirstBeatMs.HasValue)
		{
			double deltaFromStart = incoming.FirstBeatMs.Value - incoming.EffectiveStart.TotalMilliseconds;
			if (deltaFromStart >= 0 && deltaFromStart <= 1800)
			{
				incomingStart = TimeSpan.FromMilliseconds(incoming.FirstBeatMs.Value);
			}
		}

		// Ensure we don't exceed available outgoing audio
		double availableOutSec = Math.Max(0.2, (outgoing.EffectiveEnd - outgoingStart).TotalSeconds);
		finalMixSec = Math.Min(finalMixSec, availableOutSec);

		bool useBassSwap = settings.BassSwapEnabled && finalMixSec >= 2.0;
		double glideBackSec = Math.Abs(incomingTempo - 1.0) > 0.002 ? Math.Clamp(beatSec * 8.0, 2.5, 6.0) : 0.0;

		string eqTag = useBassSwap ? "Bass-Swap" : "Equal-Power";
		plan = new TransitionPlan(
			"AutoMixBeatSync",
			outgoingStart,
			incomingStart,
			TimeSpan.FromSeconds(finalMixSec),
			useBassSwap ? CrossfadeCurve.BassSwapEq : settings.Curve,
			true,
			outgoingTempo,
			incomingTempo,
			glideBackSec,
			useBassSwap,
			settings.BassSwapHz,
			$"AutoMix • {matchedBpm:0} BPM Sync • {finalMixSec:0.0}s {eqTag}");
		return true;
	}

	private static TransitionPlan BuildSmartSeguePlan(
		TrackCueSnapshot outgoing,
		TrackCueSnapshot incoming,
		TransitionSettings settings)
	{
		// Dynamic duration matrix inspired by Apple Music Dynamic Crossfade + Broadcast tightness
		double targetFadeSec = (outgoing.OutroProfile, incoming.IntroProfile) switch
		{
			(TrackEdgeProfile.Cold, TrackEdgeProfile.Cold) => 0.25,
			(TrackEdgeProfile.Cold, TrackEdgeProfile.Ramp) => 0.90,
			(TrackEdgeProfile.Cold, TrackEdgeProfile.Fade) => 1.20,
			(TrackEdgeProfile.Fade, TrackEdgeProfile.Cold) => 2.80,
			(TrackEdgeProfile.Fade, TrackEdgeProfile.Ramp) => 5.20,
			(TrackEdgeProfile.Fade, TrackEdgeProfile.Fade) => 6.50,
			(TrackEdgeProfile.Rhythmic, TrackEdgeProfile.Cold) => 1.80,
			(TrackEdgeProfile.Rhythmic, TrackEdgeProfile.Ramp) => 4.20,
			(TrackEdgeProfile.Rhythmic, TrackEdgeProfile.Fade) => 5.00,
			_ => Math.Clamp(settings.ClassicCrossfadeMs / 1000.0, 1.5, 4.0)
		};

		// Respect explicit user OutroCueMs when set
		TimeSpan mixAnchor = outgoing.OutroCueMs.HasValue
			? TimeSpan.FromMilliseconds(outgoing.OutroCueMs.Value)
			: outgoing.EffectiveMixOut;

		double tailAvailableSec = Math.Max(0.2, (outgoing.EffectiveEnd - mixAnchor).TotalSeconds);
		if (outgoing.OutroProfile == TrackEdgeProfile.Fade && tailAvailableSec > targetFadeSec)
		{
			targetFadeSec = Math.Min(8.0, tailAvailableSec);
		}

		double fadeSec = ClampFadeToTracks(targetFadeSec, outgoing, incoming);
		TimeSpan mixStart = outgoing.OutroCueMs.HasValue
			? ClampMixStart(mixAnchor, outgoing)
			: ClampMixStart(outgoing.EffectiveEnd - TimeSpan.FromSeconds(fadeSec), outgoing);

		double actualFadeSec = Math.Min(fadeSec, Math.Max(0.15, (outgoing.EffectiveEnd - mixStart).TotalSeconds));
		bool useBassSwap = settings.BassSwapEnabled && settings.Curve == CrossfadeCurve.BassSwapEq && actualFadeSec >= 2.2;
		CrossfadeCurve curve = useBassSwap ? CrossfadeCurve.BassSwapEq : CrossfadeCurve.EqualPower;

		return new TransitionPlan(
			"SmartSegue",
			mixStart,
			incoming.EffectiveStart,
			TimeSpan.FromSeconds(actualFadeSec),
			curve,
			false,
			1.0,
			1.0,
			0.0,
			useBassSwap,
			settings.BassSwapHz,
			$"Smart Segue • {actualFadeSec:0.0}s ({outgoing.OutroProfile}→{incoming.IntroProfile})");
	}

	private static double ClampFadeToTracks(double desiredSec, TrackCueSnapshot outgoing, TrackCueSnapshot incoming)
	{
		double outDur = Math.Max(0.2, (outgoing.EffectiveEnd - outgoing.EffectiveStart).TotalSeconds);
		double inDur = Math.Max(0.2, (incoming.EffectiveEnd - incoming.EffectiveStart).TotalSeconds);
		double maxAllowed = Math.Min(12.0, Math.Min(outDur * 0.35, inDur * 0.35));
		return Math.Clamp(desiredSec, 0.05, Math.Max(0.05, maxAllowed));
	}

	private static TimeSpan ClampMixStart(TimeSpan candidate, TrackCueSnapshot outgoing)
	{
		TimeSpan minStart = outgoing.EffectiveStart + TimeSpan.FromMilliseconds(200);
		TimeSpan maxStart = outgoing.EffectiveEnd - TimeSpan.FromMilliseconds(50);
		if (maxStart <= minStart) return outgoing.EffectiveStart;
		if (candidate < minStart) return minStart;
		if (candidate > maxStart) return maxStart;
		return candidate;
	}
}
