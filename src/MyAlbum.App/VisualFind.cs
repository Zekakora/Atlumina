using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using MyAlbum.Core.Models;

namespace MyAlbum_App;

/// <summary>Small visual-tree helpers shared by the tool pages.</summary>
public static class VisualFind
{
    /// <summary>Walks up the visual tree to the nearest element whose data context is T.</summary>
    public static T? FindDataContext<T>(DependencyObject? start) where T : class
    {
        for (var el = start; el is not null; el = VisualTreeHelper.GetParent(el))
        {
            if (el is FrameworkElement { DataContext: T item })
            {
                return item;
            }
        }
        return null;
    }
}
