using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using MyAlbum.Core.Infrastructure;
using MyAlbum_App.Controls;
using MyAlbum_App.Services;
using MyAlbum_App.ViewModels;

namespace MyAlbum_App.Pages;

public sealed partial class SettingsPage : Page
{
    public SettingsViewModel ViewModel { get; }

    public SettingsPage()
    {
        InitializeComponent();
        // 切走再切回不重建页面：避免每次都重新解析大 XAML（SettingsViewModel 为单例）。
        NavigationCacheMode = NavigationCacheMode.Required;
        ViewModel = App.Services.GetRequiredService<SettingsViewModel>();
    }

    public string DatabasePathText => AppPaths.DatabasePath;

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        Bindings.Update();
    }

    private async void ManageFolders_OnClick(object sender, RoutedEventArgs e)
    {
        var folders = await ViewModel.GetFoldersForManagementAsync();
        var panel = new StackPanel { Spacing = 8 };
        PopulateFolderPanel(panel, folders);

        var dialog = new ContentDialog
        {
            Title = "文件夹显示管理",
            Content = new ScrollViewer { Content = panel, MaxHeight = 420 },
            PrimaryButtonText = "保存",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = XamlRoot,
        };

        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
        {
            foreach (var row in panel.Children.OfType<Grid>())
            {
                var cb = row.Children.OfType<CheckBox>().FirstOrDefault();
                if (cb?.Tag is string path)
                {
                    await ViewModel.SetFolderVisibilityAsync(path, cb.IsChecked == true);
                }
            }
        }
    }

    private void PopulateFolderPanel(StackPanel panel, IReadOnlyList<FolderVisibilityItem> folders)
    {
        panel.Children.Clear();
        if (folders.Count == 0)
        {
            panel.Children.Add(new TextBlock
            {
                Text = "尚未导入任何文件夹。",
                Foreground = ThemeBrush.Resolve(this, "TextFillColorSecondaryBrush"),
            });
            return;
        }

        foreach (var f in folders)
        {
            var cb = new CheckBox
            {
                IsChecked = f.IsVisible,
                Tag = f.Path,
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Left,
            };
            cb.Content = new TextBlock
            {
                Text = $"{f.Name}  —  {f.Path}",
                TextTrimming = TextTrimming.CharacterEllipsis,
                TextWrapping = TextWrapping.NoWrap,
                MaxLines = 1,
            };
            var removeBtn = new Button
            {
                Content = new TextBlock { Text = "移除" },
                Margin = new Thickness(8, 0, 0, 0),
                Padding = new Thickness(8, 3, 8, 3),
                VerticalAlignment = VerticalAlignment.Center,
            };
            var path = f.Path;
            var armed = false;
            removeBtn.Click += async (_, _) =>
            {
                if (!armed)
                {
                    armed = true;
                    removeBtn.Content = new TextBlock
                    {
                        Text = "确认移除？",
                        Foreground = ThemeBrush.Resolve(this, "SystemFillColorCriticalBrush"),
                    };
                    return;
                }
                removeBtn.IsEnabled = false;
                try
                {
                    await ViewModel.RemoveFolderAsync(path);
                    PopulateFolderPanel(panel, await ViewModel.GetFoldersForManagementAsync());
                }
                finally
                {
                    removeBtn.IsEnabled = true;
                }
            };

            // 名称/路径列自适应伸缩并截断，按钮固定在最右，长路径不会把按钮挤出对话框。
            var row = new Grid
            {
                ColumnDefinitions =
                {
                    new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) },
                    new ColumnDefinition { Width = GridLength.Auto },
                },
            };
            Grid.SetColumn(cb, 0);
            Grid.SetColumn(removeBtn, 1);
            row.Children.Add(cb);
            row.Children.Add(removeBtn);
            panel.Children.Add(row);
        }
    }

    private async void RestoreDatabase_OnClick(object sender, RoutedEventArgs e)
    {
        var picker = new Microsoft.Windows.Storage.Pickers.FileOpenPicker(App.Window.AppWindow.Id)
        {
            Title = "选择数据库备份文件",
        };
        picker.FileTypeFilter.Add(".db");
        var file = await picker.PickSingleFileAsync();
        if (file is null)
        {
            return;
        }
        var path = file.Path;

        var confirm1 = ConfirmationDialog.Warning(XamlRoot, "恢复数据库",
            $"所选备份：{Path.GetFileName(path)}\n\n恢复将用此备份覆盖当前数据库，覆盖后无法撤销。",
            "继续", "取消");
        if (await confirm1.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        var confirm2 = ConfirmationDialog.Warning(XamlRoot, "再次确认",
            "此操作会覆盖当前数据库的全部内容（照片索引、标签、评分、人脸、智能相册等）。确定要继续吗？",
            "覆盖并恢复", "取消");
        if (await confirm2.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        var ok = await ViewModel.RestoreFromBackupAsync(path);
        await new ContentDialog
        {
            Title = ok ? "恢复完成" : "恢复失败",
            Content = ViewModel.BackupStatusText,
            CloseButtonText = "确定",
            XamlRoot = XamlRoot,
        }.ShowAsync();
    }

    private async void CleanupDatabase_OnClick(object sender, RoutedEventArgs e)
    {
        if (ViewModel.IsDatabaseBusy)
        {
            return;
        }

        ViewModel.DatabaseStatusText = "正在统计可清理的冗余数据…";
        var (summary, hasWork) = await ViewModel.BuildCleanupPreviewAsync();
        if (!hasWork)
        {
            await new ContentDialog
            {
                Title = "清理冗余数据",
                Content = summary,
                CloseButtonText = "确定",
                XamlRoot = XamlRoot,
            }.ShowAsync();
            ViewModel.DatabaseStatusText = summary;
            return;
        }

        var confirm = new ContentDialog
        {
            Title = "清理冗余数据",
            Content = summary + "\n\n此操作不可撤销。",
            PrimaryButtonText = "清理",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = XamlRoot,
        };
        if (await confirm.ShowAsync() != ContentDialogResult.Primary)
        {
            ViewModel.DatabaseStatusText = "已取消清理。";
            return;
        }

        await ViewModel.CleanupDatabaseAsync();
    }

    private async void ResetDatabase_OnClick(object sender, RoutedEventArgs e)
    {
        if (ViewModel.IsDatabaseBusy)
        {
            return;
        }

        var first = ConfirmationDialog.CriticalCountdown(XamlRoot, "重置数据库",
            "重置将删除全部照片索引、文件夹、标签、智能相册与人脸数据，并清除缩略图缓存。原始照片文件不受影响，但此操作不可恢复！",
            "继续", "取消");
        await first.ShowAsync();
        if (first.Tag is not true)
        {
            return;
        }

        var second = ConfirmationDialog.CriticalCountdown(XamlRoot, "再次确认",
            "这不可撤销。确定要清空整个数据库吗？（仅影响应用索引，原始照片文件不会被删除）",
            "重置数据库", "取消");
        await second.ShowAsync();
        if (second.Tag is not true)
        {
            return;
        }

        await ViewModel.ResetDatabaseAsync();
    }

    private void Exit_OnClick(object sender, RoutedEventArgs e)
    {
        ViewModel.ExitCommand.Execute(null);
    }
}
