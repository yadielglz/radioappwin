using System;
using System.IO;
using System.Text;

namespace RadioApp.Audio;

public readonly record struct Mp3GaplessInfo(int EncoderDelaySamples, int EncoderPaddingSamples, bool HasLameTag);

/// <summary>
/// Parses MP3 ID3v2 + MPEG frame + Xing/Info/LAME headers to extract exact encoder delay
/// and padding sample counts for true zero-gap sample-accurate playback.
/// </summary>
public static class Mp3GaplessHeaderParser
{
	private const int Mp3DecoderDelaySamples = 529;

	public static Mp3GaplessInfo Parse(string filePath)
	{
		try
		{
			if (string.IsNullOrWhiteSpace(filePath) ||
				!string.Equals(Path.GetExtension(filePath), ".mp3", StringComparison.OrdinalIgnoreCase) ||
				!File.Exists(filePath))
			{
				return default;
			}

			using FileStream stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
			return Parse(stream);
		}
		catch
		{
			return default;
		}
	}

	public static Mp3GaplessInfo Parse(Stream stream)
	{
		if (stream == null || !stream.CanRead)
		{
			return default;
		}

		long startOffset = 0;
		byte[] id3Header = new byte[10];
		if (stream.Read(id3Header, 0, 10) == 10 &&
			id3Header[0] == (byte)'I' &&
			id3Header[1] == (byte)'D' &&
			id3Header[2] == (byte)'3')
		{
			int tagSize = ((id3Header[6] & 0x7F) << 21) |
						  ((id3Header[7] & 0x7F) << 14) |
						  ((id3Header[8] & 0x7F) << 7) |
						  (id3Header[9] & 0x7F);
			startOffset = 10 + tagSize;
			if ((id3Header[5] & 0x10) != 0)
			{
				startOffset += 10; // ID3v2.4 footer
			}
		}

		if (stream.CanSeek)
		{
			stream.Position = Math.Min(startOffset, stream.Length);
		}

		byte[] scan = new byte[8192];
		int bytesRead = stream.Read(scan, 0, scan.Length);
		if (bytesRead < 200)
		{
			return default;
		}

		// Find first Xing / Info header inside first MPEG frame
		int xingIndex = FindAsciiTag(scan, bytesRead, "Xing");
		if (xingIndex < 0)
		{
			xingIndex = FindAsciiTag(scan, bytesRead, "Info");
		}
		if (xingIndex < 0 || xingIndex + 150 >= bytesRead)
		{
			return default;
		}

		// LAME info tag starts 120 bytes after "Xing"/"Info"
		// Encoder delay (12 bits) + padding (12 bits) live at offset +21 inside the LAME tag (xingIndex + 141)
		int lameStart = xingIndex + 120;
		int delayOffset = lameStart + 21;
		if (delayOffset + 2 >= bytesRead)
		{
			return default;
		}

		string tagPrefix = Encoding.ASCII.GetString(scan, lameStart, 4);
		bool isKnownEncoder = tagPrefix is "LAME" or "Lavf" or "Lavc" or "GOGO" or "L3.9";
		if (!isKnownEncoder)
		{
			return default;
		}

		int b0 = scan[delayOffset];
		int b1 = scan[delayOffset + 1];
		int b2 = scan[delayOffset + 2];

		int encoderDelay = (b0 << 4) | (b1 >> 4);
		int encoderPadding = ((b1 & 0x0F) << 8) | b2;

		if (encoderDelay < 0 || encoderDelay > 4095 || encoderPadding < 0 || encoderPadding > 4095)
		{
			return default;
		}

		return new Mp3GaplessInfo(encoderDelay + Mp3DecoderDelaySamples, encoderPadding, true);
	}

	private static int FindAsciiTag(byte[] buffer, int length, string tag)
	{
		byte t0 = (byte)tag[0];
		byte t1 = (byte)tag[1];
		byte t2 = (byte)tag[2];
		byte t3 = (byte)tag[3];
		int limit = length - 4;
		for (int i = 0; i <= limit; i++)
		{
			if (buffer[i] == t0 && buffer[i + 1] == t1 && buffer[i + 2] == t2 && buffer[i + 3] == t3)
			{
				return i;
			}
		}
		return -1;
	}
}
