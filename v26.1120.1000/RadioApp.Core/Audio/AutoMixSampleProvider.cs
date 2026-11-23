using System;
using NAudio.Wave;
using RadioApp.Models;

namespace RadioApp.Audio;

/// <summary>
/// Represents an active or preloaded audio track stream normalized to 44.1 kHz stereo float32.
/// </summary>
public sealed class ManagedTrackStream : IDisposable
{
	private readonly IDisposable? _ownerDisposable;
	private readonly Action<TimeSpan>? _seekAction;
	private long _framesRead;

	public TrackCueSnapshot Cue { get; }
	public ISampleProvider SampleProvider { get; }
	public WsolaTimeStretcher Stretcher { get; }
	public float StaticReplayGain { get; set; } = 1.0f;

	public TimeSpan Position => TimeSpan.FromSeconds((double)_framesRead / SampleProvider.WaveFormat.SampleRate);
	public TimeSpan Duration => Cue.Duration;

	public ManagedTrackStream(
		TrackCueSnapshot cue,
		ISampleProvider stereo44100Provider,
		IDisposable? ownerDisposable = null,
		Action<TimeSpan>? seekAction = null)
	{
		Cue = cue;
		SampleProvider = stereo44100Provider;
		Stretcher = new WsolaTimeStretcher(stereo44100Provider);
		_ownerDisposable = ownerDisposable;
		_seekAction = seekAction;
	}

	public int ReadFrames(float[] buffer, int offset, int samplesCount)
	{
		int read = Stretcher.Read(buffer, offset, samplesCount);
		if (read > 0)
		{
			_framesRead += (long)Math.Round((read / 2.0) * Stretcher.Tempo);
		}
		return read;
	}

	public void Seek(TimeSpan position)
	{
		TimeSpan clamped = position < TimeSpan.Zero ? TimeSpan.Zero : (position > Duration ? Duration : position);
		_seekAction?.Invoke(clamped);
		_framesRead = (long)Math.Round(clamped.TotalSeconds * SampleProvider.WaveFormat.SampleRate);
		Stretcher.Clear();
	}

	public void Dispose()
	{
		_ownerDisposable?.Dispose();
	}
}

/// <summary>
/// Real-time 44.1 kHz stereo float32 AutoMix & Gapless sample provider.
/// Supports:
/// - AutoMix beat-matched tempo alignment (WSOLA) + Linkwitz-Riley LR24 bass-swap EQ crossfades
/// - Smart Segue energy-matched dynamic crossfades
/// - True Gapless 0-sample handoff
/// - Smooth click-free manual skip crossfading
/// - EBU R128 static per-track loudness matching
/// </summary>
public sealed class AutoMixSampleProvider : ISampleProvider
{
	private const int SampleRate = 44100;
	private const int Channels = 2;

	private readonly object _gate = new object();
	private readonly LinkwitzRileyCrossover _outgoingCrossover = new LinkwitzRileyCrossover(SampleRate, 180.0);
	private readonly LinkwitzRileyCrossover _incomingCrossover = new LinkwitzRileyCrossover(SampleRate, 180.0);

	private ManagedTrackStream? _current;
	private ManagedTrackStream? _next;
	private ManagedTrackStream? _outgoingFading;
	private TransitionPlan? _plannedNextTransition;
	private TransitionPlan? _activeTransition;
	private TransitionSettings _settings = TransitionSettings.FromAppSettings(new AppSettings());
	private TransitionMode? _playsetModeOverride;

	private int _fadeTotalFrames;
	private int _fadeCompletedFrames;
	private double _glideTotalFrames;
	private double _glideCompletedFrames;
	private double _glideStartTempo = 1.0;

	private float[] _outBuf = Array.Empty<float>();
	private float[] _inBuf = Array.Empty<float>();
	private double _userManualTempo = 1.0;

	public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(SampleRate, Channels);

	public ManagedTrackStream? CurrentTrack
	{
		get { lock (_gate) { return _current; } }
	}

	public TransitionPlan? PlannedNextTransition
	{
		get { lock (_gate) { return _plannedNextTransition; } }
	}

	public bool IsTransitioning
	{
		get { lock (_gate) { return _outgoingFading != null; } }
	}

	public event Action<string, TransitionPlan?>? PreloadedTrackStarted;
	public event Action? TrackQueueExhausted;

	public void UpdateSettings(TransitionSettings settings, TransitionMode? playsetModeOverride = null)
	{
		lock (_gate)
		{
			_settings = settings;
			_playsetModeOverride = playsetModeOverride;
			_outgoingCrossover.Configure(SampleRate, settings.BassSwapHz);
			_incomingCrossover.Configure(SampleRate, settings.BassSwapHz);

			if (_current != null)
			{
				_current.StaticReplayGain = TransitionPlanner.ComputeStaticReplayGain(_current.Cue, _settings);
			}
			if (_next != null)
			{
				_next.StaticReplayGain = TransitionPlanner.ComputeStaticReplayGain(_next.Cue, _settings);
			}
			RecomputeNextTransitionLocked();
		}
	}

	public void SetManualTempo(double tempo)
	{
		lock (_gate)
		{
			_userManualTempo = Math.Clamp(tempo, 0.85, 1.18);
			if (_current != null && _outgoingFading == null && _glideTotalFrames <= 0)
			{
				_current.Stretcher.SetTempo(_userManualTempo);
			}
		}
	}

	public void SetCurrent(ManagedTrackStream track, bool seekToEffectiveStart = true)
	{
		lock (_gate)
		{
			_outgoingFading?.Dispose();
			_outgoingFading = null;
			_current?.Dispose();
			_current = track;
			_current.StaticReplayGain = TransitionPlanner.ComputeStaticReplayGain(track.Cue, _settings);
			_current.Stretcher.SetTempo(_userManualTempo);
			if (seekToEffectiveStart && track.Cue.EffectiveStart > TimeSpan.Zero)
			{
				track.Seek(track.Cue.EffectiveStart);
			}
			_activeTransition = null;
			_fadeCompletedFrames = 0;
			_fadeTotalFrames = 0;
			_glideCompletedFrames = 0;
			_glideTotalFrames = 0;
			RecomputeNextTransitionLocked();
		}
	}

	public void SetNext(ManagedTrackStream track)
	{
		lock (_gate)
		{
			_next?.Dispose();
			_next = track;
			_next.StaticReplayGain = TransitionPlanner.ComputeStaticReplayGain(track.Cue, _settings);
			RecomputeNextTransitionLocked();
		}
	}

	public void ClearNext()
	{
		lock (_gate)
		{
			_next?.Dispose();
			_next = null;
			_plannedNextTransition = null;
		}
	}

	/// <summary>
	/// Immediately crossfades smoothly from the current track into the provided or preloaded next track.
	/// </summary>
	public bool TriggerManualCrossfade(ManagedTrackStream? explicitNext = null)
	{
		string? startedPath = null;
		TransitionPlan? startedPlan = null;
		lock (_gate)
		{
			if (explicitNext != null)
			{
				_next?.Dispose();
				_next = explicitNext;
				_next.StaticReplayGain = TransitionPlanner.ComputeStaticReplayGain(explicitNext.Cue, _settings);
			}

			if (_current == null || _next == null)
			{
				return false;
			}

			TransitionPlan manualPlan = TransitionPlanner.ComputePlan(
				_current.Cue,
				_next.Cue,
				_settings,
				isManualSkip: true,
				currentPosition: _current.Position);

			BeginTransitionLocked(manualPlan, out startedPath, out startedPlan);
		}

		if (startedPath != null)
		{
			PreloadedTrackStarted?.Invoke(startedPath, startedPlan);
			return true;
		}
		return false;
	}

	public void SeekCurrent(TimeSpan position)
	{
		lock (_gate)
		{
			_current?.Seek(position);
		}
	}

	public void Reset()
	{
		lock (_gate)
		{
			_outgoingFading?.Dispose();
			_outgoingFading = null;
			_current?.Dispose();
			_current = null;
			_next?.Dispose();
			_next = null;
			_plannedNextTransition = null;
			_activeTransition = null;
			_fadeCompletedFrames = 0;
			_fadeTotalFrames = 0;
			_glideCompletedFrames = 0;
			_glideTotalFrames = 0;
			_userManualTempo = 1.0;
		}
	}

	public int Read(float[] buffer, int offset, int count)
	{
		int totalRead = 0;
		while (totalRead < count)
		{
			ManagedTrackStream? current;
			bool fading;
			TransitionPlan? planned;

			lock (_gate)
			{
				current = _current;
				fading = _outgoingFading != null;
				planned = _plannedNextTransition;
			}

			if (current == null)
			{
				break;
			}

			if (fading)
			{
				int mixed = ReadActiveTransition(buffer, offset + totalRead, count - totalRead);
				if (mixed <= 0)
				{
					break;
				}
				totalRead += mixed;
				continue;
			}

			// Check if we reached the planned mix-out point
			if (planned != null && current.Position >= planned.OutgoingMixStart)
			{
				string? startedPath = null;
				TransitionPlan? startedPlan = null;
				lock (_gate)
				{
					if (_current != null && _next != null && _plannedNextTransition != null)
					{
						BeginTransitionLocked(_plannedNextTransition, out startedPath, out startedPlan);
					}
				}
				if (startedPath != null)
				{
					PreloadedTrackStarted?.Invoke(startedPath, startedPlan);
					continue;
				}
			}

			// Clamp read length so we hit OutgoingMixStart or EffectiveEnd cleanly
			int requested = LimitReadSamples(current, planned, count - totalRead);
			int read = current.ReadFrames(buffer, offset + totalRead, requested);

			if (read > 0)
			{
				ApplyStaticGainAndGlide(current, buffer, offset + totalRead, read);
				totalRead += read;

				// Respect explicit EndCue / AutoCueOut when no next track is preloaded
				if (planned == null && current.Position >= current.Cue.EffectiveEnd)
				{
					if (!AdvanceWithoutCrossfade())
					{
						break;
					}
				}
			}
			else
			{
				if (!AdvanceWithoutCrossfade())
				{
					break;
				}
			}
		}

		return totalRead;
	}

	private void ApplyStaticGainAndGlide(ManagedTrackStream current, float[] buffer, int offset, int count)
	{
		float gain = current.StaticReplayGain;
		if (Math.Abs(gain - 1.0f) > 0.001f)
		{
			for (int i = offset; i < offset + count; i++)
			{
				buffer[i] *= gain;
			}
		}

		// Smoothly glide incoming beat-synced track back to natural 1.00x (or user tempo) after AutoMix completes
		lock (_gate)
		{
			if (_glideTotalFrames > 0)
			{
				_glideCompletedFrames += count / (double)Channels;
				double p = Math.Clamp(_glideCompletedFrames / _glideTotalFrames, 0.0, 1.0);
				// Smooth cosine ease back to natural tempo
				double eased = 0.5 - 0.5 * Math.Cos(p * Math.PI);
				double target = _userManualTempo;
				double currentTempo = _glideStartTempo + (target - _glideStartTempo) * eased;
				current.Stretcher.SetTempo(currentTempo);
				if (p >= 1.0)
				{
					_glideTotalFrames = 0;
					_glideCompletedFrames = 0;
					current.Stretcher.SetTempo(target);
				}
			}
		}
	}

	private int LimitReadSamples(ManagedTrackStream current, TransitionPlan? planned, int requested)
	{
		TimeSpan stopPoint = planned?.OutgoingMixStart ?? current.Cue.EffectiveEnd;
		double remainingSec = (stopPoint - current.Position).TotalSeconds;
		if (remainingSec <= 0)
		{
			return Math.Min(requested, Channels);
		}
		int maxSamples = (int)Math.Ceiling(remainingSec * SampleRate) * Channels;
		maxSamples -= maxSamples % Channels;
		return Math.Max(Channels, Math.Min(requested, maxSamples));
	}

	private void BeginTransitionLocked(TransitionPlan plan, out string? startedPath, out TransitionPlan? startedPlan)
	{
		startedPath = null;
		startedPlan = null;
		if (_current == null || _next == null)
		{
			return;
		}

		// True Gapless 0ms handoff
		if (plan.CrossfadeDuration <= TimeSpan.FromMilliseconds(5))
		{
			ManagedTrackStream previous = _current;
			ManagedTrackStream next = _next;
			_current = next;
			_next = null;
			_plannedNextTransition = null;
			_activeTransition = null;
			if (plan.IncomingStart > TimeSpan.Zero)
			{
				next.Seek(plan.IncomingStart);
			}
			next.Stretcher.SetTempo(_userManualTempo);
			previous.Dispose();
			startedPath = next.Cue.FilePath;
			startedPlan = plan;
			return;
		}

		// Dispose any stale outgoing fade before starting new transition
		_outgoingFading?.Dispose();
		_outgoingFading = _current;
		_current = _next;
		_next = null;
		_plannedNextTransition = null;
		_activeTransition = plan;

		if (plan.IncomingStart > TimeSpan.Zero)
		{
			_current.Seek(plan.IncomingStart);
		}

		if (plan.UseBeatSync)
		{
			_outgoingFading.Stretcher.SetTempo(plan.OutgoingMixTempo);
			_current.Stretcher.SetTempo(plan.IncomingStartTempo);
		}
		else
		{
			_current.Stretcher.SetTempo(_userManualTempo);
		}

		_outgoingCrossover.Configure(SampleRate, plan.BassSwapHz);
		_incomingCrossover.Configure(SampleRate, plan.BassSwapHz);
		_outgoingCrossover.Reset();
		_incomingCrossover.Reset();

		_fadeTotalFrames = Math.Max(1, (int)Math.Round(plan.CrossfadeDuration.TotalSeconds * SampleRate));
		_fadeCompletedFrames = 0;

		startedPath = _current.Cue.FilePath;
		startedPlan = plan;
	}

	private int ReadActiveTransition(float[] buffer, int offset, int count)
	{
		ManagedTrackStream? outgoing;
		ManagedTrackStream? incoming;
		TransitionPlan? plan;
		int completed;
		int total;

		lock (_gate)
		{
			outgoing = _outgoingFading;
			incoming = _current;
			plan = _activeTransition;
			completed = _fadeCompletedFrames;
			total = _fadeTotalFrames;
		}

		if (outgoing == null || incoming == null || plan == null)
		{
			return 0;
		}

		int requestedFrames = count / Channels;
		int framesToMix = Math.Min(requestedFrames, Math.Max(1, total - completed));
		int samplesToMix = framesToMix * Channels;
		EnsureBuffers(samplesToMix);

		int outRead = outgoing.ReadFrames(_outBuf, 0, samplesToMix);
		int inRead = incoming.ReadFrames(_inBuf, 0, samplesToMix);
		int framesRead = Math.Max(outRead, inRead) / Channels;

		float outReplay = outgoing.StaticReplayGain;
		float inReplay = incoming.StaticReplayGain;

		for (int f = 0; f < framesRead; f++)
		{
			double p = Math.Clamp((double)(completed + f) / Math.Max(1, total - 1), 0.0, 1.0);
			int s = f * Channels;

			float oL = (s + 1 < outRead) ? _outBuf[s] * outReplay : 0f;
			float oR = (s + 1 < outRead) ? _outBuf[s + 1] * outReplay : 0f;
			float iL = (s + 1 < inRead) ? _inBuf[s] * inReplay : 0f;
			float iR = (s + 1 < inRead) ? _inBuf[s + 1] * inReplay : 0f;

			float mixL;
			float mixR;

			if (plan.UseBassSwapEq)
			{
				// Linkwitz-Riley LR24 Bass-Swap EQ Crossfade:
				// Highs/Mids crossfade smoothly with equal-power curve over full [0..1]
				// Bass swaps decisively across the musical center [0.35..0.65] so kick drums never double-stack!
				_outgoingCrossover.SplitFrame(oL, oR, out float oLowL, out float oLowR, out float oHighL, out float oHighR);
				_incomingCrossover.SplitFrame(iL, iR, out float iLowL, out float iLowR, out float iHighL, out float iHighR);

				float highOutGain = (float)Math.Cos(p * Math.PI * 0.5);
				float highInGain = (float)Math.Sin(p * Math.PI * 0.5);

				double bassP = Math.Clamp((p - 0.35) / 0.30, 0.0, 1.0);
				// Smooth S-curve bass swap
				double bassEase = 0.5 - 0.5 * Math.Cos(bassP * Math.PI);
				float lowOutGain = (float)(1.0 - bassEase);
				float lowInGain = (float)bassEase;

				mixL = (oLowL * lowOutGain + iLowL * lowInGain) + (oHighL * highOutGain + iHighL * highInGain);
				mixR = (oLowR * lowOutGain + iLowR * lowInGain) + (oHighR * highOutGain + iHighR * highInGain);
			}
			else
			{
				ComputeCurveGains(plan.Curve, p, out float gOut, out float gIn);
				mixL = oL * gOut + iL * gIn;
				mixR = oR * gOut + iR * gIn;
			}

			buffer[offset + s] = mixL;
			buffer[offset + s + 1] = mixR;
		}

		bool finished = framesRead == 0 || completed + framesRead >= total;
		lock (_gate)
		{
			if (finished)
			{
				_outgoingFading?.Dispose();
				_outgoingFading = null;
				_fadeCompletedFrames = 0;
				_fadeTotalFrames = 0;

				if (_activeTransition != null && _activeTransition.UseBeatSync && _activeTransition.TempoGlideBackSeconds > 0.1)
				{
					_glideStartTempo = _activeTransition.IncomingStartTempo;
					_glideTotalFrames = _activeTransition.TempoGlideBackSeconds * SampleRate;
					_glideCompletedFrames = 0;
				}
				else if (_current != null)
				{
					_current.Stretcher.SetTempo(_userManualTempo);
				}

				_activeTransition = null;
				RecomputeNextTransitionLocked();
			}
			else
			{
				_fadeCompletedFrames += framesRead;
			}
		}

		return framesRead * Channels;
	}

	private static void ComputeCurveGains(CrossfadeCurve curve, double p, out float gOut, out float gIn)
	{
		switch (curve)
		{
			case CrossfadeCurve.Linear:
				gOut = (float)(1.0 - p);
				gIn = (float)p;
				break;
			case CrossfadeCurve.SCurve:
				double s = p * p * (3.0 - 2.0 * p);
				gOut = (float)Math.Cos(s * Math.PI * 0.5);
				gIn = (float)Math.Sin(s * Math.PI * 0.5);
				break;
			default: // EqualPower & BassSwapEq fallback
				gOut = (float)Math.Cos(p * Math.PI * 0.5);
				gIn = (float)Math.Sin(p * Math.PI * 0.5);
				break;
		}
	}

	private bool AdvanceWithoutCrossfade()
	{
		ManagedTrackStream? previous;
		ManagedTrackStream? next;
		lock (_gate)
		{
			previous = _current;
			next = _next;
			_current = next;
			_next = null;
			_plannedNextTransition = null;
			if (next != null)
			{
				if (next.Cue.EffectiveStart > TimeSpan.Zero)
				{
					next.Seek(next.Cue.EffectiveStart);
				}
				next.Stretcher.SetTempo(_userManualTempo);
			}
		}
		previous?.Dispose();

		if (next == null)
		{
			TrackQueueExhausted?.Invoke();
			return false;
		}
		PreloadedTrackStarted?.Invoke(next.Cue.FilePath, null);
		return true;
	}

	private void RecomputeNextTransitionLocked()
	{
		if (_current == null || _next == null)
		{
			_plannedNextTransition = null;
			return;
		}
		_plannedNextTransition = TransitionPlanner.ComputePlan(
			_current.Cue,
			_next.Cue,
			_settings,
			isManualSkip: false,
			playsetModeOverride: _playsetModeOverride);
	}

	private void EnsureBuffers(int sampleCount)
	{
		if (_outBuf.Length < sampleCount) _outBuf = new float[sampleCount];
		if (_inBuf.Length < sampleCount) _inBuf = new float[sampleCount];
	}
}
