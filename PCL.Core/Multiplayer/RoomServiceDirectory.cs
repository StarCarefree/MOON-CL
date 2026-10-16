using System;
using System.Net;
using System.Net.Http;

namespace PCL.Core.Multiplayer;

/// <summary>Publisher-owned default. The endpoint must never be used as a player-facing label.</summary>
public static class RoomServiceDirectory
{
    private const string BuiltInEndpoint = "http://47.100.247.184:6664/";
    public static string OfficialUrl => BuiltInEndpoint;
    public const string OfficialLabel = "官方中控";
    public static bool OfficialUsesPlaintext => new Uri(OfficialUrl).Scheme == "http";
    public static bool IsOfficial(Uri uri) => string.Equals(uri.AbsoluteUri.TrimEnd('/'), OfficialUrl.TrimEnd('/'), StringComparison.OrdinalIgnoreCase);
    public static string FriendlyError(Exception exception, bool official)
    {
        if (exception is OperationCanceledException) return "操作超时或已取消，请稍后重试。";
        if (exception is RoomApiException api) return api.Status switch
        {
            HttpStatusCode.NotFound => "房间不存在、已解散或已过期。",
            HttpStatusCode.Unauthorized => "房间身份已失效，请重新加入。",
            HttpStatusCode.Forbidden => "密码不正确或你无权执行此操作。",
            HttpStatusCode.Conflict => "房间已满或请求存在冲突。",
            (HttpStatusCode)429 => "请求过于频繁，请稍后重试。",
            HttpStatusCode.BadRequest => "中控拒绝了请求，请检查线路、头像及房间信息。",
            _ => "中控暂时不可用，请稍后重试。"
        };
        // Transport/socket exception messages can contain the destination. Never expose the built-in endpoint.
        if (exception is HttpRequestException) return official ? "官方中控暂未就绪或无法连接；可稍后重试或切换自定义中控。" : "无法连接自定义中控，请检查地址、证书及服务部署。";
        var text = exception.Message;
        var uri = new Uri(OfficialUrl);
        text = text.Replace(OfficialUrl, OfficialLabel, StringComparison.OrdinalIgnoreCase)
            .Replace(uri.Authority, OfficialLabel, StringComparison.OrdinalIgnoreCase).Replace(uri.Host, OfficialLabel, StringComparison.OrdinalIgnoreCase);
        return text.Length <= 600 ? text : "联机操作失败，请检查配置和网络。";
    }
}
