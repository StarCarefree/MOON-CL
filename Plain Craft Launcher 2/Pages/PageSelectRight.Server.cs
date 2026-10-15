using System.Windows;

namespace PCL;

public partial class PageSelectRight
{
    private void ClientList_Click(object sender, RoutedEventArgs e)
    {
        PanClientContent.Visibility = Visibility.Visible;
        PanServers.Visibility = Visibility.Collapsed;
        BtnServerList.Text = "切换至服务端管理列表";
        BtnClientList.Text = "客户端版本";
    }

    private void ServerList_Click(object sender, RoutedEventArgs e)
    {
        ServerUi.DownloadSource = () => PCL.Core.App.Config.Download.FileSource;
        PanServers.Content ??= new ServerManagementView();
        PanClientContent.Visibility = Visibility.Collapsed;
        PanServers.Visibility = Visibility.Visible;
        BtnServerList.Text = "服务端管理";
        BtnClientList.Text = "切换至客户端版本列表";
    }
}
