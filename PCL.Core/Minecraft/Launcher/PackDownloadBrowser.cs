using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace PCL.Core.Minecraft.Launcher;

public enum PackDownloadSort { Newest, Oldest, Name, Minecraft }
public sealed record PackDownloadGroup(string Minecraft, IReadOnlyList<PackDownloadVersion> Versions);

/// <summary>Exact game-version filtering and deterministic browsing, independent of the WPF controls.</summary>
public static class PackDownloadBrowser
{
    public static IReadOnlyList<PackDownloadGroup> Browse(IEnumerable<PackDownloadVersion> source, string query,
        string minecraft, PackDownloadSort sort)
    {
        var tokens = query.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var versions = source.Where(v => minecraft.Length == 0 || PackIdentityResolver.MinecraftMatches(v.Minecraft, minecraft))
            .Where(v => tokens.All(token => Search(v, token)));
        var ordered = sort switch
        {
            PackDownloadSort.Oldest => versions.OrderBy(v => v.Published).ThenBy(v => v.Id, StringComparer.Ordinal),
            PackDownloadSort.Name => versions.OrderBy(v => v.Name, StringComparer.OrdinalIgnoreCase).ThenByDescending(v => v.Published),
            PackDownloadSort.Minecraft => versions.OrderByDescending(v => GameVersion(v.Minecraft)).ThenByDescending(v => v.Published),
            _ => versions.OrderByDescending(v => v.Published).ThenBy(v => v.Id, StringComparer.Ordinal)
        };
        var groups = new Dictionary<string, List<PackDownloadVersion>>(StringComparer.Ordinal);
        foreach (var version in ordered)
        {
            var games = Regex.Matches(version.Minecraft, @"\d+\.\d+(?:\.\d+)?").Select(m => m.Value).Distinct().ToArray();
            foreach (var game in games.Length == 0 ? new[] { "未知版本" } : games)
            {
                if (minecraft.Length > 0 && game != minecraft) continue;
                if (!groups.TryGetValue(game, out var items)) groups[game] = items = [];
                items.Add(version);
            }
        }
        var result = groups.Select(g => new PackDownloadGroup(g.Key, g.Value));
        return (sort == PackDownloadSort.Minecraft ? result.OrderByDescending(g => GameVersion(g.Minecraft)) : result).ToArray();
    }

    private static bool Search(PackDownloadVersion version, string token)
    {
        var text = $"{version.Name} {version.Version} {version.FileName} {version.Minecraft} {version.Loader} {version.ReleaseType}";
        return Regex.IsMatch(token, @"^\d+\.\d+(?:\.\d+)?$")
            ? Regex.IsMatch(text, @"(?<![\d.])" + Regex.Escape(token) + @"(?![\d.])", RegexOptions.IgnoreCase)
            : text.Contains(token, StringComparison.OrdinalIgnoreCase);
    }

    private static Version GameVersion(string value) => Regex.Matches(value, @"\d+\.\d+(?:\.\d+)?")
        .Select(m => Version.TryParse(m.Value, out var v) ? v : new Version(0, 0)).DefaultIfEmpty(new Version(0, 0)).Max()!;
}
