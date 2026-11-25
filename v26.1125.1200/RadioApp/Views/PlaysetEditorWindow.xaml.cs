using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using RadioApp.Models;

namespace RadioApp.Views;

public partial class PlaysetEditorWindow : Window
{
	public Playset Playset { get; }
	public SmartPlaysetRule Rule { get; }

	public PlaysetEditorWindow(Playset playset, IReadOnlyList<string> categories)
	{
		InitializeComponent();
		Playset = playset;
		Rule = playset.ParseRule();
		DataContext = this;

		KindComboBox.ItemsSource = Enum.GetValues(typeof(PlaysetKind));
		RuleCategoryComboBox.ItemsSource = categories;

		TransitionOverrideComboBox.Items.Add("Use Studio Default");
		foreach (TransitionMode mode in Enum.GetValues(typeof(TransitionMode)))
		{
			TransitionOverrideComboBox.Items.Add(mode);
		}
		TransitionOverrideComboBox.SelectedItem = playset.TransitionModeOverride.HasValue
			? (object)playset.TransitionModeOverride.Value
			: "Use Studio Default";

		MinBpmBox.Text = Rule.MinBpm.HasValue ? Rule.MinBpm.Value.ToString("0.#", CultureInfo.InvariantCulture) : string.Empty;
		MaxBpmBox.Text = Rule.MaxBpm.HasValue ? Rule.MaxBpm.Value.ToString("0.#", CultureInfo.InvariantCulture) : string.Empty;
		UpdateSmartPanelVisibility();
	}

	private void KindComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
	{
		UpdateSmartPanelVisibility();
	}

	private void UpdateSmartPanelVisibility()
	{
		if (SmartRulesGroup != null)
		{
			SmartRulesGroup.Visibility = Playset.Kind == PlaysetKind.SmartRule ? Visibility.Visible : Visibility.Collapsed;
		}
	}

	private void Save_Click(object sender, RoutedEventArgs e)
	{
		if (string.IsNullOrWhiteSpace(Playset.Name))
		{
			MessageBox.Show(this, "Enter a name for this playset.", "Validation", MessageBoxButton.OK, MessageBoxImage.Warning);
			return;
		}

		Playset.TransitionModeOverride = TransitionOverrideComboBox.SelectedItem is TransitionMode mode ? mode : null;

		Rule.MinBpm = double.TryParse(MinBpmBox.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out double minBpm) && minBpm > 0 ? minBpm : null;
		Rule.MaxBpm = double.TryParse(MaxBpmBox.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out double maxBpm) && maxBpm > 0 ? maxBpm : null;

		if (Playset.Kind == PlaysetKind.SmartRule)
		{
			Playset.SetRule(Rule);
		}

		DialogResult = true;
		Close();
	}

	private void Cancel_Click(object sender, RoutedEventArgs e)
	{
		DialogResult = false;
		Close();
	}
}
