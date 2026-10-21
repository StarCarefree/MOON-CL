// net48 WinExe. Compiled by Publish-SingleExe.ps1 with the SDK csc.
// The app bytes are a normal embedded resource, not a tail appended after the PE.
// Appending a large payload is the SilverFox loader shape (small stub + huge overlay).
// Download happens only after the user clicks agree.
using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using Microsoft.Win32;

[assembly: AssemblyTitle("PCL-MOON")]
[assembly: AssemblyProduct("PCL-MOON")]
[assembly: AssemblyCompany("PCL-MOON")]
[assembly: AssemblyDescription("Minecraft launcher")]
[assembly: AssemblyCopyright("Copyright")]

internal static class Program
{
    private const string ReleasesUrl = "https://builds.dotnet.microsoft.com/dotnet/release-metadata/10.0/releases.json";
    private const string ResourceName = "PCL-MOON.App";
    private const string WebViewSetupUrl = "https://go.microsoft.com/fwlink/p/?LinkId=2124708";

    private static readonly string[] TrustedSuffixes = new string[]
    {
        "microsoft.com", "windows.net", "aka.ms", "visualstudio.com", "azure.com",
        "msecnd.net", "azureedge.net", "msft.net", "dot.net"
    };

    [STAThread]
    private static int Main(string[] args)
    {
        var selfCheck = HasFlag(args, "--self-check");
        // WinForms 的视觉样式与控件渲染初始化开销不小，而绝大多数启动只需要
        // 「校验 -> 解压 -> 拉起」这条快路径，根本不显示任何窗口。
        // 因此推迟到真正要创建窗口时再初始化，见 EnsureVisualStyles。
        try { ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12; } catch { }
        try
        {
            var host = new SingleExeHost();
            if (selfCheck) return host.SelfCheck();
            return host.Run(args);
        }
        catch (Exception ex)
        {
            if (selfCheck)
            {
                try { Console.Error.WriteLine(ex.ToString()); } catch { }
                return 1;
            }
            EnsureVisualStyles();
            MessageBox.Show(ex.Message, "PCL-MOON", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return 1;
        }
    }

    private static bool _visualStylesReady;

    /// <summary>
    /// 在第一次创建窗口之前初始化 WinForms 视觉样式。重复调用无副作用。
    /// </summary>
    private static void EnsureVisualStyles()
    {
        if (_visualStylesReady) return;
        _visualStylesReady = true;
        try
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
        }
        catch { }
    }

    private static bool HasFlag(string[] args, string flag)
    {
        if (args == null) return false;
        for (var i = 0; i < args.Length; i++)
        {
            if (string.Equals(args[i], flag, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    private sealed class RuntimeNeed
    {
        public string Key;
        public string Title;
        public string Detail;
        public bool Blocking;
    }

    private sealed class SingleExeHost
    {
        private string _extractDir;
        private string _payloadHash;

        public int SelfCheck()
        {
            _payloadHash = BuildInfo.PayloadSha256;
            using (var stream = OpenApp())
            {
                VerifyHash(stream);
                stream.Position = 0;
                var dir = Path.Combine(Path.GetTempPath(), "pcl-moon-self-check-" + Process.GetCurrentProcess().Id);
                try
                {
                    if (Directory.Exists(dir)) Directory.Delete(dir, true);
                    Directory.CreateDirectory(dir);
                    ExtractZip(stream, dir);
                    if (!File.Exists(Path.Combine(dir, "Plain Craft Launcher 2.exe")))
                        throw new InvalidOperationException("self-check missing launcher");
                }
                finally
                {
                    try { if (Directory.Exists(dir)) Directory.Delete(dir, true); } catch { }
                }
            }
            return 0;
        }

        public int Run(string[] args)
        {
            _payloadHash = BuildInfo.PayloadSha256;
            _extractDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "PCL-MOON", "single", _payloadHash);
            var missing = CollectMissing();
            if (missing.Count > 0)
            {
                EnsureVisualStyles();
                using (var form = new ConsentForm(this, args, missing))
                {
                    Application.Run(form);
                    return form.ExitCode;
                }
            }
            ExtractIfNeeded(null);
            Launch(args, ShouldClearRoot());
            return 0;
        }

        public List<RuntimeNeed> CollectMissing()
        {
            var list = new List<RuntimeNeed>();
            var vc = MissingVcFiles();
            if (vc.Count > 0)
            {
                var item = new RuntimeNeed();
                item.Key = "vcredist";
                item.Blocking = true;
                item.Title = "Visual C++ 2015-2022 运行库（" + BuildInfo.Arch + "）";
                item.Detail = "系统目录缺少 " + JoinNames(vc) + "。没有这些文件，启动器打不开。";
                list.Add(item);
            }
            if (ResolvePlan(BuildInfo.Arch) == "NeedConsent")
            {
                var item = new RuntimeNeed();
                item.Key = "dotnet";
                item.Blocking = true;
                item.Title = ".NET 10 Desktop Runtime（" + BuildInfo.Arch + "）";
                item.Detail = "缺少 Microsoft.NETCore.App 10 与 Microsoft.WindowsDesktop.App 10。没有这两套运行库，启动器打不开。";
                list.Add(item);
            }
            if (!BuildInfo.WebViewBundled && !HasWebView2Runtime())
            {
                var item = new RuntimeNeed();
                item.Key = "webview2";
                item.Blocking = false;
                item.Title = "Microsoft Edge WebView2 Runtime";
                item.Detail = "未检测到系统 WebView2，且这个包没有附带固定版本。没有它，内置浏览页打不开。装好上面的必要组件后，启动器本体仍可打开。";
                list.Add(item);
            }
            return list;
        }

        public void DownloadMissingAfterConsent(IProgressSink sink, List<RuntimeNeed> missing)
        {
            if (sink == null || !sink.ConsentAccepted)
                throw new InvalidOperationException("consent required");
            for (var i = 0; i < missing.Count; i++)
            {
                var key = missing[i].Key;
                if (key == "vcredist")
                {
                    if (!HasVcRuntime()) DownloadVc(sink);
                }
                else if (key == "dotnet")
                {
                    if (ResolvePlan(BuildInfo.Arch) == "NeedConsent") DownloadDesktopRuntimeAfterConsent(sink);
                }
                else if (key == "webview2")
                {
                    if (!HasWebView2Runtime()) DownloadWebView(sink);
                }
            }
        }

        public void DownloadDesktopRuntimeAfterConsent(IProgressSink sink)
        {
            if (sink == null || !sink.ConsentAccepted)
                throw new InvalidOperationException("consent required");
            var spec = TryReadReleasesSpec();
            if (spec == null)
            {
                spec = new InstallerSpec();
                spec.Url = AkaUrl(BuildInfo.Arch);
                spec.Hash = "";
                spec.Name = "aka.ms";
            }
            var destDir = Path.Combine(Path.GetTempPath(), "pcl-cy-dotnet", BuildInfo.Arch);
            Directory.CreateDirectory(destDir);
            var dest = Path.Combine(destDir, "windowsdesktop-runtime.exe");
            sink.Status("正在从微软官方地址下载 .NET 10 Desktop Runtime…");
            SaveHttps(spec.Url, dest, sink, AssertDesktopUrl);
            AssertPe(dest, 1024 * 1024);
            if (!string.IsNullOrEmpty(spec.Hash))
            {
                sink.Status("正在校验 .NET 安装包…");
                AssertSha512(dest, spec.Hash);
            }
            sink.Status("正在安装 .NET 10。若系统询问权限，请允许。");
            var code = Install(dest, "/install /passive /norestart", true);
            if (!WaitFor(delegate { return HasRegisteredDesktop10(BuildInfo.Arch); }) && code != 0 && code != 3010 && code != 1638)
                throw new InvalidOperationException("安装未完成，退出码 " + code + "。未继续启动。");
            if (!WaitFor(delegate { return HasRegisteredDesktop10(BuildInfo.Arch); }))
                throw new InvalidOperationException("安装程序已结束，但仍未检测到 .NET 10 Desktop Runtime。");
            try { File.Delete(dest); } catch { }
        }

        private void DownloadVc(IProgressSink sink)
        {
            var arch = BuildInfo.Arch;
            var url = arch == "arm64"
                ? "https://aka.ms/vs/17/release/vc_redist.arm64.exe"
                : "https://aka.ms/vs/17/release/vc_redist.x64.exe";
            var destDir = Path.Combine(Path.GetTempPath(), "pcl-cy-vcredist", arch);
            Directory.CreateDirectory(destDir);
            var dest = Path.Combine(destDir, "vc_redist." + arch + ".exe");
            sink.Status("正在从微软官方地址下载 Visual C++ 2015-2022…");
            SaveHttps(url, dest, sink, AssertVcUrl);
            AssertPe(dest, 1024 * 1024);
            sink.Status("正在安装 Visual C++。若系统询问权限，请允许。");
            var code = Install(dest, "/install /passive /norestart", true);
            if (!WaitFor(HasVcRuntime) && code != 0 && code != 3010 && code != 1638)
                throw new InvalidOperationException("Visual C++ 安装未完成，退出码 " + code + "。未继续启动。");
            if (!WaitFor(HasVcRuntime))
                throw new InvalidOperationException("安装程序已结束，但仍未检测到 vcruntime140.dll。");
            try { File.Delete(dest); } catch { }
        }

        private void DownloadWebView(IProgressSink sink)
        {
            var destDir = Path.Combine(Path.GetTempPath(), "pcl-cy-webview2");
            Directory.CreateDirectory(destDir);
            var dest = Path.Combine(destDir, "MicrosoftEdgeWebview2Setup.exe");
            sink.Status("正在从微软官方地址下载 WebView2 Runtime…");
            SaveHttps(WebViewSetupUrl, dest, sink, AssertWebViewUrl);
            AssertPe(dest, 64 * 1024);
            sink.Status("正在安装 WebView2。若系统询问权限，请允许。");
            var code = InstallWebView(dest);
            if (!WaitFor(HasWebView2Runtime) && code != 0 && code != 3010 && code != 1638)
                throw new InvalidOperationException("WebView2 安装未完成，退出码 " + code + "。未继续启动。");
            if (!WaitFor(HasWebView2Runtime))
                throw new InvalidOperationException("安装程序已结束，但仍未检测到 WebView2 Runtime。");
            try { File.Delete(dest); } catch { }
        }

        public void ExtractIfNeeded(IProgressSink sink)
        {
            var stampPath = Path.Combine(_extractDir, ".ready");
            var preferredReady = Path.Combine(_extractDir, "Plain Craft Launcher 2.exe");
            if (File.Exists(stampPath) && File.Exists(preferredReady))
            {
                var have = File.ReadAllText(stampPath).Trim();
                if (string.Equals(have, _payloadHash, StringComparison.OrdinalIgnoreCase))
                    return;
            }
            if (sink != null) sink.Status("正在准备启动文件…");
            Action extract = delegate
            {
                using (var stream = OpenApp())
                {
                    VerifyHash(stream);
                    stream.Position = 0;
                    if (Directory.Exists(_extractDir))
                    {
                        try { Directory.Delete(_extractDir, true); } catch { }
                    }
                    Directory.CreateDirectory(_extractDir);
                    ExtractZip(stream, _extractDir);
                }
                if (!File.Exists(LauncherPath()))
                    throw new InvalidOperationException("包内缺少启动器。");
                File.WriteAllText(Path.Combine(_extractDir, ".ready"), _payloadHash);
            };
            if (sink != null) extract();
            else PrepareWindow.ShowWhile(extract);
        }

        private static Stream OpenApp()
        {
            var stream = typeof(Program).Assembly.GetManifestResourceStream(ResourceName);
            if (stream == null)
                throw new InvalidOperationException("这个 exe 不完整，缺少启动文件。");
            if (!stream.CanSeek)
            {
                var copy = new MemoryStream();
                stream.CopyTo(copy);
                stream.Dispose();
                copy.Position = 0;
                return copy;
            }
            stream.Position = 0;
            return stream;
        }

        private static void VerifyHash(Stream stream)
        {
            stream.Position = 0;
            string actual;
            using (var sha = SHA256.Create())
                actual = ToHex(sha.ComputeHash(stream));
            if (!string.Equals(actual, BuildInfo.PayloadSha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("启动文件校验失败，文件可能已损坏。");
            stream.Position = 0;
        }

        private static void ExtractZip(Stream stream, string destDir)
        {
            using (var zip = new ZipArchive(stream, ZipArchiveMode.Read, true))
            {
                foreach (var entry in zip.Entries)
                {
                    var name = entry.FullName.Replace('/', Path.DirectorySeparatorChar);
                    if (string.IsNullOrEmpty(name) || name.EndsWith(Path.DirectorySeparatorChar.ToString()))
                        continue;
                    if (name.StartsWith("..") || Path.IsPathRooted(name))
                        throw new InvalidOperationException("拒绝解压异常路径: " + name);
                    var dest = Path.GetFullPath(Path.Combine(destDir, name));
                    var root = Path.GetFullPath(destDir) + Path.DirectorySeparatorChar;
                    if (!dest.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidOperationException("拒绝写出目录: " + name);
                    Directory.CreateDirectory(Path.GetDirectoryName(dest));
                    using (var input = entry.Open())
                    using (var output = File.Create(dest))
                        input.CopyTo(output);
                }
            }
        }

        public void Launch(string[] args, bool clearRoot)
        {
            var exe = LauncherPath();
            var psi = new ProcessStartInfo();
            psi.FileName = exe;
            psi.WorkingDirectory = Path.GetDirectoryName(exe);
            psi.UseShellExecute = false;
            psi.Arguments = JoinArgs(args);
            var bundle = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PCL-MOON", "bundle");
            Directory.CreateDirectory(bundle);
            psi.EnvironmentVariables["DOTNET_BUNDLE_EXTRACT_BASE_DIR"] = bundle;
            psi.EnvironmentVariables["DOTNET_TieredPGO"] = "1";
            psi.EnvironmentVariables["DOTNET_TC_QuickJit"] = "1";
            psi.EnvironmentVariables["DOTNET_TC_QuickJitForLoops"] = "1";
            psi.EnvironmentVariables["DOTNET_ReadyToRun"] = "1";
            // 外壳与解压目录路径，让内层程序把数据目录定位到外壳所在目录
            psi.EnvironmentVariables["PCL_MOON_HOST_EXE"] = Process.GetCurrentProcess().MainModule.FileName;
            psi.EnvironmentVariables["PCL_MOON_HOST_PAYLOAD"] = Path.GetDirectoryName(exe);
            if (clearRoot)
            {
                string[] names = new string[] { "DOTNET_ROOT", "DOTNET_ROOT_X64", "DOTNET_ROOT_ARM64", "DOTNET_ROOT_X86" };
                for (var i = 0; i < names.Length; i++)
                {
                    if (psi.EnvironmentVariables.ContainsKey(names[i]))
                        psi.EnvironmentVariables.Remove(names[i]);
                }
            }
            Process.Start(psi);
        }

        public bool ShouldClearRoot()
        {
            return ResolvePlan(BuildInfo.Arch) == "ClearEnvAndLaunch";
        }

        private string LauncherPath()
        {
            var preferred = Path.Combine(_extractDir, "Plain Craft Launcher 2.exe");
            if (File.Exists(preferred)) return preferred;
            var hits = Directory.GetFiles(_extractDir, "*.exe", SearchOption.TopDirectoryOnly);
            if (hits.Length == 1) return hits[0];
            throw new InvalidOperationException("解压后找不到启动器 exe。");
        }

        private InstallerSpec TryReadReleasesSpec()
        {
            try
            {
                var json = DownloadString(ReleasesUrl);
                return ParseReleases(json, BuildInfo.Arch);
            }
            catch
            {
                return null;
            }
        }

        private static InstallerSpec ParseReleases(string json, string arch)
        {
            var ser = new JavaScriptSerializer();
            ser.MaxJsonLength = 32 * 1024 * 1024;
            var root = ser.DeserializeObject(json) as Dictionary<string, object>;
            if (root == null) throw new InvalidOperationException("releases.json");
            var latest = root["latest-release"] as string;
            var releases = root["releases"] as ArrayList;
            if (string.IsNullOrEmpty(latest) || releases == null)
                throw new InvalidOperationException("releases.json 缺少 latest-release。");
            Dictionary<string, object> rel = null;
            foreach (var item in releases)
            {
                var row = item as Dictionary<string, object>;
                if (row == null) continue;
                if ((row["release-version"] as string) == latest)
                {
                    rel = row;
                    break;
                }
            }
            if (rel == null) throw new InvalidOperationException("找不到 latest-release。");
            var desktop = rel["windowsdesktop"] as Dictionary<string, object>;
            var files = desktop == null ? null : desktop["files"] as ArrayList;
            if (files == null) throw new InvalidOperationException("releases.json 没有 windowsdesktop。");
            var wanted = "windowsdesktop-runtime-win-" + arch + ".exe";
            foreach (var item in files)
            {
                var file = item as Dictionary<string, object>;
                if (file == null) continue;
                var name = file["name"] as string;
                if (name != null && name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) continue;
                if ((file["rid"] as string) == "win-" + arch && name == wanted)
                {
                    var spec = new InstallerSpec();
                    spec.Url = file["url"] as string;
                    spec.Hash = ((file["hash"] as string) ?? "").Trim().ToLowerInvariant();
                    spec.Name = name;
                    AssertDesktopUrl(spec.Url);
                    if (spec.Hash.Length != 128)
                        throw new InvalidOperationException("安装包哈希不是 SHA512。");
                    return spec;
                }
            }
            throw new InvalidOperationException("releases.json 没有 " + wanted);
        }

        private static string AkaUrl(string arch)
        {
            if (arch == "arm64") return "https://aka.ms/dotnet/10.0/windowsdesktop-runtime-win-arm64.exe";
            if (arch == "x64") return "https://aka.ms/dotnet/10.0/windowsdesktop-runtime-win-x64.exe";
            throw new InvalidOperationException("不支持的架构: " + arch);
        }

        private string SaveHttps(string url, string dest, IProgressSink sink, Action<string> validateFinal)
        {
            var current = url;
            for (var hop = 0; hop < 8; hop++)
            {
                AssertHttps(current, false);
                var req = (HttpWebRequest)WebRequest.Create(current);
                req.AllowAutoRedirect = false;
                req.UserAgent = "PCL-MOON/2.15";
                req.Timeout = 180000;
                req.ReadWriteTimeout = 180000;
                HttpWebResponse resp = null;
                try
                {
                    try { resp = (HttpWebResponse)req.GetResponse(); }
                    catch (WebException ex)
                    {
                        resp = ex.Response as HttpWebResponse;
                        if (resp == null) throw;
                    }
                    var status = (int)resp.StatusCode;
                    if (status >= 300 && status < 400)
                    {
                        var loc = resp.Headers["Location"];
                        if (string.IsNullOrEmpty(loc)) throw new InvalidOperationException("重定向缺少 Location。");
                        current = new Uri(new Uri(current), loc).AbsoluteUri;
                        continue;
                    }
                    if (status != 200) throw new InvalidOperationException("HTTP " + status);
                    if (validateFinal != null) validateFinal(current);
                    var total = resp.ContentLength;
                    using (var input = resp.GetResponseStream())
                    using (var output = File.Create(dest))
                    {
                        var buffer = new byte[1024 * 128];
                        long done = 0;
                        int n;
                        while ((n = input.Read(buffer, 0, buffer.Length)) > 0)
                        {
                            output.Write(buffer, 0, n);
                            done += n;
                            if (sink != null && total > 0)
                                sink.Progress((int)Math.Min(100, done * 100L / total));
                        }
                    }
                    return current;
                }
                finally
                {
                    if (resp != null) resp.Close();
                }
            }
            throw new InvalidOperationException("重定向过多。");
        }

        private static string DownloadString(string url)
        {
            AssertHttps(url, false);
            var req = (HttpWebRequest)WebRequest.Create(url);
            req.AllowAutoRedirect = false;
            req.UserAgent = "PCL-MOON/2.15";
            req.Timeout = 60000;
            using (var resp = (HttpWebResponse)req.GetResponse())
            using (var reader = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
                return reader.ReadToEnd();
        }

        private static void AssertDesktopUrl(string url)
        {
            var uri = AssertHttps(url, true);
            if (!string.Equals(uri.Host, "builds.dotnet.microsoft.com", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("安装包必须来自 builds.dotnet.microsoft.com。");
            var name = Path.GetFileName(uri.AbsolutePath);
            var prefix = "windowsdesktop-runtime-10.0.";
            var suffix = "-win-" + BuildInfo.Arch + ".exe";
            if (!name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ||
                !name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("安装包文件名不符合 .NET 10 Desktop Runtime: " + name);
        }

        private static void AssertVcUrl(string url)
        {
            var uri = AssertHttps(url, true);
            var name = Path.GetFileName(uri.AbsolutePath);
            var expected = "vc_redist." + BuildInfo.Arch + ".exe";
            if (!string.Equals(name, expected, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Visual C++ 安装包文件名不符合: " + name);
        }

        private static void AssertWebViewUrl(string url)
        {
            var uri = AssertHttps(url, true);
            var name = Path.GetFileName(uri.AbsolutePath);
            if (name.IndexOf("MicrosoftEdgeWebview2", StringComparison.OrdinalIgnoreCase) < 0 &&
                name.IndexOf("MicrosoftEdgeWebView2", StringComparison.OrdinalIgnoreCase) < 0)
                throw new InvalidOperationException("WebView2 安装包文件名不符合: " + name);
        }

        private static Uri AssertHttps(string url, bool installer)
        {
            if (string.IsNullOrEmpty(url)) throw new InvalidOperationException("URL 为空。");
            Uri uri;
            if (!Uri.TryCreate(url, UriKind.Absolute, out uri))
                throw new InvalidOperationException("不是绝对 URL。");
            if (uri.Scheme != "https") throw new InvalidOperationException("只允许 https。");
            if (uri.UserInfo.Length > 0 || !uri.IsDefaultPort)
                throw new InvalidOperationException("拒绝带用户信息或非默认端口的 URL。");
            if (!IsTrusted(uri.Host)) throw new InvalidOperationException("下载域不受信任: " + uri.Host);
            if (installer && uri.AbsolutePath.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("拒绝把压缩包当作安装包。");
            return uri;
        }

        private static bool IsTrusted(string host)
        {
            if (string.IsNullOrEmpty(host)) return false;
            host = host.Trim().TrimEnd('.').ToLowerInvariant();
            for (var i = 0; i < TrustedSuffixes.Length; i++)
            {
                var suffix = TrustedSuffixes[i];
                if (host == suffix || host.EndsWith("." + suffix, StringComparison.Ordinal)) return true;
            }
            return false;
        }

        private static void AssertSha512(string path, string expected)
        {
            var want = expected.Trim().ToLowerInvariant();
            if (want.Length != 128) throw new InvalidOperationException("SHA512 长度不对。");
            string actual;
            using (var sha = SHA512.Create())
            using (var fs = File.OpenRead(path))
                actual = ToHex(sha.ComputeHash(fs));
            if (!string.Equals(actual, want, StringComparison.Ordinal))
            {
                try { File.Delete(path); } catch { }
                throw new InvalidOperationException("安装包 SHA512 不一致，已删除。");
            }
        }

        private static void AssertPe(string path, int minBytes)
        {
            using (var fs = File.OpenRead(path))
            {
                if (fs.Length < minBytes) throw new InvalidOperationException("安装包过小。");
                var buf = new byte[2];
                if (fs.Read(buf, 0, 2) != 2 || buf[0] != 0x4D || buf[1] != 0x5A)
                    throw new InvalidOperationException("安装包不是 PE。");
            }
        }

        private static int InstallWebView(string path)
        {
            var code = -1;
            try { code = Install(path, "/silent /install", false); }
            catch (InvalidOperationException) { code = -1; }
            if (HasWebView2Runtime()) return 0;
            return Install(path, "/silent /install", true);
        }

        private static int Install(string path, string arguments, bool elevate)
        {
            try
            {
                var psi = new ProcessStartInfo();
                psi.FileName = path;
                psi.Arguments = arguments;
                psi.UseShellExecute = true;
                if (elevate) psi.Verb = "runas";
                var proc = Process.Start(psi);
                if (proc == null) throw new InvalidOperationException("无法启动安装程序。");
                proc.WaitForExit();
                return proc.ExitCode;
            }
            catch (System.ComponentModel.Win32Exception)
            {
                if (!elevate) throw new InvalidOperationException("无法启动安装程序。");
                throw new InvalidOperationException("未获得管理员权限，已取消安装。没有改动系统组件。");
            }
        }

        private static string ResolvePlan(string arch)
        {
            string envName;
            var envPath = ReadEnvRoot(arch, out envName);
            var envSet = !string.IsNullOrEmpty(envPath);
            var envOk = envSet && HasDesktop10(envPath);
            var registered = HasRegisteredDesktop10(arch);
            if (envSet && envOk) return "Launch";
            if (registered && envSet && !envOk) return "ClearEnvAndLaunch";
            if (registered) return "Launch";
            return "NeedConsent";
        }

        private static string ReadEnvRoot(string arch, out string name)
        {
            var specific = arch == "arm64" ? "DOTNET_ROOT_ARM64" : "DOTNET_ROOT_X64";
            var value = Environment.GetEnvironmentVariable(specific);
            if (!string.IsNullOrEmpty(value))
            {
                name = specific;
                return value.Trim();
            }
            value = Environment.GetEnvironmentVariable("DOTNET_ROOT");
            if (!string.IsNullOrEmpty(value))
            {
                name = "DOTNET_ROOT";
                return value.Trim();
            }
            name = null;
            return null;
        }

        private static bool HasRegisteredDesktop10(string arch)
        {
            var roots = new List<string>();
            var loc = ReadInstallLocation(arch);
            if (!string.IsNullOrEmpty(loc)) roots.Add(loc.TrimEnd('\\'));
            var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            if (!string.IsNullOrEmpty(programFiles))
            {
                roots.Add(Path.Combine(programFiles, "dotnet"));
                if (arch == "x64") roots.Add(Path.Combine(programFiles, "dotnet", "x64"));
            }
            for (var i = 0; i < roots.Count; i++)
            {
                if (HasDesktop10(roots[i])) return true;
            }
            return false;
        }

        private static string ReadInstallLocation(string arch)
        {
            using (var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64))
            using (var key = baseKey.OpenSubKey(@"SOFTWARE\dotnet\Setup\InstalledVersions\" + arch))
            {
                if (key == null) return null;
                return key.GetValue("InstallLocation") as string;
            }
        }

        private static bool WaitFor(Func<bool> probe)
        {
            for (var i = 0; i < 12; i++)
            {
                if (probe()) return true;
                Thread.Sleep(500);
            }
            return false;
        }

        private static bool HasDesktop10(string root)
        {
            return HasFramework10(root, "Microsoft.NETCore.App") && HasFramework10(root, "Microsoft.WindowsDesktop.App");
        }

        private static bool HasFramework10(string root, string name)
        {
            if (string.IsNullOrEmpty(root)) return false;
            var dir = Path.Combine(root, "shared", name);
            if (!Directory.Exists(dir)) return false;
            var hits = Directory.GetDirectories(dir, "10.*");
            return hits.Length > 0;
        }

        private static List<string> MissingVcFiles()
        {
            var missing = new List<string>();
            var sys = Environment.SystemDirectory;
            string[] names = new string[] { "vcruntime140.dll", "vcruntime140_1.dll", "msvcp140.dll" };
            for (var i = 0; i < names.Length; i++)
            {
                if (!File.Exists(Path.Combine(sys, names[i]))) missing.Add(names[i]);
            }
            return missing;
        }

        private static bool HasVcRuntime()
        {
            return MissingVcFiles().Count == 0;
        }

        private static bool HasWebView2Runtime()
        {
            if (ReadWebViewPv(RegistryHive.LocalMachine, RegistryView.Registry64)) return true;
            if (ReadWebViewPv(RegistryHive.LocalMachine, RegistryView.Registry32)) return true;
            if (ReadWebViewPv(RegistryHive.CurrentUser, RegistryView.Default)) return true;
            string[] roots = new string[]
            {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), @"Microsoft\EdgeWebView\Application"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), @"Microsoft\EdgeWebView\Application"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Microsoft\EdgeWebView\Application")
            };
            for (var i = 0; i < roots.Length; i++)
            {
                if (!Directory.Exists(roots[i])) continue;
                var dirs = Directory.GetDirectories(roots[i]);
                for (var n = 0; n < dirs.Length; n++)
                {
                    if (File.Exists(Path.Combine(dirs[n], "msedgewebview2.exe"))) return true;
                }
            }
            return false;
        }

        private static bool ReadWebViewPv(RegistryHive hive, RegistryView view)
        {
            try
            {
                using (var baseKey = RegistryKey.OpenBaseKey(hive, view))
                using (var key = baseKey.OpenSubKey(@"SOFTWARE\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}"))
                {
                    if (key == null) return false;
                    var pv = key.GetValue("pv") as string;
                    return !string.IsNullOrEmpty(pv) && pv != "0.0.0.0";
                }
            }
            catch
            {
                return false;
            }
        }

        private static string JoinNames(List<string> names)
        {
            return string.Join("、", names.ToArray());
        }

        private static string JoinArgs(string[] args)
        {
            if (args == null || args.Length == 0) return "";
            var parts = new StringBuilder();
            for (var i = 0; i < args.Length; i++)
            {
                if (i > 0) parts.Append(' ');
                var item = args[i] ?? "";
                if (item.IndexOfAny(new char[] { ' ', '\t', '"' }) >= 0)
                    parts.Append('"').Append(item.Replace("\"", "\\\"")).Append('"');
                else
                    parts.Append(item);
            }
            return parts.ToString();
        }

        private static string ToHex(byte[] bytes)
        {
            var sb = new StringBuilder(bytes.Length * 2);
            for (var i = 0; i < bytes.Length; i++) sb.Append(bytes[i].ToString("x2"));
            return sb.ToString();
        }
    }

    private sealed class InstallerSpec
    {
        public string Url;
        public string Hash;
        public string Name;
    }

    internal interface IProgressSink
    {
        bool ConsentAccepted { get; }
        void Status(string text);
        void Progress(int percent);
    }

    private sealed class ConsentForm : Form, IProgressSink
    {
        private readonly SingleExeHost _host;
        private readonly string[] _args;
        private readonly List<RuntimeNeed> _missing;
        private readonly Label _status;
        private readonly ProgressBar _bar;
        private readonly Button _agree;
        private readonly Button _cancel;
        private readonly Button _skip;
        private bool _busy;
        public int ExitCode = 2;
        public bool ConsentAccepted { get; private set; }

        public ConsentForm(SingleExeHost host, string[] args, List<RuntimeNeed> missing)
        {
            _host = host;
            _args = args;
            _missing = missing;
            Text = "PCL-MOON";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(560, 340);
            Font = new Font("Microsoft YaHei UI", 9f);
            var blocking = false;
            for (var i = 0; i < missing.Count; i++)
            {
                if (missing[i].Blocking) blocking = true;
            }
            var title = new Label();
            title.Text = "启动前缺少这些组件";
            title.Font = new Font(Font, FontStyle.Bold);
            title.AutoSize = false;
            title.Bounds = new Rectangle(16, 12, 528, 24);
            var body = new TextBox();
            body.Multiline = true;
            body.ScrollBars = ScrollBars.Vertical;
            body.ReadOnly = true;
            body.BorderStyle = BorderStyle.None;
            body.BackColor = SystemColors.Control;
            body.TabStop = false;
            body.Bounds = new Rectangle(16, 40, 528, 176);
            body.Text = Describe(missing, blocking);
            _bar = new ProgressBar();
            _bar.Bounds = new Rectangle(16, 224, 528, 18);
            _bar.Minimum = 0;
            _bar.Maximum = 100;
            _status = new Label();
            _status.Text = "等待你的选择。未点同意不会下载。";
            _status.AutoSize = false;
            _status.Bounds = new Rectangle(16, 248, 528, 36);
            _agree = new Button();
            _agree.Text = "同意并下载";
            _agree.Bounds = new Rectangle(332, 296, 110, 28);
            _agree.Click += OnAgree;
            _cancel = new Button();
            _cancel.Text = "取消";
            _cancel.Bounds = new Rectangle(450, 296, 92, 28);
            _cancel.Click += delegate { ExitCode = 2; Close(); };
            CancelButton = _cancel;
            AcceptButton = null;
            Controls.Add(title);
            Controls.Add(body);
            Controls.Add(_bar);
            Controls.Add(_status);
            Controls.Add(_agree);
            Controls.Add(_cancel);
            if (!blocking)
            {
                _skip = new Button();
                _skip.Text = "跳过并启动";
                _skip.Bounds = new Rectangle(16, 296, 110, 28);
                _skip.Click += OnSkip;
                Controls.Add(_skip);
            }
        }

        private static string Describe(List<RuntimeNeed> missing, bool blocking)
        {
            var sb = new StringBuilder();
            sb.Append("这台电脑还缺下面的组件。必要项不装，启动器打不开。");
            sb.Append("\r\n\r\n");
            for (var i = 0; i < missing.Count; i++)
            {
                var item = missing[i];
                sb.Append(item.Blocking ? "• 必要  " : "• 浏览页  ");
                sb.Append(item.Title);
                sb.Append("\r\n  ");
                sb.Append(item.Detail);
                sb.Append("\r\n\r\n");
            }
            sb.Append("点「同意并下载」后，才会从微软官方地址下载并安装上面列出的组件，然后打开启动器。");
            sb.Append("\r\n点「取消」或关闭窗口不会下载，也不会启动。");
            if (!blocking)
                sb.Append("\r\n只缺浏览页组件时，可以点「跳过并启动」。");
            return sb.ToString();
        }

        public void Status(string text)
        {
            if (InvokeRequired) BeginInvoke(new Action<string>(Status), text);
            else _status.Text = text;
        }

        public void Progress(int percent)
        {
            if (InvokeRequired) BeginInvoke(new Action<int>(Progress), percent);
            else _bar.Value = Math.Max(0, Math.Min(100, percent));
        }

        private void OnAgree(object sender, EventArgs e)
        {
            if (_busy) return;
            _busy = true;
            ConsentAccepted = true;
            SetBusy(true);
            ThreadPool.QueueUserWorkItem(delegate
            {
                try
                {
                    _host.DownloadMissingAfterConsent(this, _missing);
                    _host.ExtractIfNeeded(this);
                    _host.Launch(_args, _host.ShouldClearRoot());
                    ExitCode = 0;
                    BeginInvoke(new Action(Close));
                }
                catch (Exception ex)
                {
                    BeginInvoke(new Action(delegate
                    {
                        _busy = false;
                        ConsentAccepted = false;
                        SetBusy(false);
                        _status.Text = ex.Message;
                        MessageBox.Show(this, ex.Message, "PCL-MOON", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    }));
                }
            });
        }

        private void OnSkip(object sender, EventArgs e)
        {
            if (_busy) return;
            _busy = true;
            SetBusy(true);
            ThreadPool.QueueUserWorkItem(delegate
            {
                try
                {
                    _host.ExtractIfNeeded(this);
                    _host.Launch(_args, _host.ShouldClearRoot());
                    ExitCode = 0;
                    BeginInvoke(new Action(Close));
                }
                catch (Exception ex)
                {
                    BeginInvoke(new Action(delegate
                    {
                        _busy = false;
                        SetBusy(false);
                        _status.Text = ex.Message;
                        MessageBox.Show(this, ex.Message, "PCL-MOON", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    }));
                }
            });
        }

        private void SetBusy(bool busy)
        {
            _agree.Enabled = !busy;
            _cancel.Enabled = !busy;
            if (_skip != null) _skip.Enabled = !busy;
        }
    }

    private static class PrepareWindow
    {
        public static void ShowWhile(Action work)
        {
            Exception error = null;
            EnsureVisualStyles();
            using (var form = new Form())
            {
                form.Text = "PCL-MOON";
                form.FormBorderStyle = FormBorderStyle.FixedDialog;
                form.ControlBox = false;
                form.StartPosition = FormStartPosition.CenterScreen;
                form.ClientSize = new Size(360, 72);
                form.Font = new Font("Microsoft YaHei UI", 9f);
                var label = new Label();
                label.Text = "正在准备启动文件…";
                label.Bounds = new Rectangle(16, 24, 328, 24);
                form.Controls.Add(label);
                form.Shown += delegate
                {
                    ThreadPool.QueueUserWorkItem(delegate
                    {
                        try { work(); }
                        catch (Exception ex) { error = ex; }
                        finally { form.BeginInvoke(new Action(form.Close)); }
                    });
                };
                Application.Run(form);
            }
            if (error != null) throw error;
        }
    }
}
