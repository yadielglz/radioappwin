namespace RadioApp.Models;

public static class AppVersionInfo
{
	public const string Version = "26.1120.1000";
	public const string DisplayTitle = "RadioApp Studio — v26.1120.1000";
	public const string ProductName = "RadioApp Studio";
	public const string Manufacturer = "Glz Technical Services | Glz Tech";
	public const int CurrentAnalysisVersion = 1;
}

public enum DeckId
{
	A,
	B
}

public enum DeckPlaybackState
{
	Stopped,
	Playing,
	Paused
}

public enum StreamServerType
{
	Icecast,
	Shoutcast
}

public enum ScheduleEventType
{
	Sweep,
	StationId,
	Commercial,
	Playlist,
	VoiceTrack,
	Playset
}

public enum ScheduleRepeatMode
{
	None,
	Interval,
	Daily,
	Weekly
}

public enum TransitionMode
{
	AutoMix,
	SmartSegue,
	TrueGapless,
	ClassicCrossfade
}

public enum CrossfadeCurve
{
	BassSwapEq,
	EqualPower,
	SCurve,
	Linear
}

public enum TrackEdgeProfile
{
	Unknown,
	Cold,
	Ramp,
	Fade,
	Rhythmic
}

public enum PlaysetKind
{
	Manual,
	SmartRule
}
