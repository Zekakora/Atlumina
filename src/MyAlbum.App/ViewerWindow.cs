using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using MyAlbum.Core.Infrastructure;
using MyAlbum_App.Services;
using MyAlbum_App.ViewModels;
using Windows.Graphics;

namespace MyAlbum_App;

/// <summary>
/// A separate window that hosts the photo viewer. It is windowed (not forced fullscreen),
/// remembers its last position/size/maximized state, and opens in the foreground.
/// </summary>
public sealed class ViewerWindow : Window
{
    private static readonly string StateFile = Path.Combine(AppPaths.AppDataDirectory, "viewer_window.json");

    public ViewerWindow(ViewerSession session)
    {
        var page = new ViewerPage();
        page.CloseRequested += Close;
        Content = page;
        Title = "相册查看器";

        // 左上角窗口图标与应用一致。
        try
        {
            AppWindow.SetIcon("Assets/AppIcon.ico");
        }
        catch
        {
            // best effort
        }

        Closed += (_, _) => SaveState();
        ApplySavedState();

        Activate();
        BringToFront();

        _ = page.SetupAsync(session);
    }

    private void ApplySavedState()
    {
        try
        {
            if (File.Exists(StateFile))
            {
                var saved = JsonSerializer.Deserialize<WindowState>(File.ReadAllText(StateFile));
                if (saved is not null && saved.Width > 0 && saved.Height > 0)
                {
                    AppWindow.Move(new PointInt32(saved.X, saved.Y));
                    AppWindow.Resize(new SizeInt32(saved.Width, saved.Height));
                    if (saved.IsMaximized && AppWindow.Presenter is OverlappedPresenter presenter)
                    {
                        presenter.Maximize();
                    }
                    return;
                }
            }
        }
        catch
        {
            // fall through to default sizing
        }

        // Default: a large windowed view, centered on the current work area.
        try
        {
            var area = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Nearest).WorkArea;
            int width = (int)(area.Width * 0.72);
            int height = (int)(area.Height * 0.82);
            AppWindow.Move(new PointInt32(area.X + (area.Width - width) / 2, area.Y + (area.Height - height) / 2));
            AppWindow.Resize(new SizeInt32(width, height));
        }
        catch
        {
            // keep the OS default
        }
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
            File.WriteAllText(StateFile, JsonSerializer.Serialize(state));
        }
        catch
        {
            // best effort
        }
    }

    private void BringToFront()
    {
        try
        {
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            // Force to top of the z-order so the main window can't cover it on open,
            // then drop TOPMOST shortly after so it behaves like a normal window.
            SetWindowPos(hwnd, HWND_TOPMOST, 0, 0, 0, 0, SwpNoMove | SwpNoSize | SwpShowWindow);
            SetForegroundWindow(hwnd);
            _ = ClearTopmostAfterDelayAsync(hwnd);
        }
        catch
        {
            // best effort
        }
    }

    private static async Task ClearTopmostAfterDelayAsync(IntPtr hwnd)
    {
        await Task.Delay(1500);
        try
        {
            SetWindowPos(hwnd, HWND_NOTOPMOST, 0, 0, 0, 0, SwpNoMove | SwpNoSize);
        }
        catch
        {
            // best effort
        }
    }

    private static readonly IntPtr HWND_TOPMOST = (IntPtr)(-1);
    private static readonly IntPtr HWND_NOTOPMOST = (IntPtr)(-2);
    private const uint SwpNoMove = 0x0002;
    private const uint SwpNoSize = 0x0001;
    private const uint SwpShowWindow = 0x0040;

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint flags);

    private sealed class WindowState
    {
        public int X { get; set; }
        public int Y { get; set; }
        public int Width { get; set; }
        public int Height { get; set; }
        public bool IsMaximized { get; set; }

        [JsonIgnore]
        public bool IsDefault => Width == 0 && Height == 0;
    }
}
