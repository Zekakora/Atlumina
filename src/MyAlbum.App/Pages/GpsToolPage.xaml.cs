using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.Web.WebView2.Core;
using MyAlbum_App.Services;
using MyAlbum_App.ViewModels;
using Windows.Data.Json;

namespace MyAlbum_App.Pages;

/// <summary>
/// The GPS 补全工具 page (hosted in a separate movable window). Left: a Leaflet map with
/// green GPS anchors and a draggable red pin for the selection; right: the grouped photo
/// list. The map talks to <see cref="GpsToolViewModel"/> via WebView2 postMessage
/// (moved = red pin dragged / map clicked, anchor = green pin clicked).
/// </summary>
public sealed partial class GpsToolPage : Page
{
    public GpsToolViewModel ViewModel { get; }

    private readonly Action<string> _postToMap;

    public GpsToolPage()
    {
        ViewModel = App.Services.GetRequiredService<GpsToolViewModel>();
        _postToMap = PostToMap;
        InitializeComponent();
        Loaded += async (_, _) => await EnsureMapLoadedAsync();
        Unloaded += (_, _) =>
        {
            // The window is going away; don't let the singleton VM post to a dead view.
            if (ReferenceEquals(ViewModel.PostJson, _postToMap))
            {
                ViewModel.PostJson = null;
            }
        };
    }

    private async Task EnsureMapLoadedAsync()
    {
        if (GpsMapView.CoreWebView2 is not null)
        {
            ViewModel.PostJson = _postToMap;
            return;
        }
        var host = await MapHostService.InitializeAsync(GpsMapView, msg => ViewModel.StatusText = msg);
        if (host is null)
        {
            ViewModel.StatusText = "地图初始化失败（WebView2 不可用）。仍可从列表勾选照片使用「全部链式归类」/「选中需复核」。";
            return;
        }
        host.WebMessageReceived += GpsMapView_OnWebMessageReceived;
        ViewModel.PostJson = _postToMap;
        PostTileSource();
        ViewModel.PushToolPayload();
    }

    private void PostTileSource()
    {
        if (GpsMapView.CoreWebView2 is null)
        {
            return;
        }
        var source = App.Services.GetRequiredService<AppState>().MapTileSource;
        GpsMapView.CoreWebView2.PostWebMessageAsJson($$"""{"type":"tiles","source":"{{source}}"}""");
    }

    private void PostToMap(string json) => GpsMapView.CoreWebView2?.PostWebMessageAsJson(json);

    private void GpsMapView_OnWebMessageReceived(CoreWebView2 sender, CoreWebView2WebMessageReceivedEventArgs args)
    {
        try
        {
            var root = JsonObject.Parse(args.WebMessageAsJson);
            if (root.TryGetValue("type", out var typeVal))
            {
                string type = typeVal.GetString();
                if (type is "moved" or "anchor")
                {
                    ViewModel.HandleToolMessage(root);
                }
                // "log" messages are JS diagnostics; ignored here.
            }
        }
        catch
        {
            // ignore malformed messages
        }
    }

    private void Threshold_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ThresholdCombo.SelectedItem is GpsThresholdOption option)
        {
            ViewModel.SetThreshold(option.Label);
        }
    }

    /// <summary>双击文件名列 → 在该组的全屏查看器中打开此照片。</summary>
    private void PhotoRow_OnDoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (VisualFind.FindDataContext<GpsToolPhotoItem>(e.OriginalSource as DependencyObject) is not { } item ||
            VisualFind.FindDataContext<GpsToolGroupItem>(e.OriginalSource as DependencyObject) is not { } group)
        {
            return;
        }
        var photos = group.Photos.Select(p => new PhotoGridItem(p.Assignment.Photo, null)).ToList();
        int start = Math.Max(0, photos.FindIndex(p => ReferenceEquals(p.Photo, item.Assignment.Photo)));
        var session = new ViewerSession { Photos = photos, StartIndex = start };
        _ = new ViewerWindow(session);
    }
}
