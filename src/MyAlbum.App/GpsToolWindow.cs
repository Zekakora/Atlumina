using MyAlbum_App.Pages;

namespace MyAlbum_App;

/// <summary>
/// A separate window hosting the GPS 补全工具 (GpsToolPage). Mica backdrop + centered,
/// sizable, sized to fit the current work area.
/// </summary>
public sealed class GpsToolWindow : ToolWindow
{
    public GpsToolWindow()
        : base("GPS 补全工具", new GpsToolPage(), 1100, 760, "tool_window_gps.json")
    {
    }
}
