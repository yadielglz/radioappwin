using System;
using System.IO;
using System.Linq;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using NLayer.NAudioSupport;
using RadioApp.Audio;

namespace RadioApp.Services;

public sealed record AudioProbeResult(bool IsPlayable, TimeSpan Duration, string ErrorMessage)
{
	public static AudioProbeResult Playable(TimeSpan duration) => new AudioProbeResult(true, duration, string.Empty);
	public static AudioProbeResult Failed(string error) => new AudioProbeResult(false, TimeSpan.Zero, error);
}

/// <summary>
/// Cross-platform core audio reader & stereo 44.1kHz normalizer.
/// Windows host registers MediaFoundation fallback for FLAC, M4A, AAC, WMA.
/// </summary>
public static class AudioFileProbeCore
{
	public const int StandardSampleRate = 44100;
	public const int StandardChannels = 2;

	public static readonly string[] CoreManagedExtensions = { ".mp3", ".wav", ".aiff", ".aif" };
	public static readonly string[] ExtendedWindowsExtensions = { ".flac", ".m4a", ".aac", ".wma" };

	public static Func<string, WaveStream>? PlatformExtendedReaderFactory { get; set; }

	public static string[] SupportedExtensions => PlatformExtendedReaderFactory != null
		? CoreManagedExtensions.Concat(ExtendedWindowsExtensions).ToArray()
		: CoreManagedExtensions;

	public static string OpenFileDialogFilter =>
		"Playable audio files|*.mp3;*.wav;*.flac;*.m4a;*.aac;*.aiff;*.aif;*.wma|All files|*.*";

	public static AudioProbeResult Probe(string filePath)
	{
		try
		{
			if (string.IsNullOrWhiteSpace(filePath))
				return AudioProbeResult.Failed("No file path was provided.");
			if (!File.Exists(filePath))
				return AudioProbeResult.Failed("Audio file was not found.");

			string ext = Path.GetExtension(filePath).ToLowerInvariant();
			if (!SupportedExtensions.Contains(ext))
				return AudioProbeResult.Failed($"Unsupported audio format '{ext}'.");

			using WaveStream reader = CreateReader(filePath);
			byte[] buffer = new byte[Math.Min(Math.Max(4096, reader.WaveFormat.AverageBytesPerSecond), 65536)];
			if (reader.Read(buffer, 0, buffer.Length) <= 0)
				return AudioProbeResult.Failed("File opened, but no audio frames could be decoded.");

			return AudioProbeResult.Playable(reader.TotalTime);
		}
		catch (Exception ex)
		{
			return AudioProbeResult.Failed(ex.Message);
		}
	}

	public static WaveStream CreateReader(string filePath)
	{
		string ext = Path.GetExtension(filePath).ToLowerInvariant();
		switch (ext)
		{
			case ".mp3":
				return new Mp3FileReaderBase(filePath, wf => new Mp3FrameDecompressor(wf));
			case ".wav":
				return new WaveFileReader(filePath);
			case ".aiff":
			case ".aif":
				return new AiffFileReader(filePath);
			default:
				if (PlatformExtendedReaderFactory != null && ExtendedWindowsExtensions.Contains(ext))
				{
					return PlatformExtendedReaderFactory(filePath);
				}
				throw new NotSupportedException($"Unsupported audio format '{ext}'.");
		}
	}

	public static ISampleProvider NormalizeToStereo44100(WaveStream reader)
	{
		ISampleProvider provider = reader.ToSampleProvider();
		provider = provider.WaveFormat.Channels switch
		{
			1 => new MonoToStereoSampleProvider(provider),
			2 => provider,
			_ => new MultiplexingSampleProvider(new[] { provider }, 2)
		};

		if (provider.WaveFormat.SampleRate != StandardSampleRate)
		{
			provider = new WdlResamplingSampleProvider(provider, StandardSampleRate);
		}

		return provider;
	}

	public static TrackAnalysisResult AnalyzeFile(string filePath, double silenceThresholdDb = -44.0)
	{
		Mp3GaplessInfo gaplessInfo = Mp3GaplessHeaderParser.Parse(filePath);
		using WaveStream reader = CreateReader(filePath);
		ISampleProvider stereo = NormalizeToStereo44100(reader);
		TrackAnalysisResult result = AudioTrackAnalyzer.Analyze(stereo, silenceThresholdDb);
		result.EncoderDelaySamples = gaplessInfo.EncoderDelaySamples;
		result.EncoderPaddingSamples = gaplessInfo.EncoderPaddingSamples;
		return result;
	}
}
