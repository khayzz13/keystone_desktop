// LogicRegistry - Indirect dispatch for static logic plugin calls, enabling hot-reload
// Stores plugin instances for metadata (RenderOrder, RequiresGpu, Dependencies)

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Keystone.Core.Plugins;

namespace Keystone.Core.Management;

public static class LogicRegistry
{
    private static readonly Dictionary<string, Type> _logicTypes = new();
    private static readonly Dictionary<string, ILogicPlugin> _instances = new();
    private static readonly ConcurrentDictionary<(string, string), MethodInfo?> _methodCache = new();
    private static readonly ConcurrentDictionary<(string, string), Delegate?> _delegateCache = new();
    private static readonly object _lock = new();
    private static IReadOnlyList<string>? _cachedRenderOrder;

    public static void Register(string name, Type type, ILogicPlugin? instance = null)
    {
        lock (_lock)
        {
            _logicTypes[name] = type;
            if (instance != null) _instances[name] = instance;
            foreach (var key in _methodCache.Keys.Where(k => k.Item1 == name).ToList())
                _methodCache.TryRemove(key, out _);
            foreach (var key in _delegateCache.Keys.Where(k => k.Item1 == name).ToList())
                _delegateCache.TryRemove(key, out _);
            _cachedRenderOrder = null;
        }
        Console.WriteLine($"[LogicRegistry] Registered: {name}");
    }

    public static void Unregister(string name)
    {
        lock (_lock)
        {
            if (_instances.TryGetValue(name, out var instance))
                (instance as IDisposable)?.Dispose();
            _logicTypes.Remove(name);
            _instances.Remove(name);
            foreach (var key in _methodCache.Keys.Where(k => k.Item1 == name).ToList())
                _methodCache.TryRemove(key, out _);
            foreach (var key in _delegateCache.Keys.Where(k => k.Item1 == name).ToList())
                _delegateCache.TryRemove(key, out _);
            _cachedRenderOrder = null;
        }
    }

    public static void ClearCache()
    {
        _methodCache.Clear();
        _delegateCache.Clear();
    }

    // === Plugin metadata queries ===

    public static int GetRenderOrder(string logicName)
    {
        lock (_lock) { return _instances.TryGetValue(logicName, out var p) ? p.RenderOrder : 0; }
    }

    public static bool GetRequiresGpu(string logicName)
    {
        lock (_lock) { return _instances.TryGetValue(logicName, out var p) && p.RequiresGpu; }
    }

    public static IEnumerable<string> GetDependencies(string logicName)
    {
        lock (_lock) { return _instances.TryGetValue(logicName, out var p) ? p.Dependencies : Array.Empty<string>(); }
    }

    public static IReadOnlyList<string> GetRegisteredNames()
    {
        lock (_lock) { return _logicTypes.Keys.ToList(); }
    }

    /// <summary>Get all registered logic names sorted by RenderOrder.</summary>
    public static IReadOnlyList<string> GetRenderOrder()
    {
        var cached = _cachedRenderOrder;
        if (cached != null) return cached;
        lock (_lock)
        {
            cached = _logicTypes.Keys
                .Where(n => _delegateCache.ContainsKey((n, "Render")) || HasStaticMethod(n, "Render"))
                .OrderBy(n => _instances.TryGetValue(n, out var p) ? p.RenderOrder : 0)
                .ToList();
            _cachedRenderOrder = cached;
            return cached;
        }
    }

    private static bool HasStaticMethod(string logicName, string methodName)
    {
        return _logicTypes.TryGetValue(logicName, out var type) &&
               type.GetMethod(methodName, BindingFlags.Public | BindingFlags.Static) != null;
    }

    // === Typed invoke — zero reflection, zero boxing on hot path ===

    public static T? GetOrCreateDelegate<T>(string logicName, string methodName) where T : Delegate
    {
        var key = (logicName, methodName);
        if (_delegateCache.TryGetValue(key, out var cached))
            return cached as T;

        lock (_lock)
        {
            if (!_logicTypes.TryGetValue(logicName, out var type))
            {
                _delegateCache[key] = null;
                return null;
            }
            var method = type.GetMethod(methodName, BindingFlags.Public | BindingFlags.Static);
            if (method == null) { _delegateCache[key] = null; return null; }
            var del = Delegate.CreateDelegate(typeof(T), method, throwOnBindFailure: false);
            _delegateCache[key] = del;
            return del as T;
        }
    }

    // === Typed dispatch — window-controlled, framework-provided ===

    /// <summary>Invoke a single named logic plugin via cached delegate. Returns false if not found.</summary>
    public static bool Dispatch<T>(string logicName, string methodName, Action<T> invoker) where T : Delegate
    {
        var del = GetOrCreateDelegate<T>(logicName, methodName);
        if (del == null) return false;
        invoker(del);
        return true;
    }

    /// <summary>Invoke all registered logic plugins with a matching method, sorted by RenderOrder.</summary>
    public static void DispatchAll<T>(string methodName, Action<T> invoker) where T : Delegate
    {
        foreach (var name in GetRenderOrder())
        {
            var del = GetOrCreateDelegate<T>(name, methodName);
            if (del != null) invoker(del);
        }
    }

    /// <summary>Invoke logic plugins within a RenderOrder range [minOrder, maxOrder].</summary>
    public static void DispatchRange<T>(string methodName, int minOrder, int maxOrder, Action<T> invoker) where T : Delegate
    {
        lock (_lock)
        {
            foreach (var name in _logicTypes.Keys)
            {
                var order = _instances.TryGetValue(name, out var p) ? p.RenderOrder : 0;
                if (order < minOrder || order > maxOrder) continue;
                var del = GetOrCreateDelegate<T>(name, methodName);
                if (del != null) invoker(del);
            }
        }
    }

    // === Fallback reflection invoke (non-hot-path, e.g. Invoke<T>) ===

    public static void Invoke(string logicName, string methodName, params object?[] args)
    {
        var method = GetMethod(logicName, methodName);
        method?.Invoke(null, args);
    }

    public static T? Invoke<T>(string logicName, string methodName, params object?[] args)
    {
        var method = GetMethod(logicName, methodName);
        var result = method?.Invoke(null, args);
        return result is T t ? t : default;
    }

    private static MethodInfo? GetMethod(string logicName, string methodName)
    {
        var key = (logicName, methodName);
        if (_methodCache.TryGetValue(key, out var method))
            return method;

        lock (_lock)
        {
            if (_logicTypes.TryGetValue(logicName, out var type))
                method = type.GetMethod(methodName, BindingFlags.Public | BindingFlags.Static);
            _methodCache[key] = method;
        }
        return method;
    }

    public static bool IsRegistered(string logicName)
    {
        lock (_lock) { return _logicTypes.ContainsKey(logicName); }
    }
}
