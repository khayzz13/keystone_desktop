/*---------------------------------------------------------------------------------------------
 *  Copyright (c) 2026 Kaedyn Limon. All rights reserved.
 *  Licensed under the MIT License. See LICENSE in the project root for license information.
 *--------------------------------------------------------------------------------------------*/

// ManagedProcessBridge — NDJSON protocol layer on top of ManagedProcess.
// Absorbs the duplicated patterns from BunManager and BunWorker:
// pre-ready message queue, request-id correlation, response dispatch.

using System.Collections.Concurrent;
using System.Text.Json;

namespace Keystone.Core.Management.Process;

public class ManagedProcessBridge : IDisposable
{
    public ManagedProcess Process { get; }
    public bool IsReady => _ready;

    private volatile bool _ready;
    private int _nextId;

    private readonly ConcurrentQueue<string> _pendingQueue = new();
    private readonly ConcurrentDictionary<int, Action<string>> _callbacks = new();

    /// <summary>Called when a service pushes data. Args: (channel, dataJson).</summary>
    public Action<string, string>? OnServicePush { get; set; }

    /// <summary>Called when a web component dispatches an action. Arg: action string.</summary>
    public Action<string>? OnWebAction { get; set; }

    /// <summary>Called on relay message from a worker. Args: (target, channel, dataJson).</summary>
    public Action<string, string, string>? OnRelay { get; set; }

    /// <summary>Called on HMR notification. Arg: component name.</summary>
    public Action<string>? OnHmr { get; set; }

    /// <summary>Called when a Bun service queries the C# host. Args: (service, argsJson). Returns result object or null.</summary>
    public Func<string, string?, Task<object?>>? OnHostQuery { get; set; }

    public ManagedProcessBridge(ManagedProcess process)
    {
        Process = process;
        process.OnLine = OnStdoutLine;
    }

    /// <summary>Mark the bridge as ready and flush any queued messages.</summary>
    public void MarkReady()
    {
        _ready = true;
        while (_pendingQueue.TryDequeue(out var queued))
            Process.Send(queued);
    }

    /// <summary>Allocate a unique request id.</summary>
    public int NextId() => Interlocked.Increment(ref _nextId);

    /// <summary>Send raw NDJSON. Queued if not ready.</summary>
    public void Send(string jsonLine)
    {
        if (_ready)
            Process.Send(jsonLine);
        else
            _pendingQueue.Enqueue(jsonLine);
    }

    /// <summary>Send with a one-shot response callback.</summary>
    public void Send(int id, string jsonLine, Action<string> onResponse)
    {
        _callbacks[id] = onResponse;
        Send(jsonLine);
    }

    /// <summary>Query a service. Returns result JSON or null. Times out after timeoutMs (default 10s).</summary>
    public Task<string?> Query(string service, object? args = null, int timeoutMs = 10_000)
    {
        var tcs = new TaskCompletionSource<string?>();
        var id = NextId();
        Send(id, JsonSerializer.Serialize(new { id, type = "query", service, args }), json =>
        {
            using var doc = JsonDocument.Parse(json);
            var result = doc.RootElement.TryGetProperty("result", out var r) ? r.GetRawText() : null;
            tcs.TrySetResult(result);
        });

        if (timeoutMs > 0)
        {
            var capturedId = id;
            _ = Task.Delay(timeoutMs).ContinueWith(t =>
            {
                if (_callbacks.TryRemove(capturedId, out _))
                    tcs.TrySetException(new TimeoutException($"Bun query '{service}' timed out after {timeoutMs}ms"));
            }, TaskContinuationOptions.ExecuteSynchronously);
        }

        return tcs.Task;
    }

    /// <summary>Push data to a named WebSocket channel.</summary>
    public void Push(string channel, object data)
    {
        if (!_ready) return;
        Process.Send(JsonSerializer.Serialize(new { id = 0, type = "push", channel, data }));
    }

    /// <summary>Fire-and-forget action dispatch.</summary>
    public void HandleAction(string action)
    {
        if (!_ready) return;
        Process.Send(JsonSerializer.Serialize(new { id = 0, type = "action", action }));
    }

    /// <summary>Eval JS in the subprocess. Times out after timeoutMs (default 10s).</summary>
    public Task<string?> Eval(string code, int timeoutMs = 10_000)
    {
        var tcs = new TaskCompletionSource<string?>();
        var id = NextId();
        Send(id, JsonSerializer.Serialize(new { id, type = "eval", code }), json =>
        {
            using var doc = JsonDocument.Parse(json);
            var result = doc.RootElement.TryGetProperty("result", out var r) ? r.GetRawText() : null;
            tcs.TrySetResult(result);
        });

        if (timeoutMs > 0)
        {
            var capturedId = id;
            _ = Task.Delay(timeoutMs).ContinueWith(t =>
            {
                if (_callbacks.TryRemove(capturedId, out _))
                    tcs.TrySetException(new TimeoutException($"Bun eval timed out after {timeoutMs}ms"));
            }, TaskContinuationOptions.ExecuteSynchronously);
        }

        return tcs.Task;
    }

    /// <summary>Detach: clear state, unblock pending waiters.</summary>
    public void Detach()
    {
        _ready = false;
        foreach (var cb in _callbacks.Values)
            try { cb("{}"); } catch { }
        _callbacks.Clear();
        while (_pendingQueue.TryDequeue(out _)) { }
    }

    public void Dispose() => Detach();

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

                if (typeStr == "action_from_web")
                {
                    var action = root.GetProperty("action").GetString()!;
                    OnWebAction?.Invoke(action);
                    return;
                }

                if (typeStr == "__hmr__")
                {
                    var component = root.TryGetProperty("component", out var c) ? c.GetString() : null;
                    if (component != null) OnHmr?.Invoke(component);
                    return;
                }

                if (typeStr == "relay")
                {
                    var target = root.GetProperty("target").GetString()!;
                    var channel = root.GetProperty("channel").GetString()!;
                    var data = root.GetProperty("data").GetRawText();
                    OnRelay?.Invoke(target, channel, data);
                    return;
                }

                if (typeStr == "query_host")
                {
                    var id = root.GetProperty("id").GetInt32();
                    var service = root.GetProperty("service").GetString()!;
                    var args = root.TryGetProperty("args", out var a) ? a.GetRawText() : null;
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            if (OnHostQuery == null)
                            {
                                Send(JsonSerializer.Serialize(new { id, error = new { code = "handler_not_found", message = $"No host query handler registered" } }));
                                return;
                            }
                            var result = await OnHostQuery(service, args);
                            Send(JsonSerializer.Serialize(new { id, result }));
                        }
                        catch (Exception ex)
                        {
                            Send(JsonSerializer.Serialize(new { id, error = new { code = "handler_error", message = ex.Message } }));
                        }
                    });
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
            Console.WriteLine($"[{Process.Name}] Parse error: {ex.Message}");
        }
    }
}
