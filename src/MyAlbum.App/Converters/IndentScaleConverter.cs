using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;

namespace MyAlbum_App.Converters;

/// <summary>
/// Scales the per-depth indentation thickness produced by
/// <see cref="Microsoft.UI.Xaml.Controls.TreeViewItemTemplateSettings.Indentation"/>,
/// so the folder tree hugs the left edge more tightly. Default scale is 0.35;
/// pass a different factor via ConverterParameter.
/// </summary>
public sealed class IndentScaleConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        if (value is Thickness t)
        {
            double scale = 0.35;
            if (parameter is string s && double.TryParse(s, System.Globalization.CultureInfo.InvariantCulture, out var d))
            {
                scale = d;
            }
            return new Thickness(t.Left * scale, 0, 0, 0);
        }
        return value;
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException();
}
