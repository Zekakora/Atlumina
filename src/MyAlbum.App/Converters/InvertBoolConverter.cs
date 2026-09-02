using Microsoft.UI.Xaml.Data;

namespace MyAlbum_App.Converters;

/// <summary>Inverts a bool value.</summary>
public sealed class InvertBoolConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language) =>
        value is bool b && !b;

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        value is bool b && !b;
}
