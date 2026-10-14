using System.Windows;
using System.Windows.Input;

namespace PCL;

// Explicit adapters reuse the original event paths, including preview/cancellation and custom homepage events.
// The browser never chooses a CLR method name or accesses arbitrary object properties.
internal static class WebControlMouse
{
    public static MouseButtonEventArgs Click(UIElement source) => new(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left)
    {
        RoutedEvent = Mouse.MouseUpEvent,
        Source = source
    };
}

public partial class MyButton
{
    internal void WebClick()
    {
        var e = WebControlMouse.Click(this);
        isMouseDown = true;
        try { Button_MouseUp(this, e); }
        finally { isMouseDown = false; }
    }
}

public partial class MyListItem
{
    internal void WebClick()
    {
        var e = WebControlMouse.Click(this);
        isMouseDown = true;
        try { Button_MouseUp(this, e); }
        finally { isMouseDown = false; }
    }
}

public partial class MyRadioButton
{
    internal void WebClick()
    {
        isMouseDown = true;
        try { Radiobox_MouseUp(); }
        finally { isMouseDown = false; }
    }
}

public partial class MyIconButton
{
    internal void WebClick() => Click?.Invoke(this, EventArgs.Empty);
}

public partial class MyIconTextButton
{
    internal void WebClick()
    {
        Click?.Invoke(this, new ModBase.RouteEventArgs(true));
        ModMain.RaiseCustomEvent(this);
    }
}

public partial class MyExtraButton
{
    internal void WebClick(bool right = false)
    {
        var e = WebControlMouse.Click(this);
        if (right) RightClick?.Invoke(this, e);
        else Click?.Invoke(this, e);
    }
}

public partial class MyExtraTextButton
{
    internal void WebClick() => Click?.Invoke(this, WebControlMouse.Click(this));
}

public partial class MyLoading
{
    internal void WebClick() => Click?.Invoke(this, WebControlMouse.Click(this));
}

public partial class MySearchBox
{
    internal void WebSearch()
    {
        Search?.Invoke(BtnSearch, WebControlMouse.Click(BtnSearch));
        ModMain.RaiseCustomEvent(this);
    }
}

public partial class MyCompItem
{
    internal void WebClick()
    {
        if (CanInteraction) Click?.Invoke(this, WebControlMouse.Click(this));
    }
}

public partial class MyLocalCompItem
{
    internal void WebClick()
    {
        var e = WebControlMouse.Click(this);
        Click?.Invoke(this, e);
        if (!e.Handled) Checked = !Checked;
    }
}
