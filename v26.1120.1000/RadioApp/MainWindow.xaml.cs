using System;
using System.ComponentModel;
using System.Drawing;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Forms;
using System.Windows.Input;
using System.Windows.Resources;
using RadioApp.Models;
using RadioApp.ViewModels;
using RadioApp.Views;
using Application = System.Windows.Application;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using MessageBox = System.Windows.MessageBox;
using TextBox = System.Windows.Controls.TextBox;
using ComboBox = System.Windows.Controls.ComboBox;

namespace RadioApp;

public partial class MainWindow : Window
{
	private readonly NotifyIcon _notifyIcon;
	private Window? _poppedOutLibrary;
	private Window? _poppedOutSchedule;
	private bool _allowClose;
	private bool _resourcesDisposed;

	public MainWindow()
	{
		InitializeComponent();
		DataContext = new MainViewModel();
		_notifyIcon = CreateNotifyIcon();
		Loaded += MainWindow_Loaded;
		StateChanged += MainWindow_StateChanged;
		Closing += MainWindow_Closing;
	}

	private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
	{
		if (DataContext is MainViewModel main)
		{
			await main.InitializeAsync();
			if (main.StartMinimizedToTray)
			{
				WindowState = WindowState.Minimized;
				HideToTray("RadioApp Studio is running in the background.");
			}
		}
	}

	private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
	{
		if (DataContext is not MainViewModel main) return;

		// Do not hijack typing inside text inputs
		bool isTyping = Keyboard.FocusedElement is TextBox || Keyboard.FocusedElement is PasswordBox || Keyboard.FocusedElement is ComboBox;

		// F1-F8 trigger Cartwall Pads from anywhere!
		if (e.Key >= Key.F1 && e.Key <= Key.F8)
		{
			int slot = (int)(e.Key - Key.F1);
			main.TriggerCartPadBySlot(slot);
			e.Handled = true;
			return;
		}

		if (Keyboard.Modifiers == ModifierKeys.Control)
		{
			switch (e.Key)
			{
				case Key.D1: main.SelectedWorkspaceIndex = 0; e.Handled = true; return;
				case Key.D2: main.SelectedWorkspaceIndex = 1; e.Handled = true; return;
				case Key.D3: main.SelectedWorkspaceIndex = 2; e.Handled = true; return;
				case Key.D4: main.SelectedWorkspaceIndex = 3; e.Handled = true; return;
				case Key.Right:
					if (main.PlayNextCommand.CanExecute(null)) main.PlayNextCommand.Execute(null);
					e.Handled = true;
					return;
				case Key.M:
					main.ToggleMonitorMuteCommand.Execute(null);
					e.Handled = true;
					return;
			}
		}

		if (!isTyping && e.Key == Key.Space && Keyboard.Modifiers == ModifierKeys.None)
		{
			if (main.ProgramPlayer.PlayPauseToggleCommand.CanExecute(null))
			{
				main.ProgramPlayer.PlayPauseToggleCommand.Execute(null);
				e.Handled = true;
			}
		}
	}

	private void PopOutLibrary_Click(object sender, RoutedEventArgs e)
	{
		if (_poppedOutLibrary != null)
		{
			_poppedOutLibrary.Activate();
			return;
		}
		_poppedOutLibrary = new Window
		{
			Title = $"Library & Playsets — {AppVersionInfo.ProductName}",
			Width = 1340,
			Height = 800,
			Owner = this,
			DataContext = DataContext,
			Content = new LibraryPlaysetsView()
		};
		_poppedOutLibrary.Closed += (_, _) => _poppedOutLibrary = null;
		_poppedOutLibrary.Show();
	}

	private void PopOutSchedule_Click(object sender, RoutedEventArgs e)
	{
		if (_poppedOutSchedule != null)
		{
			_poppedOutSchedule.Activate();
			return;
		}
		_poppedOutSchedule = new Window
		{
			Title = $"Broadcast Schedule — {AppVersionInfo.ProductName}",
			Width = 1080,
			Height = 680,
			Owner = this,
			DataContext = DataContext,
			Content = new ScheduleView()
		};
		_poppedOutSchedule.Closed += (_, _) => _poppedOutSchedule = null;
		_poppedOutSchedule.Show();
	}

	private void About_Click(object sender, RoutedEventArgs e)
	{
		MessageBox.Show(
			this,
			$"{AppVersionInfo.ProductName}\nVersion {AppVersionInfo.Version}\n\n" +
			"Windows 10/11 Broadcast Automation, AutoMix Transition Engine, Playset Manager & Live Streaming Console.\n\n" +
			$"Copyright 2026 {AppVersionInfo.Manufacturer}",
			"About RadioApp Studio",
			MessageBoxButton.OK,
			MessageBoxImage.Information);
	}

	private void Exit_Click(object sender, RoutedEventArgs e)
	{
		_allowClose = true;
		Close();
	}

	private void MainWindow_StateChanged(object? sender, EventArgs e)
	{
		if (WindowState == WindowState.Minimized && DataContext is MainViewModel { MinimizeToTray: true })
		{
			HideToTray("RadioApp Studio was minimized to the tray.");
		}
	}

	private void MainWindow_Closing(object? sender, CancelEventArgs e)
	{
		if (!_allowClose && DataContext is MainViewModel { CloseToTray: true })
		{
			e.Cancel = true;
			HideToTray("RadioApp Studio is still running in the tray. Use File → Exit to quit.");
			return;
		}

		_notifyIcon.Visible = false;
		_notifyIcon.Dispose();
		if (!_resourcesDisposed)
		{
			_resourcesDisposed = true;
			(DataContext as IDisposable)?.Dispose();
		}
	}

	private void HideToTray(string message)
	{
		ShowInTaskbar = false;
		Hide();
		_notifyIcon.Visible = true;
		_notifyIcon.BalloonTipTitle = AppVersionInfo.ProductName;
		_notifyIcon.BalloonTipText = message;
		_notifyIcon.ShowBalloonTip(2500);
	}

	private void RestoreFromTray()
	{
		Show();
		ShowInTaskbar = true;
		WindowState = WindowState.Normal;
		Activate();
	}

	private NotifyIcon CreateNotifyIcon()
	{
		var icon = new NotifyIcon
		{
			Text = AppVersionInfo.ProductName,
			Icon = LoadTrayIcon(),
			Visible = false
		};
		icon.DoubleClick += (_, _) => RestoreFromTray();

		var menu = new ContextMenuStrip();
		menu.Items.Add("Open Studio", null, (_, _) => RestoreFromTray());
		menu.Items.Add("Exit", null, (_, _) =>
		{
			_allowClose = true;
			icon.Visible = false;
			Close();
		});
		icon.ContextMenuStrip = menu;
		return icon;
	}

	private static Icon LoadTrayIcon()
	{
		try
		{
			StreamResourceInfo? res = Application.GetResourceStream(new Uri("Assets/RadioAppIcon.ico", UriKind.Relative));
			if (res != null) return new Icon(res.Stream);
		}
		catch { }
		return SystemIcons.Application;
	}
}
