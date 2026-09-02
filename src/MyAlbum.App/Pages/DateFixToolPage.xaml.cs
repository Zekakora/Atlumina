using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using MyAlbum.Core.Models;
using MyAlbum_App.ViewModels;

namespace MyAlbum_App.Pages;

/// <summary>
/// The "拍摄时间修复" tool page (hosted in a separate movable window). Shows each photo's
/// current creation time and the after-fix (shooting) time, with a status filter and
/// per-row selection before applying the fix.
/// </summary>
public sealed partial class DateFixToolPage : Page
{
    /// <summary>Invoked to close the hosting window.</summary>
    public Action? CloseRequested;

    public DateFixToolViewModel ViewModel { get; }

    public DateFixToolPage(IReadOnlyList<PhotoRecord> photos)
    {
        ViewModel = new DateFixToolViewModel(photos);
        InitializeComponent();
        Loaded += async (_, _) => await ViewModel.ScanAsync();
    }

    private async void Fix_OnClick(object sender, RoutedEventArgs e)
    {
        if (!ViewModel.CanFix)
        {
            return;
        }
        await ViewModel.FixAsync();
    }

    private void Close_OnClick(object sender, RoutedEventArgs e) => CloseRequested?.Invoke();
}
