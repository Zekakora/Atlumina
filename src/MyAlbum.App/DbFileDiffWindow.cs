namespace MyAlbum_App;

/// <summary>
/// A separate movable window hosting the 数据库与文件对比 tool (DbFileDiffPage).
/// Scans the whole library against its source files and lets the user sync either
/// direction (photo → database / database → photo).
/// </summary>
public sealed class DbFileDiffWindow : ToolWindow
{
    public DbFileDiffWindow()
        : base("数据库与文件对比", new Pages.DbFileDiffPage(), 1080, 740, "tool_window_dbfile_diff.json")
    {
    }
}
