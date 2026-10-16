using System.Windows;
using System.Windows.Media;

namespace PCL;

/// <summary>MOONCL page motion (opacity, slide, ease-out-back) for the hybrid shell.</summary>
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
            ModAnimation.AniStart(animations, "Hybrid Rise " + animations[0].GetHashCode());
    }

    /// <summary>Staggered rise used by settings, download and tools right pages.</summary>
    public static void Enter(string group, params FrameworkElement?[] elements)
    {
        var animations = new List<ModAnimation.AniData>();
        var delay = 0;
        foreach (var element in elements)
        {
            if (element is null || element.Visibility != Visibility.Visible) continue;
            element.IsHitTestVisible = true;
            element.Opacity = 0;
            element.RenderTransform = new TranslateTransform(0, -16);
            animations.Add(ModAnimation.AaOpacity(element, 1, 100, delay,
                new ModAnimation.AniEaseOutFluent(ModAnimation.AniEasePower.Weak)));
            animations.Add(ModAnimation.AaTranslateY(element, 5, 250, delay, new ModAnimation.AniEaseOutFluent()));
            animations.Add(ModAnimation.AaTranslateY(element, 11, 350, delay, new ModAnimation.AniEaseOutBack()));
            delay += 25;
        }
        if (animations.Count > 0)
            ModAnimation.AniStart(animations, group, true);
    }

    /// <summary>Staggered exit used when leaving a settings, download or tools page.</summary>
    public static void Leave(string group, params FrameworkElement?[] elements)
    {
        var animations = new List<ModAnimation.AniData>();
        var delay = 0;
        foreach (var element in elements)
        {
            if (element is null || element.Visibility != Visibility.Visible) continue;
            element.IsHitTestVisible = false;
            animations.Add(ModAnimation.AaOpacity(element, -1, 70, delay));
            animations.Add(ModAnimation.AaTranslateY(element, -6, 70, delay));
            delay += 15;
        }
        if (animations.Count > 0)
            ModAnimation.AniStart(animations, group);
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
            ModAnimation.AniStart(animations, "Hybrid Slide " + animations[0].GetHashCode());
    }
    public static void Swap(FrameworkElement element)
    {
        if (element.Visibility != Visibility.Visible) return;
        element.Opacity = 0;
        element.RenderTransform = new TranslateTransform(16, 0);
        ModAnimation.AniStart(new[]
        {
            ModAnimation.AaOpacity(element, 1, 140),
            ModAnimation.AaTranslateX(element, -16, 320, 0, new ModAnimation.AniEaseOutBack(ModAnimation.AniEasePower.Weak))
        }, "Hybrid Swap " + element.GetHashCode());
    }

    /// <summary>
    /// Version change: put the text back at rest and pulse opacity.
    /// Do not add a translate. List recycling and a cancelled animation group leave that offset in place.
    /// </summary>
    public static void Crossfade(FrameworkElement element)
    {
        if (element is null || element.Visibility != Visibility.Visible) return;
        element.RenderTransform = Transform.Identity;
        element.Opacity = Math.Max(element.Opacity, 0.55);
        ModAnimation.AniStart(new[]
        {
            ModAnimation.AaOpacity(element, 1 - element.Opacity, 160)
        }, "Hybrid Crossfade " + element.GetHashCode());
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

    /// <summary>
    /// MOONCL 列表项悬停：浮层从中心放大并淡入，离开时收回。底下的选中色不动。
    /// </summary>
    public static void Float(FrameworkElement surface, bool over, string name)
    {
        if (surface is null) return;
        surface.RenderTransformOrigin = new Point(0.5, 0.5);
        var current = surface.RenderTransform is ScaleTransform existing ? existing.ScaleX : over ? 0.8 : 1;
        surface.RenderTransform = new ScaleTransform(current, current);
        var targetScale = over ? 1d : 0.8d;
        var time = over ? 120 : 180;
        var ease = new ModAnimation.AniEaseOutFluent();
        ModAnimation.AniStart(new[]
        {
            ModAnimation.AaScaleTransform(surface, targetScale - current, (int)Math.Round(time * (over ? 1.6d : 1d)), ease: ease),
            ModAnimation.AaOpacity(surface, (over ? 1d : 0d) - surface.Opacity, time, ease: ease)
        }, name);
    }
}
