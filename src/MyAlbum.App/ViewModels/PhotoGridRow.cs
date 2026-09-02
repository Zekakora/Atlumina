using System.Collections.ObjectModel;

namespace MyAlbum_App.ViewModels;

/// <summary>
/// One justified row in the adaptive photo grid (like the Windows Photos app): every tile
/// shares the row height, widths are proportional to each photo's aspect ratio and the row
/// is packed so the last tile absorbs the remainder — the row always fills the full width.
/// </summary>
public sealed class PhotoGridRow
{
    public ObservableCollection<PhotoGridItem> Items { get; } = new();
}
