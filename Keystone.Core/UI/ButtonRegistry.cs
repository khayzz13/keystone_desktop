// ButtonRegistry - Core UI type for hit testing button regions

using System.Collections.Generic;
using Keystone.Core.Plugins;
using Keystone.Core.Rendering;

namespace Keystone.Core.UI;

/// <summary>
/// Button hitbox for HitTest lookup
/// </summary>
public record ButtonHit(float X, float Y, float W, float H, string Action, CursorType Cursor = CursorType.Pointer);

/// <summary>
/// Manages button registration per-window for HitTest
/// </summary>
public class ButtonRegistry
{
    private readonly List<ButtonHit> _buttons = new();

    public void Clear() => _buttons.Clear();

    public void Add(float x, float y, float w, float h, string action, CursorType cursor = CursorType.Pointer)
        => _buttons.Add(new ButtonHit(x, y, w, h, action, cursor));

    public HitTestResult? HitTest(float x, float y)
    {
        for (int i = _buttons.Count - 1; i >= 0; i--)
        {
            var btn = _buttons[i];
            if (x >= btn.X && x < btn.X + btn.W && y >= btn.Y && y < btn.Y + btn.H)
                return new HitTestResult(btn.Action, btn.Cursor);
        }
        return null;
    }

    public (float X, float Y, float W, float H)? FindBounds(string action)
    {
        for (int i = _buttons.Count - 1; i >= 0; i--)
        {
            var btn = _buttons[i];
            if (btn.Action == action)
                return (btn.X, btn.Y, btn.W, btn.H);
        }
        return null;
    }
}
