// SceneHitTest - Walk scene tree for hit testing
// FlexGroupNode delegates to its ButtonRegistry. Other nodes check HitAction bounds.

using Keystone.Core.Plugins;
using Keystone.Core.UI;

namespace Keystone.Core.Rendering;

public static class SceneHitTest
{
    public static HitTestResult? Test(SceneNode? root, float x, float y)
    {
        if (root == null) return null;
        return TestNode(root, x, y);
    }

    static HitTestResult? TestNode(SceneNode node, float x, float y)
    {
        if (node is CanvasNode cn)
            return cn.Buttons?.HitTest(x, y);

        if (node is FlexGroupNode flex)
            return flex.Buttons?.HitTest(x, y);

        if (node is GroupNode group)
        {
            if (group.Clip.HasValue)
            {
                var c = group.Clip.Value;
                if (x < c.Left || x > c.Right || y < c.Top || y > c.Bottom)
                    return null;
            }

            float lx = x - group.X, ly = y - group.Y;
            for (int i = group.Children.Length - 1; i >= 0; i--)
            {
                var hit = TestNode(group.Children[i], lx, ly);
                if (hit != null) return hit;
            }
        }

        if (node.HitAction != null && HitBounds(node, x, y))
            return new HitTestResult(node.HitAction, node.HitCursor);

        return null;
    }

    static bool HitBounds(SceneNode node, float x, float y) => node switch
    {
        RectNode r => x >= r.X && x < r.X + r.W && y >= r.Y && y < r.Y + r.H,
        ImageNode img => x >= img.X && x < img.X + img.W && y >= img.Y && y < img.Y + img.H,
        _ => false
    };
}
