// NotificationOverlay - Toast notification stack rendered in the top-right corner of any window
// Info/Warning: auto-dismiss after 5s. Errors: persist until dismissed.
// Usage: call NotificationOverlay.Render(ctx) at the end of any window's Render() method,
// or use NotificationOverlay.Build() to get FlexNodes for scene-based windows.

using Keystone.Core;
using Keystone.Core.Rendering;
using Keystone.Core.UI;

namespace Keystone.Toolkit;

public static class NotificationOverlay
{
    private static readonly List<ToastEntry> _toasts = new();
    private static readonly object _lock = new();
    private const float ToastWidth = 320;
    private const float ToastHeight = 42;
    private const float ToastGap = 6;
    private const float Margin = 12;
    private const double AutoDismissSeconds = 5.0;
    private const int MaxVisible = 5;

    static NotificationOverlay()
    {
        Notifications.OnNotification += OnNotification;
    }

    private static void OnNotification(Notification n)
    {
        lock (_lock)
        {
            _toasts.Add(new ToastEntry(n));
            // Cap visible toasts
            while (_toasts.Count > MaxVisible * 2)
                _toasts.RemoveAt(0);
        }
    }

    /// <summary>Render toast notifications directly via RenderContext (immediate mode).</summary>
    public static void Render(RenderContext ctx)
    {
        List<ToastEntry> visible;
        lock (_lock)
        {
            Prune();
            visible = _toasts.TakeLast(MaxVisible).ToList();
        }

        if (visible.Count == 0) return;

        float windowW = ctx.State.Width;
        float y = Margin;

        for (int i = visible.Count - 1; i >= 0; i--)
        {
            var toast = visible[i];
            var n = toast.Notification;
            float x = windowW - ToastWidth - Margin;

            var (bgColor, borderColor, iconChar) = n.Level switch
            {
                NotificationLevel.Error => (0x3a1818ff, 0x662222ff, "!"),
                NotificationLevel.Warning => (0x352a10ff, 0x554418ff, "!"),
                _ => (0x1a2230ff, 0x2a3650ff, "i")
            };

            var textColor = n.Level == NotificationLevel.Error ? 0xef5350ff
                : n.Level == NotificationLevel.Warning ? 0xffca28ff
                : Theme.TextPrimary;

            ctx.RoundedRect(x, y, ToastWidth, ToastHeight, 6, (uint)bgColor);
            ctx.RoundedRectStroke(x, y, ToastWidth, ToastHeight, 6, 1, (uint)borderColor);

            // Icon circle
            float iconX = x + 12;
            float iconY = y + ToastHeight / 2;
            ctx.RoundedRect(iconX, iconY - 9, 18, 18, 9, (uint)borderColor);
            ctx.Text(iconX + 5, iconY + 5, iconChar, 12, 0xffffffcc, FontId.Bold);

            // Message text (truncated)
            var msg = n.Message.Length > 42 ? n.Message[..39] + "..." : n.Message;
            ctx.Text(x + 38, y + ToastHeight / 2 + 5, msg, 12, (uint)textColor, FontId.Regular);

            // Dismiss X for errors
            if (n.Level == NotificationLevel.Error)
                ctx.Text(x + ToastWidth - 22, y + ToastHeight / 2 + 5, "x", 11, Theme.TextSecondary, FontId.Regular);

            y += ToastHeight + ToastGap;
        }
    }

    /// <summary>Build FlexNodes for scene-based windows. Append to your scene root.</summary>
    public static FlexNode? Build()
    {
        List<ToastEntry> visible;
        lock (_lock)
        {
            Prune();
            visible = _toasts.TakeLast(MaxVisible).ToList();
        }

        if (visible.Count == 0) return null;

        var container = Flex.Absolute(right: Margin, top: Margin);
        container.Direction = FlexDir.Column;
        container.Gap = ToastGap;
        container.Width = ToastWidth;

        foreach (var toast in visible.AsEnumerable().Reverse())
        {
            var n = toast.Notification;
            var (bgColor, borderColor) = n.Level switch
            {
                NotificationLevel.Error => ((uint)0x3a1818ff, (uint)0x662222ff),
                NotificationLevel.Warning => ((uint)0x352a10ff, (uint)0x554418ff),
                _ => ((uint)0x1a2230ff, (uint)0x2a3650ff)
            };

            var textColor = n.Level == NotificationLevel.Error ? 0xef5350ff
                : n.Level == NotificationLevel.Warning ? 0xffca28ff
                : Theme.TextPrimary;

            var msg = n.Message.Length > 42 ? n.Message[..39] + "..." : n.Message;

            var row = Flex.Row(pad: 10, gap: 8, align: FlexAlign.Center);
            row.BgColor = bgColor;
            row.BorderColor = borderColor;
            row.BorderWidth = 1;
            row.BgRadius = 6;
            row.MinHeight = ToastHeight;
            row.Child(Flex.Text(msg, 12, textColor));
            container.Child(row);
        }

        return container;
    }

    private static void Prune()
    {
        var now = DateTime.UtcNow;
        _toasts.RemoveAll(t =>
            t.Notification.Level != NotificationLevel.Error &&
            (now - t.CreatedAt).TotalSeconds > AutoDismissSeconds);
    }

    private sealed class ToastEntry
    {
        public Notification Notification { get; }
        public DateTime CreatedAt { get; }

        public ToastEntry(Notification notification)
        {
            Notification = notification;
            CreatedAt = DateTime.UtcNow;
        }
    }
}
