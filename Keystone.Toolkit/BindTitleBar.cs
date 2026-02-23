// Bind Title Bar - For bound window groups
// Renders a wider title bar with close bind, drag, and float toggle

using Keystone.Core.Rendering;
using Keystone.Core.UI;

namespace Keystone.Toolkit;

public static class BindTitleBar
{
    public static float Height => Theme.BindTitleBarHeight;
    private const float FontSize = 18f;
    private const float BtnPad = 10f;

    public static void Render(RenderContext ctx, ButtonRegistry buttons, float x, float y, float w, string title, bool alwaysOnTop)
    {
        var btnSize = Theme.BtnSize + 4;
        var cornerR = Theme.CornerRadius;

        ctx.Rect(x, y, w, Height, Theme.BgMedium);

        // Close button on left
        float btnX = x + BtnPad;
        Buttons.Close(ctx, buttons, btnX, y + BtnPad, btnSize, "close_bind", cornerR);

        // Always-on-top toggle on right
        var starX = x + w - BtnPad - btnSize;
        Buttons.Icon(ctx, buttons, starX, y + BtnPad, btnSize, "\u2605", "toggle_float", 14f, cornerR,
                     bgNormal: alwaysOnTop ? Theme.AccentBright : 0x333340ff);

        // Drag region (between close and float toggle)
        Buttons.DragRegion(buttons, x + 60, y, w - 60 - btnSize - BtnPad, Height, "drag_start");

        // Title centered
        ctx.Text(x + w / 2, y + Height / 2 + 6, title, FontSize, Theme.TextPrimary, FontId.Bold, TextAlign.Center);

        ctx.Line(x, y + Height - 1, x + w, y + Height - 1, 1, Theme.BgLight);
    }
}
