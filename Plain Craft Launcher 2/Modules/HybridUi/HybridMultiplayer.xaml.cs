using System;
using System.Windows;
using System.Windows.Controls;

namespace PCL;

public partial class HybridMultiplayer : UserControl
{
    private readonly Func<FrameworkElement> _createServers;

    public HybridMultiplayer() : this(() => new VisitedServerListView()) { }

    // Keep the host independently testable; the server list is created on first show.
    internal HybridMultiplayer(Func<FrameworkElement> createServers)
    {
        InitializeComponent();
        _createServers = createServers;
        IsVisibleChanged += (_, _) => { if (IsVisible) EnsureServers(); };
    }

    internal void EnsureServers() => ServerHost.Content ??= _createServers();
}
