/*---------------------------------------------------------------------------------------------
 *  Copyright (c) 2026 Kaedyn Limon. All rights reserved.
 *  Licensed under the MIT License. See LICENSE in the project root for license information.
 *--------------------------------------------------------------------------------------------*/

// DataChannel — legacy render-wake primitive, now internal to ChannelManager.
// External callers should use ctx.Channels.Notify() and ctx.Channels.Subscribe() instead.

namespace Keystone.Core;

/// <summary>
/// Legacy render-wake primitive. Use <see cref="IChannelManager.Notify"/> and
/// <see cref="IChannelManager.Subscribe(string, Action)"/> instead.
/// Kept for ChannelManager internal delegation; will be inlined in a future pass.
/// </summary>
[System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Never)]
[Obsolete("Use ChannelManager.Instance.Notify/Subscribe or ctx.Channels instead.")]
public static class DataChannel
{
    private static readonly Dictionary<string, List<Action>> _listeners = new();
    private static readonly object _lock = new();

    /// <summary>Subscribe a callback to a named channel.</summary>
    public static void Subscribe(string channel, Action callback)
    {
        lock (_lock)
        {
            if (!_listeners.TryGetValue(channel, out var list))
            {
                list = new();
                _listeners[channel] = list;
            }
            list.Add(callback);
        }
    }

    /// <summary>Subscribe a callback to multiple channels at once.</summary>
    public static void Subscribe(IEnumerable<string> channels, Action callback)
    {
        lock (_lock)
        {
            foreach (var channel in channels)
            {
                if (!_listeners.TryGetValue(channel, out var list))
                {
                    list = new();
                    _listeners[channel] = list;
                }
                list.Add(callback);
            }
        }
    }

    /// <summary>Unsubscribe a callback from all channels.</summary>
    public static void Unsubscribe(Action callback)
    {
        lock (_lock)
        {
            foreach (var list in _listeners.Values)
                list.Remove(callback);
        }
    }

    /// <summary>
    /// Notify all subscribers of a channel. Called by services when data changes.
    /// Snapshot listeners under lock, invoke outside — safe for callbacks that call RequestRedraw.
    /// </summary>
    public static void Notify(string channel)
    {
        Action[] snapshot;
        lock (_lock)
        {
            if (!_listeners.TryGetValue(channel, out var list) || list.Count == 0)
                return;
            snapshot = list.ToArray();
        }
        foreach (var cb in snapshot)
            cb();
    }
}
