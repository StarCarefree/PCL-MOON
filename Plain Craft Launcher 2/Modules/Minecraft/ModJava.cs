using System.IO;
using System.Text.Json;
using PCL.Core.App;
using PCL.Core.IO;
using PCL.Core.Minecraft;
using PCL.Core.Minecraft.Java.UserPreference;
using PCL.Core.App.Localization;
using PCL.Core.Utils.OS;
using PCL.Core.Utils;

namespace PCL;

public static partial class ModJava
{
    public static int javaListCacheVersion = 7;

    /// <summary>
    ///     防止多个需要 Java 的部分同时要求下载 Java（#3797）。
    /// </summary>
    public static object javaLock = new();

    /// <summary>
    ///     目前所有可用的 Java。
    /// </summary>
    public static JavaManager Javas => JavaService.JavaManager;

    public sealed class LaunchJavaSelection
    {
        // 仅在本次启动中保留选择，供下载后的重选复用。
        public bool UseRecommended { get; set; }
    }

    /// <summary>
    ///     根据要求返回最适合的 Java，若找不到则返回 Nothing。
    ///     最小与最大版本在与输入相同时也会通过。
    ///     必须在工作线程调用，且必须包括 SyncLock JavaLock。
    /// </summary>
    public static JavaEntry JavaSelect(string cancelException, Version minVersion = null, Version maxVersion = null,
        McInstance relatedInstance = null, bool enforceVersionRange = false,
        IReadOnlySet<int> compatibleMajors = null, LaunchJavaSelection launchSelection = null)
    {
        ModBase.Log(
            $"[Java] 要求选择合适 Java，要求最低版本 {(minVersion is not null ? minVersion.ToString() : "未指定")}，要求选择的最高版本 {(maxVersion is not null ? maxVersion.ToString() : "未指定")}，关联实例 {(relatedInstance is not null ? relatedInstance.Name : "未指定")}");

        var reqMin = minVersion ?? new Version(1, 0, 0);
        var reqMax = maxVersion ?? new Version(999, 999, 999);

        bool AcceptSelectedJava(JavaEntry candidate, string warningKey)
        {
            var version = candidate.Installation.Version;
            var suitable = JavaManager.IsVersionSuitable(version, reqMin, reqMax) &&
                           (compatibleMajors is null ||
                            compatibleMajors.Contains(JavaManager.NormalizeVersion(version).Major));
            if (suitable) return true;
            if (enforceVersionRange)
            {
                ModBase.Log($"[Java] 指定的 Java 不满足强制范围要求，继续搜索: {candidate}");
                return false;
            }
            if (launchSelection is not null && relatedInstance?.PathInstance is not null &&
                Config.Instance.IgnoreJavaCompatibility[relatedInstance.PathInstance])
            {
                ModBase.Log($"[Java] 已忽略兼容性警告，使用指定的 Java: {candidate}");
                return true;
            }

            var warning = _GetJavaRangeWarning(warningKey, version, minVersion, maxVersion);
            if (compatibleMajors is not null)
                warning += "\n" + Lang.Text("Minecraft.Launch.Java.Compatibility.AllowedMajors",
                    string.Join(", ", compatibleMajors.OrderBy(major => major)));
            if (launchSelection is null)
            {
                HintService.Hint(warning);
                return true;
            }

            var choice = ModMain.MyMsgBox(
                warning + "\n\n" + Lang.Text("Minecraft.Launch.Java.Compatibility.SwitchMessage"),
                Lang.Text("Minecraft.Launch.Java.Compatibility.Title"),
                Lang.Text("Minecraft.Launch.Java.Compatibility.UseRecommended"),
                Lang.Text("Minecraft.Launch.Java.Compatibility.KeepSelected"),
                Lang.Text("Minecraft.Launch.Java.Compatibility.CancelLaunch"),
                isWarn: true);
            switch (choice)
            {
                case 1:
                    launchSelection.UseRecommended = true;
                    ModBase.Log("[Java] 玩家选择仅本次启动使用推荐 Java");
                    return false;
                case 2:
                    ModBase.Log($"[Java] 玩家选择继续使用不兼容的 Java: {candidate}");
                    return true;
                default:
                    throw new Exception(cancelException);
            }
        }

        // 自动搜索结果按兼容主版本集合过滤，排除集合未声明的主版本。
        JavaEntry[] FilterByCompatibleMajors(JavaEntry[] entries)
        {
            return compatibleMajors is null
                ? entries
                : entries
                    .Where(entry => compatibleMajors.Contains(
                        JavaManager.NormalizeVersion(entry.Installation.Version).Major))
                    .ToArray();
        }

        // ===== 优先级 1：实例专属 Java 偏好 =====
        if (launchSelection?.UseRecommended != true && relatedInstance?.PathInstance is not null)
        {
            var rawPreference = Config.Instance.SelectedJava[relatedInstance.PathInstance];

            if (!string.IsNullOrWhiteSpace(rawPreference))
            {
                var preference = GetInstanceJavaPreference(relatedInstance);

                // 处理解析成功的偏好
                if (preference is not null)
                    switch (true)
                    {
                        case object _ when preference is ExistingJava: // "exist"
                        {
                            var existPref = (ExistingJava)preference;
                            var candidate = Javas.AddOrGet(existPref.JavaExePath);

                            if (candidate is not null && candidate.IsEnabled)
                            {
                                if (!AcceptSelectedJava(candidate,
                                        "Minecraft.Launch.Java.Compatibility.InstanceSelectedOutOfRange"))
                                    break;
                                ModBase.Log($"[Java] 返回实例 '{relatedInstance.Name}' 指定的 Java: {candidate}");
                                return candidate;
                            }

                            ModBase.Log($"[Java] 警告：实例指定的 Java 路径无效或不可用: {existPref.JavaExePath}");

                            break;
                        }

                        case object _ when preference is UseRelativePath: // "relative"
                        {
                            var relPref = (UseRelativePath)preference;
                            var absPath =
                                Path.GetFullPath(Path.Combine(Basics.ExecutableDirectory, relPref.RelativePath));

                            if (Files.IsPathWithinDirectory(absPath, Basics.ExecutableDirectory))
                            {
                                var candidate = Javas.Get(absPath);
                                if (candidate is not null && candidate.IsEnabled)
                                {
                                    if (!AcceptSelectedJava(candidate,
                                            "Minecraft.Launch.Java.Compatibility.RelativePathSelectedOutOfRange"))
                                        break;
                                    ModBase.Log(
                                        $"[Java] 返回实例 '{relatedInstance.Name}' 相对路径指定的 Java ({relPref.RelativePath}): {candidate}");
                                    return candidate;
                                }
                            }
                            else
                            {
                                ModBase.Log($"[Java] 警告：实例相对路径指定的 Java 无效: {absPath}");
                            }

                            break;
                        }

                        case object _ when preference is UseGlobalPreference: // "global"
                        {
                            // 不返回，继续到全局设置检查
                            ModBase.Log($"[Java] 实例 '{relatedInstance.Name}' 配置为使用全局 Java 设置，继续检查全局配置");
                            break;
                        }

                        default:
                        {
                            ModBase.Log($"[Java] 警告：未知的 Java 偏好类型 '{preference}'，跳过处理");
                            break;
                        }
                    }
                else
                    ModBase.Log($"[Java] 实例 '{relatedInstance.Name}' 未指定 Java 偏好（空值），使用自动选择策略");
            }
            else
            {
                ModBase.Log($"[Java] 实例 '{relatedInstance.Name}' 无 Java 偏好配置，使用自动选择策略");
            }
        }

        // ===== 优先级 2：全局指定的 Java =====
        var globalJavaPath = launchSelection?.UseRecommended == true ? null : Config.Launch.SelectedJava;
        if (!string.IsNullOrWhiteSpace(globalJavaPath))
        {
            globalJavaPath = globalJavaPath.Trim();
            var candidate = Javas.AddOrGet(globalJavaPath);

            if (candidate is not null && candidate.IsEnabled)
            {
                if (AcceptSelectedJava(candidate,
                        "Minecraft.Launch.Java.Compatibility.GlobalSelectedOutOfRange"))
                {
                    ModBase.Log($"[Java] 返回全局指定的 Java: {candidate}");
                    return candidate;
                }
            }
            else
            {
                ModBase.Log($"[Java] 警告：全局指定的 Java 路径无效或不可用: {globalJavaPath}");
            }
        }
        else
        {
            ModBase.Log("[Java] 无全局 Java 配置，使用自动选择策略");
        }

        // ===== 优先级 3：自动搜索合适版本 =====
        ModBase.Log("[Java] 开始自动搜索符合版本要求的 Java 运行时");
        Javas.CheckAllAvailability();

        var candidates = FilterByCompatibleMajors(
            Javas.SelectSuitableJavaAsync(reqMin, reqMax).GetAwaiter().GetResult());
        var ret = candidates.FirstOrDefault();

        if (ret is null && candidates.Length == 0)
        {
            ModBase.Log("[Java] 未找到符合版本要求的 Java，触发全盘重新扫描");
            Javas.ScanJavaAsync().GetAwaiter().GetResult();
            candidates = FilterByCompatibleMajors(
                Javas.SelectSuitableJavaAsync(reqMin, reqMax).GetAwaiter().GetResult());
            ret = candidates.FirstOrDefault();
        }

        if (ret is not null)
            ModBase.Log($"[Java] 返回自动选择的 Java: {ret}");
        else
            ModBase.Log("[Java] 最终未能确定可用的 Java 运行时");

        return ret;
    }

    private static string _GetJavaRangeWarning(
        string key,
        Version selectedVersion,
        Version? minVersion,
        Version? maxVersion)
    {
        return Lang.Text(
            key,
            selectedVersion,
            minVersion?.ToString() ?? Lang.Text("Minecraft.Launch.Java.Compatibility.NoMinimum"),
            maxVersion?.ToString() ?? Lang.Text("Minecraft.Launch.Java.Compatibility.NoMaximum"));
    }

    public static JavaPreference GetInstanceJavaPreference(McInstance instance)
    {
        var rawPreference = Config.Instance.SelectedJava[instance.PathInstance];

        JavaPreference preference = default;
        
        // 尝试读取 JSON 配置
        if (!string.IsNullOrEmpty(rawPreference))
        {
            try
            {
                preference = JsonSerializer.Deserialize<JavaPreference>(rawPreference, JsonCompat.SerializerOptions);
            }
            catch (JsonException)
            {
                // ignored
            }
        }
        // 以旧方式读取配置
        if (preference is null)
        {
            var trimmed = rawPreference?.Trim();
            if (string.IsNullOrEmpty(trimmed))
            {
                preference = new AutoSelect();
            }
            else if (trimmed == "使用全局设置")
            {
                preference = new UseGlobalPreference();
            }
            else
            {
                preference = new ExistingJava(trimmed);
            }
        }

        switch (true)
        {
            case object _ when preference is ExistingJava:
            {
                var m = (ExistingJava)preference;
                if (!Path.IsPathRooted(m.JavaExePath)) preference = new UseGlobalPreference();

                break;
            }
            case object _ when preference is UseRelativePath:
            {
                var m = (UseRelativePath)preference;
                if (!Files.IsPathWithinDirectory(m.RelativePath, Basics.ExecutableDirectory))
                    preference = new UseGlobalPreference();

                break;
            }
        }

        return preference;
    }

    /// <summary>
    ///     是否强制指定了 64 位 Java。如果没有强制指定，返回是否安装了 64 位 Java。
    /// </summary>
    public static bool IsGameSet64BitJava(McInstance relatedVersion = null)
    {
        try
        {
            // 检查强制指定
            var userSetup = Config.Launch.SelectedJava;
            if (userSetup.StartsWith("{")) // 旧版本 Json 格式
            {
                var js = ModBase.GetJson(userSetup);
                userSetup = $"{js["Path"]}java.exe";
                Config.Launch.SelectedJava = userSetup;
            }

            if (relatedVersion is not null)
            {
                var instancePreference = GetInstanceJavaPreference(relatedVersion);
                switch (true)
                {
                    case object _ when instancePreference is AutoSelect:
                    {
                        return Javas.Existing64BitJava();
                    }
                    case object _ when instancePreference is ExistingJava:
                    {
                        var m = (ExistingJava)instancePreference;
                        var java = Javas.AddOrGet(m.JavaExePath);
                        return java is not null && java.Installation.Is64Bit;
                    }
                    case object _ when instancePreference is UseRelativePath:
                    {
                        var m = (UseRelativePath)instancePreference;
                        var javaExePath = Path.GetFullPath(m.RelativePath);
                        if (Files.IsPathWithinDirectory(javaExePath, Basics.ExecutableDirectory))
                        {
                            var java = Javas.Get(javaExePath);
                            return java is not null && java.Installation.Is64Bit;
                        }

                        break;
                    }
                }
            }

            if (!string.IsNullOrEmpty(userSetup) && !File.Exists(userSetup))
            {
                Config.Launch.SelectedJava = "";
                userSetup = string.Empty;
            }

            if (string.IsNullOrEmpty(userSetup)) return Javas.Existing64BitJava();
            var j = Javas.AddOrGet(userSetup);
            return j is not null && j.Installation.Is64Bit;
        }
        catch (Exception ex)
        {
            ModBase.Log(
                ex,
                "检查 Java 类别时出错",
                ModBase.LogLevel.Feedback,
                userSummary: Lang.Text("Minecraft.Launch.Java.Compatibility.CheckFailed"));
            if (relatedVersion is not null)
                Config.Instance.SelectedJava[relatedVersion.PathInstance] = "使用全局设置";
            Config.Launch.SelectedJava = "";
        }

        return true;
    }

    #region 下载

    /// <summary>
    ///     提示 Java 缺失，并弹窗确认是否自动下载。返回玩家选择是否下载。
    /// </summary>
    public static bool JavaDownloadConfirm(string versionDescription, bool forcedManualDownload = false)
    {
        if (!forcedManualDownload)
            return ModMain.MyMsgBox(
                Lang.Text("Minecraft.Launch.Java.AutoDownload.Message", versionDescription),
                Lang.Text("Minecraft.Launch.Java.AutoDownload.Title"),
                Lang.Text("Minecraft.Launch.Java.AutoDownload.Action"),
                Lang.Text("Common.Action.Cancel")) == 1;

        ModMain.MyMsgBox(
            Lang.Text("Minecraft.Launch.Java.NotFound.Manual.Message", versionDescription),
            Lang.Text("Minecraft.Launch.Java.NotFound.Title"));

        return false;
    }

    #endregion
}
