using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;

namespace PCL.Core.App.Essentials;

public static class UpdateHelper
{
    /// <summary>
    /// 去掉发布页附加的安装包说明和校验值，保留更新日志正文。
    /// </summary>
    public static string CleanChangelog(string? markdown)
    {
        if (string.IsNullOrEmpty(markdown)) return string.Empty;

        var paragraphs = Regex.Split(markdown, @"\r?\n[ \t]*\r?\n");
        var content = paragraphs.Where(paragraph =>
            !Regex.IsMatch(paragraph, @"^\s*下载[ \t]+`pcl-moon-[^`\r\n]+\.exe`[。\s]", RegexOptions.IgnoreCase) &&
            !Regex.IsMatch(paragraph, @"^\s*SHA-256:[ \t]*`?[0-9a-f]{64}`?\s*$", RegexOptions.IgnoreCase))
            .ToArray();
        return content.Length == paragraphs.Length ? markdown : string.Join("\n\n", content).Trim();
    }

    /// <summary>
    /// 从更新日志提取适合设置页展示的简短摘要；优先读取 &lt;summary&gt; 标签，缺失时从正文自动提炼要点。
    /// </summary>
    public static string ExtractSummary(string? markdown)
    {
        if (string.IsNullOrWhiteSpace(markdown)) return string.Empty;

        var cleaned = CleanChangelog(markdown);
        var explicitMatch = Regex.Match(cleaned, @"<summary>\s*(.*?)\s*</summary>",
            RegexOptions.IgnoreCase | RegexOptions.Singleline);
        if (explicitMatch.Success)
        {
            var collapsed = Regex.Replace(explicitMatch.Groups[1].Value, @"\s+", " ").Trim();
            if (!string.IsNullOrEmpty(collapsed))
                return collapsed;
        }

        var plain = Regex.Replace(cleaned, @"<!--[\s\S]*?-->", string.Empty);
        var items = new System.Collections.Generic.List<string>(3);
        foreach (var rawLine in plain.Split('\n'))
        {
            var line = rawLine.Trim('\r').Trim();
            if (string.IsNullOrEmpty(line)) continue;
            if (Regex.IsMatch(line, @"^[-*_]{3,}$")) continue;
            if (Regex.IsMatch(line, @"^#{1,6}\s+")) continue;

            var text = Regex.Replace(line, @"^\s*(?:[-*+]|\d+\.)\s+", string.Empty);
            text = Regex.Replace(text, @"\[([^\]]+)\]\([^)]+\)", "$1");
            text = text.Replace("**", string.Empty).Replace("__", string.Empty).Replace("`", string.Empty);
            text = Regex.Replace(text, @"\s+", " ").Trim();
            if (string.IsNullOrEmpty(text)) continue;

            items.Add($"• {text}");
            if (items.Count >= 3) break;
        }

        if (items.Count == 0) return string.Empty;
        var summary = string.Join("\n", items);
        return summary.Length > 220 ? summary[..219].TrimEnd() + "…" : summary;
    }

    /// <summary>
    /// 去掉发布说明以及内嵌的 &lt;summary&gt; 摘要元数据块，便于弹窗展示整洁的 Markdown 正文。
    /// </summary>
    public static string StripSummaryMetadata(string? markdown)
    {
        if (string.IsNullOrWhiteSpace(markdown)) return string.Empty;

        var text = CleanChangelog(markdown);
        text = Regex.Replace(text, @"<!--\s*<summary>[\s\S]*?</summary>\s*-->", string.Empty,
            RegexOptions.IgnoreCase);
        text = Regex.Replace(text, @"<summary>[\s\S]*?</summary>", string.Empty,
            RegexOptions.IgnoreCase);
        text = Regex.Replace(text, @"(?:\r?\n){3,}", "\n\n");
        return text.Trim();
    }

    /// <summary>
    /// 判断更新日志首个标题中的版本号是否与指定版本号一致，避免展示旧版本缓存日志。
    /// </summary>
    public static bool MatchesVersion(string? markdown, string? versionName)
    {
        if (string.IsNullOrWhiteSpace(markdown) || string.IsNullOrWhiteSpace(versionName))
            return false;

        var expected = versionName.Trim().TrimStart('v', 'V');
        if (string.IsNullOrEmpty(expected)) return false;

        var withoutComments = Regex.Replace(markdown, @"<!--[\s\S]*?-->", string.Empty);
        var headingMatch = Regex.Match(withoutComments, @"^\s*#{1,3}\s+(.+?)\s*$", RegexOptions.Multiline);
        if (!headingMatch.Success) return false;

        var headingText = headingMatch.Groups[1].Value.Trim();
        var versionMatch = Regex.Match(headingText,
            @"(?<![0-9A-Za-z.-])v?(\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?)");
        return versionMatch.Success &&
               string.Equals(versionMatch.Groups[1].Value, expected, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 更新启动器（替换文件）
    /// </summary>
    /// <param name="source">用于替换的来源文件路径</param>
    /// <param name="target">目标文件路径</param>
    public static Exception? Replace(string source, string target)
    {
        var backup = $"{target}.bak.{DateTime.Now.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture)}";
        Exception? lastEx = null;
        try
        {
            source = Path.GetFullPath(source);
            target = Path.GetFullPath(target);
            // 备份目标文件
            File.Copy(target, backup);
            if (!File.Exists(backup)) throw new FileNotFoundException("备份目标文件失败", backup);
            // 删除原文件并等待文件删除事件
            var watcher = new FileSystemWatcher(Basics.GetParentPathOrEmpty(target), Path.GetFileName(target));
            var deletedEvent = new ManualResetEventSlim(false);
            watcher.Deleted += (_, _) => deletedEvent.Set();
            watcher.EnableRaisingEvents = true;
            File.Delete(target);
            if (!deletedEvent.Wait(TimeSpan.FromSeconds(3))) throw new TimeoutException("删除目标文件失败");
            watcher.EnableRaisingEvents = false;
            watcher.Dispose();
            // 复制到目标文件
            File.Copy(source, target);
            if (!File.Exists(target)) throw new FileNotFoundException("复制到目标文件失败", target);
        }
        catch (Exception ex)
        {
            // 出错：恢复原文件并返回异常
            if (File.Exists(backup) && !File.Exists(target)) File.Move(backup, target);
            lastEx = ex;
        }
        if (File.Exists(backup)) File.Delete(backup); // 删除备份文件
        return lastEx;
    }
}
