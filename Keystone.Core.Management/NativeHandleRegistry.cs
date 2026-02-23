using System;
using System.Collections.Concurrent;

namespace Keystone.Core.Management;

/// <summary>
/// Pattern 1: Rust Creates, C# References
/// Registry for native handles with lifetime tracking
/// no longer used, should be removed if its not somehow a dependency for something else, 
/// since the current implementation is pretty much just a glorified dictionary,
///  and doesn't actually do anything useful at the moment.
/// </summary>
public class NativeHandleRegistry : IDisposable
{
    private readonly ConcurrentDictionary<IntPtr, HandleEntry> _handles = new();
    private bool _disposed;

    public void Register(IntPtr handle, string name, string category, Action<IntPtr>? destroyer = null)
    {
        if (handle == IntPtr.Zero)
            return;

        _handles[handle] = new HandleEntry
        {
            Name = name,
            Category = category,
            CreatedAt = DateTime.UtcNow,
            Destroyer = destroyer
        };
    }

    public void Unregister(IntPtr handle)
    {
        if (_handles.TryRemove(handle, out var entry))
        {
            entry.Destroyer?.Invoke(handle);
        }
    }

    public bool IsRegistered(IntPtr handle) => _handles.ContainsKey(handle);

    public string? GetName(IntPtr handle)
    {
        return _handles.TryGetValue(handle, out var entry) ? entry.Name : null;
    }

    public int GetHandleCount(string? category = null)
    {
        if (category == null)
            return _handles.Count;

        return _handles.Values.Count(e => e.Category == category);
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        foreach (var (handle, entry) in _handles)
        {
            entry.Destroyer?.Invoke(handle);
        }

        _handles.Clear();
        _disposed = true;
    }

    private class HandleEntry
    {
        public string Name { get; init; } = string.Empty;
        public string Category { get; init; } = string.Empty;
        public DateTime CreatedAt { get; init; }
        public Action<IntPtr>? Destroyer { get; init; }
    }
}
