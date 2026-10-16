using System;

namespace PCL.Core.Minecraft.Launcher;

/// <summary>Resource identity is independent from provider and must survive cache, detail and download hand-offs.</summary>
public static class BrowseResourceTypes
{
    public static readonly string[] All = ["modpack", "datapack", "world", "shader", "resourcepack", "plugin"];
    public static string Validate(string type) => type is "datapack" or "world" or "modpack" or "shader" or "resourcepack" or "plugin"
        ? type : throw new ArgumentException("未知的资源类型。");
    public static string Label(string type) => Validate(type) switch
    {
        "datapack" => "数据包", "world" => "地图", "shader" => "光影包", "resourcepack" => "材质包", "plugin" => "服务器插件", _ => "整合包"
    };
    public static int CurseForgeClass(string type) => Validate(type) switch
    {
        "datapack" => 6945, "world" => 17, "shader" => 6552, "resourcepack" => 12, "plugin" => 5, _ => 4471
    };
    public static string CurseForgePath(string type) => Validate(type) switch
    {
        "datapack" => "data-packs", "world" => "worlds", "shader" => "shaders", "resourcepack" => "texture-packs", "plugin" => "bukkit-plugins", _ => "modpacks"
    };
    public static bool Supports(string source, string type)
    {
        Validate(type);
        return source switch { "curseforge" => type != "plugin", "modrinth" => type != "world", "mcmod" => type is "modpack" or "datapack", _ => false };
    }
    public static void RequireSupported(string source, string type)
    {
        if (Supports(source, type)) return;
        throw new NotSupportedException(source == "modrinth"
            ? "Modrinth 当前未提供地图类型的目录接口，请切换到 CurseForge 浏览地图。"
            : source == "curseforge"
                ? "CurseForge 当前不作为服务器插件来源，请切换到 Modrinth。"
                : "MC百科当前仅提供可识别的整合包和数据包索引；此类型请切换到 CurseForge 或 Modrinth。");
    }
    public static bool UsesModLoader(string type) => Validate(type) == "modpack";
}
