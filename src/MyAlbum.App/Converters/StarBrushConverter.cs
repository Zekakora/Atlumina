using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media;

namespace MyAlbum_App.Converters;

/// <summary>
/// Returns the star color: a dim neutral for unlit stars and a warm amber for lit ones.
/// The converter parameter is the star's position; the bound value is the current rating.
/// </summary>
public sealed class StarBrushConverter : IValueConverter
{
    private static readonly SolidColorBrush LitBrush =
        new(Windows.UI.Color.FromArgb(0xFF, 0xFF, 0xAA, 0x33));
    private static readonly SolidColorBrush DimBrush =
        new(Windows.UI.Color.FromArgb(0x66, 0x66, 0x66, 0x66));

    public object Convert(object value, Type targetType, object parameter, string language)
    {
        int rating = value is int i ? i : 0;
        int pos = int.TryParse(parameter?.ToString(), out var n) ? n : 0;
        return rating >= pos ? LitBrush : DimBrush;
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException();
}
