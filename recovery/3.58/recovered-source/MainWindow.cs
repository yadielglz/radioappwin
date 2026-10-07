using System;
using System.ComponentModel;
using System.Drawing;
using System.Reflection;
using System.Windows;
using System.Windows.Forms;
using System.Windows.Markup;
using System.Windows.Resources;
using RadioApp.ViewModels;
using RadioApp.Views;

namespace RadioApp;

public partial class MainWindow : Window, IComponentConnector
{
	private LibraryWindow? _libraryWindow;

	private ScheduleWindow? _scheduleWindow;

	private readonly NotifyIcon _notifyIcon;

	private bool _allowClose;

	private bool _resourcesDisposed;

	public MainWindow()
	{
		InitializeComponent();
		base.DataContext = new MainViewModel();
		_notifyIcon = CreateNotifyIcon();
		base.Loaded += MainWindow_Loaded;
		base.StateChanged += MainWindow_StateChanged;
		base.Closing += MainWindow_Closing;
	}

	private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
	{
		object dataContext = base.DataContext;
		if (dataContext is MainViewModel main)
		{
			await main.InitializeAsync();
			if (main.StartMinimizedToTray)
			{
				base.WindowState = WindowState.Minimized;
				HideToTray("RadioApp is running in the background.");
			}
		}
	}

	private void Settings_Click(object sender, RoutedEventArgs e)
	{
		SettingsWindow settingsWindow = new SettingsWindow();
		settingsWindow.Owner = this;
		settingsWindow.DataContext = base.DataContext;
		settingsWindow.ShowDialog();
	}

	private void LibraryWindow_Click(object sender, RoutedEventArgs e)
	{
		if (_libraryWindow != null)
		{
			_libraryWindow.Activate();
			return;
		}
		_libraryWindow = new LibraryWindow
		{
			Owner = this,
			DataContext = base.DataContext
		};
		_libraryWindow.Closed += delegate
		{
			_libraryWindow = null;
		};
		_libraryWindow.Show();
	}

	private void ScheduleWindow_Click(object sender, RoutedEventArgs e)
	{
		if (_scheduleWindow != null)
		{
			_scheduleWindow.Activate();
			return;
		}
		_scheduleWindow = new ScheduleWindow
		{
			Owner = this,
			DataContext = base.DataContext
		};
		_scheduleWindow.Closed += delegate
		{
			_scheduleWindow = null;
		};
		_scheduleWindow.Show();
	}

	private void About_Click(object sender, RoutedEventArgs e)
	{
		string version = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "26.1007.0900";
		System.Windows.MessageBox.Show(this, "RadioApp Studio\nVersion " + version + "\n\nBroadcast automation, playout console, scheduling, and live streaming engine.\nCopyright 2026 Glz Technical Services | Glz Tech", "About RadioApp", MessageBoxButton.OK, MessageBoxImage.Asterisk);
	}

	private void Exit_Click(object sender, RoutedEventArgs e)
	{
		_allowClose = true;
		Close();
	}

	private void MainWindow_StateChanged(object? sender, EventArgs e)
	{
		if (base.WindowState == WindowState.Minimized && base.DataContext is MainViewModel { MinimizeToTray: not false })
		{
			HideToTray("RadioApp was minimized to the tray.");
		}
	}

	private void MainWindow_Closing(object? sender, CancelEventArgs e)
	{
		if (_allowClose)
		{
			_notifyIcon.Visible = false;
			_notifyIcon.Dispose();
		}
		else if (base.DataContext is MainViewModel { CloseToTray: not false })
		{
			e.Cancel = true;
			HideToTray("RadioApp is still running in the tray.");
		}
		else
		{
			_notifyIcon.Visible = false;
			_notifyIcon.Dispose();
		}
		if (!e.Cancel && !_resourcesDisposed)
		{
			_resourcesDisposed = true;
			(base.DataContext as IDisposable)?.Dispose();
		}
	}

	private void HideToTray(string message)
	{
		base.ShowInTaskbar = false;
		Hide();
		_notifyIcon.Visible = true;
		_notifyIcon.BalloonTipTitle = "RadioApp";
		_notifyIcon.BalloonTipText = message;
		_notifyIcon.ShowBalloonTip(2500);
	}

	private void RestoreFromTray()
	{
		Show();
		base.ShowInTaskbar = true;
		base.WindowState = WindowState.Normal;
		Activate();
	}

	private NotifyIcon CreateNotifyIcon()
	{
		NotifyIcon notifyIcon = new NotifyIcon
		{
			Text = "RadioApp",
			Icon = LoadTrayIcon(),
			Visible = false
		};
		notifyIcon.DoubleClick += delegate
		{
			RestoreFromTray();
		};
		ContextMenuStrip menu = new ContextMenuStrip();
		menu.Items.Add("Open", null, delegate
		{
			RestoreFromTray();
		});
		menu.Items.Add("Exit", null, delegate
		{
			_allowClose = true;
			notifyIcon.Visible = false;
			Close();
		});
		notifyIcon.ContextMenuStrip = menu;
		return notifyIcon;
	}

	private static Icon LoadTrayIcon()
	{
		StreamResourceInfo resource = System.Windows.Application.GetResourceStream(new Uri("Assets/RadioAppIcon.ico", UriKind.Relative));
		if (resource != null)
		{
			return new Icon(resource.Stream);
		}
		return SystemIcons.Application;
	}
}
