namespace MyAlbum.Core.Models;

/// <summary>A tag with optional photo count (when aggregated).</summary>
public sealed class TagRecord
{
    public string Name { get; }
    public bool IsAuto { get; }
    public long Count { get; set; }

    public TagRecord(string name, bool isAuto)
    {
        Name = name;
        IsAuto = isAuto;
    }
}
