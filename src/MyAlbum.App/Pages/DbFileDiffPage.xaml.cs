using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using MyAlbum_App.ViewModels;

namespace MyAlbum_App.Pages;

/// <summary>
/// The "数据库与文件对比" tool page (hosted in a separate movable window). Scans the whole
/// library against its source files, shows matching / differing / missing photos, and lets
/// the user sync either direction via the footer buttons.
/// </summary>
public sealed partial class DbFileDiffPage : Page
{
    public DbFileDiffViewModel ViewModel { get; }

    public DbFileDiffPage()
    {
        ViewModel = App.Services.GetRequiredService<DbFileDiffViewModel>();
        InitializeComponent();
        Loaded += async (_, _) =>
        {
            if (ViewModel.Items.Count == 0)
            {
                await ViewModel.ScanAsync();
            }
        };
    }

    private void SelectAll_OnClick(object sender, RoutedEventArgs e) => ViewModel.SelectAll();

    private void ClearSelection_OnClick(object sender, RoutedEventArgs e) => ViewModel.ClearSelection();

    private async void Overwrite_OnClick(object sender, RoutedEventArgs e)
    {
        if (!ViewModel.CanOverwrite)
        {
            return;
        }
        var dialog = new ContentDialog
        {
            Title = "覆盖数据库",
            Content = $"将把 {ViewModel.SelectedCount} 张照片的元数据（大小 / 修改时间 / 拍摄时间 / 相机 / 尺寸）覆盖到数据库。数据库中的评分、标签、缩略图不受影响。确定继续吗？",
            PrimaryButtonText = "覆盖",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = XamlRoot,
        };
        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
        {
            await ViewModel.OverwriteDatabaseAsync();
        }
    }

    private async void WriteBack_OnClick(object sender, RoutedEventArgs e)
    {
        if (!ViewModel.CanWriteBack)
        {
            return;
        }
        var dialog = new ContentDialog
        {
            Title = "写入照片",
            Content = $"将把 {ViewModel.SelectedCount} 张照片的数据库数据（拍摄时间 / 评分 / GPS）写入照片 EXIF，源文件旁保留 .original 备份。此操作会修改原始照片文件。确定继续吗？",
            PrimaryButtonText = "写入",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = XamlRoot,
        };
        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
        {
            var message = await ViewModel.WriteBackAsync();
            if (message is not null)
            {
                await new ContentDialog
                {
                    Title = "无法写入照片",
                    Content = message,
                    CloseButtonText = "确定",
                    XamlRoot = XamlRoot,
                }.ShowAsync();
            }
        }
    }
}
