// Layout - Container and display components for building window UIs
// Panels, cards, strips, headers, status rows, stat cards, badges, etc.

using Keystone.Core;
using Keystone.Core.Rendering;
using Keystone.Core.UI;

namespace Keystone.Toolkit;

public static class Layout
{
    // ═══════════════════════════════════════════════════════════
    //  PANELS & CONTAINERS
    // ═══════════════════════════════════════════════════════════

    /// <summary>Chrome panel: rounded rect with stroke border (outer wrapper)</summary>
    public static FlexNode ChromePanel(float pad = 14, float radius = 10)
    {
        var panel = Flex.Column(gap: 12, pad: pad);
        panel.BgColor = Theme.BgChrome; panel.BgRadius = radius;
        panel.BorderColor = Theme.Stroke; panel.BorderWidth = 1;
        return panel;
    }

    /// <summary>Card panel with dark background</summary>
    public static FlexNode Card(float pad = 14, float gap = 8, float radius = 8)
    {
        var card = Flex.Column(gap: gap, pad: pad);
        card.BgColor = Theme.BgElevated; card.BgRadius = radius;
        return card;
    }

    /// <summary>Strip/toolbar with darker background</summary>
    public static FlexNode Strip(float pad = 0, float gap = 0, float minH = 0)
    {
        if (pad == 0) pad = Theme.PadX;
        if (gap == 0) gap = Theme.GapX;
        if (minH == 0) minH = Theme.StripHeight;
        var strip = Flex.Row(gap: gap, align: FlexAlign.Center, pad: pad);
        strip.BgColor = Theme.BgStrip; strip.MinHeight = minH;
        return strip;
    }

    /// <summary>Warning/danger panel with red tint, checkbox confirmations, and status text</summary>
    public static FlexNode WarningPanel(string title, string[] checkLabels, bool[] checkStates,
        string[] checkActions, string statusText, bool statusOk, float fontSize = 13)
    {
        var panel = Flex.Column(gap: 8, pad: 14);
        panel.BgColor = Theme.WarningBg; panel.BgRadius = 8; panel.MinHeight = 80;

        panel.Child(Flex.Text(title, fontSize, 0xff6666ff, FontId.Bold));

        for (int i = 0; i < checkLabels.Length; i++)
        {
            var row = Flex.Row(gap: 8, align: FlexAlign.Center);
            row.Child(Controls.Checkbox(checkStates[i], checkActions[i], 18));
            row.Child(Flex.Text(checkLabels[i], fontSize,
                checkStates[i] ? Theme.TextPrimary : Theme.TextSecondary));
            row.Action = checkActions[i];
            panel.Child(row);
        }

        uint statusColor = statusOk ? 0x44ff44ff : 0xff4444ff;
        panel.Child(Flex.Text(statusText, 14, statusColor, FontId.Bold, TextAlign.Center));

        return panel;
    }

    // ═══════════════════════════════════════════════════════════
    //  LABELS & HEADERS
    // ═══════════════════════════════════════════════════════════

    /// <summary>Simple label text</summary>
    public static FlexNode Label(string text, float fontSize = 14)
        => Flex.Text(text, fontSize, Theme.TextSecondary, FontId.Bold);

    /// <summary>Section header: uppercase label + expanding divider line</summary>
    public static FlexNode SectionHeader(string text, float fontSize = 13)
    {
        var row = Flex.Row(gap: 12, align: FlexAlign.Center);
        row.Padding = 4;
        row.Child(Flex.Text(text.ToUpper(), fontSize, Theme.AccentHeader, FontId.Bold));
        row.Child(new FlexNode { FlexGrow = 1, Height = 1, BgColor = Theme.Divider });
        return row;
    }

    /// <summary>Horizontal divider line</summary>
    public static FlexNode Divider(float height = 1, uint color = 0)
    {
        if (color == 0) color = Theme.Divider;
        return new FlexNode { Height = height, BgColor = color, FlexGrow = 0 };
    }

    // ═══════════════════════════════════════════════════════════
    //  STAT CARDS
    // ═══════════════════════════════════════════════════════════

    /// <summary>Stat card: label + large value + unit (3-line)</summary>
    public static FlexNode StatCard(string label, string value, string unit, uint valueColor = 0x66b3ffff)
    {
        var card = Flex.Column(align: FlexAlign.Center, justify: FlexAlign.Center, gap: 6);
        card.BgColor = 0x1e2a3aff; card.BgRadius = 8; card.Padding = 16; card.FlexGrow = 1; card.MinHeight = 100;
        card.Child(Flex.Text(label.ToUpper(), 12, Theme.TextSecondary, FontId.Bold, TextAlign.Center));
        card.Child(Flex.Text(value, 26, valueColor, FontId.Bold, TextAlign.Center));
        card.Child(Flex.Text(unit, 12, Theme.TextMuted, FontId.Regular, TextAlign.Center));
        return card;
    }

    /// <summary>Simple stat card: label + value (2-line, compact)</summary>
    public static FlexNode StatCardSimple(string label, string value, uint valueColor,
        float minH = 48, float valueFontSize = 16)
    {
        var card = Flex.Column(align: FlexAlign.Center, justify: FlexAlign.Center, gap: 2);
        card.BgColor = 0x22222bff; card.BgRadius = 6; card.Padding = 8; card.FlexGrow = 1; card.MinHeight = minH;
        card.Child(Flex.Text(label.ToUpper(), 11, Theme.TextSecondary, FontId.Bold, TextAlign.Center));
        card.Child(Flex.Text(value, valueFontSize, valueColor, FontId.Bold, TextAlign.Center));
        return card;
    }

    /// <summary>Three-stat row</summary>
    public static FlexNode StatRow3(
        string label1, string value1, uint color1,
        string label2, string value2, uint color2,
        string label3, string value3, uint color3,
        float gap = 8, float minH = 48)
    {
        return Flex.Row(gap: gap)
            .Child(StatCardSimple(label1, value1, color1, minH))
            .Child(StatCardSimple(label2, value2, color2, minH))
            .Child(StatCardSimple(label3, value3, color3, minH));
    }

    // ═══════════════════════════════════════════════════════════
    //  ROWS & LIST ITEMS
    // ═══════════════════════════════════════════════════════════

    /// <summary>Setting row: label on left, value on right, optional action</summary>
    public static FlexNode SettingRow(string label, string value, string? action = null,
        uint valueColor = 0xaaaaaaff, float fontSize = 15)
    {
        var row = Flex.Row(gap: 8, align: FlexAlign.Center);
        row.BgColor = Theme.BgElevated; row.BgRadius = 8; row.Padding = 16; row.MinHeight = 48;
        row.Child(Flex.Text(label, fontSize, Theme.TextPrimary));
        row.Child(Flex.Spacer());
        row.Child(Flex.Text(value, fontSize, valueColor, FontId.Regular));
        if (action != null) row.Action = action;
        return row;
    }

    /// <summary>Status dot + label + status text row</summary>
    public static FlexNode StatusRow(string label, string status, uint dotColor,
        string? buttonLabel = null, string? buttonAction = null, float fontSize = 15)
    {
        var row = Flex.Row(gap: 12, align: FlexAlign.Center);
        row.BgColor = Theme.BgElevated; row.BgRadius = 8; row.Padding = 16; row.MinHeight = 48;
        row.Child(new FlexNode { Width = 10, Height = 10, BgColor = dotColor, BgRadius = 5 });
        row.Child(Flex.Text(label, fontSize, Theme.TextPrimary));
        row.Child(Flex.Spacer());
        row.Child(Flex.Text(status, fontSize - 1, Theme.TextSecondary));
        if (buttonLabel != null && buttonAction != null)
        {
            row.Child(new FlexNode
            {
                Text = buttonLabel, FontSize = 12, TextColor = Theme.TextPrimary,
                TextAlign = TextAlign.Center, BgColor = Theme.BgButton, BgRadius = 4,
                MinWidth = 60, Height = 24, Action = buttonAction
            });
        }
        return row;
    }

    // ═══════════════════════════════════════════════════════════
    //  INFO & STATUS
    // ═══════════════════════════════════════════════════════════

    /// <summary>Connection status: colored dot + latency text</summary>
    public static FlexNode ConnectionDot(bool connected, int latencyMs = 0)
    {
        var row = Flex.Row(gap: 6, align: FlexAlign.Center);
        row.Child(new FlexNode
        {
            Width = 8, Height = 8, BgRadius = 4,
            BgColor = connected ? Theme.Success : Theme.Danger
        });
        if (connected && latencyMs > 0)
        {
            uint pingColor = latencyMs < 50 ? Theme.Success : latencyMs < 200 ? Theme.Warning : Theme.Danger;
            row.Child(Flex.Text($"{latencyMs}ms", 12, pingColor));
        }
        else if (!connected)
        {
            row.Child(Flex.Text("Disconnected", 12, Theme.Danger));
        }
        return row;
    }

    /// <summary>Empty state message centered in a panel</summary>
    public static FlexNode EmptyState(string message, float fontSize = 16)
    {
        var panel = Flex.Column(align: FlexAlign.Center, justify: FlexAlign.Center);
        panel.FlexGrow = 1; panel.MinHeight = 100;
        panel.Child(Flex.Text(message, fontSize, Theme.TextSecondary, FontId.Regular, TextAlign.Center));
        return panel;
    }

    /// <summary>Badge pill (small colored label)</summary>
    public static FlexNode Badge(string text, uint bgColor, float fontSize = 10, float height = 18)
        => new()
        {
            Text = text, FontSize = fontSize, TextColor = Theme.TextPrimary, Font = FontId.Bold,
            TextAlign = TextAlign.Center, BgColor = bgColor, BgRadius = 3,
            Height = height, MinWidth = text.Length * fontSize * 0.6f + 14, Padding = 4
        };
}
