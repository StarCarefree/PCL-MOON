using System;
using System.Collections.Generic;

namespace PCL.Core.UI.WebHost;

/// <summary>
/// 与 FormMain.PageType / PageSubType 对齐的页面目录。
/// 数值是启动器公开页面 API 的一部分，改动前要同时改导航。
/// </summary>
public sealed record LauncherPageRoute(
    string Id,
    string Tab,
    string TitleKey,
    int NativePage,
    int NativeSubType,
    bool WebImplemented);

public static class LauncherPageCatalog
{
    public const int Launch = 0;
    public const int Download = 1;
    public const int Setup = 2;
    public const int Tools = 3;

    public static IReadOnlyList<LauncherPageRoute> Routes { get; } =
    [
        new("host.status", "launch", "WebUi.Page.Host", Launch, 0, true),
        new("launch.home", "launch", "Main.Tab.Launch", Launch, 0, true),
        new("download.install", "download", "Download.Left.Minecraft", Download, 1, false),
        new("download.mod", "download", "Download.Left.Mod", Download, 2, false),
        new("download.pack", "download", "Download.Left.Modpack", Download, 3, false),
        new("download.datapack", "download", "Download.Left.DataPack", Download, 4, false),
        new("download.resource", "download", "Download.Left.ResourcePack", Download, 5, false),
        new("download.shader", "download", "Download.Left.Shader", Download, 6, false),
        new("download.world", "download", "Download.Left.World", Download, 7, false),
        new("download.favorites", "download", "Download.Left.Favorites", Download, 8, false),
        new("download.client", "download", "Download.Left.Minecraft", Download, 9, false),
        new("download.optifine", "download", "Download.Left.Optifine", Download, 10, false),
        new("download.forge", "download", "Download.Left.Forge", Download, 11, false),
        new("download.neoforge", "download", "Download.Left.NeoForge", Download, 12, false),
        new("download.cleanroom", "download", "Download.Left.Cleanroom", Download, 13, false),
        new("download.fabric", "download", "Download.Left.Fabric", Download, 14, false),
        new("download.liteloader", "download", "Download.Left.LiteLoader", Download, 16, false),
        new("download.labymod", "download", "Download.Left.LabyMod", Download, 17, false),
        new("download.legacyfabric", "download", "Download.Left.LegacyFabric", Download, 18, false),
        new("setup.launch", "setup", "Setup.Left.Item.Launch", Setup, 0, false),
        new("setup.ui", "setup", "Setup.Left.Item.Ui", Setup, 1, false),
        new("setup.manage", "setup", "Setup.Left.Item.GameManage", Setup, 2, false),
        new("setup.about", "setup", "Setup.Left.Item.About", Setup, 4, true),
        new("setup.log", "setup", "Setup.Left.Item.Log", Setup, 5, false),
        new("setup.feedback", "setup", "Setup.Left.Item.Feedback", Setup, 6, false),
        new("setup.update", "setup", "Setup.Left.Item.Update", Setup, 8, false),
        new("setup.java", "setup", "Setup.Left.Item.Java", Setup, 9, false),
        new("setup.misc", "setup", "Setup.Left.Item.Misc", Setup, 10, false),
        new("setup.language", "setup", "Setup.Language.Title", Setup, 11, false),
        new("tools.test", "tools", "Tools.Left.Utilities.Title", Tools, 3, false)
    ];

    public static LauncherPageRoute? Find(string? id)
    {
        if (string.IsNullOrWhiteSpace(id)) return null;
        foreach (var route in Routes)
        {
            if (route.Id.Equals(id, StringComparison.Ordinal)) return route;
        }

        return null;
    }
}
