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
    private bool _openingMcmodDownload;
    private static readonly JsonSerializerOptions McmodJson = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private async Task HandleMcmodAsync(CoreWebView2 core, JsonObject message, FormMain form)
    {
        var operation = ReadString(message, "operation") ?? "";
        if (operation is not ("list" or "detail" or "download")) return;
        var page = message["page"] is JsonValue p && p.TryGetValue<int>(out var pageValue) ? pageValue : 1;
        var id = message["id"] is JsonValue i && i.TryGetValue<int>(out var idValue) ? idValue : 0;
        var minecraft = ReadString(message, "minecraft") ?? "";
        var loader = ReadString(message, "loader") ?? "";
        var category = message["category"] is JsonValue c && c.TryGetValue<int>(out var categoryValue) ? categoryValue : 0;
        var requestId = ReadString(message, "requestId") ?? "";
        var generation = _generation;
        bool Current() => !_disposed && generation == _generation && _view?.CoreWebView2 == core;
        void Send(JsonObject reply) { if (Current()) core.PostWebMessageAsJson(reply.ToJsonString()); }
        try
        {
            _mcmodCatalog ??= new McmodCatalog(_mcmodHttp);
            if (operation == "list")
            {
                var result = await _mcmodCatalog.GetPageAsync(page, _mcmodLifetime.Token, minecraft, loader, category);
                Send(new JsonObject { ["type"] = "mcmodList", ["page"] = result.Page, ["totalPages"] = result.TotalPages,
                    ["requestId"] = requestId, ["items"] = JsonSerializer.SerializeToNode(result.Items, McmodJson) });
                return;
            }
            var detail = await _mcmodCatalog.GetDetailAsync(id, _mcmodLifetime.Token);
            if (operation == "detail")
            {
                Send(new JsonObject { ["type"] = "mcmodDetail", ["item"] = JsonSerializer.SerializeToNode(detail, McmodJson) });
                return;
            }
            if (_openingMcmodDownload) throw new InvalidOperationException("正在获取 CurseForge 下载信息，请稍候。");
            if (!McmodCatalog.IsCurseForgeModpack(detail.CurseForgeUrl))
                throw new InvalidDataException("此整合包没有可识别的 CurseForge 项目，请使用作者提供的链接手动下载后导入。");
            _openingMcmodDownload = true;
            try
            {
                // Resolve only the server-parsed project, never a URL supplied by the browser message.
                var projectId = await Task.Run(() => ModComp.ResolveLinkToProjectId(detail.CurseForgeUrl), _mcmodLifetime.Token);
                if (string.IsNullOrEmpty(projectId)) throw new InvalidDataException("CurseForge 未返回对应整合包项目。可打开作者链接手动下载。");
                var projects = await ModComp.CompRequest.GetCompProjectsByIdsAsync([projectId]);
                var project = projects.FirstOrDefault(p => p.Type == ModComp.CompType.ModPack && p.FromCurseForge);
                if (project is null) throw new InvalidDataException("CurseForge 对应项目不是可用的整合包。可打开作者链接手动下载。");
                if (!Current() || !IsPresented || form.PanMsgBackground.Visibility == System.Windows.Visibility.Visible) return;
                // Native detail retrieves the actual version/file list and uses the existing download/install path.
                Hide();
                form.PageChange(new FormMain.PageStackData
                {
                    page = FormMain.PageType.CompDetail,
                    additional = (project, new List<string>(), string.Empty, ModComp.CompLoaderType.Any, ModComp.CompType.ModPack, null)
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
}
