namespace MyAlbum_App.ViewModels;

/// <summary>
/// Carries the photo list and start index when navigating to the fullscreen viewer.
/// </summary>
public sealed class ViewerSession
{
    public required IReadOnlyList<PhotoGridItem> Photos { get; init; }
    public required int StartIndex { get; init; }
}
