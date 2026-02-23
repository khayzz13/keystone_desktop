// SceneRenderer - Cache-aware renderer for retained scene graph
// Clean GroupNodes replay from cached SKPicture. Dirty subtrees re-render + re-cache.
// FlexGroupNode renders via FlexRenderer, caches as SKPicture.

using SkiaSharp;

namespace Keystone.Core.Rendering;

public class SceneRenderer : IDisposable
{
    SceneNode? _previous;
    float _lastMouseX = float.NaN;
    float _lastMouseY = float.NaN;
    bool _lastMouseDown;
    bool _forceDynamicFlexFrame;

    /// <summary>
    /// Diff against previous frame, then render. Caches clean subtrees.
    /// </summary>
    public void Render(SKCanvas canvas, SkiaPaintCache paints, FrameState state, SceneNode root)
    {
        _forceDynamicFlexFrame =
            _lastMouseX != state.MouseX
            || _lastMouseY != state.MouseY
            || _lastMouseDown != state.MouseDown
            || state.MouseScroll != 0
            || state.MouseClicked
            || state.RightClick;

        SceneDiff.Diff(_previous, root);
        RenderNode(canvas, paints, state, root);
        _previous = root;
        _lastMouseX = state.MouseX;
        _lastMouseY = state.MouseY;
        _lastMouseDown = state.MouseDown;
    }

    void RenderNode(SKCanvas canvas, SkiaPaintCache paints, FrameState state, SceneNode node)
    {
        switch (node)
        {
            case CanvasNode cn:
                if (cn.Draw != null)
                {
                    canvas.Save();
                    canvas.ClipRect(new SKRect(cn.X, cn.Y, cn.X + cn.W, cn.Y + cn.H));
                    using (var ctx = new RenderContext(canvas, paints, state))
                        cn.Draw(ctx);
                    canvas.Restore();
                }
                break;

            case FlexGroupNode flex:
                RenderFlex(canvas, paints, state, flex);
                break;

            case GroupNode group:
                RenderGroup(canvas, paints, state, group);
                break;

            case RectNode r:
                if (r.Radius > 0)
                    canvas.DrawRoundRect(r.X, r.Y, r.W, r.H, r.Radius, r.Radius, paints.GetFill(r.Color));
                else
                    canvas.DrawRect(r.X, r.Y, r.W, r.H, paints.GetFill(r.Color));
                break;

            case TextNode t:
            {
                var font = paints.GetFont(t.Font, t.Size);
                var paint = paints.GetFill(t.Color);
                float x = t.Align switch
                {
                    TextAlign.Center => t.X - paints.MeasureText(t.Text, t.Font, t.Size) / 2,
                    TextAlign.Right => t.X - paints.MeasureText(t.Text, t.Font, t.Size),
                    _ => t.X
                };
                canvas.DrawText(t.Text, x, t.Y, font, paint);
                break;
            }

            case NumberNode n:
            {
                var atlas = paints.GetNumericAtlas(n.Font, n.Size);
                Span<byte> digits = stackalloc byte[24];
                int count = DecomposeNumber(n.Value, n.Decimals, digits);
                atlas.DrawDigits(canvas, digits, count, n.X, n.Y, n.Color, n.Align);
                break;
            }

            case LineNode l:
                canvas.DrawLine(l.X1, l.Y1, l.X2, l.Y2, paints.GetStroke(l.Color, l.Width));
                break;

            case ImageNode img:
                if (img.Image != null)
                    canvas.DrawImage(img.Image, new SKRect(img.X, img.Y, img.X + img.W, img.Y + img.H));
                break;

            case PointsNode pts:
                if (pts.Count > 0)
                    canvas.DrawPoints(SKPointMode.Lines, pts.Points.AsSpan(0, pts.Count).ToArray(), paints.GetStroke(pts.Color, pts.Width));
                break;

            case PathNode p:
                if (p.Path != null)
                {
                    if (p.FillColor != 0) canvas.DrawPath(p.Path, paints.GetFill(p.FillColor));
                    if (p.StrokeColor != 0) canvas.DrawPath(p.Path, paints.GetStroke(p.StrokeColor, p.StrokeWidth));
                }
                break;
        }
    }

    void RenderGroup(SKCanvas canvas, SkiaPaintCache paints, FrameState state, GroupNode group)
    {
        // Clean subtree with cache → replay SKPicture
        if (!group.Dirty && group.Cache != null)
        {
            canvas.Save();
            if (group.X != 0 || group.Y != 0) canvas.Translate(group.X, group.Y);
            if (group.Clip.HasValue) canvas.ClipRect(group.Clip.Value);
            canvas.DrawPicture(group.Cache);
            canvas.Restore();
            return;
        }

        // Dirty → re-render, optionally cache result
        bool shouldCache = group.Id > 0; // only cache named groups
        SKPictureRecorder? recorder = null;
        SKCanvas drawCanvas = canvas;

        if (shouldCache)
        {
            recorder = new SKPictureRecorder();
            var bounds = group.Clip ?? new SKRect(0, 0, state.Width, state.Height);
            drawCanvas = recorder.BeginRecording(bounds);
        }
        else
        {
            drawCanvas.Save();
            if (group.X != 0 || group.Y != 0) drawCanvas.Translate(group.X, group.Y);
            if (group.Clip.HasValue) drawCanvas.ClipRect(group.Clip.Value);
        }

        foreach (var child in group.Children)
            RenderNode(drawCanvas, paints, state, child);

        if (shouldCache && recorder != null)
        {
            group.Cache = recorder.EndRecording();
            recorder.Dispose();
            // Draw the just-recorded picture
            canvas.Save();
            if (group.X != 0 || group.Y != 0) canvas.Translate(group.X, group.Y);
            if (group.Clip.HasValue) canvas.ClipRect(group.Clip.Value);
            canvas.DrawPicture(group.Cache);
            canvas.Restore();
        }
        else
        {
            drawCanvas.Restore();
        }
    }

    void RenderFlex(SKCanvas canvas, SkiaPaintCache paints, FrameState state, FlexGroupNode flex)
    {
        if (flex.Root == null) return;

        // Clean → replay cached SKPicture + lightweight button registration (no GPU rendering)
        if (!flex.Dirty && flex.Cache != null && !_forceDynamicFlexFrame)
        {
            canvas.DrawPicture(flex.Cache);
            if (flex.Buttons != null)
                flex.Root.RegisterButtons(flex.Buttons, flex.X, flex.Y, flex.W, flex.H);
            return;
        }

        // Dirty → render via FlexRenderer, cache as SKPicture
        var recorder = new SKPictureRecorder();
        var bounds = new SKRect(flex.X, flex.Y, flex.X + flex.W, flex.Y + flex.H);
        var recCanvas = recorder.BeginRecording(bounds);

        using var ctx = new RenderContext(recCanvas, paints, state);
        if (flex.Buttons != null)
            flex.Root.Render(ctx, flex.Buttons, flex.X, flex.Y, flex.W, flex.H);

        flex.Cache?.Dispose();
        flex.Cache = recorder.EndRecording();
        recorder.Dispose();
        canvas.DrawPicture(flex.Cache);
    }

    public void Dispose()
    {
        if (_previous != null) { DisposeTreeCaches(_previous); _previous = null; }
    }

    static void DisposeTreeCaches(SceneNode node)
    {
        node.Cache?.Dispose();
        if (node is GroupNode g)
            foreach (var child in g.Children)
                DisposeTreeCaches(child);
    }

    // Duplicated from RenderContext — shared via static in future
    static int DecomposeNumber(double value, int decimals, Span<byte> output)
    {
        int pos = 0;
        if (double.IsNaN(value) || double.IsInfinity(value)) { output[pos++] = (byte)'-'; return pos; }

        bool negative = value < 0;
        if (negative) value = -value;

        long multiplier = 1;
        for (int i = 0; i < decimals; i++) multiplier *= 10;
        long scaled = (long)(value * multiplier + 0.5);
        long intPart = scaled / multiplier;
        long fracPart = scaled % multiplier;

        if (negative) output[pos++] = (byte)'-';
        if (intPart == 0) { output[pos++] = (byte)'0'; }
        else
        {
            int start = pos;
            while (intPart > 0) { output[pos++] = (byte)('0' + (int)(intPart % 10)); intPart /= 10; }
            for (int i = start, j = pos - 1; i < j; i++, j--) (output[i], output[j]) = (output[j], output[i]);
        }

        if (decimals > 0)
        {
            output[pos++] = (byte)'.';
            for (int d = decimals - 1; d >= 0; d--)
            {
                long div = 1; for (int i = 0; i < d; i++) div *= 10;
                output[pos++] = (byte)('0' + (int)(fracPart / div % 10));
            }
        }
        return pos;
    }
}
