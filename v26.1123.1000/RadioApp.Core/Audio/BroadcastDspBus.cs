using System;
using System.Collections.Generic;
using NAudio.Wave;

namespace RadioApp.Audio;

public readonly record struct AudioTelemetrySnapshot(
	double PeakLeftDb,
	double PeakRightDb,
	double RmsLeftDb,
	double RmsRightDb,
	double MomentaryLufs,
	double LimiterGainReductionDb,
	double ConsecutiveSilenceSeconds,
	bool IsClipping);

/// <summary>
/// Active overlay audio source (Cartwall pad, Sweeper intro-ramp drop, or Mic Talk-Over).
/// </summary>
public sealed class OverlayChannel : IDisposable
{
	private readonly IDisposable? _owner;
	public string Key { get; }
	public ISampleProvider Provider { get; }
	public float Gain { get; set; }
	public bool DuckProgram { get; }
	public float DuckLinearTarget { get; }

	public OverlayChannel(string key, ISampleProvider stereo44100Provider, float gain, bool duckProgram, double duckDb, IDisposable? owner = null)
	{
		Key = key;
		Provider = stereo44100Provider;
		Gain = Math.Clamp(gain, 0f, 2f);
		DuckProgram = duckProgram;
		DuckLinearTarget = AudioTrackAnalyzer.DbToLinear(Math.Clamp(duckDb, -24.0, 0.0));
		_owner = owner;
	}

	public void Dispose() => _owner?.Dispose();
}

/// <summary>
/// Mixes primary Program Deck audio with concurrent Cartwall pads and optional Mic Talk-Over,
/// applying smooth click-free auto-ducking to program audio while overlays are active.
/// </summary>
public sealed class BroadcastMixerBus : ISampleProvider
{
	private const int SampleRate = 44100;
	private const int Channels = 2;

	private readonly ISampleProvider _programSource;
	private readonly object _gate = new object();
	private readonly List<OverlayChannel> _overlays = new List<OverlayChannel>();
	private float[] _overlayBuf = Array.Empty<float>();

	private float _currentDuckGain = 1.0f;
	private volatile bool _manualMicDuckActive;
	private float _manualMicDuckTarget = 0.25f;

	public WaveFormat WaveFormat => _programSource.WaveFormat;

	public bool HasActiveOverlays
	{
		get { lock (_gate) { return _overlays.Count > 0; } }
	}

	public BroadcastMixerBus(ISampleProvider programSource)
	{
		_programSource = programSource;
	}

	public void AddOverlay(OverlayChannel channel)
	{
		lock (_gate)
		{
			// Replace existing channel with same key (e.g. re-triggering the same Cart Pad)
			for (int i = _overlays.Count - 1; i >= 0; i--)
			{
				if (string.Equals(_overlays[i].Key, channel.Key, StringComparison.OrdinalIgnoreCase))
				{
					_overlays[i].Dispose();
					_overlays.RemoveAt(i);
				}
			}
			_overlays.Add(channel);
		}
	}

	public void StopOverlay(string key)
	{
		lock (_gate)
		{
			for (int i = _overlays.Count - 1; i >= 0; i--)
			{
				if (string.Equals(_overlays[i].Key, key, StringComparison.OrdinalIgnoreCase))
				{
					_overlays[i].Dispose();
					_overlays.RemoveAt(i);
				}
			}
		}
	}

	public void SetMicDucking(bool active, double duckDb)
	{
		_manualMicDuckActive = active;
		_manualMicDuckTarget = AudioTrackAnalyzer.DbToLinear(Math.Clamp(duckDb, -24.0, 0.0));
	}

	public void ClearAllOverlays()
	{
		lock (_gate)
		{
			foreach (OverlayChannel ch in _overlays) ch.Dispose();
			_overlays.Clear();
		}
	}

	public int Read(float[] buffer, int offset, int count)
	{
		int programRead = _programSource.Read(buffer, offset, count);

		// Determine if any overlays are active; if program stopped/paused, still allow overlays to play!
		List<OverlayChannel>? activeSnapshot = null;
		lock (_gate)
		{
			if (_overlays.Count > 0)
			{
				activeSnapshot = new List<OverlayChannel>(_overlays);
			}
		}

		if (programRead == 0 && activeSnapshot == null)
		{
			return 0;
		}

		int outputSamples = programRead;
		if (activeSnapshot != null && programRead < count)
		{
			Array.Clear(buffer, offset + programRead, count - programRead);
			outputSamples = count;
		}

		int frames = outputSamples / Channels;
		int duckUntilFrame = _manualMicDuckActive ? frames : 0;
		float activeDuckTarget = _manualMicDuckActive ? _manualMicDuckTarget : 1.0f;

		List<(OverlayChannel Channel, int SamplesRead)>? mixedOverlays = null;
		List<OverlayChannel>? finished = null;

		if (activeSnapshot != null)
		{
			mixedOverlays = new List<(OverlayChannel, int)>(activeSnapshot.Count);
			if (_overlayBuf.Length < outputSamples * activeSnapshot.Count)
			{
				_overlayBuf = new float[outputSamples * activeSnapshot.Count];
			}

			for (int idx = 0; idx < activeSnapshot.Count; idx++)
			{
				OverlayChannel ch = activeSnapshot[idx];
				int sliceOffset = idx * outputSamples;
				int read = ch.Provider.Read(_overlayBuf, sliceOffset, outputSamples);
				mixedOverlays.Add((ch, read));

				if (ch.DuckProgram && read > 0)
				{
					activeDuckTarget = Math.Min(activeDuckTarget, ch.DuckLinearTarget);
					duckUntilFrame = Math.Max(duckUntilFrame, read / Channels);
				}

				if (read < outputSamples)
				{
					finished ??= new List<OverlayChannel>();
					finished.Add(ch);
				}
			}
		}

		// Apply smooth sample-accurate attack (15ms) / release (250ms) ducking to program audio
		float attackCoeff = 1.0f - MathF.Exp(-1.0f / (0.015f * SampleRate));
		float releaseCoeff = 1.0f - MathF.Exp(-1.0f / (0.250f * SampleRate));

		for (int f = 0; f < frames; f++)
		{
			float frameTarget = f < duckUntilFrame ? activeDuckTarget : 1.0f;
			float coeff = frameTarget < _currentDuckGain ? attackCoeff : releaseCoeff;
			_currentDuckGain += (frameTarget - _currentDuckGain) * coeff;
			int s = offset + f * Channels;
			buffer[s] *= _currentDuckGain;
			buffer[s + 1] *= _currentDuckGain;
		}

		if (mixedOverlays != null)
		{
			for (int idx = 0; idx < mixedOverlays.Count; idx++)
			{
				var (ch, read) = mixedOverlays[idx];
				if (read > 0)
				{
					int sliceOffset = idx * outputSamples;
					float g = ch.Gain;
					for (int i = 0; i < read; i++)
					{
						buffer[offset + i] += _overlayBuf[sliceOffset + i] * g;
					}
				}
			}

			if (finished != null)
			{
				lock (_gate)
				{
					foreach (OverlayChannel done in finished)
					{
						if (_overlays.Remove(done))
						{
							done.Dispose();
						}
					}
				}
			}
		}

		return outputSamples;
	}
}

/// <summary>
/// Broadcast Master DSP stage:
/// - Broadcast Master Gain (dB)
/// - 5ms Lookahead True-Peak Brickwall Limiter (zero harsh clipping)
/// - Real-Time Stereo Peak / RMS / Momentary LUFS / Gain-Reduction Telemetry
/// - Real Audio-Level Dead-Air Silence Tracker
/// - Post-DSP, Pre-Monitor Broadcast Program PCM16 Tap (muting local speakers NEVER mutes the live stream!)
/// </summary>
public sealed class MasterDspProcessor : ISampleProvider
{
	private const int SampleRate = 44100;
	private const int Channels = 2;
	private const int LookaheadFrames = 220; // 5.0 ms @ 44.1 kHz

	private readonly ISampleProvider _source;
	private readonly Action<byte[]>? _broadcastTap;
	private readonly EbuR128KWeightingFilter _meterKFilter = new EbuR128KWeightingFilter(SampleRate);

	private readonly float[] _delayL = new float[LookaheadFrames];
	private readonly float[] _delayR = new float[LookaheadFrames];
	private int _delayIndex;

	private float _limiterGain = 1.0f;
	private readonly float _releaseCoeff = 1.0f - MathF.Exp(-1.0f / (0.080f * SampleRate));

	private volatile bool _limiterEnabled = true;
	private float _masterGainLinear = 1.0f;
	private float _ceilingLinear = 0.89125f; // -1.0 dBTP
	private double _silenceThresholdDb = -50.0;

	// Momentary LUFS (400ms exponential energy tracker)
	private double _momentaryPower;
	private readonly double _momentaryAlpha = 1.0 - Math.Exp(-1.0 / (0.400 * SampleRate));
	private double _consecutiveSilenceSeconds;

	private readonly object _telemetryGate = new object();
	private AudioTelemetrySnapshot _latestTelemetry = new AudioTelemetrySnapshot(-72, -72, -72, -72, -70, 0, 0, false);

	public WaveFormat WaveFormat => _source.WaveFormat;

	public AudioTelemetrySnapshot LatestTelemetry
	{
		get { lock (_telemetryGate) { return _latestTelemetry; } }
	}

	public MasterDspProcessor(ISampleProvider source, Action<byte[]>? broadcastTap = null)
	{
		_source = source;
		_broadcastTap = broadcastTap;
	}

	public void Configure(double masterGainDb, bool limiterEnabled, double truePeakCeilingDb, double silenceThresholdDb)
	{
		_masterGainLinear = AudioTrackAnalyzer.DbToLinear(Math.Clamp(masterGainDb, -24.0, 12.0));
		_limiterEnabled = limiterEnabled;
		_ceilingLinear = AudioTrackAnalyzer.DbToLinear(Math.Clamp(truePeakCeilingDb, -6.0, -0.1));
		_silenceThresholdDb = Math.Clamp(silenceThresholdDb, -72.0, -24.0);
	}

	public void ResetSilenceCounter()
	{
		lock (_telemetryGate)
		{
			_consecutiveSilenceSeconds = 0.0;
		}
	}

	public int Read(float[] buffer, int offset, int count)
	{
		int read = _source.Read(buffer, offset, count);
		if (read <= 0)
		{
			return read;
		}

		float masterGain = _masterGainLinear;
		bool limiterOn = _limiterEnabled;
		float ceiling = _ceilingLinear;

		float peakL = 1e-6f;
		float peakR = 1e-6f;
		double sumSqL = 0.0;
		double sumSqR = 0.0;
		float minLimiterGain = 1.0f;
		bool clipped = false;

		int frames = read / Channels;
		for (int f = 0; f < frames; f++)
		{
			int s = offset + f * Channels;
			float inL = buffer[s] * masterGain;
			float inR = buffer[s + 1] * masterGain;

			float outL;
			float outR;

			if (limiterOn)
			{
				// Push input into 5ms lookahead delay line; compute required gain reduction ahead of peak arrival
				float delayedL = _delayL[_delayIndex];
				float delayedR = _delayR[_delayIndex];
				_delayL[_delayIndex] = inL;
				_delayR[_delayIndex] = inR;
				_delayIndex = (_delayIndex + 1) % LookaheadFrames;

				float detectorPeak = Math.Max(Math.Max(Math.Abs(inL), Math.Abs(inR)), Math.Max(Math.Abs(delayedL), Math.Abs(delayedR)));
				float desiredGain = detectorPeak > ceiling ? ceiling / detectorPeak : 1.0f;

				if (desiredGain < _limiterGain)
				{
					// Fast attack (0.5ms) synced with 5ms lookahead prevents overshoot cleanly
					_limiterGain += (desiredGain - _limiterGain) * 0.25f;
				}
				else
				{
					_limiterGain += (desiredGain - _limiterGain) * _releaseCoeff;
				}

				if (_limiterGain < minLimiterGain) minLimiterGain = _limiterGain;

				outL = Math.Clamp(delayedL * _limiterGain, -ceiling, ceiling);
				outR = Math.Clamp(delayedR * _limiterGain, -ceiling, ceiling);
			}
			else
			{
				if (Math.Abs(inL) > 0.999f || Math.Abs(inR) > 0.999f) clipped = true;
				outL = Math.Clamp(inL, -1.0f, 1.0f);
				outR = Math.Clamp(inR, -1.0f, 1.0f);
			}

			buffer[s] = outL;
			buffer[s + 1] = outR;

			float absL = Math.Abs(outL);
			float absR = Math.Abs(outR);
			if (absL > peakL) peakL = absL;
			if (absR > peakR) peakR = absR;
			sumSqL += outL * outL;
			sumSqR += outR * outR;

			_meterKFilter.ProcessFrame(outL, outR, out double kL, out double kR);
			_momentaryPower += ((kL * kL + kR * kR) - _momentaryPower) * _momentaryAlpha;
		}

		double rmsL = Math.Sqrt(sumSqL / Math.Max(1, frames));
		double rmsR = Math.Sqrt(sumSqR / Math.Max(1, frames));
		double rmsCombinedDb = AudioTrackAnalyzer.LinearToDb(Math.Max(rmsL, rmsR));
		double blockSec = frames / (double)SampleRate;

		if (rmsCombinedDb < _silenceThresholdDb)
		{
			_consecutiveSilenceSeconds += blockSec;
		}
		else
		{
			_consecutiveSilenceSeconds = 0.0;
		}

		double momentaryLufs = _momentaryPower > 1e-10
			? Math.Clamp(-0.691 + 10.0 * Math.Log10(_momentaryPower), -70.0, 0.0)
			: -70.0;

		double grDb = minLimiterGain < 0.999f ? -AudioTrackAnalyzer.LinearToDb(minLimiterGain) : 0.0;

		lock (_telemetryGate)
		{
			_latestTelemetry = new AudioTelemetrySnapshot(
				AudioTrackAnalyzer.LinearToDb(peakL),
				AudioTrackAnalyzer.LinearToDb(peakR),
				AudioTrackAnalyzer.LinearToDb(rmsL),
				AudioTrackAnalyzer.LinearToDb(rmsR),
				momentaryLufs,
				grDb,
				_consecutiveSilenceSeconds,
				clipped);
		}

		// Broadcast PCM16 Tap: happens BEFORE local monitor volume so muting local speakers NEVER mutes the live stream!
		if (_broadcastTap != null)
		{
			byte[] pcm = new byte[read * 2];
			int bi = 0;
			for (int i = offset; i < offset + read; i++)
			{
				short sample = (short)Math.Clamp(buffer[i] * 32767.0, -32768.0, 32767.0);
				pcm[bi++] = (byte)(sample & 0xFF);
				pcm[bi++] = (byte)((sample >> 8) & 0xFF);
			}
			_broadcastTap(pcm);
		}

		return read;
	}
}

/// <summary>
/// Post-Broadcast-Tap Local Monitor Volume & Mute stage.
/// Controls only what plays out of the studio speakers/headphones.
/// </summary>
public sealed class LocalMonitorVolumeProvider : ISampleProvider
{
	private readonly ISampleProvider _source;
	private volatile float _targetGain = 0.85f;
	private float _smoothedGain = 0.85f;
	private volatile bool _muted;

	public WaveFormat WaveFormat => _source.WaveFormat;

	public LocalMonitorVolumeProvider(ISampleProvider source)
	{
		_source = source;
	}

	public void SetMonitorState(double volume, bool muted)
	{
		_targetGain = (float)Math.Clamp(volume, 0.0, 1.0);
		_muted = muted;
	}

	public int Read(float[] buffer, int offset, int count)
	{
		int read = _source.Read(buffer, offset, count);
		if (read <= 0) return read;

		float target = _muted ? 0f : _targetGain;
		for (int i = offset; i < offset + count && i < offset + read; i += 2)
		{
			_smoothedGain += (target - _smoothedGain) * 0.005f;
			buffer[i] *= _smoothedGain;
			if (i + 1 < offset + read)
			{
				buffer[i + 1] *= _smoothedGain;
			}
		}
		return read;
	}
}
