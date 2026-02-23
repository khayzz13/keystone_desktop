// BunManager — Async bridge for Bun subprocess communication
// Manages services (query/push), web component registration, and action dispatch.
// Process lifecycle owned by ApplicationRuntime.

using System.Collections.Concurrent;
using System.Linq;
using System.Text.Json;
using Keystone.Core.Plugins;

namespace Keystone.Core.Management.Bun;

public class BunManager : IBunService
{
    private static BunManager? _instance;
    public static BunManager Instance => _instance ??= new BunManager();

    private BunProcess? _process;
    private int _nextId;
    private volatile bool _ready;

    // Pre-ready message queue — flushed when Attach() marks ready
    private readonly ConcurrentQueue<string> _pendingQueue = new();

    // Module registries (populated from ready signal)
    private volatile List<string> _services = new();
    private volatile List<string> _webComponents = new();
    private int _bunPort;

    // Response routing: id → one-shot callback (invoked on reader thread)
    private readonly ConcurrentDictionary<int, Action<string>> _callbacks = new();

    /// <summary>Called when a service pushes data to C# via ctx.push(channel, data).</summary>
    public Action<string, string>? OnServicePush { get; set; }

    /// <summary>Called when a web component dispatches an action back to C#.</summary>
    public Action<string>? OnWebAction { get; set; }

    /// <summary>Fired when Bun hot-reloads a web component. Arg is the component name (e.g. "dashboard").</summary>
    public Action<string>? OnWebComponentHmr { get; set; }

    public bool IsRunning => _ready && (_process?.IsRunning ?? false);
    public IReadOnlyList<string> Services => _services;
    public IReadOnlyList<string> WebComponents => _webComponents;
    public int BunPort => _bunPort;

    /// <summary>Allocate a unique request id.</summary>
    public int NextId() => Interlocked.Increment(ref _nextId);

    // ── Attach / Detach ────────────────────────────────────────────────

    /// <summary>Parse a stdout line from BunProcess. If it's the ready signal,
    /// extract metadata and call Attach(). Returns true if the line was consumed.</summary>
    public bool TryAttachFromReadySignal(BunProcess process, string jsonLine)
    {
        try
        {
            using var doc = JsonDocument.Parse(jsonLine);
            var root = doc.RootElement;
            if (!root.TryGetProperty("status", out var status) || status.GetString() != "ready")
                return false;

            var services = root.TryGetProperty("services", out var sv)
                ? sv.EnumerateArray().Select(e => e.GetString()!).ToList()
                : new List<string>();
            var webComponents = root.TryGetProperty("webComponents", out var wc)
                ? wc.EnumerateArray().Select(e => e.GetString()!).ToList()
                : new List<string>();
            var port = root.TryGetProperty("port", out var p) ? p.GetInt32() : 0;

            Attach(process, services, webComponents, port);
            Console.WriteLine($"[BunManager] Attached: {services.Count} services, {webComponents.Count} web, port={port}");
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Wire a running BunProcess into the manager and mark ready.
    /// Flushes any messages queued before the process was available.</summary>
    public void Attach(BunProcess process, List<string> services,
        List<string>? webComponents = null, int port = 0)
    {
        _process = process;
        _services = services;
        _webComponents = webComponents ?? new();
        _bunPort = port;

        // Route all future stdout lines through our dispatcher
        _process.OnLine = OnStdoutLine;
        _ready = true;

        // Flush anything queued while starting up
        while (_pendingQueue.TryDequeue(out var queued))
            _process.Send(queued);
    }

    /// <summary>Detach the process (called on shutdown).</summary>
    public void Detach()
    {
        _ready = false;
        _process = null;
        _services = new();
        _webComponents = new();
        _bunPort = 0;
        // Unblock any pending TaskCompletionSource waiters
        foreach (var cb in _callbacks.Values)
            try { cb("{}"); } catch { }
        _callbacks.Clear();
        while (_pendingQueue.TryDequeue(out _)) { }
    }

    // ── Send ──────────────────────────────────────────────────────────────

    /// <summary>Send raw NDJSON to Bun stdin. Non-blocking.
    /// If Bun isn't ready yet, queued and sent when Attach() is called.</summary>
    public void Send(string jsonLine)
    {
        if (_ready && _process != null)
            _process.Send(jsonLine);
        else
            _pendingQueue.Enqueue(jsonLine);
    }

    /// <summary>Send a request and register a one-shot callback for the response.</summary>
    public void Send(int id, string jsonLine, Action<string> onResponse)
    {
        _callbacks[id] = onResponse;
        Send(jsonLine);
    }

    /// <summary>Fire-and-forget action to Bun (no response expected).</summary>
    public void HandleAction(string action)
    {
        if (!_ready) return;
        _process?.Send(JsonSerializer.Serialize(new { id = 0, type = "action", action }));
    }

    /// <summary>
    /// Push data to a named WebSocket channel. Bun broadcasts to all subscribers.
    /// Naming convention: "window:{windowId}:{topic}" to target a specific window's components.
    /// </summary>
    public void Push(string channel, object data)
    {
        if (!_ready) return;
        _process?.Send(JsonSerializer.Serialize(new { id = 0, type = "push", channel, data }));
    }

    /// <summary>Resolve a web component URL by name. Returns null if not a registered web component.</summary>
    public string? WebComponentUrl(string name)
    {
        if (_webComponents.Contains(name))
            return $"http://127.0.0.1:{_bunPort}/{name}";
        return null;
    }

    /// <summary>Query a Bun service. Returns the result JSON or null on error.</summary>
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

    /// <summary>Eval arbitrary JS in the Bun subprocess. Use sparingly — mainly for dev tooling.</summary>
    public Task<string?> Eval(string code)
    {
        var tcs = new TaskCompletionSource<string?>();
        var id = NextId();
        Send(id, JsonSerializer.Serialize(new { id, type = "eval", code }), json =>
        {
            using var doc = JsonDocument.Parse(json);
            var result = doc.RootElement.TryGetProperty("result", out var r) ? r.GetRawText() : null;
            tcs.TrySetResult(result);
        });
        return tcs.Task;
    }

    public void Shutdown() => Detach();

    // ── Response dispatch (runs on BunProcess reader thread) ─────────────

    private void OnStdoutLine(string line)
    {
        try
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;

            // Push-based messages (no request id — fire-and-forget from Bun)
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

                if (typeStr == "action_from_web")
                {
                    var action = root.GetProperty("action").GetString()!;
                    OnWebAction?.Invoke(action);
                    return;
                }

                if (typeStr == "__hmr__")
                {
                    var component = root.TryGetProperty("component", out var c) ? c.GetString() : null;
                    if (component != null)
                        OnWebComponentHmr?.Invoke(component);
                    return;
                }
            }

            if (root.TryGetProperty("error", out var err))
            {
                var id = root.TryGetProperty("id", out var eid) ? eid.GetInt32() : 0;
                Console.WriteLine($"[BunManager] Error (id={id}): {err.GetString()}");
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
            Console.WriteLine($"[BunManager] Parse error: {ex.Message}");
        }
    }
}
