// net48 WinExe. Compiled by Publish-SingleExe.ps1 with the SDK csc.
// Must not reference .NET 10. Download happens only after the user clicks agree.
using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using Microsoft.Win32;

internal static class Program
{
    private const int FooterSize = 80;
    private const string Magic = "MOONCLPKGv1";
    private const string ReleasesUrl = "https://builds.dotnet.microsoft.com/dotnet/release-metadata/10.0/releases.json";

    private static readonly string[] TrustedSuffixes = new string[]
    {
        "microsoft.com", "windows.net", "aka.ms", "visualstudio.com", "azure.com",
        "msecnd.net", "azureedge.net", "msft.net", "dot.net"
    };

    [STAThread]
    private static int Main(string[] args)
    {
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        try { ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12; } catch { }
        try
        {
            return new SingleExeHost().Run(args);
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "MOONCL", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return 1;
        }
    }

    private sealed class SingleExeHost
    {
        private string _exePath;
        private long _zipOffset;
        private long _zipLength;
        private byte[] _zipHash;
        private string _arch;
        private string _extractDir;

        public int Run(string[] args)
        {
            _exePath = Application.ExecutablePath;
            ReadFooter();
            var plan = ResolvePlan(_arch);
            if (plan == "NeedConsent")
            {
                using (var form = new ConsentForm(this, args))
                {
                    Application.Run(form);
                    return form.ExitCode;
                }
            }
            ExtractIfNeeded(null);
            Launch(args, plan == "ClearEnvAndLaunch");
            return 0;
        }

        public void DownloadDesktopRuntimeAfterConsent(IProgressSink sink)
        {
            // Called only from the agree button. The constructor must not call this.
            if (sink == null || !sink.ConsentAccepted)
                throw new InvalidOperationException("consent required");
            var spec = TryReadReleasesSpec();
            if (spec == null)
            {
                spec = new InstallerSpec();
                spec.Url = AkaUrl(_arch);
                spec.Hash = "";
                spec.Name = "aka.ms";
            }
            var destDir = Path.Combine(Path.GetTempPath(), "pcl-cy-dotnet", _arch);
            Directory.CreateDirectory(destDir);
            var dest = Path.Combine(destDir, "windowsdesktop-runtime.exe");
            sink.Status("正在从微软官方地址下载 .NET 10 Desktop Runtime…");
            var finalUrl = SaveHttps(spec.Url, dest, sink, true);
            AssertPe(dest);
            if (!string.IsNullOrEmpty(spec.Hash))
            {
                sink.Status("正在校验安装包…");
                AssertSha512(dest, spec.Hash);
            }
            sink.Status("正在安装 .NET 10。若系统询问权限，请允许。");
            var code = Install(dest);
            if (!WaitForDesktop10(_arch) && code != 0 && code != 3010 && code != 1638)
                throw new InvalidOperationException("安装未完成，退出码 " + code + "。未继续启动。");
            if (!WaitForDesktop10(_arch))
                throw new InvalidOperationException("安装程序已结束，但仍未检测到 .NET 10 Desktop Runtime。");
            try { File.Delete(dest); } catch { }
        }

        public void ExtractIfNeeded(IProgressSink sink)
        {
            var stampPath = Path.Combine(_extractDir, ".ready");
            var want = ToHex(_zipHash);
            var preferredReady = Path.Combine(_extractDir, "Plain Craft Launcher 2.exe");
            if (File.Exists(stampPath) && File.Exists(preferredReady))
            {
                var have = File.ReadAllText(stampPath).Trim();
                if (string.Equals(have, want, StringComparison.OrdinalIgnoreCase))
                    return;
            }
            if (sink != null) sink.Status("正在准备启动文件…");
            else if (_zipLength > 32L * 1024L * 1024L)
                PrepareWindow.ShowWhile(delegate { ExtractCore(want); });
            else
                ExtractCore(want);
            if (sink != null) ExtractCore(want);
        }

        private void ExtractCore(string want)
        {
            VerifyPayloadHash();
            if (Directory.Exists(_extractDir))
            {
                try { Directory.Delete(_extractDir, true); } catch { }
            }
            Directory.CreateDirectory(_extractDir);
            using (var fs = File.Open(_exePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            using (var slice = new SliceStream(fs, _zipOffset, _zipLength))
            using (var zip = new ZipArchive(slice, ZipArchiveMode.Read, true))
            {
                foreach (var entry in zip.Entries)
                {
                    var name = entry.FullName.Replace('/', Path.DirectorySeparatorChar);
                    if (string.IsNullOrEmpty(name) || name.EndsWith(Path.DirectorySeparatorChar.ToString()))
                        continue;
                    if (name.StartsWith("..") || Path.IsPathRooted(name))
                        throw new InvalidOperationException("拒绝解压异常路径: " + name);
                    var dest = Path.GetFullPath(Path.Combine(_extractDir, name));
                    var root = Path.GetFullPath(_extractDir) + Path.DirectorySeparatorChar;
                    if (!dest.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidOperationException("拒绝写出目录: " + name);
                    Directory.CreateDirectory(Path.GetDirectoryName(dest));
                    using (var input = entry.Open())
                    using (var output = File.Create(dest))
                        input.CopyTo(output);
                }
            }
            if (!File.Exists(LauncherPath()))
                throw new InvalidOperationException("单 exe 内缺少启动器。");
            File.WriteAllText(Path.Combine(_extractDir, ".ready"), want);
        }

        private void VerifyPayloadHash()
        {
            using (var fs = File.Open(_exePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            using (var sha = SHA256.Create())
            {
                fs.Position = _zipOffset;
                var left = _zipLength;
                var buffer = new byte[1024 * 128];
                while (left > 0)
                {
                    var n = fs.Read(buffer, 0, (int)Math.Min(buffer.Length, left));
                    if (n <= 0) throw new InvalidOperationException("载荷读取中断。");
                    sha.TransformBlock(buffer, 0, n, null, 0);
                    left -= n;
                }
                sha.TransformFinalBlock(new byte[0], 0, 0);
                if (!FixedTimeEquals(sha.Hash, _zipHash))
                    throw new InvalidOperationException("单 exe 载荷校验失败，文件可能已损坏。");
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
            var bundle = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MOONCL", "bundle");
            Directory.CreateDirectory(bundle);
            psi.EnvironmentVariables["DOTNET_BUNDLE_EXTRACT_BASE_DIR"] = bundle;
            psi.EnvironmentVariables["DOTNET_TieredPGO"] = "1";
            psi.EnvironmentVariables["DOTNET_TC_QuickJit"] = "1";
            psi.EnvironmentVariables["DOTNET_ReadyToRun"] = "1";
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
            return ResolvePlan(_arch) == "ClearEnvAndLaunch";
        }

        private string LauncherPath()
        {
            var preferred = Path.Combine(_extractDir, "Plain Craft Launcher 2.exe");
            if (File.Exists(preferred)) return preferred;
            var hits = Directory.GetFiles(_extractDir, "*.exe", SearchOption.TopDirectoryOnly);
            if (hits.Length == 1) return hits[0];
            throw new InvalidOperationException("解压后找不到启动器 exe。");
        }

        private void ReadFooter()
        {
            using (var fs = File.Open(_exePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                if (fs.Length < FooterSize + 64)
                    throw new InvalidOperationException("这个 exe 不完整，缺少启动载荷。");
                fs.Position = fs.Length - FooterSize;
                var footer = new byte[FooterSize];
                if (fs.Read(footer, 0, FooterSize) != FooterSize)
                    throw new InvalidOperationException("无法读取单 exe 尾部。");
                var magic = Encoding.ASCII.GetString(footer, 0, Magic.Length);
                if (magic != Magic)
                    throw new InvalidOperationException("这个 exe 不是 MOONCL 单文件包。");
                _zipOffset = BitConverter.ToInt64(footer, 16);
                _zipLength = BitConverter.ToInt64(footer, 24);
                _zipHash = new byte[32];
                Buffer.BlockCopy(footer, 32, _zipHash, 0, 32);
                var archCode = footer[64];
                if (archCode == 2) _arch = "arm64";
                else if (archCode == 1) _arch = "x64";
                else throw new InvalidOperationException("单 exe 架构标记无效。");
                if (_zipOffset < 0 || _zipLength < 32 || _zipOffset + _zipLength + FooterSize != fs.Length)
                    throw new InvalidOperationException("单 exe 载荷范围无效。");

            }
            var hashHex = ToHex(_zipHash);
            _extractDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "MOONCL", "single", hashHex);
        }

        private InstallerSpec TryReadReleasesSpec()
        {
            try
            {
                var json = DownloadString(ReleasesUrl);
                return ParseReleases(json, _arch);
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
                    AssertInstallerUrl(spec.Url, arch);
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

        private string SaveHttps(string url, string dest, IProgressSink sink, bool requireInstaller)
        {
            var current = url;
            for (var hop = 0; hop < 8; hop++)
            {
                AssertHttps(current, false);
                var req = (HttpWebRequest)WebRequest.Create(current);
                req.AllowAutoRedirect = false;
                req.UserAgent = "PCL-CY-SingleExe/1.0";
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
                    if (requireInstaller) AssertInstallerUrl(current, _arch);
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
            req.UserAgent = "PCL-CY-SingleExe/1.0";
            req.Timeout = 60000;
            using (var resp = (HttpWebResponse)req.GetResponse())
            using (var reader = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
                return reader.ReadToEnd();
        }

        private static void AssertInstallerUrl(string url, string arch)
        {
            var uri = AssertHttps(url, true);
            if (!string.Equals(uri.Host, "builds.dotnet.microsoft.com", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("安装包必须来自 builds.dotnet.microsoft.com。");
            var name = Path.GetFileName(uri.AbsolutePath);
            var prefix = "windowsdesktop-runtime-10.0.";
            var suffix = "-win-" + arch + ".exe";
            if (!name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ||
                !name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("安装包文件名不符合 .NET 10 Desktop Runtime: " + name);
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
                throw new InvalidOperationException("拒绝把 .NET 运行时 zip 当作安装包。");
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

        private static void AssertPe(string path)
        {
            using (var fs = File.OpenRead(path))
            {
                if (fs.Length < 1024 * 1024) throw new InvalidOperationException("安装包过小。");
                var buf = new byte[2];
                if (fs.Read(buf, 0, 2) != 2 || buf[0] != 0x4D || buf[1] != 0x5A)
                    throw new InvalidOperationException("安装包不是 PE。");
            }
        }

        private static int Install(string path)
        {
            try
            {
                var psi = new ProcessStartInfo();
                psi.FileName = path;
                psi.Arguments = "/install /passive /norestart";
                psi.UseShellExecute = true;
                psi.Verb = "runas";
                var proc = Process.Start(psi);
                if (proc == null) throw new InvalidOperationException("无法启动安装程序。");
                proc.WaitForExit();
                return proc.ExitCode;
            }
            catch (System.ComponentModel.Win32Exception)
            {
                throw new InvalidOperationException("未获得管理员权限，已取消安装。没有改动系统 .NET。");
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

        private static bool WaitForDesktop10(string arch)
        {
            for (var i = 0; i < 12; i++)
            {
                if (HasRegisteredDesktop10(arch)) return true;
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

        private static bool FixedTimeEquals(byte[] left, byte[] right)
        {
            if (left == null || right == null || left.Length != right.Length) return false;
            var diff = 0;
            for (var i = 0; i < left.Length; i++) diff |= left[i] ^ right[i];
            return diff == 0;
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
        private readonly Label _status;
        private readonly ProgressBar _bar;
        private readonly Button _agree;
        private readonly Button _cancel;
        private bool _busy;
        public int ExitCode = 2;
        public bool ConsentAccepted { get; private set; }

        public ConsentForm(SingleExeHost host, string[] args)
        {
            _host = host;
            _args = args;
            Text = "MOONCL";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(520, 248);
            Font = new Font("Microsoft YaHei UI", 9f);
            var title = new Label();
            title.Text = "需要 .NET 10 桌面运行时";
            title.Font = new Font(Font, FontStyle.Bold);
            title.AutoSize = false;
            title.Bounds = new Rectangle(20, 16, 480, 28);
            var body = new Label();
            body.Text = "这台电脑还没有对应架构的 .NET 10 Desktop Runtime。\r\n为了让这一个 exe 尽量小，里面没有附带 .NET 10，也没有裁掉启动器自己的库。\r\n\r\n点「同意并下载」后，才会从微软官方地址下载并安装，然后打开启动器。\r\n点「取消」或关闭窗口不会下载。";
            body.AutoSize = false;
            body.Bounds = new Rectangle(20, 48, 480, 96);
            _bar = new ProgressBar();
            _bar.Bounds = new Rectangle(20, 150, 480, 18);
            _bar.Minimum = 0;
            _bar.Maximum = 100;
            _status = new Label();
            _status.Text = "等待你的选择。";
            _status.AutoSize = false;
            _status.Bounds = new Rectangle(20, 174, 480, 22);
            _agree = new Button();
            _agree.Text = "同意并下载";
            _agree.Bounds = new Rectangle(292, 206, 110, 28);
            _agree.Click += OnAgree;
            _cancel = new Button();
            _cancel.Text = "取消";
            _cancel.Bounds = new Rectangle(408, 206, 92, 28);
            _cancel.Click += delegate { ExitCode = 2; Close(); };
            CancelButton = _cancel;
            AcceptButton = null;
            Controls.Add(title);
            Controls.Add(body);
            Controls.Add(_bar);
            Controls.Add(_status);
            Controls.Add(_agree);
            Controls.Add(_cancel);
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
            _agree.Enabled = false;
            _cancel.Enabled = false;
            ThreadPool.QueueUserWorkItem(delegate
            {
                try
                {
                    _host.DownloadDesktopRuntimeAfterConsent(this);
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
                        _agree.Enabled = true;
                        _cancel.Enabled = true;
                        _status.Text = ex.Message;
                        MessageBox.Show(this, ex.Message, "MOONCL", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    }));
                }
            });
        }
    }

    private sealed class SliceStream : Stream
    {
        private readonly Stream _inner;
        private readonly long _start;
        private readonly long _length;

        public SliceStream(Stream inner, long start, long length)
        {
            _inner = inner;
            _start = start;
            _length = length;
            _inner.Position = start;
        }

        public override bool CanRead { get { return true; } }
        public override bool CanSeek { get { return true; } }
        public override bool CanWrite { get { return false; } }
        public override long Length { get { return _length; } }
        public override long Position
        {
            get { return _inner.Position - _start; }
            set
            {
                if (value < 0 || value > _length) throw new ArgumentOutOfRangeException("value");
                _inner.Position = _start + value;
            }
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var remain = _length - Position;
            if (remain <= 0) return 0;
            if (count > remain) count = (int)remain;
            return _inner.Read(buffer, offset, count);
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            long next;
            if (origin == SeekOrigin.Begin) next = offset;
            else if (origin == SeekOrigin.Current) next = Position + offset;
            else next = _length + offset;
            Position = next;
            return Position;
        }

        public override void Flush() { }
        public override void SetLength(long value) { throw new NotSupportedException(); }
        public override void Write(byte[] buffer, int offset, int count) { throw new NotSupportedException(); }
    }

    private static class PrepareWindow
    {
        public static void ShowWhile(Action work)
        {
            Exception error = null;
            using (var form = new Form())
            {
                form.Text = "MOONCL";
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
