using System;
using System.Windows;
using System.Windows.Media;

namespace RadioApp.Controls;

/// <summary>
/// Studio-grade Stereo Peak + Peak-Hold + Limiter Gain Reduction LED meter.
/// Scales cleanly from -48 dBFS to 0 dBFS with green / amber / red broadcast zones.
/// </summary>
public sealed class StereoVuMeterControl : FrameworkElement
{
	public static readonly DependencyProperty LeftDbProperty =
		DependencyProperty.Register(nameof(LeftDb), typeof(double), typeof(StereoVuMeterControl),
			new FrameworkPropertyMetadata(-72.0, FrameworkPropertyMetadataOptions.AffectsRender, OnLevelChanged));

	public static readonly DependencyProperty RightDbProperty =
		DependencyProperty.Register(nameof(RightDb), typeof(double), typeof(StereoVuMeterControl),
			new FrameworkPropertyMetadata(-72.0, FrameworkPropertyMetadataOptions.AffectsRender, OnLevelChanged));

	public static readonly DependencyProperty GainReductionDbProperty =
		DependencyProperty.Register(nameof(GainReductionDb), typeof(double), typeof(StereoVuMeterControl),
			new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

	public double LeftDb
	{
		get => (double)GetValue(LeftDbProperty);
		set => SetValue(LeftDbProperty, value);
	}

	public double RightDb
	{
		get => (double)GetValue(RightDbProperty);
		set => SetValue(RightDbProperty, value);
	}

	public double GainReductionDb
	{
		get => (double)GetValue(GainReductionDbProperty);
		set => SetValue(GainReductionDbProperty, value);
	}

	private double _peakHoldL = -72.0;
	private double _peakHoldR = -72.0;

	private static readonly Brush TrackBg = Freeze(new SolidColorBrush(Color.FromRgb(0x09, 0x13, 0x1F)));
	private static readonly Pen TrackBorder = Freeze(new Pen(new SolidColorBrush(Color.FromRgb(0x1E, 0x3A, 0x5F)), 1.0));
	private static readonly Brush PeakHoldBrush = Freeze(new SolidColorBrush(Color.FromRgb(0xF9, 0xFA, 0xFB)));
	private static readonly Brush GrBrush = Freeze(new SolidColorBrush(Color.FromRgb(0xF5, 0x9E, 0x0B)));
	private static readonly LinearGradientBrush MeterGradient = Freeze(new LinearGradientBrush
	{
		StartPoint = new Point(0, 0),
		EndPoint = new Point(1, 0),
		GradientStops = new GradientStopCollection
		{
			new GradientStop(Color.FromRgb(0x10, 0xB9, 0x81), 0.0),
			new GradientStop(Color.FromRgb(0x34, 0xD3, 0x99), 0.68),
			new GradientStop(Color.FromRgb(0xF5, 0x9E, 0x0B), 0.84),
			new GradientStop(Color.FromRgb(0xEF, 0x44, 0x44), 0.96)
		}
	});

	public StereoVuMeterControl()
	{
		MinHeight = 32;
		ClipToBounds = true;
	}

	private static void OnLevelChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
	{
		if (d is StereoVuMeterControl ctrl)
		{
			ctrl._peakHoldL = Math.Max(ctrl.LeftDb, ctrl._peakHoldL - 0.65);
			ctrl._peakHoldR = Math.Max(ctrl.RightDb, ctrl._peakHoldR - 0.65);
		}
	}

	protected override void OnRender(DrawingContext dc)
	{
		double w = ActualWidth;
		double h = ActualHeight;
		if (w <= 8 || h <= 10) return;

		double barH = Math.Max(6, (h - 10) / 2.0);
		DrawChannelBar(dc, 0, w, barH, LeftDb, _peakHoldL);
		DrawChannelBar(dc, barH + 3, w, barH, RightDb, _peakHoldR);

		// Compact Limiter Gain Reduction indicator bar at bottom (right-to-left amber bar)
		double grY = barH * 2 + 6;
		double grH = Math.Max(3, h - grY);
		dc.DrawRoundedRectangle(TrackBg, null, new Rect(0, grY, w, grH), 2, 2);
		double grRatio = Math.Clamp(GainReductionDb / 18.0, 0.0, 1.0);
		if (grRatio > 0.01)
		{
			double grW = grRatio * w;
			dc.DrawRoundedRectangle(GrBrush, null, new Rect(w - grW, grY, grW, grH), 2, 2);
		}
	}

	private static void DrawChannelBar(DrawingContext dc, double y, double w, double h, double db, double holdDb)
	{
		dc.DrawRoundedRectangle(TrackBg, TrackBorder, new Rect(0.5, y + 0.5, w - 1, h - 1), 3, 3);
		double norm = DbToNorm(db);
		if (norm > 0.005)
		{
			double fillW = Math.Max(2, norm * (w - 2));
			dc.PushClip(new RectangleGeometry(new Rect(1, y + 1, fillW, h - 2), 2, 2));
			dc.DrawRectangle(MeterGradient, null, new Rect(1, y + 1, w - 2, h - 2));
			dc.Pop();
		}

		double holdNorm = DbToNorm(holdDb);
		if (holdNorm > 0.02)
		{
			double hx = 1 + holdNorm * (w - 4);
			dc.DrawRectangle(PeakHoldBrush, null, new Rect(hx, y + 1, 2, h - 2));
		}
	}

	private static double DbToNorm(double db)
	{
		// Map [-48 dBFS .. 0 dBFS] -> [0..1]
		return Math.Clamp((db + 48.0) / 48.0, 0.0, 1.0);
	}

	private static T Freeze<T>(T freezable) where T : Freezable
	{
		freezable.Freeze();
		return freezable;
	}
}
