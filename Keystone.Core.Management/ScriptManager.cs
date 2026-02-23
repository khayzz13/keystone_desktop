// ScriptManager - Hot-reload .csx scripts for rapid prototyping

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using Microsoft.CodeAnalysis.CSharp.Scripting;
using Microsoft.CodeAnalysis.Scripting;
using Keystone.Core;
using Keystone.Core.Plugins;
using Keystone.Core.Rendering;

namespace Keystone.Core.Management;

public class ScriptManager
{
    private readonly string _dir;
    private readonly string _webDir;
    private readonly PluginRegistry _registry;
    private readonly bool _hotReload;
    private readonly Dictionary<string, IWindowPlugin> _scripts = new();
    private readonly Dictionary<string, string> _webScripts = new(); // name -> code
    private FileSystemWatcher? _watcher;
    private FileSystemWatcher? _webWatcher;

    private static ScriptManager? _instance;

    public ScriptManager(string dir, PluginRegistry registry, Keystone.Core.ScriptConfig? config = null)
    {
        _dir = dir;
        _webDir = config?.WebDir != null
            ? Path.Combine(Path.GetDirectoryName(dir)!, config.WebDir)
            : Path.Combine(Path.GetDirectoryName(dir)!, "Web", "scripts");
        _registry = registry;
        _hotReload = config?.HotReload ?? true;
        _instance = this;
    }

    public void Start()
    {
        if (!Directory.Exists(_dir)) return;

        foreach (var csx in Directory.GetFiles(_dir, "*.csx"))
            LoadScript(csx);

        if (_hotReload)
        {
            _watcher = new FileSystemWatcher(_dir, "*.csx")
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName,
                EnableRaisingEvents = true
            };

            _watcher.Changed += (s, e) => { Thread.Sleep(100); LoadScript(e.FullPath); };
            _watcher.Created += (s, e) => { Thread.Sleep(100); LoadScript(e.FullPath); };
            _watcher.Deleted += (s, e) => UnloadScript(e.FullPath);
        }

        // Watch Web/scripts for web scripts
        if (Directory.Exists(_webDir))
        {
            foreach (var csx in Directory.GetFiles(_webDir, "*.csx"))
                LoadWebScript(csx);

            if (_hotReload)
            {
                _webWatcher = new FileSystemWatcher(_webDir, "*.csx")
                {
                    NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName,
                    EnableRaisingEvents = true
                };

                _webWatcher.Changed += (s, e) => { Thread.Sleep(100); LoadWebScript(e.FullPath); };
                _webWatcher.Created += (s, e) => { Thread.Sleep(100); LoadWebScript(e.FullPath); };
                _webWatcher.Deleted += (s, e) => UnloadWebScript(e.FullPath);
            }
        }
    }

    public void Stop()
    {
        _watcher?.Dispose();
        _watcher = null;
        _webWatcher?.Dispose();
        _webWatcher = null;
    }

    private void LoadScript(string path)
    {
        UnloadScript(path);
        var name = Path.GetFileNameWithoutExtension(path);

        try
        {
            var code = File.ReadAllText(path);
            var opts = ScriptOptions.Default
                .AddReferences(typeof(RenderContext).Assembly)
                .AddImports("Keystone.Core.Plugins", "Keystone.Core.Rendering", "System");

            var result = CSharpScript.EvaluateAsync<IWindowPlugin>(code, opts).Result;
            if (result != null)
            {
                _scripts[path] = result;
                _registry.RegisterWindow(result);
                Console.WriteLine($"[ScriptManager] Loaded plugin script: {name}");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[ScriptManager] Failed to load {name}: {ex.Message}");
            Notifications.Error($"Script failed: {name} — {ex.Message}");
        }
    }

    private void UnloadScript(string path)
    {
        if (_scripts.TryGetValue(path, out var plugin))
        {
            _registry.UnregisterWindow(plugin.WindowType);
            _scripts.Remove(path);
        }
    }

    private void LoadWebScript(string path)
    {
        var name = Path.GetFileNameWithoutExtension(path);
        try
        {
            var code = File.ReadAllText(path);
            _webScripts[name] = code;
            Console.WriteLine($"[ScriptManager] Loaded web script: {name}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[ScriptManager] Failed to load web script {name}: {ex.Message}");
            Notifications.Error($"Web script failed: {name} — {ex.Message}");
        }
    }

    private void UnloadWebScript(string path)
    {
        var name = Path.GetFileNameWithoutExtension(path);
        _webScripts.Remove(name);
    }

    // === Tool Scripts (run on background thread with full runtime access) ===

    /// <summary>Run a tool script (.csx) on a background thread with ToolScriptGlobals.
    /// Accepts either a full path or just the script name (resolved from scripts/tools/).</summary>
    [UnconditionalSuppressMessage("Trimming", "IL2026", Justification = "Scripts require dynamic code")]
    [UnconditionalSuppressMessage("Trimming", "IL2072", Justification = "Scripts require dynamic code")]
    public static void RunTool(string pathOrName)
    {
        var path = pathOrName;
        if (!Path.IsPathRooted(path) && _instance != null)
            path = Path.Combine(_instance._dir, "tools", path + ".csx");
        if (!File.Exists(path)) { Console.WriteLine($"[ScriptManager] Tool script not found: {path}"); return; }

        var name = Path.GetFileNameWithoutExtension(path);
        Console.WriteLine($"[ScriptManager] Running tool script: {name}");

        ThreadPool.QueueUserWorkItem(_ =>
        {
            try
            {
                var code = File.ReadAllText(path);
                var opts = BuildToolScriptOptions();
                var globals = new ToolScriptGlobals(name);
                CSharpScript.EvaluateAsync(code, opts, globals).Wait();
                Console.WriteLine($"[ScriptManager] Tool script completed: {name}");
            }
            catch (AggregateException ae) when (ae.InnerException is ToolScriptAssertionException ex)
            {
                Console.WriteLine($"[ToolScript:{name}] ASSERTION FAILED: {ex.Message}");
                Notifications.Error($"Tool script assertion: {name} — {ex.Message}");
            }
            catch (Exception ex)
            {
                var inner = ex is AggregateException ag ? ag.InnerException ?? ex : ex;
                Console.WriteLine($"[ToolScript:{name}] ERROR: {inner.Message}");
                Notifications.Error($"Tool script error: {name} — {inner.Message}");
            }
        });
    }

    /// <summary>List available tool scripts in scripts/tools/.</summary>
    public static string[] GetToolScripts()
    {
        if (_instance == null) return Array.Empty<string>();
        var toolDir = Path.Combine(_instance._dir, "tools");
        if (!Directory.Exists(toolDir)) return Array.Empty<string>();
        return Directory.GetFiles(toolDir, "*.csx").Select(Path.GetFileNameWithoutExtension).ToArray()!;
    }

    private static ScriptOptions BuildToolScriptOptions()
    {
        var opts = ScriptOptions.Default
            .AddReferences(typeof(RenderContext).Assembly)
            .AddReferences(typeof(Process).Assembly)
            .AddImports(
                "System", "System.Collections.Generic", "System.Linq",
                "System.Threading", "System.Diagnostics",
                "Keystone.Core", "Keystone.Core.Plugins", "Keystone.Core.Rendering");

        // Add all loaded Keystone assemblies so tool scripts can access everything
        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            var name = asm.GetName().Name;
            if (name != null && name.StartsWith("Keystone.") && !asm.IsDynamic && !string.IsNullOrEmpty(asm.Location))
            {
                opts = opts.AddReferences(asm);
                // Add the root namespace for each assembly
                var ns = name; // Keystone.Core.Runtime, etc.
                opts = opts.AddImports(ns);
            }
        }

        return opts;
    }

    /// <summary>
    /// Execute a web script by name and return JSON result
    /// </summary>
    [UnconditionalSuppressMessage("Trimming", "IL2026", Justification = "Scripts require dynamic code")]
    [UnconditionalSuppressMessage("Trimming", "IL2072", Justification = "Scripts require dynamic code")]
    public static string? CallWebScript(string scriptName, string? args = null)
    {
        if (_instance == null) return null;
        if (!_instance._webScripts.TryGetValue(scriptName, out var code))
            return null;

        try
        {
            // Find Runtime assembly from already-loaded assemblies (avoids circular project reference)
            var runtimeAsm = AppDomain.CurrentDomain.GetAssemblies()
                .FirstOrDefault(a => a.GetName().Name == "Keystone.Core.Runtime");

            var opts = ScriptOptions.Default
                .AddReferences(typeof(Process).Assembly, typeof(JsonSerializer).Assembly)
                .AddImports("System", "System.Diagnostics", "System.Text.Json", "System.Collections.Generic");

            if (runtimeAsm != null)
            {
                opts = opts.AddReferences(runtimeAsm).AddImports("Keystone.Core.Runtime");
            }

            var globals = new WebScriptGlobals { Args = args ?? "" };
            var result = CSharpScript.EvaluateAsync<object>(code, opts, globals).Result;
            return globals.JsonResult ?? JsonSerializer.Serialize(result);
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new { error = ex.Message });
        }
    }
}

public class WebScriptGlobals
{
    public string? JsonResult { get; private set; }
    public string Args { get; set; } = "";

    public object Json(object obj)
    {
        JsonResult = JsonSerializer.Serialize(obj);
        return obj;
    }
}

/// <summary>
/// Global object visible to tool scripts. Provides window lifecycle, memory diagnostics,
/// timing, and assertions. Window operations dispatch to the main thread automatically.
/// </summary>
public class ToolScriptGlobals
{
    private readonly string _scriptName;

    public ToolScriptGlobals(string scriptName) => _scriptName = scriptName;

    // === Window Lifecycle (dispatches to main thread) ===

    public void SpawnWindow(string type) => RunOnMain(() => InvokeWindowManager("SpawnWindow", type));

    public void CloseWindow(string id) => RunOnMain(() => InvokeWindowManager("CloseWindow", id));

    public void CloseAllWindows(string? exceptType = "ribbon")
    {
        RunOnMain(() =>
        {
            var wm = GetWindowManager();
            var getAllMethod = wm.GetType().GetMethod("GetAllWindows")!;
            var windows = ((System.Collections.IEnumerable)getAllMethod.Invoke(wm, null)!).Cast<object>().ToList();
            var ids = new List<string>();
            foreach (var w in windows)
            {
                var wtype = (string)w.GetType().GetProperty("WindowType")!.GetValue(w)!;
                if (exceptType == null || wtype != exceptType)
                    ids.Add((string)w.GetType().GetProperty("Id")!.GetValue(w)!);
            }
            var closeMethod = wm.GetType().GetMethod("CloseWindow")!;
            foreach (var id in ids) closeMethod.Invoke(wm, new object[] { id });
        });
    }

    public List<(string Id, string Type)> GetWindows()
    {
        List<(string, string)>? result = null;
        RunOnMain(() =>
        {
            var wm = GetWindowManager();
            var windows = ((System.Collections.IEnumerable)wm.GetType().GetMethod("GetAllWindows")!.Invoke(wm, null)!).Cast<object>();
            result = windows.Select(w => (
                (string)w.GetType().GetProperty("Id")!.GetValue(w)!,
                (string)w.GetType().GetProperty("WindowType")!.GetValue(w)!
            )).ToList();
        });
        return result!;
    }

    public int WindowCount => GetWindows().Count;

    // === Memory Diagnostics (thread-safe) ===

    [System.Runtime.InteropServices.DllImport("/usr/lib/libSystem.B.dylib", EntryPoint = "task_info")]
    static extern int task_info(int target_task, uint flavor, ref TaskVmInfoTool info, ref int count);

    [System.Runtime.InteropServices.DllImport("/usr/lib/libSystem.B.dylib", EntryPoint = "mach_task_self")]
    static extern int mach_task_self();

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    struct TaskVmInfoTool
    {
        public long virtual_size; public int region_count, page_size;
        public long resident_size, resident_size_peak;
        public long device, device_peak, @internal, internal_peak;
        public long external, external_peak, reusable, reusable_peak;
        public long purgeable_volatile_pmap, purgeable_volatile_resident, purgeable_volatile_virtual;
        public long compressed, compressed_peak, compressed_lifetime;
        public long phys_footprint;
    }

    public long FootprintMB
    {
        get
        {
            var info = new TaskVmInfoTool();
            int count = System.Runtime.InteropServices.Marshal.SizeOf<TaskVmInfoTool>() / sizeof(int);
            return task_info(mach_task_self(), 22, ref info, ref count) == 0
                ? info.phys_footprint / (1024 * 1024) : -1;
        }
    }

    public long GCHeapMB => GC.GetTotalMemory(false) / (1024 * 1024);
    public (int gen0, int gen1, int gen2) GCCounts =>
        (GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2));

    // === Timing & GC ===

    public void Wait(int ms) => Thread.Sleep(ms);
    public void WaitSeconds(double seconds) => Thread.Sleep((int)(seconds * 1000));

    /// <summary>Force full GC + finalization. Use after closing windows to reclaim IOSurfaces.</summary>
    public static void ForceGC()
    {
        GC.Collect(2, GCCollectionMode.Aggressive, true, true);
        GC.WaitForPendingFinalizers();
        GC.Collect(2, GCCollectionMode.Aggressive, true, true);
    }

    // === Logging ===

    public void Log(string msg) => Console.WriteLine($"[ToolScript:{_scriptName}] {msg}");

    // === Assertions ===

    public void Assert(bool condition, string message)
    {
        if (!condition) throw new ToolScriptAssertionException(message);
    }

    public void AssertMemoryBelow(long mb, string context)
    {
        var current = FootprintMB;
        if (current > mb)
            throw new ToolScriptAssertionException($"{context}: footprint {current}mb exceeds {mb}mb limit");
    }

    // === Internals (reflection bridge to Keystone.Core.Runtime) ===

    static object? _cachedRuntime;
    static MethodInfo? _runOnMainMethod;

    static object GetRuntime()
    {
        if (_cachedRuntime != null) return _cachedRuntime;
        var runtimeType = AppDomain.CurrentDomain.GetAssemblies()
            .SelectMany(a => { try { return a.GetTypes(); } catch { return Type.EmptyTypes; } })
            .FirstOrDefault(t => t.Name == "ApplicationRuntime");
        _cachedRuntime = runtimeType?.GetProperty("Instance", BindingFlags.Public | BindingFlags.Static)?.GetValue(null);
        _runOnMainMethod = runtimeType?.GetMethod("RunOnMainThreadAndWait");
        return _cachedRuntime ?? throw new InvalidOperationException("ApplicationRuntime not available");
    }

    static object GetWindowManager()
    {
        var runtime = GetRuntime();
        return runtime.GetType().GetProperty("WindowManager")!.GetValue(runtime)!;
    }

    static void RunOnMain(Action action)
    {
        var runtime = GetRuntime();
        _runOnMainMethod!.Invoke(runtime, new object[] { action });
    }

    static void InvokeWindowManager(string method, params object[] args)
    {
        var wm = GetWindowManager();
        wm.GetType().GetMethod(method)!.Invoke(wm, args);
    }
}

public class ToolScriptAssertionException : Exception
{
    public ToolScriptAssertionException(string message) : base(message) { }
}
