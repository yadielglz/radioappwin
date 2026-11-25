using System;
using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace RadioApp.Controls;

/// <summary>
/// Compact 60-minute broadcast hour strip visually illustrating the Station ID
/// Target Minute, Before/After Tolerance Window, and live current-minute marker.
/// </summary>
public sealed class StationIdWindowBarControl : FrameworkElement
{
	private static readonly Typeface MonoTypeface = new Typeface(new FontFamily("Consolas, Courier New"), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal);

	public static readonly DependencyProperty TargetMinuteProperty = DependencyProperty.Register(
		nameof(TargetMinute), typeof(int), typeof(StationIdWindowBarControl),
		new FrameworkPropertyMetadata(0, FrameworkPropertyMetadataOptions.AffectsRender));

	public static readonly DependencyProperty ToleranceBeforeProperty = DependencyProperty.Register(
		nameof(ToleranceBefore), typeof(int), typeof(StationIdWindowBarControl),
		new FrameworkPropertyMetadata(5, FrameworkPropertyMetadataOptions.AffectsRender));

	public static readonly DependencyProperty ToleranceAfterProperty = DependencyProperty.Register(
		nameof(ToleranceAfter), typeof(int), typeof(StationIdWindowBarControl),
		new FrameworkPropertyMetadata(5, FrameworkPropertyMetadataOptions.AffectsRender));

	public int TargetMinute
	{
		get => (int)GetValue(TargetMinuteProperty);
		set => SetValue(TargetMinuteProperty, value);
	}

	public int ToleranceBefore
	{
		get => (int)GetValue(ToleranceBeforeProperty);
		set => SetValue(ToleranceBeforeProperty, value);
	}

	public int ToleranceAfter
	{
		get => (int)GetValue(ToleranceAfterProperty);
		set => SetValue(ToleranceAfterProperty, value);
	}

	public StationIdWindowBarControl()
	{
		Height = 46;
		SnapsToDevicePixels = true;
	}

	protected override void OnRender(DrawingContext dc)
	{
		base.OnRender(dc);
		double w = ActualWidth;
		double h = ActualHeight;
		if (w < 40 || h < 20) return;

		double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
		const double padX = 8;
		const double axisH = 15;
		double trackW = Math.Max(10, w - padX * 2);
		double trackY = axisH;
		double trackH = Math.Max(16, h - axisH - 6);

		dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromRgb(15, 23, 42)), new Pen(new SolidColorBrush(Color.FromRgb(51, 65, 85)), 1), new Rect(0, 0, w, h), 6, 6);
		dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromRgb(21, 31, 48)), null, new Rect(padX, trackY, trackW, trackH), 4, 4);

		int target = Math.Clamp(TargetMinute, 0, 59);
		int before = Math.Clamp(ToleranceBefore, 0, 30);
		int after = Math.Clamp(ToleranceAfter, 0, 30);

		int startMin = target - before;
		int endMin = target + after;

		var winBrush = new SolidColorBrush(Color.FromArgb(145, 245, 158, 11));
		var winPen = new Pen(new SolidColorBrush(Color.FromRgb(251, 191, 36)), 1.2);

		void DrawSpan(double s, double e)
		{
			double x1 = padX + (s / 60.0) * trackW;
			double x2 = padX + (e / 60.0) * trackW;
			dc.DrawRoundedRectangle(winBrush, winPen, new Rect(x1, trackY + 2, Math.Max(3, x2 - x1), trackH - 4), 3, 3);
		}

		// Handle wrap-around past :00 (e.g. target :00 with before=5 -> :55..:60 and :00..:05)
		if (startMin < 0)
		{
			DrawSpan(60 + startMin, 60);
			DrawSpan(0, Math.Min(60, endMin));
		}
		else if (endMin > 60)
		{
			DrawSpan(startMin, 60);
			DrawSpan(0, endMin - 60);
		}
		else
		{
			DrawSpan(startMin, Math.Max(startMin + 0.5, endMin));
		}

		// Target minute marker line
		double tx = padX + (target / 60.0) * trackW;
		dc.DrawLine(new Pen(new SolidColorBrush(Color.FromRgb(56, 189, 248)), 2.5), new Point(tx, trackY), new Point(tx, trackY + trackH));

		// Minute axis ticks (:00, :15, :30, :45, :60)
		var labelBrush = new SolidColorBrush(Color.FromRgb(148, 163, 184));
		var tickPen = new Pen(new SolidColorBrush(Color.FromArgb(55, 255, 255, 255)), 1);
		for (int m = 0; m <= 60; m += 15)
		{
			double x = padX + (m / 60.0) * trackW;
			dc.DrawLine(tickPen, new Point(x, trackY), new Point(x, trackY + trackH));
			var ft = new FormattedText($":{m % 60:00}", CultureInfo.InvariantCulture, FlowDirection.LeftToRight, MonoTypeface, 9.5, labelBrush, dpi);
			double lx = m == 60 ? x - ft.Width : (m == 0 ? x : x - ft.Width / 2.0);
			dc.DrawText(ft, new Point(lx, 2));
		}

		// Live current-minute needle
		DateTime now = DateTime.Now;
		double nowMin = now.Minute + now.Second / 60.0;
		double nx = padX + (nowMin / 60.0) * trackW;
		dc.DrawLine(new Pen(new SolidColorBrush(Color.FromRgb(52, 211, 153)), 2.0), new Point(nx, trackY - 2), new Point(nx, trackY + trackH + 2));
	}
}
