using System;
using System.IO;
using System.Text.Json;
using RadioApp.Models;

namespace RadioApp.Services;

public class AppSettingsService
{
	private readonly string _settingsPath;

	private readonly JsonSerializerOptions _jsonOptions = new JsonSerializerOptions
	{
		WriteIndented = true
	};

	public AppSettingsService()
	{
		string folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RadioApp");
		Directory.CreateDirectory(folder);
		_settingsPath = Path.Combine(folder, "settings.json");
	}

	public AppSettings Load()
	{
		AppSettings? settings = TryLoad(_settingsPath);
		if (settings != null)
		{
			return settings;
		}
		settings = TryLoad(_settingsPath + ".bak");
		return settings ?? new AppSettings();
	}

	private static AppSettings? TryLoad(string path)
	{
		if (!File.Exists(path))
		{
			return null;
		}
		try
		{
			return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path));
		}
		catch (JsonException)
		{
			return null;
		}
		catch (IOException)
		{
			return null;
		}
	}

	public void Save(AppSettings settings)
	{
		string json = JsonSerializer.Serialize(settings, _jsonOptions);
		string temporaryPath = _settingsPath + ".tmp";
		string backupPath = _settingsPath + ".bak";
		File.WriteAllText(temporaryPath, json);
		if (File.Exists(_settingsPath))
		{
			File.Replace(temporaryPath, _settingsPath, backupPath, ignoreMetadataErrors: true);
		}
		else
		{
			File.Move(temporaryPath, _settingsPath);
		}
	}
}
