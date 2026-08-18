using System;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using RadioApp.Services;
using RadioApp.ViewModels;

namespace RadioApp;

public partial class App : Application
{
	private MainViewModel? _mainViewModel;
	private WebServerService? _webServer;
	private TrayIconService? _trayIcon;

	protected override void OnStartup(StartupEventArgs e)
	{
		AppDomain.CurrentDomain.UnhandledException += CurrentDomain_UnhandledException;
		base.DispatcherUnhandledException += App_DispatcherUnhandledException;
		base.OnStartup(e);

		// Prevent application from closing when no WPF window is open
		ShutdownMode = ShutdownMode.OnExplicitShutdown;

		try
		{
			// Initialize the core broadcast view model
			_mainViewModel = new MainViewModel();

			// Start the embedded Web Server for the Headless Web Studio (Port 5000)
			_webServer = new WebServerService(_mainViewModel, port: 5000);
			_webServer.Start();

			// Initialize the Windows Taskbar Notification Area (System Tray) Icon
			_trayIcon = new TrayIconService(_mainViewModel, webPort: 5000);
			_trayIcon.ShowNotification(
				"RadioApp 5.0 Active",
				"RadioApp 5.0 is running headlessly. Double-click this icon to open the Web Studio Dashboard.",
				System.Windows.Forms.ToolTipIcon.Info);
		}
		catch (Exception ex)
		{
			LogException(ex);
			MessageBox.Show($"Error initializing RadioApp Headless Service: {ex.Message}\nSee app-error.log", "RadioApp Startup Error", MessageBoxButton.OK, MessageBoxImage.Error);
			Shutdown(1);
		}
	}

	protected override void OnExit(ExitEventArgs e)
	{
		_trayIcon?.Dispose();
		_webServer?.Dispose();
		_mainViewModel?.Dispose();
		base.OnExit(e);
	}

	private void App_DispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
	{
		LogException(e.Exception);
		MessageBox.Show("Unhandled exception: " + e.Exception.Message + "\nSee app-error.log", "Error", MessageBoxButton.OK, MessageBoxImage.Hand);
		e.Handled = true;
		Shutdown(1);
	}

	private void CurrentDomain_UnhandledException(object sender, UnhandledExceptionEventArgs e)
	{
		if (e.ExceptionObject is Exception ex)
		{
			LogException(ex);
			return;
		}
		File.AppendAllText("app-error.log", $"Unhandled non-exception object: {e.ExceptionObject}\n");
	}

	private static void LogException(Exception exception)
	{
		string logText = $"[{DateTime.Now:O}] {exception}\n";
		File.AppendAllText("app-error.log", logText);
	}
}
