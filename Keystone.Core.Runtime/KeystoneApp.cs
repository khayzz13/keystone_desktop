// KeystoneApp — Fluent builder API for creating Keystone applications
// Sugar on top of KeystoneConfig + ApplicationRuntime.
// Equivalent to writing a keystone.json, but from C# code.
//
// Handles the full bootstrap sequence: platform init → runtime → run loop → shutdown.
// Apps can use this instead of writing their own Program.cs.

using Keystone.Core;
using Keystone.Core.Graphics.Skia;
using Keystone.Core.Platform;
using Keystone.Core.Platform.MacOS;
using Keystone.Core.Plugins;

namespace Keystone.Core.Runtime;

public class KeystoneApp
{
    private readonly KeystoneConfig _config = new();
    private readonly List<Action<PluginRegistry>> _registrations = new();
    private string? _rootDir;

    private KeystoneApp() { }

    public static KeystoneApp Create(string name, string id)
    {
        var app = new KeystoneApp();
        app._config.Name = name;
        app._config.Id = id;
        return app;
    }

    /// <summary>Register a web window (renders title bar + Flex.Bun component).</summary>
    public KeystoneApp Window(string component, Action<WindowBuilder>? configure = null)
    {
        var builder = new WindowBuilder(component);
        configure?.Invoke(builder);
        _config.Windows.Add(builder.Build());
        return this;
    }

    /// <summary>Register a native IWindowPlugin type.</summary>
    public KeystoneApp Window<T>() where T : IWindowPlugin, new()
    {
        _registrations.Add(registry => registry.RegisterWindow(new T()));
        return this;
    }

    /// <summary>Register a native IWindowPlugin instance.</summary>
    public KeystoneApp Window(IWindowPlugin plugin)
    {
        _registrations.Add(registry => registry.RegisterWindow(plugin));
        return this;
    }

    /// <summary>Register a service plugin.</summary>
    public KeystoneApp Service<T>() where T : IServicePlugin, new()
    {
        _registrations.Add(registry => registry.RegisterService(new T()));
        return this;
    }

    public KeystoneApp WithBun(string root = "bun")
    {
        _config.Bun = new BunConfig { Root = root };
        return this;
    }

    public KeystoneApp WithPlugins(string dir = "dylib")
    {
        _config.PluginDir = dir;
        return this;
    }

    public KeystoneApp RootDir(string path)
    {
        _rootDir = path;
        return this;
    }

    /// <summary>Build, initialize, and run the application. Blocks until shutdown.</summary>
    public void Run()
    {
        // Tee stdout/stderr to log file
        SetupLogging();

        var rootDir = _rootDir ?? FindRootDir(
            Path.GetDirectoryName(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar))
            ?? AppContext.BaseDirectory) ?? AppContext.BaseDirectory;

        Console.WriteLine($"[Keystone] Starting {_config.Name} ({_config.Id})...");

        // Platform init — must happen before ApplicationRuntime
        NativeLibraryLoader.Initialize();
        var platform = new MacOSPlatform();
        platform.Initialize();
        SkiaWindow.Initialize();

        var runtime = new ApplicationRuntime(_config, rootDir, platform);

        // Register native plugins after engine init — they'll be available for SpawnInitialWindows
        runtime.OnInitialized += () =>
        {
            foreach (var reg in _registrations)
                reg(runtime.PluginRegistry);
        };

        runtime.Initialize();

        Console.WriteLine("[Keystone] Entering main loop...");
        try { runtime.Run(); }
        catch (Exception ex) { Console.WriteLine($"[Keystone] Fatal: {ex}"); }
        finally { runtime.Shutdown(); }
    }

    private static void SetupLogging()
    {
        try
        {
            var logPath = Environment.GetEnvironmentVariable("KEYSTONE_LOG")
                ?? Path.Combine(Path.GetTempPath(), "keystone.log");
            var logStream = new FileStream(logPath, FileMode.Create, FileAccess.Write, FileShare.Read);
            var logWriter = new StreamWriter(logStream) { AutoFlush = true };
            Console.SetOut(new TeeTextWriter(Console.Out, logWriter));
            Console.SetError(new TeeTextWriter(Console.Error, logWriter));
        }
        catch { /* non-fatal: proceed without logging to file */ }
    }

    /// <summary>Walk up from start dir looking for keystone.json or dylib/ folder.</summary>
    private static string? FindRootDir(string startDir)
    {
        var dir = startDir;
        for (int i = 0; i < 6; i++)
        {
            if (File.Exists(Path.Combine(dir, "keystone.json"))) return dir;
            if (Directory.Exists(Path.Combine(dir, "dylib"))) return dir;
            var parent = Path.GetDirectoryName(dir);
            if (parent == null || parent == dir) break;
            dir = parent;
        }
        return null;
    }
}

public class WindowBuilder
{
    private readonly WindowConfig _cfg;

    internal WindowBuilder(string component)
    {
        _cfg = new WindowConfig { Component = component };
    }

    public WindowBuilder Title(string title) { _cfg.Title = title; return this; }
    public WindowBuilder Size(float w, float h) { _cfg.Width = w; _cfg.Height = h; return this; }
    public WindowBuilder NoSpawn() { _cfg.Spawn = false; return this; }

    public WindowBuilder Toolbar(Action<ToolbarBuilder> configure)
    {
        var tb = new ToolbarBuilder();
        configure(tb);
        _cfg.Toolbar = tb.Build();
        return this;
    }

    internal WindowConfig Build() => _cfg;
}

public class ToolbarBuilder
{
    private readonly List<ToolbarItemConfig> _items = new();

    public ToolbarBuilder Button(string label, string action)
    {
        _items.Add(new ToolbarItemConfig { Label = label, Action = action });
        return this;
    }

    public ToolbarBuilder Icon(string icon, string action)
    {
        _items.Add(new ToolbarItemConfig { Icon = icon, Action = action });
        return this;
    }

    public ToolbarBuilder Separator()
    {
        _items.Add(new ToolbarItemConfig { Type = "separator" });
        return this;
    }

    internal ToolbarConfig Build() => new() { Items = _items };
}

/// <summary>Writes to both console and log file simultaneously.</summary>
class TeeTextWriter : TextWriter
{
    private readonly TextWriter _a, _b;
    public TeeTextWriter(TextWriter a, TextWriter b) { _a = a; _b = b; }
    public override System.Text.Encoding Encoding => _a.Encoding;
    public override void Write(char value) { _a.Write(value); _b.Write(value); }
    public override void Write(string? value) { _a.Write(value); _b.Write(value); }
    public override void WriteLine(string? value) { _a.WriteLine(value); _b.WriteLine(value); }
    public override void Flush() { _a.Flush(); _b.Flush(); }
}
