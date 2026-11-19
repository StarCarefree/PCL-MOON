using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace PCL.Core.Minecraft.Launcher;

/// <summary>单个模组的入口点声明记录。</summary>
public sealed record ModEntrypointItem(string ModName, string ModId, string Entrypoint);

/// <summary>按入口点类型归类的加载阶段。</summary>
public sealed record ModEntrypointPhase(string Type, string DisplayType, IReadOnlyList<ModEntrypointItem> Entries);

/// <summary>
/// 在启动器进程内扫描实例 mods 目录的 Fabric/Quilt 清单并按声明阶段推送加载进度，
/// 替代原跨进程 Java Agent 桥接与临时文件轮询机制。
/// </summary>
public sealed class ModLoadingProgressDriver : IDisposable
{
    private static readonly string[] Order =
    [
        "preLaunch", "pre_launch", "main", "init", "client", "server",
        "client_init", "server_init", "client_game", "server_game", "modmenu"
    ];

    private static readonly JsonDocumentOptions LenientJsonOptions = new()
    {
        AllowTrailingCommas = true,
        CommentHandling = JsonCommentHandling.Skip,
        MaxDepth = 64
    };

    private readonly LaunchProgressState _state;
    private readonly string _gameDirectory;
    private readonly string? _target;
    private readonly int? _stepDelayMsOverride;
    private readonly CancellationTokenSource _cts = new();

    private Task? _runTask;
    private int _started;
    private int _disposed;

    /// <summary>创建进程内模组加载进度驱动器。</summary>
    public ModLoadingProgressDriver(LaunchProgressState state, string gameDirectory, string? target = null, int? stepDelayMsOverride = null)
    {
        _state = state ?? throw new ArgumentNullException(nameof(state));
        _gameDirectory = gameDirectory ?? string.Empty;
        _target = target;
        _stepDelayMsOverride = stepDelayMsOverride;
    }

    /// <summary>幂等启动异步扫描与进度推送任务。</summary>
    public Task Start()
    {
        if (Volatile.Read(ref _disposed) != 0) return Task.CompletedTask;
        if (Interlocked.Exchange(ref _started, 1) != 0) return _runTask ?? Task.CompletedTask;

        var task = Task.Run(() => RunAsync(_cts.Token));
        _runTask = task;
        if (Volatile.Read(ref _disposed) != 0) CancelSafely();
        return task;
    }

    /// <summary>扫描指定 mods 目录下的 *.jar 模组清单并按规范顺序整理入口点阶段。</summary>
    public static IReadOnlyList<ModEntrypointPhase> ScanEntrypoints(string modsDirectory, out int modCount, out string? fallbackTarget)
    {
        modCount = 0;
        fallbackTarget = null;
        if (string.IsNullOrWhiteSpace(modsDirectory) || !Directory.Exists(modsDirectory))
            return [];

        var mods = new List<ParsedMod>();
        var seenIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var jarPath in Directory.EnumerateFiles(modsDirectory, "*.jar").OrderBy(static p => p, StringComparer.OrdinalIgnoreCase))
        {
            var parsed = TryReadMod(jarPath);
            if (parsed is not null && seenIds.Add(parsed.Id))
                mods.Add(parsed);
        }

        modCount = mods.Count;
        if (mods.Count > 0)
            fallbackTarget = $"{mods[0].Name} {mods[0].Version}".Trim();

        var byType = new Dictionary<string, List<ModEntrypointItem>>(StringComparer.Ordinal);
        var discoveredTypes = new List<string>();
        foreach (var mod in mods)
        {
            foreach (var (type, values) in mod.Entrypoints)
            {
                if (values.Count == 0) continue;
                if (!byType.TryGetValue(type, out var sink))
                {
                    sink = [];
                    byType[type] = sink;
                    discoveredTypes.Add(type);
                }
                foreach (var entrypoint in values)
                    sink.Add(new ModEntrypointItem(mod.Name, mod.Id, entrypoint));
            }
        }

        var phases = new List<ModEntrypointPhase>();
        var usedTypes = new HashSet<string>(StringComparer.Ordinal);
        foreach (var type in Order)
        {
            if (!byType.TryGetValue(type, out var items) || items.Count == 0) continue;
            phases.Add(new ModEntrypointPhase(type, SimpleType(type), items));
            usedTypes.Add(type);
        }

        foreach (var type in discoveredTypes)
        {
            if (!usedTypes.Add(type)) continue;
            var items = byType[type];
            if (items.Count > 0)
                phases.Add(new ModEntrypointPhase(type, SimpleType(type), items));
        }

        return phases;
    }

    /// <summary>构造与原 BridgeAgent 保持一致的入口点阶段展示标题。</summary>
    public static string FormatLoadingStage(string type, string simpleType, int current, int total, string? modName)
    {
        var baseText = $"Loading '{type}' ({simpleType}) — {current}/{total}";
        return string.IsNullOrEmpty(modName) ? baseText : $"{baseText} — {modName}";
    }

    /// <summary>将 Fabric/Quilt 入口点键名映射为可读的初始化接口类名。</summary>
    public static string SimpleType(string? type)
    {
        if (type is "main" or "init") return "ModInitializer";
        if (type is "client" or "client_init") return "ClientModInitializer";
        if (type is "server" or "server_init") return "DedicatedServerModInitializer";
        if (type is "preLaunch" or "pre_launch") return "PreLaunchEntrypoint";
        if (type == "modmenu") return "ModMenuApi";
        if (string.IsNullOrEmpty(type)) return "Entrypoint";

        var sb = new StringBuilder(type.Length + 10);
        var upper = true;
        foreach (var c in type)
        {
            if (c is '_' or '-' or ' ')
            {
                upper = true;
                continue;
            }
            sb.Append(upper ? char.ToUpperInvariant(c) : c);
            upper = false;
        }
        return sb.Length == 0 ? "Entrypoint" : sb.Append("Entrypoint").ToString();
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        CancelSafely();
        try { _cts.Dispose(); } catch (ObjectDisposedException) { }
    }

    private async Task RunAsync(CancellationToken ct)
    {
        try
        {
            ct.ThrowIfCancellationRequested();
            var modsDirectory = string.IsNullOrWhiteSpace(_gameDirectory) ? string.Empty : Path.Combine(_gameDirectory, "mods");
            var phases = ScanEntrypoints(modsDirectory, out var modCount, out var fallbackTarget);
            ct.ThrowIfCancellationRequested();

            var resolvedTarget = !string.IsNullOrWhiteSpace(_target) ? _target : (fallbackTarget ?? string.Empty);
            _state.ReportProgress("准备模组加载", $"已读取 {modCount} 个模组", resolvedTarget, 0, 0);

            var totalSteps = 0;
            foreach (var phase in phases) totalSteps += phase.Entries.Count;
            var gapMs = _stepDelayMsOverride is int ov
                ? Math.Max(0, ov)
                : (totalSteps == 0 ? 0 : Math.Clamp(4000 / totalSteps, 15, 250));

            foreach (var phase in phases)
            {
                ct.ThrowIfCancellationRequested();
                var stage = FormatLoadingStage(phase.Type, phase.DisplayType, 0, phase.Entries.Count, null);
                _state.ReportProgress(stage, $"入口点 '{phase.Type}' ({phase.DisplayType})", resolvedTarget, 0, phase.Entries.Count);

                for (var i = 0; i < phase.Entries.Count; i++)
                {
                    ct.ThrowIfCancellationRequested();
                    if (Volatile.Read(ref _disposed) != 0) return;

                    var item = phase.Entries[i];
                    stage = FormatLoadingStage(phase.Type, phase.DisplayType, i + 1, phase.Entries.Count, item.ModName);
                    var detail = string.IsNullOrEmpty(item.ModId) ? item.ModName : $"{item.ModName}  ·  {item.ModId}";
                    _state.ReportProgress(stage, detail, resolvedTarget, i + 1, phase.Entries.Count);

                    if (gapMs > 0)
                        await Task.Delay(gapMs, ct).ConfigureAwait(false);
                }
            }

            ct.ThrowIfCancellationRequested();
            if (Volatile.Read(ref _disposed) == 0)
                _state.ReportModComplete(resolvedTarget);
        }
        catch (OperationCanceledException)
        {
            // 会话关闭或释放时安静终止
        }
        catch (ObjectDisposedException) when (Volatile.Read(ref _disposed) != 0)
        {
            // Dispose 与 Task.Delay 并发时的安全兜底
        }
        catch (Exception)
        {
            if (Volatile.Read(ref _disposed) == 0 && !ct.IsCancellationRequested)
                _state.ReportUnavailable("无法读取整合包进度", "将按游戏窗口完成交接", "");
        }
    }

    private void CancelSafely()
    {
        try { _cts.Cancel(); } catch (ObjectDisposedException) { }
    }

    private static ParsedMod? TryReadMod(string jarPath)
    {
        try
        {
            // 使用宽松共享模式打开，避免与刚启动的游戏 JVM 发生文件锁竞争
            using var stream = new FileStream(jarPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var zip = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: false);
            var entry = zip.GetEntry("fabric.mod.json") ?? zip.GetEntry("quilt.mod.json");
            if (entry is null) return null;

            using var entryStream = entry.Open();
            using var doc = JsonDocument.Parse(entryStream, LenientJsonOptions);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;

            string? id = null;
            string? name = null;
            string? version = null;
            JsonElement? entrypointsObj = null;

            if (root.TryGetProperty("quilt_loader", out var ql) && ql.ValueKind == JsonValueKind.Object)
            {
                id = ReadString(ql, "id");
                version = ReadString(ql, "version");
                if (ql.TryGetProperty("metadata", out var meta) && meta.ValueKind == JsonValueKind.Object)
                    name = ReadString(meta, "name");
                if (ql.TryGetProperty("entrypoints", out var qEp) && qEp.ValueKind == JsonValueKind.Object)
                    entrypointsObj = qEp;
            }

            id = string.IsNullOrEmpty(id) ? ReadString(root, "id") : id;
            if (string.IsNullOrEmpty(id)) return null;

            name = string.IsNullOrEmpty(name) ? ReadString(root, "name") : name;
            version = string.IsNullOrEmpty(version) ? ReadString(root, "version") : version;
            if (entrypointsObj is null && root.TryGetProperty("entrypoints", out var fEp) && fEp.ValueKind == JsonValueKind.Object)
                entrypointsObj = fEp;

            var entrypoints = entrypointsObj is { } ep ? ParseEntrypoints(ep) : [];
            return new ParsedMod(id, string.IsNullOrEmpty(name) ? id : name, version ?? string.Empty, entrypoints);
        }
        catch
        {
            return null;
        }
    }

    private static List<KeyValuePair<string, List<string>>> ParseEntrypoints(JsonElement entrypointsObj)
    {
        var result = new List<KeyValuePair<string, List<string>>>();
        foreach (var prop in entrypointsObj.EnumerateObject())
        {
            if (prop.Value.ValueKind != JsonValueKind.Array) continue;
            var items = new List<string>();
            ExtractArrayItems(prop.Value, items);
            if (items.Count > 0)
                result.Add(new KeyValuePair<string, List<string>>(prop.Name, items));
        }
        return result;
    }

    private static void ExtractArrayItems(JsonElement array, List<string> sink)
    {
        foreach (var item in array.EnumerateArray())
        {
            switch (item.ValueKind)
            {
                case JsonValueKind.String:
                    sink.Add(item.GetString() ?? string.Empty);
                    break;
                case JsonValueKind.Object:
                    sink.Add(ReadEntrypointObjectLabel(item));
                    break;
                case JsonValueKind.Array:
                    ExtractArrayItems(item, sink);
                    break;
                case JsonValueKind.Undefined:
                    break;
                default:
                    sink.Add(item.GetRawText());
                    break;
            }
        }
    }

    private static string ReadEntrypointObjectLabel(JsonElement obj)
    {
        var val = ReadString(obj, "value");
        if (!string.IsNullOrEmpty(val)) return val;
        var adapter = ReadString(obj, "adapter");
        if (!string.IsNullOrEmpty(adapter)) return adapter;
        return "入口点";
    }

    private static string? ReadString(JsonElement obj, string propertyName)
    {
        if (!obj.TryGetProperty(propertyName, out var el)) return null;
        if (el.ValueKind == JsonValueKind.String)
        {
            var value = el.GetString();
            if (value is null) return null;
            return value.Length <= 2048 ? value : value[..2048];
        }
        if (el.ValueKind == JsonValueKind.Array)
        {
            var flat = new List<string>();
            ExtractArrayItems(el, flat);
            var first = flat.FirstOrDefault(static s => !string.IsNullOrEmpty(s));
            return first is null ? null : (first.Length <= 2048 ? first : first[..2048]);
        }
        return null;
    }

    private sealed record ParsedMod(string Id, string Name, string Version, List<KeyValuePair<string, List<string>>> Entrypoints);
}
