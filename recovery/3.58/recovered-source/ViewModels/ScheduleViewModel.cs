using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using RadioApp.Data;
using RadioApp.Helpers;
using RadioApp.Models;
using RadioApp.Views;

namespace RadioApp.ViewModels;

public class ScheduleViewModel : ViewModelBase, IDisposable
{
	private readonly RadioLibraryContext _context;

	private readonly DispatcherTimer _timer;

	private ScheduleItem? _selectedItem;

	public ObservableCollection<ScheduleItem> Items { get; }

	public ICommand AddScheduleCommand { get; }

	public ICommand EditScheduleCommand { get; }

	public ICommand DeleteScheduleCommand { get; }

	public ICommand RefreshCommand { get; }

	public ScheduleItem? SelectedItem
	{
		get
		{
			return _selectedItem;
		}
		set
		{
			if (SetProperty(ref _selectedItem, value, "SelectedItem"))
			{
				CommandManager.InvalidateRequerySuggested();
			}
		}
	}

	public event Action<ScheduleItem>? ScheduleDue;

	public ScheduleViewModel()
	{
		_context = new RadioLibraryContext();
		_context.EnsureDatabase();
		Items = new ObservableCollection<ScheduleItem>(_context.ScheduleItems.ToList());
		AddScheduleCommand = new RelayCommand(delegate
		{
			AddSchedule();
		});
		EditScheduleCommand = new RelayCommand(delegate
		{
			EditSchedule();
		}, (object? _) => SelectedItem != null);
		DeleteScheduleCommand = new RelayCommand(delegate
		{
			DeleteSchedule();
		}, (object? _) => SelectedItem != null);
		RefreshCommand = new RelayCommand(delegate
		{
			Refresh();
		});
		_timer = new DispatcherTimer
		{
			Interval = TimeSpan.FromSeconds(20.0)
		};
		_timer.Tick += OnTimerTick;
		_timer.Start();
	}

	public void Refresh()
	{
		Items.Clear();
		foreach (ScheduleItem item in from s in _context.ScheduleItems
			orderby s.IsMain descending, s.ScheduledAt
			select s)
		{
			Items.Add(item);
		}
	}

	private void OnTimerTick(object? sender, EventArgs e)
	{
		DateTime now = DateTime.Now;
		List<ScheduleItem> list = _context.ScheduleItems.Where((ScheduleItem scheduleItem) => scheduleItem.Enabled).ToList();
		bool changed = false;
		foreach (ScheduleItem item in list)
		{
			DateTime windowStart = item.ScheduledAt.AddMinutes(-Math.Max(0, item.ToleranceBeforeMinutes));
			DateTime windowEnd = item.ScheduledAt.AddMinutes(Math.Max(0, item.ToleranceAfterMinutes));
			if (now >= windowStart && now <= windowEnd)
			{
				this.ScheduleDue?.Invoke(item);
				MoveToNextOccurrenceOrDisable(item, now);
				changed = true;
			}
			else if (now > windowEnd)
			{
				MoveToNextOccurrenceOrDisable(item, now);
				changed = true;
			}
		}
		if (changed)
		{
			_context.SaveChanges();
		}
		Refresh();
	}

	private static void MoveToNextOccurrenceOrDisable(ScheduleItem item, DateTime now)
	{
		DateTime? next = GetNextOccurrence(item, now);
		if (next.HasValue)
		{
			item.ScheduledAt = next.Value;
		}
		else
		{
			item.Enabled = false;
		}
	}

	private static DateTime? GetNextOccurrence(ScheduleItem item, DateTime now)
	{
		TimeSpan? step = item.RepeatMode switch
		{
			ScheduleRepeatMode.Interval => item.RepeatInterval, 
			ScheduleRepeatMode.Daily => TimeSpan.FromDays(1.0), 
			ScheduleRepeatMode.Weekly => TimeSpan.FromDays(7.0), 
			_ => null, 
		};
		if (!step.HasValue || step.Value <= TimeSpan.Zero)
		{
			return null;
		}
		DateTime next = item.ScheduledAt.Add(step.Value);
		while (next.AddMinutes(Math.Max(0, item.ToleranceAfterMinutes)) < now)
		{
			next = next.Add(step.Value);
		}
		return next;
	}

	private void AddSchedule()
	{
		ScheduleItem scheduleItem = new ScheduleItem
		{
			ScheduledAt = DateTime.Now.AddMinutes(5.0),
			EventType = ScheduleEventType.Sweep,
			TargetDeck = DeckId.A,
			Enabled = true,
			RepeatMode = ScheduleRepeatMode.None,
			ToleranceAfterMinutes = 2
		};
		if (new ScheduleEditorWindow(scheduleItem)
		{
			Owner = Application.Current?.MainWindow
		}.ShowDialog() == true)
		{
			_context.ScheduleItems.Add(scheduleItem);
			_context.SaveChanges();
			Items.Add(scheduleItem);
		}
	}

	private void EditSchedule()
	{
		if (SelectedItem != null && new ScheduleEditorWindow(SelectedItem)
		{
			Owner = Application.Current?.MainWindow
		}.ShowDialog() == true)
		{
			_context.SaveChanges();
			Refresh();
		}
	}

	private void DeleteSchedule()
	{
		if (SelectedItem != null)
		{
			_context.ScheduleItems.Remove(SelectedItem);
			_context.SaveChanges();
			Items.Remove(SelectedItem);
			SelectedItem = null;
		}
	}

	public void Dispose()
	{
		_timer.Stop();
		_timer.Tick -= OnTimerTick;
		_context.Dispose();
	}
}
