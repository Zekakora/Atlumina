using Microsoft.UI.Xaml.Data;

namespace MyAlbum_App.Converters;

/// <summary>
/// Returns a filled or outline star glyph depending on whether the rating
/// reaches the button's index (ConverterParameter). Glyphs: \uE734 filled, \uE735 outline.
/// </summary>
public sealed class StarGlyphConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        int rating = value is int i ? i : 0;
        int index = int.TryParse(parameter?.ToString(), out var n) ? n : 0;
        return rating >= index ? "\uE734" : "\uE735";
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException();
}
