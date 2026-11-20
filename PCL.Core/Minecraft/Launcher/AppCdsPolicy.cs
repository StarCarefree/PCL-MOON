using System;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace PCL.Core.Minecraft.Launcher;

/// <summary>AppCDS 启用决策；<see cref="Reason"/> 仅用于日志，不参与逻辑分支。</summary>
public readonly record struct AppCdsDecision(bool Enabled, string Reason);

/// <summary>
/// 应用类数据共享（AppCDS）自动归档的启用资格检查。
/// <para>
/// 仅在 JVM 能自行校验并回退的前提下追加 <c>-XX:+AutoCreateSharedArchive -XX:SharedArchiveFile</c>：
/// 归档与 JDK/类路径不一致时由 JVM 自动忽略并在正常退出时重建，不会让游戏启动失败。
/// 任何已存在的 CDS 参数、Java Agent、参数文件或无法判定的命令行一律保持原样，不启用。
/// </para>
/// <para>
/// 为什么拒绝 Java Agent：JVM 在发现 <c>-javaagent</c> 且需要动态归档时会直接退出（
/// <c>Must enable AllowArchivingWithJavaAgent in order to run Java agent during CDS dumping</c>），
/// 该选项属于诊断选项，不作为兼容路径使用。
/// </para>
/// </summary>
public static class AppCdsPolicy
{
    /// <summary>首个提供 <c>-XX:+AutoCreateSharedArchive</c> 的 JDK 版本。</summary>
    public const int MinJavaMajor = 19;

    /// <summary>已验证的最高 JDK 版本；更高版本的 VM 选项可能被移除，未验证前不追加。</summary>
    public const int MaxJavaMajor = 25;

    private static readonly string[] ConflictPrefixes =
    [
        "-Xshare", "-XX:SharedArchiveFile", "-XX:ArchiveClassesAtExit", "-XX:+AutoCreateSharedArchive",
        "-XX:-AutoCreateSharedArchive", "-XX:SharedClassListFile", "-XX:DumpLoadedClassList",
        "-XX:+DumpSharedSpaces", "-XX:+RequireSharedSpaces", "-XX:-UseSharedSpaces", "-XX:+UseSharedSpaces",
        "-XX:+AllowArchivingWithJavaAgent", "-XX:SharedBaseAddress", "-XX:AOT", "-XX:+AOT"
    ];

    private static readonly string[] AgentPrefixes = ["-javaagent:", "-agentlib:", "-agentpath:", "-Xrun", "-Xdebug"];

    /// <summary>
    /// 判定当前启动是否可以追加 AppCDS 参数。
    /// </summary>
    /// <param name="enabled">用户是否开启。</param>
    /// <param name="javaMajor">Java 主版本。</param>
    /// <param name="is64Bit">是否 64 位 JVM。</param>
    /// <param name="hasBaseArchive">JDK 自带默认 CDS 归档（<c>classes.jsa</c>）是否存在；动态归档依赖它。</param>
    /// <param name="archivePath">将使用的归档文件绝对路径。</param>
    /// <param name="arguments">当前完整的 JVM+游戏参数串。</param>
    public static AppCdsDecision Evaluate(bool enabled, int javaMajor, bool is64Bit, bool hasBaseArchive,
        string? archivePath, string? arguments)
    {
        if (!enabled) return new(false, "未开启");
        if (javaMajor is < MinJavaMajor or > MaxJavaMajor) return new(false, $"Java {javaMajor} 不在已验证范围 {MinJavaMajor}-{MaxJavaMajor}");
        if (!is64Bit) return new(false, "非 64 位 Java");
        if (!hasBaseArchive) return new(false, "当前 Java 不带默认 CDS 归档（classes.jsa）");
        if (string.IsNullOrWhiteSpace(archivePath) || !IsAscii(archivePath)) return new(false, "归档路径为空或含非 ASCII 字符");
        if (archivePath.Contains('"')) return new(false, "归档路径含引号");
        if (arguments is null) return new(false, "参数不可用");
        // 不猜测不完整或含转义引号的命令行。
        if (arguments.Count(ch => ch == '"') % 2 != 0 || arguments.Contains("\\\"", StringComparison.Ordinal))
            return new(false, "参数引号不完整或含转义引号");
        foreach (Match match in Regex.Matches(arguments, TokenPattern))
        {
            var token = match.Value.Replace("\"", "", StringComparison.Ordinal);
            if (token.StartsWith('@') || token.StartsWith("-XX:Flags=", StringComparison.Ordinal))
                return new(false, "存在外置参数文件");
            if (AgentPrefixes.Any(prefix => token.StartsWith(prefix, StringComparison.Ordinal)))
                return new(false, "存在 Java Agent");
            if (ConflictPrefixes.Any(prefix => token.StartsWith(prefix, StringComparison.Ordinal)))
                return new(false, "已存在 CDS/AOT 相关参数");
        }
        return new(true, "满足条件");
    }

    private const string TokenPattern = """(?:[^\s"]|"[^"]*")+""";

    /// <summary>生成追加到 JVM 参数最前面的 AppCDS 参数串（末尾带空格）。</summary>
    public static string BuildArguments(string archivePath)
        => "-XX:+AutoCreateSharedArchive -XX:SharedArchiveFile=\"" + archivePath + "\" ";

    /// <summary>
    /// 归档文件名：Java 可执行文件与实例路径的规范化哈希。不同 JDK、不同实例互不共享，避免互相覆盖。
    /// </summary>
    public static string ArchiveFileName(string javaExePath, string instancePath)
    {
        var identity = Normalize(javaExePath) + "\n" + Normalize(instancePath);
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(identity));
        return Convert.ToHexStringLower(hash.AsSpan(0, 12)) + ".jsa";
    }

    /// <summary>JDK 默认归档在不同布局下的候选相对路径（相对 Java 根目录）。</summary>
    public static readonly string[] BaseArchiveCandidates =
    [
        @"bin\server\classes.jsa", @"bin\client\classes.jsa", @"lib\server\classes.jsa", @"lib\client\classes.jsa"
    ];

    public static bool IsAscii(string value) => value.All(ch => ch < 0x80);

    private static string Normalize(string path)
        => (path ?? "").Replace('\\', '/').TrimEnd('/').ToLowerInvariant();
}
