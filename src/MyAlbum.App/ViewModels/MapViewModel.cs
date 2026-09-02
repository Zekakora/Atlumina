using System.Collections.ObjectModel;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Xaml.Media.Imaging;
using MyAlbum.Core.Data;
using MyAlbum.Core.Services;

namespace MyAlbum_App.ViewModels;

/// <summary>A single photo marker pushed to the map page.</summary>
public sealed record MapPhotoItem(
    long Id, double Lat, double Lon, string File, string Time, string? Thumb, long Ts);

/// <summary>Marker JSON plus the taken-time span of the GPS photos (for the time ruler).</summary>
public sealed record MapLoadResult(string Json, DateTime? MinTaken, DateTime? MaxTaken);

/// <summary>A photo shown in the native cluster side panel.</summary>
public sealed class MapClusterItem
{
    public long Id { get; }
    public string File { get; }
    public string Time { get; }
    public BitmapImage? ThumbImage { get; }

    /// <summary>The underlying photo record (for the fullscreen viewer).</summary>
    public MyAlbum.Core.Models.PhotoRecord Photo { get; }

    /// <summary>Cache path of the thumbnail (for the fullscreen viewer's grid tile).</summary>
    public string? ThumbPath { get; }

    public MapClusterItem(MyAlbum.Core.Models.PhotoRecord photo, string? thumbPath)
    {
        Id = photo.Id;
        File = photo.FileName;
        Time = photo.TakenAtUtc?.ToString("yyyy-MM-dd HH:mm") ?? "";
        Photo = photo;
        ThumbPath = thumbPath;
        ThumbImage = thumbPath is null ? null : new BitmapImage(new Uri(thumbPath));
    }
}

/// <summary>
/// Feeds the WebView2 map with GPS-tagged photos and bridges selection events
/// back to the app. The map itself is rendered by Leaflet + Supercluster in JS.
/// </summary>
public partial class MapViewModel : ObservableObject
{
    /// <summary>
    /// The map JS reads camelCase property names (p.lat, p.lon, p.thumb, ...); the
    /// default JsonSerializer would emit PascalCase and every coordinate would be undefined.
    /// </summary>
    private static readonly JsonSerializerOptions MarkerJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly PhotoDatabase _db;
    private readonly ThumbnailService _thumbs;

    [ObservableProperty]
    public partial bool IsBusy { get; set; }

    [ObservableProperty]
    public partial string StatusText { get; set; } = "就绪";

    [ObservableProperty]
    public partial double Progress { get; set; }

    [ObservableProperty]
    public partial string ProgressText { get; set; } = "";

    [ObservableProperty]
    public partial MapPhotoItem? SelectedPhoto { get; set; }

    [ObservableProperty]
    public partial bool HasSelection { get; set; }

    [ObservableProperty]
    public partial string SelectedFile { get; set; } = "";

    [ObservableProperty]
    public partial string SelectedTime { get; set; } = "";

    [ObservableProperty]
    public partial string SelectedGps { get; set; } = "";

    [ObservableProperty]
    public partial string SelectedCamera { get; set; } = "";

    [ObservableProperty]
    public partial string SelectedLens { get; set; } = "";

    [ObservableProperty]
    public partial string SelectedExposure { get; set; } = "";

    [ObservableProperty]
    public partial ObservableCollection<MapClusterItem> ClusterItems { get; set; } = new();

    [ObservableProperty]
    public partial bool HasCluster { get; set; }

    [ObservableProperty]
    public partial int ClusterCount { get; set; }

    [ObservableProperty]
    public partial string ClusterTitle { get; set; } = "";

    [ObservableProperty]
    public partial bool HasGpsRange { get; set; }

    public IAsyncRelayCommand RefreshCommand { get; }
    public IAsyncRelayCommand FillGpsFromGpxCommand { get; }

    public MapViewModel(PhotoDatabase db, ThumbnailService thumbs)
    {
        _db = db;
        _thumbs = thumbs;
        RefreshCommand = new AsyncRelayCommand(RefreshAsync);
        FillGpsFromGpxCommand = new AsyncRelayCommand<string?>(FillGpsFromGpxAsync);
    }

    /// <summary>
    /// Loads every GPS-tagged photo as a marker payload (JSON for the map JS) plus the
    /// taken-time span of those photos (for the time ruler).
    /// </summary>
    public async Task<MapLoadResult> LoadMarkersAsync()
    {
        // 全量加载（不限 1 万条）：地图时间尺度要覆盖库中最早到最晚的照片。
        var photos = await _db.QueryPhotosAsync(limit: int.MaxValue);
        var items = new List<MapPhotoItem>();
        DateTime? min = null, max = null;
        foreach (var p in photos)
        {
            if (p.GpsLatitude is null || p.GpsLongitude is null)
            {
                continue;
            }
            // 没有缓存缩略图的补生成，保证最大缩放时能看清单张照片。
            string? thumb = p.ThumbnailCachePath;
            if (thumb is null || !File.Exists(thumb))
            {
                thumb = await _thumbs.GetOrCreateThumbnailAsync(p);
                if (thumb is not null && !string.Equals(thumb, p.ThumbnailCachePath, StringComparison.OrdinalIgnoreCase))
                {
                    p.ThumbnailCachePath = thumb;
                    await _db.UpsertPhotoAsync(p);
                }
            }
            long ts = p.TakenAtUtc is { } t ? new DateTimeOffset(t).ToUnixTimeMilliseconds() : 0L;
            items.Add(new MapPhotoItem(
                p.Id,
                p.GpsLatitude.Value,
                p.GpsLongitude.Value,
                p.FileName,
                p.TakenAtUtc?.ToString("yyyy-MM-dd HH:mm") ?? "",
                thumb is null ? null : ToVirtualUrl(thumb),
                ts));
            if (p.TakenAtUtc is { } taken)
            {
                if (min is null || taken < min) min = taken;
                if (max is null || taken > max) max = taken;
            }
        }
        return new MapLoadResult(JsonSerializer.Serialize(items, MarkerJson), min, max);
    }

    /// <summary>
    /// Entry point for adding GPS coordinates to photos. The original GPX-track
    /// write-back has been removed (it rewrote source files in place with no backup);
    /// a new implementation for adding GPS will replace this stub.
    /// </summary>
    public Task FillGpsFromGpxAsync(string? gpxPath)
    {
        StatusText = string.IsNullOrWhiteSpace(gpxPath)
            ? "请选择 GPS 数据来源"
            : "GPS 补全功能待实现";
        return Task.CompletedTask;
    }

    public async Task RefreshAsync()
    {
        IsBusy = true;
        try
        {
            var photos = await _db.QueryPhotosAsync(limit: int.MaxValue);
            int withGps = photos.Count(p => p.GpsLatitude is not null && p.GpsLongitude is not null);
            StatusText = $"共 {photos.Count} 张，其中 {withGps} 张有 GPS";
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>Handles a marker selection posted from the map JS.</summary>
    public async Task HandleSelectAsync(long id)
    {
        var photo = await _db.GetPhotoByIdAsync(id);
        if (photo is null)
        {
            return;
        }
        var exposure = new List<string>();
        if (photo.Iso is { } iso) exposure.Add($"ISO {iso}");
        if (photo.ShutterSpeed is { } shutter) exposure.Add(shutter);
        if (photo.Aperture is { } aperture) exposure.Add($"f/{aperture:0.0}");
        if (photo.FocalLengthMm is { } focal) exposure.Add($"{focal:0}mm");

        SelectedFile = photo.FileName;
        SelectedTime = photo.TakenAtUtc?.ToString("yyyy-MM-dd HH:mm") ?? "";
        SelectedGps = photo.GpsLatitude is { } lat && photo.GpsLongitude is { } lon
            ? $"{lat:0.00000}, {lon:0.00000}"
            : "";
        SelectedCamera = $"{photo.CameraMake} {photo.CameraModel}".Trim();
        SelectedLens = photo.LensModel ?? "";
        SelectedExposure = string.Join("  ", exposure);
        HasSelection = true;
    }

    /// <summary>
    /// Resolves a photo record plus its thumbnail for the fullscreen viewer
    /// (double-click on a map popup image). Returns null when the photo is gone.
    /// </summary>
    public async Task<MapClusterItem?> ResolveFullscreenPhotoAsync(long id)
    {
        var photo = await _db.GetPhotoByIdAsync(id);
        if (photo is null)
        {
            return null;
        }
        string? thumb = photo.ThumbnailCachePath;
        if (thumb is null || !File.Exists(thumb))
        {
            thumb = await _thumbs.GetOrCreateThumbnailAsync(photo);
        }
        return new MapClusterItem(photo, thumb);
    }

    /// <summary>Shows a diagnostic string from the map JS in the page status bar.</summary>
    public void SetMapDiagnostics(string text) => StatusText = text;

    public void ShowSelection(MapPhotoItem item)
    {
        SelectedPhoto = item;
        HasSelection = true;
        SelectedFile = item.File;
        SelectedTime = item.Time;
        SelectedGps = $"{item.Lat:0.00000}, {item.Lon:0.00000}";
    }

    /// <summary>Shows a cluster's photos in the native side panel.</summary>
    public async Task ShowClusterAsync(IReadOnlyList<long> ids)
    {
        var photos = await _db.GetPhotosByIdsAsync(ids);
        ClusterItems.Clear();
        foreach (var p in photos)
        {
            var thumb = p.ThumbnailCachePath is not null && File.Exists(p.ThumbnailCachePath)
                ? p.ThumbnailCachePath
                : null;
            ClusterItems.Add(new MapClusterItem(p, thumb));
        }
        ClusterCount = ClusterItems.Count;
        ClusterTitle = $"聚合照片（{ClusterCount} 张）";
        HasCluster = true;
    }

    /// <summary>Hides the cluster side panel.</summary>
    public void CloseCluster()
    {
        HasCluster = false;
        ClusterItems.Clear();
        ClusterCount = 0;
        ClusterTitle = "";
    }

    /// <summary>
    /// Maps an absolute cache path to the "myalbum.data" virtual host used by WebView2
    /// so <c>&lt;img&gt;</c> can load thumbnails without file:// restrictions.
    /// </summary>
    private static string ToVirtualUrl(string absolutePath)
    {
        var root = MyAlbum.Core.Infrastructure.AppPaths.AppDataDirectory.TrimEnd('\\', '/');
        var file = absolutePath.TrimEnd('\\', '/');
        if (file.StartsWith(root + "\\", StringComparison.OrdinalIgnoreCase))
        {
            return "https://myalbum.data/" + file[(root.Length + 1)..].Replace('\\', '/');
        }
        return "file:///" + file.Replace('\\', '/');
    }
}
