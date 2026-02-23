// Flex - Declarative flexbox/grid layout builder
// FlexNode is a flat data bag. FlexRenderer (Platform) computes layout via Taffy and draws via RenderContext.

using Keystone.Core.Plugins;
using Keystone.Core.Rendering;

namespace Keystone.Core.UI;

public enum FlexDir { Column, Row }
public enum FlexAlign { Start, Center, End, Stretch, SpaceBetween, SpaceAround, SpaceEvenly }
public enum FlexDisplay { Flex, None, Grid, Block }
public enum FlexPosition { Relative, Absolute }
public enum FlexWrap { NoWrap, Wrap, WrapReverse }
public enum FlexOverflow { Visible, Scroll }

/// <summary>Persistent scroll state — pass the same instance each frame to retain scroll position.</summary>
public class ScrollState
{
    public float Offset;
    public float ContentHeight; // set by renderer
    public float ViewportHeight; // set by renderer

    public float MaxOffset => Math.Max(0, ContentHeight - ViewportHeight);
    public bool CanScroll => ContentHeight > ViewportHeight;

    public void ApplyDelta(float delta, float speed = 40f)
    {
        Offset = Math.Clamp(Offset - delta * speed, 0, MaxOffset);
    }
}

public class FlexNode
{
    // Renderer delegates — wired by Runtime at startup
    public static Action<FlexNode, RenderContext, ButtonRegistry, float, float, float, float>? RenderImpl;
    public static Action<FlexNode, ButtonRegistry, float, float, float, float>? RegisterButtonsImpl;

    // Layout
    public FlexDisplay Display;
    public FlexDir Direction;
    public FlexWrap Wrap;
    public FlexAlign Align;
    public FlexAlign JustifyContent;
    public float Gap;
    public float? GapRow, GapColumn;
    public float Padding;
    public float FlexGrow;
    public float FlexShrink = 1;
    public float? Width, Height, MinWidth, MinHeight, MaxWidth, MaxHeight;
    public float? WidthPercent, HeightPercent;

    // Position
    public FlexPosition Position;
    public float? InsetLeft, InsetTop, InsetRight, InsetBottom;

    // Aspect ratio
    public float? AspectRatio;

    // CSS Grid (container)
    public float[]? GridTemplateColumns;  // >0 = px, <0 = fr, 0 = auto
    public float[]? GridTemplateRows;

    // CSS Grid (child placement)
    public short GridRow, GridColumn;
    public ushort GridRowSpan, GridColumnSpan;

    // Visual
    public uint? BgColor;
    public uint? HoverBgColor;
    public uint? PressedBgColor;
    public float BgRadius;
    public uint? BorderColor;
    public float BorderWidth;
    public string? Text;
    public float FontSize;
    public uint TextColor;
    public FontId Font;
    public TextAlign TextAlign;

    // Icon (drawn as lines, not text)
    public FlexIcon Icon;
    public uint IconColor;

    // Interaction
    public string? Action;
    public CursorType ActionCursor = CursorType.Pointer;

    // Scroll
    public FlexOverflow Overflow;
    public ScrollState? ScrollState;

    // Layout cache (set by FlexRenderer, auto-invalidated on new tree instances)
    public float _lx, _ly, _lw, _lh;
    public bool _layoutValid;
    public float _layoutW, _layoutH;

    // Image (raster data from Bun — base64 PNG decoded to bytes)
    public byte[]? ImageData;
    public SkiaSharp.SKImage? _imageCached;

    // Vector path (SVG path `d` string — rendered natively by SkiaSharp)
    public string? SvgPath;
    public uint? PathFillColor;
    public uint? PathStrokeColor;
    public float PathStrokeWidth;
    public float PathViewBoxW, PathViewBoxH;

    // Text input
    public TextEntry? TextInput;

    // Web view (WKWebView slot — FlexRenderer positions a WKWebView at this node's rect instead of painting)
    public string? WebViewComponent;  // Bun-served component (URL built from BunPort)
    public string? WebViewUrl;        // External URL (loaded directly)

    // Children
    public List<FlexNode>? Children;

    // Builder
    public FlexNode Child(FlexNode child)
    {
        Children ??= new();
        Children.Add(child);
        return this;
    }

    public FlexNode AddChildren(IEnumerable<FlexNode> children)
    {
        Children ??= new();
        Children.AddRange(children);
        return this;
    }

    // Render this tree — delegates to Platform/Taffy
    public void Render(RenderContext ctx, ButtonRegistry buttons, float x, float y, float w, float h)
        => RenderImpl?.Invoke(this, ctx, buttons, x, y, w, h);

    // Register buttons only (no GPU rendering) — for cached scene replay
    public void RegisterButtons(ButtonRegistry buttons, float x, float y, float w, float h)
        => RegisterButtonsImpl?.Invoke(this, buttons, x, y, w, h);
}

// Static builder API — shorthand constructors, each returns a FlexNode with fields set
public static class Flex
{
    /// <summary>
    /// Resolves web component URLs — wired by ApplicationRuntime at startup.
    /// Returns the localhost URL for a named web component served by the Bun runtime.
    /// </summary>
    public static Func<string, string?>? WebComponentResolver;

    // Match CSS flexbox defaults: column children stretch across cross-axis unless overridden.
    public static FlexNode Column(float gap = 0, float pad = 0, float grow = 0,
        FlexAlign align = FlexAlign.Stretch, FlexAlign justify = FlexAlign.Start)
        => new() { Direction = FlexDir.Column, Gap = gap, Padding = pad, FlexGrow = grow, Align = align, JustifyContent = justify };

    public static FlexNode Row(float gap = 0, float pad = 0, float grow = 0,
        FlexAlign align = FlexAlign.Center, FlexAlign justify = FlexAlign.Start,
        FlexWrap wrap = FlexWrap.NoWrap)
        => new() { Direction = FlexDir.Row, Gap = gap, Padding = pad, FlexGrow = grow, Align = align, JustifyContent = justify, Wrap = wrap };

    public static FlexNode Text(string value, float size, uint color,
        FontId font = FontId.Regular, TextAlign align = TextAlign.Left)
        => new() { Text = value, FontSize = size, TextColor = color, Font = font, TextAlign = align };

    public static FlexNode Button(string label, string action, ButtonRegistry buttons,
        float grow = 0, float fontSize = 14, float minH = 36, bool enabled = true,
        uint? bg = null, uint? bgHover = null)
        => new()
        {
            Text = label, FontSize = fontSize, TextColor = enabled ? Colors.TextPrimary : Colors.TextSecondary,
            TextAlign = TextAlign.Center, Font = FontId.Regular,
            BgColor = bg ?? Colors.BgMedium, BgRadius = 5, MinHeight = minH,
            HoverBgColor = bgHover,
            FlexGrow = grow, Action = enabled ? action : null
        };

    public static FlexNode Panel(uint bgColor, float pad = 0, float gap = 0, float radius = 6, float grow = 0)
        => new() { Direction = FlexDir.Column, BgColor = bgColor, BgRadius = radius, Padding = pad, Gap = gap, FlexGrow = grow };

    public static FlexNode Spacer()
        => new() { FlexGrow = 1 };

    public static FlexNode Empty()
        => new();

    public static FlexNode Sized(float? w = null, float? h = null)
        => new() { Width = w, Height = h };

    /// <summary>CSS Grid container. Tracks: >0 = px, &lt;0 = fr (abs), 0 = auto.</summary>
    public static FlexNode Grid(float[] columns, float[]? rows = null, float gap = 0, float pad = 0, float grow = 0)
        => new()
        {
            Display = FlexDisplay.Grid, Gap = gap, Padding = pad, FlexGrow = grow,
            GridTemplateColumns = columns, GridTemplateRows = rows
        };

    /// <summary>Position a child absolutely within its parent.</summary>
    public static FlexNode Absolute(float? left = null, float? top = null, float? right = null, float? bottom = null)
        => new()
        {
            Position = FlexPosition.Absolute,
            InsetLeft = left, InsetTop = top, InsetRight = right, InsetBottom = bottom
        };

    /// <summary>Scrollable column container. Pass a persistent ScrollState to retain position across frames.</summary>
    public static FlexNode Scrollable(ScrollState scroll, float gap = 0, float pad = 0, float grow = 1)
        => new()
        {
            Direction = FlexDir.Column, Gap = gap, Padding = pad, FlexGrow = grow,
            Align = FlexAlign.Stretch, Overflow = FlexOverflow.Scroll, ScrollState = scroll
        };

    /// <summary>Terminal-style text input field. Pass a persistent TextEntry instance.</summary>
    public static FlexNode TextInput(TextEntry entry, string placeholder = "", float fontSize = 13, float grow = 1)
        => new()
        {
            TextInput = entry, FlexGrow = grow, MinHeight = 28, FontSize = fontSize,
            Text = entry.IsFocused ? entry.Buffer : (entry.Buffer.Length > 0 ? entry.Buffer : placeholder),
            TextColor = entry.IsFocused || entry.Buffer.Length > 0 ? 0xccccddff : 0x666677ff,
            BgColor = 0x0e0e14ff, BgRadius = 3, Padding = 6,
            BorderColor = entry.IsFocused ? 0x4a6fa5ffu : 0x333344ffu, BorderWidth = 1,
            Action = $"__textinput_focus:{entry.Tag ?? ""}"
        };

    /// <summary>Web component — served by the Bun runtime's HTTP server, rendered in a WKWebView slot.
    /// The component must exist in bun/web/ and export a mount(root) function.</summary>
    public static FlexNode Web(string component)
    {
        var url = WebComponentResolver?.Invoke(component);
        if (url != null)
            return new FlexNode { FlexGrow = 1, WebViewComponent = component, WebViewUrl = url };
        return Empty();
    }

    /// <summary>External URL WebView slot — WKWebView loads the given URL at this node's rect.</summary>
    public static FlexNode WebExternal(string url)
        => new() { FlexGrow = 1, WebViewUrl = url };
}
