using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using RadioApp.Models;
using RadioApp.ViewModels;

namespace RadioApp.Views;

public partial class LibraryPlaysetsView : UserControl
{
	public LibraryPlaysetsView()
	{
		InitializeComponent();
	}

	private void LibraryDataGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
	{
		if (DataContext is MainViewModel main)
		{
			List<LibraryItem> selected = LibraryDataGrid.SelectedItems.OfType<LibraryItem>().ToList();
			main.Library.UpdateMultiSelection(selected);
		}
	}

	private void LibraryDataGrid_RowEditEnding(object sender, DataGridRowEditEndingEventArgs e)
	{
		// Dispatch at Background priority so WPF commits edited cell values BEFORE persisting to SQLite!
		if (e.EditAction == DataGridEditAction.Commit && e.Row.Item is LibraryItem item && DataContext is MainViewModel main)
		{
			Dispatcher.BeginInvoke(DispatcherPriority.Background, () => main.Library.PersistItemUpdate(item));
		}
	}

	private void LibraryDataGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
	{
		if (DataContext is MainViewModel main && main.LoadSelectedToProgramCommand.CanExecute(null))
		{
			main.LoadSelectedToProgramCommand.Execute(null);
		}
	}

	private void DeleteSelected_Click(object sender, RoutedEventArgs e)
	{
		if (DataContext is MainViewModel main)
		{
			List<LibraryItem> targets = LibraryDataGrid.SelectedItems.OfType<LibraryItem>().ToList();
			if (targets.Count == 0 && main.Library.SelectedItem != null)
			{
				targets.Add(main.Library.SelectedItem);
			}
			main.Library.DeleteItems(targets);
		}
	}

	private void BulkAddToPlayset_Click(object sender, RoutedEventArgs e)
	{
		if (DataContext is MainViewModel main && BulkTargetPlaysetCombo.SelectedItem is Playset ps)
		{
			main.Library.AddSelectionToPlayset(ps);
		}
	}

	private void UserControl_DragOver(object sender, DragEventArgs e)
	{
		e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
		e.Handled = true;
	}

	private async void UserControl_Drop(object sender, DragEventArgs e)
	{
		if (DataContext is MainViewModel main && e.Data.GetDataPresent(DataFormats.FileDrop) &&
			e.Data.GetData(DataFormats.FileDrop) is string[] paths)
		{
			await main.Library.ImportDroppedPathsAsync(paths);
		}
	}
}
