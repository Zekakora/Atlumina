using MyAlbum.Core.Models;

namespace MyAlbum_App;

/// <summary>
/// A separate movable window hosting the 拍摄时间修复 tool (DateFixToolPage). The current
/// filter's photos are passed in; the scan and fix run inside the window.
/// </summary>
public sealed class DateFixToolWindow : ToolWindow
{
    public DateFixToolWindow(IReadOnlyList<PhotoRecord> photos)
        : base("拍摄时间修复", new Pages.DateFixToolPage(photos), 920, 640, "tool_window_datefix.json")
    {
        if (Content is Pages.DateFixToolPage page)
        {
            page.CloseRequested += Close;
        }
    }
}
