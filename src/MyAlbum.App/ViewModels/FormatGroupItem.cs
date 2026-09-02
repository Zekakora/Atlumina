using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using MyAlbum.Core.Models;

namespace MyAlbum_App.ViewModels;

/// <summary>
/// A logical photo shown in the "格式清理" tool window: all files sharing the same folder +
/// base name (e.g. DSC05361.ARW + DSC05361.HIF + DSC05361.JPG). Multi-selected groups are
/// pruned down to the user-picked formats.
/// </summary>
public partial class FormatGroupItem : ObservableObject
{
    public FormatGroupItem(IReadOnlyList<PhotoRecord> photos)
    {
        Photos = photos;
        Stem = Path.GetFileNameWithoutExtension(photos[0].FileName);
        Folder = photos[0].DirectoryPath;
    }

    public IReadOnlyList<PhotoRecord> Photos { get; }

    /// <summary>Thumbnail tiles of every format variant (double-click opens a large view).</summary>
    public ObservableCollection<PhotoGridItem> PhotoItems { get; } = new();

    public string Stem { get; }
    public string Folder { get; }
    public string FormatsText => string.Join(" · ", Photos.Select(p => p.Extension.TrimStart('.').ToUpperInvariant()));

    [ObservableProperty]
    public partial bool IsSelected { get; set; } = true;
}
