using System;
using System.IO;
using System.Windows;
using System.Windows.Threading;

namespace RadioApp;

public partial class App : Application
{
	protected override void OnStartup(StartupEventArgs e)
	{
		AppDomain.CurrentDomain.UnhandledException += CurrentDomain_UnhandledException;
		base.DispatcherUnhandledException += App_DispatcherUnhandledException;
		base.OnStartup(e);
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
