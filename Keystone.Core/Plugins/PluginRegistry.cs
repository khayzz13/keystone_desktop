// PluginRegistry - Manages window and service plugins with hot-reload support

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace Keystone.Core.Plugins;

/// <summary>
/// Plugin metadata for UI display
/// </summary>
public record PluginInfo(
    string Name,
    string Category,
    string? AssemblyPath,
    bool IsLoaded,
    bool HotReloadEnabled,
    DateTime? LastReload,
    string? ErrorMessage
);

/// <summary>
/// Interface for plugin registry access (used by PluginsWindow)
/// </summary>
public interface IPluginRegistry
{
    IReadOnlyList<PluginInfo> GetAllPlugins();
    PluginInfo? GetPlugin(string name);
    void ReloadPlugin(string name);
    IReadOnlyList<string> GetDependencies(string pluginName);
    IReadOnlyList<string> GetDependents(string pluginName);
}

public class PluginRegistry : IPluginRegistry
{
    // Static instance for PluginsWindow access
    private static PluginRegistry? _instance;
    public static PluginRegistry? Instance => _instance;

    private readonly Dictionary<string, ICorePlugin> _corePlugins = new();
    private readonly Dictionary<string, IWindowPlugin> _windowPlugins = new();
    private readonly Dictionary<string, IServicePlugin> _servicePlugins = new();
    private readonly Dictionary<string, ILibraryPlugin> _libraryPlugins = new();
    private readonly Dictionary<string, Thread> _serviceThreads = new();
    private readonly object _lock = new();

    public PluginRegistry() => _instance = this;

    // Events for hot-reload coordination
    public event Action<string>? WindowUnloading;
    public event Action<string, IWindowPlugin>? WindowReloaded;
    public event Action<string>? ServiceUnloading;
    public event Action<string, IServicePlugin>? ServiceReloaded;

    // Thread-safe: return copies to prevent iteration exceptions during hot-reload
    public IEnumerable<string> RegisteredCoreNames { get { lock (_lock) return _corePlugins.Keys.ToList(); } }
    public IEnumerable<string> RegisteredWindowTypes { get { lock (_lock) return _windowPlugins.Keys.ToList(); } }
    public IEnumerable<string> RegisteredServiceNames { get { lock (_lock) return _servicePlugins.Keys.ToList(); } }
    public IEnumerable<string> RegisteredLibraryNames { get { lock (_lock) return _libraryPlugins.Keys.ToList(); } }

    /// <summary>
    /// Return all currently registered plugin instances implementing T.
    /// Snapshot is thread-safe and safe to enumerate outside the lock.
    /// </summary>
    public IReadOnlyList<T> GetPluginsImplementing<T>() where T : class
    {
        lock (_lock)
        {
            return _corePlugins.Values.Cast<object>()
                .Concat(_windowPlugins.Values)
                .Concat(_servicePlugins.Values)
                .Concat(_libraryPlugins.Values)
                .OfType<T>()
                .ToList();
        }
    }

    public void RegisterCore(ICorePlugin plugin)
    {
        lock (_lock)
        {
            _corePlugins[plugin.CoreName] = plugin;
            plugin.Initialize();
        }
    }

    public ICorePlugin? GetCore(string coreName)
    {
        lock (_lock) { return _corePlugins.TryGetValue(coreName, out var p) ? p : null; }
    }

    public void RegisterLibrary(ILibraryPlugin plugin)
    {
        lock (_lock)
        {
            _libraryPlugins[plugin.LibraryName] = plugin;
            plugin.Initialize();
        }
    }

    public ILibraryPlugin? GetLibrary(string libraryName)
    {
        lock (_lock) { return _libraryPlugins.TryGetValue(libraryName, out var p) ? p : null; }
    }

    public T? GetLibrary<T>(string libraryName) where T : class, ILibraryPlugin
    {
        return GetLibrary(libraryName) as T;
    }

    public void RegisterWindow(IWindowPlugin plugin)
    {
        lock (_lock)
        {
            var windowType = plugin.WindowType;
            var isReload = _windowPlugins.ContainsKey(windowType);

            if (isReload)
                WindowUnloading?.Invoke(windowType);

            _windowPlugins[windowType] = plugin;

            if (isReload)
                WindowReloaded?.Invoke(windowType, plugin);
        }
    }

    public void UnregisterWindow(string windowType)
    {
        lock (_lock)
        {
            WindowUnloading?.Invoke(windowType);
            _windowPlugins.Remove(windowType);
        }
    }

    public IWindowPlugin? GetWindow(string windowType)
    {
        lock (_lock) { return _windowPlugins.TryGetValue(windowType, out var p) ? p : null; }
    }

    public void RegisterService(IServicePlugin plugin)
    {
        lock (_lock)
        {
            var serviceName = plugin.ServiceName;
            var isReload = _servicePlugins.ContainsKey(serviceName);

            if (isReload)
            {
                // Hot-reload path: shutdown old, preserve state if supported
                ServiceUnloading?.Invoke(serviceName);
                var oldPlugin = _servicePlugins[serviceName];
                byte[]? state = null;

                if (oldPlugin is IReloadableService reloadable)
                    state = reloadable.SerializeState();

                oldPlugin.Shutdown();
                if (_serviceThreads.TryGetValue(serviceName, out var oldThread))
                {
                    var timeout = (oldPlugin as IReloadableService)?.ShutdownTimeout ?? TimeSpan.FromSeconds(5);
                    oldThread.Join(timeout);
                    _serviceThreads.Remove(serviceName);
                }

                // Restore state to new plugin
                if (plugin is IReloadableService newReloadable && state != null)
                    newReloadable.RestoreState(state);
            }

            _servicePlugins[serviceName] = plugin;

            if (plugin.RunOnBackgroundThread)
            {
                var thread = new Thread(() => plugin.Initialize()) { IsBackground = true, Name = serviceName };
                _serviceThreads[serviceName] = thread;
                thread.Start();
            }
            else
            {
                plugin.Initialize();
            }

            if (isReload)
                ServiceReloaded?.Invoke(serviceName, plugin);
        }
    }

    public void UnregisterService(string serviceName)
    {
        lock (_lock)
        {
            if (_servicePlugins.TryGetValue(serviceName, out var plugin))
            {
                ServiceUnloading?.Invoke(serviceName);
                plugin.Shutdown();
                if (_serviceThreads.TryGetValue(serviceName, out var thread))
                {
                    var timeout = (plugin as IReloadableService)?.ShutdownTimeout ?? TimeSpan.FromSeconds(5);
                    thread.Join(timeout);
                    _serviceThreads.Remove(serviceName);
                }
                _servicePlugins.Remove(serviceName);
            }
        }
    }

    public IServicePlugin? GetService(string serviceName)
    {
        lock (_lock) { return _servicePlugins.TryGetValue(serviceName, out var p) ? p : null; }
    }

    public T? GetService<T>(string serviceName) where T : class, IServicePlugin
    {
        return GetService(serviceName) as T;
    }

    // === IPluginRegistry Implementation ===

    private readonly Dictionary<string, DateTime> _lastReloadTimes = new();
    private readonly Dictionary<string, List<string>> _dependencies = new();

    public IReadOnlyList<PluginInfo> GetAllPlugins()
    {
        lock (_lock)
        {
            var plugins = new List<PluginInfo>();

            foreach (var (name, _) in _windowPlugins)
                plugins.Add(new PluginInfo(name, "Window", null, true, true,
                    _lastReloadTimes.GetValueOrDefault(name), null));

            foreach (var (name, _) in _servicePlugins)
                plugins.Add(new PluginInfo(name, "Service", null, true, true,
                    _lastReloadTimes.GetValueOrDefault(name), null));

            foreach (var (name, _) in _corePlugins)
                plugins.Add(new PluginInfo(name, "Core", null, true, false,
                    _lastReloadTimes.GetValueOrDefault(name), null));

            return plugins;
        }
    }

    public PluginInfo? GetPlugin(string name)
    {
        lock (_lock)
        {
            if (_windowPlugins.ContainsKey(name))
                return new PluginInfo(name, "Window", null, true, true, _lastReloadTimes.GetValueOrDefault(name), null);
            if (_servicePlugins.ContainsKey(name))
                return new PluginInfo(name, "Service", null, true, true, _lastReloadTimes.GetValueOrDefault(name), null);
            if (_corePlugins.ContainsKey(name))
                return new PluginInfo(name, "Core", null, true, false, _lastReloadTimes.GetValueOrDefault(name), null);
            return null;
        }
    }

    public void ReloadPlugin(string name)
    {
        // Trigger reload event - actual reload handled by DyLibLoader
        lock (_lock)
        {
            if (_windowPlugins.ContainsKey(name))
                WindowUnloading?.Invoke(name);
            else if (_servicePlugins.ContainsKey(name))
                ServiceUnloading?.Invoke(name);
            _lastReloadTimes[name] = DateTime.Now;
        }
    }

    public void SetDependencies(string pluginName, List<string> deps)
    {
        lock (_lock) { _dependencies[pluginName] = deps; }
    }

    public IReadOnlyList<string> GetDependencies(string pluginName)
    {
        lock (_lock) { return _dependencies.GetValueOrDefault(pluginName) ?? new List<string>(); }
    }

    public IReadOnlyList<string> GetDependents(string pluginName)
    {
        lock (_lock)
        {
            return _dependencies
                .Where(kv => kv.Value.Contains(pluginName))
                .Select(kv => kv.Key)
                .ToList();
        }
    }
}
