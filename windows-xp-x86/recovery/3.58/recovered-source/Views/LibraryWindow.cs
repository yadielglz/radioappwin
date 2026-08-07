using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Markup;
using RadioApp.Models;
using RadioApp.ViewModels;

namespace RadioApp.Views;

public partial class LibraryWindow : Window, IComponentConnector
{
	public LibraryWindow()
	{
		InitializeComponent();
	}

	private void LibraryDataGrid_RowEditEnding(object sender, DataGridRowEditEndingEventArgs e)
	{
		if (base.DataContext is MainViewModel main)
		{
			main.Library.SaveChanges();
		}
	}

	private void DeleteLibrarySelection_Click(object sender, RoutedEventArgs e)
	{
		if (base.DataContext is MainViewModel main)
		{
			List<LibraryItem> selectedItems = LibraryDataGrid.SelectedItems.OfType<LibraryItem>().ToList();
			if (selectedItems.Count == 0 && main.Library.SelectedItem != null)
			{
				selectedItems.Add(main.Library.SelectedItem);
			}
			main.Library.DeleteItems(selectedItems);
		}
	}

	private void LibraryDataGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
	{
		if (base.DataContext is MainViewModel main && main.Library.SelectedItem != null && main.LoadSelectedToProgramCommand.CanExecute(null))
		{
			main.LoadSelectedToProgramCommand.Execute(null);
		}
	}
}
