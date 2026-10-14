using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json;

namespace PCL.Core.Minecraft.Launcher;

public sealed record PackArchiveMetadata(string Name, string Version, string Minecraft);

public static class PackArchive
{
    public static PackArchiveMetadata? ReadMetadata(string file)
    {
        using var archive = ZipFile.OpenRead(file);
        var entry = archive.Entries.FirstOrDefault(e => e.FullName == "modrinth.index.json")
            ?? archive.Entries.FirstOrDefault(e => e.FullName.Count(c => c == '/') == 1 && e.FullName.EndsWith("/modrinth.index.json"));
        if (entry is null) return null;
        if (entry.Length > 4 * 1024 * 1024) throw new InvalidDataException("整合包元数据过大。");
        using var stream = entry.Open();
        using var document = JsonDocument.Parse(stream);
        var root = document.RootElement;
        string Get(string key) => root.TryGetProperty(key, out var value) ? value.GetString() ?? "" : "";
        var minecraft = root.TryGetProperty("dependencies", out var dependencies) && dependencies.TryGetProperty("minecraft", out var mc) ? mc.GetString() ?? "" : "";
        return new PackArchiveMetadata(Get("name"), Get("versionId"), minecraft);
    }
}
