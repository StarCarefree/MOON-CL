using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.CompilerServices;
using PCL;
using PCL.Core.Minecraft.Launcher;
using PCL.Network;
using PCL.Network.Loaders;

internal static class Program
{
    private const string Secret = "smoke-private-token";
    private const string Signature = "smoke-signed-query";
    private static int _checks;
    private static void Check(bool condition, string message) { _checks++; if (!condition) throw new Exception(message); }
    private static readonly Type Bridge = typeof(DownloadFile).Assembly.GetType("PCL.QuarkNativeDownload", throwOnError: true)!;
    private static ModLoader.LoaderCombo<string> Create(string name, string path, QuarkOwnedDownload owned) =>
        (ModLoader.LoaderCombo<string>)Bridge.GetMethod("Create", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, [name, path, owned])!;
    private static Task Wait(ModLoader.LoaderCombo<string> task, CancellationToken token = default) =>
        (Task)Bridge.GetMethod("WaitAsync", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, [task, token])!;

    [STAThread]
    public static int Main(string[] args)
    {
        var launcher = Path.GetFullPath(args[0]);
        AppDomain.CurrentDomain.AssemblyResolve += (_, e) =>
        {
            var path = Path.Combine(launcher, new AssemblyName(e.Name).Name + ".dll");
            return File.Exists(path) ? Assembly.LoadFrom(path) : null;
        };
        try { Run(args).GetAwaiter().GetResult(); return 0; }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task Run(string[] args)
    {
        var scratch = Path.GetFullPath(args[1]); Directory.CreateDirectory(scratch);
        foreach (var key in new[] { "PCL_PATH", "PCL_PATH_SHARED", "PCL_PATH_LOCAL", "PCL_PATH_TEMP" })
            Environment.SetEnvironmentVariable(key, Path.Combine(scratch, key));
        InitializeDefaults();
        // Native completion refreshes the download button; no real launcher/window is started.
        ModMain.frmMain = (FormMain)RuntimeHelpers.GetUninitializedObject(typeof(FormMain));
        typeof(FormMain).GetField("BtnExtraDownload", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!
            .SetValue(ModMain.frmMain, RuntimeHelpers.GetUninitializedObject(typeof(MyExtraButton)));
        typeof(PCL.Core.IO.Net.NetworkService).GetMethod("_Start", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, null);
        using var server = new FixtureServer();
        var headers = new Dictionary<string, string> { ["Cookie"] = "x_pan_access_token=" + Secret, ["Referer"] = "https://pan.quark.cn/" };
        var session = new QuarkOpenSession("dummy-access", "dummy-refresh", "dummy-device", 0, 0);
        const int size = 8 * 1024 * 1024;
        var path = Path.Combine(scratch, "native-download.bin");
        var owned = new QuarkOwnedDownload(new QuarkDownload(server.Url + "protected?sig=" + Signature, "pack.zip", headers), session, expectedSize: size);
        var task = Create("测试单文件", path, owned);
        var downloader = task.GetLoaderList().OfType<LoaderDownload>().Single();
        var file = downloader.files.Single();
        Check(file.Check!.actualSize == size && !file.Check.canUseExistsFile, "Native file must enforce expected size and reject stale local files");
        headers["Cookie"] = "mutated";
        Check(file.RequestHeaders["Cookie"].EndsWith(Secret, StringComparison.Ordinal), "Request headers must be an immutable snapshot");
        task.Start(path); ModLoader.LoaderTaskbarAdd(task);
        var waiting = Wait(task); var sawProgress = false; var sawSpeed = false;
        while (!waiting.IsCompleted)
        {
            sawProgress |= task.Progress > 0.05 && task.Progress < 0.99 && file.DownloadedBytes > 0;
            sawSpeed |= file.Speed > 0;
            await Task.Delay(20);
        }
        await waiting;
        Console.WriteLine("Native states: task=" + task.State + " loader=" + downloader.State + " file=" + file.State + " bytes=" + file.DownloadedBytes + " exists=" + File.Exists(path));
        Check(task.State == ModBase.LoadState.Finished && file.State == NetState.Finished, "Native task must finish");
        Check(sawProgress && sawSpeed, "Native task must expose live percentage, bytes and speed");
        Check(new FileInfo(path).Length == size, "Native download must write exact expected bytes");
        Check(ModLoader.loaderTaskbar.Contains(task), "Real native task must be registered in the task list");
        ModLoader.loaderTaskbar.Remove(task);
        Check(server.Cookies.TryGetValue("/protected", out var cookie) && cookie == "x_pan_access_token=" + Secret, "Session cookie must reach the selected CDN request");
        Check(server.Count("/protected") == 1, "Private transfer must avoid unauthenticated Range probes");

        var publicPath = Path.Combine(scratch, "public.bin");
        var publicFile = new DownloadFile([server.Url + "public"], publicPath, new ModBase.FileChecker(actualSize: 1024));
        await FileDownloader.DownloadAsync(publicFile.Urls, publicPath, enableParallelChunks: false, trackedFile: publicFile);
        Check(server.Cookies["/public"] == "", "A later public file must not inherit the Quark session cookie");

        foreach (var endpoint in new[] { "denied", "redirect" })
        {
            var target = Path.Combine(scratch, endpoint + ".bin");
            var privateFile = new DownloadFile([server.Url + endpoint + "?sig=" + Signature], target,
                new ModBase.FileChecker(actualSize: 1024), requestHeaders: file.RequestHeaders);
            try { await FileDownloader.DownloadAsync(privateFile.Urls, target, trackedFile: privateFile); throw new Exception("Expected rejection"); }
            catch (HttpRequestException ex)
            {
                Check(ex.StatusCode == (endpoint == "denied" ? HttpStatusCode.PreconditionFailed : HttpStatusCode.Redirect), "Native failure must retain HTTP status");
                Check(!ex.ToString().Contains(Secret) && !ex.ToString().Contains(Signature), "Native errors must redact authorization and signed URLs");
            }
            Check(!File.Exists(target) && !File.Exists(target + ModNet.NetDownloadEnd), "Rejected download must leave no archive or partial file");
            Check(server.Count("/" + endpoint) == 1, "Refused private request must not be retried repeatedly");
        }
        Check(server.Count("/leak") == 0, "Private headers must never follow a redirect");

        var mismatch = Path.Combine(scratch, "mismatch.bin");
        var mismatchFile = new DownloadFile([server.Url + "public"], mismatch, new ModBase.FileChecker(actualSize: 2048), requestHeaders: file.RequestHeaders);
        try { await FileDownloader.DownloadAsync(mismatchFile.Urls, mismatch, trackedFile: mismatchFile); throw new Exception("Expected size mismatch"); }
        catch (IOException) { Check(!File.Exists(mismatch), "Wrong-sized payload must never become an installed archive"); }

        foreach (var nativeAbort in new[] { false, true })
        {
            var cancelPath = Path.Combine(scratch, nativeAbort ? "native-abort.bin" : "token-abort.bin");
            var cancelOwned = new QuarkOwnedDownload(new QuarkDownload(server.Url + "slow?sig=" + Signature, "pack.zip", file.RequestHeaders), session, expectedSize: size);
            var cancelTask = Create("测试取消", cancelPath, cancelOwned);
            var tracked = cancelTask.GetLoaderList().OfType<LoaderDownload>().Single().files.Single();
            using var cts = new CancellationTokenSource();
            cancelTask.Start(cancelPath); var pending = Wait(cancelTask, cts.Token);
            await Until(() => tracked.DownloadedBytes > 0, "Download did not begin before cancellation");
            if (nativeAbort) cancelTask.Abort(); else cts.Cancel();
            try { await pending; throw new Exception("Cancellation must stop installation handoff"); }
            catch (OperationCanceledException) { Check(cancelTask.State == ModBase.LoadState.Aborted, "Cancellation must abort the real native task"); }
            await Until(() => !File.Exists(cancelPath) && !File.Exists(cancelPath + ModNet.NetDownloadEnd), "Cancelled download did not clean up");
        }

        var latePath = Path.Combine(scratch, "late-start.bin");
        var lateOwned = new QuarkOwnedDownload(new QuarkDownload(server.Url + "late", "pack.zip", file.RequestHeaders), session, expectedSize: 1024);
        var lateTask = Create("测试取消竞态", latePath, lateOwned);
        var lateLoader = lateTask.GetLoaderList().OfType<LoaderDownload>().Single();
        lateTask.Abort();
        // Model the generic combo's completion race: cancellation must remain sticky.
        lateTask.State = ModBase.LoadState.Finished;
        try { await Wait(lateTask); throw new Exception("A late Finished state must not undo cancellation"); }
        catch (OperationCanceledException) { Check(true, "Sticky cancellation wins over a late Finished state"); }
        // Model an already-queued combo Update starting its child after cancellation/cleanup.
        lateLoader.Start();
        await lateLoader.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        await Task.Delay(100);
        Check(server.Count("/late") == 0 && !File.Exists(latePath), "A cancelled task must not start delayed native IO");

        if (args.Length > 2 && args[2] == "--live")
        {
            var livePath = Path.Combine(scratch, "quark-owned-live.zip");
            var store = new QuarkOpenSessionStore(AppContext.BaseDirectory);
            var liveSession = store.Load() ?? throw new Exception("No reusable Quark session");
            using var http = new HttpClient(new HttpClientHandler { UseCookies = false });
            var api = new QuarkOpenClient(http); api.SessionRefreshed += store.Save;
            var live = await api.ResolveOwnedAsync(liveSession, PackCatalog.TestShare, PackCatalog.TestSharePath, CancellationToken.None);
            Check(live.ExpectedSize > 50 * 1024 * 1024, "Live fixture must be larger than 50 MiB");
            var liveTask = Create("真实授权下载验证", livePath, live);
            liveTask.Start(livePath); ModLoader.LoaderTaskbarAdd(liveTask);
            await Wait(liveTask).WaitAsync(TimeSpan.FromMinutes(6));
            Check(new FileInfo(livePath).Length == live.ExpectedSize, "Actual native API download size mismatch");
            ModLoader.loaderTaskbar.Remove(liveTask);
            Console.WriteLine("LIVE PASS: native authorized archive bytes=" + live.ExpectedSize);
        }
        Console.WriteLine("PASS: " + _checks + " actual native downloader/task checks (progress, speed, headers, cancellation, redirects, size and errors)");
    }

    private static async Task Until(Func<bool> condition, string error)
    {
        var until = DateTime.UtcNow.AddSeconds(10);
        while (!condition()) { if (DateTime.UtcNow >= until) throw new Exception(error); await Task.Delay(20); }
    }

    private static void InitializeDefaults()
    {
        var service = typeof(PCL.Core.App.Configuration.ConfigService);
        const BindingFlags flags = BindingFlags.Static | BindingFlags.NonPublic;
        service.GetMethod("_InitializeConfigItems", flags)!.Invoke(null, null);
        service.GetField("_isConfigItemsInitialized", flags)!.SetValue(null, true);
        var items = (System.Collections.IDictionary)service.GetField("_Items", flags)!.GetValue(null)!;
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

    private sealed class FixtureServer : IDisposable
    {
        private readonly HttpListener _listener = new();
        private readonly CancellationTokenSource _stop = new();
        private readonly Task _worker;
        internal readonly ConcurrentDictionary<string, string> Cookies = new();
        private readonly ConcurrentDictionary<string, int> _counts = new();
        internal string Url { get; }
        internal int Count(string path) => _counts.GetValueOrDefault(path);
        internal FixtureServer()
        {
            var socket = new TcpListener(IPAddress.Loopback, 0); socket.Start(); var port = ((IPEndPoint)socket.LocalEndpoint).Port; socket.Stop();
            Url = "http://127.0.0.1:" + port + "/"; _listener.Prefixes.Add(Url); _listener.Start();
            _worker = Task.Run(async () =>
            {
                while (!_stop.IsCancellationRequested)
                {
                    HttpListenerContext context;
                    try { context = await _listener.GetContextAsync(); } catch { break; }
                    _ = Serve(context);
                }
            });
        }
        private async Task Serve(HttpListenerContext context)
        {
            var path = context.Request.Url!.AbsolutePath;
            _counts.AddOrUpdate(path, 1, (_, count) => count + 1); Cookies[path] = context.Request.Headers["Cookie"] ?? "";
            try
            {
                if (path == "/redirect") { context.Response.Redirect(Url + "leak"); return; }
                if (path == "/denied") { context.Response.StatusCode = 412; return; }
                if ((path is "/protected" or "/slow") && Cookies[path] != "x_pan_access_token=" + Secret) { context.Response.StatusCode = 401; return; }
                var size = path is "/protected" or "/slow" ? 8 * 1024 * 1024 : 1024;
                context.Response.ContentLength64 = size;
                var buffer = new byte[64 * 1024]; Array.Fill<byte>(buffer, 42);
                for (var remaining = size; remaining > 0; remaining -= buffer.Length)
                {
                    await context.Response.OutputStream.WriteAsync(buffer.AsMemory(0, Math.Min(remaining, buffer.Length)), _stop.Token);
                    if (size > 1024) { await context.Response.OutputStream.FlushAsync(); await Task.Delay(15, _stop.Token); }
                }
            }
            catch (Exception ex) when (ex is IOException or HttpListenerException or OperationCanceledException or ObjectDisposedException) { }
            finally { context.Response.Close(); }
        }
        public void Dispose() { _stop.Cancel(); _listener.Stop(); _listener.Close(); _worker.GetAwaiter().GetResult(); _stop.Dispose(); }
    }
}
