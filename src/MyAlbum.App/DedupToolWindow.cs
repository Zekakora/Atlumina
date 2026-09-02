using MyAlbum.Core.Models;

namespace MyAlbum_App;

/// <summary>
/// A separate movable window hosting the 去重检测 tool (DedupToolPage). The current
/// filter's photos are passed in; detection and thumbnails load inside the window.
/// </summary>
public sealed class DedupToolWindow : ToolWindow
{
    public DedupToolWindow(IReadOnlyList<PhotoRecord> photos)
        : base("去重检测", new Pages.DedupToolPage(photos), 980, 700, "tool_window_dedup.json")
    {
    }
}
