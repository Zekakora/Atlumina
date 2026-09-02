using Microsoft.UI.Xaml.Data;

namespace MyAlbum_App.Converters;

/// <summary>
/// Maps a bool to a chevron rotation angle for an expand/collapse toggle:
/// true → 0° (down / expanded), false → 270° (right / collapsed).
/// </summary>
public sealed class BoolToAngleConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language) =>
        value is true ? 0d : 270d;

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        value is double d && d < 90;
}
