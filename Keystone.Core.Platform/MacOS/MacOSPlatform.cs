using System.Runtime.InteropServices;
using AppKit;
using CoreAnimation;
using CoreGraphics;
using Foundation;
using Metal;
using ObjCRuntime;
using Keystone.Core;
using Keystone.Core.Rendering;

namespace Keystone.Core.Platform.MacOS;

public class MacOSPlatform : IPlatform
{
    private KeystoneAppDelegate? _appDelegate;
    private Func<IEnumerable<(string id, string title)>>? _windowListProvider;

    public void Initialize()
    {
        NSApplication.Init();
        var app = NSApplication.SharedApplication;
        app.ActivationPolicy = NSApplicationActivationPolicy.Regular;
        app.FinishLaunching();
        app.Activate();
    }

    public void Quit() => NSApplication.SharedApplication.Terminate(null);

    public void PumpRunLoop(double seconds = 0.01)
        => NSRunLoop.Current.RunUntil(NSDate.FromTimeIntervalSinceNow(seconds));

    public (double x, double y, double w, double h) GetMainScreenFrame()
    {
        var f = NSScreen.MainScreen!.Frame;
        return ((double)f.X, (double)f.Y, (double)f.Width, (double)f.Height);
    }

    public void SetCursor(CursorType cursor)
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

    public (double x, double y) GetMouseLocation()
    {
        var p = NSEvent.CurrentMouseLocation;
        return ((double)p.X, (double)p.Y);
    }

    public bool IsMouseButtonDown() => (NSEvent.CurrentPressedMouseButtons & 1) != 0;

    public void BringAllWindowsToFront()
    {
        foreach (var window in NSApplication.SharedApplication.DangerousWindows)
            window.OrderFront(null);
    }

    public INativeWindow CreateWindow(WindowConfig config)
    {
        var nsWindow = config.TitleBarStyle == "hidden"
            ? CreateTitledTransparentWindow(config.X, config.Y, config.Width, config.Height)
            : CreateBorderlessWindow(config.X, config.Y, config.Width, config.Height);

        if (config.Floating)
        {
            nsWindow.CollectionBehavior =
                NSWindowCollectionBehavior.Stationary | NSWindowCollectionBehavior.FullScreenAuxiliary;
            nsWindow.Level = NSWindowLevel.Floating;
        }

        return new MacOSNativeWindow(nsWindow, config.Renderless);
    }

    public INativeWindow CreateOverlayWindow(WindowConfig config)
    {
        var nsWindow = CreateOverlayNSWindow(config.X, config.Y, config.Width, config.Height);
        return new MacOSNativeWindow(nsWindow);
    }

    public Task<string[]?> ShowOpenDialogAsync(OpenDialogOptions opts)
    {
        var tcs = new TaskCompletionSource<string[]?>();
        NSApplication.SharedApplication.InvokeOnMainThread(() =>
        {
            try
            {
                var panel = NSOpenPanel.OpenPanel;
                if (opts.Title != null) panel.Message = opts.Title;
                panel.AllowsMultipleSelection = opts.Multiple;
                if (opts.FileExtensions is { Length: > 0 })
                    panel.AllowedFileTypes = opts.FileExtensions;

                var response = (int)panel.RunModal();
                if (response == (int)NSModalResponse.OK)
                {
                    var paths = panel.Urls
                        .Select(u => u.Path ?? "")
                        .Where(p => !string.IsNullOrEmpty(p))
                        .ToArray();
                    tcs.TrySetResult(paths.Length > 0 ? paths : null);
                }
                else
                    tcs.TrySetResult(null);
            }
            catch (Exception ex) { tcs.TrySetException(ex); }
        });
        return tcs.Task;
    }

    public Task<string?> ShowSaveDialogAsync(SaveDialogOptions opts)
    {
        var tcs = new TaskCompletionSource<string?>();
        NSApplication.SharedApplication.InvokeOnMainThread(() =>
        {
            try
            {
                var panel = NSSavePanel.SavePanel;
                if (opts.Title != null) panel.Message = opts.Title;
                if (opts.DefaultName != null) panel.NameFieldStringValue = opts.DefaultName;

                var response = (int)panel.RunModal();
                var path = response == (int)NSModalResponse.OK ? panel.Url?.Path : null;
                tcs.TrySetResult(path);
            }
            catch (Exception ex) { tcs.TrySetException(ex); }
        });
        return tcs.Task;
    }

    public Task<int> ShowMessageBoxAsync(MessageBoxOptions opts)
    {
        var tcs = new TaskCompletionSource<int>();
        NSApplication.SharedApplication.InvokeOnMainThread(() =>
        {
            try
            {
                var alert = new NSAlert();
                if (!string.IsNullOrEmpty(opts.Title)) alert.MessageText = opts.Title;
                if (!string.IsNullOrEmpty(opts.Message)) alert.InformativeText = opts.Message;
                if (opts.Buttons != null)
                    foreach (var btn in opts.Buttons)
                        alert.AddButton(btn);
                if (alert.Buttons.Length == 0)
                    alert.AddButton("OK");

                var response = (int)alert.RunModal();
                // NSAlertFirstButtonReturn = 1000, second = 1001, etc.
                var index = response - 1000;
                tcs.TrySetResult(index < 0 ? 0 : index);
            }
            catch (Exception ex) { tcs.TrySetException(ex); }
        });
        return tcs.Task;
    }

    public void OpenExternal(string url)
        => NSWorkspace.SharedWorkspace.OpenUrl(new NSUrl(url));

    public void OpenPath(string path)
        => NSWorkspace.SharedWorkspace.OpenFile(path);

    public void InitializeMenu(Action<string> onMenuAction, KeystoneConfig? config = null)
        => MainMenuFactory.Initialize(onMenuAction, config);

    public void AddMenuItem(string menu, string title, string action, string shortcut = "")
        => MainMenuFactory.AddMenuItem(menu, title, action, shortcut);

    public void AddToolScripts(string[] scriptNames)
        => MainMenuFactory.AddToolScripts(scriptNames);

    public void SetWindowListProvider(Func<IEnumerable<(string id, string title)>> provider)
    {
        _windowListProvider = provider;
        if (_appDelegate != null)
            _appDelegate.GetWindowTitles = provider;
    }

    public void SetAppDelegate(Action onDockClick)
    {
        _appDelegate = new KeystoneAppDelegate
        {
            OnDockClick = onDockClick,
            GetWindowTitles = _windowListProvider
        };
        NSApplication.SharedApplication.Delegate = _appDelegate;
    }

    // === Metal layer configuration (called from ManagedWindow GPU path) ===

    public static void ConfigureMetalLayer(CAMetalLayer layer, IMTLDevice device, double contentsScale)
    {
        layer.Device = device;
        layer.PixelFormat = MTLPixelFormat.BGRA8Unorm;
        layer.MaximumDrawableCount = 3;
        layer.ContentsScale = (nfloat)contentsScale;
    }

    // === NSWindow creation helpers ===

    private static NSWindow CreateBorderlessWindow(double x, double y, double width, double height)
    {
        return new NSWindow(
            new CGRect(x, y, width, height),
            NSWindowStyle.Borderless | NSWindowStyle.Resizable,
            NSBackingStore.Buffered, false)
        {
            Level = NSWindowLevel.Normal,
            HidesOnDeactivate = false,
            BackgroundColor = NSColor.DarkGray,
            IsOpaque = true
        };
    }

    private static NSWindow CreateTitledTransparentWindow(double x, double y, double width, double height)
    {
        return new NSWindow(
            new CGRect(x, y, width, height),
            NSWindowStyle.Titled | NSWindowStyle.Resizable | NSWindowStyle.Closable
                | NSWindowStyle.Miniaturizable | NSWindowStyle.FullSizeContentView,
            NSBackingStore.Buffered, false)
        {
            Level = NSWindowLevel.Normal,
            HidesOnDeactivate = false,
            TitlebarAppearsTransparent = true,
            TitleVisibility = NSWindowTitleVisibility.Hidden,
            BackgroundColor = NSColor.Black,
            IsOpaque = true
        };
    }

    private static NSWindow CreateOverlayNSWindow(double x, double y, double width, double height)
    {
        return new NSWindow(
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
    }
}

// === App Delegate (moved from NativeAppKit.cs) ===

internal class KeystoneAppDelegate : NSApplicationDelegate
{
    public Action? OnDockClick { get; set; }
    public Func<IEnumerable<(string id, string title)>>? GetWindowTitles { get; set; }

    public override bool ApplicationShouldHandleReopen(NSApplication sender, bool hasVisibleWindows)
    {
        OnDockClick?.Invoke();
        return true;
    }

    public override NSMenu ApplicationDockMenu(NSApplication sender)
    {
        var menu = new NSMenu();
        var windows = GetWindowTitles?.Invoke();
        if (windows == null) return menu;

        foreach (var (id, title) in windows)
        {
            var item = new NSMenuItem(title, (s, e) =>
            {
                // Bring all windows to front when dock menu item is clicked
                foreach (var w in NSApplication.SharedApplication.DangerousWindows)
                    w.OrderFront(null);
            });
            menu.AddItem(item);
        }
        return menu;
    }
}

// === Menu Bar (moved from NativeAppKit.cs) ===

internal static class MainMenuFactory
{
    private static Action<string>? _onMenuAction;
    private static NSMenu? _toolsMenu;
    private static NSMenu? _mainMenu;
    private static readonly Dictionary<string, NSMenu> _namedMenus = new();

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

    public static void AddMenuItem(string menu, string title, string action, string shortcut = "")
    {
        if (!_namedMenus.TryGetValue(menu, out var nsMenu))
        {
            nsMenu = new NSMenu(menu);
            _namedMenus[menu] = nsMenu;
            if (_mainMenu != null)
            {
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

        BuildFileMenu(mainMenu, config);

        if (config?.Menus != null)
        {
            foreach (var (menuName, items) in config.Menus)
            {
                if (menuName == "File") continue;
                var customMenu = new NSMenu(menuName);
                foreach (var item in items)
                    AddActionItem(customMenu, item.Title, item.Action, item.Shortcut ?? "");
                AddSubmenu(mainMenu, menuName, customMenu);
                _namedMenus[menuName] = customMenu;
            }
        }

        var toolsMenu = new NSMenu("Tools");
        AddSubmenu(mainMenu, "Tools", toolsMenu);
        _toolsMenu = toolsMenu;
        _namedMenus["Tools"] = toolsMenu;

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

    private static void BuildFileMenu(NSMenu mainMenu, KeystoneConfig? config)
    {
        if (config?.Menus != null && config.Menus.TryGetValue("File", out var fileItems))
        {
            var fileMenu = new NSMenu("File");
            foreach (var item in fileItems)
                AddActionItem(fileMenu, item.Title, item.Action, item.Shortcut ?? "");
            AddSubmenu(mainMenu, "File", fileMenu);
            _namedMenus["File"] = fileMenu;
            return;
        }

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
        }
    }

    private static void AddActionItem(NSMenu menu, string title, string action,
        string key = "", NSEventModifierMask modifiers = 0)
    {
        var act = action;
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
