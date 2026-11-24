using System;
using System.Diagnostics;
using Microsoft.Win32;

namespace RadioApp.Services;

/// <summary>
/// Manages per-user Windows Startup registration (HKCU\Software\Microsoft\Windows\CurrentVersion\Run)
/// so a Cloud PC automatically relaunches RadioApp Studio after Windows Update reboots.
/// </summary>
public static class WindowsStartupService
{
	private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
	private const string ValueName = "RadioAppStudio";

	public static bool IsAutoStartEnabled()
	{
		if (!OperatingSystem.IsWindows()) return false;
		try
		{
			using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
			object? value = key?.GetValue(ValueName);
			return value is string s && !string.IsNullOrWhiteSpace(s);
		}
		catch
		{
			return false;
		}
	}

	public static bool SetAutoStart(bool enable)
	{
		if (!OperatingSystem.IsWindows()) return false;
		try
		{
			using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
			if (key == null) return false;
			if (enable)
			{
				string? exePath = Environment.ProcessPath ?? Process.GetCurrentProcess().MainModule?.FileName;
				if (string.IsNullOrWhiteSpace(exePath)) return false;
				key.SetValue(ValueName, $"\"{exePath}\"");
				LogService.Info("CloudPC", $"Registered Windows login auto-start: {exePath}");
			}
			else
			{
				key.DeleteValue(ValueName, throwOnMissingValue: false);
				LogService.Info("CloudPC", "Removed Windows login auto-start registration.");
			}
			return true;
		}
		catch (Exception ex)
		{
			LogService.Warn("CloudPC", $"Failed to update Windows startup key: {ex.Message}");
			return false;
		}
	}
}
