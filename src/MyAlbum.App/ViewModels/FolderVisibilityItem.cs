namespace MyAlbum_App.ViewModels;

/// <summary>An imported folder with its current visibility, used by the folder management dialog.</summary>
public sealed class FolderVisibilityItem
{
    public string Name { get; }
    public string Path { get; }
    public bool IsVisible { get; }

    public FolderVisibilityItem(string name, string path, bool isVisible)
    {
        Name = name;
        Path = path;
        IsVisible = isVisible;
    }
}
