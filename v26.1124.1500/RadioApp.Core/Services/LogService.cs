using System;
using System.IO;
using System.Linq;

namespace RadioApp.Services;

public static class LogService
{
	private static readonly object Gate = new object();
	private static string? _logFolder;

	public static string LogFolder
	{
		get
		{
			if (_logFolder == null)
			{
				_logFolder = Path.Combine(
					Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
					"RadioApp",
					"logs");
				Directory.CreateDirectory(_logFolder);
			}
			return _logFolder;
		}
	}

	public static string CurrentLogFile =>
		Path.Combine(LogFolder, $"radioapp-{DateTime.Now:yyyyMMdd}.log");

	public static void Info(string category, string message) => Write("INFO", category, message);

	public static void Warn(string category, string message) => Write("WARN", category, message);

	public static void Error(string category, string message, Exception? ex = null)
	{
		string full = ex == null ? message : $"{message}{Environment.NewLine}{ex}";
		Write("ERROR", category, full);
	}

	public static void PruneOldLogs(int keepDays = 14)
	{
		try
		{
			DateTime cutoff = DateTime.UtcNow.AddDays(-Math.Max(1, keepDays));
			foreach (string file in Directory.EnumerateFiles(LogFolder, "radioapp-*.log"))
			{
				if (File.GetLastWriteTimeUtc(file) < cutoff)
				{
					File.Delete(file);
				}
			}
		}
		catch
		{
		}
	}

	private static void Write(string level, string category, string message)
	{
		try
		{
			lock (Gate)
			{
				string line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] [{level}] [{category}] {message}{Environment.NewLine}";
				File.AppendAllText(CurrentLogFile, line);
			}
		}
		catch
		{
			// Never crash the app from the logger itself
		}
	}
}
