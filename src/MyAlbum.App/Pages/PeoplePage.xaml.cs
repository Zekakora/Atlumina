using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using MyAlbum_App.ViewModels;

namespace MyAlbum_App.Pages;

public sealed partial class PeoplePage : Page
{
    public PeopleViewModel ViewModel { get; }

    public PeoplePage()
    {
        InitializeComponent();
        ViewModel = App.Services.GetRequiredService<PeopleViewModel>();
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        _ = ViewModel.InitializeAsync();
    }

    private async void PersonCard_OnItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is PersonItem item)
        {
            await ViewModel.OpenPersonAsync(item);
        }
    }

    private async void RenamePerson_OnClick(object sender, RoutedEventArgs e)
    {
        if (sender is MenuFlyoutItem { Tag: PersonItem item })
        {
            await ShowRenameDialogAsync(item);
        }
    }

    private async void MergePerson_OnClick(object sender, RoutedEventArgs e)
    {
        if (sender is MenuFlyoutItem { Tag: PersonItem item })
        {
            await ShowMergeDialogAsync(item);
        }
    }

    private async void DeletePerson_OnClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: PersonItem item })
        {
            return;
        }
        var dialog = new ContentDialog
        {
            Title = $"删除「{item.Title}」？",
            Content = $"将删除该人物的 {item.FaceCount} 张人脸识别记录，照片本身不受影响。此操作不可撤销。",
            PrimaryButtonText = "删除",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = XamlRoot,
        };
        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
        {
            await ViewModel.DeletePersonAsync(item);
        }
    }

    private async Task ShowRenameDialogAsync(PersonItem item)
    {
        var box = new TextBox
        {
            Text = item.Title,
            PlaceholderText = "姓名（留空清除）",
            Header = "为该人物命名",
        };
        var dialog = new ContentDialog
        {
            Title = $"重命名：{item.Title}",
            Content = box,
            PrimaryButtonText = "保存",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = XamlRoot,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }
        await ViewModel.RenamePersonAsync(item, box.Text);
    }

    private async Task ShowMergeDialogAsync(PersonItem source)
    {
        // Targets = every other person (must be >= 1 to merge anywhere).
        var targets = ViewModel.People.Where(p => p.PersonId != source.PersonId).ToList();
        if (targets.Count == 0)
        {
            var noTarget = new ContentDialog
            {
                Title = "无法合并",
                Content = "库中只有一个已识别人物，没有可合并的目标。",
                CloseButtonText = "确定",
                XamlRoot = XamlRoot,
            };
            await noTarget.ShowAsync();
            return;
        }

        var picker = new ListView
        {
            ItemsSource = targets,
            SelectionMode = ListViewSelectionMode.Single,
            MaxHeight = 320,
            DisplayMemberPath = "Title",
        };
        var dialog = new ContentDialog
        {
            Title = $"合并「{source.Title}」到…",
            Content = picker,
            PrimaryButtonText = "合并",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = XamlRoot,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }
        if (picker.SelectedItem is PersonItem target)
        {
            await ViewModel.MergePersonAsync(source, target);
        }
    }
}
