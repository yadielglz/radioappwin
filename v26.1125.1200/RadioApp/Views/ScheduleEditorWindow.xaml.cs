using System;
using System.Collections.Generic;
using System.IO;
using System.Windows;
using Microsoft.Win32;
using RadioApp.Models;
using RadioApp.Services;

namespace RadioApp.Views;

public partial class ScheduleEditorWindow : Window
{
	public ScheduleItem ScheduleItem { get; }
	public DateTime SelectedDate { get; set; }
	public string ScheduleTime { get; set; }
	public string RepeatMinutes { get; set; }
	public string ToleranceBeforeMinutes { get; set; }
	public string ToleranceAfterMinutes { get; set; }
	public string BlockDurationMinutes { get; set; }

	public ScheduleEditorWindow(ScheduleItem scheduleItem, IReadOnlyList<Playset> playsets)
	{
		InitializeComponent();
		ScheduleItem = scheduleItem;
		SelectedDate = scheduleItem.ScheduledAt.Date;
		ScheduleTime = scheduleItem.ScheduledAt.ToString("HH:mm");
		RepeatMinutes = scheduleItem.RepeatInterval?.TotalMinutes.ToString("0") ?? string.Empty;
		ToleranceBeforeMinutes = scheduleItem.ToleranceBeforeMinutes.ToString();
		ToleranceAfterMinutes = scheduleItem.ToleranceAfterMinutes.ToString();
		BlockDurationMinutes = Math.Max(1, scheduleItem.BlockDurationMinutes).ToString();

		DataContext = this;
		EventTypeComboBox.ItemsSource = Enum.GetValues(typeof(ScheduleEventType));
		RepeatModeComboBox.ItemsSource = Enum.GetValues(typeof(ScheduleRepeatMode));

		var playsetOptions = new List<Playset> { new Playset { Id = 0, Name = "(None — use file or folder)" } };
		playsetOptions.AddRange(playsets);
		PlaysetComboBox.ItemsSource = playsetOptions;
		PlaysetComboBox.SelectedValue = scheduleItem.PlaysetId ?? 0;
	}

	private void BrowseFile_Click(object sender, RoutedEventArgs e)
	{
		var dialog = new OpenFileDialog { Filter = AudioFileProbeCore.OpenFileDialogFilter };
		if (dialog.ShowDialog() == true)
		{
			ScheduleItem.FilePath = dialog.FileName;
			ScheduleItem.IsFolderSource = false;
			SourcePathBox.Text = dialog.FileName;
		}
	}

	private void BrowseFolder_Click(object sender, RoutedEventArgs e)
	{
		var dialog = new OpenFolderDialog { Title = "Select scheduled playlist folder" };
		if (dialog.ShowDialog() == true)
		{
			ScheduleItem.FilePath = dialog.FolderName;
			ScheduleItem.IsFolderSource = true;
			ScheduleItem.EventType = ScheduleEventType.Playlist;
			SourcePathBox.Text = dialog.FolderName;
		}
	}

	private void Save_Click(object sender, RoutedEventArgs e)
	{
		if (!TimeSpan.TryParse(ScheduleTime, out TimeSpan ts))
		{
			MessageBox.Show(this, "Enter a valid time in HH:mm format.", "Invalid Time", MessageBoxButton.OK, MessageBoxImage.Warning);
			return;
		}
		if (!int.TryParse(ToleranceBeforeMinutes, out int tolBefore) || tolBefore < 0 ||
			!int.TryParse(ToleranceAfterMinutes, out int tolAfter) || tolAfter < 0)
		{
			MessageBox.Show(this, "Enter valid non-negative early/late tolerances.", "Invalid Tolerance", MessageBoxButton.OK, MessageBoxImage.Warning);
			return;
		}
		if (!int.TryParse(BlockDurationMinutes, out int blockMins) || blockMins <= 0)
		{
			MessageBox.Show(this, "Enter a valid positive block duration in minutes.", "Invalid Duration", MessageBoxButton.OK, MessageBoxImage.Warning);
			return;
		}

		ScheduleItem.ScheduledAt = SelectedDate.Date + ts;
		ScheduleItem.ToleranceBeforeMinutes = tolBefore;
		ScheduleItem.ToleranceAfterMinutes = tolAfter;
		ScheduleItem.BlockDurationMinutes = blockMins;

		if (PlaysetComboBox.SelectedValue is int psId && psId > 0)
		{
			ScheduleItem.PlaysetId = psId;
			ScheduleItem.EventType = ScheduleEventType.Playset;
		}
		else
		{
			ScheduleItem.PlaysetId = null;
		}

		if (ScheduleItem.EventType != ScheduleEventType.Playset && ScheduleItem.IsFolderSource && !Directory.Exists(ScheduleItem.FilePath))
		{
			MessageBox.Show(this, "Select a valid folder for the scheduled playlist block.", "Invalid Folder", MessageBoxButton.OK, MessageBoxImage.Warning);
			return;
		}

		if (ScheduleItem.RepeatMode == ScheduleRepeatMode.Interval)
		{
			if (!int.TryParse(RepeatMinutes, out int mins) || mins <= 0)
			{
				MessageBox.Show(this, "Enter repeat interval minutes > 0.", "Invalid Interval", MessageBoxButton.OK, MessageBoxImage.Warning);
				return;
			}
			ScheduleItem.RepeatInterval = TimeSpan.FromMinutes(mins);
		}
		else
		{
			ScheduleItem.RepeatInterval = null;
		}

		DialogResult = true;
		Close();
	}

	private void Cancel_Click(object sender, RoutedEventArgs e)
	{
		DialogResult = false;
		Close();
	}
}
