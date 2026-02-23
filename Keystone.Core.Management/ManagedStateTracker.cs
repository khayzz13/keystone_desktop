using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace Keystone.Core.Management;

/// <summary>
/// Comprehensive tracker for C#-managed state objects
/// Tracks GC memory, object lifetimes, and memory patterns
/// </summary>
public class ManagedStateTracker
{
    private readonly Dictionary<string, StateEntry> _states = new();
    private long _totalManagedBytes;

    public long TotalManagedBytes => _totalManagedBytes;

    public void TrackState(string name, string category, object state, long estimatedBytes)
    {
        var entry = new StateEntry
        {
            Name = name,
            Category = category,
            State = new WeakReference(state),
            EstimatedBytes = estimatedBytes,
            CreatedAt = DateTime.UtcNow,
            LastAccessed = DateTime.UtcNow
        };

        _states[name] = entry;
        _totalManagedBytes += estimatedBytes;
    }

    public void TouchState(string name)
    {
        if (_states.TryGetValue(name, out var entry))
        {
            entry.LastAccessed = DateTime.UtcNow;
            entry.AccessCount++;
        }
    }

    public T? GetState<T>(string name) where T : class
    {
        if (_states.TryGetValue(name, out var entry))
        {
            entry.LastAccessed = DateTime.UtcNow;
            entry.AccessCount++;
            return entry.State.Target as T;
        }
        return null;
    }

    public void RemoveState(string name)
    {
        if (_states.TryGetValue(name, out var entry))
        {
            _totalManagedBytes -= entry.EstimatedBytes;
            _states.Remove(name);
        }
    }

    public List<string> GetDeadStates()
    {
        var dead = new List<string>();
        foreach (var (name, entry) in _states)
        {
            if (!entry.State.IsAlive)
            {
                dead.Add(name);
                _totalManagedBytes -= entry.EstimatedBytes;
            }
        }

        foreach (var name in dead)
            _states.Remove(name);

        return dead;
    }

    public List<string> GetLRUCandidates(int count)
    {
        return _states
            .OrderBy(kvp => kvp.Value.LastAccessed)
            .Take(count)
            .Select(kvp => kvp.Key)
            .ToList();
    }

    public StateStats GetStats()
    {
        var liveCount = _states.Count(kvp => kvp.Value.State.IsAlive);
        var deadCount = _states.Count - liveCount;

        return new StateStats
        {
            TotalStates = _states.Count,
            LiveStates = liveCount,
            DeadStates = deadCount,
            TotalEstimatedBytes = _totalManagedBytes,
            GCTotalMemory = GC.GetTotalMemory(false)
        };
    }

    public Dictionary<string, long> GetCategoryBreakdown()
    {
        var breakdown = new Dictionary<string, long>();
        foreach (var entry in _states.Values)
        {
            if (!breakdown.ContainsKey(entry.Category))
                breakdown[entry.Category] = 0;
            breakdown[entry.Category] += entry.EstimatedBytes;
        }
        return breakdown;
    }

    private class StateEntry
    {
        public string Name { get; init; } = string.Empty;
        public string Category { get; init; } = string.Empty;
        public WeakReference State { get; init; } = null!;
        public long EstimatedBytes { get; init; }
        public DateTime CreatedAt { get; init; }
        public DateTime LastAccessed { get; set; }
        public int AccessCount { get; set; }
    }
}

public struct StateStats
{
    public int TotalStates { get; init; }
    public int LiveStates { get; init; }
    public int DeadStates { get; init; }
    public long TotalEstimatedBytes { get; init; }
    public long GCTotalMemory { get; init; }
}
