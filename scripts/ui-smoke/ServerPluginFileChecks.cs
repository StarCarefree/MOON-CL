using System.IO;
using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json;
using System.Text.Json.Nodes;

internal static class ServerPluginFileChecks
{
    internal static void Run(Assembly assembly, string directory)
    {
        InitializeDefaults(directory);
        var fileType = assembly.GetType("PCL.ModComp+CompFile", true)!;
        var compType = assembly.GetType("PCL.ModComp+CompType", true)!;
        var plugin = Enum.Parse(compType, "Plugin");
        foreach (var (loaders, expected) in new[]
        {
            (new[] { "fabric" }, "Mod"), (new[] { "datapack" }, "DataPack"),
            (new[] { "paper" }, "Plugin"), (new[] { "folia" }, "Plugin"),
            (new[] { "paper", "fabric" }, "Plugin")
        })
        {
            var raw = JsonSerializer.SerializeToNode(new
            {
                id = "version1", project_id = "project1", name = "Plugin test", version_number = "1.0",
                date_published = "2025-01-01T00:00:00Z", version_type = "release", downloads = 10,
                loaders, game_versions = new[] { "1.21.1" },
                files = new[]
                {
                    new { filename = "sources.zip", primary = false, url = "https://cdn.modrinth.com/sources.zip", hashes = new { sha1 = new string('a', 40) } },
                    new { filename = "plugin.jar", primary = true, url = "https://cdn.modrinth.com/plugin.jar", hashes = new { sha1 = new string('b', 40) } }
                }
            })!.AsObject();
            var file = Activator.CreateInstance(fileType, raw, plugin)!;
            if (fileType.GetField("Type")!.GetValue(file)!.ToString() != expected) throw new Exception("Wrong file type: " + string.Join(',', loaders));
            if ((string)fileType.GetField("FileName")!.GetValue(file)! != "plugin.jar") throw new Exception("Did not select primary file");
            var cached = (JsonObject)fileType.GetMethod("ToJson")!.Invoke(file, null)!;
            var restored = Activator.CreateInstance(fileType, cached, plugin)!;
            if (fileType.GetField("Type")!.GetValue(restored)!.ToString() != expected) throw new Exception("Cache lost file type");
            var before = (System.Collections.ICollection)fileType.GetField("PluginLoaders")!.GetValue(file)!;
            var after = (System.Collections.ICollection)fileType.GetField("PluginLoaders")!.GetValue(restored)!;
            if (before.Count != after.Count) throw new Exception("Cache lost plugin environments");
            Console.WriteLine("PASS native file parser / primary / cache: " + string.Join(',', loaders));
        }
    }

    private static void InitializeDefaults(string directory)
    {
        var core = AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.Combine(directory, "PCL.Core.dll"));
        var service = core.GetType("PCL.Core.App.Configuration.ConfigService", true)!;
        const BindingFlags statics = BindingFlags.Static | BindingFlags.NonPublic;
        service.GetMethod("_InitializeConfigItems", statics)!.Invoke(null, null);
        service.GetField("_isConfigItemsInitialized", statics)!.SetValue(null, true);
        var items = (System.Collections.IDictionary)service.GetField("_Items", statics)!.GetValue(null)!;
        foreach (var item in items.Values)
        {
            var type = item!.GetType();
            type.GetProperty("EnableCache")!.SetValue(item, true);
            var value = type.GetProperty("DefaultValueNoType")!.GetValue(item);
            var cache = type.GetField("_valueCache", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(item)!;
            cache.GetType().GetMethod("Write")!.Invoke(cache, new[] { value, null });
            type.GetField("_valueCache", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(item, cache);
        }
    }
}
