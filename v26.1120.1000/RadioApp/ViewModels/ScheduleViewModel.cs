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

public sealed class ScheduleViewModel : ViewModelBase, IDisposable
{
	private readonly RadioDbContextFactory _dbFactory;
	private readonly Func<IReadOnlyList<Playset>> _playsetsProvider;
	private readonly DispatcherTimer _timer;
	private ScheduleItem? _selectedItem;

	public ObservableCollection<ScheduleItem> Items { get; } = new ObservableCollection<ScheduleItem>();

	public ICommand AddScheduleCommand { get; }
	public ICommand EditScheduleCommand { get; }
	public ICommand DeleteScheduleCommand { get; }
	public ICommand ToggleEnabledCommand { get; }
	public ICommand RefreshCommand { get; }

	public ScheduleItem? SelectedItem
	{
		get => _selectedItem;
		set
		{
			if (SetProperty(ref _selectedItem, value))
			{
				RelayCommand.InvalidateAll();
			}
		}
	}

	public event Action<ScheduleItem>? ScheduleDue;

	public ScheduleViewModel(RadioDbContextFactory dbFactory, Func<IReadOnlyList<Playset>> playsetsProvider)
	{
		_dbFactory = dbFactory;
		_playsetsProvider = playsetsProvider;

		AddScheduleCommand = new RelayCommand(AddSchedule);
		EditScheduleCommand = new RelayCommand(EditSchedule, () => SelectedItem != null);
		DeleteScheduleCommand = new RelayCommand(DeleteSchedule, () => SelectedItem != null);
		ToggleEnabledCommand = new RelayCommand(ToggleSelectedEnabled, () => SelectedItem != null);
		RefreshCommand = new RelayCommand(Refresh);

		Refresh();

		_timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(15.0) };
		_timer.Tick += OnTimerTick;
		_timer.Start();
	}

	public void Refresh()
	{
		int? selectedId = SelectedItem?.Id;
		using RadioLibraryContext ctx = _dbFactory.CreateContext();
		List<ScheduleItem> fresh = ctx.ScheduleItems
			.OrderByDescending(s => s.IsMain)
			.ThenBy(s => s.ScheduledAt)
			.ToList();

		Items.Clear();
		foreach (ScheduleItem s in fresh)
		{
			Items.Add(s);
		}
		if (selectedId.HasValue)
		{
			SelectedItem = Items.FirstOrDefault(i => i.Id == selectedId.Value);
		}
	}

	private void OnTimerTick(object? sender, EventArgs e)
	{
		DateTime now = DateTime.Now;
		List<ScheduleItem> dueEvents = new List<ScheduleItem>();
		bool changed = false;

		using (RadioLibraryContext ctx = _dbFactory.CreateContext())
		{
			List<ScheduleItem> active = ctx.ScheduleItems.Where(s => s.Enabled).ToList();
			foreach (ScheduleItem item in active)
			{
				DateTime windowStart = item.ScheduledAt.AddMinutes(-Math.Max(0, item.ToleranceBeforeMinutes));
				DateTime windowEnd = item.ScheduledAt.AddMinutes(Math.Max(0, item.ToleranceAfterMinutes));
				if (now >= windowStart && now <= windowEnd)
				{
					dueEvents.Add(CloneItem(item));
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
				ctx.SaveChanges();
			}
		}

		foreach (ScheduleItem due in dueEvents)
		{
			ScheduleDue?.Invoke(due);
		}

		// Only refresh UI grid when state actually changed (preserves user selection & scroll position!)
		if (changed)
		{
			Refresh();
		}
	}

	private void AddSchedule()
	{
		var item = new ScheduleItem
		{
			ScheduledAt = DateTime.Now.AddMinutes(5.0),
			EventType = ScheduleEventType.Playset,
			TargetDeck = DeckId.A,
			Enabled = true,
			RepeatMode = ScheduleRepeatMode.None,
			ToleranceAfterMinutes = 2,
			BlockDurationMinutes = 60
		};

		var editor = new ScheduleEditorWindow(item, _playsetsProvider())
		{
			Owner = Application.Current?.MainWindow
		};
		if (editor.ShowDialog() == true)
		{
			using RadioLibraryContext ctx = _dbFactory.CreateContext();
			ctx.ScheduleItems.Add(item);
			ctx.SaveChanges();
			Refresh();
			SelectedItem = Items.FirstOrDefault(i => i.Id == item.Id);
		}
	}

	private void EditSchedule()
	{
		if (SelectedItem == null) return;
		ScheduleItem copy = CloneItem(SelectedItem);
		var editor = new ScheduleEditorWindow(copy, _playsetsProvider())
		{
			Owner = Application.Current?.MainWindow
		};
		if (editor.ShowDialog() == true)
		{
			using RadioLibraryContext ctx = _dbFactory.CreateContext();
			ScheduleItem? dbItem = ctx.ScheduleItems.Find(copy.Id);
			if (dbItem != null)
			{
				dbItem.Title = copy.Title;
				dbItem.EventType = copy.EventType;
				dbItem.FilePath = copy.FilePath;
				dbItem.ScheduledAt = copy.ScheduledAt;
				dbItem.RepeatInterval = copy.RepeatInterval;
				dbItem.RepeatMode = copy.RepeatMode;
				dbItem.ToleranceBeforeMinutes = copy.ToleranceBeforeMinutes;
				dbItem.ToleranceAfterMinutes = copy.ToleranceAfterMinutes;
				dbItem.IsMain = copy.IsMain;
				dbItem.Enabled = copy.Enabled;
				dbItem.TargetDeck = copy.TargetDeck;
				dbItem.IsFolderSource = copy.IsFolderSource;
				dbItem.BlockDurationMinutes = copy.BlockDurationMinutes;
				dbItem.PlaysetId = copy.PlaysetId;
				ctx.SaveChanges();
			}
			Refresh();
		}
	}

	private void ToggleSelectedEnabled()
	{
		if (SelectedItem == null) return;
		using RadioLibraryContext ctx = _dbFactory.CreateContext();
		ScheduleItem? dbItem = ctx.ScheduleItems.Find(SelectedItem.Id);
		if (dbItem != null)
		{
			dbItem.Enabled = !dbItem.Enabled;
			ctx.SaveChanges();
		}
		Refresh();
	}

	private void DeleteSchedule()
	{
		if (SelectedItem == null) return;
		using RadioLibraryContext ctx = _dbFactory.CreateContext();
		ScheduleItem? dbItem = ctx.ScheduleItems.Find(SelectedItem.Id);
		if (dbItem != null)
		{
			ctx.ScheduleItems.Remove(dbItem);
			ctx.SaveChanges();
		}
		Refresh();
	}

	private static void MoveToNextOccurrenceOrDisable(ScheduleItem item, DateTime now)
	{
		TimeSpan? step = item.RepeatMode switch
		{
			ScheduleRepeatMode.Interval => item.RepeatInterval,
			ScheduleRepeatMode.Daily => TimeSpan.FromDays(1.0),
			ScheduleRepeatMode.Weekly => TimeSpan.FromDays(7.0),
			_ => null
		};
		if (!step.HasValue || step.Value <= TimeSpan.Zero)
		{
			item.Enabled = false;
			return;
		}
		DateTime next = item.ScheduledAt.Add(step.Value);
		while (next.AddMinutes(Math.Max(0, item.ToleranceAfterMinutes)) < now)
		{
			next = next.Add(step.Value);
		}
		item.ScheduledAt = next;
	}

	private static ScheduleItem CloneItem(ScheduleItem s) => new ScheduleItem
	{
		Id = s.Id,
		Title = s.Title,
		EventType = s.EventType,
		FilePath = s.FilePath,
		ScheduledAt = s.ScheduledAt,
		RepeatInterval = s.RepeatInterval,
		RepeatMode = s.RepeatMode,
		ToleranceBeforeMinutes = s.ToleranceBeforeMinutes,
		ToleranceAfterMinutes = s.ToleranceAfterMinutes,
		IsMain = s.IsMain,
		Enabled = s.Enabled,
		TargetDeck = s.TargetDeck,
		IsFolderSource = s.IsFolderSource,
		BlockDurationMinutes = s.BlockDurationMinutes,
		PlaysetId = s.PlaysetId
	};

	public void Dispose()
	{
		_timer.Stop();
		_timer.Tick -= OnTimerTick;
	}
}
