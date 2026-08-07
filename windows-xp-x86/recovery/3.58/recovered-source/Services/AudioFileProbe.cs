using System;
using System.IO;
using System.Linq;
using NAudio.Wave;
using NLayer.NAudioSupport;

namespace RadioApp.Services;

public static class AudioFileProbe
{
	public static readonly string[] SupportedExtensions = new string[4] { ".mp3", ".wav", ".aiff", ".aif" };

	public static AudioProbeResult Probe(string filePath)
	{
		try
		{
			if (string.IsNullOrWhiteSpace(filePath))
			{
				return AudioProbeResult.Failed("No file path was provided.");
			}
			if (!File.Exists(filePath))
			{
				return AudioProbeResult.Failed("Audio file was not found.");
			}
			string extension = Path.GetExtension(filePath).ToLowerInvariant();
			if (!Enumerable.Contains(SupportedExtensions, extension))
			{
				return AudioProbeResult.Failed("Unsupported audio format '" + extension + "'. Supported formats: MP3, WAV, AIFF.");
			}
			using WaveStream reader = CreateReader(filePath);
			byte[] buffer = new byte[Math.Min(reader.WaveFormat.AverageBytesPerSecond, 65536)];
			if (reader.Read(buffer, 0, buffer.Length) <= 0)
			{
				return AudioProbeResult.Failed("The file opened, but no audio frames could be decoded.");
			}
			return AudioProbeResult.Playable(reader.TotalTime);
		}
		catch (Exception ex)
		{
			return AudioProbeResult.Failed(ex.Message);
		}
	}

	public static WaveStream CreateReader(string filePath)
	{
		string extension = Path.GetExtension(filePath).ToLowerInvariant();
		switch (extension)
		{
		case ".mp3":
			return CreateManagedMp3Reader(filePath);
		case ".wav":
			return new WaveFileReader(filePath);
		case ".aiff":
		case ".aif":
			return new AiffFileReader(filePath);
		default:
			throw new NotSupportedException("Unsupported audio format '" + extension + "'.");
		}
	}

	private static WaveStream CreateManagedMp3Reader(string filePath)
	{
		Mp3FileReaderBase.FrameDecompressorBuilder builder = (WaveFormat waveFormat) => new Mp3FrameDecompressor(waveFormat);
		return new Mp3FileReaderBase(filePath, builder);
	}
}
