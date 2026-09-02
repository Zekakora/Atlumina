using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace MyAlbum_App.ViewModels;

/// <summary>
/// A node in the folder tree (left panel). The root node "全部照片" has an empty Path.
/// Children are the sub-folders that contain indexed photos.
/// </summary>
public partial class FolderTreeNode : ObservableObject
{
    public string Name { get; }
    public string Path { get; }
    public long PhotoCount { get; }
    public bool IsAllPhotos { get; }

    public ObservableCollection<FolderTreeNode> Children { get; } = new();

    [ObservableProperty]
    public partial bool IsExpanded { get; set; }

    public bool HasChildren => Children.Count > 0;

    public FolderTreeNode(string name, string path, long photoCount, bool isAllPhotos = false)
    {
        Name = name;
        Path = path;
        PhotoCount = photoCount;
        IsAllPhotos = isAllPhotos;
        IsExpanded = isAllPhotos;
    }
}
