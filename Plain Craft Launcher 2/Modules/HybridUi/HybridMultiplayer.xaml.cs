using System;
using System.Windows;
using System.Windows.Controls;

namespace PCL;

public partial class HybridMultiplayer : UserControl
{
    private readonly Func<FrameworkElement> _createServers;
    internal HybridRooms Rooms => (HybridRooms)RoomHost.Content;

    public HybridMultiplayer() : this(new HybridRooms(), () => new VisitedServerListView()) { }

    // Keep the tab host independently testable, without starting room networking.
    internal HybridMultiplayer(FrameworkElement rooms, Func<FrameworkElement> createServers)
    {
        InitializeComponent();
        RoomHost.Content = rooms;
        _createServers = createServers;
    }

    internal void SelectRooms() => SelectTab(false);

    private void RoomsTab_Click(object sender, RoutedEventArgs e)
    {
        if (RoomHost is not null && RoomHost.Visibility != Visibility.Visible) SelectRooms();
    }
    private void ServersTab_Click(object sender, RoutedEventArgs e)
    {
        if (ServerHost is not null && ServerHost.Visibility != Visibility.Visible) SelectTab(true);
    }

    private void SelectTab(bool servers)
    {
        if (servers) ServerHost.Content ??= _createServers();
        RoomHost.Visibility = servers ? Visibility.Collapsed : Visibility.Visible;
        ServerHost.Visibility = servers ? Visibility.Visible : Visibility.Collapsed;
        RoomsTab.IsChecked = !servers;
        ServersTab.IsChecked = servers;
        // Hide, don't dispose: active rooms keep their connection and state across tabs.
        if (!servers && IsVisible && RoomHost.Content is HybridRooms rooms) rooms.PlayPageSwitch();
    }
}
