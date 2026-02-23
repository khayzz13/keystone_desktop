// Buttons - Immediate-mode button rendering with HitTest support
// Provides button builders that register hitboxes for click handling

using Keystone.Core;
using Keystone.Core.Plugins;
using Keystone.Core.Rendering;
using Keystone.Core.UI;

namespace Keystone.Toolkit;

public static class Buttons
{
    /// <summary>Render a standard rounded button with text. Returns rendered width.</summary>
    public static float Text(RenderContext ctx, ButtonRegistry reg, float x, float y, float h,
                             string label, string action, float fontSize, float cornerR = 4f,
                             float? fixedW = null, uint? bgNormal = null, uint? bgHover = null)
    {
        var w = fixedW ?? label.Length * fontSize * 0.6f + h * 0.6f;
        reg.Add(x, y, w, h, action);

        var hovered = ctx.IsHovered(x, y, w, h);
        var bg = hovered ? (bgHover ?? Theme.BgLight) : (bgNormal ?? Theme.BgMedium);
        ctx.RoundedRect(x, y, w, h, cornerR, bg);
        ctx.TextCentered(x, y + h / 2 + fontSize * 0.35f, w, label, fontSize, Theme.TextPrimary);
        return w;
    }

    /// <summary>Render an icon button (single character/emoji)</summary>
    public static void Icon(RenderContext ctx, ButtonRegistry reg, float x, float y, float size,
                            string icon, string action, float fontSize, float cornerR = 4f,
                            uint? bgNormal = null, uint? bgHover = null)
    {
        reg.Add(x, y, size, size, action);
        var hovered = ctx.IsHovered(x, y, size, size);
        var bg = hovered ? (bgHover ?? Theme.BgLight) : (bgNormal ?? Theme.BgMedium);
        ctx.RoundedRect(x, y, size, size, cornerR, bg);
        ctx.TextCentered(x, y + size / 2 + fontSize * 0.35f, size, icon, fontSize, Theme.TextPrimary);
    }

    /// <summary>Render a close button (X icon)</summary>
    public static void Close(RenderContext ctx, ButtonRegistry reg, float x, float y, float size,
                             string action = "close_window", float cornerR = 4f)
    {
        reg.Add(x, y, size, size, action);
        var hovered = ctx.IsHovered(x, y, size, size);
        ctx.RoundedRect(x, y, size, size, cornerR, hovered ? Theme.Danger : Theme.BgMedium);

        var inset = size * 0.3f;
        var color = hovered ? Theme.TextPrimary : Theme.TextSecondary;
        ctx.Line(x + inset, y + inset, x + size - inset, y + size - inset, 2f, color);
        ctx.Line(x + size - inset, y + inset, x + inset, y + size - inset, 2f, color);
    }

    /// <summary>Render a minimize button (- icon)</summary>
    public static void Minimize(RenderContext ctx, ButtonRegistry reg, float x, float y, float size,
                                string action = "minimize", float cornerR = 4f)
    {
        reg.Add(x, y, size, size, action);
        var hovered = ctx.IsHovered(x, y, size, size);
        ctx.RoundedRect(x, y, size, size, cornerR, hovered ? Theme.BgLight : 0x333340ff);
        ctx.Line(x + size * 0.25f, y + size / 2, x + size * 0.75f, y + size / 2, 2f, Theme.TextPrimary);
    }

    /// <summary>Render a toggle button (on/off state). Returns rendered width.</summary>
    public static float Toggle(RenderContext ctx, ButtonRegistry reg, float x, float y, float w, float h,
                               string label, string action, bool active, float fontSize, float cornerR = 4f)
    {
        reg.Add(x, y, w, h, action);
        var hovered = ctx.IsHovered(x, y, w, h);
        var bg = active ? Theme.Accent : (hovered ? Theme.BgLight : Theme.BgMedium);
        ctx.RoundedRect(x, y, w, h, cornerR, bg);
        ctx.TextCentered(x, y + h / 2 + fontSize * 0.35f, w, label, fontSize, Theme.TextPrimary);
        return w;
    }

    /// <summary>Render a tab button. Returns rendered width.</summary>
    public static float Tab(RenderContext ctx, ButtonRegistry reg, float x, float y, float h,
                            string label, string action, bool active, float fontSize, float cornerR = 2f)
    {
        var w = label.Length * fontSize * 0.6f + 8f;
        reg.Add(x, y, w, h, action);
        var hovered = ctx.IsHovered(x, y, w, h);
        var bg = active ? Theme.BgMedium : (hovered ? 0x333340ffu : 0x2a2a32ffu);
        ctx.RoundedRect(x, y, w, h, cornerR, bg);
        ctx.TextCentered(x, y + h / 2 + fontSize * 0.35f, w, label, fontSize,
                        active ? Theme.TextPrimary : Theme.TextSecondary);
        return w;
    }

    /// <summary>Render a dropdown menu item</summary>
    public static void MenuItem(RenderContext ctx, ButtonRegistry reg, float x, float y, float w, float h,
                                string label, string action, bool selected, float fontSize)
    {
        reg.Add(x, y, w, h, action);
        var hovered = ctx.IsHovered(x, y, w, h);
        if (hovered) ctx.Rect(x, y, w, h, Theme.BgLight);
        ctx.Text(x + 4, y + h / 2 + fontSize * 0.35f, label, fontSize,
                selected ? Theme.AccentBright : Theme.TextPrimary);
    }

    /// <summary>Register a drag region (no visual, just hitbox)</summary>
    public static void DragRegion(ButtonRegistry reg, float x, float y, float w, float h,
                                  string action = "drag_start")
    {
        reg.Add(x, y, w, h, action, CursorType.Move);
    }
}
