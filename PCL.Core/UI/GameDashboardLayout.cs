using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace PCL.Core.UI;

public sealed class GameDashboardWidget
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Kind { get; set; } = "greeting";
    public string Title { get; set; } = "";
    public bool Wide { get; set; }
    public string Target { get; set; } = "";
    public string Text { get; set; } = "";
}

public sealed class GameDashboardLayout
{
    private const int MaxWidgets = 40;
    private const int MaxFileBytes = 1024 * 1024;
    private const int MaxTitleLength = 80;
    private const int MaxTextLength = 4000;
    private const int MaxTargetLength = 2048;

    private static readonly HashSet<string> AllowedKinds = new(StringComparer.OrdinalIgnoreCase)
    {
        "hero", "greeting", "recent", "library", "calendar", "stats", "shortcut", "note",
        "favorites", "worlds", "servers", "actions", "folders"
    };

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    public int Columns { get; set; } = 2;
    public List<GameDashboardWidget> Widgets { get; set; } = [];

    public static GameDashboardLayout CreateDefault() => new()
    {
        Columns = 2,
        Widgets =
        [
            new() { Kind = "hero", Wide = true },
            new() { Kind = "greeting" },
            new() { Kind = "library" }
        ]
    };

    public GameDashboardLayout Clone()
    {
        var copy = new GameDashboardLayout { Columns = Columns, Widgets = [] };
        foreach (var widget in Widgets ?? [])
        {
            if (widget is null) continue;
            copy.Widgets.Add(new GameDashboardWidget
            {
                Id = widget.Id,
                Kind = widget.Kind,
                Title = widget.Title,
                Wide = widget.Wide,
                Target = widget.Target,
                Text = widget.Text
            });
        }
        return copy;
    }

    public static GameDashboardLayout Load(string path)
    {
        try
        {
            if (!File.Exists(path)) return CreateDefault();
            if (new FileInfo(path).Length > MaxFileBytes) return CreateDefault();
            var json = File.ReadAllText(path);
            if (string.IsNullOrWhiteSpace(json)) return CreateDefault();
            var loaded = JsonSerializer.Deserialize<GameDashboardLayout>(json, JsonOptions);
            if (loaded is null) return CreateDefault();
            loaded.Normalize();
            return loaded;
        }
        catch (Exception ex) when (ex is JsonException or InvalidDataException or IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            return CreateDefault();
        }
    }

    public void Save(string path)
    {
        Normalize();
        var json = JsonSerializer.Serialize(this, JsonOptions);
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temp, json);
            if (File.Exists(path)) File.Replace(temp, path, destinationBackupFileName: null);
            else File.Move(temp, path);
        }
        catch
        {
            try { if (File.Exists(temp)) File.Delete(temp); }
            catch (Exception) { /* keep the original Save exception */ }
            throw;
        }
    }

    private void Normalize()
    {
        Columns = Columns < 1 ? 1 : Columns > 2 ? 2 : Columns;
        Widgets ??= [];

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var normalized = new List<GameDashboardWidget>(Math.Min(Widgets.Count, MaxWidgets));
        foreach (var widget in Widgets)
        {
            if (normalized.Count >= MaxWidgets) break;
            if (widget is null) continue;
            var kind = widget.Kind?.Trim() ?? "";
            if (!AllowedKinds.Contains(kind)) continue;

            var id = widget.Id?.Trim() ?? "";
            if (id.Length == 0 || !seen.Add(id))
            {
                do { id = Guid.NewGuid().ToString("N"); }
                while (!seen.Add(id));
            }

            widget.Id = id;
            widget.Kind = kind.ToLowerInvariant();
            widget.Title = Truncate(widget.Title, MaxTitleLength);
            widget.Target = Truncate(widget.Target, MaxTargetLength);
            widget.Text = Truncate(widget.Text, MaxTextLength);
            normalized.Add(widget);
        }

        MergeLegacyRecentIntoLibrary(normalized);
        Widgets = normalized;
    }

    /// <summary>
    /// 兼容旧布局：将遗留的 recent 组件并入 library，同时存在时保留较前位置并沿用 recent 的宽度设置。
    /// </summary>
    private static void MergeLegacyRecentIntoLibrary(List<GameDashboardWidget> widgets)
    {
        var recentIndex = widgets.FindIndex(item => item.Kind == "recent");
        if (recentIndex < 0) return;
        var libraryIndex = widgets.FindIndex(item => item.Kind == "library");
        if (libraryIndex >= 0)
        {
            var recent = widgets[recentIndex];
            var library = widgets[libraryIndex];
            var keepIndex = Math.Min(recentIndex, libraryIndex);
            var merged = widgets[keepIndex];
            merged.Kind = "library";
            merged.Wide = recent.Wide;
            if (string.IsNullOrEmpty(merged.Title))
                merged.Title = !string.IsNullOrEmpty(library.Title) ? library.Title : recent.Title;
            widgets.RemoveAll(item => !ReferenceEquals(item, merged) && item.Kind is "recent" or "library");
        }
        else
        {
            var firstRecent = widgets[recentIndex];
            firstRecent.Kind = "library";
            widgets.RemoveAll(item => !ReferenceEquals(item, firstRecent) && item.Kind == "recent");
        }
    }

    private static string Truncate(string? value, int max)
    {
        var text = value ?? "";
        return text.Length <= max ? text : text[..max];
    }
}
