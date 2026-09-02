namespace MyAlbum.Core.Models;

/// <summary>
/// A folder known to the library. Watched folders are monitored with a FileSystemWatcher.
/// </summary>
public sealed class FolderRecord
{
    public long Id { get; set; }
    public string Path { get; set; } = "";
    public DateTime? LastScannedUtc { get; set; }
    public bool IsWatched { get; set; }
    public bool IsHidden { get; set; }
    public DateTime AddedUtc { get; set; }
}
