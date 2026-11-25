using System;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace RadioApp.Services;

/// <summary>
/// Prevents Windows Cloud PCs from sleeping, idling, or starving RadioApp when RDP sessions disconnect.
/// Uses Win32 SetThreadExecutionState and process priority management safely.
/// </summary>
public sealed class CloudKeepAliveService : IDisposable
{
	[Flags]
	private enum ExecutionState : uint
	{
		ES_AWAYMODE_REQUIRED = 0x00000040,
		ES_CONTINUOUS = 0x80000000,
		ES_DISPLAY_REQUIRED = 0x00000002,
		ES_SYSTEM_REQUIRED = 0x00000001
	}

	[DllImport("kernel32.dll", SetLastError = true)]
	private static extern ExecutionState SetThreadExecutionState(ExecutionState esFlags);

	private bool _keepAwakeActive;
	private bool _highPriorityActive;

	public bool IsKeepAwakeActive => _keepAwakeActive;
	public bool IsHighPriorityActive => _highPriorityActive;

	public void Apply(bool keepSystemAwake, bool elevatePriority)
	{
		ApplyKeepAwake(keepSystemAwake);
		ApplyProcessPriority(elevatePriority);
	}

	public void ApplyKeepAwake(bool enable)
	{
		_keepAwakeActive = enable;
		if (!OperatingSystem.IsWindows()) return;

		try
		{
			ExecutionState flags = enable
				? (ExecutionState.ES_CONTINUOUS | ExecutionState.ES_SYSTEM_REQUIRED | ExecutionState.ES_AWAYMODE_REQUIRED)
				: ExecutionState.ES_CONTINUOUS;
			SetThreadExecutionState(flags);
		}
		catch (Exception ex)
		{
			LogService.Warn("CloudKeepAlive", $"SetThreadExecutionState failed: {ex.Message}");
		}
	}

	public void ApplyProcessPriority(bool highPriority)
	{
		_highPriorityActive = highPriority;
		if (!OperatingSystem.IsWindows()) return;

		try
		{
			using Process current = Process.GetCurrentProcess();
			current.PriorityClass = highPriority ? ProcessPriorityClass.AboveNormal : ProcessPriorityClass.Normal;
		}
		catch (Exception ex)
		{
			LogService.Warn("CloudKeepAlive", $"Priority adjustment skipped: {ex.Message}");
		}
	}

	public void Dispose()
	{
		ApplyKeepAwake(false);
	}
}
