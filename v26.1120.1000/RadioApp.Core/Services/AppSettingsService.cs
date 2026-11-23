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

	public string SettingsPath => _settingsPath;

	public AppSettingsService(string? customSettingsPath = null)
	{
		if (!string.IsNullOrWhiteSpace(customSettingsPath))
		{
			string? dir = Path.GetDirectoryName(customSettingsPath);
			if (!string.IsNullOrWhiteSpace(dir))
			{
				Directory.CreateDirectory(dir);
			}
			_settingsPath = customSettingsPath;
		}
		else
		{
			string folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RadioApp");
			Directory.CreateDirectory(folder);
			_settingsPath = Path.Combine(folder, "settings.json");
		}
	}

	public AppSettings Load()
	{
		AppSettings settings = TryLoad(_settingsPath) ?? TryLoad(_settingsPath + ".bak") ?? new AppSettings();
		HydrateSecrets(settings);
		EnsureCartPads(settings);
		return settings;
	}

	public void Save(AppSettings settings)
	{
		EnsureCartPads(settings);
		AppSettings persisted = settings.DeepClone();
		if (!string.IsNullOrEmpty(persisted.StreamPassword))
		{
			persisted.StreamPasswordEncrypted = SecretProtector.Protect(persisted.StreamPassword);
			persisted.StreamPassword = string.Empty;
		}
		else
		{
			persisted.StreamPasswordEncrypted = string.Empty;
		}

		string json = JsonSerializer.Serialize(persisted, _jsonOptions);
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

	public void ExportBackup(AppSettings settings, string targetPath)
	{
		AppSettings exportCopy = settings.DeepClone();
		exportCopy.StreamPassword = string.Empty;
		exportCopy.StreamPasswordEncrypted = string.Empty;
		File.WriteAllText(targetPath, JsonSerializer.Serialize(exportCopy, _jsonOptions));
	}

	public AppSettings? ImportBackup(string sourcePath)
	{
		AppSettings? loaded = TryLoad(sourcePath);
		if (loaded != null)
		{
			HydrateSecrets(loaded);
			EnsureCartPads(loaded);
		}
		return loaded;
	}

	private static void HydrateSecrets(AppSettings settings)
	{
		if (!string.IsNullOrEmpty(settings.StreamPasswordEncrypted))
		{
			string decrypted = SecretProtector.Unprotect(settings.StreamPasswordEncrypted);
			if (!string.IsNullOrEmpty(decrypted))
			{
				settings.StreamPassword = decrypted;
			}
		}
	}

	private static void EnsureCartPads(AppSettings settings)
	{
		if (settings.CartPads == null || settings.CartPads.Count < 8)
		{
			var defaults = CartPadConfig.CreateDefaults();
			if (settings.CartPads != null)
			{
				foreach (CartPadConfig existing in settings.CartPads)
				{
					if (existing.SlotIndex >= 0 && existing.SlotIndex < defaults.Count)
					{
						defaults[existing.SlotIndex] = existing;
					}
				}
			}
			settings.CartPads = defaults;
		}
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
}
