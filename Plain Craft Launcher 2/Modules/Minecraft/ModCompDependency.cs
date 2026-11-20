using System.IO;
using CompFile = PCL.ModComp.CompFile;
using CompFileStatus = PCL.ModComp.CompFileStatus;
using CompLoaderType = PCL.ModComp.CompLoaderType;
using CompProject = PCL.ModComp.CompProject;
using LocalCompFile = PCL.ModLocalComp.LocalCompFile;
using PCL.Core.App;
using PCL.Core.App.Localization;
using PCL.Core.Logging;
using PCL.Core.Minecraft.ResourceProject;
using PCL.Core.UI;
using PCL.Core.Utils;
using PCL.Network;

namespace PCL;

public static class ModCompDependency
{
    public static ModDependencyRequest BuildRequest(
        CompFile file,
        CompProject project,
        string targetMinecraftVersion,
        List<CompLoaderType> targetLoaders,
        string targetModsFolder,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(file);
        ArgumentNullException.ThrowIfNull(project);
        targetLoaders ??= new List<CompLoaderType>();

        var source = GetSource(project.FromCurseForge);
        List<string> requiredIds;
        List<string> optionalIds;
        // 避免与 ModComp.CompFilesGet 并发补全依赖映射时枚举异常
        lock (file)
        {
            requiredIds = file.Dependencies
                .Where(static dependencyId => !string.IsNullOrWhiteSpace(dependencyId))
                .ToList();
            optionalIds = file.OptionalDependencies
                .Where(static dependencyId => !string.IsNullOrWhiteSpace(dependencyId))
                .ToList();
        }

        var dependencies = requiredIds
            .Select(dependencyId => new ModDependencyReference
            {
                ProjectId = dependencyId,
                Source = source,
                IsRequired = true,
            })
            .Concat(optionalIds
                .Select(dependencyId => new ModDependencyReference
                {
                    ProjectId = dependencyId,
                    Source = source,
                    IsRequired = false,
                }))
            .ToList();

        return new ModDependencyRequest
        {
            TargetMinecraftVersion = targetMinecraftVersion ?? string.Empty,
            TargetLoaders = ToLoaderNames(targetLoaders),
            RequiredDependencies = dependencies,
            InstalledMods = ScanInstalledMods(targetModsFolder, cancellationToken),
            ProjectResolver = (source, projectId) => ResolveProjectFiles(source, projectId, cancellationToken),
        };
    }

    public static List<InstalledModIdentity> ScanInstalledMods(string targetModsFolder, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var result = new List<InstalledModIdentity>();
        if (string.IsNullOrWhiteSpace(targetModsFolder) || !Directory.Exists(targetModsFolder))
        {
            return result;
        }

        var modPaths = Directory.EnumerateFiles(targetModsFolder)
            .Where(static path => LocalCompFile.IsModFile(path)
                                  && !path.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase)
                                  && !path.EndsWith(".old", StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (modPaths.Count == 0)
        {
            return result;
        }

        var sha1ByPath = ModLocalComp.GetFileSha1Batch(modPaths, cancellationToken);
        var localCache = ModLocalComp.LoadLocalCompCacheBySha1();

        foreach (var path in modPaths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var fullPath = Path.GetFullPath(path);
            sha1ByPath.TryGetValue(fullPath, out var sha1);

            string? sourceProjectId = null;
            string? source = null;
            var gameVersions = new List<string>();
            var loaders = new List<string>();

            if (!string.IsNullOrWhiteSpace(sha1) && localCache.TryGetValue(sha1, out var cached))
            {
                try
                {
                    if (cached["Comp"] is JsonObject compObj)
                    {
                        sourceProjectId = compObj["Id"]?.ToString();
                        source = compObj["DataSource"]?.ToString();
                    }

                    if (cached["CompFile"] is JsonObject fileObj)
                    {
                        if (fileObj["GameVersions"] is JsonArray gvArr)
                        {
                            gameVersions = gvArr
                                .Select(static node => node?.ToString())
                                .Where(static version => !string.IsNullOrWhiteSpace(version))
                                .Cast<string>()
                                .ToList();
                        }

                        if (fileObj["ModLoaders"] is JsonArray loaderArr)
                        {
                            loaders = ToLoaderNames(loaderArr.Select(static node =>
                                node is JsonValue val && val.TryGetValue<int>(out var loaderInt)
                                    ? (CompLoaderType)loaderInt
                                    : CompLoaderType.Any));
                        }
                    }
                }
                catch (Exception ex)
                {
                    ModBase.Log(ex, $"[CompDeps] 读取本地模组缓存条目失败，已降级为哈希匹配：{path}", ModBase.LogLevel.Developer);
                    sourceProjectId = null;
                    source = null;
                    gameVersions = new List<string>();
                    loaders = new List<string>();
                }
            }

            result.Add(new InstalledModIdentity
            {
                SourceProjectId = sourceProjectId,
                Source = source,
                FileHashes = !string.IsNullOrWhiteSpace(sha1) ? [sha1] : [],
                GameVersions = gameVersions,
                Loaders = loaders,
            });
        }

        return result;
    }

    public static ModDependencyProject? ResolveProjectFiles(string source, string projectId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(source) || string.IsNullOrWhiteSpace(projectId))
        {
            return null;
        }

        var fromCurseForge = string.Equals(source, "CurseForge", StringComparison.OrdinalIgnoreCase);
        var files = ModComp.CompFilesGet(projectId, fromCurseForge, cancellationToken);
        if (!ModComp.compProjectCache.TryGetValue(projectId, out var compProject))
        {
            return null;
        }

        if (compProject.FromCurseForge != fromCurseForge)
        {
            return null;
        }

        // 混合发布工程优先仅保留 Mod 产物，避免 Datapack/Plugin 因加载器为空被误选
        var modFiles = files.Where(static f => f.Available && f.Type == ModComp.CompType.Mod).ToList();
        var candidateFiles = modFiles.Count > 0
            ? modFiles
            : files.Where(static f => f.Available).ToList();

        return new ModDependencyProject
        {
            ProjectId = compProject.Id,
            Source = source,
            ProjectName = compProject.TranslatedName ?? compProject.RawName,
            Files = candidateFiles.Select(compFile =>
            {
                List<ModDependencyReference> requiredDependencies;
                lock (compFile)
                {
                    requiredDependencies = compFile.Dependencies
                        .Where(static dependencyId => !string.IsNullOrWhiteSpace(dependencyId))
                        .Select(dependencyId => new ModDependencyReference
                        {
                            ProjectId = dependencyId,
                            Source = source,
                            IsRequired = true,
                        })
                        .ToList();
                }

                return new ModDependencyFile
                {
                    Id = compFile.Id,
                    DisplayName = compFile.DisplayName,
                    Version = compFile.Version,
                    Hash = compFile.Hash,
                    GameVersions = compFile.GameVersions?.Where(static version => !string.IsNullOrWhiteSpace(version)).ToList()
                                   ?? new List<string>(),
                    Loaders = ToLoaderNames(compFile.ModLoaders),
                    ReleaseType = MapReleaseType(compFile.Status),
                    ReleaseDate = compFile.ReleaseDate,
                    RequiredDependencies = requiredDependencies,
                };
            }).ToList(),
        };
    }

    public static ModDependencyFile? SelectCompatibleDependencyFile(
        ModDependencyResolutionResult result,
        string projectId,
        string source)
    {
        ArgumentNullException.ThrowIfNull(result);

        return result.ToInstall
            .FirstOrDefault(install =>
                string.Equals(install.ProjectId, projectId, StringComparison.OrdinalIgnoreCase)
                && string.Equals(install.Source, source, StringComparison.OrdinalIgnoreCase))
            ?.File;
    }

    public static List<(string Filename, DownloadFile File)> BuildDependencyDownloads(
        ModDependencyResolutionResult result,
        string targetModsFolder,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(result);

        var downloads = new List<(string, DownloadFile)>();
        foreach (var install in result.ToInstall.AsEnumerable().Reverse())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!ModComp.compProjectCache.TryGetValue(install.ProjectId, out var depProject))
            {
                continue;
            }

            var fromCurseForge = string.Equals(install.Source, "CurseForge", StringComparison.OrdinalIgnoreCase);
            if (depProject.FromCurseForge != fromCurseForge)
            {
                continue;
            }

            var depCompFile = ModComp.CompFilesGet(install.ProjectId, fromCurseForge, cancellationToken)
                .FirstOrDefault(file => string.Equals(file.Id, install.File.Id, StringComparison.OrdinalIgnoreCase));
            if (depCompFile is null)
            {
                continue;
            }

            var depFileName = ModComp.CompFileNameGet(depProject, depCompFile);
            var targetPath = Path.Combine(targetModsFolder ?? string.Empty, depFileName);
            downloads.Add((depFileName, depCompFile.ToNetFile(targetPath, ModComp.DownloadReason.Dependency)));
        }

        return downloads;
    }

    /// <summary>
    ///     弹出必需前置安装确认弹窗。
    ///     返回值：WithDeps 表示连同前置一并下载，WithoutDeps 表示仅下载本体，Cancel 表示取消，Unresolved 表示存在无法解析的前置但用户选择继续。
    /// </summary>
    public static ModComp.CompDepsInstallTypes ConfirmDependencyInstall(
        ModDependencyResolutionResult result,
        string? titleSuffix = null)
    {
        ArgumentNullException.ThrowIfNull(result);

        static string FormatTitle(string baseTitle, string? suffix) =>
            string.IsNullOrWhiteSpace(suffix) ? baseTitle : $"{baseTitle} - {suffix}";

        if (result.Unresolved is { Count: > 0 })
        {
            ModBase.Log($"[CompDeps] 无法解析: {result.Unresolved.Count} 个必需前置");
            var dependencies = string.Join(
                Environment.NewLine,
                result.Unresolved.Select(dep => Lang.Text(
                    "Download.Comp.Dependency.Unresolved.ListItem",
                    dep.Source,
                    dep.ProjectId,
                    dep.Reason)));
            var message = Lang.Text("Download.Comp.Dependency.Unresolved.Message", dependencies);
            var selectedButton = ModMain.MyMsgBox(
                message,
                FormatTitle(Lang.Text("Download.Comp.Dependency.Unresolved.Title"), titleSuffix),
                Lang.Text("Download.Comp.Dependency.Unresolved.ContinueWithoutDependencies"),
                Lang.Text("Common.Action.Cancel"),
                isWarn: true,
                forceWait: true);

            return selectedButton == 1
                ? ModComp.CompDepsInstallTypes.Unresolved
                : ModComp.CompDepsInstallTypes.Cancel;
        }

        if (result.ToInstall is { Count: > 0 })
        {
            var dependencies = string.Join(
                Environment.NewLine,
                result.ToInstall.Select(install => Lang.Text(
                    "Download.Comp.Dependency.Install.ListItem",
                    install.ProjectName,
                    install.Source,
                    install.File.DisplayName,
                    install.File.Version)));
            var message = Lang.Text("Download.Comp.Dependency.Install.Message", dependencies);
            var dialogResult = ModMain.MyMsgBox(
                message,
                FormatTitle(Lang.Text("Download.Comp.Dependency.Install.Title"), titleSuffix),
                Lang.Text("Download.Comp.Dependency.Install.WithDependencies"),
                Lang.Text("Download.Comp.Dependency.Install.WithoutDependencies"),
                Lang.Text("Download.Comp.Dependency.Install.Cancel"),
                forceWait: true);

            return dialogResult switch
            {
                1 => ModComp.CompDepsInstallTypes.WithDeps,
                2 => ModComp.CompDepsInstallTypes.WithoutDeps,
                3 => ModComp.CompDepsInstallTypes.Cancel,
                _ => ModComp.CompDepsInstallTypes.Cancel
            };
        }

        return ModComp.CompDepsInstallTypes.WithDeps;
    }

    /// <summary>
    /// 提取目标实例的模组加载器列表；若无实例信息或未匹配到加载器则回退到候选加载器集合。
    /// </summary>
    public static List<CompLoaderType> ResolveTargetLoaders(
        McInstance? targetInstance,
        IReadOnlyCollection<CompLoaderType>? fallbackLoaders = null)
    {
        var targetLoaders = new List<CompLoaderType>();
        if (targetInstance is not null)
        {
            if (targetInstance.Info.HasForge || targetInstance.Info.HasCleanroom)
                targetLoaders.Add(CompLoaderType.Forge);
            if (targetInstance.Info.HasFabric || targetInstance.Info.HasLegacyFabric)
                targetLoaders.Add(CompLoaderType.Fabric);
            if (targetInstance.Info.HasQuilt)
                targetLoaders.Add(CompLoaderType.Quilt);
            if (targetInstance.Info.HasNeoForge)
                targetLoaders.Add(CompLoaderType.NeoForge);
            if (targetInstance.Info.HasLiteLoader)
                targetLoaders.Add(CompLoaderType.LiteLoader);
        }

        if (targetLoaders.Count == 0 && fallbackLoaders is { Count: > 0 })
        {
            targetLoaders.AddRange(fallbackLoaders);
        }

        return targetLoaders;
    }

    /// <summary>
    /// 准备资源下载列表；当资源为模组且启用了自动安装前置时，检测缺失的必需前置并弹窗确认后合并进下载队列。
    /// </summary>
    public static List<DownloadFile> PrepareResourceDownload(
        CompFile file,
        CompProject project,
        string target,
        List<CompLoaderType> allowedLoaders,
        IEnumerable<McInstance> knownInstances,
        ModLoader.LoaderTask<int, List<DownloadFile>> task,
        McInstance? explicitInstance = null,
        string? dialogContextSuffix = null,
        Func<string, bool>? tryReservePath = null,
        Action<int>? onPrepared = null)
    {
        var cancellationToken = task.AbortedToken;
        cancellationToken.ThrowIfCancellationRequested();
        var downloads = new List<DownloadFile>();
        var dependencyCount = 0;

        bool hasDependencies;
        lock (file)
        {
            hasDependencies = file.Dependencies.Count > 0;
        }

        if (file.Type == ModComp.CompType.Mod && Config.Download.Comp.AutoInstallDependencies && hasDependencies)
        {
            var targetDir = ModBase.GetPathFromFullPath(target);
            using var resolutionCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            resolutionCancellation.CancelAfter(TimeSpan.FromMinutes(2));
            var resolutionToken = resolutionCancellation.Token;
            try
            {
                var targetInstance = explicitInstance ?? knownInstances.Distinct().FirstOrDefault(instance =>
                    targetDir.StartsWith(instance.PathIndie, StringComparison.OrdinalIgnoreCase));
                if (targetInstance is not null && !targetInstance.IsLoaded)
                    targetInstance.Load();

                var mcVersion = targetInstance?.Info?.VanillaName
                                ?? file.GameVersions.FirstOrDefault(static version => version.Contains("."))
                                ?? string.Empty;
                var targetLoaders = ResolveTargetLoaders(targetInstance, allowedLoaders);

                int depCount;
                lock (file)
                {
                    depCount = file.Dependencies.Count;
                }
                ModBase.Log($"[CompDeps] 开始解析必需前置: {depCount} 个依赖");
                var request = BuildRequest(file, project, mcVersion, targetLoaders, targetDir, resolutionToken);
                task.Progress = 0.2;
                var result = new ModDependencyResolver().Resolve(request, resolutionToken);
                // 下载信息也在后台准备；用户确认期间不计算检索超时。
                var depDownloads = BuildDependencyDownloads(result, targetDir, resolutionToken);
                resolutionToken.ThrowIfCancellationRequested();
                resolutionCancellation.CancelAfter(Timeout.InfiniteTimeSpan);
                task.Progress = 0.8;

                if (result.Unresolved.Any() || result.ToInstall.Any())
                {
                    var installChoice = ModBase.RunInUiWait(() =>
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        return ConfirmDependencyInstall(result, dialogContextSuffix);
                    });
                    cancellationToken.ThrowIfCancellationRequested();
                    switch (installChoice)
                    {
                        case ModComp.CompDepsInstallTypes.WithDeps:
                            foreach (var (_, depDownloadFile) in depDownloads)
                            {
                                if (tryReservePath is null || tryReservePath(depDownloadFile.LocalPath))
                                {
                                    downloads.Add(depDownloadFile);
                                    dependencyCount++;
                                }
                            }
                            ModBase.Log($"[CompDeps] 准备下载: {dependencyCount} 个前置");
                            break;
                        case ModComp.CompDepsInstallTypes.Unresolved:
                        case ModComp.CompDepsInstallTypes.WithoutDeps:
                            ModBase.Log("[CompDeps] 用户选择仅下载 Mod 本体，跳过前置下载");
                            break;
                        default:
                            ModBase.Log("[CompDeps] 用户取消安装");
                            throw new ModBase.CancelledException();
                    }
                }
                else
                {
                    ModBase.Log("[CompDeps] 已满足: 所有必需前置已安装");
                }
            }
            catch (ModBase.CancelledException)
            {
                throw;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw new ModBase.CancelledException();
            }
            catch (Exception depEx) when (!cancellationToken.IsCancellationRequested)
            {
                ModBase.Log(depEx, "[CompDeps] 依赖解析失败或超时，跳过前置安装");
                var message = ExceptionDetails.Compose(
                    Lang.Text("Download.Comp.Dependency.ResolveFailed.Message"), depEx);
                ModBase.RunInUiWait(() =>
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    ModMain.MyMsgBox(message,
                        Lang.Text("Download.Comp.Dependency.ResolveFailed.Title"),
                        Lang.Text("Download.Comp.Dependency.ResolveFailed.Continue"),
                        isWarn: true, forceWait: true);
                });
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        downloads.Add(file.Type == ModComp.CompType.Mod
            ? file.ToNetFile(target)
            : file.ToNetFile(target, ModComp.DownloadReason.Standalone, null));
        onPrepared?.Invoke(dependencyCount);
        return downloads;
    }

    /// <summary>
    ///     Shows abort message when dependency resolution was cancelled by user or failed.
    /// </summary>
    public static void ShowDependencyAbortMessage(string reason)
    {
        ModMain.MyMsgBox(
            Lang.Text("Download.Comp.Dependency.Abort.Message", reason),
            Lang.Text("Download.Comp.Dependency.Abort.Title"),
            Lang.Text("Common.Action.Confirm"),
            isWarn: false,
            forceWait: true);
    }

    private static string GetSource(bool fromCurseForge)
    {
        return fromCurseForge ? "CurseForge" : "Modrinth";
    }

    private static List<string> ToLoaderNames(IEnumerable<CompLoaderType>? loaders)
    {
        if (loaders is null)
        {
            return new List<string>();
        }

        return loaders
            .Where(static loader => loader != CompLoaderType.Any)
            .Select(static loader => loader.ToString())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static int MapReleaseType(CompFileStatus status)
    {
        return status switch
        {
            CompFileStatus.Release => 1,
            CompFileStatus.Beta => 2,
            CompFileStatus.Alpha => 3,
            _ => 1,
        };
    }
}
