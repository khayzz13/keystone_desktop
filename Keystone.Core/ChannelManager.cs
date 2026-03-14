/*---------------------------------------------------------------------------------------------
 *  Copyright (c) 2026 Kaedyn Limon. All rights reserved.
 *  Licensed under the MIT License. See LICENSE in the project root for license information.
 *--------------------------------------------------------------------------------------------*/

// ChannelManager — Unified communication API for C#-side data/event flow.
// Typed Value/Event channels, render-wake (DataChannel), alerts (Notifications).
// All subscriptions are assembly-tracked for automatic hot-reload cleanup.
//
// Access: ICoreContext.Channels or ChannelManager.Instance

using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.CompilerServices;
using Keystone.Core.Plugins;

namespace Keystone.Core;

// Non-generic base so ChannelManager can iterate all channels for hot-reload cleanup
interface IChannel
{
    void RemoveByAssembly(Assembly assembly);
}

/// <summary>
/// Typed value channel with retain semantics — last value is replayed to new subscribers.
/// Dispatch: SyncContext → Post(), no SyncContext → ThreadPool. Never inlines on setter's thread.
/// </summary>
public sealed class ValueChannel<T> : IChannel
{
    readonly Lock _lock = new();
    readonly List<Subscriber> _subscribers = new();
    T? _current;
    bool _hasValue;

    /// <summary>Current value (synchronous read). Default(T) if no value has been set.</summary>
    public T? Current { get { lock (_lock) return _current; } }

    /// <summary>Whether a value has been set at least once.</summary>
    public bool HasValue { get { lock (_lock) return _hasValue; } }

    /// <summary>Set the current value and dispatch to all subscribers.</summary>
    public void Set(T value)
    {
        Subscriber[] snapshot;
        lock (_lock)
        {
            _current = value;
            _hasValue = true;
            snapshot = _subscribers.Count > 0 ? _subscribers.ToArray() : [];
        }
        Dispatch(snapshot, value);
    }

    /// <summary>
    /// Subscribe to value changes. Callback is dispatched via the caller's SynchronizationContext
    /// (render thread, main thread) or ThreadPool if none. If a value exists, it is replayed
    /// asynchronously via Post — use .Current for synchronous reads.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public IDisposable Subscribe(Action<T> callback)
    {
        var sub = new Subscriber(callback, SynchronizationContext.Current, Assembly.GetCallingAssembly());
        bool replay;
        T? replayValue;
        lock (_lock)
        {
            _subscribers.Add(sub);
            replay = _hasValue;
            replayValue = _current;
        }
        // Replay via Post (async), never synchronous — uniform threading contract
        if (replay)
            Dispatch(sub, replayValue!);
        return new Subscription(() => { lock (_lock) _subscribers.Remove(sub); });
    }

    /// <summary>Subscribe from a known assembly context (for framework internal use).</summary>
    public IDisposable Subscribe(Action<T> callback, Assembly owner)
    {
        var sub = new Subscriber(callback, SynchronizationContext.Current, owner);
        bool replay;
        T? replayValue;
        lock (_lock)
        {
            _subscribers.Add(sub);
            replay = _hasValue;
            replayValue = _current;
        }
        if (replay)
            Dispatch(sub, replayValue!);
        return new Subscription(() => { lock (_lock) _subscribers.Remove(sub); });
    }

    public void RemoveByAssembly(Assembly assembly)
    {
        lock (_lock)
            _subscribers.RemoveAll(s => s.Owner == assembly);
    }

    static void Dispatch(Subscriber[] subscribers, T value)
    {
        foreach (var sub in subscribers)
            Dispatch(sub, value);
    }

    static void Dispatch(Subscriber sub, T value)
    {
        if (sub.Ctx is { } ctx)
            ctx.Post(_ => sub.Callback(value), null);
        else
            ThreadPool.UnsafeQueueUserWorkItem(static (state) => state.sub.Callback(state.value), (sub, value), preferLocal: false);
    }

    record struct Subscriber(Action<T> Callback, SynchronizationContext? Ctx, Assembly Owner);
}

/// <summary>
/// Typed event channel with fire-and-forget semantics — no value retention, no replay.
/// Dispatch: SyncContext → Post(), no SyncContext → ThreadPool.
/// </summary>
public sealed class EventChannel<T> : IChannel
{
    readonly Lock _lock = new();
    readonly List<Subscriber> _subscribers = new();

    /// <summary>Emit an event to all subscribers.</summary>
    public void Emit(T value)
    {
        Subscriber[] snapshot;
        lock (_lock)
            snapshot = _subscribers.Count > 0 ? _subscribers.ToArray() : [];
        foreach (var sub in snapshot)
        {
            if (sub.Ctx is { } ctx)
                ctx.Post(_ => sub.Callback(value), null);
            else
                ThreadPool.UnsafeQueueUserWorkItem(static (state) => state.sub.Callback(state.value), (sub, value), preferLocal: false);
        }
    }

    /// <summary>
    /// Subscribe to events. Callback is dispatched via the caller's SynchronizationContext
    /// or ThreadPool if none.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public IDisposable Subscribe(Action<T> callback)
    {
        var sub = new Subscriber(callback, SynchronizationContext.Current, Assembly.GetCallingAssembly());
        lock (_lock) _subscribers.Add(sub);
        return new Subscription(() => { lock (_lock) _subscribers.Remove(sub); });
    }

    /// <summary>Subscribe from a known assembly context (for framework internal use).</summary>
    public IDisposable Subscribe(Action<T> callback, Assembly owner)
    {
        var sub = new Subscriber(callback, SynchronizationContext.Current, owner);
        lock (_lock) _subscribers.Add(sub);
        return new Subscription(() => { lock (_lock) _subscribers.Remove(sub); });
    }

    public void RemoveByAssembly(Assembly assembly)
    {
        lock (_lock)
            _subscribers.RemoveAll(s => s.Owner == assembly);
    }

    record struct Subscriber(Action<T> Callback, SynchronizationContext? Ctx, Assembly Owner);
}

/// <summary>
/// Typed request/reply channel — exactly one handler, assembly-tracked for hot-reload.
/// Unlike Value/Event channels, this is point-to-point: a single registered handler
/// processes requests and returns responses. Multiple handlers are not supported.
/// </summary>
public sealed class CallChannel<TReq, TRes> : IChannel
{
    readonly Lock _lock = new();
    Func<TReq, Task<TRes>>? _handler;
    Assembly? _handlerOwner;

    /// <summary>Whether a handler is registered.</summary>
    public bool HasHandler { get { lock (_lock) return _handler != null; } }

    /// <summary>Register a handler for requests. Only one handler at a time — replaces any existing.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public void Handle(Func<TReq, Task<TRes>> handler)
    {
        lock (_lock) { _handler = handler; _handlerOwner = Assembly.GetCallingAssembly(); }
    }

    /// <summary>Register a synchronous handler (convenience).</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public void Handle(Func<TReq, TRes> handler)
    {
        lock (_lock) { _handler = req => Task.FromResult(handler(req)); _handlerOwner = Assembly.GetCallingAssembly(); }
    }

    /// <summary>Register a handler from a known assembly context (framework internal use).</summary>
    public void Handle(Func<TReq, Task<TRes>> handler, Assembly owner)
    {
        lock (_lock) { _handler = handler; _handlerOwner = owner; }
    }

    /// <summary>Send a request and await the response. Throws if no handler is registered.</summary>
    public Task<TRes> Request(TReq request)
    {
        Func<TReq, Task<TRes>>? handler;
        lock (_lock) handler = _handler;
        if (handler == null)
            throw new InvalidOperationException($"No handler registered for CallChannel<{typeof(TReq).Name}, {typeof(TRes).Name}>");
        return handler(request);
    }

    public void RemoveByAssembly(Assembly assembly)
    {
        lock (_lock)
        {
            if (_handlerOwner == assembly) { _handler = null; _handlerOwner = null; }
        }
    }
}

/// <summary>
/// Central channel manager implementing IChannelManager.
/// Typed channels are keyed with "v:" / "e:" prefix to prevent name collisions.
/// Render-wake delegates to DataChannel; alerts delegate storage to Notifications.
/// </summary>
public sealed class ChannelManager : IChannelManager
{
    public static readonly ChannelManager Instance = new();

    readonly ConcurrentDictionary<string, IChannel> _channels = new();
    readonly AlertChannelImpl _alert = new();

    // Render-wake subscriptions tracked for assembly cleanup
    readonly Lock _wakeLock = new();
    readonly List<WakeSub> _wakeSubs = new();

    public IAlertChannel Alert => _alert;

    // ── Typed channels ──────────────────────────────────────────

    public ValueChannel<T> Value<T>(string name)
    {
        var key = $"v:{name}";
        var channel = _channels.GetOrAdd(key, _ => new ValueChannel<T>());
        if (channel is not ValueChannel<T> typed)
            throw new InvalidOperationException(
                $"Channel '{name}' is {channel.GetType().Name}, cannot access as ValueChannel<{typeof(T).Name}>");
        return typed;
    }

    public EventChannel<T> Event<T>(string name)
    {
        var key = $"e:{name}";
        var channel = _channels.GetOrAdd(key, _ => new EventChannel<T>());
        if (channel is not EventChannel<T> typed)
            throw new InvalidOperationException(
                $"Channel '{name}' is {channel.GetType().Name}, cannot access as EventChannel<{typeof(T).Name}>");
        return typed;
    }

    public CallChannel<TReq, TRes> Call<TReq, TRes>(string name)
    {
        var key = $"c:{name}";
        var channel = _channels.GetOrAdd(key, _ => new CallChannel<TReq, TRes>());
        if (channel is not CallChannel<TReq, TRes> typed)
            throw new InvalidOperationException(
                $"Channel '{name}' is {channel.GetType().Name}, cannot access as CallChannel<{typeof(TReq).Name}, {typeof(TRes).Name}>");
        return typed;
    }

    // ── Render wake (delegates to DataChannel internally) ──────────────────

#pragma warning disable CS0618 // DataChannel is obsolete — ChannelManager is the canonical wrapper
    public void Notify(string channel) => DataChannel.Notify(channel);

    [MethodImpl(MethodImplOptions.NoInlining)]
    public IDisposable Subscribe(string channel, Action callback)
    {
        var assembly = Assembly.GetCallingAssembly();
        var sub = new WakeSub(channel, callback, assembly);
        lock (_wakeLock) _wakeSubs.Add(sub);
        DataChannel.Subscribe(channel, callback);
        return new Subscription(() =>
        {
            lock (_wakeLock) _wakeSubs.Remove(sub);
            DataChannel.Unsubscribe(callback);
        });
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    public IDisposable Subscribe(IEnumerable<string> channels, Action callback)
    {
        var assembly = Assembly.GetCallingAssembly();
        var channelList = channels.ToList();
        foreach (var ch in channelList)
        {
            var sub = new WakeSub(ch, callback, assembly);
            lock (_wakeLock) _wakeSubs.Add(sub);
        }
        DataChannel.Subscribe(channelList, callback);
        return new Subscription(() =>
        {
            lock (_wakeLock) _wakeSubs.RemoveAll(s => s.Callback == callback);
            DataChannel.Unsubscribe(callback);
        });
    }
#pragma warning restore CS0618

    // ── Hot-reload cleanup ──────────────────────────────────────

    public void UnsubscribeAll(Assembly assembly)
    {
        // Typed channels
        foreach (var channel in _channels.Values)
            channel.RemoveByAssembly(assembly);

        // Render-wake subscriptions
        WakeSub[] toRemove;
        lock (_wakeLock)
        {
            toRemove = _wakeSubs.Where(s => s.Owner == assembly).ToArray();
            _wakeSubs.RemoveAll(s => s.Owner == assembly);
        }
#pragma warning disable CS0618
        foreach (var sub in toRemove)
            DataChannel.Unsubscribe(sub.Callback);
#pragma warning restore CS0618

        // Alert subscribers
        _alert.RemoveByAssembly(assembly);
    }

    record struct WakeSub(string Channel, Action Callback, Assembly Owner);
}

/// <summary>
/// Alert channel implementation. Owns its own subscriber list for assembly tracking.
/// Delegates storage (Push/Recent/Dismiss/Clear) to the Notifications static class.
/// </summary>
sealed class AlertChannelImpl : IAlertChannel
{
    readonly Lock _lock = new();
    readonly List<AlertSub> _subscribers = new();

    // Prevents double-dispatch: when Push() fires Notifications.OnNotification,
    // the bridge handler skips because we already dispatched from Push() directly.
    [ThreadStatic] static bool _pushing;

    public IReadOnlyList<Notification> Recent => Notifications.Recent;

    public void Push(string message, NotificationLevel level = NotificationLevel.Info)
    {
        var notification = new Notification(message, level, DateTime.UtcNow);
        _pushing = true;
        Notifications.Push(message, level);
        _pushing = false;
        DispatchToSubscribers(notification);
    }

    public void Error(string message) => Push(message, NotificationLevel.Error);
    public void Warn(string message) => Push(message, NotificationLevel.Warning);
    public void Info(string message) => Push(message, NotificationLevel.Info);

    [MethodImpl(MethodImplOptions.NoInlining)]
    public IDisposable OnNotification(Action<Notification> callback)
    {
        var assembly = Assembly.GetCallingAssembly();
        // Bridge to old API — only fires for direct Notifications.Push() calls,
        // skipped when _pushing (our own Push() already dispatched).
        Action<Notification> handler = n => { if (!_pushing) callback(n); };
        var sub = new AlertSub(callback, assembly, handler);
        lock (_lock) _subscribers.Add(sub);
        Notifications.OnNotification += handler;
        return new Subscription(() =>
        {
            lock (_lock) _subscribers.Remove(sub);
            Notifications.OnNotification -= handler;
        });
    }

    public void Dismiss(Notification notification) => Notifications.Dismiss(notification);
    public void Clear() => Notifications.Clear();

    internal void RemoveByAssembly(Assembly assembly)
    {
        AlertSub[] toRemove;
        lock (_lock)
        {
            toRemove = _subscribers.Where(s => s.Owner == assembly).ToArray();
            _subscribers.RemoveAll(s => s.Owner == assembly);
        }
        // Unhook the Notifications.OnNotification bridge delegates — prevents ALC pin
        foreach (var sub in toRemove)
            Notifications.OnNotification -= sub.Handler;
    }

    void DispatchToSubscribers(Notification notification)
    {
        AlertSub[] snapshot;
        lock (_lock)
            snapshot = _subscribers.Count > 0 ? _subscribers.ToArray() : [];
        foreach (var sub in snapshot)
            sub.Callback(notification);
    }

    // Handler stored alongside subscriber so RemoveByAssembly can unhook it
    record struct AlertSub(Action<Notification> Callback, Assembly Owner, Action<Notification> Handler);
}

/// <summary>Thread-safe single-dispose helper.</summary>
sealed class Subscription(Action onDispose) : IDisposable
{
    int _disposed;
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
            onDispose();
    }
}
