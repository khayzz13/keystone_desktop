// DyLibLoader - Hot-reload plugin DLLs with per-plugin collectible AssemblyLoadContexts

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Loader;
using System.Threading;
using Keystone.Core;
using Keystone.Core.Platform;
using Keystone.Core.Plugins;

namespace Keystone.Core.Management;

public class DyLibLoader
{
    public static DyLibLoader? Instance { get; private set; }

    private readonly string _dir;
    private readonly PluginRegistry _registry;
    private readonly Dictionary<string, PluginAssemblyInfo> _loaded = new();
    private readonly PluginDependencyGraph _dependencyGraph = new();
    private readonly ConcurrentQueue<string> _pendingReloads = new();
    private readonly ConcurrentDictionary<string, DateTime> _lastReloadTime = new();
    private readonly object _lock = new();
    private readonly List<CustomPluginTypeHandler> _customHandlers = new();
    private FileSystemWatcher? _watcher;
    private bool _hotReloadEnabled;
    private const int DebounceMs = 200;

    // ALC leak detection — after unload, store weak ref + timestamp to verify collection
    private readonly List<(string name, WeakReference<AssemblyLoadContext> weakRef, DateTime unloadedAt)> _pendingCollections = new();

    private class CustomPluginTypeHandler
    {
        public Type InterfaceType;
        public Action<object> OnLoaded;
        public Action<string>? OnUnloaded;
        public Func<PluginAssemblyInfo, List<string>> GetNames;

        public CustomPluginTypeHandler(Type interfaceType, Action<object> onLoaded, Action<string>? onUnloaded)
        {
            InterfaceType = interfaceType;
            OnLoaded = onLoaded;
            OnUnloaded = onUnloaded;
            GetNames = info =>
            {
                if (!info.CustomNames.TryGetValue(interfaceType, out var list))
                {
                    list = new List<string>();
                    info.CustomNames[interfaceType] = list;
                }
                return list;
            };
        }
    }

    private class PluginAssemblyInfo
    {
        public CollectiblePluginContext? Context;
        public Assembly? Assembly; // For legacy mode
        public List<string> CoreNames = new();
        public List<string> WindowTypes = new();
        public List<string> ServiceNames = new();
        public List<string> LibraryNames = new();
        public List<string> LogicNames = new();
        public Dictionary<Type, List<string>> CustomNames = new();
        public DateTime LastModified;
    }

    // Legacy shared context for non-hot-reload mode
    private AssemblyLoadContext? _sharedContext;

    /// <summary>
    /// Core context passed to ICorePlugin.Initialize(). Set by ApplicationRuntime after construction.
    /// DyLibLoader lives in Management (can't reference Runtime), but ICoreContext lives in Core.Plugins.
    /// </summary>
    public ICoreContext? CoreContext { get; set; }

    public DyLibLoader(string dir, PluginRegistry registry)
    {
        _dir = dir;
        _registry = registry;
        Instance = this;
        _hotReloadEnabled = Environment.GetEnvironmentVariable("DISABLE_HOT_RELOAD") != "1";

        if (!_hotReloadEnabled)
        {
            _sharedContext = new AssemblyLoadContext("PluginContext", isCollectible: false);
            _sharedContext.Resolving += OnSharedContextResolving;
        }

        AppDomain.CurrentDomain.AssemblyResolve += OnAssemblyResolve;
        Console.WriteLine($"[DyLibLoader] Hot-reload: {(_hotReloadEnabled ? "ENABLED" : "DISABLED")}");
    }

    /// <summary>
    /// Register a custom plugin type for auto-discovery during Load().
    /// Apps call this in ICorePlugin.Initialize() to get the same hot-reload,
    /// dependency cascade, and lifecycle management as built-in plugin types.
    /// </summary>
    public void RegisterCustomPluginType<T>(
        Action<T> onLoaded,
        Action<string>? onUnloaded = null) where T : class
    {
        _customHandlers.Add(new CustomPluginTypeHandler(
            typeof(T),
            obj => onLoaded((T)obj),
            onUnloaded
        ));
    }

    private Assembly? OnSharedContextResolving(AssemblyLoadContext context, AssemblyName name)
    {
        if (name.Name?.StartsWith("Keystone.") == true)
        {
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                if (asm.GetName().Name == name.Name) return asm;
        }
        if (name.Name != null && _loaded.TryGetValue(name.Name, out var pa))
            return pa.Assembly;
        if (name.Name != null)
        {
            var path = Path.Combine(_dir, name.Name + ".dll");
            if (File.Exists(path))
            {
                using var fs = File.OpenRead(path);
                return _sharedContext!.LoadFromStream(fs);
            }
        }
        return null;
    }

    private Assembly? OnAssemblyResolve(object? sender, ResolveEventArgs args)
    {
        var requestedName = new AssemblyName(args.Name);
        if (requestedName.Name?.StartsWith("Keystone.") == true)
        {
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                if (asm.GetName().Name == requestedName.Name) return asm;
        }
        lock (_lock)
        {
            if (requestedName.Name != null && _loaded.TryGetValue(requestedName.Name, out var pa))
                return pa.Assembly;
        }
        return null;
    }

    public void LoadAll()
    {
        Console.WriteLine($"[DyLibLoader] Loading from: {_dir}");
        if (!Directory.Exists(_dir)) return;

        var dlls = Directory.GetFiles(_dir, "*.dll");
        Console.WriteLine($"[DyLibLoader] Found {dlls.Length} DLLs");

        foreach (var dll in dlls)
        {
            var name = Path.GetFileNameWithoutExtension(dll);
            if (name.StartsWith("Microsoft.") || name.StartsWith("System.") || name.StartsWith("Keystone.Core")) continue;
            Load(dll);
        }
    }

    [UnconditionalSuppressMessage("Trimming", "IL2026", Justification = "Plugin loading requires dynamic assembly loading")]
    [UnconditionalSuppressMessage("Trimming", "IL2072", Justification = "Plugin loading requires dynamic type instantiation")]
    public void Load(string path)
    {
        var name = Path.GetFileNameWithoutExtension(path);

        lock (_lock)
        {
            // Unload old version first
            if (_loaded.TryGetValue(name, out var oldInfo))
                UnloadInternal(name, oldInfo);

            try
            {
                Assembly asm;
                CollectiblePluginContext? context = null;

                if (_hotReloadEnabled)
                {
                    context = new CollectiblePluginContext($"Plugin_{name}", path);
                    asm = context.Load();
                }
                else
                {
                    using var fs = File.OpenRead(path);
                    asm = _sharedContext!.LoadFromStream(fs);
                }

                // Set up native library resolver — checks plugin dir, then framework search paths
                NativeLibrary.SetDllImportResolver(asm, (libraryName, assembly, searchPath) =>
                {
                    // Plugin's own native libs (Go dylibs, etc.)
                    var libPath = Path.Combine(_dir, libraryName + ".dylib");
                    if (File.Exists(libPath)) return NativeLibrary.Load(libPath);

                    // Fallback to framework native lib search paths (exe dir, etc.)
                    return NativeLibraryLoader.Resolve(libraryName);
                });

                var info = new PluginAssemblyInfo
                {
                    Context = context,
                    Assembly = asm,
                    LastModified = File.GetLastWriteTimeUtc(path)
                };

                // Detect dependencies for cascade reload
                if (_hotReloadEnabled)
                    DetectDependencies(asm, name);

                // Register all plugin types
                foreach (var type in asm.GetTypes())
                {
                    if (typeof(ICorePlugin).IsAssignableFrom(type) && !type.IsAbstract)
                    {
                        var plugin = (ICorePlugin)Activator.CreateInstance(type)!;
                        if (CoreContext != null)
                            plugin.Initialize(CoreContext);
                        _registry.RegisterCore(plugin);
                        info.CoreNames.Add(plugin.CoreName);
                        Console.WriteLine($"[DyLibLoader] Registered core: {plugin.CoreName}");
                    }
                    else if (typeof(IWindowPlugin).IsAssignableFrom(type) && !type.IsAbstract)
                    {
                        var plugin = (IWindowPlugin)Activator.CreateInstance(type)!;
                        _registry.RegisterWindow(plugin);
                        info.WindowTypes.Add(plugin.WindowType);
                        PluginCacheCoordinator.Instance.NotifyReload(PluginType.Window, plugin.WindowType);
                        Console.WriteLine($"[DyLibLoader] Registered window: {plugin.WindowType}");
                    }
                    else if (typeof(IServicePlugin).IsAssignableFrom(type) && !type.IsAbstract)
                    {
                        var plugin = (IServicePlugin)Activator.CreateInstance(type)!;
                        _registry.RegisterService(plugin);
                        info.ServiceNames.Add(plugin.ServiceName);
                        PluginCacheCoordinator.Instance.NotifyReload(PluginType.Service, plugin.ServiceName);
                        Console.WriteLine($"[DyLibLoader] Registered service: {plugin.ServiceName}");
                    }
                    else if (typeof(ILibraryPlugin).IsAssignableFrom(type) && !type.IsAbstract)
                    {
                        var plugin = (ILibraryPlugin)Activator.CreateInstance(type)!;
                        _registry.RegisterLibrary(plugin);
                        info.LibraryNames.Add(plugin.LibraryName);
                        PluginCacheCoordinator.Instance.NotifyReload(PluginType.Library, plugin.LibraryName);
                        Console.WriteLine($"[DyLibLoader] Registered library: {plugin.LibraryName}");
                    }
                    else if (typeof(ILogicPlugin).IsAssignableFrom(type) && !type.IsAbstract)
                    {
                        var plugin = (ILogicPlugin)Activator.CreateInstance(type)!;
                        plugin.Initialize();
                        LogicRegistry.Register(plugin.LogicName, type, plugin);
                        info.LogicNames.Add(plugin.LogicName);
                        PluginCacheCoordinator.Instance.NotifyReload(PluginType.Logic, plugin.LogicName);
                        Console.WriteLine($"[DyLibLoader] Registered logic: {plugin.LogicName}");
                    }

                    // Custom plugin types registered by apps
                    foreach (var handler in _customHandlers)
                    {
                        if (handler.InterfaceType.IsAssignableFrom(type) && !type.IsAbstract)
                        {
                            var instance = Activator.CreateInstance(type)!;
                            handler.OnLoaded(instance);
                            var names = handler.GetNames(info);
                            names.Add(type.Name);
                            Console.WriteLine($"[DyLibLoader] Registered custom {handler.InterfaceType.Name}: {type.Name}");
                        }
                    }
                }

                _loaded[name] = info;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[DyLibLoader] Failed to load {name}: {ex.Message}");
                Notifications.Error($"Plugin load failed: {name} — {ex.Message}");
            }
        }
    }

    private void DetectDependencies(Assembly asm, string pluginName)
    {
        _dependencyGraph.ClearPlugin(pluginName);
        foreach (var asmRef in asm.GetReferencedAssemblies())
        {
            var refName = asmRef.Name;
            if (refName == null || refName.StartsWith("System.") || refName.StartsWith("Microsoft.") || refName.StartsWith("Keystone.Core"))
                continue;
            // check if dll exists in plugin dir (not if loaded yet)
            if (File.Exists(Path.Combine(_dir, refName + ".dll")))
            {
                if (!_dependencyGraph.WouldCreateCycle(pluginName, refName))
                {
                    _dependencyGraph.RegisterDependency(pluginName, refName);
                    Console.WriteLine($"[DyLibLoader] Dependency: {pluginName} -> {refName}");
                }
            }
        }
    }

    public void Unload(string name)
    {
        lock (_lock)
        {
            if (_loaded.TryGetValue(name, out var info))
                UnloadInternal(name, info);
        }
    }

    private void UnloadInternal(string name, PluginAssemblyInfo info)
    {
        foreach (var wt in info.WindowTypes)
        {
            PluginCacheCoordinator.Instance.NotifyUnloading(PluginType.Window, wt);
            _registry.UnregisterWindow(wt);
        }
        foreach (var sn in info.ServiceNames)
        {
            PluginCacheCoordinator.Instance.NotifyUnloading(PluginType.Service, sn);
            _registry.UnregisterService(sn);
        }
        foreach (var ln in info.LogicNames)
            LogicRegistry.Unregister(ln);
        // Fire custom plugin type unload callbacks
        foreach (var handler in _customHandlers)
        {
            if (handler.OnUnloaded != null && info.CustomNames.TryGetValue(handler.InterfaceType, out var names))
            {
                foreach (var n in names)
                    handler.OnUnloaded(n);
            }
        }
        // Clean up services from this assembly via ServiceLocator
        if (info.Assembly != null)
            Keystone.Core.ServiceLocator.UnregisterAll(info.Assembly);
        _loaded.Remove(name);

        if (_hotReloadEnabled && info.Context != null)
        {
            var weakRef = new WeakReference<AssemblyLoadContext>(info.Context.Context);
            info.Context.Dispose();
            for (int i = 0; i < 3; i++) { GC.Collect(); GC.WaitForPendingFinalizers(); }

            // Track for leak detection — checked in ProcessPendingReloads
            _pendingCollections.Add((name, weakRef, DateTime.UtcNow));
        }
    }

    public void StartWatching()
    {
        if (!Directory.Exists(_dir)) return;

        _watcher = new FileSystemWatcher(_dir, "*.dll")
        {
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName,
            EnableRaisingEvents = true
        };

        _watcher.Changed += OnFileChanged;
        _watcher.Created += OnFileChanged;
        _watcher.Deleted += (s, e) => Unload(Path.GetFileNameWithoutExtension(e.Name ?? ""));

        Console.WriteLine($"[DyLibLoader] Watching for changes in: {_dir}");
    }

    private void OnFileChanged(object sender, FileSystemEventArgs e)
    {
        // Queue for main thread processing to avoid thread pool issues
        _pendingReloads.Enqueue(e.FullPath);
    }

    /// <summary>
    /// Process pending reloads - call from main thread/render loop
    /// </summary>
    public void ProcessPendingReloads()
    {
        while (_pendingReloads.TryDequeue(out var path))
        {
            // Timestamp-based debounce — FSW fires duplicate events for a single write.
            // Drop events within 200ms of the last accepted event for the same path.
            var now = DateTime.UtcNow;
            if (_lastReloadTime.TryGetValue(path, out var last) && (now - last).TotalMilliseconds < DebounceMs)
                continue;
            _lastReloadTime[path] = now;

            var name = Path.GetFileNameWithoutExtension(path);
            lock (_lock)
            {
                if (_loaded.TryGetValue(name, out var info))
                {
                    var newMod = File.GetLastWriteTimeUtc(path);
                    if (newMod > info.LastModified)
                    {
                        Console.WriteLine($"[DyLibLoader] Reloading: {name}");

                        // Cascade reload if this is a library, logic, or indicator plugin
                        var needsCascade = _hotReloadEnabled &&
                            (info.LibraryNames.Count > 0 || info.LogicNames.Count > 0);

                        if (needsCascade)
                        {
                            var cascade = _dependencyGraph.GetCascadeReloadOrder(name);
                            if (cascade.Any())
                            {
                                Console.WriteLine($"[DyLibLoader] Cascade reload: {string.Join(", ", cascade)}");
                                // Unload in reverse order
                                foreach (var dep in cascade.Reverse())
                                    Unload(dep);
                            }
                            // Reload source plugin first, then dependents
                            Load(path);
                            foreach (var dep in cascade)
                            {
                                var depPath = Path.Combine(_dir, $"{dep}.dll");
                                if (File.Exists(depPath)) Load(depPath);
                            }
                        }
                        else
                        {
                            Load(path);
                        }
                    }
                }
                else
                {
                    // New plugin
                    Load(path);
                }
            }
        }

        // ALC leak detection — check if unloaded contexts were actually collected
        CheckPendingCollections();
    }

    private void CheckPendingCollections()
    {
        if (_pendingCollections.Count == 0) return;

        var now = DateTime.UtcNow;
        for (int i = _pendingCollections.Count - 1; i >= 0; i--)
        {
            var (name, weakRef, unloadedAt) = _pendingCollections[i];
            var elapsed = (now - unloadedAt).TotalSeconds;

            if (!weakRef.TryGetTarget(out _))
            {
                // Collected successfully
                _pendingCollections.RemoveAt(i);
            }
            else if (elapsed > 10)
            {
                Console.WriteLine($"[DyLibLoader] WARNING: ALC for \"{name}\" not collected after {elapsed:F0}s — suspected pinning");
                Notifications.Warn($"Plugin \"{name}\" ALC not collected after {elapsed:F0}s");
                _pendingCollections.RemoveAt(i);
            }
        }
    }

    public void StopWatching()
    {
        _watcher?.Dispose();
        _watcher = null;
    }
}
