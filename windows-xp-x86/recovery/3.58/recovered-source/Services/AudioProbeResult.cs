using System;

namespace RadioApp.Services;

public sealed record AudioProbeResult(bool IsPlayable, TimeSpan Duration, string? ErrorMessage)
{
	public static AudioProbeResult Playable(TimeSpan duration)
	{
		return new AudioProbeResult(IsPlayable: true, duration, null);
	}

	public static AudioProbeResult Failed(string errorMessage)
	{
		return new AudioProbeResult(IsPlayable: false, TimeSpan.Zero, errorMessage);
	}
}
