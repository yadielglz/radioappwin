using System;
using System.Diagnostics;
using System.Threading;
using NAudio.Wave;

namespace RadioApp.Audio;

/// <summary>
/// Precision drift-compensated software audio clock implementing NAudio's IWavePlayer.
/// Drives the full AutoMix -> BroadcastMixerBus -> MasterDspProcessor -> StreamingService pipeline
/// at exact real-time sample rate (44.1 kHz) with ZERO audio hardware dependency.
/// Keeps Cloud PC broadcasts alive 24/7 even when RDP disconnects and Windows destroys Remote Audio!
/// </summary>
public sealed class HeadlessBroadcastClockPlayer : IWavePlayer
{
	private readonly object _gate = new object();
	private IWaveProvider? _provider;
	private Thread? _pumpThread;
	private CancellationTokenSource? _cts;
	private volatile PlaybackState _playbackState = PlaybackState.Stopped;
	private volatile bool _stopRequested;
	private float _volume = 1.0f;

	public float Volume
	{
		get => _volume;
		set => _volume = Math.Clamp(value, 0f, 1f);
	}

	public PlaybackState PlaybackState => _playbackState;

	public WaveFormat OutputWaveFormat => _provider?.WaveFormat ?? new WaveFormat(44100, 16, 2);

	public event EventHandler<StoppedEventArgs>? PlaybackStopped;

	public void Init(IWaveProvider waveProvider)
	{
		lock (_gate)
		{
			StopInternal(raiseStopped: false);
			_provider = waveProvider ?? throw new ArgumentNullException(nameof(waveProvider));
		}
	}

	public void Play()
	{
		lock (_gate)
		{
			if (_provider == null)
			{
				throw new InvalidOperationException("HeadlessBroadcastClockPlayer must be initialized before Play().");
			}
			if (_playbackState == PlaybackState.Playing)
			{
				return;
			}
			if (_playbackState == PlaybackState.Paused && _pumpThread != null && _pumpThread.IsAlive)
			{
				_playbackState = PlaybackState.Playing;
				return;
			}

			_stopRequested = false;
			_cts?.Cancel();
			_cts?.Dispose();
			_cts = new CancellationTokenSource();
			_playbackState = PlaybackState.Playing;

			CancellationToken ct = _cts.Token;
			IWaveProvider provider = _provider;
			_pumpThread = new Thread(() => PumpLoop(provider, ct))
			{
				IsBackground = true,
				Name = "RadioApp.HeadlessBroadcastClock",
				Priority = ThreadPriority.AboveNormal
			};
			_pumpThread.Start();
		}
	}

	public void Pause()
	{
		lock (_gate)
		{
			if (_playbackState == PlaybackState.Playing)
			{
				_playbackState = PlaybackState.Paused;
			}
		}
	}

	public void Stop()
	{
		lock (_gate)
		{
			StopInternal(raiseStopped: true);
		}
	}

	private void StopInternal(bool raiseStopped)
	{
		bool wasRunning = _playbackState != PlaybackState.Stopped;
		_stopRequested = true;
		_playbackState = PlaybackState.Stopped;
		_cts?.Cancel();

		Thread? t = _pumpThread;
		_pumpThread = null;
		if (t != null && t.IsAlive && t != Thread.CurrentThread)
		{
			t.Join(TimeSpan.FromMilliseconds(400));
		}
		_cts?.Dispose();
		_cts = null;

		if (raiseStopped && wasRunning)
		{
			PlaybackStopped?.Invoke(this, new StoppedEventArgs());
		}
	}

	private void PumpLoop(IWaveProvider provider, CancellationToken ct)
	{
		WaveFormat wf = provider.WaveFormat;
		int bytesPerSecond = Math.Max(1, wf.AverageBytesPerSecond);
		int blockAlign = Math.Max(1, wf.BlockAlign);

		// 20 ms real-time chunks aligned to frame boundary (e.g. 882 frames * 4 bytes = 3528 bytes @ 44.1kHz stereo 16-bit)
		int chunkBytes = (int)Math.Round(bytesPerSecond * 0.020);
		chunkBytes -= chunkBytes % blockAlign;
		if (chunkBytes < blockAlign) chunkBytes = blockAlign;

		byte[] buffer = new byte[chunkBytes];
		double tickFrequency = Stopwatch.Frequency;
		long clockStart = Stopwatch.GetTimestamp();
		long totalBytesPumped = 0;
		Exception? failure = null;

		try
		{
			while (!ct.IsCancellationRequested)
			{
				if (_playbackState == PlaybackState.Paused)
				{
					Thread.Sleep(25);
					// Reset pace baseline on resume so pause doesn't cause a catch-up burst
					clockStart = Stopwatch.GetTimestamp();
					totalBytesPumped = 0;
					continue;
				}

				if (_playbackState != PlaybackState.Playing)
				{
					break;
				}

				int read = provider.Read(buffer, 0, buffer.Length);
				if (read <= 0)
				{
					break;
				}

				totalBytesPumped += read;
				double expectedElapsedSec = (double)totalBytesPumped / bytesPerSecond;
				double actualElapsedSec = (Stopwatch.GetTimestamp() - clockStart) / tickFrequency;
				double sleepMs = (expectedElapsedSec - actualElapsedSec) * 1000.0;

				// Prevent runaway drift if system briefly hibernated or paused
				if (sleepMs < -250.0)
				{
					clockStart = Stopwatch.GetTimestamp();
					totalBytesPumped = 0;
				}
				else if (sleepMs > 1.5)
				{
					int wholeMs = (int)Math.Floor(sleepMs);
					if (wholeMs > 0)
					{
						Thread.Sleep(Math.Min(wholeMs, 40));
					}
				}
			}
		}
		catch (OperationCanceledException)
		{
		}
		catch (Exception ex)
		{
			failure = ex;
		}
		finally
		{
			bool naturalEnd = !_stopRequested && !ct.IsCancellationRequested;
			_playbackState = PlaybackState.Stopped;
			if (naturalEnd || failure != null)
			{
				PlaybackStopped?.Invoke(this, new StoppedEventArgs(failure));
			}
		}
	}

	public void Dispose()
	{
		StopInternal(raiseStopped: false);
	}
}
