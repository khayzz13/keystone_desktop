// DataChannel — string-keyed pub/sub for data-driven redraws
// Services call DataChannel.Notify("trades") when new data arrives.
// ManagedWindow subscribes RequestRedraw for the plugin's Dependencies channels.
// Apps define their own channel names — the framework has no domain knowledge.

namespace Keystone.Core;

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
