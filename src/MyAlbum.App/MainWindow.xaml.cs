using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using MyAlbum.Core.Data;
using MyAlbum_App.Pages;
using MyAlbum_App.Services;
using MyAlbum_App.ViewModels;

namespace MyAlbum_App;

/// <summary>
/// The application window: a top navigation bar (主页/相册/地图/AI/设置) with a
/// search box on the right, and a Frame that hosts the selected page.
/// </summary>
public sealed partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        AppWindow.SetIcon("Assets/AppIcon.ico");
        ThemeManager.Register(this);

        SelectView("home");
    }

    /// <summary>Selects a top-level view by tag and navigates the content frame.</summary>
    public void SelectView(string tag)
    {
        foreach (var item in NavView.MenuItems.OfType<NavigationViewItem>())
        {
            if (string.Equals(item.Tag?.ToString(), tag, StringComparison.OrdinalIgnoreCase))
            {
                NavView.SelectedItem = item;
                break;
            }
        }
    }

    private void NavView_OnSelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.SelectedItem is NavigationViewItem item && item.Tag is string tag)
        {
            NavigateTo(tag);
        }
    }

    private void NavigateTo(string tag)
    {
        Type? page = tag switch
        {
            "home" => typeof(HomePage),
            "albums" => typeof(AlbumsPage),
            "people" => typeof(PeoplePage),
            "map" => typeof(MapPage),
            "ai" => typeof(AiPage),
            "settings" => typeof(SettingsPage),
            _ => null,
        };
        if (page is not null && MainFrame.CurrentSourcePageType != page)
        {
            MainFrame.Navigate(page);
        }
    }

    private void SearchBox_OnTextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        if (args.Reason == AutoSuggestionBoxTextChangeReason.UserInput)
        {
            _ = UpdateSuggestionsAsync(sender.Text);
        }
    }

    private async Task UpdateSuggestionsAsync(string query)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            SearchBox.ItemsSource = null;
            return;
        }
        var db = App.Services.GetRequiredService<PhotoDatabase>();
        var suggestions = await db.GetSearchSuggestionsAsync(query.Trim());
        // Prepend resolved place-name hints matching the prefix (e.g. "成都") so geo search is discoverable.
        var placeHits = (await db.GetPlaceSuggestionsAsync(query.Trim(), 10))
            .Select(n => "📍 " + n)
            .ToList();
        var merged = placeHits.Concat(suggestions).Take(10).ToList();
        SearchBox.ItemsSource = merged;
    }

    private void SearchBox_OnSuggestionChosen(AutoSuggestBox sender, AutoSuggestBoxSuggestionChosenEventArgs args)
    {
        if (args.SelectedItem is string s)
        {
            sender.Text = s;
        }
    }

    private void SemanticModeToggle_OnChanged(object sender, RoutedEventArgs e)
    {
        bool semantic = SemanticModeToggle.IsChecked == true;
        SearchBox.PlaceholderText = semantic
            ? "语义搜图：描述照片，如「海边的狗」…"
            : "搜索照片…";
        // Keyword suggestions don't apply to semantic search; clear stale suggestions.
        if (semantic)
        {
            SearchBox.ItemsSource = null;
        }
        else
        {
            _ = UpdateSuggestionsAsync(SearchBox.Text);
        }
    }

    private async void SearchBox_OnQuerySubmitted(AutoSuggestBox sender, AutoSuggestBoxQuerySubmittedEventArgs args)
    {
        var text = string.IsNullOrWhiteSpace(args.ChosenSuggestion as string) ? args.QueryText : (string)args.ChosenSuggestion;
        text = text?.Trim().TrimStart(' ', '\uD83D', '\uDCCD'); // strip "📍 " prefix from place suggestions
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }
        var home = App.Services.GetRequiredService<HomeViewModel>();
        if (SemanticModeToggle.IsChecked == true)
        {
            // Semantic mode: run MobileCLIP search, then show the results in the Home grid.
            var ai = App.Services.GetRequiredService<AiViewModel>();
            ai.SearchQuery = text.Trim();
            if (!ai.IsClipInstalled)
            {
                await home.ApplySemanticSearchAsync([], text.Trim());
                SelectView("home");
                home.StatusText = "未安装 MobileCLIP 模型，无法语义搜图。请先在 AI 功能页下载模型并运行深度分析。";
                return;
            }
            await ai.SearchCommand.ExecuteAsync(null);
            await home.ApplySemanticSearchAsync(ai.LastSearchPhotos ?? [], ai.LastSearchQuery);
            SelectView("home");
            return;
        }
        // Keyword mode: a place name ("成都") first searches photos by GPS around that place.
        await home.ApplyGeoSearchAsync(text.Trim());
        SelectView("home");
    }
}
