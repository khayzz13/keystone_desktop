/*---------------------------------------------------------------------------------------------
 *  Copyright (c) 2026 Kaedyn Limon. All rights reserved.
 *  Licensed under the MIT License. See LICENSE in the project root for license information.
 *--------------------------------------------------------------------------------------------*/

using System.Collections.Concurrent;
using System.Text.Json;

namespace Keystone.Core.Runtime;

/// <summary>
/// Routes invoke messages from WKScriptMessageHandler to registered handlers.
/// Owns handler registries and the dispatch pipeline.
/// </summary>
internal sealed class WindowInvokeRouter : IDisposable
{
    readonly string _windowId;
    readonly Action<string, object> _push;
    readonly Action<string>? _directEval;
    readonly Action<string>? _onAction;
    readonly Dictionary<string, Func<JsonElement, Task<object?>>> _invokeHandlers = new();
    readonly Dictionary<string, Func<JsonElement, CancellationToken, Task<object?>>> _cancellableInvokeHandlers = new();
    readonly Dictionary<string, Func<JsonElement, object?>> _mainThreadInvokeHandlers = new();
    readonly ConcurrentDictionary<int, CancellationTokenSource> _inflightInvokes = new();

    public Action<string>? OnDirectMessage { get; set; }

    public WindowInvokeRouter(string windowId, Action<string, object> pushChannel,
        Action<string>? directEval = null, Action<string>? onAction = null)
    {
        _windowId = windowId;
        _push = pushChannel;
        _directEval = directEval;
        _onAction = onAction;
    }

    public void RegisterHandler(string channel, Func<JsonElement, Task<object?>> handler)
        => _invokeHandlers[channel] = handler;

    public void RegisterHandler(string channel, Func<JsonElement, CancellationToken, Task<object?>> handler)
        => _cancellableInvokeHandlers[channel] = handler;

    public void RegisterMainThreadHandler(string channel, Func<JsonElement, object?> handler)
        => _mainThreadInvokeHandlers[channel] = handler;

    /// <summary>
    /// Send an invoke reply to the browser. Prefers direct EvaluateJavaScript injection
    /// (no Bun round-trip); falls back to Bun WS relay via _push.
    /// </summary>
    void Reply(string ctrlChannel, int id, object? result = null, object? error = null)
    {
        var payload = error != null
            ? new { type = "__invoke_reply__", id, error }
            : (object)new { type = "__invoke_reply__", id, result };

        if (_directEval != null)
        {
            var json = JsonSerializer.Serialize(payload);
            _directEval($"window.__ks_dr__?.({json})");
        }
        else
        {
            _push(ctrlChannel, payload);
        }
    }

    /// <summary>
    /// Entry point for all WKScriptMessageHandler messages from JS.
    /// If the message has ks_invoke:true, dispatches to the registered handler and replies.
    /// Otherwise falls through to OnDirectMessage.
    /// </summary>
    public void Dispatch(string msg)
    {
        JsonDocument? doc = null;
        try
        {
            doc = JsonDocument.Parse(msg);
            var root = doc.RootElement;

            // Context menu interception
            if (root.TryGetProperty("type", out var msgType) && msgType.GetString() == "__contextmenu__")
            {
                _push($"window:{_windowId}:contextmenu", new {
                    linkUrl = root.TryGetProperty("linkUrl", out var l) ? l.GetString() : null,
                    imageUrl = root.TryGetProperty("imageUrl", out var img) ? img.GetString() : null,
                    selectedText = root.TryGetProperty("selectedText", out var sel) ? sel.GetString() : null,
                    isEditable = root.TryGetProperty("isEditable", out var ed) && ed.GetBoolean(),
                    x = root.GetProperty("x").GetDouble(),
                    y = root.GetProperty("y").GetDouble(),
                });
                return;
            }

            // Direct action from browser (bypasses WS)
            if (root.TryGetProperty("ks_action", out var ksAction) && ksAction.GetBoolean())
            {
                var action = root.GetProperty("action").GetString() ?? "";
                _onAction?.Invoke(action);
                return;
            }

            // Cancellation from browser
            if (root.TryGetProperty("ks_cancel", out var ksCancel) && ksCancel.GetBoolean())
            {
                var cancelId = root.GetProperty("id").GetInt32();
                if (_inflightInvokes.TryRemove(cancelId, out var cts))
                    cts.Cancel();
                return;
            }

            if (root.TryGetProperty("ks_invoke", out var ksInvoke) && ksInvoke.GetBoolean())
            {
                var id = root.GetProperty("id").GetInt32();
                var channel = root.GetProperty("channel").GetString() ?? "";
                var windowId = root.TryGetProperty("windowId", out var wid) ? wid.GetString() ?? _windowId : _windowId;
                var args = root.TryGetProperty("args", out var a) ? a : default;

                var ctrlChannel = $"window:{windowId}:__ctrl__";

                // Sync main-thread handlers — execute inline, no Task.Run.
                if (_mainThreadInvokeHandlers.TryGetValue(channel, out var syncHandler))
                {
                    try
                    {
                        var result = syncHandler(args);
                        Reply(ctrlChannel, id, result: result);
                    }
                    catch (Exception ex)
                    {
                        Reply(ctrlChannel, id, error: new { code = "handler_error", message = ex.Message });
                    }
                    return;
                }

                // Cancellable async handler
                if (_cancellableInvokeHandlers.TryGetValue(channel, out var cancellableHandler))
                {
                    var cts = new CancellationTokenSource();
                    _inflightInvokes[id] = cts;
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            var result = await cancellableHandler(args, cts.Token);
                            Reply(ctrlChannel, id, result: result);
                        }
                        catch (OperationCanceledException)
                        {
                            Reply(ctrlChannel, id, error: new { code = "cancelled", message = "Request cancelled" });
                        }
                        catch (Exception ex)
                        {
                            Reply(ctrlChannel, id, error: new { code = "handler_error", message = ex.Message });
                        }
                        finally
                        {
                            _inflightInvokes.TryRemove(id, out _);
                            cts.Dispose();
                        }
                    });
                    return;
                }

                // Standard async handler
                if (_invokeHandlers.TryGetValue(channel, out var handler))
                {
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            var result = await handler(args);
                            Reply(ctrlChannel, id, result: result);
                        }
                        catch (Exception ex)
                        {
                            Reply(ctrlChannel, id, error: new { code = "handler_error", message = ex.Message });
                        }
                    });
                }
                else
                {
                    Reply(ctrlChannel, id, error: new { code = "handler_not_found", message = $"No handler: {channel}" });
                }
                return;
            }
        }
        catch (Exception ex) { Console.Error.WriteLine($"[WindowInvokeRouter:{_windowId}] Failed to parse direct message: {ex.Message}"); }
        finally { doc?.Dispose(); }

        OnDirectMessage?.Invoke(msg);
    }

    public void Dispose()
    {
        foreach (var kvp in _inflightInvokes)
            if (_inflightInvokes.TryRemove(kvp.Key, out var cts))
                { cts.Cancel(); cts.Dispose(); }
        OnDirectMessage = null;
    }
}
