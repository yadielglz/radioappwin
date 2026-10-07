using System;
using System.Collections.Generic;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using RadioApp.Models;

namespace RadioApp.Services;

public class AudioPlaybackService : IDisposable
{
	private sealed class PlaybackSession : IDisposable
	{
		private readonly DeckId _deckId;

		private readonly GaplessSampleProvider _provider = new GaplessSampleProvider();

		private WaveOutEvent? _output;

		private double _volume = 0.75;

		private int _outputDeviceNumber = -1;

		private bool _normalizationEnabled;

		private double _normalizationTargetDb = -3.0;

		private double _normalizationMaxGainDb = 9.0;

		private bool _limiterEnabled = true;

		private double _gaplessSilenceThresholdDb = -42.0;

		private int _gaplessSilenceHoldMilliseconds = 650;

		private double _gaplessScanWindowSeconds = 20.0;

		private TempoSampleProvider? _tempoProvider;

		private bool _manualStop;

		public bool CanPlay
		{
			get
			{
				if (_output != null)
				{
					return _provider.CurrentSource != null;
				}
				return false;
			}
		}

		public DeckPlaybackState State { get; private set; }

		public TimeSpan Position => _provider.CurrentSource?.Position ?? TimeSpan.Zero;

		public TimeSpan Duration => _provider.CurrentSource?.Duration ?? TimeSpan.Zero;

		public event Action<DeckId, bool>? PlaybackStopped;

		public event Action<DeckId, string>? PreloadedTrackStarted;

		public event Action<byte[]>? ProgramOutputAvailable;

		public PlaybackSession(DeckId deckId)
		{
			_deckId = deckId;
			_provider.PreloadedTrackStarted += delegate(string path)
			{
				_tempoProvider?.SetTempo(1.0);
				this.PreloadedTrackStarted?.Invoke(_deckId, path);
			};
		}

		public void ApplyAudioSettings(AppSettings settings)
		{
			_outputDeviceNumber = settings.OutputDeviceNumber;
			_normalizationEnabled = settings.AudioNormalizationEnabled;
			_normalizationTargetDb = settings.NormalizationTargetDb;
			_normalizationMaxGainDb = settings.NormalizationMaxGainDb;
			_limiterEnabled = settings.AudioLimiterEnabled;
			_gaplessSilenceThresholdDb = settings.GaplessSilenceThresholdDb;
			_gaplessSilenceHoldMilliseconds = settings.GaplessSilenceHoldMilliseconds;
			_gaplessScanWindowSeconds = settings.GaplessScanWindowSeconds;
			_provider.ConfigureLevelTriggeredGapless(_gaplessSilenceThresholdDb, _gaplessSilenceHoldMilliseconds, _gaplessScanWindowSeconds);
		}

		public void Load(string filePath, bool disableLevelTriggeredGapless)
		{
			Reset();
			_provider.ConfigureLevelTriggeredGapless(_gaplessSilenceThresholdDb, _gaplessSilenceHoldMilliseconds, _gaplessScanWindowSeconds);
			_provider.SetCurrent(new TrackSource(filePath, !disableLevelTriggeredGapless, _gaplessSilenceThresholdDb, _gaplessScanWindowSeconds));
			_output = new WaveOutEvent
			{
				DesiredLatency = 100,
				NumberOfBuffers = 2,
				DeviceNumber = _outputDeviceNumber
			};
			ISampleProvider outputProvider = _provider;
			_tempoProvider = new TempoSampleProvider(outputProvider);
			outputProvider = _tempoProvider;
			if (_normalizationEnabled)
			{
				outputProvider = new AdaptiveNormalizationSampleProvider(outputProvider, _normalizationTargetDb, _normalizationMaxGainDb, _limiterEnabled);
			}
			if (_deckId == DeckId.A)
			{
				outputProvider = new ProgramOutputTapSampleProvider(outputProvider, () => _volume, delegate(byte[] bytes)
				{
					this.ProgramOutputAvailable?.Invoke(bytes);
				});
			}
			_output.Init(new SampleToWaveProvider16(outputProvider));
			_output.Volume = (float)Math.Clamp(_volume, 0.0, 1.0);
			_output.PlaybackStopped += HandlePlaybackStopped;
			State = DeckPlaybackState.Stopped;
		}

		public void Play()
		{
			if (_output != null)
			{
				_manualStop = false;
				_output.Play();
				State = DeckPlaybackState.Playing;
			}
		}

		public void PreloadNext(string filePath, bool disableLevelTriggeredGapless)
		{
			_provider.SetNext(new TrackSource(filePath, !disableLevelTriggeredGapless, _gaplessSilenceThresholdDb, _gaplessScanWindowSeconds));
		}

		public void ClearPreloadedNext()
		{
			_provider.ClearNext();
		}

		public void Pause()
		{
			if (_output != null)
			{
				_output.Pause();
				State = DeckPlaybackState.Paused;
			}
		}

		public void Stop()
		{
			if (_output == null)
			{
				State = DeckPlaybackState.Stopped;
				return;
			}
			_manualStop = true;
			_output.Stop();
			_provider.CurrentSource?.Seek(TimeSpan.Zero);
			State = DeckPlaybackState.Stopped;
		}

		public void SetVolume(double volume)
		{
			_volume = Math.Clamp(volume, 0.0, 1.0);
			if (_output != null)
			{
				_output.Volume = (float)_volume;
			}
		}

		public void SetTempo(double tempo)
		{
			_tempoProvider?.SetTempo(tempo);
		}

		public void Seek(TimeSpan position)
		{
			_provider.CurrentSource?.Seek(position);
			_tempoProvider?.Clear();
		}

		public void Reset()
		{
			_manualStop = true;
			if (_output != null)
			{
				_output.PlaybackStopped -= HandlePlaybackStopped;
				_output.Stop();
				_output.Dispose();
				_output = null;
			}
			_provider.Reset();
			_tempoProvider = null;
			State = DeckPlaybackState.Stopped;
			_manualStop = false;
		}

		public void Dispose()
		{
			Reset();
		}

		private void HandlePlaybackStopped(object? sender, StoppedEventArgs e)
		{
			State = DeckPlaybackState.Stopped;
			bool endedNaturally = !_manualStop && _provider.CurrentSource == null;
			this.PlaybackStopped?.Invoke(_deckId, endedNaturally);
			_manualStop = false;
		}
	}

	private sealed class GaplessSampleProvider : ISampleProvider
	{
		private readonly object _gate = new object();

		private TrackSource? _currentSource;

		private TrackSource? _nextSource;

		private TrackSource? _fadingOutSource;

		private int _crossfadeFramesTotal;

		private int _crossfadeFramesCompleted;

		private float[] _crossfadeOutgoingBuffer = Array.Empty<float>();

		private float[] _crossfadeIncomingBuffer = Array.Empty<float>();

		private int _crossfadeMilliseconds = 650;

		public WaveFormat WaveFormat { get; } = NAudio.Wave.WaveFormat.CreateIeeeFloatWaveFormat(44100, 2);

		public TrackSource? CurrentSource
		{
			get
			{
				lock (_gate)
				{
					return _currentSource;
				}
			}
		}

		public event Action<string>? PreloadedTrackStarted;

		public int Read(float[] buffer, int offset, int count)
		{
			int totalRead = 0;
			while (totalRead < count)
			{
				TrackSource source;
				bool crossfading;
				lock (_gate)
				{
					source = _currentSource;
					crossfading = _fadingOutSource != null;
				}
				if (source == null)
				{
					break;
				}
				if (crossfading)
				{
					totalRead += ReadCrossfade(buffer, offset + totalRead, count - totalRead);
					continue;
				}
				if (ShouldBeginCrossfade(source))
				{
					BeginCrossfade();
					continue;
				}
				int requested = LimitReadToCrossfadeStart(source, count - totalRead);
				int read = source.Provider.Read(buffer, offset + totalRead, requested);
				if (read > 0)
				{
					totalRead += read;
				}
				else if (!StartNextTrack())
				{
					break;
				}
			}
			return totalRead;
		}

		public void ConfigureLevelTriggeredGapless(double thresholdDb, int holdMilliseconds, double scanWindowSeconds)
		{
			int safeHoldMilliseconds = Math.Clamp(holdMilliseconds, 100, 3000);
			_crossfadeMilliseconds = safeHoldMilliseconds;
		}

		public void SetCurrent(TrackSource source)
		{
			lock (_gate)
			{
				_fadingOutSource?.Dispose();
				_fadingOutSource = null;
				_currentSource?.Dispose();
				_currentSource = source;
				ClearNextLocked();
				_crossfadeFramesCompleted = 0;
				_crossfadeFramesTotal = 0;
			}
		}

		public void SetNext(TrackSource source)
		{
			lock (_gate)
			{
				ClearNextLocked();
				_nextSource = source;
			}
		}

		public void ClearNext()
		{
			lock (_gate)
			{
				ClearNextLocked();
			}
		}

		public void Reset()
		{
			lock (_gate)
			{
				_fadingOutSource?.Dispose();
				_fadingOutSource = null;
				_currentSource?.Dispose();
				_currentSource = null;
				ClearNextLocked();
				_crossfadeFramesCompleted = 0;
				_crossfadeFramesTotal = 0;
			}
		}

		private void ClearNextLocked()
		{
			_nextSource?.Dispose();
			_nextSource = null;
		}

		private bool ShouldBeginCrossfade(TrackSource source)
		{
			TrackSource next;
			lock (_gate)
			{
				next = _nextSource;
			}
			if (next == null)
			{
				return false;
			}
			return source.Position >= GetCrossfadeStart(source);
		}

		private int LimitReadToCrossfadeStart(TrackSource source, int requested)
		{
			lock (_gate)
			{
				if (_nextSource == null)
				{
					return requested;
				}
			}
			double seconds = (GetCrossfadeStart(source) - source.Position).TotalSeconds;
			int samples = (int)Math.Ceiling(seconds * WaveFormat.SampleRate) * WaveFormat.Channels;
			if (samples <= 0)
			{
				return Math.Min(requested, WaveFormat.Channels);
			}
			samples -= samples % WaveFormat.Channels;
			return Math.Max(WaveFormat.Channels, Math.Min(requested, samples));
		}

		private TimeSpan GetCrossfadeStart(TrackSource source)
		{
			double fadeSeconds = source.AllowLevelTriggeredGapless ? _crossfadeMilliseconds / 1000.0 : 0.12;
			TimeSpan fade = TimeSpan.FromSeconds(Math.Min(fadeSeconds, Math.Max(0.02, source.CueOut.TotalSeconds / 4.0)));
			return source.CueOut - fade;
		}

		private void BeginCrossfade()
		{
			TrackSource? next;
			lock (_gate)
			{
				if (_fadingOutSource != null || _currentSource == null || _nextSource == null)
				{
					return;
				}
				_fadingOutSource = _currentSource;
				next = _nextSource;
				_currentSource = next;
				_nextSource = null;
				double seconds = _fadingOutSource.AllowLevelTriggeredGapless ? _crossfadeMilliseconds / 1000.0 : 0.12;
				double remaining = Math.Max(0.02, (_fadingOutSource.CueOut - _fadingOutSource.Position).TotalSeconds);
				_crossfadeFramesTotal = Math.Max(1, (int)Math.Round(Math.Min(seconds, remaining) * WaveFormat.SampleRate));
				_crossfadeFramesCompleted = 0;
			}
			this.PreloadedTrackStarted?.Invoke(next.FilePath);
		}

		private int ReadCrossfade(float[] buffer, int offset, int count)
		{
			TrackSource? outgoing;
			TrackSource? incoming;
			int completed;
			int total;
			lock (_gate)
			{
				outgoing = _fadingOutSource;
				incoming = _currentSource;
				completed = _crossfadeFramesCompleted;
				total = _crossfadeFramesTotal;
			}
			if (outgoing == null || incoming == null)
			{
				return 0;
			}
			int channels = WaveFormat.Channels;
			int requestedFrames = count / channels;
			int fadeFrames = Math.Min(requestedFrames, total - completed);
			int sampleCount = fadeFrames * channels;
			EnsureCrossfadeBufferCapacity(sampleCount);
			int outgoingRead = outgoing.Provider.Read(_crossfadeOutgoingBuffer, 0, sampleCount);
			int incomingRead = incoming.Provider.Read(_crossfadeIncomingBuffer, 0, sampleCount);
			int framesRead = Math.Max(outgoingRead, incomingRead) / channels;
			for (int frame = 0; frame < framesRead; frame++)
			{
				double progress = Math.Clamp((double)(completed + frame) / Math.Max(1, total - 1), 0.0, 1.0);
				float outgoingGain = (float)Math.Cos(progress * Math.PI / 2.0);
				float incomingGain = (float)Math.Sin(progress * Math.PI / 2.0);
				for (int channel = 0; channel < channels; channel++)
				{
					int sample = frame * channels + channel;
					float first = sample < outgoingRead ? _crossfadeOutgoingBuffer[sample] : 0f;
					float second = sample < incomingRead ? _crossfadeIncomingBuffer[sample] : 0f;
					buffer[offset + sample] = Math.Clamp(first * outgoingGain + second * incomingGain, -1f, 1f);
				}
			}
			bool finished = framesRead == 0 || completed + framesRead >= total;
			if (finished)
			{
				lock (_gate)
				{
					_fadingOutSource?.Dispose();
					_fadingOutSource = null;
					_crossfadeFramesCompleted = 0;
					_crossfadeFramesTotal = 0;
				}
			}
			else
			{
				lock (_gate)
				{
					_crossfadeFramesCompleted += framesRead;
				}
			}
			return framesRead * channels;
		}

		private void EnsureCrossfadeBufferCapacity(int sampleCount)
		{
			if (_crossfadeOutgoingBuffer.Length < sampleCount)
			{
				_crossfadeOutgoingBuffer = new float[sampleCount];
			}
			if (_crossfadeIncomingBuffer.Length < sampleCount)
			{
				_crossfadeIncomingBuffer = new float[sampleCount];
			}
		}

		private bool StartNextTrack()
		{
			TrackSource previous;
			TrackSource next;
			lock (_gate)
			{
				previous = _currentSource;
				next = (_currentSource = _nextSource);
				_nextSource = null;
			}
			previous?.Dispose();
			if (next == null)
			{
				return false;
			}
			this.PreloadedTrackStarted?.Invoke(next.FilePath);
			return true;
		}

	}

	private sealed class TrackSource : IDisposable
	{
		private readonly WaveStream _reader;

		public string FilePath { get; }

		public bool AllowLevelTriggeredGapless { get; }

		public ISampleProvider Provider { get; }

		public int LowLevelSamples { get; private set; }

		public TimeSpan Position
		{
			get
			{
				if (!(Provider is TrackingSampleProvider trackingProvider))
				{
					return _reader.CurrentTime;
				}
				return trackingProvider.Position;
			}
		}

		public TimeSpan Duration => _reader.TotalTime;

		public TimeSpan CueIn { get; }

		public TimeSpan CueOut { get; }

		public TrackSource(string filePath, bool allowLevelTriggeredGapless, double silenceThresholdDb, double scanWindowSeconds)
		{
			FilePath = filePath;
			AllowLevelTriggeredGapless = allowLevelTriggeredGapless;
			_reader = AudioFileProbe.CreateReader(filePath);
			Provider = new TrackingSampleProvider(CreateProvider(_reader));
			(CueIn, CueOut) = allowLevelTriggeredGapless ? AnalyzeCuePoints(filePath, silenceThresholdDb, scanWindowSeconds) : (TimeSpan.Zero, Duration);
			if (CueIn > TimeSpan.Zero)
			{
				Seek(CueIn);
			}
		}

		public bool CanUseLevelTriggeredGapless(TimeSpan scanWindow)
		{
			if (!AllowLevelTriggeredGapless || Duration <= TimeSpan.Zero)
			{
				return false;
			}
			TimeSpan position = Position;
			TimeSpan minimumPlayed = TimeSpan.FromSeconds(Math.Min(30.0, Math.Max(5.0, Duration.TotalSeconds * 0.25)));
			if (position < minimumPlayed)
			{
				return false;
			}
			return Duration - position <= scanWindow;
		}

		public void Seek(TimeSpan position)
		{
			_reader.CurrentTime = ((position < TimeSpan.Zero) ? TimeSpan.Zero : ((position > _reader.TotalTime) ? _reader.TotalTime : position));
			if (Provider is TrackingSampleProvider trackingProvider)
			{
				trackingProvider.SetPosition(_reader.CurrentTime);
			}
			ResetLowLevelSampleCount();
		}

		public void Dispose()
		{
			_reader.Dispose();
		}

		public void AddLowLevelSamples(int samples)
		{
			LowLevelSamples += samples;
		}

		public void ResetLowLevelSampleCount()
		{
			LowLevelSamples = 0;
		}

		private static ISampleProvider CreateProvider(WaveStream reader)
		{
			ISampleProvider provider = reader.ToSampleProvider();
			provider = EnsureStereo(provider);
			if (provider.WaveFormat.SampleRate != 44100)
			{
				provider = new WdlResamplingSampleProvider(provider, 44100);
			}
			if (provider.WaveFormat.SampleRate != 44100 || provider.WaveFormat.Channels != 2)
			{
				throw new NotSupportedException($"Could not convert audio to {44100} Hz stereo. Got {provider.WaveFormat.SampleRate} Hz, {provider.WaveFormat.Channels} channel(s).");
			}
			return provider;
		}

		private static ISampleProvider EnsureStereo(ISampleProvider provider)
		{
			return provider.WaveFormat.Channels switch
			{
				1 => new MonoToStereoSampleProvider(provider), 
				2 => provider, 
				_ => new MultiplexingSampleProvider(new _003C_003Ez__ReadOnlySingleElementList<ISampleProvider>(provider), 2), 
			};
		}

		private static (TimeSpan CueIn, TimeSpan CueOut) AnalyzeCuePoints(string filePath, double thresholdDb, double scanWindowSeconds)
		{
			using WaveStream analysisReader = AudioFileProbe.CreateReader(filePath);
			TimeSpan duration = analysisReader.TotalTime;
			if (duration <= TimeSpan.Zero)
			{
				return (TimeSpan.Zero, duration);
			}
			float threshold = DecibelsToLinear(Math.Clamp(thresholdDb, -72.0, -12.0));
			TimeSpan headEnd = TimeSpan.FromSeconds(Math.Min(10.0, duration.TotalSeconds));
			TimeSpan cueIn = FindFirstAudibleFrame(analysisReader, TimeSpan.Zero, headEnd, threshold);
			TimeSpan tailStart = TimeSpan.FromSeconds(Math.Max(0.0, duration.TotalSeconds - Math.Clamp(scanWindowSeconds, 3.0, 60.0)));
			TimeSpan cueOut = FindLastAudibleFrame(analysisReader, tailStart, duration, threshold);
			cueIn = cueIn > TimeSpan.FromMilliseconds(20.0) ? cueIn - TimeSpan.FromMilliseconds(20.0) : TimeSpan.Zero;
			cueOut = cueOut + TimeSpan.FromMilliseconds(100.0);
			if (cueOut > duration)
			{
				cueOut = duration;
			}
			if (cueOut <= cueIn + TimeSpan.FromMilliseconds(250.0))
			{
				return (TimeSpan.Zero, duration);
			}
			return (cueIn, cueOut);
		}

		private static TimeSpan FindFirstAudibleFrame(WaveStream reader, TimeSpan start, TimeSpan end, float threshold)
		{
			reader.CurrentTime = start;
			ISampleProvider provider = CreateProvider(reader);
			float[] buffer = new float[4096];
			long frame = (long)Math.Round(start.TotalSeconds * 44100.0);
			long endFrame = (long)Math.Round(end.TotalSeconds * 44100.0);
			while (frame < endFrame)
			{
				int read = provider.Read(buffer, 0, buffer.Length);
				if (read <= 0)
				{
					break;
				}
				for (int sample = 0; sample + 1 < read; sample += 2, frame++)
				{
					if (Math.Max(Math.Abs(buffer[sample]), Math.Abs(buffer[sample + 1])) >= threshold)
					{
						return TimeSpan.FromSeconds((double)frame / 44100.0);
					}
				}
			}
			return start;
		}

		private static TimeSpan FindLastAudibleFrame(WaveStream reader, TimeSpan start, TimeSpan end, float threshold)
		{
			reader.CurrentTime = start;
			ISampleProvider provider = CreateProvider(reader);
			float[] buffer = new float[4096];
			long frame = (long)Math.Round(start.TotalSeconds * 44100.0);
			long endFrame = (long)Math.Round(end.TotalSeconds * 44100.0);
			long lastAudibleFrame = -1;
			while (frame < endFrame)
			{
				int read = provider.Read(buffer, 0, buffer.Length);
				if (read <= 0)
				{
					break;
				}
				for (int sample = 0; sample + 1 < read && frame < endFrame; sample += 2, frame++)
				{
					if (Math.Max(Math.Abs(buffer[sample]), Math.Abs(buffer[sample + 1])) >= threshold)
					{
						lastAudibleFrame = frame;
					}
				}
			}
			return lastAudibleFrame < 0 ? end : TimeSpan.FromSeconds((double)(lastAudibleFrame + 1) / 44100.0);
		}

		private static float DecibelsToLinear(double decibels)
		{
			return (float)Math.Pow(10.0, decibels / 20.0);
		}
	}

	private sealed class TrackingSampleProvider : ISampleProvider
	{
		private readonly ISampleProvider _source;

		private long _samplesRead;

		public WaveFormat WaveFormat => _source.WaveFormat;

		public TimeSpan Position => TimeSpan.FromSeconds((double)_samplesRead / (double)Math.Max(1, WaveFormat.SampleRate * WaveFormat.Channels));

		public TrackingSampleProvider(ISampleProvider source)
		{
			_source = source;
		}

		public int Read(float[] buffer, int offset, int count)
		{
			int read = _source.Read(buffer, offset, count);
			_samplesRead += read;
			return read;
		}

		public void SetPosition(TimeSpan position)
		{
			double seconds = Math.Max(0.0, position.TotalSeconds);
			_samplesRead = (long)Math.Round(seconds * (double)WaveFormat.SampleRate * (double)WaveFormat.Channels);
		}
	}

	private sealed class TempoSampleProvider : ISampleProvider
	{
		private const double MinimumTempo = 1.0;

		private const double MaximumTempo = 1.15;

		private const int BlockFrames = 4096;

		private readonly ISampleProvider _source;

		private readonly Queue<float> _pending = new Queue<float>();

		private readonly object _gate = new object();

		private double _tempo = 1.0;

		public WaveFormat WaveFormat => _source.WaveFormat;

		private int PendingCount
		{
			get
			{
				lock (_gate)
				{
					return _pending.Count;
				}
			}
		}

		public TempoSampleProvider(ISampleProvider source)
		{
			_source = source;
		}

		public int Read(float[] buffer, int offset, int count)
		{
			double tempo;
			lock (_gate)
			{
				tempo = _tempo;
			}
			if (tempo <= 1.001)
			{
				return _source.Read(buffer, offset, count);
			}
			while (PendingCount < count && FillCompressedBlock(tempo))
			{
			}
			int written = 0;
			lock (_gate)
			{
				for (; written < count; written++)
				{
					if (_pending.Count <= 0)
					{
						break;
					}
					buffer[offset + written] = _pending.Dequeue();
				}
			}
			return written;
		}

		public void SetTempo(double tempo)
		{
			lock (_gate)
			{
				_tempo = Math.Clamp(tempo, 1.0, 1.15);
				_pending.Clear();
			}
		}

		public void Clear()
		{
			lock (_gate)
			{
				_pending.Clear();
			}
		}

		private bool FillCompressedBlock(double tempo)
		{
			int channels = WaveFormat.Channels;
			float[] input = new float[4096 * channels];
			int read = _source.Read(input, 0, input.Length);
			if (read <= 0)
			{
				return false;
			}
			int inputFrames = read / channels;
			if (inputFrames < 512)
			{
				lock (_gate)
				{
					if (Math.Abs(_tempo - tempo) > 0.0001)
					{
						return true;
					}
					EnqueueFrames(input, 0, inputFrames, channels);
				}
				return true;
			}
			int targetFrames = Math.Max(1, (int)Math.Round((double)inputFrames / tempo));
			int dropFrames = inputFrames - targetFrames;
			if (dropFrames <= 0)
			{
				lock (_gate)
				{
					if (Math.Abs(_tempo - tempo) > 0.0001)
					{
						return true;
					}
					EnqueueFrames(input, 0, inputFrames, channels);
				}
				return true;
			}
			int crossfadeFrames = Math.Min(256, Math.Min(dropFrames / 2, Math.Max(1, targetFrames / 8)));
			int dropStart = Math.Max(0, (inputFrames - dropFrames - crossfadeFrames) / 2);
			lock (_gate)
			{
				if (Math.Abs(_tempo - tempo) > 0.0001)
				{
					return true;
				}
				if (dropStart + dropFrames + crossfadeFrames >= inputFrames)
				{
					EnqueueFrames(input, 0, targetFrames, channels);
					return true;
				}
				EnqueueFrames(input, 0, dropStart, channels);
				EnqueueCrossfade(input, dropStart, dropStart + dropFrames, crossfadeFrames, channels);
				EnqueueFrames(input, dropStart + dropFrames + crossfadeFrames, inputFrames, channels);
			}
			return true;
		}

		private void EnqueueFrames(float[] input, int startFrame, int endFrame, int channels)
		{
			int num = startFrame * channels;
			int end = Math.Min(input.Length, endFrame * channels);
			for (int i = num; i < end; i++)
			{
				_pending.Enqueue(input[i]);
			}
		}

		private void EnqueueCrossfade(float[] input, int firstFrame, int secondFrame, int frameCount, int channels)
		{
			for (int frame = 0; frame < frameCount; frame++)
			{
				float mix = ((frameCount <= 1) ? 1f : ((float)frame / (float)(frameCount - 1)));
				for (int channel = 0; channel < channels; channel++)
				{
					float first = input[(firstFrame + frame) * channels + channel];
					float second = input[(secondFrame + frame) * channels + channel];
					_pending.Enqueue(first * (1f - mix) + second * mix);
				}
			}
		}
	}

	private sealed class AdaptiveNormalizationSampleProvider : ISampleProvider
	{
		private readonly ISampleProvider _source;

		private readonly bool _limiterEnabled;

		private readonly float _targetPeak;

		private readonly float _maxGain;

		private float _currentGain = 1f;

		public WaveFormat WaveFormat => _source.WaveFormat;

		public AdaptiveNormalizationSampleProvider(ISampleProvider source, double targetPeakDb, double maxGainDb, bool limiterEnabled)
		{
			_source = source;
			_limiterEnabled = limiterEnabled;
			_targetPeak = (float)Math.Pow(10.0, Math.Clamp(targetPeakDb, -24.0, 0.0) / 20.0);
			_maxGain = (float)Math.Pow(10.0, Math.Clamp(maxGainDb, 0.0, 24.0) / 20.0);
		}

		public int Read(float[] buffer, int offset, int count)
		{
			int read = _source.Read(buffer, offset, count);
			if (read == 0)
			{
				return 0;
			}
			float peak = 0.0001f;
			for (int i = offset; i < offset + read; i++)
			{
				float abs = Math.Abs(buffer[i]);
				if (abs > peak) peak = abs;
			}
			float desiredGain = Math.Clamp(_targetPeak / peak, 0.1f, _maxGain);
			float smoothing = ((desiredGain < _currentGain) ? 0.25f : 0.02f);
			_currentGain += (desiredGain - _currentGain) * smoothing;
			for (int j = offset; j < offset + read; j++)
			{
				float sample = buffer[j] * _currentGain;
				if (_limiterEnabled)
				{
					if (sample > 0.85f)
					{
						sample = 0.85f + 0.15f * MathF.Tanh((sample - 0.85f) / 0.15f);
					}
					else if (sample < -0.85f)
					{
						sample = -0.85f + 0.15f * MathF.Tanh((sample + 0.85f) / 0.15f);
					}
				}
				buffer[j] = Math.Clamp(sample, -1f, 1f);
			}
			return read;
		}
	}

	private sealed class ProgramOutputTapSampleProvider : ISampleProvider
	{
		private readonly ISampleProvider _source;

		private readonly Func<double> _volumeProvider;

		private readonly Action<byte[]> _outputAvailable;

		public WaveFormat WaveFormat => _source.WaveFormat;

		public ProgramOutputTapSampleProvider(ISampleProvider source, Func<double> volumeProvider, Action<byte[]> outputAvailable)
		{
			_source = source;
			_volumeProvider = volumeProvider;
			_outputAvailable = outputAvailable;
		}

		public int Read(float[] buffer, int offset, int count)
		{
			int read = _source.Read(buffer, offset, count);
			if (read <= 0)
			{
				return read;
			}
			double volume = Math.Clamp(_volumeProvider(), 0.0, 1.0);
			byte[] pcmBytes = new byte[read * 2];
			int byteOffset = 0;
			for (int i = offset; i < offset + read; i++)
			{
				short sample = (short)Math.Clamp((double)buffer[i] * volume * 32767.0, -32768.0, 32767.0);
				pcmBytes[byteOffset++] = (byte)(sample & 0xFF);
				pcmBytes[byteOffset++] = (byte)((sample >> 8) & 0xFF);
			}
			_outputAvailable(pcmBytes);
			return read;
		}
	}

	private const int OutputSampleRate = 44100;

	private const int OutputChannels = 2;

	private readonly PlaybackSession _deckA = new PlaybackSession(DeckId.A);

	private readonly PlaybackSession _deckB = new PlaybackSession(DeckId.B);

	private readonly DispatcherTimer _positionTimer;

	public event Action<DeckId, DeckPlaybackState, TimeSpan, TimeSpan>? PlaybackUpdated;

	public event Action<DeckId>? TrackEnded;

	public event Action<DeckId, string>? PreloadedTrackStarted;

	public event Action<DeckId, string>? PlaybackFailed;

	public event Action<byte[]>? ProgramOutputAvailable;

	public AudioPlaybackService()
	{
		_deckA.PlaybackStopped += OnPlaybackStopped;
		_deckA.PreloadedTrackStarted += OnPreloadedTrackStarted;
		_deckA.ProgramOutputAvailable += OnProgramOutputAvailable;
		_deckB.PlaybackStopped += OnPlaybackStopped;
		_deckB.PreloadedTrackStarted += OnPreloadedTrackStarted;
		_positionTimer = new DispatcherTimer
		{
			Interval = TimeSpan.FromMilliseconds(250.0)
		};
		_positionTimer.Tick += delegate
		{
			UpdatePlayback(DeckId.A);
			UpdatePlayback(DeckId.B);
		};
		_positionTimer.Start();
	}

	public static IReadOnlyList<AudioDeviceInfo> GetOutputDevices()
	{
		List<AudioDeviceInfo> devices = new List<AudioDeviceInfo>
		{
			new AudioDeviceInfo
			{
				DeviceNumber = -1,
				Name = "Windows default playback device"
			}
		};
		for (int i = 0; i < WaveOut.DeviceCount; i++)
		{
			WaveOutCapabilities capabilities = WaveOut.GetCapabilities(i);
			devices.Add(new AudioDeviceInfo
			{
				DeviceNumber = i,
				Name = capabilities.ProductName
			});
		}
		return devices;
	}

	public static IReadOnlyList<AudioDeviceInfo> GetInputDevices()
	{
		List<AudioDeviceInfo> devices = new List<AudioDeviceInfo>
		{
			new AudioDeviceInfo
			{
				DeviceNumber = -1,
				Name = "Windows default recording device"
			}
		};
		for (int i = 0; i < WaveIn.DeviceCount; i++)
		{
			WaveInCapabilities capabilities = WaveIn.GetCapabilities(i);
			devices.Add(new AudioDeviceInfo
			{
				DeviceNumber = i,
				Name = capabilities.ProductName
			});
		}
		return devices;
	}

	public void ApplyAudioSettings(AppSettings settings)
	{
		_deckA.ApplyAudioSettings(settings);
		_deckB.ApplyAudioSettings(settings);
	}

	public bool LoadTrack(DeckId deckId, string filePath, bool disableLevelTriggeredGapless = false)
	{
		PlaybackSession session = GetSession(deckId);
		try
		{
			if (!File.Exists(filePath))
			{
				throw new FileNotFoundException("Audio file was not found.", filePath);
			}
			session.Load(filePath, disableLevelTriggeredGapless);
			this.PlaybackUpdated?.Invoke(deckId, DeckPlaybackState.Stopped, TimeSpan.Zero, session.Duration);
			return true;
		}
		catch (Exception ex)
		{
			session.Reset();
			this.PlaybackUpdated?.Invoke(deckId, DeckPlaybackState.Stopped, TimeSpan.Zero, TimeSpan.Zero);
			this.PlaybackFailed?.Invoke(deckId, ex.Message);
			return false;
		}
	}

	public void Play(DeckId deckId)
	{
		PlaybackSession session = GetSession(deckId);
		if (!session.CanPlay)
		{
			this.PlaybackFailed?.Invoke(deckId, "Load a playable audio file first.");
			return;
		}
		session.Play();
		UpdatePlayback(deckId);
	}

	public bool PreloadNext(DeckId deckId, string filePath, bool disableLevelTriggeredGapless = false)
	{
		try
		{
			if (!File.Exists(filePath))
			{
				throw new FileNotFoundException("Audio file was not found.", filePath);
			}
			GetSession(deckId).PreloadNext(filePath, disableLevelTriggeredGapless);
			return true;
		}
		catch (Exception ex)
		{
			this.PlaybackFailed?.Invoke(deckId, "Could not preload next track: " + ex.Message);
			return false;
		}
	}

	public void ClearPreloadedNext(DeckId deckId)
	{
		GetSession(deckId).ClearPreloadedNext();
	}

	public void Pause(DeckId deckId)
	{
		GetSession(deckId).Pause();
		UpdatePlayback(deckId);
	}

	public void Stop(DeckId deckId)
	{
		PlaybackSession session = GetSession(deckId);
		session.Stop();
		this.PlaybackUpdated?.Invoke(deckId, DeckPlaybackState.Stopped, TimeSpan.Zero, session.Duration);
	}

	public void SetVolume(DeckId deckId, double volume)
	{
		GetSession(deckId).SetVolume(volume);
	}

	public void SetTempo(DeckId deckId, double tempo)
	{
		GetSession(deckId).SetTempo(tempo);
	}

	public void Seek(DeckId deckId, TimeSpan position)
	{
		GetSession(deckId).Seek(position);
		UpdatePlayback(deckId);
	}

	public void Dispose()
	{
		_positionTimer.Stop();
		_deckA.Dispose();
		_deckB.Dispose();
	}

	private void UpdatePlayback(DeckId deckId)
	{
		PlaybackSession session = GetSession(deckId);
		this.PlaybackUpdated?.Invoke(deckId, session.State, session.Position, session.Duration);
	}

	private void OnPlaybackStopped(DeckId deckId, bool endedNaturally)
	{
		Dispatcher dispatcher = Application.Current?.Dispatcher;
		if (dispatcher != null && !dispatcher.CheckAccess())
		{
			dispatcher.BeginInvoke((Action)delegate
			{
				OnPlaybackStopped(deckId, endedNaturally);
			});
			return;
		}
		PlaybackSession session = GetSession(deckId);
		this.PlaybackUpdated?.Invoke(deckId, DeckPlaybackState.Stopped, endedNaturally ? session.Duration : TimeSpan.Zero, session.Duration);
		if (endedNaturally)
		{
			this.TrackEnded?.Invoke(deckId);
		}
	}

	private void OnPreloadedTrackStarted(DeckId deckId, string filePath)
	{
		Dispatcher dispatcher = Application.Current?.Dispatcher;
		if (dispatcher != null && !dispatcher.CheckAccess())
		{
			dispatcher.BeginInvoke((Action)delegate
			{
				OnPreloadedTrackStarted(deckId, filePath);
			});
		}
		else
		{
			this.PreloadedTrackStarted?.Invoke(deckId, filePath);
			UpdatePlayback(deckId);
		}
	}

	private PlaybackSession GetSession(DeckId deckId)
	{
		if (deckId != DeckId.A)
		{
			return _deckB;
		}
		return _deckA;
	}

	private void OnProgramOutputAvailable(byte[] pcmBytes)
	{
		this.ProgramOutputAvailable?.Invoke(pcmBytes);
	}
}
