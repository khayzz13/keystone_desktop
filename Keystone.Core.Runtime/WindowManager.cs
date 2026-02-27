using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Text.Json;
#if MACOS
using AppKit;
#endif
using Keystone.Core;
using Keystone.Core.Management;
using Keystone.Core.Platform;
using Keystone.Core.Plugins;
using Keystone.Core.Rendering;
using Keystone.Core.UI;

namespace Keystone.Core.Runtime;

/// <summary>
/// Window registry, event processing, and rendering coordinator
/// Owns both event loop and frame loop per window
/// </summary>
public class WindowManager : IDisposable
{
    // Window registries
    private readonly Dictionary<IntPtr, ManagedWindow> _windows = new();
    private readonly Dictionary<string, ManagedWindow> _windowsById = new();
    private readonly Dictionary<string, BindContainer> _bindContainers = new();
    private readonly Dictionary<string, TabGroup> _tabGroups = new();

    // Dependencies
    private readonly ActionRouter _actionRouter;
    private readonly Stopwatch _stopwatch = new();
    private readonly IPlatform _platform;

    // Bind mode state
    private bool _bindModeActive;
    private readonly List<string> _bindSelectedWindowIds = new();
    private int _windowCounter;

    // Tab merge tracking - window being dragged for potential merge
    private string? _draggingWindowId;

    // Spawn callback for ApplicationRuntime to hook into
    public Action<string>? OnSpawnWindow;
    // Spawn at position callback
    public Func<string, (INativeWindow nativeWindow, ManagedWindow managed)?>? OnSpawnWindowAt;
    public bool BindModeActive => _bindModeActive;

    public string? ActiveWorkspaceId => KeystoneDb.GetActiveWorkspaceId();

    // Overlay window (floating dropdown/panel)
    private string? _activeOverlayId;
    // Tab drag preview overlay (browser-style)
    private string? _activeTabDragOverlayId;
    private int _tabDragOverlayCounter;
    private TabDragSession? _activeTabDrag;

    // Plugin registry reference for hot-reload
    private PluginRegistry? _registry;

    private sealed class TabDragSession
    {
        public string TabId { get; }
        public ManagedWindow TabWindow { get; }
        public string? OriginalGroupId { get; }
        public bool SingleTabSource { get; }
        public double OffsetX { get; }
        public double PreviewOffsetX { get; }
        public double WindowWidth { get; }
        public double WindowHeight { get; }

        public TabDragSession(
            string tabId,
            ManagedWindow tabWindow,
            string? originalGroupId,
            bool singleTabSource,
            double offsetX,
            double previewOffsetX,
            double windowWidth,
            double windowHeight)
        {
            TabId = tabId;
            TabWindow = tabWindow;
            OriginalGroupId = originalGroupId;
            SingleTabSource = singleTabSource;
            OffsetX = offsetX;
            PreviewOffsetX = previewOffsetX;
            WindowWidth = windowWidth;
            WindowHeight = windowHeight;
        }
    }

    public WindowManager(IPlatform platform)
    {
        _actionRouter = new ActionRouter(this);
        _platform = platform;
        _stopwatch.Start();
    }

    /// <summary>
    /// Subscribe to plugin registry for hot-reload coordination.
    /// </summary>
    public void SubscribeToRegistry(PluginRegistry registry)
    {
        _registry = registry;
        registry.WindowUnloading += OnWindowPluginUnloading;
        registry.WindowReloaded += OnWindowPluginReloaded;
    }

    public void Dispose()
    {
        if (_registry != null)
        {
            _registry.WindowUnloading -= OnWindowPluginUnloading;
            _registry.WindowReloaded -= OnWindowPluginReloaded;
        }
    }

    private void OnWindowPluginUnloading(string windowType)
    {
        foreach (var window in _windowsById.Values)
        {
            if (window.WindowType == windowType)
                window.SetPendingReload(true);
        }
    }

    [UnconditionalSuppressMessage("Trimming", "IL2072", Justification = "Hot-reload requires dynamic type instantiation")]
    private void OnWindowPluginReloaded(string windowType, IWindowPlugin newPlugin)
    {
        // Standalone windows
        foreach (var window in _windowsById.Values.ToList())
        {
            if (window.WindowType == windowType)
            {
                var newInstance = Activator.CreateInstance(newPlugin.GetType()) as IWindowPlugin;
                if (newInstance != null)
                    window.SwapPlugin(newInstance);
            }
        }

        // Bind container slots — tiled windows also need hot-reload
        foreach (var container in _bindContainers.Values)
        {
            foreach (var slotIndex in container.GetSlotIndices(windowType).ToList())
            {
                var newInstance = Activator.CreateInstance(newPlugin.GetType()) as IWindowPlugin;
                if (newInstance != null)
                    container.SwapSlotPlugin(slotIndex, newInstance);
            }
        }

        Console.WriteLine($"[WindowManager] Hot-reloaded {windowType} across all windows and bind slots");
    }

    // === Window Registration ===

    public ManagedWindow CreateWindow(string id, IWindowPlugin plugin)
    {
        var window = new ManagedWindow(id, plugin, _actionRouter, _platform)
        {
            GetBindModeActive = () => _bindModeActive,
            GetIsSelectedForBind = wid => _bindSelectedWindowIds.Contains(wid),
            GetTabGroupInfo = groupId => groupId != null && _tabGroups.TryGetValue(groupId, out var g) ? g.GetTabInfo() : null,
            OnTabDraggedOut = (sourceWindowId, tabId, dragOffsetX) => PopoutTabAtMouse(sourceWindowId, tabId, dragOffsetX),
            OnShowOverlay = ShowOverlay,
            OnCloseOverlay = CloseOverlay
        };
        return window;
    }

    public void RegisterWindow(ManagedWindow window)
    {
        if (window.Handle != IntPtr.Zero)
            _windows[window.Handle] = window;
        _windowsById[window.Id] = window;
    }

    public void UpdateWindowHandle(ManagedWindow window, INativeWindow nativeWindow)
    {
        var oldHandle = window.Handle;
        if (oldHandle != IntPtr.Zero && _windows.ContainsKey(oldHandle))
            _windows.Remove(oldHandle);

        if (nativeWindow.Handle != IntPtr.Zero)
            _windows[nativeWindow.Handle] = window;
    }

    public void UnregisterWindow(string windowId)
    {
        if (!_windowsById.TryGetValue(windowId, out var window))
            return;

        if (window.ContainerId != null && _bindContainers.TryGetValue(window.ContainerId, out var container))
        {
            if (container.WindowCount == 0)
                _bindContainers.Remove(window.ContainerId);
        }

        if (window.GroupId != null && _tabGroups.TryGetValue(window.GroupId, out var group))
        {
            group.RemoveWindow(windowId);
            if (group.WindowCount == 0)
                _tabGroups.Remove(window.GroupId);
        }

        StateManager.Instance.RemoveWindowState(windowId);
        _windows.Remove(window.Handle);
        _windowsById.Remove(windowId);
        window.Dispose();
    }

    public ManagedWindow? GetWindow(IntPtr nsWindow) =>
        _windows.TryGetValue(nsWindow, out var w) ? w : null;

    public ManagedWindow? GetWindow(string windowId) =>
        _windowsById.TryGetValue(windowId, out var w) ? w : null;

    public IEnumerable<ManagedWindow> GetAllWindows() => _windowsById.Values;

    // === Event Processing ===

#if MACOS
    public void ProcessEvents()
    {
        var nsApp = NSApplication.SharedApplication;
        while (true)
        {
            var evt = nsApp.NextEvent(NSEventMask.AnyEvent, NSDate.DistantPast, NSRunLoopMode.Default, true);
            if (evt == null) break;

            nsApp.SendEvent(evt);
            RouteInputEvent(evt);
        }
    }

    private void RouteInputEvent(NSEvent evt)
    {
        var type = evt.Type;

        // Global TextEntry handling — before window lookup since it's app-wide
        if (type == NSEventType.KeyDown && TextEntry.Active != null)
        {
            var keyCode = evt.KeyCode;
            if (!TextEntry.HandleKeyCode(keyCode))
            {
                var chars = evt.Characters;
                if (chars != null)
                    TextEntry.HandleCharacters(chars);
            }
            foreach (var w in _windowsById.Values)
                w.RequestRedraw();
            return;
        }

        var evtWindow = evt.Window;
        if (evtWindow == null) return;

        // Route to managed window via handle lookup
        if (!_windows.TryGetValue(evtWindow.Handle, out var window))
            return;

        var eventType = type switch
        {
            NSEventType.LeftMouseDown => InputEventType.MouseDown,
            NSEventType.LeftMouseUp => InputEventType.MouseUp,
            NSEventType.RightMouseDown => InputEventType.RightMouseDown,
            NSEventType.RightMouseUp => InputEventType.RightMouseUp,
            NSEventType.MouseMoved or NSEventType.LeftMouseDragged => InputEventType.MouseMove,
            NSEventType.KeyDown => InputEventType.KeyDown,
            NSEventType.KeyUp => InputEventType.KeyUp,
            NSEventType.ScrollWheel => InputEventType.ScrollWheel,
            _ => InputEventType.Unknown
        };

        switch (eventType)
        {
            case InputEventType.MouseDown:
                var downLoc = evt.LocationInWindow;
                window.OnMouseDown(downLoc.X, downLoc.Y);
                ForwardToBindContainer(window.Id, c => c.HandleMouseDown((float)(downLoc.X * window.ScaleFactor), (float)((window.Height / window.ScaleFactor - downLoc.Y) * window.ScaleFactor)));
                break;

            case InputEventType.MouseUp:
                var upLoc = evt.LocationInWindow;
                window.OnMouseUp(upLoc.X, upLoc.Y);
                ForwardToBindContainer(window.Id, c => c.HandleMouseUp());
                break;

            case InputEventType.RightMouseDown:
                var rightDownLoc = evt.LocationInWindow;
                window.OnRightClick(rightDownLoc.X, rightDownLoc.Y);
                break;

            case InputEventType.MouseMove:
                var moveLoc = evt.LocationInWindow;
                window.OnMouseMove(moveLoc.X, moveLoc.Y);
                ForwardToBindContainer(window.Id, c => c.HandleMouseMove((float)(moveLoc.X * window.ScaleFactor), (float)((window.Height / window.ScaleFactor - moveLoc.Y) * window.ScaleFactor)));
                break;

            case InputEventType.ScrollWheel:
                window.OnScroll(evt.ScrollingDeltaX, evt.ScrollingDeltaY);
                break;

            case InputEventType.KeyDown:
                var modifiers = MapModifiers(evt.ModifierFlags);
                window.OnKeyDown(evt.KeyCode, modifiers);
                break;

            case InputEventType.KeyUp:
                var keyUpMods = MapModifiers(evt.ModifierFlags);
                window.OnKeyUp(evt.KeyCode, keyUpMods);
                break;
        }
    }

    private static KeyModifiers MapModifiers(NSEventModifierMask nsModifiers)
    {
        var mods = KeyModifiers.None;
        if (nsModifiers.HasFlag(NSEventModifierMask.ShiftKeyMask)) mods |= KeyModifiers.Shift;
        if (nsModifiers.HasFlag(NSEventModifierMask.ControlKeyMask)) mods |= KeyModifiers.Control;
        if (nsModifiers.HasFlag(NSEventModifierMask.AlternateKeyMask)) mods |= KeyModifiers.Alt;
        if (nsModifiers.HasFlag(NSEventModifierMask.CommandKeyMask)) mods |= KeyModifiers.Command;
        return mods;
    }
#else
    public void ProcessEvents()
    {
        // Linux/Windows: platform-specific event loop goes here.
        // GTK4: while (Gtk.Application.EventsPending()) Gtk.Application.MainIteration();
        // Input events should be routed via GTK signal handlers connected in LinuxNativeWindow.
    }
#endif

    private void ForwardToBindContainer(string windowId, Action<BindContainer> action)
    {
        if (_bindContainers.TryGetValue(windowId, out var container))
            action(container);
    }

    // === Frame Rendering (per-window threads — see WindowRenderThread) ===

    public void CheckTabDragState()
    {
        if (_activeTabDrag != null)
        {
            UpdateTabDragOverlayPosition();
            if (!_platform.IsMouseButtonDown())
                CompleteTabDrag();
            return;
        }

        if (_draggingWindowId != null && !_platform.IsMouseButtonDown())
            CheckTabMergeOnDragEnd();
    }

    // === Window Spawning (via ActionRouter) ===

    public void SpawnWindow(string windowType)
    {
        Console.WriteLine($"[WindowManager] SpawnWindow: {windowType}");
        OnSpawnWindow?.Invoke(windowType);
    }

    public void CloseWindow(string windowId)
    {
        Console.WriteLine($"[WindowManager] CloseWindow: {windowId}");

        if (_windowsById.TryGetValue(windowId, out var window) && window.NativeWindow != null)
            window.NativeWindow.Close();
        UnregisterWindow(windowId);
    }

    public void StartDrag(string windowId)
    {
        if (_windowsById.TryGetValue(windowId, out var window) && window.NativeWindow != null)
            window.NativeWindow.StartDrag();
    }

    public void ToggleMenu(string windowId, string menuName)
    {
        Console.WriteLine($"[WindowManager] ToggleMenu: {menuName} on {windowId}");
    }

    public void SetTool(string windowId, string toolName)
    {
        Console.WriteLine($"[WindowManager] SetTool: {toolName} on {windowId}");
    }

    public void EnterBindMode()
    {
        _bindModeActive = !_bindModeActive;
        if (!_bindModeActive)
            _bindSelectedWindowIds.Clear();
        Console.WriteLine($"[WindowManager] BindMode: {_bindModeActive}");
    }

    public void ToggleBindSelection(string windowId)
    {
        if (!_bindModeActive) return;

        if (_bindSelectedWindowIds.Contains(windowId))
            _bindSelectedWindowIds.Remove(windowId);
        else
            _bindSelectedWindowIds.Add(windowId);

        Console.WriteLine($"[WindowManager] BindSelection: {string.Join(", ", _bindSelectedWindowIds)}");
    }

    public bool IsWindowSelectedForBind(string windowId) => _bindSelectedWindowIds.Contains(windowId);

    public void ExecuteBind()
    {
        if (_bindSelectedWindowIds.Count < 2) return;

        var windows = _bindSelectedWindowIds
            .Select(id => _windowsById.TryGetValue(id, out var w) ? w : null)
            .Where(w => w?.NativeWindow != null)
            .ToList();

        if (windows.Count < 2) return;

        double minX = double.MaxValue, minY = double.MaxValue;
        double maxX = double.MinValue, maxY = double.MinValue;

        foreach (var w in windows)
        {
            var (fx, fy, fw, fh) = w!.NativeWindow!.Frame;
            minX = Math.Min(minX, fx);
            minY = Math.Min(minY, fy);
            maxX = Math.Max(maxX, fx + fw);
            maxY = Math.Max(maxY, fy + fh);
        }

        var (f1x, f1y, _, _) = windows[0]!.NativeWindow!.Frame;
        var (f2x, f2y, _, _) = windows[1]!.NativeWindow!.Frame;
        var isVertical = Math.Abs(f1y - f2y) > Math.Abs(f1x - f2x);
        var orientation = isVertical ? BindOrientation.Vertical : BindOrientation.Horizontal;

        var plugins = windows.Select(w => w!.GetPlugin()).Where(p => p != null).ToArray();

        if (plugins.Length < 2) return;

        foreach (var w in windows)
            w!.NativeWindow!.Hide();

        var layout = new BindLayout(BindLayoutType.Split, orientation);
        CreateBindContainer(plugins!, layout, minX, minY, maxX - minX, maxY - minY);

        _bindSelectedWindowIds.Clear();
        _bindModeActive = false;

        Console.WriteLine($"[WindowManager] Created bind with {plugins.Length} windows");
    }

    // === Overlay System ===

    private int _overlayCounter;

    public void ShowOverlay(IOverlayContent content, double screenX, double screenY, double w, double h)
    {
        CloseOverlay();
        var wndConfig = new Platform.WindowConfig(screenX, screenY - h, w, h);
        var nativeWindow = _platform.CreateOverlayWindow(wndConfig);

        var overlayId = $"overlay_{++_overlayCounter}";
        var adapter = new OverlayAdapter(content);
        var managed = CreateWindow(overlayId, adapter);
        managed.OnCreated(nativeWindow);
        RegisterWindow(managed);
        nativeWindow.Show();
        _activeOverlayId = overlayId;
    }

    public void CloseOverlay()
    {
        if (_activeOverlayId == null) return;
        if (_windowsById.TryGetValue(_activeOverlayId, out var w) && w.NativeWindow != null)
            w.NativeWindow.Close();
        UnregisterWindow(_activeOverlayId);
        _activeOverlayId = null;
    }

    public bool HasActiveOverlay => _activeOverlayId != null;

    private ManagedWindow? ShowTabDragOverlay(string title, double x, double y, double w, double h)
    {
        CloseTabDragOverlay();

        var wndConfig = new Platform.WindowConfig(x, y, w, h);
        var nativeWindow = _platform.CreateOverlayWindow(wndConfig);

        var overlayId = $"tab_drag_overlay_{++_tabDragOverlayCounter}";
        var adapter = new OverlayAdapter(new TabDragOverlayContent(title));
        var managed = CreateWindow(overlayId, adapter);
        managed.OnCreated(nativeWindow);
        RegisterWindow(managed);
        nativeWindow.Show();
        _activeTabDragOverlayId = overlayId;
        return managed;
    }

    private void CloseTabDragOverlay()
    {
        if (_activeTabDragOverlayId == null) return;
        if (_windowsById.TryGetValue(_activeTabDragOverlayId, out var w) && w.NativeWindow != null)
            w.NativeWindow.Close();
        UnregisterWindow(_activeTabDragOverlayId);
        _activeTabDragOverlayId = null;
    }

    // === Workspaces ===

    private static readonly JsonSerializerOptions _jsonOpts = new() { WriteIndented = false };

    public List<(string Id, string Name, bool IsActive)> GetWorkspaces() => KeystoneDb.GetWorkspaces();

    public void SaveWorkspace(string name)
    {
        var snapshots = new List<WindowSnapshotDto>();
        foreach (var window in _windowsById.Values)
        {
            if (window.NativeWindow == null) continue;
            if (window.ContainerId != null) continue; // bind container slots saved separately
            var plugin = window.GetPlugin();
            if (plugin.ExcludeFromWorkspace) continue;

            var (fx, fy, fw, fh) = window.NativeWindow.Frame;
            snapshots.Add(new WindowSnapshotDto
            {
                WindowType = window.WindowType,
                X = fx, Y = fy,
                Width = fw, Height = fh,
                ConfigJson = plugin.SerializeConfig()
            });
        }
        var workspaceId = KeystoneDb.SaveWorkspace(name, snapshots);

        // Save tab groups
        var tabGroupSnapshots = new List<TabGroupSnapshotDto>();
        foreach (var (groupId, group) in _tabGroups)
        {
            var windows = group.GetWindows().ToList();
            if (windows.Count < 2) continue;
            var activeId = group.ActiveWindowId;
            var activeIndex = 0;
            var types = new List<string>();
            for (int i = 0; i < windows.Count; i++)
            {
                types.Add(windows[i].WindowType);
                if (windows[i].Id == activeId) activeIndex = i;
            }
            tabGroupSnapshots.Add(new TabGroupSnapshotDto
            {
                GroupId = groupId,
                WindowTypes = types,
                ActiveIndex = activeIndex
            });
        }
        KeystoneDb.SaveWorkspaceTabGroups(workspaceId, tabGroupSnapshots);

        // Save bind containers
        var bindSnapshots = new List<BindContainerSnapshotDto>();
        foreach (var (containerId, container) in _bindContainers)
        {
            var types = container.GetPlugins().Select(p => p.WindowType).ToList();
            if (types.Count == 0) continue;
            bindSnapshots.Add(new BindContainerSnapshotDto
            {
                ContainerId = containerId,
                LayoutType = container.Layout.Type.ToString(),
                Orientation = container.Layout.Orientation.ToString(),
                Ratios = container.Layout.Ratios.ToList(),
                WindowTypes = types
            });
        }
        KeystoneDb.SaveWorkspaceBindContainers(workspaceId, bindSnapshots);
    }

    public void LoadWorkspace(string id)
    {
        var data = KeystoneDb.LoadWorkspace(id);
        if (data == null) return;

        var toClose = _windowsById.Keys.ToList();
        foreach (var wid in toClose) CloseWindow(wid);

        // Spawn standalone windows
        var spawnedByType = new Dictionary<string, List<string>>(); // windowType -> [windowId]
        foreach (var snap in data.Value.Windows)
        {
            var result = OnSpawnWindowAt?.Invoke(snap.WindowType);
            if (result == null) continue;
            result.Value.nativeWindow.SetFrame(snap.X, snap.Y, snap.Width, snap.Height);

            if (snap.ConfigJson != null)
                result.Value.managed.GetPlugin().RestoreConfig(snap.ConfigJson);

            if (!spawnedByType.TryGetValue(snap.WindowType, out var ids))
            {
                ids = new List<string>();
                spawnedByType[snap.WindowType] = ids;
            }
            ids.Add(result.Value.managed.Id);
        }

        // Restore tab groups
        var tabGroups = KeystoneDb.LoadWorkspaceTabGroups(id);
        foreach (var tg in tabGroups)
        {
            var windowIds = new List<string>();
            foreach (var wtype in tg.WindowTypes)
            {
                if (spawnedByType.TryGetValue(wtype, out var ids) && ids.Count > 0)
                {
                    windowIds.Add(ids[0]);
                    ids.RemoveAt(0);
                }
            }
            if (windowIds.Count >= 2)
            {
                var groupId = CreateTabGroup(windowIds.ToArray());
                if (tg.ActiveIndex >= 0 && tg.ActiveIndex < windowIds.Count)
                    SetActiveTab(groupId, windowIds[tg.ActiveIndex]);
            }
        }

        // Restore bind containers
        var bindContainers = KeystoneDb.LoadWorkspaceBindContainers(id);
        foreach (var bc in bindContainers)
        {
            var plugins = new List<IWindowPlugin>();
            foreach (var wtype in bc.WindowTypes)
            {
                var plugin = _registry?.GetWindow(wtype);
                if (plugin != null)
                {
                    var instance = Activator.CreateInstance(plugin.GetType()) as IWindowPlugin;
                    if (instance != null) plugins.Add(instance);
                }
            }
            if (plugins.Count < 2) continue;

            var layoutType = Enum.TryParse<BindLayoutType>(bc.LayoutType, out var lt) ? lt : BindLayoutType.Split;
            var orientation = Enum.TryParse<BindOrientation>(bc.Orientation, out var or) ? or : BindOrientation.Horizontal;
            var layout = new BindLayout(layoutType, orientation);
            if (bc.Ratios.Count == plugins.Count)
                layout.Ratios = bc.Ratios.ToArray();

            // Use reasonable default position/size
            CreateBindContainer(plugins.ToArray(), layout, 100, 100, 1200, 800);
        }

        Console.WriteLine($"[WindowManager] Loaded workspace '{data.Value.Name}' with {data.Value.Windows.Count} windows, {tabGroups.Count} tab groups, {bindContainers.Count} bind containers");
    }

    public void DeleteWorkspace(string id) => KeystoneDb.DeleteWorkspace(id);

    // === Layouts (per-window) ===

    public List<(string Id, string Name)> GetLayouts(string windowType) => KeystoneDb.GetLayouts(windowType);

    public void SaveLayout(string windowId, string name)
    {
        if (!_windowsById.TryGetValue(windowId, out var window)) return;
        var plugin = window.GetPlugin();
        var configJson = plugin.SerializeConfig() ?? "{}";
        KeystoneDb.SaveLayout(name, window.WindowType, configJson);
    }

    public void LoadLayout(string windowId, string layoutId)
    {
        if (!_windowsById.TryGetValue(windowId, out var window)) return;
        var data = KeystoneDb.LoadLayout(layoutId);
        if (data == null) return;
        window.GetPlugin().RestoreConfig(data.Value.ConfigJson);
    }

    public void DeleteLayout(string id) => KeystoneDb.DeleteLayout(id);

    public void MinimizeWindow(string windowId)
    {
        if (_windowsById.TryGetValue(windowId, out var window) && window.NativeWindow != null)
            window.NativeWindow.Minimize();
    }

    public void MaximizeWindow(string windowId)
    {
        if (_windowsById.TryGetValue(windowId, out var window) && window.NativeWindow != null)
            window.NativeWindow.Zoom();
    }

    public void ToggleAlwaysOnTop(string windowId)
    {
        if (_windowsById.TryGetValue(windowId, out var window) && window.NativeWindow != null)
        {
            window.AlwaysOnTop = !window.AlwaysOnTop;
            window.NativeWindow.SetFloating(window.AlwaysOnTop);
        }
    }

    public bool IsWindowAlwaysOnTop(string windowId) =>
        _windowsById.TryGetValue(windowId, out var w) && w.AlwaysOnTop;

    public void BringAllWindowsToFront() => _platform.BringAllWindowsToFront();

    public Action? OnQuitApp { get; set; }
    public void QuitApp() => OnQuitApp?.Invoke(); // wired by ApplicationRuntime

    public IEnumerable<(string id, string title)> GetWindowsForDockMenu()
    {
        foreach (var w in _windowsById.Values)
            if (w.NativeWindow != null)
                yield return (w.Id, w.GetPlugin().WindowTitle);
    }

    // === Bind Containers ===

    private int _bindCounter;

    [UnconditionalSuppressMessage("Trimming", "IL2072", Justification = "BindWindow plugin loaded dynamically")]
    public string CreateBindContainer(IWindowPlugin[] plugins, BindLayout layout, double x, double y, double width, double height)
    {
        var containerId = $"bind_{++_bindCounter}";
        var name = $"Bind {_bindCounter}";

        var container = new BindContainer(containerId, name, plugins, layout);
        container.OnPopout = (cid, slotIndex) => PopoutFromBind(cid, slotIndex);
        container.OnAction = action => _actionRouter.Execute(action, containerId);

        var bindWindowPlugin = _registry?.GetWindow("bind");
        if (bindWindowPlugin == null)
        {
            Console.WriteLine("[WindowManager] ERROR: bind window plugin not registered");
            return containerId;
        }

        var bindWindow = Activator.CreateInstance(bindWindowPlugin.GetType()) as IWindowPlugin;
        if (bindWindow == null)
        {
            Console.WriteLine("[WindowManager] ERROR: failed to create BindWindow instance");
            return containerId;
        }

        var bindType = bindWindow.GetType();
        bindType.GetProperty("Title")?.SetValue(bindWindow, name);
        bindType.GetField("RenderSlots")?.SetValue(bindWindow, (Action<RenderContext, ButtonRegistry, float>)container.RenderSlots);
        bindType.GetField("HitTestSlots")?.SetValue(bindWindow, (Func<float, float, float, float, HitTestResult?>)container.HitTestSlots);

        var managedWindow = CreateWindow(containerId, bindWindow);

        var wndConfig = new Platform.WindowConfig(x, y, width, height);
        var nativeWindow = _platform.CreateWindow(wndConfig);

        managedWindow.OnCreated(nativeWindow);
        RegisterWindow(managedWindow);

        container.HostWindow = managedWindow;
        _bindContainers[containerId] = container;

        nativeWindow.Show();
        return containerId;
    }

    public BindContainer? GetBindContainer(string containerId) =>
        _bindContainers.TryGetValue(containerId, out var c) ? c : null;

    public void DissolveBindContainer(string containerId)
    {
        if (!_bindContainers.TryGetValue(containerId, out var container))
            return;

        if (container.HostWindow != null)
            UnregisterWindow(containerId);

        container.Dispose();
        _bindContainers.Remove(containerId);
    }

    public void PopoutFromBind(string containerId, int slotIndex)
    {
        if (!_bindContainers.TryGetValue(containerId, out var container))
            return;
        if (container.HostWindow?.NativeWindow == null)
            return;

        var (fx, fy, _, _) = container.HostWindow.NativeWindow.Frame;

        if (container.WindowCount <= 1)
        {
            var lastPlugin = container.RemoveSlot(0);
            if (lastPlugin != null)
            {
                var (lw, lh) = lastPlugin.DefaultSize;
                var lastConfig = new Platform.WindowConfig(fx, fy, lw, lh);
                var lastNativeWindow = _platform.CreateWindow(lastConfig);

                var lastManaged = CreateWindow($"popout_{++_windowCounter}", lastPlugin);
                lastManaged.OnCreated(lastNativeWindow);
                RegisterWindow(lastManaged);
                lastNativeWindow.Show();
            }
            DissolveBindContainer(containerId);
            Console.WriteLine($"[WindowManager] Dissolved {containerId} (last slot popped out)");
            return;
        }

        var plugin = container.RemoveSlot(slotIndex);
        if (plugin == null) return;

        Console.WriteLine($"[WindowManager] Popout slot {slotIndex} from {containerId} ({container.WindowCount} remaining)");

        var (w, h) = plugin.DefaultSize;
        var popoutConfig = new Platform.WindowConfig(fx + 50, fy + 50, w, h);
        var nativeWindow = _platform.CreateWindow(popoutConfig);

        var managedWindow = CreateWindow($"popout_{++_windowCounter}", plugin);
        managedWindow.OnCreated(nativeWindow);
        RegisterWindow(managedWindow);
        nativeWindow.Show();
    }

    // === Tab Groups ===

    public string CreateTabGroup(string[] windowIds)
    {
        var groupId = $"group_{Guid.NewGuid():N}";
        var windows = windowIds.Select(id => _windowsById[id]).ToArray();

        var group = new TabGroup(groupId, windows);
        _tabGroups[groupId] = group;

        for (int i = 0; i < windows.Length; i++)
        {
            var window = windows[i];
            window.LayoutMode = WindowLayoutMode.TabGroup;
            window.GroupId = groupId;
            if (i > 0 && window.NativeWindow != null)
                window.NativeWindow.Hide();
        }

        return groupId;
    }

    public void SetActiveTab(string groupId, string windowId)
    {
        if (!_tabGroups.TryGetValue(groupId, out var group)) return;

        var oldActiveId = group.ActiveWindowId;
        group.SetActiveWindow(windowId);

        if (oldActiveId != null && _windowsById.TryGetValue(oldActiveId, out var oldWindow) && oldWindow.NativeWindow != null)
            oldWindow.NativeWindow.Hide();
        if (_windowsById.TryGetValue(windowId, out var newWindow) && newWindow.NativeWindow != null)
            newWindow.NativeWindow.Show();
    }

    public void DissolveTabGroup(string groupId)
    {
        if (!_tabGroups.TryGetValue(groupId, out var group))
            return;

        foreach (var window in group.GetWindows())
        {
            window.LayoutMode = WindowLayoutMode.Standalone;
            window.GroupId = null;
            window.NativeWindow?.Show();
        }

        _tabGroups.Remove(groupId);
    }

    public void SelectTabInGroup(string sourceWindowId, string tabId)
    {
        if (!_windowsById.TryGetValue(sourceWindowId, out var sourceWindow)) return;
        if (sourceWindow.GroupId == null) return;
        SetActiveTab(sourceWindow.GroupId, tabId);
    }

    public void CloseTabInGroup(string sourceWindowId, string tabId)
    {
        if (!_windowsById.TryGetValue(sourceWindowId, out var sourceWindow)) return;
        if (sourceWindow.GroupId == null) return;
        if (!_tabGroups.TryGetValue(sourceWindow.GroupId, out var group)) return;

        if (group.WindowCount <= 1)
        {
            DissolveTabGroup(sourceWindow.GroupId);
            CloseWindow(tabId);
            return;
        }

        group.RemoveWindow(tabId);
        if (_windowsById.TryGetValue(tabId, out var tabWindow))
        {
            tabWindow.LayoutMode = WindowLayoutMode.Standalone;
            tabWindow.GroupId = null;
        }
        CloseWindow(tabId);
    }

    public void StartTabDrag(string sourceWindowId, string tabId)
    {
        PopoutTabAtMouse(sourceWindowId, tabId);
    }

    public void PopoutTab(string sourceWindowId, string tabId)
    {
        if (!_windowsById.TryGetValue(sourceWindowId, out var sourceWindow)) return;
        if (sourceWindow.GroupId == null) return;
        if (!_tabGroups.TryGetValue(sourceWindow.GroupId, out var group)) return;
        if (!_windowsById.TryGetValue(tabId, out var tabWindow)) return;
        if (group.WindowCount <= 1) return;

        group.RemoveWindow(tabId);
        tabWindow.LayoutMode = WindowLayoutMode.Standalone;
        tabWindow.GroupId = null;

        if (sourceWindow.NativeWindow != null && tabWindow.NativeWindow != null)
        {
            var (fx, fy, fw, fh) = sourceWindow.NativeWindow.Frame;
            tabWindow.NativeWindow.SetFrame(fx + 30, fy - 30, fw, fh);
            tabWindow.NativeWindow.Show();
        }

        if (group.WindowCount == 1)
            DissolveTabGroup(sourceWindow.GroupId);

        Console.WriteLine($"[WindowManager] Popped out tab {tabId} from group");
    }

    public void PopoutTabAtMouse(string sourceWindowId, string tabId, float dragOffsetX = -1)
    {
        if (_activeTabDrag != null)
            CompleteTabDrag();

        if (!_windowsById.TryGetValue(sourceWindowId, out var sourceWindow)) return;
        if (!_windowsById.TryGetValue(tabId, out var tabWindow)) return;
        if (tabWindow.NativeWindow == null) return;
        if (sourceWindowId != tabId && sourceWindow.GroupId != null && sourceWindow.GroupId != tabWindow.GroupId) return;

        _draggingWindowId = null;
        var originalGroupId = tabWindow.GroupId;
        bool singleTabSource = true;

        if (originalGroupId != null && _tabGroups.TryGetValue(originalGroupId, out var group))
        {
            singleTabSource = group.WindowCount <= 1;

            // Multi-tab source: detach immediately so source window updates right away.
            if (!singleTabSource)
            {
                group.RemoveWindow(tabId);
                tabWindow.LayoutMode = WindowLayoutMode.Standalone;
                tabWindow.GroupId = null;

                if (group.WindowCount == 1)
                {
                    DissolveTabGroup(originalGroupId);
                }
                else if (group.ActiveWindowId != null &&
                         _windowsById.TryGetValue(group.ActiveWindowId, out var activeWindow) &&
                         activeWindow.NativeWindow != null)
                {
                    activeWindow.NativeWindow.Show();
                }
            }
        }

        var (mouseX, mouseY) = _platform.GetMouseLocation();
        var (fx, fy, fw, fh) = tabWindow.NativeWindow.Frame;
        double offsetX = dragOffsetX >= 0 ? dragOffsetX : fw / 2;

        // Browser-like preview: drag a small tab overlay, then place full window at release.
        var title = tabWindow.GetPlugin().WindowTitle;
        var previewWidth = Math.Clamp(120 + title.Length * 6.0, 140, 320);
        const double previewHeight = 34;
        var previewOffsetX = Math.Clamp(offsetX, 18, previewWidth - 18);
        var preview = ShowTabDragOverlay(title, mouseX - previewOffsetX, mouseY - 22, previewWidth, previewHeight);

        // Single-tab source keeps its full window in place until release.
        if (!singleTabSource)
            tabWindow.NativeWindow.Hide();

        if (preview?.NativeWindow == null)
        {
            if (singleTabSource && originalGroupId != null && _tabGroups.ContainsKey(originalGroupId))
                DissolveTabGroup(originalGroupId);

            tabWindow.NativeWindow.SetFrame(mouseX - offsetX, mouseY - 22, fw, fh);
            tabWindow.NativeWindow.Show();
            _draggingWindowId = tabId;
            CheckTabMergeOnDragEnd();
            return;
        }

        _activeTabDrag = new TabDragSession(
            tabId,
            tabWindow,
            originalGroupId,
            singleTabSource,
            offsetX,
            previewOffsetX,
            fw,
            fh);
        UpdateTabDragOverlayPosition();
    }

    private void UpdateTabDragOverlayPosition()
    {
        if (_activeTabDrag == null) return;
        if (_activeTabDragOverlayId == null) return;
        if (!_windowsById.TryGetValue(_activeTabDragOverlayId, out var overlay)) return;
        if (overlay.NativeWindow == null) return;

        var (mouseX, mouseY) = _platform.GetMouseLocation();
        var (_, _, ow, oh) = overlay.NativeWindow.Frame;
        overlay.NativeWindow.SetFrame(mouseX - _activeTabDrag.PreviewOffsetX, mouseY - 22, ow, oh);
    }

    private void CompleteTabDrag()
    {
        if (_activeTabDrag == null) return;

        var drag = _activeTabDrag;
        _activeTabDrag = null;
        var (releaseX, releaseY) = _platform.GetMouseLocation();

        CloseTabDragOverlay();

        if (drag.TabWindow.NativeWindow == null)
            return;

        if (drag.SingleTabSource && drag.OriginalGroupId != null && _tabGroups.ContainsKey(drag.OriginalGroupId))
            DissolveTabGroup(drag.OriginalGroupId);

        drag.TabWindow.NativeWindow.SetFrame(
            releaseX - drag.OffsetX,
            releaseY - 22,
            drag.WindowWidth,
            drag.WindowHeight);
        drag.TabWindow.NativeWindow.Show();

        _draggingWindowId = drag.TabId;
        CheckTabMergeOnDragEnd();
    }

    public void CheckTabMergeOnDragEnd()
    {
        if (_draggingWindowId == null) return;
        var draggedId = _draggingWindowId;
        _draggingWindowId = null;

        if (!_windowsById.TryGetValue(draggedId, out var draggedWindow)) return;
        if (draggedWindow.NativeWindow == null) return;

        var (fx, fy, fw, fh) = draggedWindow.NativeWindow.Frame;
        var titleBarY = fy + fh - 22;
        var centerX = fx + fw / 2;

        foreach (var (id, window) in _windowsById)
        {
            if (id == draggedId) continue;
            if (window.WindowType == "ribbon" || window.WindowType == "overlay") continue;
            if (window.GroupId != null && window.GroupId == draggedWindow.GroupId) continue;
            if (window.NativeWindow == null) continue;

            var (tx, ty, tw, th) = window.NativeWindow.Frame;
            var targetTitleTop = ty + th;
            var targetTitleBottom = targetTitleTop - 44;

            if (centerX >= tx && centerX <= tx + tw &&
                titleBarY >= targetTitleBottom && titleBarY <= targetTitleTop)
            {
                MergeIntoTabGroup(id, draggedId);
                return;
            }
        }
    }

    public void MergeIntoTabGroup(string targetWindowId, string windowToMergeId)
    {
        if (!_windowsById.TryGetValue(targetWindowId, out var targetWindow)) return;
        if (!_windowsById.TryGetValue(windowToMergeId, out var windowToMerge)) return;
        if (targetWindowId == windowToMergeId) return;
        if (targetWindow.WindowType == "ribbon" || targetWindow.WindowType == "overlay") return;
        if (windowToMerge.WindowType == "overlay") return;

        if (targetWindow.GroupId != null && _tabGroups.TryGetValue(targetWindow.GroupId, out var existingGroup))
        {
            if (windowToMerge.GroupId != null && _tabGroups.TryGetValue(windowToMerge.GroupId, out var oldGroup))
            {
                oldGroup.RemoveWindow(windowToMergeId);
                if (oldGroup.WindowCount == 0)
                    _tabGroups.Remove(windowToMerge.GroupId);
            }

            existingGroup.AddWindow(windowToMerge);
            windowToMerge.LayoutMode = WindowLayoutMode.TabGroup;
            windowToMerge.GroupId = targetWindow.GroupId;
            windowToMerge.NativeWindow?.Hide();
        }
        else
        {
            CreateTabGroup(new[] { targetWindowId, windowToMergeId });
            windowToMerge.NativeWindow?.Hide();
        }

        Console.WriteLine($"[WindowManager] Merged {windowToMergeId} into {targetWindowId}'s tab group");
    }

    public TabGroup? GetTabGroup(string groupId) =>
        _tabGroups.TryGetValue(groupId, out var g) ? g : null;
}

/// <summary>
/// Slot manager for bound window groups - renders slots into BindWindow plugin
/// No longer owns OS window - uses ManagedWindow with BindWindow plugin
/// </summary>
public class BindContainer : IDisposable
{
    public string ContainerId { get; }
    public string Name { get; }
    private readonly List<BindSlot> _slots = new();
    public BindLayout Layout { get; private set; }
    public int WindowCount => _slots.Count;

    public ManagedWindow? HostWindow { get; set; }

    private float _mouseX, _mouseY;
    private bool _mouseDown;
    private bool _mouseClicked;
    private int _activeSlot = -1;
    private int _draggingDivider = -1;

    private float _width, _height, _scale;

    public Action<string, int>? OnPopout;
    public Action<string>? OnAction;

    private const float DividerHitWidth = 8f;

    public BindContainer(string containerId, string name, IWindowPlugin[] plugins, BindLayout layout)
    {
        ContainerId = containerId;
        Name = name;
        Layout = layout;
        foreach (var plugin in plugins)
            _slots.Add(new BindSlot(plugin));
    }

    public void RenderSlots(RenderContext ctx, ButtonRegistry buttons, float titleBarHeight)
    {
        _width = ctx.State.Width;
        _height = ctx.State.Height;
        _scale = ctx.State.ScaleFactor;

        var rects = CalculateLayout(titleBarHeight);

        for (int i = 0; i < _slots.Count && i < rects.Length; i++)
        {
            var rect = rects[i];
            var slot = _slots[i];

            slot.State.Width = rect.w;
            slot.State.Height = rect.h;
            slot.State.ScaleFactor = _scale;
            slot.State.MouseX = _mouseX - rect.x;
            slot.State.MouseY = _mouseY - rect.y;
            slot.State.MouseDown = _mouseDown && _activeSlot == i;
            slot.State.MouseClicked = _mouseClicked && _activeSlot == i;
            slot.State.IsInBind = true;
            slot.State.BindContainerId = ContainerId;
            slot.State.BindSlotIndex = i;
            slot.State.WindowTitle = slot.Plugin.WindowTitle;

            ctx.PushClip(rect.x, rect.y, rect.w, rect.h);
            ctx.PushTransform(rect.x, rect.y);
            using var slotCtx = new RenderContext(ctx.Canvas, ctx.PaintCache, slot.State);
            slot.Plugin.Render(slotCtx);
            ctx.PopTransform();
            ctx.PopClip();
        }

        RenderDividers(ctx, rects);
        _mouseClicked = false;
    }

    public HitTestResult? HitTestSlots(float x, float y, float width, float height)
    {
        var titleBarHeight = 48f;
        var rects = CalculateLayout(titleBarHeight);

        for (int i = 0; i < _slots.Count && i < rects.Length; i++)
        {
            var rect = rects[i];
            if (x >= rect.x && x < rect.x + rect.w && y >= rect.y && y < rect.y + rect.h)
            {
                var localX = x - rect.x;
                var localY = y - rect.y;
                var result = _slots[i].Plugin.HitTest(localX, localY, rect.w, rect.h);
                if (result?.Action != null)
                    return new HitTestResult($"slot:{_slots[i].SlotId}:{result.Action}", result.Cursor);
                return result;
            }
        }

        var divider = HitTestDivider(x, y, titleBarHeight);
        if (divider >= 0)
            return new HitTestResult("divider_drag", Layout.Orientation == BindOrientation.Vertical ? CursorType.ResizeNS : CursorType.ResizeEW);

        return null;
    }

    public void HandleMouseDown(float x, float y)
    {
        _mouseX = x;
        _mouseY = y;
        _mouseDown = true;
        _mouseClicked = true;

        var titleBarHeight = 48f;
        var rects = CalculateLayout(titleBarHeight);
        _activeSlot = -1;

        for (int i = 0; i < _slots.Count && i < rects.Length; i++)
        {
            var rect = rects[i];
            if (x >= rect.x && x < rect.x + rect.w && y >= rect.y && y < rect.y + rect.h)
            {
                _activeSlot = i;
                break;
            }
        }

        var divider = HitTestDivider(x, y, titleBarHeight);
        if (divider >= 0 && divider < Layout.Ratios.Length)
            _draggingDivider = divider;
    }

    public void HandleMouseUp()
    {
        _mouseDown = false;
        _draggingDivider = -1;
        _activeSlot = -1;
    }

    public void HandleMouseMove(float x, float y)
    {
        _mouseX = x;
        _mouseY = y;

        if (_draggingDivider < 0) return;

        var titleBarHeight = 48f;
        var idx = _draggingDivider;
        if (idx + 1 >= Layout.Ratios.Length) return;

        var availableSize = Layout.Orientation == BindOrientation.Vertical ? _height - titleBarHeight : _width;
        var pos = Layout.Orientation == BindOrientation.Vertical ? y - titleBarHeight : x;
        if (availableSize <= 0) return;

        var minRatio = 0.1f;
        var cumulative = 0f;
        for (int i = 0; i < idx; i++) cumulative += Layout.Ratios[i];

        var newRatio = pos / availableSize;
        var newSlotRatio = Math.Clamp(newRatio - cumulative, minRatio, 1f - minRatio * (_slots.Count - idx - 1) - cumulative);
        var delta = newSlotRatio - Layout.Ratios[idx];
        Layout.Ratios[idx] = newSlotRatio;
        Layout.Ratios[idx + 1] -= delta;
    }

    private (float x, float y, float w, float h)[] CalculateLayout(float titleBarHeight)
    {
        if (_slots.Count == 0) return Array.Empty<(float, float, float, float)>();

        var rects = new (float x, float y, float w, float h)[_slots.Count];
        if (Layout.Ratios.Length != _slots.Count)
            Layout.Ratios = Enumerable.Repeat(1f / _slots.Count, _slots.Count).ToArray();

        var availableHeight = _height - titleBarHeight;

        if (Layout.Orientation == BindOrientation.Vertical)
        {
            float yOffset = titleBarHeight;
            for (int i = 0; i < _slots.Count; i++)
            {
                var h = availableHeight * Layout.Ratios[i];
                rects[i] = (0, yOffset, _width, h);
                yOffset += h;
            }
        }
        else
        {
            float xOffset = 0;
            for (int i = 0; i < _slots.Count; i++)
            {
                var w = _width * Layout.Ratios[i];
                rects[i] = (xOffset, titleBarHeight, w, availableHeight);
                xOffset += w;
            }
        }
        return rects;
    }

    private void RenderDividers(RenderContext ctx, (float x, float y, float w, float h)[] rects)
    {
        const float dividerThickness = 4f;
        for (int i = 0; i < rects.Length - 1; i++)
        {
            var r = rects[i];
            if (Layout.Orientation == BindOrientation.Vertical)
                ctx.Rect(0, r.y + r.h - dividerThickness / 2, _width, dividerThickness, 0x444455ff);
            else
                ctx.Rect(r.x + r.w - dividerThickness / 2, 48f, dividerThickness, _height - 48f, 0x444455ff);
        }
    }

    private int HitTestDivider(float x, float y, float titleBarHeight)
    {
        var rects = CalculateLayout(titleBarHeight);
        for (int i = 0; i < rects.Length - 1; i++)
        {
            var r = rects[i];
            if (Layout.Orientation == BindOrientation.Vertical)
            {
                var divY = r.y + r.h;
                if (y >= divY - DividerHitWidth / 2 && y <= divY + DividerHitWidth / 2) return i;
            }
            else
            {
                var divX = r.x + r.w;
                if (x >= divX - DividerHitWidth / 2 && x <= divX + DividerHitWidth / 2) return i;
            }
        }
        return -1;
    }

    public void AddPlugin(IWindowPlugin plugin)
    {
        _slots.Add(new BindSlot(plugin));
        RecalculateRatios();
    }

    public IWindowPlugin? RemoveSlot(int index)
    {
        if (index < 0 || index >= _slots.Count) return null;
        var plugin = _slots[index].Plugin;
        _slots.RemoveAt(index);
        RecalculateRatios();
        return plugin;
    }

    private void RecalculateRatios()
    {
        if (_slots.Count == 0) return;
        Layout.Ratios = Enumerable.Repeat(1f / _slots.Count, _slots.Count).ToArray();
    }

    public IEnumerable<IWindowPlugin> GetPlugins() => _slots.Select(s => s.Plugin);

    /// <summary>
    /// Hot-swap a slot's plugin. Transfers state via IStatefulPlugin if both sides support it.
    /// Same pattern as ManagedWindow.SwapPlugin — dispose old, assign new.
    /// </summary>
    public void SwapSlotPlugin(int index, IWindowPlugin newPlugin)
    {
        if (index < 0 || index >= _slots.Count) return;
        var slot = _slots[index];
        var old = slot.Plugin;

        if (old is IStatefulPlugin oldStateful && newPlugin is IStatefulPlugin newStateful)
            newStateful.RestoreState(oldStateful.SerializeState());

        (old as IDisposable)?.Dispose();
        slot.Plugin = newPlugin;
        HostWindow?.RequestRedraw();
    }

    /// <summary>Check if any slot uses the given window type.</summary>
    public bool HasWindowType(string windowType)
        => _slots.Any(s => s.Plugin.WindowType == windowType);

    /// <summary>Get slot indices matching a window type (for reload targeting).</summary>
    public IEnumerable<int> GetSlotIndices(string windowType)
    {
        for (int i = 0; i < _slots.Count; i++)
            if (_slots[i].Plugin.WindowType == windowType)
                yield return i;
    }

    public void Dispose()
    {
        OnPopout = null;
        OnAction = null;
        _slots.Clear();
        HostWindow = null;
    }
}

public class BindSlot
{
    private static uint _nextSlotId = 10000;

    public IWindowPlugin Plugin { get; internal set; }
    public FrameState State { get; }
    public uint SlotId { get; }

    public BindSlot(IWindowPlugin plugin)
    {
        Plugin = plugin;
        SlotId = _nextSlotId++;
        State = new FrameState { WindowId = SlotId };
    }
}

public class TabGroup
{
    public string GroupId { get; }
    private readonly List<ManagedWindow> _windows = new();
    private readonly object _lock = new();
    public string? ActiveWindowId { get; private set; }
    public int WindowCount { get { lock (_lock) return _windows.Count; } }

    public TabGroup(string groupId, ManagedWindow[] windows)
    {
        GroupId = groupId;
        _windows.AddRange(windows);
        ActiveWindowId = windows.Length > 0 ? windows[0].Id : null;
    }

    public void AddWindow(ManagedWindow window)
    {
        lock (_lock)
        {
            if (!_windows.Contains(window))
            {
                _windows.Add(window);
                ActiveWindowId ??= window.Id;
            }
        }
    }

    public void RemoveWindow(string windowId)
    {
        lock (_lock)
        {
            _windows.RemoveAll(w => w.Id == windowId);
            if (ActiveWindowId == windowId)
                ActiveWindowId = _windows.Count > 0 ? _windows[0].Id : null;
        }
    }

    public void SetActiveWindow(string windowId)
    {
        lock (_lock)
        {
            if (_windows.Any(w => w.Id == windowId))
                ActiveWindowId = windowId;
        }
    }

    public ManagedWindow? GetActiveWindow()
    {
        lock (_lock) return _windows.FirstOrDefault(w => w.Id == ActiveWindowId);
    }

    public IEnumerable<ManagedWindow> GetWindows()
    {
        lock (_lock) return _windows.ToList();
    }

    public IEnumerable<string> GetWindowIds()
    {
        lock (_lock) return _windows.Select(w => w.Id).ToList();
    }

    private string[] _cachedIds = Array.Empty<string>();
    private string[] _cachedTitles = Array.Empty<string>();

    /// <summary>
    /// Tab info for render thread — snapshots under lock to prevent
    /// IndexOutOfRangeException from concurrent Add/Remove during render.
    /// </summary>
    public (string[] ids, string[] titles, string activeId) GetTabInfo()
    {
        lock (_lock)
        {
            if (_cachedIds.Length != _windows.Count)
            {
                _cachedIds = new string[_windows.Count];
                _cachedTitles = new string[_windows.Count];
            }
            for (int i = 0; i < _windows.Count; i++)
            {
                _cachedIds[i] = _windows[i].Id;
                _cachedTitles[i] = _windows[i].GetPlugin().WindowTitle;
            }
            return (_cachedIds, _cachedTitles, ActiveWindowId ?? "");
        }
    }
}

public class BindLayout
{
    public BindLayoutType Type { get; set; }
    public BindOrientation Orientation { get; set; }
    public float[] Ratios { get; set; } = Array.Empty<float>();
    public int Spacing { get; set; } = 0;

    public BindLayout(BindLayoutType type, BindOrientation orientation)
    {
        Type = type;
        Orientation = orientation;
    }
}

public enum BindLayoutType { Split, Stack, Grid }
public enum BindOrientation { Horizontal, Vertical }

internal class OverlayAdapter : IWindowPlugin
{
    private readonly IOverlayContent _content;
    public OverlayAdapter(IOverlayContent content) => _content = content;
    public string WindowType => "overlay";
    public (float Width, float Height) DefaultSize => (260, 400);
    public void Render(RenderContext ctx) => _content.Render(ctx);
    public HitTestResult? HitTest(float x, float y, float w, float h) => _content.HitTest(x, y, w, h);
}

internal sealed class TabDragOverlayContent : IOverlayContent
{
    private readonly string _title;

    public TabDragOverlayContent(string title)
    {
        _title = title.Length > 44 ? title[..41] + "..." : title;
    }

    public void Render(RenderContext ctx)
    {
        float w = ctx.State.Width;
        float h = ctx.State.Height;

        ctx.RoundedRect(0, 0, w, h, 6, 0x1e2433ff);
        ctx.RoundedRectStroke(0, 0, w, h, 6, 1, 0x4a5e81ff);
        ctx.Rect(0, h - 2, w, 2, 0x4a6fa5ff);
        ctx.Text(10, h / 2 + 5, _title, 13, 0xe6ecffff, FontId.Bold);
    }

    public HitTestResult? HitTest(float x, float y, float w, float h) => null;
}
