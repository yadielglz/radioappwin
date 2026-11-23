using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;

namespace RadioApp.Helpers;

public class RelayCommand : ICommand
{
	private readonly Action<object?> _execute;
	private readonly Func<object?, bool>? _canExecute;

	public event EventHandler? CanExecuteChanged
	{
		add
		{
			if (_canExecute != null)
			{
				CommandManager.RequerySuggested += value;
			}
		}
		remove
		{
			if (_canExecute != null)
			{
				CommandManager.RequerySuggested -= value;
			}
		}
	}

	public RelayCommand(Action<object?> execute, Func<object?, bool>? canExecute = null)
	{
		_execute = execute ?? throw new ArgumentNullException(nameof(execute));
		_canExecute = canExecute;
	}

	public RelayCommand(Action execute, Func<bool>? canExecute = null)
		: this(_ => execute(), canExecute == null ? null : _ => canExecute())
	{
	}

	public bool CanExecute(object? parameter) => _canExecute?.Invoke(parameter) ?? true;

	public void Execute(object? parameter) => _execute(parameter);

	public static void InvalidateAll() => CommandManager.InvalidateRequerySuggested();
}

public sealed class InverseBoolToVisibilityConverter : IValueConverter
{
	public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
		value is bool b && b ? Visibility.Collapsed : Visibility.Visible;

	public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
		value is Visibility v && v != Visibility.Visible;
}

public sealed class NullToVisibilityConverter : IValueConverter
{
	public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
	{
		bool invert = string.Equals(parameter as string, "invert", StringComparison.OrdinalIgnoreCase);
		bool hasValue = value != null && (!(value is string s) || !string.IsNullOrWhiteSpace(s));
		if (invert) hasValue = !hasValue;
		return hasValue ? Visibility.Visible : Visibility.Collapsed;
	}

	public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
		Binding.DoNothing;
}

public sealed class HexColorToBrushConverter : IValueConverter
{
	public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
	{
		if (value is string hex && !string.IsNullOrWhiteSpace(hex))
		{
			try
			{
				if (ColorConverter.ConvertFromString(hex) is Color c)
				{
					return new SolidColorBrush(c);
				}
			}
			catch
			{
			}
		}
		return new SolidColorBrush(Color.FromRgb(0x02, 0x84, 0xC7));
	}

	public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
		Binding.DoNothing;
}

public sealed class EqualityToBoolConverter : IValueConverter
{
	public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
		Equals(value?.ToString(), parameter?.ToString());

	public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
	{
		if (value is true && parameter != null)
		{
			if (targetType == typeof(int) && int.TryParse(parameter.ToString(), out int parsed))
			{
				return parsed;
			}
			return parameter;
		}
		return Binding.DoNothing;
	}
}
