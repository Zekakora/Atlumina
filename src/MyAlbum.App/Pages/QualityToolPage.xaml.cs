using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using MyAlbum_App.ViewModels;

namespace MyAlbum_App.Pages;

/// <summary>
/// The "低质量照片清理" tool page (hosted in a separate movable window). Runs the blur +
/// aesthetic analysis, shows low-quality photos grouped into logical photo groups, and deletes
/// whole groups into the recycle bin.
/// </summary>
public sealed partial class QualityToolPage : Page
{
    /// <summary>Invoked to close the hosting window.</summary>
    public Action? CloseRequested;

    public QualityToolViewModel ViewModel { get; }

    public QualityToolPage()
    {
        ViewModel = App.Services.GetRequiredService<QualityToolViewModel>();
        InitializeComponent();
        Loaded += async (_, _) =>
        {
            if (ViewModel.Groups.Count == 0 && !ViewModel.IsBusy)
            {
                await ViewModel.LoadGroupsAsync();
            }
        };
    }

    private void GroupThumb_OnDoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (VisualFind.FindDataContext<PhotoGridItem>(e.OriginalSource as DependencyObject) is not { } item ||
            VisualFind.FindDataContext<QualityGroupItem>(e.OriginalSource as DependencyObject) is not { } group)
        {
            return;
        }
        var photos = group.PhotoItems.ToList();
        var session = new ViewerSession { Photos = photos, StartIndex = Math.Max(0, photos.IndexOf(item)) };
        _ = new ViewerWindow(session);
    }

    private void ToolThumb_PointerEntered(object sender, PointerRoutedEventArgs e) =>
        HoverScale.PointerEntered((UIElement)sender);

    private void ToolThumb_PointerExited(object sender, PointerRoutedEventArgs e) =>
        HoverScale.PointerExited(sender as UIElement);

    private void Close_OnClick(object sender, RoutedEventArgs e) => CloseRequested?.Invoke();
}
