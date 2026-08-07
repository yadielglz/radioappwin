using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Markup;
using Microsoft.Win32;
using RadioApp.Models;

namespace RadioApp.Views;

public partial class ScheduleEditorWindow : Window, INotifyPropertyChanged, IComponentConnector
{
	private DateTime _selectedDate;

	private string _scheduleTime = string.Empty;

	private string _repeatMinutes = string.Empty;

	private string _toleranceBeforeMinutes = "0";

	private string _toleranceAfterMinutes = "2";

	private string _blockDurationMinutes = "60";

	public ScheduleItem ScheduleItem { get; }

	public DateTime SelectedDate
	{
		get
		{
			return _selectedDate;
		}
		set
		{
			SetProperty(ref _selectedDate, value, "SelectedDate");
		}
	}

	public string ScheduleTime
	{
		get
		{
			return _scheduleTime;
		}
		set
		{
			SetProperty(ref _scheduleTime, value, "ScheduleTime");
		}
	}

	public string RepeatMinutes
	{
		get
		{
			return _repeatMinutes;
		}
		set
		{
			SetProperty(ref _repeatMinutes, value, "RepeatMinutes");
		}
	}

	public string ToleranceBeforeMinutes
	{
		get
		{
			return _toleranceBeforeMinutes;
		}
		set
		{
			SetProperty(ref _toleranceBeforeMinutes, value, "ToleranceBeforeMinutes");
		}
	}

	public string ToleranceAfterMinutes
	{
		get
		{
			return _toleranceAfterMinutes;
		}
		set
		{
			SetProperty(ref _toleranceAfterMinutes, value, "ToleranceAfterMinutes");
		}
	}

	public string BlockDurationMinutes
	{
		get
		{
			return _blockDurationMinutes;
		}
		set
		{
			SetProperty(ref _blockDurationMinutes, value, "BlockDurationMinutes");
		}
	}

	public event PropertyChangedEventHandler? PropertyChanged;

	public ScheduleEditorWindow(ScheduleItem scheduleItem)
	{
		InitializeComponent();
		ScheduleItem = scheduleItem;
		SelectedDate = scheduleItem.ScheduledAt.Date;
		ScheduleTime = scheduleItem.ScheduledAt.ToString("HH:mm");
		RepeatMinutes = scheduleItem.RepeatInterval?.TotalMinutes.ToString() ?? string.Empty;
		ToleranceBeforeMinutes = scheduleItem.ToleranceBeforeMinutes.ToString();
		ToleranceAfterMinutes = scheduleItem.ToleranceAfterMinutes.ToString();
		BlockDurationMinutes = Math.Max(1, scheduleItem.BlockDurationMinutes).ToString();
		base.DataContext = this;
		EventTypeComboBox.ItemsSource = Enum.GetValues(typeof(ScheduleEventType));
		TargetDeckComboBox.ItemsSource = Enum.GetValues(typeof(DeckId));
		RepeatModeComboBox.ItemsSource = Enum.GetValues(typeof(ScheduleRepeatMode));
	}

	private void SetProperty<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
	{
		if (!EqualityComparer<T>.Default.Equals(field, value))
		{
			field = value;
			this.PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
		}
	}

	private void BrowseFile_Click(object sender, RoutedEventArgs e)
	{
		OpenFileDialog dialog = new OpenFileDialog
		{
			Filter = "Audio and video files|*.mp3;*.mp4;*.flac;*.wav;*.wma|All files|*.*"
		};
		if (dialog.ShowDialog() == true)
		{
			ScheduleItem.FilePath = dialog.FileName;
			base.DataContext = null;
			base.DataContext = this;
		}
	}

	private void BrowseFolder_Click(object sender, RoutedEventArgs e)
	{
		OpenFolderDialog dialog = new OpenFolderDialog
		{
			Title = "Select scheduled playlist folder",
			Multiselect = false
		};
		if (dialog.ShowDialog() == true)
		{
			ScheduleItem.FilePath = dialog.FolderName;
			ScheduleItem.IsFolderSource = true;
			ScheduleItem.EventType = ScheduleEventType.Playlist;
			base.DataContext = null;
			base.DataContext = this;
		}
	}

	private void Save_Click(object sender, RoutedEventArgs e)
	{
		if (!TimeSpan.TryParse(ScheduleTime, out var timeSpan))
		{
			MessageBox.Show(this, "Enter a valid time in HH:mm format.", "Invalid Time", MessageBoxButton.OK, MessageBoxImage.Exclamation);
			return;
		}
		ScheduleItem.ScheduledAt = SelectedDate.Date + timeSpan;
		if (!int.TryParse(ToleranceBeforeMinutes, out var toleranceBefore) || toleranceBefore < 0)
		{
			MessageBox.Show(this, "Enter a valid early tolerance in minutes.", "Invalid Tolerance", MessageBoxButton.OK, MessageBoxImage.Exclamation);
			return;
		}
		if (!int.TryParse(ToleranceAfterMinutes, out var toleranceAfter) || toleranceAfter < 0)
		{
			MessageBox.Show(this, "Enter a valid late tolerance in minutes.", "Invalid Tolerance", MessageBoxButton.OK, MessageBoxImage.Exclamation);
			return;
		}
		ScheduleItem.ToleranceBeforeMinutes = toleranceBefore;
		ScheduleItem.ToleranceAfterMinutes = toleranceAfter;
		if (!int.TryParse(BlockDurationMinutes, out var blockMinutes) || blockMinutes <= 0)
		{
			MessageBox.Show(this, "Enter a valid folder block duration in minutes.", "Invalid Block Duration", MessageBoxButton.OK, MessageBoxImage.Exclamation);
			return;
		}
		ScheduleItem.BlockDurationMinutes = blockMinutes;
		if (ScheduleItem.IsFolderSource && !Directory.Exists(ScheduleItem.FilePath))
		{
			MessageBox.Show(this, "Select a valid folder for the scheduled playlist block.", "Invalid Folder", MessageBoxButton.OK, MessageBoxImage.Exclamation);
			return;
		}
		if (ScheduleItem.RepeatMode == ScheduleRepeatMode.Interval && int.TryParse(RepeatMinutes, out var minutes) && minutes > 0)
		{
			ScheduleItem.RepeatInterval = TimeSpan.FromMinutes(minutes);
		}
		else
		{
			ScheduleItem.RepeatInterval = null;
			if (ScheduleItem.RepeatMode == ScheduleRepeatMode.Interval)
			{
				MessageBox.Show(this, "Enter repeat interval minutes, or choose a daily/weekly repeat.", "Invalid Repeat", MessageBoxButton.OK, MessageBoxImage.Exclamation);
				return;
			}
		}
		base.DialogResult = true;
		Close();
	}

	private void Cancel_Click(object sender, RoutedEventArgs e)
	{
		base.DialogResult = false;
		Close();
	}
}
