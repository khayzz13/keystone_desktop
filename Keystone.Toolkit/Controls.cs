// Controls - Flex-based UI component library
// Apps compose windows from these components instead of hand-coding ctx.* calls.

using Keystone.Core;
using Keystone.Core.Rendering;
using Keystone.Core.UI;

namespace Keystone.Toolkit;

public static class Controls
{
    // ═══════════════════════════════════════════════════════════
    //  SPINNERS
    // ═══════════════════════════════════════════════════════════

    /// <summary>Full-size spinner: [-] [value] [+]</summary>
    public static FlexNode Spinner(string value, string decAction, string incAction,
        float h = 44, float fontSize = 20, float valueW = 120)
    {
        return Flex.Row(justify: FlexAlign.Center)
            .Child(SpinnerBtn("-", decAction, h))
            .Child(new FlexNode
            {
                Text = value, FontSize = fontSize, TextColor = Theme.TextPrimary,
                TextAlign = TextAlign.Center, Font = FontId.Bold,
                BgColor = Theme.BgBase, MinHeight = h, Width = valueW
            })
            .Child(SpinnerBtn("+", incAction, h));
    }

    /// <summary>Compact spinner for inline use (32px height)</summary>
    public static FlexNode MiniSpinner(string value, string decAction, string incAction)
    {
        return Flex.Row(justify: FlexAlign.Center)
            .Child(new FlexNode
            {
                Text = "-", FontSize = 16, TextColor = Theme.TextPrimary, TextAlign = TextAlign.Center,
                BgColor = Theme.BgButtonHover, BgRadius = 5, Width = 32, Height = 32, Action = decAction
            })
            .Child(new FlexNode
            {
                Text = value, FontSize = 16, TextColor = Theme.TextPrimary, TextAlign = TextAlign.Center,
                Font = FontId.Bold, BgColor = Theme.BgBase, Width = 56, Height = 32
            })
            .Child(new FlexNode
            {
                Text = "+", FontSize = 16, TextColor = Theme.TextPrimary, TextAlign = TextAlign.Center,
                BgColor = Theme.BgButtonHover, BgRadius = 5, Width = 32, Height = 32, Action = incAction
            });
    }

    static FlexNode SpinnerBtn(string label, string action, float h)
        => new()
        {
            Text = label, FontSize = 22, TextColor = Theme.TextPrimary,
            TextAlign = TextAlign.Center, Font = FontId.Bold,
            BgColor = Theme.BgButtonDark, BgRadius = 6, Width = 48, MinHeight = h, Action = action
        };

    // ═══════════════════════════════════════════════════════════
    //  CHECKBOXES & TOGGLES
    // ═══════════════════════════════════════════════════════════

    /// <summary>Standalone checkbox square</summary>
    public static FlexNode Checkbox(bool isChecked, string? action = null, float size = 20)
        => new()
        {
            Width = size, Height = size,
            BgColor = isChecked ? Theme.Accent : Theme.BgButtonHover, BgRadius = 4,
            Text = isChecked ? "\u2713" : null, FontSize = size * 0.65f,
            TextColor = Theme.TextPrimary, TextAlign = TextAlign.Center,
            Action = action
        };

    /// <summary>Checkbox + label row</summary>
    public static FlexNode CheckRow(string label, bool isChecked, string action, float fontSize = 15)
    {
        var row = Flex.Row(gap: 10, align: FlexAlign.Center);
        row.MinHeight = 36;
        row.Child(Checkbox(isChecked));
        row.Child(Flex.Text(label, fontSize, isChecked ? Theme.TextPrimary : Theme.TextSecondary));
        row.Action = action;
        return row;
    }

    /// <summary>Toggle button (active/inactive with custom active color)</summary>
    public static FlexNode ToggleButton(string label, string action, bool active,
        uint activeColor = 0, float fontSize = 15, float minH = 40)
    {
        if (activeColor == 0) activeColor = Theme.Accent;
        return new FlexNode
        {
            Text = label, FontSize = fontSize, TextColor = Theme.TextPrimary, Font = FontId.Bold,
            TextAlign = TextAlign.Center, BgRadius = 6, MinHeight = minH, FlexGrow = 1,
            BgColor = active ? activeColor : Theme.BgButton,
            HoverBgColor = active ? activeColor : Theme.BgButtonHover,
            PressedBgColor = active ? activeColor : Theme.BgButtonDark,
            Action = action
        };
    }

    // ═══════════════════════════════════════════════════════════
    //  TABS & CHIPS
    // ═══════════════════════════════════════════════════════════

    /// <summary>Pill-style tab strip with container background.
    /// Actions fire as "{actionPrefix}:{index}".</summary>
    public static FlexNode TabStrip<T>(T[] values, string[] labels, T current, string actionPrefix,
        float fontSize = 15, float minH = 40) where T : struct
    {
        var row = Flex.Row(gap: 4);
        row.BgColor = Theme.BgBase; row.BgRadius = 8; row.Padding = 4;
        for (int i = 0; i < values.Length; i++)
        {
            bool active = EqualityComparer<T>.Default.Equals(values[i], current);
            row.Child(new FlexNode
            {
                Text = labels[i], FontSize = fontSize, Font = FontId.Bold,
                TextColor = active ? Theme.TextPrimary : Theme.TextSecondary,
                TextAlign = TextAlign.Center, BgRadius = 6, MinHeight = minH, FlexGrow = 1,
                BgColor = active ? Theme.Accent : 0x00000000u,
                Action = $"{actionPrefix}:{i}"
            });
        }
        return row;
    }

    /// <summary>Small chip/pill button (for filters, tags)</summary>
    public static FlexNode Chip(string label, bool active, string action,
        uint activeColor = 0, float height = 24, float fontSize = 11)
    {
        if (activeColor == 0) activeColor = Theme.AccentBright;
        return new FlexNode
        {
            Text = label, FontSize = fontSize, Font = active ? FontId.Bold : FontId.Regular,
            TextColor = active ? Theme.TextPrimary : Theme.TextSecondary,
            TextAlign = TextAlign.Center, BgRadius = 4, Height = height,
            MinWidth = 36, Padding = 6,
            BgColor = active ? activeColor : Theme.BgButton,
            Action = action
        };
    }

    /// <summary>Row of chips for filtering</summary>
    public static FlexNode ChipRow(string[] labels, string[] actions, int activeIndex = -1,
        uint activeColor = 0, float height = 24)
    {
        var row = Flex.Row(gap: 4, align: FlexAlign.Center);
        for (int i = 0; i < labels.Length; i++)
            row.Child(Chip(labels[i], i == activeIndex, actions[i], activeColor, height));
        return row;
    }

    // ═══════════════════════════════════════════════════════════
    //  DROPDOWNS
    // ═══════════════════════════════════════════════════════════

    /// <summary>Dropdown selector: trigger button + floating options list via absolute positioning.
    /// Actions fire as "{actionPrefix}:{index}". Caller manages open state via toggleAction.</summary>
    public static FlexNode Dropdown(string current, string[] options, string actionPrefix,
        bool isOpen, string toggleAction, int selectedIndex = -1,
        float minW = 140, float fontSize = 14)
    {
        var wrapper = new FlexNode { MinWidth = minW };

        // Trigger button
        var trigger = Flex.Row(gap: 6, pad: 10, align: FlexAlign.Center);
        trigger.BgColor = Theme.BgBase; trigger.BgRadius = 6; trigger.MinHeight = 34;
        trigger.HoverBgColor = Theme.BgButton;
        trigger.Action = toggleAction;
        trigger.ActionCursor = CursorType.Pointer;
        trigger.Child(Flex.Text(current, fontSize, Theme.TextPrimary, FontId.Bold));
        trigger.Child(Flex.Spacer());
        trigger.Child(Flex.Text(isOpen ? "\u25b4" : "\u25be", 12, Theme.TextSecondary));
        wrapper.Child(trigger);

        if (isOpen)
        {
            var list = Flex.Absolute(left: 0, top: 36);
            list.Width = minW; list.BgColor = 0x1e1e28ff; list.BgRadius = 6;
            list.BorderColor = Theme.Stroke; list.BorderWidth = 1;
            list.Padding = 2; list.Direction = FlexDir.Column;
            for (int i = 0; i < options.Length; i++)
            {
                bool active = i == selectedIndex;
                list.Child(new FlexNode
                {
                    Text = options[i], FontSize = fontSize,
                    TextColor = active ? Theme.TextPrimary : Theme.TextSecondary,
                    Font = active ? FontId.Bold : FontId.Regular,
                    BgColor = active ? Theme.Accent : 0x00000000u,
                    HoverBgColor = active ? Theme.Accent : Theme.BgButtonHover,
                    BgRadius = 4, MinHeight = 32, Padding = 10,
                    Action = $"{actionPrefix}:{i}",
                    ActionCursor = CursorType.Pointer
                });
            }
            wrapper.Child(list);
        }

        return wrapper;
    }

    // ═══════════════════════════════════════════════════════════
    //  PRESET ROWS
    // ═══════════════════════════════════════════════════════════

    /// <summary>Preset row with active highlight (e.g. [1] [2] [5] [10])</summary>
    public static FlexNode PresetRow(int[] values, int current, string actionPrefix,
        float fontSize = 14, float minH = 34)
    {
        var row = Flex.Row(gap: 6, wrap: FlexWrap.Wrap);
        row.GapRow = 6;
        foreach (var val in values)
        {
            bool active = val == current;
            row.Child(new FlexNode
            {
                Text = val.ToString(), FontSize = fontSize,
                TextColor = active ? Theme.TextPrimary : Theme.TextSecondary,
                TextAlign = TextAlign.Center, Font = active ? FontId.Bold : FontId.Regular,
                BgRadius = 6, MinHeight = minH, FlexGrow = 1,
                MinWidth = 42, BgColor = active ? Theme.Accent : Theme.BgButton,
                Action = $"{actionPrefix}:{val}"
            });
        }
        return row;
    }

    /// <summary>Preset row with string labels (no active state)</summary>
    public static FlexNode PresetRow(string[] labels, string[] actions, float fontSize = 14, float minH = 34)
    {
        var row = Flex.Row(gap: 6);
        for (int i = 0; i < labels.Length; i++)
        {
            row.Child(new FlexNode
            {
                Text = labels[i], FontSize = fontSize, TextColor = Theme.TextSecondary,
                TextAlign = TextAlign.Center, BgRadius = 6, MinHeight = minH, FlexGrow = 1,
                BgColor = Theme.BgButton, Action = actions[i]
            });
        }
        return row;
    }

    // ═══════════════════════════════════════════════════════════
    //  ACTION BUTTONS
    // ═══════════════════════════════════════════════════════════

    /// <summary>Large action button with main label + optional sub-label</summary>
    public static FlexNode ActionButton(string label, string? subLabel, string action, uint bgColor,
        float fontSize = 20, float minH = 64, uint? hoverColor = null, uint? pressedColor = null)
    {
        var btn = Flex.Column(align: FlexAlign.Center, justify: FlexAlign.Center);
        btn.BgColor = bgColor; btn.BgRadius = 8; btn.MinHeight = minH; btn.FlexGrow = 1; btn.Action = action;
        btn.HoverBgColor = hoverColor ?? bgColor;
        btn.PressedBgColor = pressedColor ?? bgColor;
        btn.BorderColor = Theme.ButtonBorder; btn.BorderWidth = 1;
        btn.Padding = 8;
        btn.Child(Flex.Text(label, fontSize, Theme.TextPrimary, FontId.Bold, TextAlign.Center));
        if (subLabel != null)
            btn.Child(Flex.Text(subLabel, 14, Theme.TextSubtle, FontId.Regular, TextAlign.Center));
        return btn;
    }

    // ═══════════════════════════════════════════════════════════
    //  TEXT INPUT
    // ═══════════════════════════════════════════════════════════

    /// <summary>Themed single-line text input with cursor, focus border, and placeholder.
    /// Wraps Core's Flex.TextInput with Toolkit theme colors.</summary>
    public static FlexNode TextInput(TextEntry entry, string placeholder = "",
        float fontSize = 14, float minH = 34, float minW = 140)
    {
        var text = entry.IsFocused ? entry.Buffer
            : entry.Buffer.Length > 0 ? entry.Buffer
            : placeholder;

        var textColor = entry.IsFocused || entry.Buffer.Length > 0
            ? Theme.TextPrimary
            : Theme.TextSecondary;

        return new FlexNode
        {
            TextInput = entry,
            Text = text,
            FontSize = fontSize,
            TextColor = textColor,
            BgColor = Theme.BgBase,
            BgRadius = 6,
            BorderColor = entry.IsFocused ? Theme.Accent : Theme.Stroke,
            BorderWidth = entry.IsFocused ? 1.5f : 1f,
            MinHeight = minH,
            MinWidth = minW,
            FlexGrow = 1,
            Padding = 10,
            Action = $"__textinput_focus:{entry.Tag ?? ""}"
        };
    }

    /// <summary>Multi-line text area with scroll support.</summary>
    public static FlexNode TextArea(TextEntry entry, string placeholder = "",
        int rows = 4, float fontSize = 14)
    {
        var text = entry.IsFocused ? entry.Buffer
            : entry.Buffer.Length > 0 ? entry.Buffer
            : placeholder;

        var textColor = entry.IsFocused || entry.Buffer.Length > 0
            ? Theme.TextPrimary
            : Theme.TextSecondary;

        float lineHeight = fontSize * 1.5f;
        float minH = lineHeight * rows + 20; // padding

        return new FlexNode
        {
            TextInput = entry,
            Text = text,
            FontSize = fontSize,
            TextColor = textColor,
            BgColor = Theme.BgBase,
            BgRadius = 6,
            BorderColor = entry.IsFocused ? Theme.Accent : Theme.Stroke,
            BorderWidth = entry.IsFocused ? 1.5f : 1f,
            MinHeight = minH,
            FlexGrow = 1,
            Padding = 10,
            Action = $"__textinput_focus:{entry.Tag ?? ""}"
        };
    }

    /// <summary>Labeled text input: [Label] [Input field]</summary>
    public static FlexNode LabeledInput(string label, TextEntry entry,
        string placeholder = "", float labelWidth = 100, float fontSize = 14)
    {
        var row = Flex.Row(gap: 10, align: FlexAlign.Center);
        row.MinHeight = 34;
        row.Child(new FlexNode
        {
            Text = label, FontSize = fontSize, TextColor = Theme.TextSecondary,
            Font = FontId.Bold, Width = labelWidth
        });
        row.Child(TextInput(entry, placeholder, fontSize));
        return row;
    }
}
