using System.Windows;
using System.Windows.Controls;

namespace PCL;

/// <summary>Places running-instance cards in one row directly under the matching game card.</summary>
public static class RunningInstanceCards
{
    public static void Refresh(Panel host, McInstance? selected)
    {
        var path = selected?.PathInstance;
        var live = Live(path);
        foreach (var child in host.Children.OfType<RunningInstanceCard>().ToArray())
            if (live.All(item => item.id != child.ProcessId)) host.Children.Remove(child);
        foreach (var (id, window, lines, name) in live)
        {
            if (host.Children.OfType<RunningInstanceCard>().Any(card => card.ProcessId == id)) continue;
            host.Children.Add(new RunningInstanceCard(name, id, window, lines));
        }
        Arrange(host);
    }

    /// <summary>One process fills the row; two share it; N processes each take 1/N.</summary>
    public static void Arrange(Panel host)
    {
        var cards = host.Children.OfType<RunningInstanceCard>().ToArray();
        var count = cards.Length;
        host.Visibility = count == 0 ? Visibility.Collapsed : Visibility.Visible;
        if (count == 0) return;
        var share = new GridLength(1, GridUnitType.Star);
        for (var i = 0; i < count; i++)
        {
            var card = cards[i];
            card.HorizontalAlignment = HorizontalAlignment.Stretch;
            card.Margin = new Thickness(i == 0 ? 0 : 8, 0, 0, 0);
            if (host is Grid grid)
            {
                while (grid.ColumnDefinitions.Count < count)
                    grid.ColumnDefinitions.Add(new ColumnDefinition { Width = share });
                while (grid.ColumnDefinitions.Count > count)
                    grid.ColumnDefinitions.RemoveAt(grid.ColumnDefinitions.Count - 1);
                grid.ColumnDefinitions[i].Width = share;
                Grid.SetColumn(card, i);
                Grid.SetRow(card, 0);
            }
        }
    }

    private static List<(int id, Func<nint> window, Func<IReadOnlyList<string?>> lines, string name)> Live(string? path)
    {
        var result = new List<(int, Func<nint>, Func<IReadOnlyList<string?>>, string)>();
        if (string.IsNullOrEmpty(path)) return result;
        ModWatcher.Watcher[] watchers;
        try { watchers = ModWatcher.mcWatcherList.ToArray(); }
        catch (InvalidOperationException) { return result; }
        foreach (var watcher in watchers)
        {
            if (!string.Equals(watcher.version?.PathInstance, path, StringComparison.OrdinalIgnoreCase)) continue;
            if (watcher.State is not (ModWatcher.Watcher.MinecraftState.Loading or ModWatcher.Watcher.MinecraftState.Running))
                continue;
            int id;
            try
            {
                if (watcher.gameProcess is null || watcher.gameProcess.HasExited) continue;
                id = watcher.gameProcess.Id;
            }
            catch (Exception) { continue; }
            var current = watcher;
            result.Add((id, () => current.WindowHandle, () => current.GetFullLog(), current.version?.Name ?? ""));
        }
        return result;
    }
}
