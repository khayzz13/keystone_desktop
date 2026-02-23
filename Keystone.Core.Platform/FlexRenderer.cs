// FlexRenderer - Bridges FlexNode tree → Taffy layout → RenderContext drawing
// Layout caching: on cache hit, skips all Taffy FFI (tree alloc, node creation,
// text measurement, layout compute, result queries) and renders from cached positions.

using System;
using Keystone.Core;
using Keystone.Core.Rendering;
using Keystone.Core.UI;

namespace Keystone.Core.Platform;

public static class FlexRenderer
{
    public static void Render(FlexNode root, RenderContext ctx, ButtonRegistry buttons,
        float x, float y, float w, float h)
    {
        // Cache hit: same tree instance + same viewport → skip Taffy entirely
        if (root._layoutValid && root._layoutW == w && root._layoutH == h)
        {
            RenderNode(root, IntPtr.Zero, 0, ctx, buttons, x, y, true);
            return;
        }

        // Full layout pass: build Taffy tree, compute, cache results on FlexNodes
        var originalWidth = root.Width;
        var originalHeight = root.Height;
        if (!root.Width.HasValue && !root.WidthPercent.HasValue) root.Width = w;
        if (!root.Height.HasValue && !root.HeightPercent.HasValue) root.Height = h;

        var tree = Taffy.layout_tree_new();
        try
        {
            var rootId = BuildTree(root, tree, ctx);
            Taffy.layout_compute(tree, rootId, w, h);
            RenderNode(root, tree, rootId, ctx, buttons, x, y, false);
        }
        finally { Taffy.layout_tree_free(tree); }

        root.Width = originalWidth;
        root.Height = originalHeight;
        root._layoutValid = true;
        root._layoutW = w;
        root._layoutH = h;
    }

    static ulong BuildTree(FlexNode node, IntPtr tree, RenderContext ctx)
    {
        var id = Taffy.layout_new_node(tree);

        // Display mode (flex/grid/none/block)
        if (node.Display != FlexDisplay.Flex)
            Taffy.layout_set_display(tree, id, (byte)node.Display);

        // Direction (only relevant for flex)
        if (node.Display != FlexDisplay.Grid)
            Taffy.layout_set_flex_direction(tree, id, node.Direction == FlexDir.Row
                ? (byte)Taffy.FlexDirection.Row : (byte)Taffy.FlexDirection.Column);

        // Wrap
        if (node.Wrap != FlexWrap.NoWrap)
            Taffy.layout_set_flex_wrap(tree, id, (byte)node.Wrap);

        // Alignment
        Taffy.layout_set_align_items(tree, id, MapAlign(node.Align));
        Taffy.layout_set_justify_content(tree, id, MapJustify(node.JustifyContent));

        // Flex
        if (node.FlexGrow > 0) Taffy.layout_set_flex_grow(tree, id, node.FlexGrow);
        if (node.FlexShrink != 1) Taffy.layout_set_flex_shrink(tree, id, node.FlexShrink);

        // Padding
        if (node.Padding > 0) Taffy.layout_set_padding(tree, id, (byte)Taffy.Edge.All, node.Padding);

        // Gap (per-axis takes priority over uniform)
        if (node.GapRow.HasValue)
            Taffy.layout_set_gap_row(tree, id, node.GapRow.Value);
        if (node.GapColumn.HasValue)
            Taffy.layout_set_gap_column(tree, id, node.GapColumn.Value);
        if (!node.GapRow.HasValue && !node.GapColumn.HasValue && node.Gap > 0)
            Taffy.layout_set_gap_all(tree, id, node.Gap);

        // Fixed dimensions
        if (node.Width.HasValue) Taffy.layout_set_width(tree, id, node.Width.Value);
        if (node.Height.HasValue) Taffy.layout_set_height(tree, id, node.Height.Value);
        if (node.MinWidth.HasValue) Taffy.layout_set_min_width(tree, id, node.MinWidth.Value);
        if (node.MinHeight.HasValue) Taffy.layout_set_min_height(tree, id, node.MinHeight.Value);
        if (node.MaxWidth.HasValue) Taffy.layout_set_max_width(tree, id, node.MaxWidth.Value);
        if (node.MaxHeight.HasValue) Taffy.layout_set_max_height(tree, id, node.MaxHeight.Value);

        // Percentage dimensions
        if (node.WidthPercent.HasValue) Taffy.layout_set_width_percent(tree, id, node.WidthPercent.Value);
        if (node.HeightPercent.HasValue) Taffy.layout_set_height_percent(tree, id, node.HeightPercent.Value);

        // Position
        if (node.Position == FlexPosition.Absolute)
        {
            Taffy.layout_set_position_type(tree, id, (byte)Taffy.PositionType.Absolute);
            if (node.InsetLeft.HasValue) Taffy.layout_set_position(tree, id, (byte)Taffy.Edge.Left, node.InsetLeft.Value);
            if (node.InsetTop.HasValue) Taffy.layout_set_position(tree, id, (byte)Taffy.Edge.Top, node.InsetTop.Value);
            if (node.InsetRight.HasValue) Taffy.layout_set_position(tree, id, (byte)Taffy.Edge.Right, node.InsetRight.Value);
            if (node.InsetBottom.HasValue) Taffy.layout_set_position(tree, id, (byte)Taffy.Edge.Bottom, node.InsetBottom.Value);
        }

        // Aspect ratio
        if (node.AspectRatio.HasValue) Taffy.layout_set_aspect_ratio(tree, id, node.AspectRatio.Value);

        // Overflow (scroll containers let children overflow their bounds)
        if (node.Overflow == FlexOverflow.Scroll)
            Taffy.layout_set_overflow(tree, id, 2); // Scroll

        // CSS Grid template
        if (node.GridTemplateColumns != null)
            Taffy.layout_set_grid_template_columns(tree, id, node.GridTemplateColumns, (nuint)node.GridTemplateColumns.Length);
        if (node.GridTemplateRows != null)
            Taffy.layout_set_grid_template_rows(tree, id, node.GridTemplateRows, (nuint)node.GridTemplateRows.Length);

        // CSS Grid placement
        if (node.GridRow != 0 || node.GridColumn != 0)
            Taffy.layout_set_grid_placement(tree, id, node.GridRow, node.GridColumn, node.GridRowSpan, node.GridColumnSpan);

        // Text leaf — measure and set fixed size
        if (node.Text != null && (node.Children == null || node.Children.Count == 0))
        {
            float tw = ctx.MeasureText(node.Text, node.FontSize, node.Font);
            float th = node.FontSize * 1.4f;

            // Only auto-size text width for intrinsic leaves.
            // If FlexGrow is set, width should remain flexible so rows can scale/fill.
            if (!node.Width.HasValue && !node.WidthPercent.HasValue && node.FlexGrow <= 0)
            {
                float pad = (node.BgColor.HasValue || node.HoverBgColor.HasValue || node.PressedBgColor.HasValue)
                    ? node.FontSize * 1.5f : 0;
                Taffy.layout_set_width(tree, id, tw + pad);
            }
            if (!node.Height.HasValue && !node.MinHeight.HasValue && !node.HeightPercent.HasValue)
                Taffy.layout_set_height(tree, id, th);
        }

        // Children
        if (node.Children != null)
        {
            for (int i = 0; i < node.Children.Count; i++)
            {
                var childId = BuildTree(node.Children[i], tree, ctx);
                Taffy.layout_add_child(tree, id, childId);
            }
        }

        return id;
    }

    static void RenderNode(FlexNode node, IntPtr tree, ulong id, RenderContext ctx,
        ButtonRegistry buttons, float parentX, float parentY, bool cached,
        bool hasClip = false, float clipX = 0, float clipY = 0, float clipW = 0, float clipH = 0)
    {
        float lx, ly, w, h;
        if (cached)
        {
            lx = node._lx; ly = node._ly; w = node._lw; h = node._lh;
        }
        else
        {
            Taffy.layout_get_result(tree, id, out lx, out ly, out w, out h);
            node._lx = lx; node._ly = ly; node._lw = w; node._lh = h;
        }

        float x = parentX + lx;
        float y = parentY + ly;

        // WebView slot — record request for ManagedWindow to process after rendering
        // isSlot=true: Bun component → shared host WebView with CSS-positioned slots (CSS top-left origin)
        // isSlot=false: external URL → dedicated WKWebView (AppKit bottom-left origin)
        if (node.WebViewUrl != null)
        {
            var scale = ctx.State.ScaleFactor;
            var key = node.WebViewComponent ?? node.WebViewUrl;
            var isSlot = node.WebViewComponent != null;
            // Slots use CSS coordinates (top-left origin) — no Y flip
            // External WebViews use AppKit CGRect (bottom-left origin) — flip Y
            var wy = isSlot ? y / scale : (ctx.State.Height - y - h) / scale;
            ctx.State.WebViewRequests ??= new();
            ctx.State.WebViewRequests.Add((key, node.WebViewUrl,
                x / scale, wy, w / scale, h / scale, isSlot));
            return;
        }

        bool interactive = node.Action != null;
        bool hovered = interactive && ctx.IsHovered(x, y, w, h);
        bool pressed = interactive && ctx.IsMouseDown(x, y, w, h);

        // Background (base + optional hover/pressed states)
        uint? bgColor = node.BgColor;
        if (hovered && node.HoverBgColor.HasValue) bgColor = node.HoverBgColor.Value;
        if (pressed && node.PressedBgColor.HasValue) bgColor = node.PressedBgColor.Value;

        if (bgColor.HasValue)
        {
            if (node.BgRadius > 0)
                ctx.RoundedRect(x, y, w, h, node.BgRadius, bgColor.Value);
            else
                ctx.Rect(x, y, w, h, bgColor.Value);
        }

        // Optional stroke border
        if (node.BorderColor.HasValue && node.BorderWidth > 0)
        {
            if (node.BgRadius > 0)
                ctx.RoundedRectStroke(x, y, w, h, node.BgRadius, node.BorderWidth, node.BorderColor.Value);
            else
                ctx.RectStroke(x, y, w, h, node.BorderWidth, node.BorderColor.Value);
        }

        // Image (raster from Bun canvas)
        if (node.ImageData != null)
        {
            node._imageCached ??= SkiaSharp.SKImage.FromEncodedData(node.ImageData);
            if (node._imageCached != null)
                ctx.DrawImage(node._imageCached, x, y, w, h);
        }

        // SVG path (vector from Bun)
        if (node.SvgPath != null)
        {
            float sx = node.PathViewBoxW > 0 ? w / node.PathViewBoxW : 1;
            float sy = node.PathViewBoxH > 0 ? h / node.PathViewBoxH : 1;
            ctx.PushTransform(x, y, scaleX: sx, scaleY: sy);
            var pathCmds = SvgPathParser.Parse(node.SvgPath);
            if (node.PathFillColor.HasValue)
            {
                ctx.PathBegin();
                foreach (var pc in pathCmds) pc(ctx);
                ctx.PathFill(node.PathFillColor.Value);
            }
            if (node.PathStrokeColor.HasValue)
            {
                ctx.PathBegin();
                foreach (var pc in pathCmds) pc(ctx);
                ctx.PathStroke(node.PathStrokeWidth, node.PathStrokeColor.Value);
            }
            ctx.PopTransform();
        }

        // Icon (drawn as lines)
        if (node.Icon == FlexIcon.Close)
        {
            float inset = w * 0.3f;
            ctx.Line(x + inset, y + inset, x + w - inset, y + h - inset, 2f, node.IconColor);
            ctx.Line(x + w - inset, y + inset, x + inset, y + h - inset, 2f, node.IconColor);
        }
        else if (node.Icon == FlexIcon.Minimize)
        {
            ctx.Line(x + w * 0.25f, y + h / 2, x + w * 0.75f, y + h / 2, 2f, node.IconColor);
        }

        // Text (with cursor for active text inputs)
        if (node.Text != null)
        {
            float textY = y + h / 2 + node.FontSize * 0.35f;
            float textX = x + (node.TextInput != null ? node.Padding : 0);

            if (node.TextAlign == TextAlign.Center)
                ctx.TextCentered(x, textY, w, node.Text, node.FontSize, node.TextColor, node.Font);
            else if (node.TextAlign == TextAlign.Right)
                ctx.Text(x + w, textY, node.Text, node.FontSize, node.TextColor, node.Font, TextAlign.Right);
            else
                ctx.Text(textX, textY, node.Text, node.FontSize, node.TextColor, node.Font);

            // Blinking cursor for focused text inputs
            if (node.TextInput is { IsFocused: true })
            {
                var entry = node.TextInput;
                string beforeCursor = entry.Buffer[..Math.Min(entry.Cursor, entry.Buffer.Length)];
                float cursorX = textX + ctx.MeasureText(beforeCursor, node.FontSize, node.Font);
                bool blink = (ctx.State.TimeMs / 500) % 2 == 0;
                if (blink)
                    ctx.Line(cursorX, y + 4, cursorX, y + h - 4, 1.5f, 0x4a6fa5ff);
                ctx.RequestRedraw(); // keep animating for blink
            }
        }

        // Hit test
        if (node.Action != null)
        {
            if (!hasClip)
            {
                buttons.Add(x, y, w, h, node.Action, node.ActionCursor);
            }
            else if (TryIntersectRect(x, y, w, h, clipX, clipY, clipW, clipH, out float bx, out float by, out float bw, out float bh))
            {
                buttons.Add(bx, by, bw, bh, node.Action, node.ActionCursor);
            }
        }

        // Children
        if (node.Children != null)
        {
            if (node.Overflow == FlexOverflow.Scroll && node.ScrollState != null)
                RenderScrollChildren(node, tree, id, ctx, buttons, x, y, w, h, cached, hasClip, clipX, clipY, clipW, clipH);
            else
            {
                for (int i = 0; i < node.Children.Count; i++)
                {
                    ulong childId = cached ? 0 : Taffy.layout_get_child(tree, id, (nuint)i);
                    RenderNode(node.Children[i], tree, childId, ctx, buttons, x, y, cached, hasClip, clipX, clipY, clipW, clipH);
                }
            }
        }
    }

    static void RenderScrollChildren(FlexNode node, IntPtr tree, ulong id, RenderContext ctx,
        ButtonRegistry buttons, float x, float y, float w, float h, bool cached,
        bool hasClip, float clipX, float clipY, float clipW, float clipH)
    {
        var ss = node.ScrollState!;
        float viewportX = x, viewportY = y, viewportW = w, viewportH = h;
        bool hasViewport = true;
        if (hasClip)
            hasViewport = TryIntersectRect(x, y, w, h, clipX, clipY, clipW, clipH,
                out viewportX, out viewportY, out viewportW, out viewportH);

        // Compute total content height from child layouts
        float contentH = 0;
        for (int i = 0; i < node.Children!.Count; i++)
        {
            float cy, ch;
            if (cached)
            {
                cy = node.Children[i]._ly;
                ch = node.Children[i]._lh;
            }
            else
            {
                var childId = Taffy.layout_get_child(tree, id, (nuint)i);
                Taffy.layout_get_result(tree, childId, out _, out cy, out _, out ch);
            }
            contentH = Math.Max(contentH, cy + ch);
        }
        ss.ContentHeight = contentH;
        ss.ViewportHeight = h;
        ss.Offset = Math.Clamp(ss.Offset, 0, ss.MaxOffset);

        // Handle scroll input when mouse is over this container
        if (hasViewport && ss.CanScroll && ctx.IsHovered(viewportX, viewportY, viewportW, viewportH) && ctx.State.MouseScroll != 0)
            ss.ApplyDelta(ctx.State.MouseScroll);

        // Clip and offset
        ctx.PushClip(x, y, w, h);
        float offsetY = y - ss.Offset;

        for (int i = 0; i < node.Children.Count; i++)
        {
            float cy, ch;
            ulong childId = 0;
            if (cached)
            {
                cy = node.Children[i]._ly;
                ch = node.Children[i]._lh;
            }
            else
            {
                childId = Taffy.layout_get_child(tree, id, (nuint)i);
                Taffy.layout_get_result(tree, childId, out _, out cy, out _, out ch);
            }

            // Cull offscreen children
            float childTop = offsetY + cy;
            if (childTop + ch < y || childTop > y + h)
                continue;

            if (!hasViewport) continue;
            RenderNode(node.Children[i], tree, childId, ctx, buttons, x, offsetY, cached, true, viewportX, viewportY, viewportW, viewportH);
        }

        ctx.PopClip();

        // Scrollbar
        if (ss.CanScroll)
        {
            float ratio = h / contentH;
            float barH = Math.Max(h * ratio, 30);
            float trackH = Math.Max(1, h - barH);
            float barY = y + (ss.Offset / ss.MaxOffset) * trackH;
            bool barHovered = ctx.IsHovered(x + w - 8, y, 8, h);
            ctx.RoundedRect(x + w - 4, barY, 3, barH, 2, barHovered ? 0x888899cc : 0x555566aa);
        }
    }

    static bool TryIntersectRect(
        float x1, float y1, float w1, float h1,
        float x2, float y2, float w2, float h2,
        out float ox, out float oy, out float ow, out float oh)
    {
        float left = Math.Max(x1, x2);
        float top = Math.Max(y1, y2);
        float right = Math.Min(x1 + w1, x2 + w2);
        float bottom = Math.Min(y1 + h1, y2 + h2);
        if (right <= left || bottom <= top)
        {
            ox = oy = ow = oh = 0;
            return false;
        }
        ox = left;
        oy = top;
        ow = right - left;
        oh = bottom - top;
        return true;
    }

    /// <summary>
    /// Walk cached layout tree for button registration only — no GPU rendering.
    /// Used by SceneRenderer on clean FlexGroupNodes to populate ButtonRegistry
    /// without the cost of a full FlexRenderer render pass.
    /// Requires _layoutValid (a previous Render pass must have cached positions).
    /// </summary>
    public static void RegisterButtons(FlexNode root, ButtonRegistry buttons, float x, float y, float w, float h)
    {
        if (!root._layoutValid) return;
        RegisterNode(root, buttons, x, y);
    }

    static void RegisterNode(FlexNode node, ButtonRegistry buttons, float parentX, float parentY,
        bool hasClip = false, float clipX = 0, float clipY = 0, float clipW = 0, float clipH = 0)
    {
        float x = parentX + node._lx;
        float y = parentY + node._ly;
        float w = node._lw;
        float h = node._lh;

        if (node.Action != null)
        {
            if (!hasClip)
                buttons.Add(x, y, w, h, node.Action, node.ActionCursor);
            else if (TryIntersectRect(x, y, w, h, clipX, clipY, clipW, clipH, out float bx, out float by, out float bw, out float bh))
                buttons.Add(bx, by, bw, bh, node.Action, node.ActionCursor);
        }

        if (node.Children == null) return;

        if (node.Overflow == FlexOverflow.Scroll && node.ScrollState != null)
        {
            var ss = node.ScrollState;
            float vx = x, vy = y, vw = w, vh = h;
            bool hasViewport = true;
            if (hasClip)
                hasViewport = TryIntersectRect(x, y, w, h, clipX, clipY, clipW, clipH, out vx, out vy, out vw, out vh);
            if (!hasViewport) return;

            float offsetY = y - ss.Offset;
            for (int i = 0; i < node.Children.Count; i++)
            {
                var child = node.Children[i];
                float childTop = offsetY + child._ly;
                if (childTop + child._lh < y || childTop > y + h) continue;
                RegisterNode(child, buttons, x, offsetY, true, vx, vy, vw, vh);
            }
        }
        else
        {
            for (int i = 0; i < node.Children.Count; i++)
                RegisterNode(node.Children[i], buttons, x, y, hasClip, clipX, clipY, clipW, clipH);
        }
    }

    static byte MapAlign(FlexAlign a) => a switch
    {
        FlexAlign.Center => (byte)Taffy.AlignItems.Center,
        FlexAlign.End => (byte)Taffy.AlignItems.FlexEnd,
        FlexAlign.Stretch => (byte)Taffy.AlignItems.Stretch,
        _ => (byte)Taffy.AlignItems.FlexStart
    };

    static byte MapJustify(FlexAlign a) => a switch
    {
        FlexAlign.Center => (byte)Taffy.JustifyContent.Center,
        FlexAlign.End => (byte)Taffy.JustifyContent.FlexEnd,
        FlexAlign.SpaceBetween => (byte)Taffy.JustifyContent.SpaceBetween,
        FlexAlign.SpaceAround => (byte)Taffy.JustifyContent.SpaceAround,
        FlexAlign.SpaceEvenly => (byte)Taffy.JustifyContent.SpaceEvenly,
        _ => (byte)Taffy.JustifyContent.FlexStart
    };
}
