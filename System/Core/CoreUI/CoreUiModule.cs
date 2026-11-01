using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Threading;
using Poseidon.Core.Modules;
using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("CoreUI.Tests")]

namespace Poseidon.Modules.CoreUI;

public sealed class CoreUiModule : IModule, IShell
{
    private const string ViewportUnavailable = "Chart viewport unavailable";
    private const string NoControlPage = "No control page is available.";
    private const string NoTidButtons = "No buttons are available.";

    // H4, H5: the chart+side-panel window always exists; the control-pages window exists only in
    // Dual mode (in Single mode its content is embedded in the chart window's sidebar instead).
    private Window? chartWindow;
    private Window? controlPagesWindow;
    private Window? tidWindow;
    private DispatcherTimer? timer;
    private bool stopping;
    private bool refreshing;
    private DisplayMode displayMode;

    // Chart region (unchanged from B5 to B9).
    private ContentControl? viewportHost;
    private (string FeatureId, long Generation)? viewportSource;
    private bool attachingViewport;

    // H3: always-visible side panels.
    private StackPanel? sidePanelsHost;

    // H6 to H11: control pages.
    private StackPanel? pageSwitcherHost;
    private ContentControl? controlPagesHost;
    private string? activePageId;

    // H17 to H23: the TID.
    private StackPanel? tidButtonsHost;

    // H8, H9, H21: reported here instead of in the (removed, H12) diagnostics panel.
    private readonly List<string> diagnostics = [];

    internal Window? ActiveWindow => chartWindow;
    internal IReadOnlyList<string> Diagnostics => diagnostics;
    public IReadOnlyList<ModuleCapability> Capabilities => [];
    public IReadOnlyDictionary<string, object> Features => new Dictionary<string, object> { ["core.ui.shell"] = this };

    public async ValueTask StartAsync(IModuleHost host, ModuleState? state, CancellationToken cancellationToken)
    {
        if (state is not null)
            throw new InvalidDataException("CoreUI does not accept persisted state yet.");
        // H1: which physical console this deployment is built for, found next to CoreUI's own
        // assembly (the same place CoreRadar finds its own hardware configuration).
        var moduleDirectory = Path.GetDirectoryName(typeof(CoreUiModule).Assembly.Location)!;
        displayMode = ConsoleHardwareConfiguration.Load(moduleDirectory).DisplayMode;

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            stopping = false;
            activePageId = null;
            diagnostics.Clear();
            BuildWindows(host);
            // Feature calls are rejected inside lifecycle methods, so everything that discovers a
            // contribution is attached from the timer rather than during StartAsync.
            timer = new DispatcherTimer(TimeSpan.FromMilliseconds(500), DispatcherPriority.Background,
                (_, _) => _ = RefreshAsync(host));
            if (chartWindow is not null)
                chartWindow.Title = $"Poseidon - generation {host.Status.Generation}";
            chartWindow!.Show();
            controlPagesWindow?.Show();
            tidWindow!.Show();
        });
    }

    private void BuildWindows(IModuleHost host)
    {
        viewportHost = new ContentControl { Margin = new Thickness(12) };
        sidePanelsHost = new StackPanel { Spacing = 8 };
        var sideScroll = new ScrollViewer { Content = sidePanelsHost, Margin = new Thickness(12) };

        pageSwitcherHost = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(12) };
        controlPagesHost = new ContentControl { Margin = new Thickness(12) };
        var controlPagesLayout = new Grid { RowDefinitions = new RowDefinitions("Auto,*") };
        controlPagesLayout.Children.Add(pageSwitcherHost);
        var controlPagesScroll = new ScrollViewer { Content = controlPagesHost };
        Grid.SetRow(controlPagesScroll, 1);
        controlPagesLayout.Children.Add(controlPagesScroll);

        if (displayMode == DisplayMode.Single)
        {
            var sidebar = new Grid { RowDefinitions = new RowDefinitions("*,*") };
            sidebar.Children.Add(sideScroll);
            Grid.SetRow(controlPagesLayout, 1);
            sidebar.Children.Add(controlPagesLayout);

            var layout = new Grid { ColumnDefinitions = new ColumnDefinitions("*,380") };
            layout.Children.Add(viewportHost);
            Grid.SetColumn(sidebar, 1);
            layout.Children.Add(sidebar);

            chartWindow = NewWindow("Poseidon", layout, host);
            controlPagesWindow = null;
        }
        else
        {
            var chartLayout = new Grid { ColumnDefinitions = new ColumnDefinitions("*,380") };
            chartLayout.Children.Add(viewportHost);
            Grid.SetColumn(sideScroll, 1);
            chartLayout.Children.Add(sideScroll);
            chartWindow = NewWindow("Poseidon", chartLayout, host);
            controlPagesWindow = NewWindow("Poseidon - control pages", controlPagesLayout, host);
        }

        tidButtonsHost = new StackPanel { Spacing = 8 };
        var tidScroll = new ScrollViewer { Content = tidButtonsHost, Margin = new Thickness(12) };
        tidWindow = NewWindow("Poseidon - TID", tidScroll, host);
    }

    // H17: a plain resizable, closable window for this milestone (fullscreen on real TID hardware is
    // future follow-up). Every window shares the same keyboard-forwarding (H16) and close-requests-
    // shutdown behavior.
    private Window NewWindow(string title, object content, IModuleHost host)
    {
        var window = new Window
        {
            Title = title, Width = 1000, Height = 650,
            Content = content, WindowStartupLocation = WindowStartupLocation.CenterScreen
        };
        window.Closing += (_, e) =>
        {
            if (!stopping)
            {
                e.Cancel = true;
                host.RequestShutdown();
            }
        };
        window.KeyDown += (_, e) =>
        {
            if (MapKey(e.Key) is { } key)
                host.DispatchShortcut(key, MapModifiers(e.KeyModifiers));
        };
        return window;
    }

    private async Task RefreshAsync(IModuleHost host)
    {
        if (refreshing)
            return;
        refreshing = true;
        try
        {
            var snapshot = host.Status;
            if (chartWindow is not null)
                chartWindow.Title = $"Poseidon - generation {snapshot.Generation}";
            await RefreshViewportAsync(host, snapshot);
            await RefreshSidePanelsAsync(host, snapshot);
            await RefreshPagesPanelsAndTidAsync(host, snapshot);
        }
        finally { refreshing = false; }
    }

    private async Task RefreshViewportAsync(IModuleHost host, RuntimeStatus snapshot)
    {
        if (attachingViewport || viewportHost is null)
            return;
        var provider = snapshot.Features.FirstOrDefault(f => f.PointId == DisplayPoints.Viewport);
        if (provider is null)
        {
            if (viewportSource is not null || viewportHost.Content is null)
            {
                viewportSource = null;
                viewportHost.Content = Placeholder($"{ViewportUnavailable}: no module provides {DisplayPoints.Viewport}.");
            }
            return;
        }
        var source = (provider.FeatureId, snapshot.Generation);
        // A failed provider is not retried until the provider or generation changes.
        if (viewportSource == source)
            return;
        viewportSource = source;
        viewportHost.Content = null;
        attachingViewport = true;
        try
        {
            var view = await host.InvokeAsync<IChartViewport, object>(provider.FeatureId, v => v.CreateView());
            if (stopping || viewportHost is null || viewportSource != source)
                return;
            viewportHost.Content = view as Control ??
                Placeholder($"{ViewportUnavailable}: {provider.ProviderId} returned an unsupported view.");
        }
        catch (Exception ex)
        {
            if (!stopping && viewportHost is not null && viewportSource == source)
                viewportHost.Content = Placeholder($"{ViewportUnavailable}: {ex.Message}");
        }
        finally { attachingViewport = false; }
    }

    // H3: rebuilt fresh every tick (a starting-point simplification; unlike the single viewport above,
    // nothing here gates on a (FeatureId, Generation) signature, so a contributed panel's CreateView is
    // called on every refresh rather than once per attach. Revisit if that ever matters for a real
    // panel.).
    private async Task RefreshSidePanelsAsync(IModuleHost host, RuntimeStatus snapshot)
    {
        if (sidePanelsHost is null)
            return;
        var matches = snapshot.Features.Where(f => f.PointId == CoreUiPoints.SidePanel)
            .OrderBy(f => f.FeatureId, StringComparer.Ordinal).ToList();
        var fetched = new List<(int Order, string FeatureId, object View)>();
        foreach (var f in matches)
        {
            try
            {
                var (order, view) = await host.InvokeAsync<IUiSidePanel, (int Order, object View)>(
                    f.FeatureId, p => (p.Order, p.CreateView()));
                fetched.Add((order, f.FeatureId, view));
            }
            catch { /* unavailable mid-fetch; next tick retries */ }
        }
        sidePanelsHost.Children.Clear();
        foreach (var (_, _, view) in fetched.OrderBy(x => x.Order).ThenBy(x => x.FeatureId, StringComparer.Ordinal))
            if (view is Control control)
                sidePanelsHost.Children.Add(control);
    }

    // H6 to H11, H17 to H23: pages, their panels, the page switcher, and the TID all depend on each
    // other (the TID and the control-pages region both key off the active page), so they are discovered
    // and rendered together.
    private async Task RefreshPagesPanelsAndTidAsync(IModuleHost host, RuntimeStatus snapshot)
    {
        var newDiagnostics = new List<string>();

        var pages = new Dictionary<string, (string FeatureId, string DisplayName)>(StringComparer.Ordinal);
        foreach (var f in snapshot.Features.Where(f => f.PointId == CoreUiPoints.Page)
                     .OrderBy(f => f.FeatureId, StringComparer.Ordinal))
        {
            (string PageId, string DisplayName) data;
            try { data = await host.InvokeAsync<IUiPage, (string, string)>(f.FeatureId, p => (p.PageId, p.DisplayName)); }
            catch { continue; }
            if (pages.ContainsKey(data.PageId))
                newDiagnostics.Add($"{f.FeatureId}: a page with ID '{data.PageId}' is already registered; rejected.");
            else
                pages[data.PageId] = (f.FeatureId, data.DisplayName);
        }

        var panels = new List<(string FeatureId, string PageId, int Order)>();
        foreach (var f in snapshot.Features.Where(f => f.PointId == CoreUiPoints.PagePanel)
                     .OrderBy(f => f.FeatureId, StringComparer.Ordinal))
        {
            (string PageId, int Order) data;
            try { data = await host.InvokeAsync<IUiPanel, (string, int)>(f.FeatureId, p => (p.PageId, p.Order)); }
            catch { continue; }
            if (!pages.ContainsKey(data.PageId))
                newDiagnostics.Add($"{f.FeatureId}: targets the unregistered page '{data.PageId}'; rejected.");
            else
                panels.Add((f.FeatureId, data.PageId, data.Order));
        }

        var tidPages = new Dictionary<string, (string FeatureId, IReadOnlyList<TidButton> Buttons)>(StringComparer.Ordinal);
        foreach (var f in snapshot.Features.Where(f => f.PointId == CoreUiPoints.TidPage)
                     .OrderBy(f => f.FeatureId, StringComparer.Ordinal))
        {
            (string PageId, IReadOnlyList<TidButton> Buttons) data;
            try
            {
                data = await host.InvokeAsync<ITidPage, (string, IReadOnlyList<TidButton>)>(
                    f.FeatureId, p => (p.PageId, p.Buttons));
            }
            catch { continue; }
            if (tidPages.ContainsKey(data.PageId))
                newDiagnostics.Add($"{f.FeatureId}: a TID page for '{data.PageId}' is already registered; rejected.");
            else
                tidPages[data.PageId] = (f.FeatureId, data.Buttons);
        }

        diagnostics.Clear();
        diagnostics.AddRange(newDiagnostics);

        if (activePageId is null || !pages.ContainsKey(activePageId))
        {
            var next = pages.Keys.OrderBy(k => k, StringComparer.Ordinal).FirstOrDefault();
            if (next != activePageId)
            {
                activePageId = next;
                host.SetActiveShortcutScope(activePageId);
            }
        }

        RenderPageSwitcher(host, pages);
        await RenderControlPagesContentAsync(host, panels);
        RenderTidButtons(host, tidPages);
    }

    private void RenderPageSwitcher(IModuleHost host, Dictionary<string, (string FeatureId, string DisplayName)> pages)
    {
        if (pageSwitcherHost is null)
            return;
        pageSwitcherHost.Children.Clear();
        foreach (var entry in pages.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            var pageId = entry.Key;
            var button = new Button { Content = entry.Value.DisplayName };
            button.Click += (_, _) =>
            {
                if (activePageId == pageId)
                    return;
                activePageId = pageId;
                host.SetActiveShortcutScope(activePageId);
            };
            pageSwitcherHost.Children.Add(button);
        }
    }

    private async Task RenderControlPagesContentAsync(
        IModuleHost host, List<(string FeatureId, string PageId, int Order)> panels)
    {
        if (controlPagesHost is null)
            return;
        // Releasing a panel's view means clearing it out of the intermediate stack that holds it, not
        // just detaching that stack from controlPagesHost: a control's Parent is whatever directly
        // contains it (the stack), so replacing controlPagesHost.Content alone leaves the stack's own
        // Children (and each view's Parent) unchanged.
        if (controlPagesHost.Content is StackPanel previous)
            previous.Children.Clear();
        if (activePageId is null)
        {
            controlPagesHost.Content = Placeholder(NoControlPage);
            return;
        }
        var relevant = panels.Where(p => p.PageId == activePageId)
            .OrderBy(p => p.Order).ThenBy(p => p.FeatureId, StringComparer.Ordinal).ToList();
        if (relevant.Count == 0)
        {
            controlPagesHost.Content = null;
            return;
        }
        var stack = new StackPanel { Spacing = 8 };
        foreach (var panel in relevant)
        {
            try
            {
                var view = await host.InvokeAsync<IUiPanel, object>(panel.FeatureId, p => p.CreateView());
                if (view is Control control)
                    stack.Children.Add(control);
            }
            catch { /* unavailable mid-fetch; next tick retries */ }
        }
        controlPagesHost.Content = stack;
    }

    private void RenderTidButtons(
        IModuleHost host, Dictionary<string, (string FeatureId, IReadOnlyList<TidButton> Buttons)> tidPages)
    {
        if (tidButtonsHost is null)
            return;
        tidButtonsHost.Children.Clear();
        if (activePageId is null || !tidPages.TryGetValue(activePageId, out var tidPage) || tidPage.Buttons.Count == 0)
        {
            tidButtonsHost.Children.Add(Placeholder(NoTidButtons));
            return;
        }
        var featureId = tidPage.FeatureId;
        foreach (var button in tidPage.Buttons)
        {
            var buttonId = button.Id;
            var canActivate = button.IsEnabled;
            var control = new Button { Content = button.Label, IsEnabled = button.IsEnabled };
            control.Click += (_, _) =>
            {
                if (!canActivate)
                    return;
                _ = host.InvokeAsync<ITidPage, bool>(featureId, p => { p.Activate(buttonId); return true; });
            };
            tidButtonsHost.Children.Add(control);
        }
    }

    // H16: a curated subset of Avalonia's Key enum, mapped to the framework-independent UiKey Core
    // itself (IModuleHost) works with.
    private static UiKey? MapKey(Key key) => key switch
    {
        Key.A => UiKey.A, Key.B => UiKey.B, Key.C => UiKey.C, Key.D => UiKey.D, Key.E => UiKey.E,
        Key.F => UiKey.F, Key.G => UiKey.G, Key.H => UiKey.H, Key.I => UiKey.I, Key.J => UiKey.J,
        Key.K => UiKey.K, Key.L => UiKey.L, Key.M => UiKey.M, Key.N => UiKey.N, Key.O => UiKey.O,
        Key.P => UiKey.P, Key.Q => UiKey.Q, Key.R => UiKey.R, Key.S => UiKey.S, Key.T => UiKey.T,
        Key.U => UiKey.U, Key.V => UiKey.V, Key.W => UiKey.W, Key.X => UiKey.X, Key.Y => UiKey.Y,
        Key.Z => UiKey.Z,
        Key.D0 => UiKey.D0, Key.D1 => UiKey.D1, Key.D2 => UiKey.D2, Key.D3 => UiKey.D3, Key.D4 => UiKey.D4,
        Key.D5 => UiKey.D5, Key.D6 => UiKey.D6, Key.D7 => UiKey.D7, Key.D8 => UiKey.D8, Key.D9 => UiKey.D9,
        Key.F1 => UiKey.F1, Key.F2 => UiKey.F2, Key.F3 => UiKey.F3, Key.F4 => UiKey.F4,
        Key.F5 => UiKey.F5, Key.F6 => UiKey.F6, Key.F7 => UiKey.F7, Key.F8 => UiKey.F8,
        Key.F9 => UiKey.F9, Key.F10 => UiKey.F10, Key.F11 => UiKey.F11, Key.F12 => UiKey.F12,
        Key.Up => UiKey.Up, Key.Down => UiKey.Down, Key.Left => UiKey.Left, Key.Right => UiKey.Right,
        Key.Enter => UiKey.Enter, Key.Escape => UiKey.Escape, Key.Tab => UiKey.Tab,
        Key.Space => UiKey.Space, Key.Delete => UiKey.Delete, Key.Back => UiKey.Backspace,
        _ => null
    };

    private static UiKeyModifiers MapModifiers(KeyModifiers modifiers)
    {
        var result = UiKeyModifiers.None;
        if (modifiers.HasFlag(KeyModifiers.Shift)) result |= UiKeyModifiers.Shift;
        if (modifiers.HasFlag(KeyModifiers.Control)) result |= UiKeyModifiers.Control;
        if (modifiers.HasFlag(KeyModifiers.Alt)) result |= UiKeyModifiers.Alt;
        return result;
    }

    private static TextBlock Placeholder(string text) => new()
    {
        Text = text,
        TextWrapping = Avalonia.Media.TextWrapping.Wrap,
        HorizontalAlignment = HorizontalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Center
    };

    public async ValueTask StopAsync(CancellationToken cancellationToken)
    {
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            stopping = true;
            timer?.Stop();
            timer = null;
            // Release the hosted views so no window keeps a retired provider's load context alive.
            if (viewportHost is not null)
                viewportHost.Content = null;
            viewportHost = null;
            viewportSource = null;
            if (controlPagesHost is not null)
                controlPagesHost.Content = null;
            controlPagesHost = null;
            sidePanelsHost?.Children.Clear();
            sidePanelsHost = null;
            tidButtonsHost?.Children.Clear();
            tidButtonsHost = null;
            pageSwitcherHost = null;
            activePageId = null;
            chartWindow?.Close();
            chartWindow = null;
            controlPagesWindow?.Close();
            controlPagesWindow = null;
            tidWindow?.Close();
            tidWindow = null;
        });
    }
}
