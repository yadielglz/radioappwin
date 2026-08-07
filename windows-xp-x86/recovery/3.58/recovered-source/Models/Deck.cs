using System;

namespace RadioApp.Models;

public class Deck
{
	public DeckId Id { get; set; }

	public string? FilePath { get; set; }

	public string Title { get; set; } = string.Empty;

	public double Volume { get; set; } = 0.75;

	public TimeSpan Position { get; set; }

	public TimeSpan Duration { get; set; }

	public DeckPlaybackState PlaybackState { get; set; }
}
