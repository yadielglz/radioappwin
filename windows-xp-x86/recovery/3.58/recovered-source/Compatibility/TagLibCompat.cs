using System;
using System.IO;
using System.Text;
using RadioApp.Services;

namespace TagLib
{
	public sealed class Tag
	{
		public string Title { get; internal set; }
		public string Album { get; internal set; }
		public string[] Performers { get; internal set; }
		public string FirstPerformer { get { return Performers != null && Performers.Length != 0 ? Performers[0] : null; } }
	}

	public sealed class Properties
	{
		public TimeSpan Duration { get; internal set; }
	}

	public sealed class File : IDisposable
	{
		public Tag Tag { get; private set; }
		public Properties Properties { get; private set; }

		private File(string path)
		{
			Tag = new Tag { Title = Path.GetFileNameWithoutExtension(path), Album = string.Empty, Performers = new string[0] };
			Properties = new Properties();
			ReadId3v1(path);
			try { using (NAudio.Wave.WaveStream reader = AudioFileProbe.CreateReader(path)) Properties.Duration = reader.TotalTime; } catch { }
		}

		public static File Create(string path) { return new File(path); }

		private void ReadId3v1(string path)
		{
			if (!string.Equals(Path.GetExtension(path), ".mp3", StringComparison.OrdinalIgnoreCase)) return;
			using (FileStream stream = System.IO.File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
			{
				if (stream.Length < 128) return;
				stream.Seek(-128, SeekOrigin.End);
				byte[] data = new byte[128];
				if (stream.Read(data, 0, data.Length) != data.Length || Encoding.ASCII.GetString(data, 0, 3) != "TAG") return;
				Tag.Title = ReadText(data, 3, 30, Tag.Title);
				string artist = ReadText(data, 33, 30, string.Empty);
				Tag.Performers = string.IsNullOrEmpty(artist) ? new string[0] : new[] { artist };
				Tag.Album = ReadText(data, 63, 30, string.Empty);
			}
		}

		private static string ReadText(byte[] data, int offset, int count, string fallback)
		{
			string value = Encoding.GetEncoding(1252).GetString(data, offset, count).TrimEnd('\0', ' ');
			return string.IsNullOrEmpty(value) ? fallback : value;
		}

		public void Dispose() { }
	}
}
