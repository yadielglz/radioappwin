using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using RadioApp.Models;
using RadioApp.Services;

namespace RadioApp.Controls;

/// <summary>
/// Interactive 24-hour broadcast daypart rotation timeline.
/// Renders color-coded RotationBlocks for the selected DayOfWeek (including overnight wrap-around),
/// highlights uncovered default-fallback gaps, displays a live NOW playhead needle, and allows
/// clicking directly on any block to select and edit it.
/// </summary>
public sealed class RotationTimelineControl : FrameworkElement
{
	private sealed record RenderedSegment(RotationBlock Block, Rect Bounds);

	private readonly List<RenderedSegment> _segments = new List<RenderedSegment>();
	private static readonly Typeface UiTypeface = new Typeface(new FontFamily("Segoe UI Variable, Segoe UI, Arial"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);
	private static readonly Typeface MonoTypeface = new Typeface(new FontFamily("Consolas, Courier New"), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal);

	public static readonly DependencyProperty BlocksProperty = DependencyProperty.Register(
		nameof(Blocks), typeof(IEnumerable<RotationBlock>), typeof(RotationTimelineControl),
		new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender, OnBlocksChanged));

	public static readonly DependencyProperty SelectedBlockProperty = DependencyProperty.Register(
		nameof(SelectedBlock), typeof(RotationBlock), typeof(RotationTimelineControl),
		new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault | FrameworkPropertyMetadataOptions.AffectsRender));

	public static readonly DependencyProperty PreviewDayProperty = DependencyProperty.Register(
		nameof(PreviewDay), typeof(DayOfWeek), typeof(RotationTimelineControl),
		new FrameworkPropertyMetadata(DateTime.Now.DayOfWeek, FrameworkPropertyMetadataOptions.AffectsRender));

	public static readonly DependencyProperty RevisionProperty = DependencyProperty.Register(
		nameof(Revision), typeof(int), typeof(RotationTimelineControl),
		new FrameworkPropertyMetadata(0, FrameworkPropertyMetadataOptions.AffectsRender));

	public IEnumerable<RotationBlock>? Blocks
	{
		get => (IEnumerable<RotationBlock>?)GetValue(BlocksProperty);
		set => SetValue(BlocksProperty, value);
	}

	public RotationBlock? SelectedBlock
	{
		get => (RotationBlock?)GetValue(SelectedBlockProperty);
		set => SetValue(SelectedBlockProperty, value);
	}

	public DayOfWeek PreviewDay
	{
		get => (DayOfWeek)GetValue(PreviewDayProperty);
		set => SetValue(PreviewDayProperty, value);
	}

	public int Revision
	{
		get => (int)GetValue(RevisionProperty);
		set => SetValue(RevisionProperty, value);
	}

	public RotationTimelineControl()
	{
		Height = 92;
		Cursor = Cursors.Hand;
		SnapsToDevicePixels = true;
	}

	private static void OnBlocksChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
	{
		var ctrl = (RotationTimelineControl)d;
		if (e.OldValue is INotifyCollectionChanged oldCol)
		{
			oldCol.CollectionChanged -= ctrl.OnCollectionChanged;
		}
		if (e.NewValue is INotifyCollectionChanged newCol)
		{
			newCol.CollectionChanged += ctrl.OnCollectionChanged;
		}
		ctrl.InvalidateVisual();
	}

	private void OnCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
	{
		InvalidateVisual();
	}

	protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
	{
		base.OnMouseLeftButtonDown(e);
		Point pt = e.GetPosition(this);
		// Iterate in reverse so higher-priority top segments win click hit-testing
		for (int i = _segments.Count - 1; i >= 0; i--)
		{
			if (_segments[i].Bounds.Contains(pt))
			{
				SelectedBlock = _segments[i].Block;
				InvalidateVisual();
				e.Handled = true;
				return;
			}
		}
	}

	protected override void OnRender(DrawingContext dc)
	{
		base.OnRender(dc);
		_segments.Clear();

		double w = ActualWidth;
		double h = ActualHeight;
		if (w < 40 || h < 30) return;

		double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
		const double padX = 8;
		const double topAxisH = 18;
		const double bottomPad = 8;
		double trackW = Math.Max(10, w - padX * 2);
		double trackY = topAxisH;
		double trackH = Math.Max(24, h - topAxisH - bottomPad);

		// Outer card background
		var bgBrush = new SolidColorBrush(Color.FromRgb(15, 23, 42));
		var borderPen = new Pen(new SolidColorBrush(Color.FromRgb(51, 65, 85)), 1);
		dc.DrawRoundedRectangle(bgBrush, borderPen, new Rect(0, 0, w, h), 8, 8);

		// Track background (uncovered fallback zone)
		var trackRect = new Rect(padX, trackY, trackW, trackH);
		dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromRgb(21, 31, 48)), null, trackRect, 6, 6);

		// Subtle diagonal fallback label
		var fallbackText = new FormattedText(
			"Uncovered hours use Default Fallback Rotation",
			CultureInfo.InvariantCulture,
			FlowDirection.LeftToRight,
			UiTypeface,
			10.5,
			new SolidColorBrush(Color.FromRgb(100, 116, 139)),
			dpi);
		dc.DrawText(fallbackText, new Point(padX + (trackW - fallbackText.Width) / 2.0, trackY + (trackH - fallbackText.Height) / 2.0));

		// Render active rotation blocks ordered by priority (lowest drawn first, highest drawn on top)
		DayOfWeek day = PreviewDay;
		DayOfWeek prevDay = day == DayOfWeek.Sunday ? DayOfWeek.Saturday : day - 1;
		List<RotationBlock> blocks = (Blocks ?? Enumerable.Empty<RotationBlock>())
			.Where(b => b.Enabled)
			.OrderBy(b => b.Priority)
			.ToList();

		foreach (RotationBlock block in blocks)
		{
			if (!RotationService.TryParseRuleTime(block.StartTime, out TimeSpan start) ||
				!RotationService.TryParseRuleTime(block.EndTime, out TimeSpan end))
			{
				continue;
			}

			var ranges = new List<(double StartMin, double EndMin)>(2);
			if (end > start)
			{
				if (block.DaysMask.HasFlag(RotationBlock.FromDayOfWeek(day)))
				{
					ranges.Add((start.TotalMinutes, end.TotalMinutes));
				}
			}
			else if (end == start)
			{
				if (block.DaysMask.HasFlag(RotationBlock.FromDayOfWeek(day)))
				{
					ranges.Add((0, 1440));
				}
			}
			else
			{
				// Overnight wrap-around block
				if (block.DaysMask.HasFlag(RotationBlock.FromDayOfWeek(prevDay)) && end.TotalMinutes > 0)
				{
					ranges.Add((0, end.TotalMinutes));
				}
				if (block.DaysMask.HasFlag(RotationBlock.FromDayOfWeek(day)) && start.TotalMinutes < 1440)
				{
					ranges.Add((start.TotalMinutes, 1440));
				}
			}

			Color baseColor = ParseColor(block.ColorHex, Color.FromRgb(2, 132, 199));
			bool isSelected = ReferenceEquals(block, SelectedBlock);

			foreach (var (sMin, eMin) in ranges)
			{
				double x1 = padX + (sMin / 1440.0) * trackW;
				double x2 = padX + (eMin / 1440.0) * trackW;
				double segW = Math.Max(4, x2 - x1);
				var segRect = new Rect(x1 + 0.5, trackY + 2, Math.Max(3, segW - 1), trackH - 4);
				_segments.Add(new RenderedSegment(block, segRect));

				var fill = new SolidColorBrush(Color.FromArgb(isSelected ? (byte)235 : (byte)185, baseColor.R, baseColor.G, baseColor.B));
				var segPen = isSelected
					? new Pen(Brushes.White, 2.0)
					: new Pen(new SolidColorBrush(Color.FromArgb(220, baseColor.R, baseColor.G, baseColor.B)), 1.0);
				dc.DrawRoundedRectangle(fill, segPen, segRect, 5, 5);

				// Draw title + source inside segment if wide enough
				if (segW >= 42)
				{
					dc.PushClip(new RectangleGeometry(new Rect(segRect.X + 4, segRect.Y + 2, Math.Max(1, segRect.Width - 8), Math.Max(1, segRect.Height - 4))));
					var titleFt = new FormattedText(
						string.IsNullOrWhiteSpace(block.Name) ? block.SourceSummary : block.Name,
						CultureInfo.InvariantCulture,
						FlowDirection.LeftToRight,
						UiTypeface,
						11.5,
						Brushes.White,
						dpi);
					dc.DrawText(titleFt, new Point(segRect.X + 6, segRect.Y + 6));

					if (segW >= 78 && trackH >= 48)
					{
						var subFt = new FormattedText(
							block.SourceSummary,
							CultureInfo.InvariantCulture,
							FlowDirection.LeftToRight,
							UiTypeface,
							10.0,
							new SolidColorBrush(Color.FromArgb(220, 241, 245, 249)),
							dpi);
						dc.DrawText(subFt, new Point(segRect.X + 6, segRect.Y + 23));
					}
					dc.Pop();
				}
			}
		}

		// Hour grid lines & labels
		var gridPen = new Pen(new SolidColorBrush(Color.FromArgb(55, 255, 255, 255)), 1);
		var labelBrush = new SolidColorBrush(Color.FromRgb(148, 163, 184));
		for (int hr = 0; hr <= 24; hr += 3)
		{
			double x = padX + (hr / 24.0) * trackW;
			dc.DrawLine(gridPen, new Point(x, trackY), new Point(x, trackY + trackH));
			var hrText = new FormattedText(
				$"{hr:00}",
				CultureInfo.InvariantCulture,
				FlowDirection.LeftToRight,
				MonoTypeface,
				10,
				labelBrush,
				dpi);
			double lx = hr == 24 ? x - hrText.Width : (hr == 0 ? x : x - hrText.Width / 2.0);
			dc.DrawText(hrText, new Point(lx, 3));
		}

		// Live NOW needle when previewing today
		DateTime now = DateTime.Now;
		if (day == now.DayOfWeek)
		{
			double nowMin = now.TimeOfDay.TotalMinutes;
			double nx = padX + (nowMin / 1440.0) * trackW;
			var nowBrush = new SolidColorBrush(Color.FromRgb(52, 211, 153));
			dc.DrawLine(new Pen(nowBrush, 2.2), new Point(nx, trackY - 3), new Point(nx, trackY + trackH + 3));
			dc.DrawEllipse(nowBrush, null, new Point(nx, trackY - 3), 3.5, 3.5);
		}
	}

	private static Color ParseColor(string? hex, Color fallback)
	{
		if (string.IsNullOrWhiteSpace(hex)) return fallback;
		try
		{
			object? parsed = ColorConverter.ConvertFromString(hex.Trim());
			return parsed is Color c ? c : fallback;
		}
		catch
		{
			return fallback;
		}
	}
}
