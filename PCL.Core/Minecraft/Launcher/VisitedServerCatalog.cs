using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using fNbt;

namespace PCL.Core.Minecraft.Launcher;

public sealed record VisitedServer(string Name, string Address, string Icon, string InstanceName, string InstancePath, string Minecraft);

/// <summary>Reads multiplayer servers already saved by Minecraft. Never writes servers.dat.</summary>
public static class VisitedServerCatalog
{
    public sealed record InstanceRoot(string Name, string IndiePath, string Minecraft);

    public static IReadOnlyList<VisitedServer> Load(IEnumerable<InstanceRoot> instances)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<VisitedServer>();
        foreach (var instance in instances ?? [])
        {
            if (instance is null || string.IsNullOrWhiteSpace(instance.IndiePath)) continue;
            foreach (var server in Read(instance))
            {
                if (!seen.Add(Key(server.Address))) continue;
                result.Add(server);
                if (result.Count >= 200) return result;
            }
        }
        return result;
    }

    public static string Key(string address)
    {
        var text = (address ?? "").Trim();
        var split = text.LastIndexOf(':');
        if (split > 0 && text.IndexOf(':') == split && int.TryParse(text[(split + 1)..], out var port) && port == 25565)
            text = text[..split];
        return text;
    }

    private static IEnumerable<VisitedServer> Read(InstanceRoot instance)
    {
        var file = Path.Combine(instance.IndiePath, "servers.dat");
        if (!File.Exists(file)) yield break;
        NbtFile nbt;
        try
        {
            nbt = new NbtFile();
            using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            nbt.LoadFromStream(stream, NbtCompression.AutoDetect);
        }
        catch (Exception) { yield break; }
        if (nbt.RootTag.Get<NbtList>("servers") is not { } servers) yield break;
        foreach (var tag in servers)
        {
            if (tag is not NbtCompound server) continue;
            var address = server.Get<NbtString>("ip")?.Value?.Trim() ?? "";
            var name = server.Get<NbtString>("name")?.Value?.Trim() ?? "";
            if (!PackCatalog.IsAddress(address) || name.Length is 0 or > 120) continue;
            if (server.Get<NbtByte>("hidden")?.Value == 1) continue;
            var icon = Icon(server.Get<NbtString>("icon")?.Value);
            yield return new VisitedServer(name, address, icon, instance.Name ?? "", instance.IndiePath, Version(instance.Minecraft));
        }
    }

    private static string Icon(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "";
        var text = value.Trim();
        if (!text.StartsWith("data:image/", StringComparison.OrdinalIgnoreCase))
            text = "data:image/png;base64," + text;
        return text.Contains(',') && text.Length <= 300_000 ? text : "";
    }

    private static string Version(string? value)
    {
        var match = Regex.Match(value ?? "", @"\d+\.\d+(?:\.\d+)?");
        return match.Success ? match.Value : "";
    }
}
