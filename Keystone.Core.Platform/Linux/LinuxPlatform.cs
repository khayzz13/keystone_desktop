// LinuxPlatform — GTK4 implementation of IPlatform.
// Uses P/Invoke into libgtk-4.so and libgdk-4.0.so.
// All UI operations must happen on the GTK main thread (the calling thread of Initialize).

using System.Diagnostics;
using System.Runtime.InteropServices;
using Keystone.Core;
using Keystone.Core.Rendering;

namespace Keystone.Core.Platform.Linux;

public class LinuxPlatform : IPlatform
{
    private IntPtr _app;       // GtkApplication*
    private IntPtr _display;   // GdkDisplay*
    private Action<string>? _onMenuAction;
    private Func<IEnumerable<(string id, string title)>>? _windowListProvider;

    // GApplication activate signal callback (required for g_application_register)
    private static readonly GtkCallbacks.GCallback _activateCb = OnActivate;

    public void Initialize()
    {
        // gtk_init() is gtk4's lightweight init — no argc/argv needed
        Gtk.Init();

        // Create GtkApplication for menu integration and lifecycle
        _app = Gtk.ApplicationNew("com.keystone.app", 0);
        GLib.SignalConnectData(_app, "activate", _activateCb, IntPtr.Zero, IntPtr.Zero, 0);
        GLib.ApplicationRegister(_app, IntPtr.Zero, IntPtr.Zero);

        _display = Gdk.DisplayGetDefault();
    }

    private static void OnActivate(IntPtr app, IntPtr userData) { /* no-op, windows are created manually */ }

    public void Quit()
    {
        if (_app != IntPtr.Zero)
            GLib.ApplicationQuit(_app);
    }

    public void PumpRunLoop(double seconds = 0.01)
    {
        // Process all pending GTK events
        var sw = Stopwatch.StartNew();
        var timeoutMs = (long)(seconds * 1000);
        while (Gtk.EventsPending())
        {
            Gtk.MainIteration();
            if (sw.ElapsedMilliseconds > timeoutMs) break;
        }
    }

    public (double x, double y, double w, double h) GetMainScreenFrame()
    {
        if (_display == IntPtr.Zero)
            return (0, 0, 1920, 1080);

        var monitors = Gdk.DisplayGetMonitors(_display);
        if (monitors == IntPtr.Zero)
            return (0, 0, 1920, 1080);

        var monitor = GLib.ListModelGetItem(monitors, 0);
        if (monitor == IntPtr.Zero)
            return (0, 0, 1920, 1080);

        Gdk.MonitorGetGeometry(monitor, out var rect);
        GLib.ObjectUnref(monitor);
        return (rect.X, rect.Y, rect.Width, rect.Height);
    }

    public void SetCursor(CursorType cursor)
    {
        // GTK4 cursors are set per-widget, not globally.
        // The active window's widget cursor is updated via LinuxNativeWindow.
        // Global cursor change is a no-op here — windows handle it.
    }

    public (double x, double y) GetMouseLocation()
    {
        // GTK4 removed global pointer query.
        // Use GdkSeat → GdkDevice → surface position as fallback.
        if (_display == IntPtr.Zero) return (0, 0);
        var seat = Gdk.DisplayGetDefaultSeat(_display);
        if (seat == IntPtr.Zero) return (0, 0);
        var pointer = Gdk.SeatGetPointer(seat);
        if (pointer == IntPtr.Zero) return (0, 0);
        // No good global coordinate API in GTK4/Wayland — return (0,0)
        return (0, 0);
    }

    public bool IsMouseButtonDown()
    {
        // No clean global query in GTK4/Wayland.
        return false;
    }

    public void BringAllWindowsToFront()
    {
        // GTK4 on Wayland cannot raise windows programmatically.
        // On X11 this would use gtk_window_present(), but that's per-window.
    }

    public INativeWindow CreateWindow(WindowConfig config)
    {
        var window = new LinuxNativeWindow(_app, config, isOverlay: false);
        return window;
    }

    public INativeWindow CreateOverlayWindow(WindowConfig config)
    {
        var window = new LinuxNativeWindow(_app, config, isOverlay: true);
        return window;
    }

    public Task<string[]?> ShowOpenDialogAsync(OpenDialogOptions opts)
    {
        var tcs = new TaskCompletionSource<string[]?>();

        // GTK4 FileDialog (4.10+) or fallback to FileChooserDialog
        var dialog = Gtk.FileDialogNew();
        if (opts.Title != null)
            Gtk.FileDialogSetTitle(dialog, opts.Title);

        // Use the async open method
        Gtk.FileDialogOpen(dialog, IntPtr.Zero, IntPtr.Zero,
            (source, result, _) =>
            {
                var file = Gtk.FileDialogOpenFinish(source, result, IntPtr.Zero);
                if (file != IntPtr.Zero)
                {
                    var path = GLib.FileGetPath(file);
                    GLib.ObjectUnref(file);
                    tcs.TrySetResult(path != null ? new[] { path } : null);
                }
                else
                    tcs.TrySetResult(null);
            }, IntPtr.Zero);

        return tcs.Task;
    }

    public Task<string?> ShowSaveDialogAsync(SaveDialogOptions opts)
    {
        var tcs = new TaskCompletionSource<string?>();

        var dialog = Gtk.FileDialogNew();
        if (opts.Title != null)
            Gtk.FileDialogSetTitle(dialog, opts.Title);
        if (opts.DefaultName != null)
            Gtk.FileDialogSetInitialName(dialog, opts.DefaultName);

        Gtk.FileDialogSave(dialog, IntPtr.Zero, IntPtr.Zero,
            (source, result, _) =>
            {
                var file = Gtk.FileDialogSaveFinish(source, result, IntPtr.Zero);
                if (file != IntPtr.Zero)
                {
                    var path = GLib.FileGetPath(file);
                    GLib.ObjectUnref(file);
                    tcs.TrySetResult(path);
                }
                else
                    tcs.TrySetResult(null);
            }, IntPtr.Zero);

        return tcs.Task;
    }

    public Task<int> ShowMessageBoxAsync(MessageBoxOptions opts)
    {
        var tcs = new TaskCompletionSource<int>();

        var dialog = Gtk.AlertDialogNew(opts.Title);
        Gtk.AlertDialogSetDetail(dialog, opts.Message);

        if (opts.Buttons is { Length: > 0 })
            Gtk.AlertDialogSetButtons(dialog, opts.Buttons);
        else
            Gtk.AlertDialogSetButtons(dialog, new[] { "OK" });

        Gtk.AlertDialogChoose(dialog, IntPtr.Zero, IntPtr.Zero,
            (source, result, _) =>
            {
                var index = Gtk.AlertDialogChooseFinish(source, result, IntPtr.Zero);
                tcs.TrySetResult(index);
            }, IntPtr.Zero);

        return tcs.Task;
    }

    public void OpenExternal(string url)
    {
        if (_display != IntPtr.Zero)
            Gtk.ShowUri(IntPtr.Zero, url, 0);
        else
            Process.Start(new ProcessStartInfo("xdg-open", url) { UseShellExecute = false });
    }

    public void OpenPath(string path)
    {
        Process.Start(new ProcessStartInfo("xdg-open", path) { UseShellExecute = false });
    }

    // ── Clipboard ──────────────────────────────────────────────────────────────
    // Uses wl-paste/wl-copy (Wayland) or xclip (X11) subprocess.

    public string? ClipboardReadText()
    {
        // Wayland first
        var result = RunClipboardSubprocess("wl-paste", "--no-newline");
        if (result != null) return result;
        // X11 fallback
        return RunClipboardSubprocess("xclip", "-selection clipboard -o");
    }

    public void ClipboardWriteText(string text)
    {
        if (!WriteToClipboardSubprocess("wl-copy", text))
            WriteToClipboardSubprocess("xclip", "-selection clipboard", text);
    }

    public void ClipboardClear() => ClipboardWriteText("");

    public bool ClipboardHasText() => ClipboardReadText() is { Length: > 0 };

    private static string? RunClipboardSubprocess(string program, string args)
    {
        try
        {
            using var proc = Process.Start(new ProcessStartInfo(program, args)
                { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false });
            if (proc == null) return null;
            var output = proc.StandardOutput.ReadToEnd();
            proc.WaitForExit();
            return proc.ExitCode == 0 && output.Length > 0 ? output : null;
        }
        catch { return null; }
    }

    private static bool WriteToClipboardSubprocess(string program, string args, string text = "")
    {
        try
        {
            using var proc = Process.Start(new ProcessStartInfo(program, args)
                { RedirectStandardInput = true, UseShellExecute = false });
            if (proc == null) return false;
            proc.StandardInput.Write(text);
            proc.StandardInput.Close();
            proc.WaitForExit();
            return proc.ExitCode == 0;
        }
        catch { return false; }
    }

    // ── Screen ─────────────────────────────────────────────────────────────────

    public IReadOnlyList<DisplayInfo> GetAllDisplays()
    {
        var result = new List<DisplayInfo>();
        var display = Gdk.DisplayGetDefault();
        if (display == IntPtr.Zero) return result;
        var monitors = Gdk.DisplayGetMonitors(display);
        if (monitors == IntPtr.Zero) return result;
        var count = GLib.ListModelGetNItems(monitors);
        for (uint i = 0; i < count; i++)
        {
            var monitor = GLib.ListModelGetItem(monitors, i);
            if (monitor == IntPtr.Zero) continue;
            Gdk.MonitorGetGeometry(monitor, out var rect);
            var scale = Gdk.MonitorGetScaleFactor(monitor);
            result.Add(new DisplayInfo(rect.X, rect.Y, rect.Width, rect.Height, scale, i == 0));
        }
        return result;
    }

    // ── System state ───────────────────────────────────────────────────────────

    public bool IsDarkMode()
    {
        // Check XDG_CURRENT_DESKTOP + portal color-scheme first
        var scheme = Environment.GetEnvironmentVariable("GTK_THEME");
        if (scheme != null && scheme.Contains("dark", StringComparison.OrdinalIgnoreCase))
            return true;
        // Try reading the GSettings key used by GNOME/KDE
        try
        {
            var result = RunClipboardSubprocess("gsettings",
                "get org.gnome.desktop.interface color-scheme");
            if (result != null && result.Contains("dark", StringComparison.OrdinalIgnoreCase))
                return true;
        }
        catch { }
        return false;
    }

    public PowerStatus GetPowerStatus()
    {
        try
        {
            const string supplyDir = "/sys/class/power_supply";
            if (!Directory.Exists(supplyDir)) return new PowerStatus(false, -1);
            foreach (var dir in Directory.GetDirectories(supplyDir))
            {
                var typePath = Path.Combine(dir, "type");
                if (!File.Exists(typePath)) continue;
                if (File.ReadAllText(typePath).Trim() != "Battery") continue;
                var status = File.ReadAllText(Path.Combine(dir, "status")).Trim();
                var capacityPath = Path.Combine(dir, "capacity");
                var pct = File.Exists(capacityPath) &&
                          int.TryParse(File.ReadAllText(capacityPath).Trim(), out var p) ? p : -1;
                return new PowerStatus(status != "Charging" && status != "Full", pct);
            }
        }
        catch { }
        return new PowerStatus(false, -1);
    }

    // ── Notifications ──────────────────────────────────────────────────────────

    public async Task ShowOsNotification(string title, string body)
    {
        try
        {
            var t = title.Replace("\"", "\\\"");
            var b = body.Replace("\"", "\\\"");
            using var proc = Process.Start(new ProcessStartInfo("notify-send",
                $"\"{t}\" \"{b}\"") { UseShellExecute = false });
            if (proc != null) await proc.WaitForExitAsync();
        }
        catch { }
    }

    public void InitializeMenu(Action<string> onMenuAction, KeystoneConfig? config = null)
    {
        _onMenuAction = onMenuAction;

        if (config?.Menus == null || _app == IntPtr.Zero) return;

        var menuBar = GLib.MenuNew();

        foreach (var (menuName, items) in config.Menus)
        {
            var submenu = GLib.MenuNew();
            foreach (var item in items)
            {
                var actionName = $"app.{item.Action?.Replace(":", "_") ?? "noop"}";
                GLib.MenuAppendItem(submenu, item.Title ?? "", actionName);
            }
            GLib.MenuAppendSubmenu(menuBar, menuName, submenu);
        }

        Gtk.ApplicationSetMenubar(_app, menuBar);
    }

    public void AddMenuItem(string menu, string title, string action, string shortcut = "")
    {
        // Dynamic menu items — would need to rebuild the menu model.
        // For now, log and skip.
        Console.WriteLine($"[LinuxPlatform] AddMenuItem not yet implemented: {menu}/{title}");
    }

    public void AddToolScripts(string[] scriptNames)
    {
        // Tool scripts appear in the menu — requires menu model update.
        Console.WriteLine($"[LinuxPlatform] AddToolScripts: {scriptNames.Length} scripts");
    }

    public void SetWindowListProvider(Func<IEnumerable<(string id, string title)>> provider)
    {
        _windowListProvider = provider;
    }
}

// === P/Invoke bindings ===
// Thin wrappers around GTK4, GDK4, GLib, and GIO functions.

internal static class Gtk
{
    private const string Lib = "libgtk-4.so.1";

    [DllImport(Lib, EntryPoint = "gtk_init")]
    public static extern void Init();

    [DllImport(Lib, EntryPoint = "gtk_events_pending")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool EventsPending();

    [DllImport(Lib, EntryPoint = "gtk_main_iteration")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool MainIteration();

    [DllImport(Lib, EntryPoint = "gtk_application_new")]
    public static extern IntPtr ApplicationNew(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string applicationId, int flags);

    [DllImport(Lib, EntryPoint = "gtk_application_set_menubar")]
    public static extern void ApplicationSetMenubar(IntPtr app, IntPtr menuModel);

    [DllImport(Lib, EntryPoint = "gtk_window_new")]
    public static extern IntPtr WindowNew();

    [DllImport(Lib, EntryPoint = "gtk_application_window_new")]
    public static extern IntPtr ApplicationWindowNew(IntPtr app);

    [DllImport(Lib, EntryPoint = "gtk_window_set_title")]
    public static extern void WindowSetTitle(IntPtr window,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string title);

    [DllImport(Lib, EntryPoint = "gtk_window_get_title")]
    [return: MarshalAs(UnmanagedType.LPUTF8Str)]
    public static extern string? WindowGetTitle(IntPtr window);

    [DllImport(Lib, EntryPoint = "gtk_window_set_default_size")]
    public static extern void WindowSetDefaultSize(IntPtr window, int width, int height);

    [DllImport(Lib, EntryPoint = "gtk_window_set_decorated")]
    public static extern void WindowSetDecorated(IntPtr window, [MarshalAs(UnmanagedType.Bool)] bool decorated);

    [DllImport(Lib, EntryPoint = "gtk_widget_set_visible")]
    public static extern void WidgetSetVisible(IntPtr widget, [MarshalAs(UnmanagedType.Bool)] bool visible);

    [DllImport(Lib, EntryPoint = "gtk_widget_get_width")]
    public static extern int WidgetGetWidth(IntPtr widget);

    [DllImport(Lib, EntryPoint = "gtk_widget_get_height")]
    public static extern int WidgetGetHeight(IntPtr widget);

    [DllImport(Lib, EntryPoint = "gtk_widget_get_scale_factor")]
    public static extern int WidgetGetScaleFactor(IntPtr widget);

    [DllImport(Lib, EntryPoint = "gtk_widget_get_native")]
    public static extern IntPtr WidgetGetNative(IntPtr widget);

    [DllImport(Lib, EntryPoint = "gtk_native_get_surface")]
    public static extern IntPtr NativeGetSurface(IntPtr native);

    [DllImport(Lib, EntryPoint = "gtk_window_present")]
    public static extern void WindowPresent(IntPtr window);

    [DllImport(Lib, EntryPoint = "gtk_window_minimize")]
    public static extern void WindowMinimize(IntPtr window);

    [DllImport(Lib, EntryPoint = "gtk_window_unminimize")]
    public static extern void WindowUnminimize(IntPtr window);

    [DllImport(Lib, EntryPoint = "gtk_window_maximize")]
    public static extern void WindowMaximize(IntPtr window);

    [DllImport(Lib, EntryPoint = "gtk_window_close")]
    public static extern void WindowClose(IntPtr window);

    [DllImport(Lib, EntryPoint = "gtk_window_destroy")]
    public static extern void WindowDestroy(IntPtr window);

    [DllImport(Lib, EntryPoint = "gtk_window_set_keep_above")]
    public static extern void WindowSetKeepAbove(IntPtr window, [MarshalAs(UnmanagedType.Bool)] bool above);

    [DllImport(Lib, EntryPoint = "gtk_window_set_titlebar")]
    public static extern void WindowSetTitlebar(IntPtr window, IntPtr titlebar);

    // GTK4.10+ FileDialog
    [DllImport(Lib, EntryPoint = "gtk_file_dialog_new")]
    public static extern IntPtr FileDialogNew();

    [DllImport(Lib, EntryPoint = "gtk_file_dialog_set_title")]
    public static extern void FileDialogSetTitle(IntPtr dialog,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string title);

    [DllImport(Lib, EntryPoint = "gtk_file_dialog_set_initial_name")]
    public static extern void FileDialogSetInitialName(IntPtr dialog,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string name);

    [DllImport(Lib, EntryPoint = "gtk_file_dialog_open")]
    public static extern void FileDialogOpen(IntPtr dialog, IntPtr parent, IntPtr cancellable,
        GtkCallbacks.GAsyncReadyCallback callback, IntPtr userData);

    [DllImport(Lib, EntryPoint = "gtk_file_dialog_open_finish")]
    public static extern IntPtr FileDialogOpenFinish(IntPtr dialog, IntPtr result, IntPtr error);

    [DllImport(Lib, EntryPoint = "gtk_file_dialog_save")]
    public static extern void FileDialogSave(IntPtr dialog, IntPtr parent, IntPtr cancellable,
        GtkCallbacks.GAsyncReadyCallback callback, IntPtr userData);

    [DllImport(Lib, EntryPoint = "gtk_file_dialog_save_finish")]
    public static extern IntPtr FileDialogSaveFinish(IntPtr dialog, IntPtr result, IntPtr error);

    // GTK4.10+ AlertDialog
    [DllImport(Lib, EntryPoint = "gtk_alert_dialog_new")]
    public static extern IntPtr AlertDialogNew([MarshalAs(UnmanagedType.LPUTF8Str)] string format);

    [DllImport(Lib, EntryPoint = "gtk_alert_dialog_set_detail")]
    public static extern void AlertDialogSetDetail(IntPtr dialog,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string detail);

    [DllImport(Lib, EntryPoint = "gtk_alert_dialog_set_buttons")]
    public static extern void AlertDialogSetButtons(IntPtr dialog,
        [MarshalAs(UnmanagedType.LPArray, ArraySubType = UnmanagedType.LPUTF8Str)] string[] labels);

    [DllImport(Lib, EntryPoint = "gtk_alert_dialog_choose")]
    public static extern void AlertDialogChoose(IntPtr dialog, IntPtr parent, IntPtr cancellable,
        GtkCallbacks.GAsyncReadyCallback callback, IntPtr userData);

    [DllImport(Lib, EntryPoint = "gtk_alert_dialog_choose_finish")]
    public static extern int AlertDialogChooseFinish(IntPtr dialog, IntPtr result, IntPtr error);

    [DllImport(Lib, EntryPoint = "gtk_show_uri")]
    public static extern void ShowUri(IntPtr parent,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string uri, uint timestamp);

    // Overlay container
    [DllImport(Lib, EntryPoint = "gtk_overlay_new")]
    public static extern IntPtr OverlayNew();

    [DllImport(Lib, EntryPoint = "gtk_overlay_add_overlay")]
    public static extern void OverlayAddOverlay(IntPtr overlay, IntPtr child);

    [DllImport(Lib, EntryPoint = "gtk_window_set_child")]
    public static extern void WindowSetChild(IntPtr window, IntPtr child);

    // Drawing area (for Vulkan rendering surface)
    [DllImport(Lib, EntryPoint = "gtk_drawing_area_new")]
    public static extern IntPtr DrawingAreaNew();

    [DllImport(Lib, EntryPoint = "gtk_widget_set_size_request")]
    public static extern void WidgetSetSizeRequest(IntPtr widget, int width, int height);

    [DllImport(Lib, EntryPoint = "gtk_widget_set_hexpand")]
    public static extern void WidgetSetHexpand(IntPtr widget, [MarshalAs(UnmanagedType.Bool)] bool expand);

    [DllImport(Lib, EntryPoint = "gtk_widget_set_vexpand")]
    public static extern void WidgetSetVexpand(IntPtr widget, [MarshalAs(UnmanagedType.Bool)] bool expand);

    [DllImport(Lib, EntryPoint = "gtk_widget_set_cursor")]
    public static extern void WidgetSetCursor(IntPtr widget, IntPtr cursor);
}

internal static class Gdk
{
    private const string Lib = "libgtk-4.so.1"; // GDK4 symbols are in libgtk-4

    [StructLayout(LayoutKind.Sequential)]
    public struct Rectangle
    {
        public int X, Y, Width, Height;
    }

    [DllImport(Lib, EntryPoint = "gdk_display_get_default")]
    public static extern IntPtr DisplayGetDefault();

    [DllImport(Lib, EntryPoint = "gdk_display_get_monitors")]
    public static extern IntPtr DisplayGetMonitors(IntPtr display);

    [DllImport(Lib, EntryPoint = "gdk_monitor_get_geometry")]
    public static extern void MonitorGetGeometry(IntPtr monitor, out Rectangle geometry);

    [DllImport(Lib, EntryPoint = "gdk_display_get_default_seat")]
    public static extern IntPtr DisplayGetDefaultSeat(IntPtr display);

    [DllImport(Lib, EntryPoint = "gdk_seat_get_pointer")]
    public static extern IntPtr SeatGetPointer(IntPtr seat);

    [DllImport(Lib, EntryPoint = "gdk_cursor_new_from_name")]
    public static extern IntPtr CursorNewFromName(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string name, IntPtr fallback);

    // X11-specific: get xid from GdkSurface (for Vulkan surface creation)
    [DllImport("libgtk-4.so.1", EntryPoint = "gdk_x11_surface_get_xid")]
    public static extern IntPtr X11SurfaceGetXid(IntPtr surface);

    // Wayland-specific
    [DllImport("libgtk-4.so.1", EntryPoint = "gdk_wayland_surface_get_wl_surface")]
    public static extern IntPtr WaylandSurfaceGetWlSurface(IntPtr surface);

    [DllImport("libgtk-4.so.1", EntryPoint = "gdk_wayland_display_get_wl_display")]
    public static extern IntPtr WaylandDisplayGetWlDisplay(IntPtr display);

    [DllImport(Lib, EntryPoint = "gdk_surface_get_width")]
    public static extern int SurfaceGetWidth(IntPtr surface);

    [DllImport(Lib, EntryPoint = "gdk_surface_get_height")]
    public static extern int SurfaceGetHeight(IntPtr surface);

    [DllImport(Lib, EntryPoint = "gdk_surface_get_scale_factor")]
    public static extern int SurfaceGetScaleFactor(IntPtr surface);

    [DllImport(Lib, EntryPoint = "gdk_monitor_get_scale_factor")]
    public static extern int MonitorGetScaleFactor(IntPtr monitor);
}

internal static class GLib
{
    private const string Lib = "libglib-2.0.so.0";
    private const string GObjectLib = "libgobject-2.0.so.0";
    private const string GioLib = "libgio-2.0.so.0";

    [DllImport(GObjectLib, EntryPoint = "g_signal_connect_data")]
    public static extern ulong SignalConnectData(IntPtr instance,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string signal,
        GtkCallbacks.GCallback handler, IntPtr data, IntPtr destroyData, int connectFlags);

    [DllImport(GObjectLib, EntryPoint = "g_object_unref")]
    public static extern void ObjectUnref(IntPtr obj);

    [DllImport(GioLib, EntryPoint = "g_application_register")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool ApplicationRegister(IntPtr app, IntPtr cancellable, IntPtr error);

    [DllImport(GioLib, EntryPoint = "g_application_quit")]
    public static extern void ApplicationQuit(IntPtr app);

    [DllImport(GioLib, EntryPoint = "g_file_get_path")]
    [return: MarshalAs(UnmanagedType.LPUTF8Str)]
    public static extern string? FileGetPath(IntPtr file);

    [DllImport(GioLib, EntryPoint = "g_list_model_get_n_items")]
    public static extern uint ListModelGetNItems(IntPtr list);

    [DllImport(GioLib, EntryPoint = "g_list_model_get_item")]
    public static extern IntPtr ListModelGetItem(IntPtr list, uint position);

    // GMenu
    [DllImport(GioLib, EntryPoint = "g_menu_new")]
    public static extern IntPtr MenuNew();

    [DllImport(GioLib, EntryPoint = "g_menu_append")]
    public static extern void MenuAppendItem(IntPtr menu,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string? label,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string? action);

    [DllImport(GioLib, EntryPoint = "g_menu_append_submenu")]
    public static extern void MenuAppendSubmenu(IntPtr menu,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string? label, IntPtr submenu);

    [DllImport(GioLib, EntryPoint = "g_menu_append_section")]
    public static extern void MenuAppendSection(IntPtr menu,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string? label, IntPtr section);
}

internal static class GtkCallbacks
{
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate void GCallback(IntPtr instance, IntPtr userData);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate void GAsyncReadyCallback(IntPtr sourceObject, IntPtr res, IntPtr userData);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate void GtkResizeCallback(IntPtr widget, int width, int height, IntPtr userData);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public delegate bool GtkCloseRequestCallback(IntPtr widget, IntPtr userData);
}
