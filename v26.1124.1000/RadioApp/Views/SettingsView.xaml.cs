using System.Windows;
using System.Windows.Controls;
using RadioApp.ViewModels;

namespace RadioApp.Views;

public partial class SettingsView : UserControl
{
	private bool _syncingPassword;

	public SettingsView()
	{
		InitializeComponent();
		Loaded += SettingsView_Loaded;
	}

	private void SettingsView_Loaded(object sender, RoutedEventArgs e)
	{
		if (DataContext is MainViewModel main)
		{
			_syncingPassword = true;
			StreamPasswordBox.Password = main.StreamConfig.Password ?? string.Empty;
			GitTokenBox.Password = main.GitUpdateToken ?? string.Empty;
			_syncingPassword = false;
		}
	}

	private void StreamPasswordBox_PasswordChanged(object sender, RoutedEventArgs e)
	{
		if (_syncingPassword) return;
		if (DataContext is MainViewModel main)
		{
			main.StreamConfig.Password = StreamPasswordBox.Password;
		}
	}

	private void ShowPassword_Changed(object sender, RoutedEventArgs e)
	{
		bool show = ShowPasswordToggle.IsChecked == true;
		if (show)
		{
			StreamPasswordBox.Visibility = Visibility.Collapsed;
			StreamPasswordPlainBox.Visibility = Visibility.Visible;
		}
		else
		{
			_syncingPassword = true;
			if (DataContext is MainViewModel main)
			{
				StreamPasswordBox.Password = main.StreamConfig.Password ?? string.Empty;
			}
			_syncingPassword = false;
			StreamPasswordPlainBox.Visibility = Visibility.Collapsed;
			StreamPasswordBox.Visibility = Visibility.Visible;
		}
	}

	private void GitTokenBox_PasswordChanged(object sender, RoutedEventArgs e)
	{
		if (_syncingPassword) return;
		if (DataContext is MainViewModel main)
		{
			main.GitUpdateToken = GitTokenBox.Password;
		}
	}

	private void ShowGitToken_Changed(object sender, RoutedEventArgs e)
	{
		bool show = ShowGitTokenToggle.IsChecked == true;
		if (show)
		{
			GitTokenBox.Visibility = Visibility.Collapsed;
			GitTokenPlainBox.Visibility = Visibility.Visible;
		}
		else
		{
			_syncingPassword = true;
			if (DataContext is MainViewModel main)
			{
				GitTokenBox.Password = main.GitUpdateToken ?? string.Empty;
			}
			_syncingPassword = false;
			GitTokenPlainBox.Visibility = Visibility.Collapsed;
			GitTokenBox.Visibility = Visibility.Visible;
		}
	}
}
