using System.Windows;
using System.Windows.Media;

namespace PCL.Core.UI.Theme;

/// <summary>Shared charcoal/gold palette for the native launcher and its supplementary browser surface.</summary>
internal static class UnifiedDesktopPalette
{
    public static void ApplyNeutral(ResourceDictionary resources)
    {
        Set(resources, "Gray1", "#ECEDEF");
        Set(resources, "Gray2", "#CCD0D5");
        Set(resources, "Gray3", "#A3A8B0");
        Set(resources, "Gray4", "#737982");
        Set(resources, "Gray5", "#45494F");
        Set(resources, "Gray6", "#2E3136");
        Set(resources, "Gray7", "#24262A");
        Set(resources, "Gray8", "#1B1D21");
        Set(resources, "Background", "#111315");
        Set(resources, "TransparentBackground", "#1B1D21");
        Set(resources, "HalfWhite", "#222428");
        Set(resources, "SemiWhite", "#24262A");
        Set(resources, "White", "#1B1D21");
        Set(resources, "ToolTip", "#24262A");
        Set(resources, "Transparent", "#00111315");
        Set(resources, "RedBack", "#322322");
        Set(resources, "Memory", "#A3A8B0");
        Set(resources, "ControlBorder", "#3A3D42");
        Set(resources, "Shadow", "#000000");
    }

    public static void ApplyAccent(ResourceDictionary resources)
    {
        Set(resources, "1", "#ECEDEF");
        Set(resources, "2", "#E0A24A");
        Set(resources, "3", "#FFB942");
        Set(resources, "4", "#F0B15A");
        Set(resources, "5", "#AC7836");
        Set(resources, "6", "#51402B");
        Set(resources, "7", "#332C22");
        Set(resources, "8", "#211E19");
        Set(resources, "Bg0", "#292721");
        Set(resources, "Bg1", "#272A2E");
        Set(resources, "SemiTransparent", "#01111315");
    }

    private static void Set(ResourceDictionary resources, string suffix, string hex)
    {
        var color = (Color)ColorConverter.ConvertFromString(hex);
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        resources["ColorObject" + suffix] = color;
        resources["ColorBrush" + suffix] = brush;
    }
}
