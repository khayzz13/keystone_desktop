/*---------------------------------------------------------------------------------------------
 *  Copyright (c) 2026 Kaedyn Limon. All rights reserved.
 *  Licensed under the MIT License. See LICENSE in the project root for license information.
 *--------------------------------------------------------------------------------------------*/

// ThreadPoolManager — Named thread pools with lazy creation for service/plugin work consolidation.
// 50 services sharing 10 threads instead of each spinning up their own.

using System;
using System.Collections.Concurrent;
using System.Threading;
using Keystone.Core.Plugins;

namespace Keystone.Core.Management;

public class ThreadPoolManager : IThreadPoolManager, IDisposable
{
    readonly ConcurrentDictionary<string, ManagedThreadPool> _pools = new();
    readonly ConcurrentDictionary<string, int> _configs = new();
    const int DefaultMaxThreads = 2;
    bool _disposed;

    public void Configure(string name, int maxThreads)
        => _configs[name] = maxThreads;

    IManagedThreadPool IThreadPoolManager.Get(string name) => Get(name);

    public ManagedThreadPool Get(string name)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(ThreadPoolManager));
        return _pools.GetOrAdd(name, n =>
            new ManagedThreadPool(n, _configs.TryGetValue(n, out var max) ? max : DefaultMaxThreads));
    }

    public void QueueWork(string poolName, Action work)
        => Get(poolName).QueueWork(work);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (var pool in _pools.Values) pool.Dispose();
        _pools.Clear();
    }
}

public class ManagedThreadPool : IManagedThreadPool, IDisposable
{
    readonly string _name;
    readonly Thread[] _threads;
    readonly BlockingCollection<Action> _queue = new();

    public ManagedThreadPool(string name, int maxThreads)
    {
        _name = name;
        _threads = new Thread[maxThreads];
        for (int i = 0; i < maxThreads; i++)
        {
            _threads[i] = new Thread(Worker)
            {
                Name = $"Pool-{name}-{i}",
                IsBackground = true
            };
            _threads[i].Start();
        }
    }

    public void QueueWork(Action work) => _queue.Add(work);

    void Worker()
    {
        foreach (var work in _queue.GetConsumingEnumerable())
        {
            try { work(); }
            catch (Exception ex) { Console.WriteLine($"[ThreadPool:{_name}] {ex.Message}"); }
        }
    }

    public void Dispose()
    {
        _queue.CompleteAdding();
        foreach (var t in _threads) t.Join(TimeSpan.FromSeconds(5));
        _queue.Dispose();
    }
}
