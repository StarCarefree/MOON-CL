using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using PCL.Core.Minecraft.Launcher;
using PCL.Core.Multiplayer;

namespace PCL;

/// <summary>Maps a launched instance to a public catalog download. Never publishes a local path.</summary>
internal static class RoomPackShareResolver
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    internal sealed record Marker(string Id, string Version);

    internal static RoomPackShare? Resolve(McInstance? instance, IEnumerable<PackDefinition>? catalog)
    {
        if (instance is null || string.IsNullOrWhiteSpace(instance.PathInstance)) return null;
        var packs = (catalog ?? []).Where(Shareable).ToArray();
        if (packs.Length == 0) return null;
        var marker = ReadMarker(instance.PathInstance);
        var match = marker is null ? null : packs.FirstOrDefault(pack => string.Equals(pack.Id, marker.Id, StringComparison.Ordinal));
        if (match is null)
        {
            var identity = PackIdentityStore.TryRead(instance.PathInstance);
            match = identity is null ? null : packs.FirstOrDefault(pack => SameIdentity(pack, identity));
        }
        if (match is null && instance.Name.Contains("StarRailHarpyExpress", StringComparison.OrdinalIgnoreCase))
            match = packs.FirstOrDefault(pack => pack.Id == PackCatalog.StarRailId);
        return match is null ? null : From(match);
    }

    internal static RoomPackShare? From(PackDefinition? pack) => Shareable(pack) ? new RoomPackShare(
        pack!.Id, Bound(pack.Name, 80), Bound(pack.Version, 80), Bound(pack.Minecraft, 40), pack.FileName,
        pack.DownloadUrl, pack.Sha256, pack.ShareUrl, pack.SharePath) : null;

    internal static RoomPackShare? FromPublicVersion(PackIdentity? identity, PackDownloadVersion? version)
    {
        if (identity is null || version is null) return null;
        var source = version.Source == "modrinth" ? "modrinth" : version.Source == "curseforge" ? "curseforge" : "";
        if (source.Length == 0) return null;
        var id = source + "-" + version.ProjectId.ToLowerInvariant();
        if (id.Length > 64) id = id[..64];
        var minecraft = version.Minecraft.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault() ?? identity.MinecraftVersion;
        return From(new PackDefinition
        {
            Id = id, Name = identity.Name, Version = version.Version, Minecraft = minecraft,
            FileName = version.FileName, DownloadUrl = version.Url,
            Sha256 = version.Algorithm == System.Security.Cryptography.HashAlgorithmName.SHA256 ? version.Hash : ""
        });
    }

    internal static bool Shareable(PackDefinition? pack)
    {
        if (pack is null || string.IsNullOrWhiteSpace(pack.DownloadUrl) && string.IsNullOrWhiteSpace(pack.ShareUrl)) return false;
        try { new RoomPackShare(pack.Id, Bound(pack.Name, 80), Bound(pack.Version, 80), Bound(pack.Minecraft, 40), pack.FileName, pack.DownloadUrl, pack.Sha256, pack.ShareUrl, pack.SharePath).Validate(); return true; }
        catch (ArgumentException) { return false; }
    }

    internal static PackDefinition ToPack(RoomPackShare share) => new()
    {
        Id = share.Id, Name = share.Name, Version = share.Version, Minecraft = share.Minecraft,
        FileName = share.FileName, DownloadUrl = share.DownloadUrl, Sha256 = share.Sha256,
        ShareUrl = share.ShareUrl, SharePath = share.SharePath,
        Description = "来自联机房间的整合包分享"
    };

    private static bool SameIdentity(PackDefinition pack, PackIdentity identity)
    {
        if (identity.Source == "modrinth" && !string.IsNullOrEmpty(identity.ModrinthId) &&
            (pack.Id.Equals(identity.ModrinthId, StringComparison.OrdinalIgnoreCase) || pack.DownloadUrl.Contains("/" + identity.ModrinthId + "/", StringComparison.OrdinalIgnoreCase)))
            return true;
        if (identity.Source == "mcmod" && !string.IsNullOrEmpty(identity.McmodId) && pack.Id.Contains(identity.McmodId, StringComparison.Ordinal))
            return true;
        return !string.IsNullOrWhiteSpace(identity.Name) && string.Equals(pack.Name, identity.Name, StringComparison.OrdinalIgnoreCase);
    }

    private static Marker? ReadMarker(string versionFolder)
    {
        try
        {
            var path = Path.Combine(versionFolder, "PCL", "launcher-pack.json");
            if (!File.Exists(path)) return null;
            var marker = JsonSerializer.Deserialize<Marker>(File.ReadAllText(path), Json);
            return marker is { Id.Length: > 0 } ? marker : null;
        }
        catch (Exception) { return null; }
    }

    private static string Bound(string? text, int max)
    {
        var value = new string((text ?? "").Where(ch => !char.IsControl(ch)).ToArray()).Trim();
        return value.Length <= max ? value : value[..max];
    }
}
