using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace PCL;

// WebView2's native renderer can own the keyboard without a WPF IsKeyboardFocusWithin event.
internal static class HybridBrowserFocus
{
    [StructLayout(LayoutKind.Sequential)]
    private struct GuiThreadInfo
    {
        public uint Size, Flags;
        public IntPtr Active, Focus, Capture, MenuOwner, MoveSize, Caret;
        public int Left, Top, Right, Bottom;
    }
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetGUIThreadInfo(uint threadId, ref GuiThreadInfo information);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsChild(IntPtr parent, IntPtr child);

    public static bool HasNativeChildFocus(Window owner)
    {
        var info = new GuiThreadInfo { Size = (uint)Marshal.SizeOf<GuiThreadInfo>() };
        var window = new WindowInteropHelper(owner).Handle;
        return window != IntPtr.Zero && GetGUIThreadInfo(0, ref info) && info.Focus != IntPtr.Zero &&
            info.Focus != window && IsChild(window, info.Focus);
    }
}
