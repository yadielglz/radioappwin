using System.Windows;
using RadioApp.Models;

namespace RadioApp.Views;

public partial class ImportOptionsWindow : Window
{
	public ImportOptions Options { get; }

	public ImportOptionsWindow(ImportOptions options)
	{
		InitializeComponent();
		Options = options;
		DataContext = this;
	}

	private void Import_Click(object sender, RoutedEventArgs e)
	{
		DialogResult = true;
		Close();
	}

	private void Cancel_Click(object sender, RoutedEventArgs e)
	{
		DialogResult = false;
		Close();
	}
}
