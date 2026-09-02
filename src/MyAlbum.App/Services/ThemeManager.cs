using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace MyAlbum_App.Services;

/// <summary>
/// Applies the 浅色 / 深色 / 跟随系统 theme to every open window. Windows register
/// themselves on construction; the current theme is re-applied to them immediately.
/// </summary>
public static class ThemeManager
{
    private static readonly HashSet<Window> Windows = new();
    private static ElementTheme _current = ElementTheme.Default;

    /// <summary>Tracks a window so theme changes reach it (new and already-open ones).</summary>
    public static void Register(Window window)
    {
        Windows.Add(window);
        window.Closed += (_, _) => Windows.Remove(window);
        ApplyTo(window);
    }

    public static void Apply(ElementTheme theme)
    {
        _current = theme;
        foreach (var window in Windows.ToList())
        {
            ApplyTo(window);
        }
    }

    /// <summary>Maps the persisted mode string ("system"/"light"/"dark") to an ElementTheme.</summary>
    public static ElementTheme MapMode(string mode) => mode switch
    {
        "light" => ElementTheme.Light,
        "dark" => ElementTheme.Dark,
        _ => ElementTheme.Default,
    };

    private static void ApplyTo(Window window)
    {
        if (window.Content is FrameworkElement root)
        {
            root.RequestedTheme = _current;
        }
    }
}
