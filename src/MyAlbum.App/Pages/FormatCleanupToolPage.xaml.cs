using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using MyAlbum.Core.Models;
using MyAlbum_App.ViewModels;

namespace MyAlbum_App.Pages;

/// <summary>
/// The "格式清理" tool page (hosted in a separate movable window). Groups by folder +
/// base name, shows thumbnails, lets the user keep only the chosen formats, and deletes
/// the rest into the recycle bin.
/// </summary>
public sealed partial class FormatCleanupToolPage : Page
{
    /// <summary>Invoked to close the hosting window.</summary>
    public Action? CloseRequested;

    public FormatCleanupToolViewModel ViewModel { get; }

    public FormatCleanupToolPage(IReadOnlyList<PhotoRecord> photos)
    {
        ViewModel = new FormatCleanupToolViewModel(photos);
        InitializeComponent();
        Loaded += async (_, _) => await ViewModel.RunAsync();
    }

    private void GroupThumb_OnDoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (VisualFind.FindDataContext<PhotoGridItem>(e.OriginalSource as DependencyObject) is not { } item ||
            VisualFind.FindDataContext<FormatGroupItem>(e.OriginalSource as DependencyObject) is not { } group)
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

    private async void Delete_OnClick(object sender, RoutedEventArgs e)
    {
        if (!ViewModel.CanDelete)
        {
            return;
        }
        var dialog = new ContentDialog
        {
            Title = "确认删除",
            Content = "未保留格式的文件将被移入回收站，可从回收站恢复。确定继续吗？",
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

    private void Close_OnClick(object sender, RoutedEventArgs e) => CloseRequested?.Invoke();
}
