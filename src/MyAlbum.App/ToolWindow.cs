using System.Text.Json;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using MyAlbum.Core.Infrastructure;
using MyAlbum_App.Services;
using Windows.Graphics;

namespace MyAlbum_App;

/// <summary>
/// Base class for the movable tool windows (去重检测 / 拍摄时间修复 / 格式清理 / GPS 补全).
/// Uses the same Mica material + app icon as the main window, remembers its last
/// position / size / maximized state (per-window JSON file in <see cref="AppPaths.AppDataDirectory"/>),
/// and opens centered + sizable when there is no saved state yet.
/// </summary>
public abstract class ToolWindow : Window
{
    private readonly string _stateFilePath;

    protected ToolWindow(string title, UIElement content, int width, int height, string stateFileName)
    {
        Title = title;
        Content = content;
        _stateFilePath = Path.Combine(AppPaths.AppDataDirectory, stateFileName);

        // Match the main window: same app icon + Mica material.
        try
        {
            AppWindow.SetIcon("Assets/AppIcon.ico");
        }
        catch
        {
            // best effort
        }

        try
        {
            SystemBackdrop = new MicaBackdrop();
        }
        catch
        {
            // best effort; fall back to the default backdrop
        }

        ThemeManager.Register(this);

        Closed += (_, _) => SaveState();

        if (!ApplySavedState())
        {
            CenterAndSize(width, height);
        }

        Activate();
    }

    private void CenterAndSize(int width, int height)
    {
        try
        {
            var area = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Nearest).WorkArea;
            int w = Math.Min(width, area.Width - 80);
            int h = Math.Min(height, area.Height - 80);
            AppWindow.Move(new PointInt32(area.X + (area.Width - w) / 2, area.Y + (area.Height - h) / 2));
            AppWindow.Resize(new SizeInt32(w, h));
        }
        catch
        {
            // keep the OS default
        }
    }

    /// <summary>Restores the window to its last position/size, clamped into a visible display.</summary>
    private bool ApplySavedState()
    {
        try
        {
            if (!File.Exists(_stateFilePath))
            {
                return false;
            }
            var saved = JsonSerializer.Deserialize<WindowState>(File.ReadAllText(_stateFilePath));
            if (saved is null || saved.Width <= 0 || saved.Height <= 0)
            {
                return false;
            }

            var area = FindAreaForPoint(saved.X + saved.Width / 2, saved.Y + saved.Height / 2);
            int w = Math.Min(saved.Width, area.Width);
            int h = Math.Min(saved.Height, area.Height);
            int x = Math.Clamp(saved.X, area.X, area.X + Math.Max(0, area.Width - w));
            int y = Math.Clamp(saved.Y, area.Y, area.Y + Math.Max(0, area.Height - h));

            AppWindow.Move(new PointInt32(x, y));
            AppWindow.Resize(new SizeInt32(w, h));
            if (saved.IsMaximized && AppWindow.Presenter is OverlappedPresenter presenter)
            {
                presenter.Maximize();
            }
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static RectInt32 FindAreaForPoint(int x, int y)
    {
        return DisplayArea.GetFromPoint(new PointInt32(x, y), DisplayAreaFallback.Nearest).WorkArea;
    }

    private void SaveState()
    {
        try
        {
            var state = new WindowState
            {
                IsMaximized = AppWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Maximized },
                Width = AppWindow.Size.Width,
                Height = AppWindow.Size.Height,
                X = AppWindow.Position.X,
                Y = AppWindow.Position.Y,
            };
            Directory.CreateDirectory(AppPaths.AppDataDirectory);
            File.WriteAllText(_stateFilePath, JsonSerializer.Serialize(state));
        }
        catch
        {
            // best effort
        }
    }

    private sealed class WindowState
    {
        public int X { get; set; }
        public int Y { get; set; }
        public int Width { get; set; }
        public int Height { get; set; }
        public bool IsMaximized { get; set; }
    }
}
