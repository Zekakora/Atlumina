namespace MyAlbum_App;

/// <summary>
/// A separate movable window hosting the 低质量照片清理 tool (QualityToolPage). Combines
/// the blur (照片质量分析) and NIMA aesthetic (照片美学分析) passes: flags low-quality photo
/// groups and lets the user delete whole groups into the recycle bin.
/// </summary>
public sealed class QualityToolWindow : ToolWindow
{
    public QualityToolWindow()
        : base("低质量照片清理", new Pages.QualityToolPage(), 1020, 740, "tool_window_quality.json")
    {
        if (Content is Pages.QualityToolPage page)
        {
            page.CloseRequested += Close;
        }
    }
}
