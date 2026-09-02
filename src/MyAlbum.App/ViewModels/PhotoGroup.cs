namespace MyAlbum_App.ViewModels;

/// <summary>
/// A group of photos for the zoom-grouped grid (by day / month / year).
/// It is itself a collection so CollectionViewSource grouping can use it directly;
/// the group header renders <see cref="Header"/>.
/// </summary>
public sealed class PhotoGroup : List<PhotoGridItem>
{
    public string Header { get; }

    public PhotoGroup(string header, IEnumerable<PhotoGridItem> items)
        : base(items)
    {
        Header = header;
    }
}
