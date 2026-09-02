using MyAlbum.Core.Models;

namespace MyAlbum_App.ViewModels;

/// <summary>
/// A saved smart album shown in the left panel: the stored name plus a summary
/// of its filter criteria.
/// </summary>
public sealed class SmartAlbumItem
{
    public SmartAlbum Album { get; }
    public LibraryFilter Filter { get; }

    public long Id => Album.Id;
    public string Name => Album.Name;
    public string Summary => Filter.ToString();
    public long PhotoCount { get; set; }

    public SmartAlbumItem(SmartAlbum album)
    {
        Album = album;
        Filter = LibraryFilter.FromJson(album.FilterJson);
    }
}
