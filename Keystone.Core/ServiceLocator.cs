// ServiceLocator — typed service registry for cross-ALC service discovery
// Primary use: app assembly registers managers in ICorePlugin.Initialize(),
// plugins resolve them via ServiceLocator.Get<T>()

using System.Collections.Concurrent;
using System.Reflection;

namespace Keystone.Core;

public static class ServiceLocator
{
    private static readonly ConcurrentDictionary<Type, object> _services = new();

    public static void Register<T>(T service) where T : class
    {
        _services[typeof(T)] = service;
    }

    public static T? Get<T>() where T : class
    {
        return _services.TryGetValue(typeof(T), out var service) ? (T)service : null;
    }

    public static T GetRequired<T>() where T : class
    {
        return Get<T>() ?? throw new InvalidOperationException(
            $"Service {typeof(T).Name} not registered. Ensure it is registered in ICorePlugin.Initialize().");
    }

    public static void Unregister<T>() where T : class
    {
        _services.TryRemove(typeof(T), out _);
    }

    /// <summary>
    /// Remove all services whose implementation type came from the given assembly.
    /// Called by DyLibLoader during ALC unload to prevent pinned references.
    /// </summary>
    public static void UnregisterAll(Assembly assembly)
    {
        foreach (var kvp in _services)
        {
            if (kvp.Value.GetType().Assembly == assembly)
                _services.TryRemove(kvp.Key, out _);
        }
    }

    public static void Clear()
    {
        _services.Clear();
    }
}
