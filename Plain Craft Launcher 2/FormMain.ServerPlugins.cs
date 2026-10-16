using PCL.Core.Minecraft.Server;

namespace PCL;

public partial class FormMain
{
    internal async Task OpenServerPluginsAsync(ServerProfile profile)
    {
        ServerPluginDownloads.PreferredServerId = profile.Id;
        await _webUi.OpenServerPluginsAsync(this, profile.MinecraftVersion);
        Activate();
    }
}
