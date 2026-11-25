using System;

namespace RadioApp.Models;

public static class AppVersionInfo
{
	public const string Version = "26.1125.1210";
	public const string DisplayTitle = "RadioApp Studio — v26.1125.1210";
	public const string ProductName = "RadioApp Studio";
	public const string Manufacturer = "Glz Technical Services | Glz Tech";
	public const string DefaultGitHubRepository = "yadielglz/radioappwin";
	public const int CurrentAnalysisVersion = 1;
	public const int HeadlessCloudClockDeviceNumber = -2;
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
	AutoMix = 0,
	SmartSegue = 1,
	TrueGapless = 2,
	ClassicCrossfade = 3,
	RadioGapless = 4
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

public enum SweeperPlaybackMode
{
	QueueBetweenTracks,
	OverlayOverIntro,
	SmartAuto
}

public enum StationIdEnforcementMode
{
	QueueInWindow,
	HardInterruptAtDeadline,
	OverlayAtTarget
}

public enum RotationSourceKind
{
	AllMusic,
	Tag,
	Category,
	Playset
}

[Flags]
public enum RotationDayMask
{
	None = 0,
	Sunday = 1 << 0,
	Monday = 1 << 1,
	Tuesday = 1 << 2,
	Wednesday = 1 << 3,
	Thursday = 1 << 4,
	Friday = 1 << 5,
	Saturday = 1 << 6,
	Weekdays = Monday | Tuesday | Wednesday | Thursday | Friday,
	Weekends = Saturday | Sunday,
	All = Weekdays | Weekends
}
