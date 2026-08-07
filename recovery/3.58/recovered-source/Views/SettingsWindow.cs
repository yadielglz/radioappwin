using System.Windows;
using System.Windows.Markup;

namespace RadioApp.Views;

public partial class SettingsWindow : Window, IComponentConnector
{
	public SettingsWindow()
	{
		InitializeComponent();
	}

	private void Close_Click(object sender, RoutedEventArgs e)
	{
		Close();
	}
}
