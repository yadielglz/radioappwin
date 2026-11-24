using System;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace RadioApp.Controls;

/// <summary>
/// Interactive broadcast waveform timeline displaying:
/// - 200-bar stereo peak waveform (or clean fallback bar if track is still analyzing)
/// - Played progress fill + glowing playhead
/// - Intro (cyan), MixOut (amber), and End (red) cue markers
/// - Shaded AutoMix crossfade zone
/// - Click/drag scrubbing
/// </summary>
public sealed class WaveformCueControl : FrameworkElement
{
	public static readonly DependencyProperty WaveformPeaksProperty =
		DependencyProperty.Register(nameof(WaveformPeaks), typeof(byte[]), typeof(WaveformCueControl),
			new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

	public static readonly DependencyProperty ProgressPercentProperty =
		DependencyProperty.Register(nameof(ProgressPercent), typeof(double), typeof(WaveformCueControl),
			new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

	public static readonly DependencyProperty DurationSecondsProperty =
		DependencyProperty.Register(nameof(DurationSeconds), typeof(double), typeof(WaveformCueControl),
			new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

	public static readonly DependencyProperty IntroSecondsProperty =
		DependencyProperty.Register(nameof(IntroSeconds), typeof(double?), typeof(WaveformCueControl),
			new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

	public static readonly DependencyProperty MixOutSecondsProperty =
		DependencyProperty.Register(nameof(MixOutSeconds), typeof(double?), typeof(WaveformCueControl),
			new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

	public static readonly DependencyProperty EndSecondsProperty =
		DependencyProperty.Register(nameof(EndSeconds), typeof(double?), typeof(WaveformCueControl),
			new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

	public static readonly DependencyProperty SeekCommandProperty =
		DependencyProperty.Register(nameof(SeekCommand), typeof(ICommand), typeof(WaveformCueControl));

	public byte[]? WaveformPeaks
	{
		get => (byte[]?)GetValue(WaveformPeaksProperty);
		set => SetValue(WaveformPeaksProperty, value);
	}

	public double ProgressPercent
	{
		get => (double)GetValue(ProgressPercentProperty);
		set => SetValue(ProgressPercentProperty, value);
	}

	public double DurationSeconds
	{
		get => (double)GetValue(DurationSecondsProperty);
		set => SetValue(DurationSecondsProperty, value);
	}

	public double? IntroSeconds
	{
		get => (double?)GetValue(IntroSecondsProperty);
		set => SetValue(IntroSecondsProperty, value);
	}

	public double? MixOutSeconds
	{
		get => (double?)GetValue(MixOutSecondsProperty);
		set => SetValue(MixOutSecondsProperty, value);
	}

	public double? EndSeconds
	{
		get => (double?)GetValue(EndSecondsProperty);
		set => SetValue(EndSecondsProperty, value);
	}

	public ICommand? SeekCommand
	{
		get => (ICommand?)GetValue(SeekCommandProperty);
		set => SetValue(SeekCommandProperty, value);
	}

	private static readonly Brush BgBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x07, 0x0E, 0x18)));
	private static readonly Pen BorderPen = Freeze(new Pen(new SolidColorBrush(Color.FromRgb(0x1D, 0x3A, 0x5F)), 1.0));
	private static readonly Brush PlayedBarBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x38, 0xBD, 0xF8)));
	private static readonly Brush UnplayedBarBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x28, 0x41, 0x62)));
	private static readonly Brush CrossfadeZoneBrush = Freeze(new SolidColorBrush(Color.FromArgb(0x38, 0xF5, 0x9E, 0x0B)));
	private static readonly Pen PlayheadPen = Freeze(new Pen(new SolidColorBrush(Color.FromRgb(0xF9, 0xFA, 0xFB)), 2.0));
	private static readonly Pen IntroPen = Freeze(new Pen(new SolidColorBrush(Color.FromRgb(0x06, 0xB6, 0xD4)), 1.5));
	private static readonly Pen MixOutPen = Freeze(new Pen(new SolidColorBrush(Color.FromRgb(0xF5, 0x9E, 0x0B)), 1.5));
	private static readonly Pen EndPen = Freeze(new Pen(new SolidColorBrush(Color.FromRgb(0xEF, 0x44, 0x44)), 1.5));
	private static readonly Pen CenterLinePen = Freeze(new Pen(new SolidColorBrush(Color.FromArgb(0x40, 0x64, 0x74, 0x8B)), 1.0));

	private bool _dragging;

	public WaveformCueControl()
	{
		Cursor = Cursors.Hand;
		ClipToBounds = true;
		MinHeight = 64;
	}

	protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
	{
		base.OnMouseLeftButtonDown(e);
		_dragging = true;
		CaptureMouse();
		SeekAtPoint(e.GetPosition(this));
	}

	protected override void OnMouseMove(MouseEventArgs e)
	{
		base.OnMouseMove(e);
		if (_dragging && e.LeftButton == MouseButtonState.Pressed)
		{
			SeekAtPoint(e.GetPosition(this));
		}
	}

	protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
	{
		base.OnMouseLeftButtonUp(e);
		if (_dragging)
		{
			_dragging = false;
			ReleaseMouseCapture();
		}
	}

	private void SeekAtPoint(Point pt)
	{
		if (ActualWidth <= 0) return;
		double percent = Math.Clamp(pt.X / ActualWidth * 100.0, 0.0, 100.0);
		if (SeekCommand != null && SeekCommand.CanExecute(percent))
		{
			SeekCommand.Execute(percent);
		}
	}

	protected override void OnRender(DrawingContext dc)
	{
		double w = ActualWidth;
		double h = ActualHeight;
		if (w <= 4 || h <= 4) return;

		dc.DrawRoundedRectangle(BgBrush, BorderPen, new Rect(0.5, 0.5, w - 1, h - 1), 8, 8);
		double midY = h * 0.5;
		dc.DrawLine(CenterLinePen, new Point(4, midY), new Point(w - 4, midY));

		double duration = DurationSeconds;
		double playX = Math.Clamp(ProgressPercent / 100.0, 0.0, 1.0) * w;

		// Shaded AutoMix crossfade zone (MixOut -> End)
		if (duration > 0 && MixOutSeconds.HasValue)
		{
			double mixX = Math.Clamp(MixOutSeconds.Value / duration, 0.0, 1.0) * w;
			double endX = EndSeconds.HasValue ? Math.Clamp(EndSeconds.Value / duration, 0.0, 1.0) * w : w;
			if (endX > mixX + 2)
			{
				dc.DrawRectangle(CrossfadeZoneBrush, null, new Rect(mixX, 2, endX - mixX, h - 4));
			}
		}

		// Render waveform bars
		byte[]? peaks = WaveformPeaks;
		int count = peaks is { Length: > 0 } ? peaks.Length : 120;
		double padX = 6;
		double usableW = Math.Max(1, w - padX * 2);
		double barStep = usableW / count;
		double barW = Math.Max(1.4, barStep * 0.68);
		double maxHalfH = (h - 14) * 0.5;

		for (int i = 0; i < count; i++)
		{
			double x = padX + i * barStep;
			double norm = peaks is { Length: > 0 }
				? peaks[i] / 255.0
				: 0.22 + 0.14 * Math.Sin(i * 0.35) * Math.Cos(i * 0.11);
			double halfBar = Math.Max(2.0, norm * maxHalfH);
			Brush b = (x + barW * 0.5) <= playX ? PlayedBarBrush : UnplayedBarBrush;
			dc.DrawRectangle(b, null, new Rect(x, midY - halfBar, barW, halfBar * 2.0));
		}

		// Cue Markers
		if (duration > 0)
		{
			DrawMarker(dc, IntroSeconds, duration, w, h, IntroPen);
			DrawMarker(dc, MixOutSeconds, duration, w, h, MixOutPen);
			DrawMarker(dc, EndSeconds, duration, w, h, EndPen);
		}

		// Playhead
		if (playX > 1 && playX < w - 1)
		{
			dc.DrawLine(PlayheadPen, new Point(playX, 2), new Point(playX, h - 2));
		}
	}

	private static void DrawMarker(DrawingContext dc, double? sec, double duration, double w, double h, Pen pen)
	{
		if (!sec.HasValue || sec.Value <= 0 || sec.Value >= duration) return;
		double x = Math.Clamp(sec.Value / duration, 0.0, 1.0) * w;
		dc.DrawLine(pen, new Point(x, 3), new Point(x, h - 3));
	}

	private static T Freeze<T>(T freezable) where T : Freezable
	{
		freezable.Freeze();
		return freezable;
	}
}
