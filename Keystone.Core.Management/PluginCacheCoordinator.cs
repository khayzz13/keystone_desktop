// PluginCacheCoordinator - Events for cache invalidation when plugins reload

using System;

namespace Keystone.Core.Management;

public enum PluginType { Window, Service, Logic, Library, Core, Indicator }

public class PluginCacheCoordinator
{
    public static PluginCacheCoordinator Instance { get; } = new();

    public event Action<string>? OnWindowPluginReloaded;
    public event Action<string>? OnServicePluginReloaded;
    public event Action<string>? OnLogicPluginReloaded;
    public event Action<string>? OnLibraryPluginReloaded;
    public event Action<string>? OnCorePluginReloaded;
    public event Action<string>? OnIndicatorPluginReloaded;

    public event Action<string>? OnWindowPluginUnloading;
    public event Action<string>? OnServicePluginUnloading;

    public void NotifyReload(PluginType type, string name)
    {
        switch (type)
        {
            case PluginType.Window:
                OnWindowPluginReloaded?.Invoke(name);
                break;
            case PluginType.Service:
                OnServicePluginReloaded?.Invoke(name);
                break;
            case PluginType.Logic:
                LogicRegistry.ClearCache();
                OnLogicPluginReloaded?.Invoke(name);
                break;
            case PluginType.Library:
                OnLibraryPluginReloaded?.Invoke(name);
                break;
            case PluginType.Core:
                OnCorePluginReloaded?.Invoke(name);
                break;
            case PluginType.Indicator:
                OnIndicatorPluginReloaded?.Invoke(name);
                break;
        }
    }

    public void NotifyUnloading(PluginType type, string name)
    {
        switch (type)
        {
            case PluginType.Window:
                OnWindowPluginUnloading?.Invoke(name);
                break;
            case PluginType.Service:
                OnServicePluginUnloading?.Invoke(name);
                break;
        }
    }
}
