using System.IO;
using System.Reflection;
using PCL.Core.App;
using PCL.Core.App.Configuration;
using PCL.Core.App.Configuration.Storage;

internal static partial class Program
{
    // A separate test process installs only a scratch local provider: no lifecycle or user-data access.
    private static async Task CheckPreferences(string scratch)
    {
        Directory.CreateDirectory(scratch);
        foreach (var key in new[] { "PCL_PATH", "PCL_PATH_SHARED", "PCL_PATH_LOCAL", "PCL_PATH_TEMP" })
            Environment.SetEnvironmentVariable(key, Path.Combine(scratch, key));
        var file = Path.Combine(scratch, "launch-preferences.yml");
        if (File.Exists(file)) File.Delete(file);
        const BindingFlags flags = BindingFlags.NonPublic | BindingFlags.Static;
        var service = typeof(ConfigService);
        service.GetMethod("_InitializeConfigItems", flags)!.Invoke(null, null);
        service.GetField("_isConfigItemsInitialized", flags)!.SetValue(null, true);
        var storage = new FileConfigStorage(new YamlFileProvider(file));
        service.GetField("_localConfigProvider", flags)!.SetValue(null, storage);
        service.GetField("_isProvidersInitialized", flags)!.SetValue(null, true);
        var item = ConfigService.GetConfigItem<bool>("UiShowLaunchAnimation");
        Check(item.Source == ConfigSource.Local && item.DefaultValue, "Animation must default on in local config");
        Check(Config.Preference.ShowLaunchAnimation, "Missing preference must default on");
        ConfigService.TrySetValue("UiShowLaunchAnimation", false);
        Check(!Config.Preference.ShowLaunchAnimation, "Settings tag must disable animation");
        Config.Preference.ShowLaunchAnimation = true;
        Check(Config.Preference.ShowLaunchAnimation, "Animation must re-enable");
        Config.Preference.ShowLaunchAnimation = false;
        await Task.Delay(150); // Let the provider's writer enter its read loop before stopping/flushing.
        storage.Stop();
        Check(File.Exists(file) && !new YamlFileProvider(file).Get<bool>("UiShowLaunchAnimation"), "Disabled preference must survive disk reload");
    }
}
