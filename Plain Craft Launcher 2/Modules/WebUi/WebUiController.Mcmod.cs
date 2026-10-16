using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using Microsoft.Web.WebView2.Core;
using PCL.Core.Minecraft.Launcher;

namespace PCL;

internal sealed partial class WebUiController
{
    private readonly CancellationTokenSource _mcmodLifetime = new();
    private readonly HttpClient _mcmodHttp = new(new HttpClientHandler
    {
        AutomaticDecompression = DecompressionMethods.All,
        AllowAutoRedirect = false
    }) { Timeout = TimeSpan.FromSeconds(30) };
    private McmodCatalog? _mcmodCatalog;
    private readonly HttpClient _quarkListHttp = new(new HttpClientHandler { UseCookies = false }) { Timeout = TimeSpan.FromSeconds(30) };
    private bool _openingMcmodDownload;
    private static readonly JsonSerializerOptions McmodJson = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private async Task HandleMcmodAsync(CoreWebView2 core, JsonObject message, FormMain form)
    {
        var operation = ReadString(message, "operation") ?? "";
        if (operation is not ("list" or "detail" or "download" or "links" or "categories" or "quarkList" or "quarkDownload")) return;
        var page = message["page"] is JsonValue p && p.TryGetValue<int>(out var pageValue) ? pageValue : 1;
        var id = message["id"] is JsonValue i && i.TryGetValue<int>(out var idValue) ? idValue : 0;
        var minecraft = ReadString(message, "minecraft") ?? "";
        var loader = ReadString(message, "loader") ?? "";
        var category = message["category"] is JsonValue c && c.TryGetValue<int>(out var categoryValue) ? categoryValue : 0;
        var requestId = ReadString(message, "requestId") ?? "";
        var resourceType = ReadString(message, "resourceType") ?? "modpack";
        var generation = _generation;
        bool Current() => !_disposed && generation == _generation && _view?.CoreWebView2 == core;
        void Send(JsonObject reply)
        {
            if (!Current()) return;
            reply["resourceType"] = resourceType; reply["requestId"] = requestId;
            core.PostWebMessageAsJson(reply.ToJsonString());
        }
        try
        {
            BrowseResourceTypes.RequireSupported("mcmod", resourceType);
            _mcmodCatalog ??= new McmodCatalog(_mcmodHttp);
            if (operation == "categories")
            {
                var items = await _mcmodCatalog.GetCategoriesAsync(resourceType, _mcmodLifetime.Token);
                Send(new JsonObject { ["type"] = "mcmodCategories", ["items"] = JsonSerializer.SerializeToNode(items, McmodJson) });
                return;
            }
            if (operation == "list")
            {
                var result = await _mcmodCatalog.GetPageAsync(page, _mcmodLifetime.Token, minecraft, loader, category, resourceType);
                Send(new JsonObject { ["type"] = "mcmodList", ["page"] = result.Page, ["totalPages"] = result.TotalPages,
                    ["requestId"] = requestId, ["categories"] = JsonSerializer.SerializeToNode(result.Categories, McmodJson), ["items"] = JsonSerializer.SerializeToNode(result.Items, McmodJson) });
                return;
            }
            var detail = await _mcmodCatalog.GetDetailAsync(id, _mcmodLifetime.Token, resourceType);
            if (operation is "detail" or "links")
            {
                Send(new JsonObject { ["type"] = operation == "links" ? "mcmodLinks" : "mcmodDetail", ["id"] = id, ["item"] = JsonSerializer.SerializeToNode(detail, McmodJson) });
                return;
            }
            if (operation is "quarkList" or "quarkDownload")
            {
                await HandleQuarkShareAsync(message, operation, id, resourceType, detail, Current, Send);
                return;
            }
            if (_openingMcmodDownload) throw new InvalidOperationException("正在获取关联项目的下载信息，请稍候。");
            var fromCurseForge = McmodCatalog.IsCurseForgeResource(detail.CurseForgeUrl, resourceType);
            var fromModrinth = McmodCatalog.IsModrinthResource(detail.ModrinthUrl, resourceType);
            if (!fromCurseForge && !fromModrinth) throw new InvalidDataException("此资源没有对应类型的 CF / Modrinth 项目，请使用作者链接手动下载。");
            _openingMcmodDownload = true;
            try
            {
                // Resolve only the server-parsed project, never a URL supplied by the browser message.
                var url = fromCurseForge ? detail.CurseForgeUrl : detail.ModrinthUrl;
                var projectId = await Task.Run(() => ModComp.ResolveLinkToProjectId(url), _mcmodLifetime.Token);
                if (string.IsNullOrEmpty(projectId)) throw new InvalidDataException("关联平台未返回项目，可打开作者链接手动下载。");
                var projects = await (fromCurseForge ? ModComp.CompRequest.GetListByIdsFromCurseforgeAsync([projectId]) : ModComp.CompRequest.GetListByIdsFromModrinthAsync([projectId]));
                var project = projects.FirstOrDefault(p => p.Type == ResourceCompType(resourceType) && p.FromCurseForge == fromCurseForge);
                if (project is null) throw new InvalidDataException("关联平台项目类型不符或不可用，可打开作者链接手动下载。");
                if (!Current() || !IsPresented || form.PanMsgBackground.Visibility == System.Windows.Visibility.Visible) return;
                // Native detail retrieves the actual version/file list and uses the existing download/install path.
                Hide();
                form.PageChange(new FormMain.PageStackData
                {
                    page = FormMain.PageType.CompDetail,
                    additional = (project, new List<string>(), string.Empty, ModComp.CompLoaderType.Any, ResourceCompType(resourceType), null!)
                });
            }
            finally { _openingMcmodDownload = false; }
        }
        catch (Exception ex)
        {
            if (_disposed) return;
            ModBase.Log(ex, "获取 MC 百科整合包信息失败", ModBase.LogLevel.Debug);
            Send(new JsonObject { ["type"] = "mcmodError", ["operation"] = operation, ["page"] = page, ["id"] = id,
                ["requestId"] = requestId,
                ["message"] = ex is OperationCanceledException ? "百科请求超时，请重试或打开百科页面。" : ex.Message });
        }
    }

    private async Task HandleQuarkShareAsync(JsonObject message, string operation, int id, string resourceType, McmodPack detail, Func<bool> current, Action<JsonObject> send)
    {
        if (resourceType != "modpack") throw new InvalidDataException("只有整合包可以在启动器里选择夸克版本。没有转存。");
        var shareId = ReadString(message, "shareId") ?? "";
        var shareUrl = QuarkClientLaunch.MatchShare(detail.Links.Select(link => link.Url), shareId);
        if (shareUrl is null) throw new InvalidDataException("这个整合包没有对应的夸克分享，没有读取网盘。");
        var path = ReadString(message, "path") ?? "";
        if (path.Length > 4096) throw new InvalidDataException("夸克目录无效，没有读取网盘。");
        if (operation == "quarkList")
        {
            var listing = await new QuarkPackClient(_quarkListHttp).ListAsync(shareUrl, path, _mcmodLifetime.Token);
            if (!current()) return;
            send(new JsonObject
            {
                ["type"] = "quarkVersions", ["id"] = id, ["shareId"] = shareId, ["path"] = listing.Path,
                ["truncated"] = listing.Truncated, ["entries"] = JsonSerializer.SerializeToNode(listing.Entries, McmodJson)
            });
            return;
        }
        if (_library is null) throw new InvalidOperationException("启动器尚未就绪，没有转存。");
        var started = await _library.InstallBrowseQuarkAsync(id, detail.Name, shareUrl, path);
        if (!current()) return;
        send(new JsonObject { ["type"] = "quarkDownload", ["id"] = id, ["shareId"] = shareId, ["accepted"] = started });
        Refresh(force: true);
    }
}
