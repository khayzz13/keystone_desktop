// BunWorker — Per-worker lifecycle manager for additional Bun subprocesses.
// Mirrors BunManager patterns (pre-ready queue, callback routing, crash recovery)
// but stripped of web component concerns. Each worker runs worker-host.ts with
// its own services directory and optional WebSocket server.

using System.Collections.Concurrent;
using System.Text.Json;

namespace Keystone.Core.Management.Bun;

public class BunWorker : IDisposable
{
    public string Name { get; }
    public BunWorkerConfig Config { get; }
    public bool IsRunning => _ready && (_process?.IsRunning ?? false);
    public int? Port => _port > 0 ? _port : null;
    public IReadOnlyList<string> Services => _services;

    private BunProcess? _process;
    private volatile bool _ready;
    private int _nextId;
    private int _port;
    private List<string> _services = new();

    private readonly ConcurrentQueue<string> _pendingQueue = new();
    private readonly ConcurrentDictionary<int, Action<string>> _callbacks = new();

    // Crash recovery
    private int _restartCount;
    private bool _restartScheduled;
    private string? _workerHostPath;
    private string? _appRoot;
    private volatile bool _shutdownRequested;

    // Events
    public Action<string, string>? OnServicePush;
    public Action<int>? OnCrash;
    public Action<int>? OnRestart;

    public BunWorker(BunWorkerConfig config)
    {
        Name = config.Name;
        Config = config;
    }

    public bool Start(string workerHostPath, string appRoot)
    {
        _workerHostPath = workerHostPath;
        _appRoot = appRoot;
        _shutdownRequested = false;

        var process = new BunProcess();
        var env = new Dictionary<string, string>
        {
            ["KEYSTONE_WORKER_NAME"] = Config.Name,
            ["KEYSTONE_SERVICES_DIR"] = Config.ServicesDir,
            ["KEYSTONE_BROWSER_ACCESS"] = Config.BrowserAccess.ToString().ToLower(),
            ["KEYSTONE_APP_ROOT"] = appRoot,
        };

        if (Config.IsExtensionHost)
        {
            env["KEYSTONE_EXTENSION_HOST"] = "true";
            if (Config.AllowedChannels is { Count: > 0 })
                env["KEYSTONE_ALLOWED_CHANNELS"] = string.Join(",", Config.AllowedChannels);
        }

        // Wire ready signal detection
        process.OnLine = line =>
        {
            if (!_ready && TryAttachFromReadySignal(process, line))
                return;
        };

        process.OnExit = exitCode =>
        {
            if (_shutdownRequested) return;
            Console.WriteLine($"[BunWorker:{Name}] Process exited (code={exitCode})");
            Detach();
            OnCrash?.Invoke(exitCode);
            ScheduleRestart();
        };

        if (!process.Start(workerHostPath, appRoot, env: env))
        {
            Console.WriteLine($"[BunWorker:{Name}] Failed to start");
            return false;
        }

        _process = process;
        Console.WriteLine($"[BunWorker:{Name}] Started");
        return true;
    }

    private bool TryAttachFromReadySignal(BunProcess process, string jsonLine)
    {
        try
        {
            using var doc = JsonDocument.Parse(jsonLine);
            var root = doc.RootElement;
            if (!root.TryGetProperty("status", out var status) || status.GetString() != "ready")
                return false;

            _services = root.TryGetProperty("services", out var sv)
                ? sv.EnumerateArray().Select(e => e.GetString()!).ToList()
                : new List<string>();
            _port = root.TryGetProperty("port", out var p) ? p.GetInt32() : 0;

            // Switch to dispatch mode
            process.OnLine = OnStdoutLine;
            _ready = true;

            // Flush queued messages
            while (_pendingQueue.TryDequeue(out var queued))
                process.Send(queued);

            Console.WriteLine($"[BunWorker:{Name}] Ready: {_services.Count} services, port={_port}");
            return true;
        }
        catch { return false; }
    }

    private void Detach()
    {
        _ready = false;
        _process = null;
        _services = new();
        _port = 0;
        foreach (var cb in _callbacks.Values)
            try { cb("{}"); } catch { }
        _callbacks.Clear();
        while (_pendingQueue.TryDequeue(out _)) { }
    }

    private void ScheduleRestart()
    {
        if (_restartScheduled || _shutdownRequested) return;
        _restartScheduled = true;

        var attempt = ++_restartCount;
        if (attempt > Config.MaxRestarts)
        {
            Console.WriteLine($"[BunWorker:{Name}] Restart limit ({Config.MaxRestarts}) reached");
            _restartScheduled = false;
            return;
        }

        var delayMs = (int)Math.Min(Config.BaseBackoffMs * Math.Pow(2, attempt - 1), 30_000);
        Console.WriteLine($"[BunWorker:{Name}] Restarting in {delayMs}ms (attempt {attempt}/{Config.MaxRestarts})");

        Task.Run(async () =>
        {
            await Task.Delay(delayMs);
            _restartScheduled = false;
            _process?.Dispose();

            if (Start(_workerHostPath!, _appRoot!))
            {
                _restartCount = 0;
                OnRestart?.Invoke(attempt);
            }
            else
            {
                ScheduleRestart();
            }
        });
    }

    // ── Send ──────────────────────────────────────────────────────────

    public void Send(string jsonLine)
    {
        if (_ready && _process != null)
            _process.Send(jsonLine);
        else
            _pendingQueue.Enqueue(jsonLine);
    }

    public void Send(int id, string jsonLine, Action<string> onResponse)
    {
        _callbacks[id] = onResponse;
        Send(jsonLine);
    }

    public int NextId() => Interlocked.Increment(ref _nextId);

    public Task<string?> Query(string service, object? args = null)
    {
        var tcs = new TaskCompletionSource<string?>();
        var id = NextId();
        Send(id, JsonSerializer.Serialize(new { id, type = "query", service, args }), json =>
        {
            using var doc = JsonDocument.Parse(json);
            var result = doc.RootElement.TryGetProperty("result", out var r) ? r.GetRawText() : null;
            tcs.TrySetResult(result);
        });
        return tcs.Task;
    }

    public void Push(string channel, object data)
    {
        if (!_ready) return;
        _process?.Send(JsonSerializer.Serialize(new { id = 0, type = "push", channel, data }));
    }

    public void Stop()
    {
        _shutdownRequested = true;
        _process?.Stop();
        Detach();
    }

    public void Dispose() => Stop();

    // ── Response dispatch ─────────────────────────────────────────────

    private void OnStdoutLine(string line)
    {
        try
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;

            if (root.TryGetProperty("type", out var typeProp))
            {
                var typeStr = typeProp.GetString();

                if (typeStr == "service_push")
                {
                    var channel = root.GetProperty("channel").GetString()!;
                    var data = root.GetProperty("data").GetRawText();
                    OnServicePush?.Invoke(channel, data);
                    return;
                }

                if (typeStr == "relay")
                {
                    var target = root.GetProperty("target").GetString()!;
                    var channel = root.GetProperty("channel").GetString()!;
                    var data = root.GetProperty("data").GetRawText();
                    BunWorkerManager.Instance.Route(Name, target, channel, data);
                    return;
                }
            }

            if (root.TryGetProperty("error", out _))
            {
                var id = root.TryGetProperty("id", out var eid) ? eid.GetInt32() : 0;
                if (_callbacks.TryRemove(id, out var errCb))
                    errCb(line);
                return;
            }

            if (root.TryGetProperty("id", out var idProp))
            {
                var id = idProp.GetInt32();
                if (_callbacks.TryRemove(id, out var cb))
                    cb(line);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[BunWorker:{Name}] Parse error: {ex.Message}");
        }
    }
}
