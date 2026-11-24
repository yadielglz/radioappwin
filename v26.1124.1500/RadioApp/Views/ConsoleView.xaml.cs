using System.Windows.Controls;
using System.Windows.Input;
using RadioApp.ViewModels;

namespace RadioApp.Views;

public partial class ConsoleView : UserControl
{
	public ConsoleView()
	{
		InitializeComponent();
	}

	private void CartPad_MouseRightButtonUp(object sender, MouseButtonEventArgs e)
	{
		if (sender is Button btn && btn.DataContext is CartPadViewModel pad && DataContext is MainViewModel main)
		{
			main.EditCartPadCommand.Execute(pad);
			e.Handled = true;
		}
	}
}
