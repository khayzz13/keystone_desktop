// BunWorkerManager — Registry and lifecycle coordinator for Bun worker processes.
// Workers communicate with C# via stdin/stdout NDJSON (same protocol as main Bun).
// Cross-worker routing goes through C#. Direct WebSocket connections are opt-in
// when the target worker has browserAccess enabled.

using System.Collections.Concurrent;
using System.Text.Json;
using Keystone.Core;
using Keystone.Core.Plugins;

namespace Keystone.Core.Management.Bun;

public class BunWorkerManager : IBunWorkerManager
{
    private static BunWorkerManager? _instance;
    public static BunWorkerManager Instance => _instance ??= new BunWorkerManager();

    private readonly ConcurrentDictionary<string, BunWorker> _workers = new();

    public BunWorker? this[string name] => _workers.GetValueOrDefault(name);
    public IReadOnlyDictionary<string, BunWorker> All => _workers;

    public BunWorker Spawn(BunWorkerConfig config, string workerHostPath, string appRoot)
    {
        if (_workers.TryGetValue(config.Name, out var existing))
        {
            existing.Dispose();
            _workers.TryRemove(config.Name, out _);
        }

        var worker = new BunWorker(config);
        _workers[config.Name] = worker;
        worker.Start(workerHostPath, appRoot);
        return worker;
    }

    public void Stop(string name)
    {
        if (_workers.TryRemove(name, out var worker))
            worker.Dispose();
    }

    public void StopAll()
    {
        foreach (var (name, worker) in _workers)
        {
            worker.Dispose();
            Console.WriteLine($"[BunWorkerManager] Stopped {name}");
        }
        _workers.Clear();
    }

    /// <summary>Route a relay message from one worker to another (or to main Bun).</summary>
    public void Route(string fromWorker, string target, string channel, string data)
    {
        if (target == "main")
        {
            // Forward to main Bun process
            BunManager.Instance.Send(JsonSerializer.Serialize(new
            {
                id = 0, type = "relay_in", channel, data = JsonSerializer.Deserialize<object>(data)
            }));
            return;
        }

        if (_workers.TryGetValue(target, out var worker))
        {
            worker.Send(JsonSerializer.Serialize(new
            {
                id = 0, type = "relay_in", channel, data = JsonSerializer.Deserialize<object>(data)
            }));
        }
        else
        {
            Console.WriteLine($"[BunWorkerManager] Relay target '{target}' not found (from {fromWorker})");
        }
    }

    /// <summary>Broadcast the port map of all workers with browserAccess to all workers + main Bun.
    /// Called after all autoStart workers are ready.</summary>
    public void BroadcastPorts()
    {
        var ports = _workers
            .Where(kv => kv.Value.Port > 0)
            .ToDictionary(kv => kv.Key, kv => kv.Value.Port!.Value);

        // Also include main Bun port
        if (BunManager.Instance.BunPort > 0)
            ports["main"] = BunManager.Instance.BunPort;

        if (ports.Count == 0) return;

        var msg = JsonSerializer.Serialize(new { type = "worker_ports", data = ports });

        foreach (var (_, worker) in _workers)
            worker.Send(msg);

        // Main Bun also gets the map so its services can connect to workers
        BunManager.Instance.Send(msg);

        Console.WriteLine($"[BunWorkerManager] Broadcast ports: {string.Join(", ", ports.Select(p => $"{p.Key}={p.Value}"))}");
    }
}
