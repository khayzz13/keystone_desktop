// PluginDependencyGraph - Tracks dependencies between plugins for cascade reloads

using System;
using System.Collections.Generic;
using System.Linq;

namespace Keystone.Core.Management;

public class PluginDependencyGraph
{
    private readonly Dictionary<string, HashSet<string>> _dependents = new();
    private readonly Dictionary<string, HashSet<string>> _dependencies = new();
    private readonly object _lock = new();

    public void RegisterDependency(string plugin, string library)
    {
        lock (_lock)
        {
            if (!_dependents.TryGetValue(library, out var deps))
                _dependents[library] = deps = new HashSet<string>();
            deps.Add(plugin);

            if (!_dependencies.TryGetValue(plugin, out var libs))
                _dependencies[plugin] = libs = new HashSet<string>();
            libs.Add(library);
        }
    }

    public void ClearPlugin(string plugin)
    {
        lock (_lock)
        {
            if (_dependencies.TryGetValue(plugin, out var libs))
            {
                foreach (var lib in libs)
                    if (_dependents.TryGetValue(lib, out var deps))
                        deps.Remove(plugin);
                _dependencies.Remove(plugin);
            }
        }
    }

    /// <summary>
    /// Get all plugins that need reloading when a library changes, in dependency order.
    /// </summary>
    public IReadOnlyList<string> GetCascadeReloadOrder(string changedLibrary)
    {
        lock (_lock)
        {
            var visited = new HashSet<string>();
            var result = new List<string>();

            void Visit(string lib)
            {
                if (!visited.Add(lib)) return;
                if (_dependents.TryGetValue(lib, out var deps))
                {
                    foreach (var dep in deps)
                    {
                        Visit(dep);
                        if (!result.Contains(dep))
                            result.Add(dep);
                    }
                }
            }

            Visit(changedLibrary);
            return result;
        }
    }

    /// <summary>
    /// Check if adding a dependency would create a cycle.
    /// </summary>
    public bool WouldCreateCycle(string plugin, string library)
    {
        lock (_lock)
        {
            var visited = new HashSet<string>();
            bool HasPath(string from, string to)
            {
                if (from == to) return true;
                if (!visited.Add(from)) return false;
                if (_dependencies.TryGetValue(from, out var deps))
                    foreach (var d in deps)
                        if (HasPath(d, to)) return true;
                return false;
            }
            return HasPath(library, plugin);
        }
    }
}
