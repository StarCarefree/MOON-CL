using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using PCL.Core.Link.McPing;
using PCL.Core.Logging;
using PCL.Core.Minecraft;
namespace PCL.Core.Minecraft.Launcher;

public sealed record BrowseServerSave(bool Updated);

/// <summary>Launcher-owned server bookmarks. Never written into an instance servers.dat.</summary>
public static class BrowseServerStore
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private static readonly SemaphoreSlim Gate = new(1, 1);

    public static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MOONCL", "browse-servers.json");

    public static IReadOnlyList<ServerDefinition> Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return [];
            var items = JsonSerializer.Deserialize<List<ServerDefinition>>(File.ReadAllText(FilePath), Json) ?? [];
            return items.Where(item => item.Local && PackCatalog.IsAddress(item.Address) && !string.IsNullOrWhiteSpace(item.Name)).Take(200).ToArray();
        }
        catch (Exception ex)
        {
            LogWrapper.Debug(ex, "BrowseServers", "读取本地浏览服务器失败");
            return [];
        }
    }

    public static async Task<BrowseServerSave> AddAsync(string name, string address, string minecraft, string description, string icon)
    {
        address = address.Trim();
        if (!PackCatalog.IsAddress(address)) throw new InvalidDataException("服务器地址无效。");
        name = name.Trim();
        if (name.Length is 0 or > 120) throw new InvalidDataException("服务器名称无效。");
        await Gate.WaitAsync().ConfigureAwait(false);
        try
        {
            var items = Load().ToList();
            var existing = items.FindIndex(item => string.Equals(item.Address, address, StringComparison.OrdinalIgnoreCase));
            var saved = new ServerDefinition
            {
                Id = existing >= 0 ? items[existing].Id : "local-" + Guid.NewGuid().ToString("N")[..12],
                Name = name, Description = Clean(description), Address = address, Minecraft = CleanVersion(minecraft),
                Icon = Icon(icon), Website = "", Source = "local", Local = true
            };
            if (existing >= 0) items[existing] = saved;
            else items.Insert(0, saved);
            Write(items);
            return new BrowseServerSave(existing >= 0);
        }
        finally { Gate.Release(); }
    }

    public static async Task<BrowseServerSave> AddFromInstanceAsync(string name, string address, string minecraft)
    {
        var description = "";
        var icon = "";
        var detected = minecraft;
        try
        {
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            var resolved = await ServerAddressResolver.GetResolvedServerAddressAsync(address, stop.Token).ConfigureAwait(false);
            using var query = McPingServiceFactory.CreateService(resolved.Host, resolved.Ip, resolved.Port);
            var result = await query.PingAsync(stop.Token).ConfigureAwait(false);
            if (result is not null)
            {
                description = result.Description ?? "";
                icon = result.Favicon ?? "";
                if (!string.IsNullOrWhiteSpace(result.Version.Name)) detected = result.Version.Name;
            }
        }
        catch (Exception ex) { LogWrapper.Debug(ex, "BrowseServers", "读取服务器信息失败，仍保存地址"); }
        return await AddAsync(name, address, string.IsNullOrWhiteSpace(detected) ? minecraft : detected, description, icon).ConfigureAwait(false);
    }

    public static void Remove(string id)
    {
        Gate.Wait();
        try { Write(Load().Where(item => item.Id != id).ToArray()); }
        finally { Gate.Release(); }
    }

    private static void Write(IReadOnlyList<ServerDefinition> items)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        var temporary = FilePath + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(items, Json));
        File.Move(temporary, FilePath, true);
    }

    private static string Clean(string value)
    {
        var text = Regex.Replace(value ?? "", @"§.", "").Trim();
        return text.Length <= 300 ? text : text[..300];
    }

    private static string CleanVersion(string value)
    {
        var match = Regex.Match(value ?? "", @"\d+\.\d+(?:\.\d+)?");
        return match.Success ? match.Value : "";
    }

    private static string Icon(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "";
        var text = value.Trim();
        if (!text.StartsWith("data:image/", StringComparison.OrdinalIgnoreCase) || !text.Contains(',') || text.Length > 300_000) return "";
        return text;
    }
}
