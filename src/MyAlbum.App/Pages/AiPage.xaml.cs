using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using MyAlbum_App.ViewModels;

namespace MyAlbum_App.Pages;

public sealed partial class AiPage : Page
{
    public AiViewModel ViewModel { get; }

    public AiPage()
    {
        InitializeComponent();
        ViewModel = App.Services.GetRequiredService<AiViewModel>();
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        ViewModel.RefreshDeviceInfo();
        _ = ViewModel.LoadResultsAsync();
        _ = ViewModel.LoadDeepResultsAsync();
        _ = ViewModel.LoadPlaceCoverageAsync();
        _ = ViewModel.LoadAddressCoverageAsync();
        // Reflect a query routed from the top search bar (semantic mode).
        if (!string.IsNullOrWhiteSpace(ViewModel.SearchQuery) && SearchQueryBox is not null)
        {
            SearchQueryBox.Text = ViewModel.SearchQuery;
        }
    }

    private async void SearchBox_OnQuerySubmitted(AutoSuggestBox sender, AutoSuggestBoxQuerySubmittedEventArgs args)
    {
        ViewModel.SearchQuery = args.QueryText;
        await ViewModel.SearchCommand.ExecuteAsync(null);
    }

    private async void ClearAiTags_OnClick(object sender, RoutedEventArgs e)
    {
        var dialog = new ContentDialog
        {
            Title = "删除所有 AI 标记",
            Content = "将删除全部照片的 AI 场景/物体标签（手动标签不受影响）。删除后可重新点击「开始自动打标」。确定继续吗？",
            PrimaryButtonText = "删除",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = XamlRoot,
        };
        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
        {
            await ViewModel.ClearAiTagsAsync();
        }
    }

    private async void ShowSkipped_OnClick(object sender, RoutedEventArgs e)
    {
        var items = ViewModel.SkippedAddresses;
        if (items.Count == 0)
        {
            return;
        }

        var list = new ListView
        {
            SelectionMode = ListViewSelectionMode.None,
            ItemsSource = items.Select(s => $"{s.FileName}    （地点：{s.Place}）").ToList(),
            MaxHeight = 420,
        };

        var dialog = new ContentDialog
        {
            Title = $"本次被跳过的照片（{items.Count} 张）",
            Content = new ScrollViewer
            {
                Content = list,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            },
            CloseButtonText = "关闭",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = XamlRoot,
        };
        await dialog.ShowAsync();
    }
}
