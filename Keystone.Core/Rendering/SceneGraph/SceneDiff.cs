// SceneDiff - Tree differ for retained scene graph
// Compares current frame's tree against previous. Marks changed nodes Dirty.
// Clean subtrees retain their SKPicture cache.

namespace Keystone.Core.Rendering;

public static class SceneDiff
{
    /// <summary>
    /// Diff current against previous tree. Sets Dirty on changed nodes.
    /// Invalidates parent Cache when children change.
    /// </summary>
    public static void Diff(SceneNode? prev, SceneNode current)
    {
        if (prev == null) { MarkDirty(current); return; }
        if (prev.GetType() != current.GetType()) { DisposeTreeCaches(prev); MarkDirty(current); return; }

        // Groups/Flex have specialized diff logic (children/layout/cache handling).
        // Skip generic cache transfer for these node kinds.
        if (current is GroupNode cg && prev is GroupNode pg)
        {
            DiffGroup(pg, cg);
            return;
        }
        if (current is FlexGroupNode cf && prev is FlexGroupNode pf)
        {
            DiffFlex(pf, cf);
            return;
        }

        current.Dirty = !NodesEqual(prev, current);

        // Transfer cache from previous if clean, dispose if dirty
        if (!current.Dirty && prev.Cache != null)
            current.Cache = prev.Cache;
        else
        {
            prev.Cache?.Dispose();
            current.Cache = null;
        }

    }

    static void DiffGroup(GroupNode prev, GroupNode current)
    {
        if (prev.Children.Length != current.Children.Length)
        {
            DisposeTreeCaches(prev);
            MarkDirty(current);
            return;
        }

        current.Dirty = !NodesEqual(prev, current);
        if (!current.Dirty && prev.Cache != null)
            current.Cache = prev.Cache;
        else
        {
            prev.Cache?.Dispose();
            current.Cache = null;
        }

        bool anyChildDirty = false;
        for (int i = 0; i < current.Children.Length; i++)
        {
            var pc = FindMatch(prev.Children, current.Children[i], i);
            Diff(pc, current.Children[i]);
            if (current.Children[i].Dirty) anyChildDirty = true;
        }

        if (anyChildDirty)
        {
            current.Dirty = true;
            prev.Cache?.Dispose();
            current.Cache = null;
        }
    }

    static void DiffFlex(FlexGroupNode prev, FlexGroupNode current)
    {
        // Dirty if FlexNode tree reference changed or layout invalidated
        bool dirty = !ReferenceEquals(prev.Root, current.Root)
            || current.Root == null
            || !current.Root._layoutValid
            || prev.X != current.X || prev.Y != current.Y
            || prev.W != current.W || prev.H != current.H;

        current.Dirty = dirty;
        if (!dirty && prev.Cache != null)
            current.Cache = prev.Cache;
        else
        {
            prev.Cache?.Dispose();
            current.Cache = null;
        }
    }

    /// <summary>Find matching previous child by Id (if >0) or position index.</summary>
    static SceneNode? FindMatch(SceneNode[] prevChildren, SceneNode current, int posIndex)
    {
        if (current.Id > 0)
        {
            for (int i = 0; i < prevChildren.Length; i++)
                if (prevChildren[i].Id == current.Id) return prevChildren[i];
            return null;
        }
        return posIndex < prevChildren.Length ? prevChildren[posIndex] : null;
    }

    static bool NodesEqual(SceneNode a, SceneNode b) => (a, b) switch
    {
        (RectNode ra, RectNode rb) =>
            ra.X == rb.X && ra.Y == rb.Y && ra.W == rb.W && ra.H == rb.H
            && ra.Color == rb.Color && ra.Radius == rb.Radius,

        (TextNode ta, TextNode tb) =>
            ta.X == tb.X && ta.Y == tb.Y && ta.Text == tb.Text
            && ta.Size == tb.Size && ta.Color == tb.Color && ta.Font == tb.Font && ta.Align == tb.Align,

        (NumberNode na, NumberNode nb) =>
            na.X == nb.X && na.Y == nb.Y && na.Value == nb.Value && na.Decimals == nb.Decimals
            && na.Size == nb.Size && na.Color == nb.Color && na.Font == nb.Font,

        (LineNode la, LineNode lb) =>
            la.X1 == lb.X1 && la.Y1 == lb.Y1 && la.X2 == lb.X2 && la.Y2 == lb.Y2
            && la.Width == lb.Width && la.Color == lb.Color,

        (ImageNode ia, ImageNode ib) =>
            ReferenceEquals(ia.Image, ib.Image) && ia.X == ib.X && ia.Y == ib.Y && ia.W == ib.W && ia.H == ib.H,

        (GroupNode ga, GroupNode gb) =>
            ga.X == gb.X && ga.Y == gb.Y && ga.Clip == gb.Clip,

        (PointsNode pa, PointsNode pb) =>
            pa.Count == pb.Count && pa.Width == pb.Width && pa.Color == pb.Color
            && ReferenceEquals(pa.Points, pb.Points),

        (PathNode pa, PathNode pb) =>
            ReferenceEquals(pa.Path, pb.Path) && pa.FillColor == pb.FillColor
            && pa.StrokeColor == pb.StrokeColor && pa.StrokeWidth == pb.StrokeWidth,

        (CanvasNode, CanvasNode) => false, // always dirty — opaque callback

        _ => false
    };

    static void MarkDirty(SceneNode node)
    {
        node.Dirty = true;
        node.Cache = null;
        if (node is GroupNode g)
            foreach (var child in g.Children)
                MarkDirty(child);
    }

    /// <summary>Dispose all SKPicture caches in a tree being discarded.</summary>
    static void DisposeTreeCaches(SceneNode node)
    {
        node.Cache?.Dispose();
        if (node is GroupNode g)
            foreach (var child in g.Children)
                DisposeTreeCaches(child);
    }
}
