// SceneNode - Retained scene graph node types
// Plugins build SceneNode trees. Framework diffs against previous frame,
// caches unchanged subtrees as SKPicture, only re-renders dirty nodes.

using Keystone.Core.UI;
using SkiaSharp;

namespace Keystone.Core.Rendering;

// Id > 0 = stable ID diffing. Id == 0 = position-based diffing.
public abstract class SceneNode
{
    public int Id;
    public bool Dirty = true;
    public SKPicture? Cache;
    // Optional hit testing
    public string? HitAction;
    public CursorType HitCursor;
}

public class GroupNode : SceneNode
{
    public SceneNode[] Children = Array.Empty<SceneNode>();
    public float X, Y;
    public SKRect? Clip;
}

public class RectNode : SceneNode
{
    public float X, Y, W, H;
    public uint Color;
    public float Radius;
}

public class TextNode : SceneNode
{
    public float X, Y;
    public string Text = "";
    public float Size;
    public uint Color;
    public FontId Font;
    public TextAlign Align;
}

public class NumberNode : SceneNode
{
    public float X, Y;
    public double Value;
    public int Decimals;
    public float Size;
    public uint Color;
    public FontId Font;
    public TextAlign Align;
}

public class LineNode : SceneNode
{
    public float X1, Y1, X2, Y2;
    public float Width;
    public uint Color;
}

public class ImageNode : SceneNode
{
    public SKImage? Image;
    public float X, Y, W, H;
}

public class PointsNode : SceneNode
{
    public SKPoint[] Points = Array.Empty<SKPoint>();
    public int Count;
    public float Width;
    public uint Color;
}

public class PathNode : SceneNode
{
    public SKPath? Path;
    public uint FillColor;
    public uint StrokeColor;
    public float StrokeWidth;
}

public class LayerNode : GroupNode
{
    public float Alpha = 1f;
    public BlendMode Blend;
}

/// <summary>
/// Embeds a FlexNode tree into the scene graph.
/// Layout via Taffy, rendering via FlexRenderer.
/// Scene graph provides SKPicture caching around the entire Flex subtree.
/// Dirty when: Root reference changed OR Root._layoutValid == false.
/// </summary>
public class FlexGroupNode : SceneNode
{
    public FlexNode? Root;
    public float X, Y, W, H;
    public ButtonRegistry? Buttons;
}

/// <summary>
/// Immediate-mode canvas region within the retained scene tree.
/// Always dirty — opaque callback, never cached.
/// Use for LogicPlugin rendering, axes, menus, and other complex draw paths.
/// </summary>
public class CanvasNode : SceneNode
{
    public float X, Y, W, H;
    public Action<RenderContext>? Draw;
    public ButtonRegistry? Buttons;
}
