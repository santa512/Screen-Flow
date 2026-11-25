using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace ScreenFlow;
public sealed class StepNumberConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value is int index ? (index + 1).ToString("00", culture) : "";
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => DependencyProperty.UnsetValue;
}
