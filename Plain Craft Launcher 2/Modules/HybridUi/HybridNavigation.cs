using System.Windows;

namespace PCL;

/// <summary>Selection state for the native shell, independent of the legacy title-tab controls.</summary>
public static class HybridNavigation
{
    public static readonly DependencyProperty IsActiveProperty = DependencyProperty.RegisterAttached(
        "IsActive", typeof(bool), typeof(HybridNavigation), new FrameworkPropertyMetadata(false));

    public static bool GetIsActive(DependencyObject element) => (bool)element.GetValue(IsActiveProperty);
    public static void SetIsActive(DependencyObject element, bool value) => element.SetValue(IsActiveProperty, value);
}
