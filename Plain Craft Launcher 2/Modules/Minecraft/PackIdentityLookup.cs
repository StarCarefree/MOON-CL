using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
 using PCL.Core.App;
 using PCL.Core.Minecraft.Launcher;

namespace PCL;

internal static class PackIdentityLookup
{
    public static PackIdentity ResolveInstalled(McInstance instance)
    {
        var path = instance.PathInstance;
        var existing = PackIdentityStore.TryRead(path);
        var source = States.Instance.ModpackSource[path] ?? existing?.Source ?? "";
        var id = States.Instance.ModpackId[path] ?? "";
        var clues = new PackClues
        {
            Name = !string.IsNullOrWhiteSpace(existing?.Name) ? existing.Name : instance.Name,
            FileName = instance.Name,
            Version = existing?.Version ?? States.Instance.ModpackVersion[path] ?? "",
            MinecraftVersion = existing?.MinecraftVersion ?? "",
            CurseForgeId = existing?.CurseForgeId ?? "",
            ModrinthId = existing?.ModrinthId ?? "",
            McmodId = existing?.McmodId ?? ""
        };
        if (clues.CurseForgeId.Length == 0 && source.Contains("curse", StringComparison.OrdinalIgnoreCase))
            clues = clues with { CurseForgeId = id };
        if (clues.ModrinthId.Length == 0 && source.Contains("modrinth", StringComparison.OrdinalIgnoreCase))
            clues = clues with { ModrinthId = id };
        if (clues.McmodId.Length == 0 && (source == "mcmod" || source == "MC百科"))
            clues = clues with { McmodId = id };
        return Resolve(clues);
    }

    public static PackIdentity Resolve(PackClues clues)
    {
         if (clues.Query.Length < 2 && clues.CurseForgeId.Length == 0 && clues.ModrinthId.Length == 0 && clues.McmodId.Length == 0)
            return PackIdentityResolver.Local(clues);
        var mcmod = new List<PackCandidate>();
        var curseforge = new List<PackCandidate>();
        var modrinth = new List<PackCandidate>();
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(22));
            using var http = new HttpClient(new HttpClientHandler { AutomaticDecompression = DecompressionMethods.All }) { Timeout = TimeSpan.FromSeconds(20) };
            var catalog = new McmodCatalog(http);
            var platform = new PlatformPackClient((uri, ct) => Task.Run(() => ModDownload.DlModRequest(uri.AbsoluteUri), ct));
            var token = timeout.Token;
            if (int.TryParse(clues.CurseForgeId, out var curseId) && curseId > 0)
                Add(curseforge, () => FromPlatform(platform.GetDetailAsync("curseforge", curseId.ToString(), token).GetAwaiter().GetResult()));
            if (Regex.IsMatch(clues.ModrinthId, "^[A-Za-z0-9]{8}$"))
                Add(modrinth, () => FromPlatform(platform.GetDetailAsync("modrinth", clues.ModrinthId, token).GetAwaiter().GetResult()));
             if (int.TryParse(clues.McmodId, out var knownWiki) && knownWiki > 0)
                 Add(mcmod, () => FromMcmod(catalog.GetDetailAsync(knownWiki, token).GetAwaiter().GetResult()));
             if (clues.Query.Length >= 2)
            {
                var hits = catalog.SearchModpacksAsync(clues.Query, token).GetAwaiter().GetResult().Select(FromMcmod).ToList();
                var best = hits.OrderByDescending(item => PackIdentityResolver.Score(clues, item)).FirstOrDefault();
                if (best is not null && PackIdentityResolver.Score(clues, best) >= PackIdentityResolver.AcceptScore && int.TryParse(best.McmodId, out var wikiId))
                {
                    var detail = catalog.GetDetailAsync(wikiId, token).GetAwaiter().GetResult();
                    if (detail.Description.Length > 0) best = FromMcmod(detail);
                }
                mcmod.AddRange(hits.Where(item => item.McmodId != best?.McmodId));
                if (best is not null) mcmod.Add(best);
                if (curseforge.Count == 0)
                    AddRange(curseforge, () => platform.GetPageAsync("curseforge", 1, clues.Query, clues.MinecraftVersion, token: token).GetAwaiter().GetResult().Items.Take(4).Select(FromPlatform));
                if (modrinth.Count == 0)
                    AddRange(modrinth, () => platform.GetPageAsync("modrinth", 1, clues.Query, clues.MinecraftVersion, token: token).GetAwaiter().GetResult().Items.Take(4).Select(FromPlatform));
            }
        }
        catch (Exception ex)
        {
            ModBase.Log(ex, "[ModPack] 查找整合包介绍失败", ModBase.LogLevel.Debug);
        }
        return PackIdentityResolver.Choose(clues, mcmod, curseforge, modrinth);
    }

    private static void Add(List<PackCandidate> target, Func<PackCandidate> read)
    {
        try { target.Add(read()); }
        catch (Exception ex) { ModBase.Log(ex, "[ModPack] 单个介绍来源失败", ModBase.LogLevel.Debug); }
    }

    private static void AddRange(List<PackCandidate> target, Func<IEnumerable<PackCandidate>> read)
    {
        try { target.AddRange(read()); }
        catch (Exception ex) { ModBase.Log(ex, "[ModPack] 单个介绍来源失败", ModBase.LogLevel.Debug); }
    }

    private static PackCandidate FromMcmod(McmodPack pack) => new()
    {
        Source = "mcmod", Name = pack.Name, EnglishName = pack.EnglishName, Description = pack.Description,
        Website = pack.Website, MinecraftVersion = pack.MinecraftVersions.FirstOrDefault() ?? "", McmodId = pack.WikiId.ToString()
    };

    private static PackCandidate FromPlatform(PlatformPack pack) => new()
    {
        Source = pack.Source, Name = pack.Name, Description = pack.Description, Website = pack.Website, Version = pack.Version,
        Slug = pack.Slug, MinecraftVersion = pack.MinecraftVersions.FirstOrDefault(version => Regex.IsMatch(version, @"^\d{1,2}\.\d{1,2}(?:\.\d{1,2})?$")) ?? "",
        CurseForgeId = pack.Source == "curseforge" ? pack.ProjectId : "", ModrinthId = pack.Source == "modrinth" ? pack.ProjectId : ""
    };
}

internal static class PackIdentityInstall
{
    public static void Attach(ModLoader.LoaderCombo<string> loader, PackIdentity identity)
    {
         void Save()
         {
             if (loader.State != ModBase.LoadState.Finished) return;
             var folder = loader.typedInput;
             if (string.IsNullOrWhiteSpace(folder)) return;
             try
             {
                 PackIdentityStore.Save(folder, identity);
                 States.Instance.ModpackSource[folder] = identity.SourceLabel;
                 if (!string.IsNullOrWhiteSpace(identity.Version)) States.Instance.ModpackVersion[folder] = identity.Version;
                 var id = First(identity.CurseForgeId, identity.ModrinthId, identity.McmodId);
                 if (id.Length > 0) States.Instance.ModpackId[folder] = id;
                 if (string.IsNullOrWhiteSpace(States.Instance.CustomInfo[folder]))
                     States.Instance.CustomInfo[folder] = identity.SidebarLine;
             }
             catch (Exception ex)
             {
                 ModBase.Log(ex, "[ModPack] 保存整合包身份失败", ModBase.LogLevel.Debug);
             }
         }
         loader.OnStateChanged = _ => Save();
         if (loader.State == ModBase.LoadState.Finished) Save();
    }

    private static string First(params string[] values) => values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? "";
}
