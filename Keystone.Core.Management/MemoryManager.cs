// MemoryManager - C# owned memory tracking + GC stats API

using System;
using System.Collections.Generic;

namespace Keystone.Core.Management;

public class MemoryManager
{
    private readonly Dictionary<string, (string Category, ulong Bytes, DateTime LastAccess)> _tracked = new();
    private readonly Dictionary<string, ulong> _categoryBudgets = new();
    private readonly object _lock = new();

    public ulong TotalBudgetBytes { get; }

    public MemoryManager(ulong totalBudgetMb = 750)
    {
        TotalBudgetBytes = totalBudgetMb * 1024 * 1024;
    }

    /// <summary>Register a per-category memory budget in MB.</summary>
    public void SetCategoryBudget(string category, ulong budgetMb)
        => _categoryBudgets[category] = budgetMb * 1024 * 1024;

    // === Tracking API ===

    public void Track(string name, string category, ulong bytes)
    {
        lock (_lock) { _tracked[name] = (category, bytes, DateTime.UtcNow); }
    }

    public void Untrack(string name)
    {
        lock (_lock) { _tracked.Remove(name); }
    }

    public void Touch(string name)
    {
        lock (_lock)
        {
            if (_tracked.TryGetValue(name, out var entry))
                _tracked[name] = (entry.Category, entry.Bytes, DateTime.UtcNow);
        }
    }

    public ulong GetTracked(string category)
    {
        lock (_lock)
        {
            ulong total = 0;
            foreach (var (_, (cat, bytes, _)) in _tracked)
                if (cat == category) total += bytes;
            return total;
        }
    }

    public ulong GetTotalTracked()
    {
        lock (_lock)
        {
            ulong total = 0;
            foreach (var (_, (_, bytes, _)) in _tracked) total += bytes;
            return total;
        }
    }

    // === Eviction API ===

    public bool CheckPressure() => GetTotalTracked() > TotalBudgetBytes;

    public bool CheckCategoryPressure(string category)
    {
        var budget = _categoryBudgets.TryGetValue(category, out var b) ? b : TotalBudgetBytes;
        return GetTracked(category) > budget;
    }

    public List<string> GetEvictionCandidates(string? category = null)
    {
        lock (_lock)
        {
            var sorted = new List<(string Name, DateTime LastAccess)>();
            foreach (var (name, (cat, _, lastAccess)) in _tracked)
            {
                if (category == null || cat == category)
                    sorted.Add((name, lastAccess));
            }
            sorted.Sort((a, b) => a.LastAccess.CompareTo(b.LastAccess));
            var result = new List<string>(sorted.Count);
            foreach (var (name, _) in sorted) result.Add(name);
            return result;
        }
    }

    // === C# GC Stats API ===
    // This needs a more comprehensive breakdown, also it is slightly broken and/or not actually accurate to the amount of MB when 
    // viewing it in activity monitor. perhaps this is a scope issue and it isn't tracking background processes? 

    public static long ManagedHeapBytes => GC.GetTotalMemory(false);
    public static long ManagedHeapMB => GC.GetTotalMemory(false) / (1024 * 1024);

    public static GCMemoryInfo GetGCInfo() => GC.GetGCMemoryInfo();

    public static (long Gen0, long Gen1, long Gen2) GetCollectionCounts() =>
        (GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2));

    public static void ForceCollect(int generation = 2, GCCollectionMode mode = GCCollectionMode.Default)
    {
        GC.Collect(generation, mode);
        GC.WaitForPendingFinalizers();
    }

    public static long GetAllocatedBytesForCurrentThread() => GC.GetAllocatedBytesForCurrentThread();
}
