using MyAlbum.Core.Data;
using MyAlbum.Core.Models;
using MyAlbum.Core.Services;

namespace MyAlbum_App.ViewModels;

/// <summary>
/// Shared helpers for the tool windows (去重检测 / 格式清理): resolve an up-to-date
/// thumbnail cache path for a photo, generating the render when it is missing.
/// </summary>
public static class ToolThumbnailLoader
{
    public static async Task<string?> EnsureThumbnailAsync(ThumbnailService thumbs, PhotoDatabase db, PhotoRecord photo)
    {
        string? thumb = photo.ThumbnailCachePath;
        if (thumb is null || !File.Exists(thumb))
        {
            thumb = await thumbs.GetOrCreateThumbnailAsync(photo);
            if (thumb is not null && !string.Equals(thumb, photo.ThumbnailCachePath, StringComparison.OrdinalIgnoreCase))
            {
                photo.ThumbnailCachePath = thumb;
                await db.UpsertPhotoAsync(photo);
            }
        }
        return thumb;
    }

    public static async Task<PhotoGridItem?> LoadThumbAsync(ThumbnailService thumbs, PhotoDatabase db, PhotoRecord photo)
    {
        string? thumb = await EnsureThumbnailAsync(thumbs, db, photo);
        return thumb is null ? null : new PhotoGridItem(photo, thumb);
    }
}
