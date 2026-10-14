using System.Windows;
using System.Windows.Media;

namespace PCL;

/// <summary>PCL CE page motion (opacity, slide, ease-out-back) for the hybrid shell.</summary>
internal static class HybridMotion
{
    public static void Rise(params FrameworkElement?[] elements)
    {
        var animations = new List<ModAnimation.AniData>();
        var delay = 0;
        foreach (var element in elements)
        {
            if (element is null || element.Visibility != Visibility.Visible) continue;
            element.Opacity = 0;
            element.RenderTransform = new TranslateTransform(0, -16);
            animations.Add(ModAnimation.AaOpacity(element, 1, 110, delay,
                new ModAnimation.AniEaseOutFluent(ModAnimation.AniEasePower.Weak)));
            animations.Add(ModAnimation.AaTranslateY(element, 5, 250, delay, new ModAnimation.AniEaseOutFluent()));
            animations.Add(ModAnimation.AaTranslateY(element, 11, 360, delay, new ModAnimation.AniEaseOutBack()));
            delay += 28;
        }
        if (animations.Count > 0)
            ModAnimation.AniStart(animations, "Hybrid Rise");
    }

    public static void SlideIn(params FrameworkElement?[] elements)
    {
        var animations = new List<ModAnimation.AniData>();
        var delay = 0;
        foreach (var element in elements)
        {
            if (element is null || element.Visibility != Visibility.Visible) continue;
            element.Opacity = 0;
            element.RenderTransform = new TranslateTransform(-22, 0);
            animations.Add(ModAnimation.AaOpacity(element, 1, 100, delay,
                new ModAnimation.AniEaseOutFluent(ModAnimation.AniEasePower.Weak)));
            animations.Add(ModAnimation.AaTranslateX(element, 4, 180, delay, new ModAnimation.AniEaseOutFluent()));
            animations.Add(ModAnimation.AaTranslateX(element, 18, 300, delay,
                new ModAnimation.AniEaseOutBack(ModAnimation.AniEasePower.Weak)));
            delay += 26;
        }
        if (animations.Count > 0)
            ModAnimation.AniStart(animations, "Hybrid Slide");
    }

    public static void Pop(FrameworkElement card, FrameworkElement backdrop)
    {
        card.RenderTransformOrigin = new Point(0.5, 0.5);
        card.RenderTransform = new ScaleTransform(0.92, 0.92);
        card.Opacity = 0;
        backdrop.Opacity = 0;
        ModAnimation.AniStart(new[]
        {
            ModAnimation.AaOpacity(backdrop, 1, 160),
            ModAnimation.AaOpacity(card, 1, 180, 30),
            ModAnimation.AaScaleTransform(card, 0.08, 420, 30,
                new ModAnimation.AniEaseOutBack(ModAnimation.AniEasePower.Weak))
        }, "Hybrid Pop");
    }

    public static void Dismiss(FrameworkElement card, FrameworkElement backdrop, Action done)
    {
        card.RenderTransformOrigin = new Point(0.5, 0.5);
        if (card.RenderTransform is not ScaleTransform)
            card.RenderTransform = new ScaleTransform(1, 1);
        var scale = ((ScaleTransform)card.RenderTransform).ScaleX;
        ModAnimation.AniStart(new[]
        {
            ModAnimation.AaScaleTransform(card, 0.94 - scale, 140,
                ease: new ModAnimation.AniEaseInFluent(ModAnimation.AniEasePower.Weak)),
            ModAnimation.AaOpacity(card, -card.Opacity, 120),
            ModAnimation.AaOpacity(backdrop, -backdrop.Opacity, 140),
            ModAnimation.AaCode(() => done(), after: true)
        }, "Hybrid Dismiss");
    }

    public static void Press(FrameworkElement element)
    {
        element.RenderTransformOrigin = new Point(0.5, 0.5);
        if (element.RenderTransform is not ScaleTransform)
            element.RenderTransform = new ScaleTransform(1, 1);
        var scale = ((ScaleTransform)element.RenderTransform).ScaleX;
        ModAnimation.AniStart(new[]
        {
            ModAnimation.AaScaleTransform(element, 0.955 - scale, 80,
                ease: new ModAnimation.AniEaseOutFluent(ModAnimation.AniEasePower.ExtraStrong)),
            ModAnimation.AaScaleTransform(element, 0.045, 280, 70,
                new ModAnimation.AniEaseOutBack(ModAnimation.AniEasePower.Weak))
        }, "Hybrid Press " + element.GetHashCode());
    }
}
