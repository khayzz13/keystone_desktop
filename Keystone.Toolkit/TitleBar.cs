// Title Bar - Fixed height title bar with tabs, close, minimize, float toggle
// Build() returns a FlexNode tree; Render() is a thin wrapper for immediate-mode callers.

using Keystone.Core;
using Keystone.Core.Plugins;
using Keystone.Core.Rendering;
using Keystone.Core.UI;

namespace Keystone.Toolkit;

public struct TabInfo
{
    public string Id;
    public string Title;
    public bool IsActive;
}

public static class TitleBar
{
    public static float Height => Theme.TitleBarHeight;
    private const float FontSize = 15f;
    private const float BtnPad = 10f;

    /// <summary>Build a FlexNode tree for the title bar (for scene graph / FlexGroupNode).</summary>
    public static FlexNode Build(FrameState state, float w, bool showClose = true, bool showMinimize = true)
    {
        var btnSize = Theme.BtnSize;
        var cornerR = Theme.CornerRadius;
        var bgColor = state.IsSelectedForBind ? Theme.AccentBright : Theme.BgMedium;

        var row = Flex.Row(gap: 6, pad: 0, align: FlexAlign.Center);
        row.Height = Height;
        row.BgColor = bgColor;
        row.Padding = BtnPad;
        row.Action = "drag_start";
        row.ActionCursor = CursorType.Default;

        if (state.BindModeActive)
        {
            row.Action = "bind_select";
            row.ActionCursor = CursorType.Pointer;
            return row;
        }

        // Left buttons: close, minimize
        if (showClose)
        {
            row.Child(new FlexNode
            {
                Width = btnSize, Height = btnSize, BgRadius = cornerR,
                BgColor = Theme.BgMedium, HoverBgColor = Theme.Danger,
                Icon = FlexIcon.Close, IconColor = Theme.TextSecondary,
                Action = "close_window"
            });
        }

        if (showMinimize)
        {
            row.Child(new FlexNode
            {
                Width = btnSize, Height = btnSize, BgRadius = cornerR,
                BgColor = 0x333340ff, HoverBgColor = Theme.BgLight,
                Icon = FlexIcon.Minimize, IconColor = Theme.TextPrimary,
                Action = "minimize"
            });
        }

        // Tab area
        TabInfo[] tabs;
        if (state.IsInTabGroup && state.TabIds != null && state.TabTitles != null)
        {
            tabs = new TabInfo[state.TabIds.Length];
            for (int i = 0; i < tabs.Length; i++)
                tabs[i] = new TabInfo { Id = state.TabIds[i], Title = state.TabTitles[i], IsActive = state.TabIds[i] == state.ActiveTabId };
        }
        else
        {
            tabs = [new TabInfo { Id = state.WindowId.ToString(), Title = state.WindowTitle, IsActive = true }];
        }

        // Tab bar
        var tabContainer = Flex.Row(gap: 3, align: FlexAlign.End);
        tabContainer.FlexGrow = 1;
        tabContainer.Height = Height - 7;

        if (state.IsInTabGroup && tabs.Length > 1)
            tabContainer.BgColor = 0x222228ff;

        foreach (var tab in tabs)
        {
            var tabNode = Flex.Row(gap: 0, align: FlexAlign.Center, justify: FlexAlign.Center);
            tabNode.MinWidth = 80;
            tabNode.MaxWidth = 200;
            tabNode.FlexGrow = 0;
            tabNode.Height = Height - 7;
            tabNode.BgRadius = 6;
            tabNode.BgColor = tab.IsActive ? Theme.BgSurface : Theme.BgMedium;
            tabNode.HoverBgColor = tab.IsActive ? Theme.BgSurface : 0x333340ff;
            tabNode.Action = $"tab_select:{tab.Id}";

            tabNode.Child(new FlexNode
            {
                Text = tab.Title, FontSize = FontSize,
                TextColor = tab.IsActive ? Theme.TextPrimary : Theme.TextSecondary,
                Font = tab.IsActive ? FontId.Bold : FontId.Regular,
                TextAlign = TextAlign.Center, FlexGrow = 1
            });

            // Tab close button (multi-tab only)
            if (tabs.Length > 1)
            {
                tabNode.Child(new FlexNode
                {
                    Text = "\u00d7", FontSize = 13, Font = FontId.Bold,
                    TextColor = Theme.TextSecondary, TextAlign = TextAlign.Center,
                    Width = 16, Height = 16,
                    HoverBgColor = 0xef535044, BgRadius = 8,
                    Action = $"tab_close:{tab.Id}"
                });
            }

            tabContainer.Child(tabNode);
        }

        // "+" button for tab groups
        if (state.IsInTabGroup)
        {
            tabContainer.Child(new FlexNode
            {
                Text = "+", FontSize = 16, Font = FontId.Bold,
                TextColor = Theme.TextSecondary, TextAlign = TextAlign.Center,
                Width = 26, Height = 26, BgRadius = 4,
                BgColor = 0x2a2a35ff, HoverBgColor = 0x3a3a45ff,
                Action = $"spawn:{state.WindowType ?? "default"}"
            });
        }

        row.Child(tabContainer);

        // Right buttons
        if (state.IsInBind)
        {
            var popoutAction = $"popout:{state.BindContainerId}:{state.BindSlotIndex}";
            row.Child(new FlexNode
            {
                Text = "\u2197", FontSize = 12, TextColor = Theme.TextPrimary,
                TextAlign = TextAlign.Center,
                Width = btnSize, Height = btnSize, BgRadius = cornerR,
                BgColor = Theme.BgMedium, HoverBgColor = Theme.BgLight,
                Action = popoutAction
            });
        }

        if (!state.IsInBind)
        {
            var isFloating = state.AlwaysOnTop;
            row.Child(new FlexNode
            {
                Text = "\u2605", FontSize = 12, TextColor = Theme.TextPrimary,
                TextAlign = TextAlign.Center,
                Width = btnSize, Height = btnSize, BgRadius = cornerR,
                BgColor = isFloating ? Theme.AccentBright : 0x333340ff,
                HoverBgColor = Theme.BgLight,
                Action = "toggle_float"
            });
        }

        return row;
    }

    /// <summary>Immediate-mode render (backward compat for windows not yet on scene graph).</summary>
    public static void Render(RenderContext ctx, ButtonRegistry buttons, float x, float y, float w,
                              bool showClose = true, bool showMinimize = true)
    {
        var node = Build(ctx.State, w, showClose, showMinimize);
        node.Render(ctx, buttons, x, y, w, Height);
        ctx.Line(x, y + Height - 1, x + w, y + Height - 1, 1, Theme.BgLight);
    }
}
