using System;
using System.Windows;
using System.Windows.Threading;
using RadioApp.Services;

namespace RadioApp;

public partial class App : Application
{
	protected override void OnStartup(StartupEventArgs e)
	{
		AppDomain.CurrentDomain.UnhandledException += CurrentDomain_UnhandledException;
		DispatcherUnhandledException += App_DispatcherUnhandledException;
		base.OnStartup(e);
	}

	private void App_DispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
	{
		LogService.Error("Fatal", "Unhandled UI exception", e.Exception);
		MessageBox.Show(
			$"An unexpected error occurred:\n{e.Exception.Message}\n\nDetails were logged to:\n{LogService.CurrentLogFile}",
			"RadioApp Studio Error",
			MessageBoxButton.OK,
			MessageBoxImage.Error);
		e.Handled = true;
	}

	private void CurrentDomain_UnhandledException(object sender, UnhandledExceptionEventArgs e)
	{
		if (e.ExceptionObject is Exception ex)
		{
			LogService.Error("Fatal", "Unhandled background exception", ex);
		}
		else
		{
			LogService.Error("Fatal", $"Unhandled non-exception object: {e.ExceptionObject}");
		}
	}
}
