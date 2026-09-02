using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace MyAlbum_App.ViewModels;

/// <summary>Chooses the header template for group headers and the row template for photo rows.</summary>
public sealed class GridItemTemplateSelector : DataTemplateSelector
{
    public DataTemplate? HeaderTemplate { get; set; }
    public DataTemplate? RowTemplate { get; set; }

    protected override DataTemplate? SelectTemplateCore(object item) =>
        item is PhotoGroupHeaderItem ? HeaderTemplate : RowTemplate;

    protected override DataTemplate? SelectTemplateCore(object item, DependencyObject container) =>
        SelectTemplateCore(item);
}
