using System.IO;
using System.Windows;
using Microsoft.Win32;
using RadioApp.Models;
using RadioApp.Services;

namespace RadioApp.Views;

public partial class CartPadEditorWindow : Window
{
	public CartPadConfig Pad { get; }

	public CartPadEditorWindow(CartPadConfig pad)
	{
		InitializeComponent();
		Pad = pad;
		DataContext = this;
	}

	private void Browse_Click(object sender, RoutedEventArgs e)
	{
		var dialog = new OpenFileDialog { Filter = AudioFileProbeCore.OpenFileDialogFilter };
		if (dialog.ShowDialog() == true)
		{
			Pad.FilePath = dialog.FileName;
			FilePathBox.Text = dialog.FileName;
			if (string.IsNullOrWhiteSpace(Pad.Label))
			{
				Pad.Label = Path.GetFileNameWithoutExtension(dialog.FileName).ToUpperInvariant();
			}
		}
	}

	private void Clear_Click(object sender, RoutedEventArgs e)
	{
		Pad.FilePath = string.Empty;
		FilePathBox.Text = string.Empty;
	}

	private void Save_Click(object sender, RoutedEventArgs e)
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
