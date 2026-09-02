using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using MyAlbum.Core.Models;
using MyAlbum_App.ViewModels;

namespace MyAlbum_App.Pages;

/// <summary>
/// The "去重检测" tool page (hosted in a separate movable window). Shows each duplicate
/// group as a card with thumbnails; double-click a thumbnail to open the large viewer.
/// Marked photos are deleted (sent to the recycle bin) via the footer button.
/// </summary>
public sealed partial class DedupToolPage : Page
{
    public DedupToolViewModel ViewModel { get; }

    public DedupToolPage(IReadOnlyList<PhotoRecord> photos)
    {
        ViewModel = new DedupToolViewModel(photos);
        InitializeComponent();
        Loaded += async (_, _) => await ViewModel.RunAsync();
    }

    private void GroupThumb_OnDoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (VisualFind.FindDataContext<DedupPhotoItem>(e.OriginalSource as DependencyObject) is not { } item ||
            VisualFind.FindDataContext<DedupGroupItem>(e.OriginalSource as DependencyObject) is not { } group)
        {
            return;
        }
        var photos = group.Occurrences.SelectMany(o => o.Photos).Select(p => p.Grid).ToList();
        var session = new ViewerSession { Photos = photos, StartIndex = Math.Max(0, photos.IndexOf(item.Grid)) };
        _ = new ViewerWindow(session);
    }

    private void SelectDeletable_OnClick(object sender, RoutedEventArgs e) => ViewModel.SelectDeletable();

    private void ClearSelection_OnClick(object sender, RoutedEventArgs e) => ViewModel.ClearSelection();

    private async void Delete_OnClick(object sender, RoutedEventArgs e)
    {
        if (!ViewModel.CanDelete)
        {
            return;
        }
        var dialog = new ContentDialog
        {
            Title = "确认删除",
            Content = $"将删除 {ViewModel.MarkedCount} 张重复照片，文件会移入回收站，可从回收站恢复。确定继续吗？",
            PrimaryButtonText = "删除",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = XamlRoot,
        };
        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
        {
            await ViewModel.DeleteAsync();
        }
    }

    private void ToolThumb_PointerEntered(object sender, PointerRoutedEventArgs e) =>
        HoverScale.PointerEntered((UIElement)sender);

    private void ToolThumb_PointerExited(object sender, PointerRoutedEventArgs e) =>
        HoverScale.PointerExited(sender as UIElement);
}
