using System.Runtime.InteropServices;
using AppKit;
using CoreAnimation;
using CoreGraphics;
using Foundation;
using Metal;
using ObjCRuntime;
using Keystone.Core;
using Keystone.Core.Rendering;

namespace Keystone.Core.Platform;

/// <summary>
/// macOS native bindings via .NET Apple platform SDK.
/// Replaces the old manual objc_msgSend P/Invoke approach.
/// should be refactored to 'MacOSAppKit' or something like that to be more specific, 
/// since this a macos specifc implementation, and need other platform-specific bindings 
/// in the future, and probably don't want to build a monolithic class with bindings for all platforms.
/// also need to remove the application speicfic stuff, and expose the right api
/// so it can become a framework
/// </summary>
public static class NativeAppKit
{
    public static void ActivateApp()
    {
        var app = NSApplication.SharedApplication;
        app.ActivationPolicy = NSApplicationActivationPolicy.Regular;
        app.FinishLaunching();
        app.Activate();
    }

    public static void PumpRunLoop(double seconds = 0.01)
    {
        NSRunLoop.Current.RunUntil(NSDate.FromTimeIntervalSinceNow(seconds));
    }

    // === Window Creation ===

    public static NSWindow CreateBorderlessWindow(double x, double y, double width, double height)
    {
        var window = new NSWindow(
            new CGRect(x, y, width, height),
            NSWindowStyle.Borderless | NSWindowStyle.Resizable,
            NSBackingStore.Buffered, false)
        {
            Level = NSWindowLevel.Floating,
            HidesOnDeactivate = false,
            BackgroundColor = NSColor.DarkGray,
            IsOpaque = true
        };
        return window;
    }

    public static NSWindow CreateOverlayWindow(double x, double y, double width, double height)
    {
        var window = new NSWindow(
            new CGRect(x, y, width, height),
            NSWindowStyle.Borderless,
            NSBackingStore.Buffered, false)
        {
            Level = NSWindowLevel.Floating,
            HidesOnDeactivate = false,
            BackgroundColor = NSColor.DarkGray,
            IsOpaque = true,
            HasShadow = true
        };
        return window;
    }

    public static NSWindow CreateContainerWindow(double x, double y, double width, double height)
    {
        var window = new NSWindow(
            new CGRect(x, y, width, height),
            NSWindowStyle.Borderless | NSWindowStyle.Resizable,
            NSBackingStore.Buffered, false)
        {
            Level = NSWindowLevel.Normal,
            HidesOnDeactivate = false,
            BackgroundColor = NSColor.Clear,
            IsOpaque = false
        };
        return window;
    }

    public static (NSWindow window, NSView view, CAMetalLayer metalLayer) CreateWindowCentered(
        string title, int width, int height)
    {
        var screen = NSScreen.MainScreen!.Frame;
        var x = (screen.Width - width) / 2;
        var y = (screen.Height - height) / 2;

        var window = CreateBorderlessWindow(x, y, width, height);
        var view = window.ContentView!;
        var metalLayer = MakeLayerBacked(view);
        window.MakeKeyAndOrderFront(null);

        return (window, view, metalLayer);
    }

    // === Metal Layer ===

    public static CAMetalLayer MakeLayerBacked(NSView view)
    {
        view.WantsLayer = true;

        // Create a Metal subview instead of making the parent view layer-hosting.
        // Layer-hosting views (view.Layer = custom) don't support subviews —
        // WKWebView needs to be a sibling subview of the Metal view.
        var metalView = new NSView(view.Bounds);
        metalView.AutoresizingMask = NSViewResizingMask.WidthSizable | NSViewResizingMask.HeightSizable;
        metalView.WantsLayer = true;
        var metalLayer = new CAMetalLayer();
        metalView.Layer = metalLayer;
        view.AddSubview(metalView);

        return metalLayer;
    }

    public static void ConfigureMetalLayer(CAMetalLayer layer, IMTLDevice device, double contentsScale)
    {
        layer.Device = device;
        layer.PixelFormat = MTLPixelFormat.BGRA8Unorm;
        layer.MaximumDrawableCount = 3;
        layer.ContentsScale = (nfloat)contentsScale;
    }

    // === Window Operations ===

    public static void ShowWindow(NSWindow window) => window.MakeKeyAndOrderFront(null);
    public static void CloseWindow(NSWindow window) => window.Close();
    public static void HideWindow(NSWindow window) => window.OrderOut(null);
    public static void MinimizeWindow(NSWindow window) => window.Miniaturize(null);

    public static void SetWindowFrame(NSWindow window, CGRect frame, bool animate = false)
        => window.SetFrame(frame, true, animate);

    public static void SetWindowFrame(NSWindow window, double x, double y, double width, double height, bool animate = false)
        => window.SetFrame(new CGRect(x, y, width, height), true, animate);

    public static void SetWindowFloating(NSWindow window, bool floating)
    {
        window.CollectionBehavior = floating
            ? NSWindowCollectionBehavior.Stationary | NSWindowCollectionBehavior.FullScreenAuxiliary
            : 0;
        window.Level = floating ? NSWindowLevel.Floating : NSWindowLevel.Normal;
    }

    public static void StartDrag(NSWindow window)
    {
        var currentEvent = NSApplication.SharedApplication.CurrentEvent;
        if (currentEvent != null)
            window.PerformWindowDrag(currentEvent);
    }

    public static void BringAllWindowsToFront()
    {
        foreach (var window in NSApplication.SharedApplication.DangerousWindows)
            window.OrderFront(null);
    }

    public static void TerminateApp() => NSApplication.SharedApplication.Terminate(null);

    // === Screen / Mouse ===

    public static CGRect GetMainScreenFrame() => NSScreen.MainScreen!.Frame;
    public static CGRect GetMainScreenVisibleFrame() => NSScreen.MainScreen!.VisibleFrame;
    public static CGPoint GetMouseLocationOnScreen() => NSEvent.CurrentMouseLocation;
    public static bool IsMouseButtonDown() => (NSEvent.CurrentPressedMouseButtons & 1) != 0;

    // === Cursor ===

    public static void SetCursor(CursorType cursor)
    {
        var nsCursor = cursor switch
        {
            CursorType.Pointer => NSCursor.PointingHandCursor,
            CursorType.Text => NSCursor.IBeamCursor,
            CursorType.Crosshair => NSCursor.CrosshairCursor,
            CursorType.Move => NSCursor.OpenHandCursor,
            CursorType.ResizeNS => NSCursor.ResizeUpDownCursor,
            CursorType.ResizeEW => NSCursor.ResizeLeftRightCursor,
            CursorType.Grab => NSCursor.OpenHandCursor,
            CursorType.Grabbing => NSCursor.ClosedHandCursor,
            CursorType.NotAllowed => NSCursor.OperationNotAllowedCursor,
            _ => NSCursor.ArrowCursor
        };
        nsCursor.Set();
    }
}

// === Window Delegate ===

public class KeystoneWindowDelegate : NSObject, INSWindowDelegate
{
    public Action? OnResizeStart { get; set; }
    public Action? OnResizeEnd { get; set; }
    public Action? OnResize { get; set; }

    [Export("windowWillStartLiveResize:")]
    public void WillStartLiveResize(NSNotification notification) => OnResizeStart?.Invoke();

    [Export("windowDidEndLiveResize:")]
    public void DidEndLiveResize(NSNotification notification) => OnResizeEnd?.Invoke();

    [Export("windowDidResize:")]
    public void DidResize(NSNotification notification) => OnResize?.Invoke();
}

// === App Delegate ===

public class KeystoneAppDelegate : NSApplicationDelegate
{
    public Action? OnDockClick { get; set; }
    public Func<IEnumerable<(string id, string title, NSWindow window)>>? GetWindows { get; set; }

    public override bool ApplicationShouldHandleReopen(NSApplication sender, bool hasVisibleWindows)
    {
        OnDockClick?.Invoke();
        return true;
    }

    public override NSMenu ApplicationDockMenu(NSApplication sender)
    {
        var menu = new NSMenu();
        var windows = GetWindows?.Invoke();
        if (windows == null) return menu;

        foreach (var (id, title, window) in windows)
        {
            var w = window; // capture
            var item = new NSMenuItem(title, (s, e) =>
            {
                w.MakeKeyAndOrderFront(null);
                w.Deminiaturize(null);
            });
            menu.AddItem(item);
        }
        return menu;
    }
}

// === Menu Bar ===

public static class MainMenuFactory
{
    private static Action<string>? _onMenuAction;
    private static NSMenu? _toolsMenu;
    private static NSMenu? _mainMenu;
    private static readonly Dictionary<string, NSMenu> _namedMenus = new();

    /// <summary>Populate the Tools menu with discovered tool scripts.</summary>
    public static void AddToolScripts(string[] scriptNames)
    {
        if (_toolsMenu == null) return;
        _toolsMenu.RemoveAllItems();
        foreach (var name in scriptNames)
        {
            var display = name.Replace('_', ' ');
            display = char.ToUpper(display[0]) + display[1..];
            AddActionItem(_toolsMenu, display, $"run_tool:{name}");
        }
    }

    /// <summary>Add a menu item at runtime. Apps call this from ICorePlugin.Initialize().</summary>
    public static void AddMenuItem(string menu, string title, string action, string shortcut = "")
    {
        if (!_namedMenus.TryGetValue(menu, out var nsMenu))
        {
            // Create new submenu on the fly, insert before Window menu
            nsMenu = new NSMenu(menu);
            _namedMenus[menu] = nsMenu;
            if (_mainMenu != null)
            {
                // Insert before the last menu (Window) to keep Window at the end
                var insertIdx = Math.Max(0, (int)_mainMenu.Count - 1);
                var item = new NSMenuItem(menu) { Submenu = nsMenu };
                _mainMenu.InsertItem(item, insertIdx);
            }
        }
        AddActionItem(nsMenu, title, action, shortcut);
    }

    public static void Initialize(Action<string> onMenuAction, KeystoneConfig? config = null)
    {
        _onMenuAction = onMenuAction;
        _namedMenus.Clear();
        var app = NSApplication.SharedApplication;
        var mainMenu = new NSMenu();
        _mainMenu = mainMenu;

        // App menu (universal — always present)
        var appName = config?.Name ?? "Keystone";
        var appMenu = new NSMenu(appName);
        AddActionItem(appMenu, "Preferences...", "spawn:settings", ",");
        appMenu.AddItem(NSMenuItem.SeparatorItem);
        AddNativeItem(appMenu, $"Hide {appName}", "hide:", "h");
        AddNativeItem(appMenu, "Hide Others", "hideOtherApplications:", "h",
            NSEventModifierMask.CommandKeyMask | NSEventModifierMask.AlternateKeyMask);
        AddNativeItem(appMenu, "Show All", "unhideAllApplications:");
        appMenu.AddItem(NSMenuItem.SeparatorItem);
        AddNativeItem(appMenu, $"Quit {appName}", "terminate:", "q");
        AddSubmenu(mainMenu, appName, appMenu);

        // File menu — config-driven
        BuildFileMenu(mainMenu, config);

        // Custom menus from config.Menus (keys other than "File")
        if (config?.Menus != null)
        {
            foreach (var (menuName, items) in config.Menus)
            {
                if (menuName == "File") continue; // already handled
                var customMenu = new NSMenu(menuName);
                foreach (var item in items)
                    AddActionItem(customMenu, item.Title, item.Action, item.Shortcut ?? "");
                AddSubmenu(mainMenu, menuName, customMenu);
                _namedMenus[menuName] = customMenu;
            }
        }

        // Tools menu (populated dynamically via AddToolScripts)
        var toolsMenu = new NSMenu("Tools");
        AddSubmenu(mainMenu, "Tools", toolsMenu);
        _toolsMenu = toolsMenu;
        _namedMenus["Tools"] = toolsMenu;

        // Window menu (universal — always present)
        var windowMenu = new NSMenu("Window");
        AddActionItem(windowMenu, "Dev Console", "dev_console", "c",
            NSEventModifierMask.CommandKeyMask | NSEventModifierMask.ShiftKeyMask);
        windowMenu.AddItem(NSMenuItem.SeparatorItem);
        AddNativeItem(windowMenu, "Minimize", "performMiniaturize:", "m");
        AddActionItem(windowMenu, "Bring All to Front", "bring_all_front");
        AddSubmenu(mainMenu, "Window", windowMenu);
        _namedMenus["Window"] = windowMenu;

        app.MainMenu = mainMenu;
        app.WindowsMenu = windowMenu;
    }

    /// <summary>Build the File menu from config — explicit menus, window configs, or nothing.</summary>
    private static void BuildFileMenu(NSMenu mainMenu, KeystoneConfig? config)
    {
        // Priority 1: Explicit "File" menu in config.Menus
        if (config?.Menus != null && config.Menus.TryGetValue("File", out var fileItems))
        {
            var fileMenu = new NSMenu("File");
            foreach (var item in fileItems)
                AddActionItem(fileMenu, item.Title, item.Action, item.Shortcut ?? "");
            AddSubmenu(mainMenu, "File", fileMenu);
            _namedMenus["File"] = fileMenu;
            return;
        }

        // Priority 2: Auto-generate from config.Windows
        if (config?.Windows != null && config.Windows.Count > 0)
        {
            var fileMenu = new NSMenu("File");
            bool first = true;
            foreach (var winCfg in config.Windows)
            {
                var title = winCfg.Title ?? winCfg.Component;
                var shortcut = first ? "n" : "";
                AddActionItem(fileMenu, $"New {title}", $"spawn:{winCfg.Component}", shortcut);
                first = false;
            }
            AddSubmenu(mainMenu, "File", fileMenu);
            _namedMenus["File"] = fileMenu;
            return;
        }

        // Priority 3: No file menu at all — app has no windows declared in config
        // (windows may still be registered at runtime by plugins)
    }

    private static void AddActionItem(NSMenu menu, string title, string action,
        string key = "", NSEventModifierMask modifiers = 0)
    {
        var act = action; // capture
        var item = new NSMenuItem(title, (s, e) => _onMenuAction?.Invoke(act))
        {
            KeyEquivalent = key
        };
        if (modifiers != 0) item.KeyEquivalentModifierMask = modifiers;
        menu.AddItem(item);
    }

    private static void AddNativeItem(NSMenu menu, string title, string selector,
        string key = "", NSEventModifierMask modifiers = 0)
    {
        var item = new NSMenuItem(title, new Selector(selector), key);
        if (modifiers != 0) item.KeyEquivalentModifierMask = modifiers;
        menu.AddItem(item);
    }

    private static void AddSubmenu(NSMenu parent, string title, NSMenu submenu)
    {
        var item = new NSMenuItem(title) { Submenu = submenu };
        parent.AddItem(item);
    }
}
