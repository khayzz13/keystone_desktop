// SceneBuilder - Fluent API for building scene trees
// Mirrors RenderContext API so plugins can migrate incrementally.

using Keystone.Core.UI;
using SkiaSharp;

namespace Keystone.Core.Rendering;

public class SceneBuilder
{
    readonly Stack<List<SceneNode>> _stack = new();
    readonly Stack<(int id, float x, float y, SKRect? clip)> _groupMeta = new();
    List<SceneNode> _current;

    public SceneBuilder()
    {
        _current = new List<SceneNode>();
    }

    public SceneBuilder Rect(float x, float y, float w, float h, uint color, float radius = 0)
    {
        _current.Add(new RectNode { X = x, Y = y, W = w, H = h, Color = color, Radius = radius });
        return this;
    }

    public SceneBuilder Text(float x, float y, string text, float size, uint color,
        FontId font = FontId.Regular, TextAlign align = TextAlign.Left)
    {
        _current.Add(new TextNode { X = x, Y = y, Text = text, Size = size, Color = color, Font = font, Align = align });
        return this;
    }

    public SceneBuilder Number(float x, float y, double value, int decimals, float size, uint color,
        FontId font = FontId.Regular, TextAlign align = TextAlign.Left)
    {
        _current.Add(new NumberNode { X = x, Y = y, Value = value, Decimals = decimals, Size = size, Color = color, Font = font, Align = align });
        return this;
    }

    public SceneBuilder Line(float x1, float y1, float x2, float y2, float width, uint color)
    {
        _current.Add(new LineNode { X1 = x1, Y1 = y1, X2 = x2, Y2 = y2, Width = width, Color = color });
        return this;
    }

    public SceneBuilder Image(SKImage image, float x, float y, float w, float h)
    {
        _current.Add(new ImageNode { Image = image, X = x, Y = y, W = w, H = h });
        return this;
    }

    public SceneBuilder PushGroup(int id = 0, float x = 0, float y = 0)
    {
        _stack.Push(_current);
        _groupMeta.Push((id, x, y, null));
        _current = new List<SceneNode>();
        return this;
    }

    public SceneBuilder PushClip(int id, float x, float y, float w, float h)
    {
        _stack.Push(_current);
        _groupMeta.Push((id, 0, 0, new SKRect(x, y, x + w, y + h)));
        _current = new List<SceneNode>();
        return this;
    }

    public SceneBuilder PopGroup()
    {
        var children = _current.ToArray();
        var (id, x, y, clip) = _groupMeta.Pop();
        _current = _stack.Pop();
        _current.Add(new GroupNode { Id = id, X = x, Y = y, Clip = clip, Children = children });
        return this;
    }

    /// <summary>Embed an immediate-mode canvas region into the scene tree.</summary>
    public SceneBuilder Canvas(int id, float x, float y, float w, float h,
        ButtonRegistry? buttons, Action<RenderContext> draw)
    {
        _current.Add(new CanvasNode { Id = id, X = x, Y = y, W = w, H = h, Buttons = buttons, Draw = draw });
        return this;
    }

    /// <summary>Embed a FlexNode tree as a scene subtree.</summary>
    public SceneBuilder Flex(int id, FlexNode root, float x, float y, float w, float h, ButtonRegistry buttons)
    {
        _current.Add(new FlexGroupNode { Id = id, Root = root, X = x, Y = y, W = w, H = h, Buttons = buttons });
        return this;
    }

    /// <summary>Add a node with hit testing action.</summary>
    public SceneBuilder Hit(SceneNode node, string action, CursorType cursor = CursorType.Pointer)
    {
        node.HitAction = action;
        node.HitCursor = cursor;
        _current.Add(node);
        return this;
    }

    public GroupNode Build()
    {
        return new GroupNode { Children = _current.ToArray() };
    }
}
