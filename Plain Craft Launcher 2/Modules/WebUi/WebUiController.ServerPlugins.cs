namespace PCL;

internal sealed partial class WebUiController
{
    private string? _pendingPluginVersion;

    internal async Task OpenServerPluginsAsync(FormMain form, string minecraftVersion)
    {
        _pendingPluginVersion = minecraftVersion;
        await AttachAsync(form);
        SendPendingPluginBrowse();
    }

    private void SendPendingPluginBrowse()
    {
        if (_disposed || !_ready || _pendingPluginVersion is not { } version || _view?.CoreWebView2 is not { } core) return;
        core.PostWebMessageAsJson(new JsonObject { ["type"] = "browsePlugins", ["minecraft"] = version }.ToJsonString());
        _pendingPluginVersion = null;
    }
}
