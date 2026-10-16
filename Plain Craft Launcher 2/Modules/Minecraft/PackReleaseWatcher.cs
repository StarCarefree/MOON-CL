using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Threading;
using PCL.Core.App;
using PCL.Core.Minecraft.Launcher;

namespace PCL;

internal static class PackReleaseWatcher
{
    private static readonly object Gate = new();
    private static readonly Dictionary<string, (DateTimeOffset At, PlatformFile? File)> Remote = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, string> Lines = new(StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> Hinted = new(StringComparer.OrdinalIgnoreCase);
    private static readonly TimeSpan Fresh = TimeSpan.FromHours(6);
    private static McInstance? _pending;
    private static int _running;

    public static string NoticeText(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return "";
        lock (Gate) return Lines.TryGetValue(path, out var text) ? text : "";
    }

    public static void Queue(McInstance instance)
    {
        if (string.IsNullOrWhiteSpace(instance?.PathInstance)) return;
        lock (Gate) _pending = instance;
        if (Interlocked.CompareExchange(ref _running, 1, 0) != 0) return;
        ModBase.RunInThread(Drain);
    }

    private static void Drain()
    {
        try
        {
            while (true)
            {
                McInstance? current;
                lock (Gate)
                {
                    current = _pending;
                    _pending = null;
                }
                if (current is null) return;
                try { Check(current); }
                catch (Exception ex) { ModBase.Log(ex, "检查整合包新版本失败", ModBase.LogLevel.Debug); }
            }
        }
        finally
        {
            Interlocked.Exchange(ref _running, 0);
            lock (Gate)
                if (_pending is not null && Interlocked.CompareExchange(ref _running, 1, 0) == 0)
                    ModBase.RunInThread(Drain);
        }
    }

    private static void Check(McInstance instance)
    {
        var path = instance.PathInstance;
         if (!TryTarget(path, out var source, out var id, out var version) || version.Length == 0)
         {
             Publish(path, instance.Name, "", false, "");
             return;
         }
        if (!TryCached(source, id, out var latest))
        {
            latest = Fetch(source, id);
            lock (Gate) Remote[source + ":" + id] = (DateTimeOffset.UtcNow, latest);
        }
         if (latest is null)
         {
             Publish(path, instance.Name, "", false, "");
             return;
         }
        var newer = PackReleaseCheck.IsNewer(version, "", latest);
        Publish(path, instance.Name, newer ? PackReleaseCheck.Notice(source, version, latest) : "", newer, source + ":" + id + ":" + latest.Id);
    }

    private static bool TryTarget(string path, out string source, out string id, out string version)
    {
        source = "";
        id = "";
         version = "";
        PackIdentity? identity = null;
        try { identity = PackIdentityStore.TryRead(path); }
        catch (Exception ex) { ModBase.Log(ex, "读取整合包身份失败", ModBase.LogLevel.Debug); }
        version = identity?.Version ?? "";
        var stateSource = "";
        var stateId = "";
        try
        {
            stateSource = States.Instance.ModpackSource[path] ?? "";
            stateId = States.Instance.ModpackId[path] ?? "";
            if (version.Length == 0) version = States.Instance.ModpackVersion[path] ?? "";
        }
        catch (Exception ex) { ModBase.Log(ex, "读取整合包来源失败", ModBase.LogLevel.Debug); }
        var curse = ValidCurse(identity?.CurseForgeId) ? identity!.CurseForgeId : stateSource.Contains("curse", StringComparison.OrdinalIgnoreCase) && ValidCurse(stateId) ? stateId : "";
        var modrinth = ValidModrinth(identity?.ModrinthId) ? identity!.ModrinthId : stateSource.Contains("modrinth", StringComparison.OrdinalIgnoreCase) && ValidModrinth(stateId) ? stateId : "";
        var preferModrinth = stateSource.Contains("modrinth", StringComparison.OrdinalIgnoreCase) || identity?.Source == "modrinth";
        if (preferModrinth && modrinth.Length > 0) { source = "modrinth"; id = modrinth; return true; }
        if (curse.Length > 0) { source = "curseforge"; id = curse; return true; }
        if (modrinth.Length > 0) { source = "modrinth"; id = modrinth; return true; }
        return false;
    }

    private static bool TryCached(string source, string id, out PlatformFile? file)
    {
        lock (Gate)
        {
            if (Remote.TryGetValue(source + ":" + id, out var item) && DateTimeOffset.UtcNow - item.At < Fresh)
            {
                file = item.File;
                return true;
            }
        }
        file = null;
        return false;
    }

    private static PlatformFile? Fetch(string source, string id)
    {
        var json = ModDownload.DlModRequest(PackReleaseCheck.FilesAddress(source, id).AbsoluteUri);
        return PackReleaseCheck.LatestRelease(source, json, id);
    }

    private static void Publish(string path, string name, string text, bool newer, string hintKey)
    {
        lock (Gate)
        {
            if (text.Length == 0) Lines.Remove(path);
            else Lines[path] = text;
        }
        ModBase.RunInUi(() =>
        {
            if (!string.Equals(ModInstanceList.McMcInstanceSelected?.PathInstance, path, StringComparison.OrdinalIgnoreCase)) return;
            try { ModMain.frmInstanceSetup?.ShowPackRelease(); }
            catch (Exception ex) { ModBase.Log(ex, "刷新版本设置的更新提示失败", ModBase.LogLevel.Debug); }
            try { ModMain.frmMain?.RefreshHybridVersions(); }
            catch (Exception ex) { ModBase.Log(ex, "刷新版本列表的更新提示失败", ModBase.LogLevel.Debug); }
            if (newer && Hinted.Add(hintKey))
                HintService.Hint((string.IsNullOrWhiteSpace(name) ? "当前版本" : name) + "：" + text, HintType.Info);
        });
    }

    private static bool ValidCurse(string? id) => int.TryParse(id, out var number) && number > 0;
    private static bool ValidModrinth(string? id) => id is not null && Regex.IsMatch(id, "^[A-Za-z0-9]{8}$");
}
