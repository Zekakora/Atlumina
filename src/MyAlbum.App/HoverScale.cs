using System.Numerics;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Hosting;

namespace MyAlbum_App;

/// <summary>
/// Shared spring-scale hover animation for tool-window thumbnails, matching the home
/// grid's ~2% spring growth (GPU smoothed, constant regardless of tile size).
/// </summary>
public static class HoverScale
{
    public static void PointerEntered(UIElement element)
    {
        if (element is FrameworkElement el && el.Width > 0)
        {
            Animate(el, (float)(1 + Math.Min(0.025, 2.0 / el.Width)));
        }
    }

    public static void PointerExited(UIElement? element) => Animate(element, 1f);

    private static void Animate(UIElement? element, float target)
    {
        if (element is null)
        {
            return;
        }
        var visual = ElementCompositionPreview.GetElementVisual(element);
        var spring = visual.Compositor.CreateSpringVector3Animation();
        spring.Target = "Scale";
        spring.InitialValue = new Vector3(visual.Scale.X, visual.Scale.Y, 1f);
        spring.FinalValue = new Vector3(target, target, 1f);
        spring.DampingRatio = 0.65f;
        spring.Period = TimeSpan.FromMilliseconds(80);
        visual.StartAnimation("Scale", spring);
    }
}
