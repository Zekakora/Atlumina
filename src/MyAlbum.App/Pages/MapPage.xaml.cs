using System.Collections.Generic;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using MyAlbum_App.Services;
using MyAlbum_App.ViewModels;
using Windows.Data.Json;

namespace MyAlbum_App.Pages;

/// <summary>
/// The GPS map view: a WebView2 hosting Leaflet + Supercluster. Photos with GPS
/// coordinates are pushed to the page as markers; clicking one posts back a select event.
/// </summary>
public sealed partial class MapPage : Page
{
    public MapViewModel ViewModel { get; }

    private bool _timeMachinePlaying;

    /// <summary>首次进入已加载；之后切走再切回不再重刷，保留地图缩放/中心。</summary>
    private bool _mapInitialized;

    /// <summary>时光机已播完、轨迹待保留；再按一次按钮清除轨迹恢复正常。</summary>
    private bool _tourFinished;

    public MapPage()
    {
        InitializeComponent();
        // 切走再切回不重建页面 / 不重刷地图：保留缩放、中心位置与 WebView2 实例。
        NavigationCacheMode = NavigationCacheMode.Required;
        ViewModel = App.Services.GetRequiredService<MapViewModel>();
        TimeRuler.RangeChanged += (_, _) => PushTimeFilter(commit: false);
        TimeRuler.RangeCommitted += (_, _) => PushTimeFilter(commit: true);
    }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        // 首次进入才加载；缓存页面切回时不重刷，保留缩放与中心位置。
        if (_mapInitialized)
        {
            return;
        }
        _mapInitialized = true;
        await ViewModel.RefreshAsync();
        await EnsureMapLoadedAsync();
        PostTileSource();
        await PushPhotosAsync();
    }

    /// <summary>刷新按钮：重新统计张数并重推全部 GPS 标记（保留当前缩放/中心）。</summary>
    private async void Refresh_OnClick(object sender, RoutedEventArgs e)
    {
        await ViewModel.RefreshAsync();
        if (MapView.CoreWebView2 is not null)
        {
            await PushPhotosAsync();
        }
        else
        {
            await EnsureMapLoadedAsync();
            PostTileSource();
            await PushPhotosAsync();
        }
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e);
        if (_timeMachinePlaying)
        {
            _timeMachinePlaying = false;
            UpdateTimeMachineButton();
            PostTimeMachineMessage(false);
        }
    }

    /// <summary>时光机按钮：播放中再按=取消；播完保留轨迹时再按=清除轨迹恢复正常；空闲=开始播放。</summary>
    private void TimeMachine_OnClick(object sender, RoutedEventArgs e)
    {
        if (_tourFinished)
        {
            _tourFinished = false;
            PostTimeMachineMessage(false);
            return;
        }
        _timeMachinePlaying = !_timeMachinePlaying;
        UpdateTimeMachineButton();
        PostTimeMachineMessage(_timeMachinePlaying);
    }

    private void UpdateTimeMachineButton()
    {
        TimeMachineButtonText.Text = _timeMachinePlaying ? "播放中" : "时光机";
    }

    private void TimeMachineSpeed_OnTextChanged(object sender, TextChangedEventArgs e)
    {
        // 倍速只在下一次点击播放时生效；输入即持久化到按钮状态即可。
        // 非法输入（空 / 非数字）在读取时回落为 1.0。
    }

    private double TimeMachineSpeedValue =>
        double.TryParse(TimeMachineSpeed.Text, System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out var v)
            ? Math.Clamp(v, 0.1, 20.0)
            : 1.0;

    private void PostTimeMachineMessage(bool start)
    {
        if (MapView.CoreWebView2 is null)
        {
            return;
        }
        MapView.CoreWebView2.PostWebMessageAsJson(
            $$"""{"type":"timemachine","start":{{(start ? "true" : "false")}},"speed":{{TimeMachineSpeedValue.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture)}}}""");
    }

    /// <summary>Tells the JS page which tile source (and thus which coordinate system) to use.</summary>
    private void PostTileSource()
    {
        if (MapView.CoreWebView2 is null)
        {
            return;
        }
        var source = App.Services.GetRequiredService<AppState>().MapTileSource;
        MapView.CoreWebView2.PostWebMessageAsJson($$"""{"type":"tiles","source":"{{source}}"}""");
    }

    /// <summary>
    /// Sets up a virtual host for the local HTML assets and another for the thumbnail
    /// cache, then loads map.html (shared bootstrap via <see cref="MapHostService"/>).
    /// Returns only after the document has finished navigating.
    /// </summary>
    private async Task EnsureMapLoadedAsync()
    {
        if (MapView.CoreWebView2 is not null)
        {
            return;
        }
        var host = await MapHostService.InitializeAsync(MapView, ViewModel.SetMapDiagnostics);
        if (host is not null)
        {
            host.WebMessageReceived += MapView_OnWebMessageReceived;
        }
    }

    private static void LogToCrash(string message)
    {
        try
        {
            var dir = Path.Combine(Path.GetTempPath(), "Atlumina");
            Directory.CreateDirectory(dir);
            File.AppendAllText(Path.Combine(dir, "crash.log"),
                $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} [MapPage] {message}\n");
        }
        catch
        {
            // never crash while logging
        }
    }

    private async Task PushPhotosAsync()
    {
        if (MapView.CoreWebView2 is null)
        {
            return;
        }
        var result = await ViewModel.LoadMarkersAsync();
        var count = 0;
        try
        {
            if (Windows.Data.Json.JsonArray.Parse(result.Json) is { } arr)
            {
                count = arr.Count;
            }
        }
        catch
        {
            // count is informational only
        }
        ViewModel.SetMapDiagnostics($"地图: 推送 {count} 个 GPS 标记…");
        ViewModel.HasGpsRange = result.MinTaken is not null;
        if (result.MinTaken is not null)
        {
            TimeRuler.SetRange(result.MinTaken, result.MaxTaken);
        }
        MapView.CoreWebView2.PostWebMessageAsJson($$"""{"type":"photos","photos":{{result.Json}}}""");
    }

    private void PushTimeFilter(bool commit)
    {
        if (MapView.CoreWebView2 is null || ViewModel.HasGpsRange is false)
        {
            return;
        }
        long minTs = new DateTimeOffset(TimeRuler.SelectedMin).ToUnixTimeMilliseconds();
        long maxTs = new DateTimeOffset(TimeRuler.SelectedMax).ToUnixTimeMilliseconds();
        string commitFlag = commit ? "true" : "false";
        MapView.CoreWebView2.PostWebMessageAsJson(
            $$"""{"type":"timefilter","minTs":{{minTs}},"maxTs":{{maxTs}},"commit":{{commitFlag}}}""");
    }

    private async void MapView_OnWebMessageReceived(
        Microsoft.Web.WebView2.Core.CoreWebView2 sender,
        Microsoft.Web.WebView2.Core.CoreWebView2WebMessageReceivedEventArgs args)
    {
        try
        {
            // The JS posts objects ({type:...}); WebMessageAsJson then carries the
            // object's JSON. (If JS posted a JSON string, this would be quoted text.)
            var root = JsonObject.Parse(args.WebMessageAsJson);
            if (root.TryGetValue("type", out var type))
            {
                if (type.GetString() == "select"
                    && root.TryGetValue("id", out var idVal))
                {
                    var id = (long)idVal.GetNumber();
                    await ViewModel.HandleSelectAsync(id);
                }
                else if (type.GetString() == "cluster"
                         && root.TryGetValue("ids", out var idsVal))
                {
                    if (idsVal.ValueType == JsonValueType.Array)
                    {
                        var ids = new List<long>();
                        foreach (var v in idsVal.GetArray())
                        {
                            ids.Add((long)v.GetNumber());
                        }
                        await ViewModel.ShowClusterAsync(ids);
                    }
                }
                else if (type.GetString() == "log"
                         && root.TryGetValue("text", out var text))
                {
                    ViewModel.SetMapDiagnostics("地图: " + text.GetString());
                    LogToCrash("[js] " + text.GetString());
                }
                else if (type.GetString() == "tour"
                         && root.TryGetValue("state", out var state)
                         && state.GetString() == "done"
                         && _timeMachinePlaying)
                {
                    // 时光机自然播放完毕：复位按钮，轨迹保留（再按一次才清除）。
                    _timeMachinePlaying = false;
                    _tourFinished = true;
                    UpdateTimeMachineButton();
                }
                else if (type.GetString() == "viewphoto"
                         && root.TryGetValue("id", out var viewIdVal))
                {
                    var target = await ViewModel.ResolveFullscreenPhotoAsync((long)viewIdVal.GetNumber());
                    if (target is not null)
                    {
                        var photos = new List<PhotoGridItem> { new(target.Photo, target.ThumbPath) };
                        _ = new global::MyAlbum_App.ViewerWindow(new ViewerSession { Photos = photos, StartIndex = 0 });
                    }
                }
            }
        }
        catch
        {
            // ignore malformed messages
        }
    }

    private void FillGps_OnClick(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        _ = new GpsToolWindow();
    }

    private async void ClusterGrid_OnItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is MapClusterItem item)
        {
            await ViewModel.HandleSelectAsync(item.Id);
        }
    }

    /// <summary>双击聚合缩略图 → 在该聚合的照片范围内打开全屏查看器。</summary>
    private void ClusterGrid_OnDoubleTapped(object sender, Microsoft.UI.Xaml.Input.DoubleTappedRoutedEventArgs e)
    {
        if (VisualFind.FindDataContext<MapClusterItem>(e.OriginalSource as DependencyObject) is not { } item)
        {
            return;
        }
        var photos = ViewModel.ClusterItems.Select(c => new PhotoGridItem(c.Photo, c.ThumbPath)).ToList();
        int start = Math.Max(0, photos.FindIndex(p => ReferenceEquals(p.Photo, item.Photo)));
        var session = new ViewerSession { Photos = photos, StartIndex = start };
        _ = new ViewerWindow(session);
    }

    private void CloseCluster_OnClick(object sender, RoutedEventArgs e)
    {
        ViewModel.CloseCluster();
    }
}
