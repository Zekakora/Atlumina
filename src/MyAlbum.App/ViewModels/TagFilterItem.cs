namespace MyAlbum_App.ViewModels;

/// <summary>A tag entry in the left panel's tag filter list.</summary>
public sealed class TagFilterItem
{
    public string Name { get; }
    public long Count { get; }

    public TagFilterItem(string name, long count)
    {
        Name = name;
        Count = count;
    }
}
