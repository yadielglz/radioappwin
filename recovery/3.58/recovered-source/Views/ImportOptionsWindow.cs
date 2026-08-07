using System.Windows;
using System.Windows.Markup;
using RadioApp.Models;

namespace RadioApp.Views;

public partial class ImportOptionsWindow : Window, IComponentConnector
{
	public ImportOptions Options { get; }

	public ImportOptionsWindow(ImportOptions options)
	{
		InitializeComponent();
		Options = options;
		base.DataContext = this;
	}

	private void Import_Click(object sender, RoutedEventArgs e)
	{
		base.DialogResult = true;
		Close();
	}

	private void Cancel_Click(object sender, RoutedEventArgs e)
	{
		base.DialogResult = false;
		Close();
	}
}
