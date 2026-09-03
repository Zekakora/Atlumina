using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Xaml.Media.Imaging;
using MyAlbum.Core.Models;

namespace MyAlbum_App.ViewModels;

/// <summary>
/// A single tile in the photo grid. The thumbnail is a BitmapImage created from
/// the L2 cache path; WinUI decodes it asynchronously off the UI thread.
/// In the masonry grid every tile shares the same <see cref="TileSize"/> (row height);
/// the width follows the photo's aspect ratio so rows stay equal-height while tiles
/// vary in width.
/// </summary>
public partial class PhotoGridItem : ObservableObject
{
    public PhotoRecord Photo { get; }

    public string FileName => Photo.FileName;
    public DateTime? TakenAtUtc => Photo.TakenAtUtc;
    public string? CameraModel => Photo.CameraModel;

    public BitmapImage? Thumbnail
    {
        get
        {
            if (_thumbnail is null && _thumbnailPath is not null && File.Exists(_thumbnailPath))
            {
                _thumbnail = new BitmapImage(new Uri(_thumbnailPath));
            }
            return _thumbnail;
        }
    }

    private string? _thumbnailPath;
    private BitmapImage? _thumbnail;

    [ObservableProperty]
    public partial int Rating { get; set; }

    /// <summary>Row height (all tiles in a row share it).</summary>
    [ObservableProperty]
    public partial double TileSize { get; set; } = 150;

    /// <summary>Width derived from <see cref="TileSize"/> and the photo's aspect ratio.</summary>
    [ObservableProperty]
    public partial double TileWidth { get; set; } = 150;

    /// <summary>True when the photo is part of the suggested-keep set (去重检测 window).</summary>
    [ObservableProperty]
    public partial bool IsSuggestedKeep { get; set; }

    /// <summary>True when the photo is part of the current multi-selection (Ctrl/Shift/Ctrl+A).</summary>
    [ObservableProperty]
    public partial bool IsSelected { get; set; }

    public bool HasRating => Rating > 0;

    /// <summary>Back-fills the thumbnail path once a missing render is generated in the
    /// background (see <see cref="HomeViewModel.PopulatePhotosAsync"/>). Raises
    /// <see cref="Thumbnail"/> so a OneWay-bound Image re-decodes.</summary>
    public void SetThumbnailPath(string? path)
    {
        if (string.Equals(_thumbnailPath, path, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }
        _thumbnailPath = path;
        _thumbnail = null;
        OnPropertyChanged(nameof(Thumbnail));
    }

    public PhotoGridItem(PhotoRecord photo, string? thumbnailPath)
    {
        Photo = photo;
        Rating = photo.Rating;
        TileWidth = ComputeWidth(TileSize);
        _thumbnailPath = thumbnailPath;
    }

    partial void OnTileSizeChanged(double value) => TileWidth = ComputeWidth(value);

    private double ComputeWidth(double rowHeight)
    {
        if (Photo.Width is { } w && Photo.Height is { } h && h > 0)
        {
            return Math.Clamp(rowHeight * w / h, 56, 340);
        }
        return rowHeight;
    }
}
