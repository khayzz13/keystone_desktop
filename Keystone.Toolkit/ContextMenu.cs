// ContextMenu - Generic right-click context menu system
// Provides: items, separators, sliders, color wheels, submenus
// Self-contained state — does not depend on StateManager.

using Keystone.Core;
using Keystone.Core.Rendering;

namespace Keystone.Toolkit;

public enum ContextItemType { Button, Separator, Slider, ColorWheel, Submenu }

public class ContextItem
{
    public string Id { get; set; } = "";
    public string Label { get; set; } = "";
    public ContextItemType Type { get; set; } = ContextItemType.Button;
    public float SliderValue { get; set; }
    public float SliderMin { get; set; }
    public float SliderMax { get; set; } = 1f;
    public uint ColorValue { get; set; } = 0xFFFFFFFF;
    public List<ContextItem>? Submenu { get; set; }
    public Action? OnClick { get; set; }
    public Action<float>? OnSliderChange { get; set; }
    public Action<uint>? OnColorChange { get; set; }
}

/// <summary>Per-menu instance state. Create one per window and reuse across frames.</summary>
public class ContextMenuState
{
    public bool IsOpen;
    public float X, Y;
    public string? OpenSubmenuId;
    public string? ActiveSliderId;
    public string? ActiveColorId;
    public List<ContextItem>? Items;
}

public static class ContextMenu
{
    private const float ItemH = 24f;
    private const float SliderH = 32f;
    private const float ColorWheelH = 120f;
    private const float MenuW = 160f;
    private const float Pad = 6f;
    private const float SliderTrackH = 4f;
    private const float SliderKnobR = 6f;

    public static void Open(ContextMenuState state, float x, float y, List<ContextItem> items)
    {
        state.IsOpen = true;
        state.X = x;
        state.Y = y;
        state.OpenSubmenuId = null;
        state.ActiveSliderId = null;
        state.ActiveColorId = null;
        state.Items = items;
    }

    public static void Close(ContextMenuState state)
    {
        state.IsOpen = false;
        state.OpenSubmenuId = null;
        state.ActiveSliderId = null;
        state.ActiveColorId = null;
    }

    /// <summary>Render the context menu. Returns true if interaction was consumed.</summary>
    public static bool Render(RenderContext ctx, ContextMenuState state)
    {
        if (!state.IsOpen || state.Items == null) return false;

        var items = state.Items;
        float menuH = CalcMenuHeight(items, state);
        float x = state.X, y = state.Y;

        // Clamp to screen
        if (x + MenuW > ctx.State.Width) x = ctx.State.Width - MenuW - 4;
        if (y + menuH > ctx.State.Height) y = ctx.State.Height - menuH - 4;

        // Background
        ctx.Rect(x, y, MenuW, menuH, Theme.BgSurface);
        ctx.RectStroke(x, y, MenuW, menuH, 1, Theme.BgLight);

        float curY = y + 2;
        bool consumed = false;

        foreach (var item in items)
            consumed |= RenderItem(ctx, state, item, x, ref curY, MenuW);

        // Close on click outside
        if (ctx.State.MouseClicked && !ctx.IsHovered(x - 10, y - 10, MenuW + 20, menuH + 20))
        {
            Close(state);
            ctx.RequestRedraw();
            return true;
        }

        return consumed;
    }

    static float CalcMenuHeight(List<ContextItem> items, ContextMenuState state)
    {
        float h = 4;
        foreach (var item in items)
        {
            h += item.Type switch
            {
                ContextItemType.Separator => 8,
                ContextItemType.Slider => SliderH,
                ContextItemType.ColorWheel => state.ActiveColorId == item.Id ? ColorWheelH + ItemH : ItemH,
                _ => ItemH
            };
        }
        return h;
    }

    static bool RenderItem(RenderContext ctx, ContextMenuState state, ContextItem item, float x, ref float curY, float w)
    {
        bool consumed = false;
        switch (item.Type)
        {
            case ContextItemType.Separator:
                ctx.Rect(x + Pad, curY + 3, w - Pad * 2, 1, Theme.BgLight);
                curY += 8;
                break;
            case ContextItemType.Button:
                consumed = RenderButton(ctx, state, item, x, curY, w);
                curY += ItemH;
                break;
            case ContextItemType.Slider:
                consumed = RenderSlider(ctx, state, item, x, curY, w);
                curY += SliderH;
                break;
            case ContextItemType.ColorWheel:
                consumed = RenderColorItem(ctx, state, item, x, ref curY, w);
                break;
            case ContextItemType.Submenu:
                consumed = RenderSubmenu(ctx, state, item, x, curY, w);
                curY += ItemH;
                break;
        }
        return consumed;
    }

    static bool RenderButton(RenderContext ctx, ContextMenuState state, ContextItem item, float x, float y, float w)
    {
        var hovered = ctx.IsHovered(x, y, w, ItemH);
        if (hovered)
        {
            ctx.Rect(x + 2, y, w - 4, ItemH, Theme.BgLight);
            ctx.SetCursor(CursorType.Pointer);
        }
        ctx.Text(x + Pad, y + ItemH / 2 + 4, item.Label, 10f, Theme.TextPrimary, FontId.Regular);

        if (ctx.WasClicked(x, y, w, ItemH))
        {
            item.OnClick?.Invoke();
            Close(state);
            ctx.RequestRedraw();
            return true;
        }
        return false;
    }

    static bool RenderSlider(RenderContext ctx, ContextMenuState state, ContextItem item, float x, float y, float w)
    {
        ctx.Text(x + Pad, y + 12, item.Label, 9f, Theme.TextSecondary, FontId.Regular);

        float trackX = x + Pad;
        float trackY = y + 20;
        float trackW = w - Pad * 2;
        ctx.RoundedRect(trackX, trackY, trackW, SliderTrackH, 2, Theme.BgLight);

        float ratio = (item.SliderValue - item.SliderMin) / (item.SliderMax - item.SliderMin);
        ratio = Math.Clamp(ratio, 0, 1);
        ctx.RoundedRect(trackX, trackY, trackW * ratio, SliderTrackH, 2, Theme.AccentBright);

        float knobX = trackX + trackW * ratio;
        float knobY = trackY + SliderTrackH / 2;
        ctx.Circle(knobX, knobY, SliderKnobR, Theme.TextPrimary);

        var valText = item.SliderValue.ToString("F1");
        ctx.Text(x + w - Pad, y + 12, valText, 9f, Theme.TextPrimary, FontId.Regular, TextAlign.Right);

        bool inTrack = ctx.IsHovered(trackX - SliderKnobR, trackY - SliderKnobR, trackW + SliderKnobR * 2, SliderTrackH + SliderKnobR * 2);
        if (inTrack) ctx.SetCursor(CursorType.Pointer);
        if (inTrack && ctx.State.MouseDown) state.ActiveSliderId = item.Id;

        if (state.ActiveSliderId == item.Id)
        {
            float newRatio = Math.Clamp((ctx.State.MouseX - trackX) / trackW, 0, 1);
            float newVal = item.SliderMin + newRatio * (item.SliderMax - item.SliderMin);
            if (Math.Abs(newVal - item.SliderValue) > 0.001f)
            {
                item.SliderValue = newVal;
                item.OnSliderChange?.Invoke(newVal);
                ctx.RequestRedraw();
            }
            if (!ctx.State.MouseDown) state.ActiveSliderId = null;
            return true;
        }
        return false;
    }

    static bool RenderColorItem(RenderContext ctx, ContextMenuState state, ContextItem item, float x, ref float curY, float w)
    {
        bool expanded = state.ActiveColorId == item.Id;
        var hovered = ctx.IsHovered(x, curY, w, ItemH);
        if (hovered)
        {
            ctx.Rect(x + 2, curY, w - 4, ItemH, Theme.BgLight);
            ctx.SetCursor(CursorType.Pointer);
        }
        ctx.Text(x + Pad, curY + ItemH / 2 + 4, item.Label, 10f, Theme.TextPrimary, FontId.Regular);

        float swatchX = x + w - Pad - 20;
        ctx.Rect(swatchX, curY + 4, 16, 16, item.ColorValue);
        ctx.RectStroke(swatchX, curY + 4, 16, 16, 1, Theme.BgLight);

        if (ctx.WasClicked(x, curY, w, ItemH))
        {
            state.ActiveColorId = expanded ? null : item.Id;
            ctx.RequestRedraw();
        }

        curY += ItemH;

        if (expanded)
        {
            bool consumed = RenderColorWheel(ctx, item, x + Pad, curY, w - Pad * 2, ColorWheelH - 10);
            curY += ColorWheelH - ItemH;
            return consumed;
        }
        return false;
    }

    static bool RenderColorWheel(RenderContext ctx, ContextItem item, float x, float y, float w, float h)
    {
        float wheelR = Math.Min(w, h) / 2 - 10;
        float cx = x + w / 2;
        float cy = y + h / 2;

        for (int i = 0; i < 12; i++)
        {
            float angle = i * 30f * MathF.PI / 180f;
            float nextAngle = (i + 1) * 30f * MathF.PI / 180f;
            uint hueColor = HsvToRgb(i * 30f, 1f, 1f);

            float x1 = cx + MathF.Cos(angle) * (wheelR - 8);
            float y1 = cy + MathF.Sin(angle) * (wheelR - 8);
            float x2 = cx + MathF.Cos(nextAngle) * (wheelR - 8);
            float y2 = cy + MathF.Sin(nextAngle) * (wheelR - 8);
            ctx.Line(x1, y1, x2, y2, 12, hueColor);
        }

        ctx.Circle(cx, cy, wheelR - 20, item.ColorValue);
        ctx.CircleStroke(cx, cy, wheelR - 20, 2, Theme.BgLight);

        float dx = ctx.State.MouseX - cx;
        float dy = ctx.State.MouseY - cy;
        float dist = MathF.Sqrt(dx * dx + dy * dy);

        if (dist <= wheelR && dist >= wheelR - 16)
        {
            ctx.SetCursor(CursorType.Pointer);
            if (ctx.State.MouseDown)
            {
                float angle = MathF.Atan2(dy, dx);
                if (angle < 0) angle += MathF.PI * 2;
                float hue = angle * 180f / MathF.PI;
                item.ColorValue = HsvToRgb(hue, 1f, 1f);
                item.OnColorChange?.Invoke(item.ColorValue);
                ctx.RequestRedraw();
                return true;
            }
        }
        return false;
    }

    static bool RenderSubmenu(RenderContext ctx, ContextMenuState state, ContextItem item, float x, float y, float w)
    {
        var hovered = ctx.IsHovered(x, y, w, ItemH);
        if (hovered)
        {
            ctx.Rect(x + 2, y, w - 4, ItemH, Theme.BgLight);
            ctx.SetCursor(CursorType.Pointer);
            state.OpenSubmenuId = item.Id;
        }
        ctx.Text(x + Pad, y + ItemH / 2 + 4, item.Label, 10f, Theme.TextPrimary, FontId.Regular);
        ctx.Text(x + w - Pad - 8, y + ItemH / 2 + 4, "\u25b6", 8f, Theme.TextSecondary, FontId.Regular);

        if (state.OpenSubmenuId == item.Id && item.Submenu != null)
        {
            float subX = x + w - 2;
            float subY = y;
            float subH = 4 + item.Submenu.Count * ItemH;

            if (subX + MenuW > ctx.State.Width) subX = x - MenuW + 2;
            if (subY + subH > ctx.State.Height) subY = ctx.State.Height - subH - 4;

            ctx.Rect(subX, subY, MenuW, subH, Theme.BgSurface);
            ctx.RectStroke(subX, subY, MenuW, subH, 1, Theme.BgLight);

            float subCurY = subY + 2;
            foreach (var subItem in item.Submenu)
                RenderItem(ctx, state, subItem, subX, ref subCurY, MenuW);
        }
        return false;
    }

    static uint HsvToRgb(float h, float s, float v)
    {
        float c = v * s;
        float x = c * (1 - MathF.Abs((h / 60f) % 2 - 1));
        float m = v - c;

        float r, g, b;
        if (h < 60) { r = c; g = x; b = 0; }
        else if (h < 120) { r = x; g = c; b = 0; }
        else if (h < 180) { r = 0; g = c; b = x; }
        else if (h < 240) { r = 0; g = x; b = c; }
        else if (h < 300) { r = x; g = 0; b = c; }
        else { r = c; g = 0; b = x; }

        byte rb = (byte)((r + m) * 255);
        byte gb = (byte)((g + m) * 255);
        byte bb = (byte)((b + m) * 255);
        return (uint)((rb << 24) | (gb << 16) | (bb << 8) | 0xFF);
    }
}
