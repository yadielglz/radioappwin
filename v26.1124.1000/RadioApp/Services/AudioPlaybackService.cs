using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Win32;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using RadioApp.Audio;
using RadioApp.Models;

namespace RadioApp.Services;

/// <summary>
/// Windows 10/11 & Cloud PC master audio playout engine hosting:
/// - AutoMixSampleProvider (AutoMix Beat-Sync + Smart Segue + True Gapless + Manual Crossfade)
/// - BroadcastMixerBus (Program + 8-Channel Cartwall Overlay + Live Mic Talk-Over Ducking)
/// - MasterDspProcessor (Broadcast Gain + 5ms Lookahead True-Peak Limiter + Live LUFS/Peak Telemetry + Broadcast PCM Tap)
/// - LocalMonitorVolumeProvider (Decoupled Studio Monitor Volume/Mute)
/// - Automatic RDP-Disconnect Failover to HeadlessBroadcastClockPlayer (24/7 Cloud PC broadcast continuity!)
/// - Independent PFL / Audition Preview Player on selectable cue output device
/// </summary>
public sealed class AudioPlaybackService : IDisposable
{
	private readonly AutoMixSampleProvider _autoMix = new AutoMixSampleProvider();
	private readonly BroadcastMixerBus _mixerBus;
	private readonly MasterDspProcessor _masterDsp;
	private readonly LocalMonitorVolumeProvider _monitorStage;
	private readonly SampleToWaveProvider16 _outputWaveProvider;
	private readonly DispatcherTimer _uiTimer;
	private readonly object _outputGate = new object();

	private IWavePlayer? _masterOutput;
	private int _outputDeviceNumber = -1;
	private bool _autoFallbackToHeadless = true;
	private bool _cloudUnattendedMode = true;
	private bool _isUsingHeadlessClock;
	private bool _failedOverDueToRdpDisconnect;
	private bool _manualStop;
	private bool _disposed;
	private DeckPlaybackState _state = DeckPlaybackState.Stopped;

	// PFL Audition Player
	private WaveOutEvent? _pflOutput;
	private WaveStream? _pflReader;
	private int _pflDeviceNumber = -1;

	// Live Mic Talk-Over Capture
	private WaveInEvent? _micInput;
	private BufferedWaveProvider? _micBuffer;
	private bool _micActive;

	public DeckPlaybackState State => _state;
	public TimeSpan Position => _autoMix.CurrentTrack?.Position ?? TimeSpan.Zero;
	public TimeSpan Duration => _autoMix.CurrentTrack?.Duration ?? TimeSpan.Zero;
	public TransitionPlan? PlannedNextTransition => _autoMix.PlannedNextTransition;
	public bool IsTransitioning => _autoMix.IsTransitioning;
	public bool IsMicTalkOverActive => _micActive;
	public bool IsPflPlaying => _pflOutput != null && _pflOutput.PlaybackState == PlaybackState.Playing;
	public bool IsUsingHeadlessClock => _isUsingHeadlessClock;
	public AudioTelemetrySnapshot LatestTelemetry => _masterDsp.LatestTelemetry;

	public string ActiveClockStatusText => _isUsingHeadlessClock
		? (_failedOverDueToRdpDisconnect ? "CLOUD CLOCK (RDP Failover Active)" : "CLOUD CLOCK (Headless 24/7)")
		: "HARDWARE AUDIO";

	public event Action<DeckPlaybackState, TimeSpan, TimeSpan>? PlaybackUpdated;
	public event Action<AudioTelemetrySnapshot>? TelemetryUpdated;
	public event Action<string, bool>? OutputEngineStatusChanged;
	public event Action? TrackEnded;
	public event Action<string, TransitionPlan?>? PreloadedTrackStarted;
	public event Action<string>? PlaybackFailed;
	public event Action<byte[]>? ProgramOutputAvailable;

	static AudioPlaybackService()
	{
		// Register Windows 10/11 MediaFoundation decoder for FLAC, M4A, AAC, WMA
		AudioFileProbeCore.PlatformExtendedReaderFactory = path => new MediaFoundationReader(path);
	}

	public AudioPlaybackService()
	{
		_mixerBus = new BroadcastMixerBus(_autoMix);
		_masterDsp = new MasterDspProcessor(_mixerBus, pcm => ProgramOutputAvailable?.Invoke(pcm));
		_monitorStage = new LocalMonitorVolumeProvider(_masterDsp);
		_outputWaveProvider = new SampleToWaveProvider16(_monitorStage);

		_autoMix.PreloadedTrackStarted += OnAutoMixPreloadedTrackStarted;

		if (OperatingSystem.IsWindows())
		{
			try
			{
				SystemEvents.SessionSwitch += OnWindowsSessionSwitch;
			}
			catch (Exception ex)
			{
				LogService.Warn("CloudPC", $"Could not subscribe to SystemEvents.SessionSwitch: {ex.Message}");
			}
		}

		_uiTimer = new DispatcherTimer
		{
			Interval = TimeSpan.FromMilliseconds(60.0)
		};
		_uiTimer.Tick += (_, _) =>
		{
			PlaybackUpdated?.Invoke(_state, Position, Duration);
			TelemetryUpdated?.Invoke(_masterDsp.LatestTelemetry);
		};
		_uiTimer.Start();
	}

	public static IReadOnlyList<AudioDeviceInfo> GetOutputDevices()
	{
		var devices = new List<AudioDeviceInfo>
		{
			new AudioDeviceInfo { DeviceNumber = -1, Name = "Windows Default Playback Device" },
			new AudioDeviceInfo { DeviceNumber = AppVersionInfo.HeadlessCloudClockDeviceNumber, Name = "Headless Cloud Broadcast Clock (RDP-Safe, No Soundcard)" }
		};
		try
		{
			for (int i = 0; i < WaveOut.DeviceCount; i++)
			{
				WaveOutCapabilities caps = WaveOut.GetCapabilities(i);
				devices.Add(new AudioDeviceInfo { DeviceNumber = i, Name = caps.ProductName });
			}
		}
		catch (Exception ex)
		{
			LogService.Warn("AudioDevices", $"WaveOut enumeration warning: {ex.Message}");
		}
		return devices;
	}

	public static IReadOnlyList<AudioDeviceInfo> GetInputDevices()
	{
		var devices = new List<AudioDeviceInfo>
		{
			new AudioDeviceInfo { DeviceNumber = -1, Name = "Windows Default Recording Device" }
		};
		try
		{
			for (int i = 0; i < WaveIn.DeviceCount; i++)
			{
				WaveInCapabilities caps = WaveIn.GetCapabilities(i);
				devices.Add(new AudioDeviceInfo { DeviceNumber = i, Name = caps.ProductName });
			}
		}
		catch (Exception ex)
		{
			LogService.Warn("AudioDevices", $"WaveIn enumeration warning: {ex.Message}");
		}
		return devices;
	}

	public void ApplyAudioSettings(AppSettings settings, TransitionMode? playsetModeOverride = null)
	{
		_pflDeviceNumber = settings.PflOutputDeviceNumber;
		_autoFallbackToHeadless = settings.AutoFallbackToHeadlessClock;
		_cloudUnattendedMode = settings.CloudUnattendedModeEnabled;

		_autoMix.UpdateSettings(TransitionSettings.FromAppSettings(settings), playsetModeOverride);
		_masterDsp.Configure(
			settings.BroadcastMasterGainDb,
			settings.AudioLimiterEnabled,
			settings.TruePeakCeilingDb,
			settings.SilenceWatchdogThresholdDb);
		_monitorStage.SetMonitorState(settings.LocalMonitorVolume, settings.LocalMonitorMuted);

		if (_micActive)
		{
			_mixerBus.SetMicDucking(true, settings.MicDuckDb);
		}

		// Live hot-swap output device (including switching to/from Headless Cloud Clock!)
		if (_outputDeviceNumber != settings.OutputDeviceNumber)
		{
			_outputDeviceNumber = settings.OutputDeviceNumber;
			_failedOverDueToRdpDisconnect = false;
			if (_masterOutput != null)
			{
				HotSwapOutput(forceHeadless: _outputDeviceNumber == AppVersionInfo.HeadlessCloudClockDeviceNumber, rdpReason: null);
			}
		}
	}

	public void SetMonitorState(double volume, bool muted) => _monitorStage.SetMonitorState(volume, muted);

	public void ForceHeadlessFailover(string reason)
	{
		HotSwapOutput(forceHeadless: true, rdpReason: reason);
	}

	public bool LoadTrack(LibraryItem item, bool smoothCrossfadeIfPlaying = false)
	{
		try
		{
			if (string.IsNullOrWhiteSpace(item.FilePath) || !File.Exists(item.FilePath))
			{
				throw new FileNotFoundException("Audio file was not found.", item.FilePath);
			}

			ManagedTrackStream stream = CreateManagedTrackStream(item);
			EnsureMasterOutputInitialized();

			if (smoothCrossfadeIfPlaying && _state == DeckPlaybackState.Playing && _autoMix.CurrentTrack != null)
			{
				if (_autoMix.TriggerManualCrossfade(stream))
				{
					_masterDsp.ResetSilenceCounter();
					PlaybackUpdated?.Invoke(_state, Position, Duration);
					return true;
				}
			}

			lock (_outputGate)
			{
				_manualStop = true;
				_masterOutput?.Stop();
				_autoMix.SetCurrent(stream, seekToEffectiveStart: true);
				_masterDsp.ResetSilenceCounter();
				_state = DeckPlaybackState.Stopped;
				_manualStop = false;
			}
			PlaybackUpdated?.Invoke(_state, Position, Duration);
			return true;
		}
		catch (Exception ex)
		{
			LogService.Error("Playback", $"Failed to load '{item.FilePath}'", ex);
			PlaybackFailed?.Invoke(ex.Message);
			return false;
		}
	}

	public bool PreloadNext(LibraryItem item)
	{
		try
		{
			if (string.IsNullOrWhiteSpace(item.FilePath) || !File.Exists(item.FilePath))
			{
				return false;
			}
			ManagedTrackStream nextStream = CreateManagedTrackStream(item);
			_autoMix.SetNext(nextStream);
			return true;
		}
		catch (Exception ex)
		{
			LogService.Warn("Playback", $"Could not preload '{item.Title}': {ex.Message}");
			return false;
		}
	}

	public void ClearPreloadedNext() => _autoMix.ClearNext();

	public bool TriggerSmoothSkipToPreloaded()
	{
		if (_state != DeckPlaybackState.Playing) return false;
		bool ok = _autoMix.TriggerManualCrossfade();
		if (ok) _masterDsp.ResetSilenceCounter();
		return ok;
	}

	public void Play()
	{
		if (_autoMix.CurrentTrack == null)
		{
			PlaybackFailed?.Invoke("Load a playable audio file first.");
			return;
		}
		EnsureMasterOutputInitialized();
		lock (_outputGate)
		{
			_manualStop = false;
			_masterDsp.ResetSilenceCounter();
			try
			{
				_masterOutput?.Play();
				_state = DeckPlaybackState.Playing;
			}
			catch (Exception ex) when (_autoFallbackToHeadless && !_isUsingHeadlessClock)
			{
				LogService.Warn("CloudPC", $"Hardware output Play() failed ({ex.Message}); failing over to Headless Broadcast Clock.");
				HotSwapOutputLocked(forceHeadless: true, rdpReason: ex.Message, keepPlaying: true);
			}
		}
		PlaybackUpdated?.Invoke(_state, Position, Duration);
	}

	public void Pause()
	{
		lock (_outputGate)
		{
			if (_masterOutput != null && _state == DeckPlaybackState.Playing)
			{
				_masterOutput.Pause();
				_state = DeckPlaybackState.Paused;
			}
		}
		PlaybackUpdated?.Invoke(_state, Position, Duration);
	}

	public void Stop()
	{
		lock (_outputGate)
		{
			_manualStop = true;
			_masterOutput?.Stop();
			_autoMix.SeekCurrent(_autoMix.CurrentTrack?.Cue.EffectiveStart ?? TimeSpan.Zero);
			_state = DeckPlaybackState.Stopped;
			_manualStop = false;
		}
		PlaybackUpdated?.Invoke(_state, Position, Duration);
	}

	public void Seek(TimeSpan position)
	{
		_autoMix.SeekCurrent(position);
		PlaybackUpdated?.Invoke(_state, Position, Duration);
	}

	public void SetManualTempo(double tempo) => _autoMix.SetManualTempo(tempo);

	public bool TriggerCartPad(CartPadConfig pad)
	{
		try
		{
			if (string.IsNullOrWhiteSpace(pad.FilePath) || !File.Exists(pad.FilePath)) return false;
			EnsureMasterOutputInitialized();
			WaveStream reader = AudioFileProbeCore.CreateReader(pad.FilePath);
			ISampleProvider stereo = AudioFileProbeCore.NormalizeToStereo44100(reader);
			var overlay = new OverlayChannel(
				$"cart:{pad.SlotIndex}",
				stereo,
				(float)Math.Clamp(pad.Volume, 0.0, 2.0),
				pad.DuckMusic,
				pad.DuckDb,
				reader);
			_mixerBus.AddOverlay(overlay);

			lock (_outputGate)
			{
				if (_masterOutput != null && _masterOutput.PlaybackState != PlaybackState.Playing)
				{
					_masterOutput.Play();
				}
			}
			return true;
		}
		catch (Exception ex)
		{
			LogService.Warn("Cartwall", $"Failed to trigger pad '{pad.Label}': {ex.Message}");
			PlaybackFailed?.Invoke($"Cart pad error: {ex.Message}");
			return false;
		}
	}

	public bool TriggerOverlay(string filePath, bool duckMusic, double duckDb, double volume = 1.0, string? channelId = null)
	{
		try
		{
			if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath)) return false;
			EnsureMasterOutputInitialized();
			WaveStream reader = AudioFileProbeCore.CreateReader(filePath);
			ISampleProvider stereo = AudioFileProbeCore.NormalizeToStereo44100(reader);
			var overlay = new OverlayChannel(
				string.IsNullOrWhiteSpace(channelId) ? "imaging:overlay" : channelId,
				stereo,
				(float)Math.Clamp(volume, 0.0, 2.0),
				duckMusic,
				duckDb,
				reader);
			_mixerBus.AddOverlay(overlay);

			lock (_outputGate)
			{
				if (_masterOutput != null && _masterOutput.PlaybackState != PlaybackState.Playing)
				{
					_masterOutput.Play();
				}
			}
			return true;
		}
		catch (Exception ex)
		{
			LogService.Warn("ImagingOverlay", $"Failed to trigger overlay '{Path.GetFileName(filePath)}': {ex.Message}");
			PlaybackFailed?.Invoke($"Imaging overlay error: {ex.Message}");
			return false;
		}
	}

	public void StopCartPad(int slotIndex) => _mixerBus.StopOverlay($"cart:{slotIndex}");

	public void StopAllOverlays() => _mixerBus.ClearAllOverlays();

	public bool SetMicTalkOver(bool enable, int inputDeviceNumber, double duckDb)
	{
		if (!enable)
		{
			StopMicTalkOver();
			return true;
		}
		try
		{
			StopMicTalkOver();
			EnsureMasterOutputInitialized();
			var waveFormat = new WaveFormat(44100, 16, 1);
			_micBuffer = new BufferedWaveProvider(waveFormat)
			{
				DiscardOnBufferOverflow = true,
				BufferDuration = TimeSpan.FromMilliseconds(250)
			};
			_micInput = new WaveInEvent
			{
				DeviceNumber = inputDeviceNumber,
				WaveFormat = waveFormat,
				BufferMilliseconds = 40
			};
			_micInput.DataAvailable += (_, e) => _micBuffer?.AddSamples(e.Buffer, 0, e.BytesRecorded);

			ISampleProvider micStereo = new MonoToStereoSampleProvider(_micBuffer.ToSampleProvider());
			_mixerBus.AddOverlay(new OverlayChannel("mic:talkover", micStereo, 1.0f, true, duckDb));
			_mixerBus.SetMicDucking(true, duckDb);
			_micInput.StartRecording();
			_micActive = true;

			lock (_outputGate)
			{
				if (_masterOutput != null && _masterOutput.PlaybackState != PlaybackState.Playing)
				{
					_masterOutput.Play();
				}
			}
			return true;
		}
		catch (Exception ex)
		{
			LogService.Error("MicTalkOver", "Failed to open microphone input", ex);
			StopMicTalkOver();
			PlaybackFailed?.Invoke($"Mic Talk-Over failed: {ex.Message}");
			return false;
		}
	}

	public void StopMicTalkOver()
	{
		_micActive = false;
		_mixerBus.SetMicDucking(false, -12.0);
		_mixerBus.StopOverlay("mic:talkover");
		try { _micInput?.StopRecording(); } catch { }
		_micInput?.Dispose();
		_micInput = null;
		_micBuffer = null;
	}

	public bool PlayPfl(string filePath, TimeSpan? startAt = null)
	{
		StopPfl();
		if (_pflDeviceNumber == AppVersionInfo.HeadlessCloudClockDeviceNumber)
		{
			return false;
		}
		try
		{
			if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath)) return false;
			_pflReader = AudioFileProbeCore.CreateReader(filePath);
			if (startAt.HasValue && startAt.Value > TimeSpan.Zero && startAt.Value < _pflReader.TotalTime)
			{
				_pflReader.CurrentTime = startAt.Value;
			}
			ISampleProvider stereo = AudioFileProbeCore.NormalizeToStereo44100(_pflReader);
			_pflOutput = new WaveOutEvent { DeviceNumber = _pflDeviceNumber, DesiredLatency = 100 };
			_pflOutput.Init(new SampleToWaveProvider16(stereo));
			_pflOutput.Play();
			return true;
		}
		catch (Exception ex)
		{
			LogService.Warn("PFL", $"Audition playback failed: {ex.Message}");
			StopPfl();
			return false;
		}
	}

	public void StopPfl()
	{
		try { _pflOutput?.Stop(); } catch { }
		_pflOutput?.Dispose();
		_pflOutput = null;
		_pflReader?.Dispose();
		_pflReader = null;
	}

	public static void PlayTestTone(int outputDeviceNumber)
	{
		if (outputDeviceNumber == AppVersionInfo.HeadlessCloudClockDeviceNumber)
		{
			return;
		}
		try
		{
			var gen = new SignalGenerator(44100, 2) { Gain = 0.18, Frequency = 523.25, Type = SignalGeneratorType.Sin };
			ISampleProvider shortTone = gen.Take(TimeSpan.FromMilliseconds(550));
			var output = new WaveOutEvent { DeviceNumber = outputDeviceNumber, DesiredLatency = 80 };
			output.PlaybackStopped += (_, _) => output.Dispose();
			output.Init(shortTone);
			output.Play();
		}
		catch (Exception ex)
		{
			LogService.Warn("AudioTest", $"Test tone failed: {ex.Message}");
		}
	}

	private void OnWindowsSessionSwitch(object sender, SessionSwitchEventArgs e)
	{
		if (_disposed || !_cloudUnattendedMode) return;

		LogService.Info("CloudPC", $"Windows session event: {e.Reason}");

		if (e.Reason is SessionSwitchReason.RemoteDisconnect or SessionSwitchReason.ConsoleDisconnect)
		{
			// Proactively switch to Headless Broadcast Clock BEFORE Windows tears down RDP Remote Audio!
			if (_autoFallbackToHeadless && !_isUsingHeadlessClock && _masterOutput != null)
			{
				LogService.Info("CloudPC", "RDP disconnected — switching seamlessly to Headless Broadcast Clock to keep stream alive!");
				HotSwapOutput(forceHeadless: true, rdpReason: "RDP session disconnected");
			}
		}
		else if (e.Reason is SessionSwitchReason.RemoteConnect or SessionSwitchReason.ConsoleConnect)
		{
			// If we failed over during RDP disconnect (and user's configured device wasn't explicitly Headless), restore local audio!
			if (_failedOverDueToRdpDisconnect && _outputDeviceNumber != AppVersionInfo.HeadlessCloudClockDeviceNumber)
			{
				Task.Run(async () =>
				{
					await Task.Delay(1500).ConfigureAwait(false);
					if (_disposed) return;
					LogService.Info("CloudPC", "RDP reconnected — restoring hardware/RDP audio monitor output.");
					HotSwapOutput(forceHeadless: false, rdpReason: null);
				});
			}
		}
	}

	private ManagedTrackStream CreateManagedTrackStream(LibraryItem item)
	{
		WaveStream reader = AudioFileProbeCore.CreateReader(item.FilePath);
		ISampleProvider stereo = AudioFileProbeCore.NormalizeToStereo44100(reader);
		if (item.Duration <= TimeSpan.Zero && reader.TotalTime > TimeSpan.Zero)
		{
			item.Duration = reader.TotalTime;
		}
		TrackCueSnapshot cue = TrackCueSnapshot.FromLibraryItem(item);
		return new ManagedTrackStream(
			cue,
			stereo,
			ownerDisposable: reader,
			seekAction: pos =>
			{
				TimeSpan clamped = pos < TimeSpan.Zero ? TimeSpan.Zero : (pos > reader.TotalTime ? reader.TotalTime : pos);
				reader.CurrentTime = clamped;
			});
	}

	private void EnsureMasterOutputInitialized()
	{
		lock (_outputGate)
		{
			if (_masterOutput == null)
			{
				HotSwapOutputLocked(
					forceHeadless: _outputDeviceNumber == AppVersionInfo.HeadlessCloudClockDeviceNumber,
					rdpReason: null,
					keepPlaying: false);
			}
		}
	}

	private void HotSwapOutput(bool forceHeadless, string? rdpReason)
	{
		lock (_outputGate)
		{
			bool wasPlaying = _state == DeckPlaybackState.Playing;
			HotSwapOutputLocked(forceHeadless, rdpReason, keepPlaying: wasPlaying);
		}
	}

	private void HotSwapOutputLocked(bool forceHeadless, string? rdpReason, bool keepPlaying)
	{
		if (_masterOutput != null)
		{
			_masterOutput.PlaybackStopped -= HandleMasterPlaybackStopped;
			_manualStop = true;
			try { _masterOutput.Stop(); } catch { }
			try { _masterOutput.Dispose(); } catch { }
			_masterOutput = null;
			_manualStop = false;
		}

		bool useHeadless = forceHeadless || _outputDeviceNumber == AppVersionInfo.HeadlessCloudClockDeviceNumber;

		if (!useHeadless)
		{
			try
			{
				if (WaveOut.DeviceCount <= 0)
				{
					throw new InvalidOperationException("No Windows audio playback devices are available.");
				}
				var hw = new WaveOutEvent
				{
					DesiredLatency = 90,
					NumberOfBuffers = 3,
					DeviceNumber = _outputDeviceNumber
				};
				hw.Init(_outputWaveProvider);
				hw.PlaybackStopped += HandleMasterPlaybackStopped;
				_masterOutput = hw;
				_isUsingHeadlessClock = false;
				_failedOverDueToRdpDisconnect = false;
			}
			catch (Exception ex) when (_autoFallbackToHeadless)
			{
				LogService.Warn("CloudPC", $"Hardware WaveOut unavailable ({ex.Message}); activating Headless Broadcast Clock.");
				useHeadless = true;
				rdpReason ??= ex.Message;
			}
		}

		if (useHeadless)
		{
			var headless = new HeadlessBroadcastClockPlayer();
			headless.Init(_outputWaveProvider);
			headless.PlaybackStopped += HandleMasterPlaybackStopped;
			_masterOutput = headless;
			_isUsingHeadlessClock = true;
			_failedOverDueToRdpDisconnect = !string.IsNullOrWhiteSpace(rdpReason);
		}

		if (keepPlaying && _masterOutput != null)
		{
			_masterOutput.Play();
			_state = DeckPlaybackState.Playing;
		}

		string statusText = ActiveClockStatusText;
		bool isHeadless = _isUsingHeadlessClock;
		DispatchUi(() => OutputEngineStatusChanged?.Invoke(statusText, isHeadless));
	}

	private void HandleMasterPlaybackStopped(object? sender, StoppedEventArgs e)
	{
		// Safety net: if a hardware audio device died mid-track (e.g. RDP cut off or USB unplugged),
		// immediately fail over to HeadlessBroadcastClockPlayer so the stream NEVER stops!
		bool hardwareError = e.Exception != null;
		bool unexpectedHardwareDrop = !_manualStop && !_isUsingHeadlessClock && _state == DeckPlaybackState.Playing && _autoMix.CurrentTrack != null;

		if ((hardwareError || unexpectedHardwareDrop) && _autoFallbackToHeadless && !_isUsingHeadlessClock && !_disposed)
		{
			string reason = e.Exception?.Message ?? "Audio endpoint removed (RDP/device disconnect)";
			LogService.Warn("CloudPC", $"Detected audio output interruption: {reason}. Failing over to Headless Broadcast Clock!");
			HotSwapOutput(forceHeadless: true, rdpReason: reason);
			return;
		}

		bool endedNaturally = !_manualStop && _autoMix.CurrentTrack == null;
		if (!_mixerBus.HasActiveOverlays)
		{
			_state = DeckPlaybackState.Stopped;
		}
		_manualStop = false;

		DispatchUi(() =>
		{
			PlaybackUpdated?.Invoke(_state, endedNaturally ? Duration : Position, Duration);
			if (endedNaturally)
			{
				TrackEnded?.Invoke();
			}
		});
	}

	private void OnAutoMixPreloadedTrackStarted(string filePath, TransitionPlan? plan)
	{
		DispatchUi(() =>
		{
			PreloadedTrackStarted?.Invoke(filePath, plan);
			PlaybackUpdated?.Invoke(_state, Position, Duration);
		});
	}

	private static void DispatchUi(Action action)
	{
		Dispatcher? dispatcher = Application.Current?.Dispatcher;
		if (dispatcher != null && !dispatcher.CheckAccess())
		{
			dispatcher.BeginInvoke(action);
		}
		else
		{
			action();
		}
	}

	public void Dispose()
	{
		_disposed = true;
		_uiTimer.Stop();
		if (OperatingSystem.IsWindows())
		{
			try { SystemEvents.SessionSwitch -= OnWindowsSessionSwitch; } catch { }
		}
		StopMicTalkOver();
		StopPfl();
		_mixerBus.ClearAllOverlays();
		lock (_outputGate)
		{
			if (_masterOutput != null)
			{
				_masterOutput.PlaybackStopped -= HandleMasterPlaybackStopped;
				try { _masterOutput.Stop(); } catch { }
				try { _masterOutput.Dispose(); } catch { }
				_masterOutput = null;
			}
		}
		_autoMix.Reset();
	}
}
