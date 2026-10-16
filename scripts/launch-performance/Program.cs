using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using PCL;

internal static class Program
{
    private static int _checks;
    private static void Check(bool value, string message)
    {
        _checks++;
        if (!value) throw new Exception(message);
    }

    private static void Index(string name, object objects, bool virtualFiles = false, bool remap = false)
        => File.WriteAllText(Path.Combine(ModFolder.mcFolderSelected, "assets", "indexes", name + ".json"),
            JsonSerializer.Serialize(new { objects, @virtual = virtualFiles, map_to_resources = remap }));

    private static string Hash(int value) => Convert.ToHexStringLower(System.Security.Cryptography.SHA1.HashData(BitConverter.GetBytes(value)));
    private static string ObjectPath(int value) => Path.Combine(ModFolder.mcFolderSelected, "assets", "objects", Hash(value)[..2], Hash(value));
    private static void Put(string path, int size)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path));
        File.WriteAllBytes(path, new byte[size]);
    }

    public static int Main(string[] args)
    {
        var root = Path.Combine(Environment.GetEnvironmentVariable("PI_SCRATCH_DIR") ?? Path.GetTempPath(), "launch-performance-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        ModFolder.mcFolderSelected = root + Path.DirectorySeparatorChar;
        Directory.CreateDirectory(Path.Combine(root, "assets", "indexes"));
        var instance = new McInstance(root) { JsonObject = JsonNode.Parse("{\"assets\":\"benchmark\"}") };
        try
        {
            var objects = Enumerable.Range(1, 16000).ToDictionary(i => $"minecraft/sounds/{i}.ogg", i => new { hash = Hash(i), size = 64 });
            Index("benchmark", objects);
            // Warm JIT, then report medians, not one noisy sample. File system cache is warm in both versions.
            for (var i = 0; i < 3; i++) ModAssets.McAssetsListGet(instance);
            var times = new List<double>();
            var allocations = new List<long>();
            for (var i = 0; i < 9; i++)
            {
                var before = GC.GetAllocatedBytesForCurrentThread();
                var watch = Stopwatch.StartNew();
                Check(ModAssets.McAssetsListGet(instance).Count == 16000, "Benchmark lost assets");
                watch.Stop();
                times.Add(watch.Elapsed.TotalMilliseconds);
                allocations.Add(GC.GetAllocatedBytesForCurrentThread() - before);
            }
            times.Sort(); allocations.Sort();
            Console.WriteLine($"INDEX 16000: median={times[4]:F2} ms, allocated={allocations[4]} bytes");

            // Measure the complete existing-file scan as well as index parsing.
            foreach (var i in Enumerable.Range(1, 16000)) Put(ObjectPath(i), 64);
            var scanTimes = new List<double>();
            ModLoader.LoaderBase progress = new();
            for (var i = 0; i < 7; i++)
            {
                var watch = Stopwatch.StartNew();
                Check(ModAssets.McAssetsFixList(instance, false, ref progress).Count == 0, "Existing assets must not download");
                watch.Stop(); scanTimes.Add(watch.Elapsed.TotalMilliseconds);
            }
            scanTimes.Sort();
            Console.WriteLine($"SCAN 16000 existing: median={scanTimes[3]:F2} ms");
            if (args.Contains("--baseline")) return 0;

            progress = new ModLoader.LoaderBase();
            Check(ModAssets.McAssetsFixList(instance, false, ref progress).Count == 0, "Distributed asset scan failed");
            Console.WriteLine($"PROGRESS 16000: {progress.Updates} updates");
            Check(progress.Updates <= 103, "Asset scan must not send a progress event for every file");

            // Exercise the bulk path, not just the small-index fallback, on each fresh scan.
            File.Delete(ObjectPath(1));
            Put(ObjectPath(2), 65);
            File.Delete(ObjectPath(3));
            Directory.CreateDirectory(ObjectPath(3));
            var bulkMissing = ModAssets.McAssetsFixList(instance, false, ref progress);
            Check(bulkMissing.Count == 3, "Bulk scan missed a deleted, resized or directory-only asset");
            Check(bulkMissing.Select(f => f.LocalPath).ToHashSet().SetEquals(new[] { ObjectPath(1), ObjectPath(2), ObjectPath(3) }),
                "Bulk scan returned the wrong missing files");
            Directory.Delete(ObjectPath(3));
            foreach (var i in Enumerable.Range(1, 3)) Put(ObjectPath(i), 64);
            Check(ModAssets.McAssetsFixList(instance, false, ref progress).Count == 0, "Bulk scan cached stale metadata");
            var removedDirectory = Path.GetDirectoryName(ObjectPath(1));
            Directory.Delete(removedDirectory, true);
            var removedTokens = ModAssets.McAssetsListGet(instance).Where(t => Path.GetDirectoryName(t.localPath) == removedDirectory).ToList();
            Check(ModAssets.McAssetsFixList(instance, false, ref progress).Count == removedTokens.Count,
                "Missing hash directory must queue all its assets");
            Put(removedDirectory, 1);
            Check(ModAssets.McAssetsFixList(instance, false, ref progress).Count == removedTokens.Count,
                "Unreadable hash directory must fall back without dropping missing assets");
            File.Delete(removedDirectory);
            foreach (var token in removedTokens) Put(token.localPath, (int)token.size);
            ModLoader.LoaderBase noProgress = null;
            Check(ModAssets.McAssetsFixList(instance, false, ref noProgress).Count == 0, "Null progress feed failed");
            for (var i = 0; i < 512; i++) Put(Path.Combine(removedDirectory, $"!unrelated-{i:D4}"), 1);
            Check(ModAssets.McAssetsFixList(instance, false, ref noProgress).Count == 0,
                "Crowded shared directory must preserve fallback checks");
            File.Delete(ObjectPath(1));
            var crowdedMissing = ModAssets.McAssetsFixList(instance, false, ref noProgress);
            Check(crowdedMissing.Count == 1 && crowdedMissing[0].LocalPath == ObjectPath(1),
                "Crowded directory fallback missed a deleted asset");
            Put(ObjectPath(1), 64);

            Index("benchmark", new Dictionary<string, object>
            {
                ["first.ogg"] = new { hash = Hash(1), size = 64 },
                ["alias.ogg"] = new { hash = Hash(1), size = 64 },
                ["wrong-size.ogg"] = new { hash = Hash(2), size = 65 },
                ["missing.ogg"] = new { hash = Hash(17000), size = 64 },
                ["unknown-size.ogg"] = new { hash = Hash(3), size = 0 }
            });
            Check(ModAssets.McAssetsListGet(instance).Count == 4, "Object aliases must be scanned only once");
            var missing = ModAssets.McAssetsFixList(instance, false, ref progress);
            Check(missing.Count == 2, "Missing and wrong-size assets must still download");
            Check(missing.Any(f => f.LocalPath == ObjectPath(2) && f.Check.actualSize == 65), "Wrong size not detected");
            Check(missing.Any(f => f.Check.hash == Hash(17000)), "Missing asset lost its SHA1 check");
            Check(ModAssets.McAssetsFixList(instance, true, ref progress).Count == 4, "Full hash verification must cover every unique asset");
            Check(progress.Progress == .99, "Final progress missing");

            // Metadata must not be cached across launches: deletions and replacements are picked up immediately.
            File.Delete(ObjectPath(1));
            Put(ObjectPath(3), 1);
            Check(ModAssets.McAssetsFixList(instance, false, ref progress).Count == 3, "Deletion/unknown-size handling changed");
            Index("benchmark", new Dictionary<string, object> { ["replacement"] = new { hash = Hash(4), size = 64 } });
            Check(ModAssets.McAssetsListGet(instance).Single().hash == Hash(4), "Changed index was cached");

            foreach (var remap in new[] { false, true })
            {
                Index("benchmark", new Dictionary<string, object>
                {
                    ["a/first.ogg"] = new { hash = Hash(1), size = 64 },
                    ["b/alias.ogg"] = new { hash = Hash(1), size = 64 }
                }, virtualFiles: true, remap: remap);
                var tokens = ModAssets.McAssetsListGet(instance);
                Check(tokens.Count == 2, "Legacy paths must not be deduplicated by hash");
                var basePath = remap ? Path.Combine(root, "resources") : Path.Combine(root, "assets", "virtual", "legacy");
                Check(tokens[0].localPath == Path.Combine(basePath, @"a\first.ogg"), "Legacy path or remap precedence changed");
                Put(tokens[0].localPath, 64);
                Check(ModAssets.McAssetsFixList(instance, false, ref progress).Count == 1, "Legacy files must be checked separately");
            }

            Index("benchmark", new Dictionary<string, object>
            {
                ["first"] = new { hash = Hash(4), size = 64 },
                ["conflict"] = new { hash = Hash(4), size = 65 }
            });
            Check(ModAssets.McAssetsListGet(instance).Count == 2, "Conflicting sizes must not be hidden by deduplication");
            File.WriteAllText(Path.Combine(root, "assets", "indexes", "benchmark.json"),
                "{/* comment */\"OBJECTS\":{\"a\":{\"HASH\":\"" + Hash(4) + "\",\"SIZE\":\"64\",},},\"VIRTUAL\":\"true\",\"MAP_TO_RESOURCES\":null,}");
            var flexible = ModAssets.McAssetsListGet(instance).Single();
            Check(flexible.size == 64 && flexible.localPath == Path.Combine(root, "assets", "virtual", "legacy", "a"),
                "Loose JSON, case-insensitive fields, quoted numbers/booleans or null flags regressed");
            Index("benchmark", new Dictionary<string, object>());
            Check(ModAssets.McAssetsListGet(instance).Count == 0, "Empty index failed");
            Check(ModAssets.McAssetsFixList(instance, false, ref progress).Count == 0 && progress.Progress == .99, "Empty scan failed");
            Index("benchmark", new Dictionary<string, object> { ["culture"] = new { hash = Hash(4), size = 64 } });

            // Culture must not affect numeric index parsing.
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("ar-SA");
            Check(ModAssets.McAssetsListGet(instance)[0].size == 64, "Culture-dependent size parsing");
            Console.WriteLine($"PASS: {_checks} checks");
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex.Message); return 1; }
        finally { Directory.Delete(root, true); }
    }
}

// Narrow adapters for unrelated launcher services. ModAssets itself is linked unchanged from production.
namespace PCL
{
    public sealed class McInstance(string path)
    {
        public string PathIndie => path;
        public JsonNode JsonObject { get; set; }
        public string InheritInstanceName => null;
    }
    public static class ModFolder { public static string mcFolderSelected; }
    public static class ModBase
    {
        public static object GetJson(string text) => PCL.Core.Utils.JsonCompat.ParseNode(text);
        public static string ReadFile(string path) => File.ReadAllText(path);
        public static string GetString(long value) => value.ToString();
        public static void Log(string text) { }
        public static void Log(Exception error, string text) => throw new Exception(text, error);
        public sealed class FileChecker(long actualSize = -1, string hash = null)
        {
            public long actualSize = actualSize;
            public string hash = hash;
        }
    }
    public static class ModLoader
    {
        public sealed class LoaderBase
        {
            private double _progress;
            public int Updates { get; private set; }
            public double Progress { get => _progress; set { _progress = value; Updates++; } }
        }
    }
    public static class ModDownload
    {
        public static string[] DlSourceAssetsGet(string url) => [url];
    }
}
namespace PCL.Network
{
    public sealed class DownloadFile(string[] urls, string path, ModBase.FileChecker check)
    {
        public string LocalPath => path;
        public string[] Urls => urls;
        public ModBase.FileChecker Check => check;
    }
}
namespace PCL.Core.App.Localization
{
    public static class Lang { public static string Text(string key) => key; }
}
