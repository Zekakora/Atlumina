using MyAlbum.Core.Models;

namespace MyAlbum_App;

/// <summary>
/// A separate movable window hosting the 格式清理 tool (FormatCleanupToolPage). The
/// current filter's photos are passed in; grouping, thumbnails and deletion run inside
/// the window.
/// </summary>
public sealed class FormatCleanupToolWindow : ToolWindow
{
    public FormatCleanupToolWindow(IReadOnlyList<PhotoRecord> photos)
        : base("格式清理", new Pages.FormatCleanupToolPage(photos), 1020, 740, "tool_window_format.json")
    {
        if (Content is Pages.FormatCleanupToolPage page)
        {
            page.CloseRequested += Close;
        }
    }
}
