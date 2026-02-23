// Notifications - Lightweight notification bus for surfacing errors, warnings, and info messages in-app
// Error paths (DyLibLoader, ScriptManager, BunProcess, etc.) push here alongside Console.WriteLine.
// UI components subscribe via OnNotification or read Recent.

namespace Keystone.Core;

public static class Notifications
{
    private static readonly List<Notification> _recent = new();
    private static readonly object _lock = new();
    private const int MaxRecent = 50;

    public static event Action<Notification>? OnNotification;

    public static IReadOnlyList<Notification> Recent
    {
        get { lock (_lock) return _recent.ToList(); }
    }

    public static void Push(string message, NotificationLevel level = NotificationLevel.Info)
    {
        var notification = new Notification(message, level, DateTime.UtcNow);
        lock (_lock)
        {
            _recent.Add(notification);
            if (_recent.Count > MaxRecent)
                _recent.RemoveAt(0);
        }
        OnNotification?.Invoke(notification);
    }

    public static void Error(string message) => Push(message, NotificationLevel.Error);
    public static void Warn(string message) => Push(message, NotificationLevel.Warning);
    public static void Info(string message) => Push(message, NotificationLevel.Info);

    /// <summary>Dismiss a specific notification (removes from Recent).</summary>
    public static void Dismiss(Notification notification)
    {
        lock (_lock) _recent.Remove(notification);
    }

    /// <summary>Clear all notifications.</summary>
    public static void Clear()
    {
        lock (_lock) _recent.Clear();
    }
}

public record Notification(string Message, NotificationLevel Level, DateTime Timestamp);

public enum NotificationLevel { Info, Warning, Error }
