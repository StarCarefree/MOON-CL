using System.IO;
using System.Threading;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using PCL.Core.Minecraft.Launcher;

namespace PCL;

internal sealed partial class WebUiController
{
    private PlatformPackClient? _platformCatalog;
    private bool _openingPlatformDownload;

    internal static ModComp.CompType ResourceCompType(string resourceType) => BrowseResourceTypes.Validate(resourceType) switch
    {
        "datapack" => ModComp.CompType.DataPack,
        "world" => ModComp.CompType.World,
        "shader" => ModComp.CompType.Shader,
        "resourcepack" => ModComp.CompType.ResourcePack,
        "plugin" => ModComp.CompType.Plugin,
        _ => ModComp.CompType.ModPack
    };

    private async Task HandlePlatformAsync(CoreWebView2 core, JsonObject message, FormMain form)
    {
        var operation = ReadString(message, "operation") ?? "";
        if (operation is not ("list" or "detail" or "download" or "categories" or "translate")) return;
        var source = ReadString(message, "source") ?? "";
        var requestId = ReadString(message, "requestId") ?? "";
        var id = ReadString(message, "id") ?? "";
        var page = message["page"] is JsonValue pageNode && pageNode.TryGetValue<int>(out var number) ? number : 1;
        var resourceType = ReadString(message, "resourceType") ?? "modpack";
        var category = ReadString(message, "category") ?? "";
        var generation = _generation;
        bool Current() => !_disposed && generation == _generation && _view?.CoreWebView2 == core;
        void Send(JsonObject reply) => PostUi(() =>
        {
            if (!Current()) return;
            reply["source"] = source; reply["requestId"] = requestId; reply["id"] = id; reply["page"] = page;
            reply["resourceType"] = resourceType; reply["category"] = category;
            core.PostWebMessageAsJson(reply.ToJsonString());
        });
        try
        {
            PlatformPackCatalog.ValidateSource(source);
            BrowseResourceTypes.RequireSupported(source, resourceType);
            if (requestId.Length is < 1 or > 128) throw new ArgumentException("缺少有效的浏览请求标识。");
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(_mcmodLifetime.Token);
            deadline.CancelAfter(TimeSpan.FromSeconds(55));
            var token = deadline.Token;
            _platformCatalog ??= new PlatformPackClient((uri, ct) =>
                Task.Run(() => ModDownload.DlModRequest(uri.AbsoluteUri), ct).WaitAsync(ct));
            if (operation == "categories")
            {
                var items = await _platformCatalog.GetCategoriesAsync(source, resourceType, token);
                Send(new JsonObject { ["type"] = "platformCategories", ["items"] = JsonSerializer.SerializeToNode(items, McmodJson) });
                return;
            }
            if (operation == "translate")
            {
                if (source is not ("curseforge" or "modrinth")) throw new ArgumentException("只翻译 CurseForge 与 Modrinth 条目。");
                PlatformPackCatalog.ValidateId(source, id);
                var address = $"https://mod.mcimirror.top/translate/{source}/{id}";
                var json = await Task.Run(() => ModDownload.DlModRequest(address), token).WaitAsync(token);
                if (json.Length > 200_000) throw new InvalidDataException("翻译结果超过大小上限。");
                var root = JsonNode.Parse(json) as JsonObject ?? throw new InvalidDataException("翻译结果不是 JSON 对象。");
                var text = root["translated"]?.ToString()?.Trim() ?? "";
                if (text.Length > 8000) text = text[..8000];
                Send(new JsonObject { ["type"] = "platformTranslation", ["text"] = text });
                return;
            }
            if (operation == "list")
            {
                var result = await _platformCatalog.GetPageAsync(source, page,
                    ReadString(message, "query") ?? "", ReadString(message, "minecraft") ?? "",
                    ReadString(message, "loader") ?? "", ReadString(message, "sort") ?? "relevance", token, resourceType, category);
                Send(new JsonObject { ["type"] = "platformList", ["totalPages"] = result.TotalPages,
                    ["items"] = JsonSerializer.SerializeToNode(result.Items, McmodJson) });
                return;
            }
            var pack = await _platformCatalog.GetDetailAsync(source, id, token, resourceType);
            if (operation == "detail")
            {
                Send(new JsonObject { ["type"] = "platformDetail", ["item"] = JsonSerializer.SerializeToNode(pack, McmodJson) });
                return;
            }
            if (_openingPlatformDownload) throw new InvalidOperationException("正在打开资源版本列表，请稍候。");
            _openingPlatformDownload = true;
            try
            {
                // Preserve source identity even if a Modrinth ID happens to contain digits only.
                var projects = await (source == "curseforge"
                    ? ModComp.CompRequest.GetListByIdsFromCurseforgeAsync([pack.ProjectId])
                    : ModComp.CompRequest.GetListByIdsFromModrinthAsync([pack.ProjectId])).WaitAsync(token);
                await SwitchToUiAsync(form.Dispatcher);
                var project = projects.FirstOrDefault(p => p.Id == pack.ProjectId && p.Type == ResourceCompType(resourceType) && p.FromCurseForge == (source == "curseforge"));
                if (project is null) throw new InvalidDataException("平台未返回对应资源的下载信息，请打开项目网站查看。");
                if (_disposed || !Current() || !IsPresented || form.PanMsgBackground.Visibility == System.Windows.Visibility.Visible) return;
                if (resourceType == "plugin")
                {
                    var selected = ServerPluginDownloads.Pick(pack.MinecraftVersions);
                    if (selected is null)
                    {
                        Send(new JsonObject { ["type"] = "platformError", ["operation"] = "download", ["message"] = "未选择插件服务器，没有打开版本列表。" });
                        return;
                    }
                    ServerPluginDownloads.PreferredServerId = selected.Id;
                    Hide();
                    form.PageChange(new FormMain.PageStackData
                    {
                        page = FormMain.PageType.CompDetail,
                        additional = (project, new List<string>(), selected.MinecraftVersion, ModComp.CompLoaderType.Any, ModComp.CompType.Plugin, PCL.Core.Minecraft.Server.ServerPluginTargets.PluginsDirectory(ServerUi.Manager, selected))
                    });
                    return;
                }
                Hide();
                form.PageChange(new FormMain.PageStackData
                {
                    page = FormMain.PageType.CompDetail,
                    additional = (project, new List<string>(), string.Empty, ModComp.CompLoaderType.Any, ResourceCompType(resourceType), null!)
                });
            }
            finally { _openingPlatformDownload = false; }
        }
        catch (Exception ex)
        {
            if (_disposed) return;
            ModBase.Log(ex, "获取平台整合包信息失败", ModBase.LogLevel.Debug);
            Send(new JsonObject { ["type"] = "platformError", ["operation"] = operation,
                ["message"] = ex is OperationCanceledException ? "平台请求超时，请稍后重试或打开项目网站。" : ex.Message });
        }
    }
}
