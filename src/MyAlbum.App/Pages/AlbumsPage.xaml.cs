using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using MyAlbum_App.ViewModels;

namespace MyAlbum_App.Pages;

public sealed partial class AlbumsPage : Page
{
    public AlbumsViewModel ViewModel { get; }

    public AlbumsPage()
    {
        InitializeComponent();
        ViewModel = App.Services.GetRequiredService<AlbumsViewModel>();
    }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        await ViewModel.InitializeAsync();
    }

    private void AlbumCard_OnItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is SmartAlbumItem item)
        {
            ViewModel.ApplyAlbumCommand.Execute(item);
            if (App.Window is MainWindow window)
            {
                window.SelectView("home");
            }
        }
    }

    private async void AlbumDelete_OnClick(object sender, RoutedEventArgs e)
    {
        if (sender is MenuFlyoutItem { Tag: long id })
        {
            var item = ViewModel.AlbumCards.FirstOrDefault(a => a.Id == id);
            if (item is not null)
            {
                ViewModel.DeleteAlbumCommand.Execute(item);
            }
        }
    }

    private async void NewAlbum_OnClick(object sender, RoutedEventArgs e)
    {
        var input = new TextBox { PlaceholderText = "智能相册名称", MinWidth = 260 };
        var dialog = new ContentDialog
        {
            Title = "保存当前筛选为智能相册",
            Content = input,
            PrimaryButtonText = "保存",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = XamlRoot,
        };
        if (await dialog.ShowAsync() == ContentDialogResult.Primary && !string.IsNullOrWhiteSpace(input.Text))
        {
            await ViewModel.SaveAlbumAsync(input.Text);
        }
    }
}
