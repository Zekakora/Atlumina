namespace MyAlbum.Core.Models;

/// <summary>
/// A smart album: a saved set of filter criteria evaluated against the index.
/// The filter is stored as JSON so the set of conditions can grow without schema changes.
/// </summary>
public sealed class SmartAlbum
{
    public long Id { get; set; }
    public string Name { get; set; } = "";
    public string FilterJson { get; set; } = "{}";
    public DateTime CreatedUtc { get; set; }
}
