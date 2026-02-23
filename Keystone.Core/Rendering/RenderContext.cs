// RenderContext - Direct SkiaSharp rendering
// Fluent API for window plugins

using SkiaSharp;

namespace Keystone.Core.Rendering;

public class RenderContext : IDisposable
{
    private readonly SKCanvas _canvas;
    private readonly SkiaPaintCache _paints;
    private SKPath? _currentPath;
    private bool _disposed;

    public FrameState State { get; }

    /// <summary>Per-window GPU context for compute shader access. Null if GPU unavailable.</summary>
    public IGpuContext? Gpu => State.GpuContext;

    // Exposed for child context creation (bind slots)
    public SKCanvas Canvas => _canvas;
    public SkiaPaintCache PaintCache => _paints;

    // Frame flags (matching RenderContext)
    private uint _flags;
    private uint _nextFrameMs;
    public const uint FLAG_NEEDS_REDRAW = 0x01;
    public const uint FLAG_ANIMATING = 0x02;
    public const uint FLAG_IDLE = 0x04;
    public uint Flags => _flags;

    public RenderContext(SKCanvas canvas, SkiaPaintCache paints, FrameState state)
    {
        _canvas = canvas;
        _paints = paints;
        State = state;
    }

    // === Frame Loop Control ===
    public RenderContext RequestRedraw() { _flags |= FLAG_NEEDS_REDRAW; return this; }
    public RenderContext SetAnimating(bool animating)
    {
        if (animating) _flags |= FLAG_ANIMATING;
        else _flags &= ~FLAG_ANIMATING;
        return this;
    }
    public RenderContext SetIdle() { _flags |= FLAG_IDLE; return this; }
    public RenderContext RequestNextFrameIn(uint ms) { _nextFrameMs = ms; return this; }

    // === Shape Fills ===
    public RenderContext Rect(float x, float y, float w, float h, uint color)
    {
        _canvas.DrawRect(x, y, w, h, _paints.GetFill(color));
        return this;
    }

    public RenderContext RoundedRect(float x, float y, float w, float h, float radius, uint color)
    {
        _canvas.DrawRoundRect(x, y, w, h, radius, radius, _paints.GetFill(color));
        return this;
    }

    public RenderContext Circle(float cx, float cy, float r, uint color)
    {
        _canvas.DrawCircle(cx, cy, r, _paints.GetFill(color));
        return this;
    }

    public RenderContext Ellipse(float cx, float cy, float rx, float ry, uint color)
    {
        _canvas.DrawOval(new SKRect(cx - rx, cy - ry, cx + rx, cy + ry), _paints.GetFill(color));
        return this;
    }

    // === Shape Strokes ===
    public RenderContext RectStroke(float x, float y, float w, float h, float width, uint color)
    {
        _canvas.DrawRect(x, y, w, h, _paints.GetStroke(color, width));
        return this;
    }

    public RenderContext RoundedRectStroke(float x, float y, float w, float h, float radius, float width, uint color)
    {
        _canvas.DrawRoundRect(x, y, w, h, radius, radius, _paints.GetStroke(color, width));
        return this;
    }

    public RenderContext CircleStroke(float cx, float cy, float r, float width, uint color)
    {
        _canvas.DrawCircle(cx, cy, r, _paints.GetStroke(color, width));
        return this;
    }

    public RenderContext EllipseStroke(float cx, float cy, float rx, float ry, float width, uint color)
    {
        _canvas.DrawOval(new SKRect(cx - rx, cy - ry, cx + rx, cy + ry), _paints.GetStroke(color, width));
        return this;
    }

    // === Lines ===
    public RenderContext Line(float x1, float y1, float x2, float y2, float width, uint color)
    {
        _canvas.DrawLine(x1, y1, x2, y2, _paints.GetStroke(color, width));
        return this;
    }

    public RenderContext DrawPoints(SKPoint[] points, float width, uint color)
    {
        if (points.Length < 2) return this;
        _canvas.DrawPoints(SKPointMode.Polygon, points, _paints.GetStroke(color, width));
        return this;
    }

    public RenderContext DrawPoints(SKPoint[] points, int count, float width, uint color)
    {
        if (count < 2) return this;
        // SkiaSharp doesn't have Span overload, but we can use the array directly
        // since the underlying native call just reads the first 'count' points
        if (count == points.Length)
            _canvas.DrawPoints(SKPointMode.Polygon, points, _paints.GetStroke(color, width));
        else
            _canvas.DrawPoints(SKPointMode.Polygon, points[..count], _paints.GetStroke(color, width));
        return this;
    }

    public RenderContext LineEx(float x1, float y1, float x2, float y2, float width, uint color, LineCap cap)
    {
        var paint = _paints.GetStroke(color, width);
        paint.StrokeCap = cap switch
        {
            LineCap.Round => SKStrokeCap.Round,
            LineCap.Square => SKStrokeCap.Square,
            _ => SKStrokeCap.Butt
        };
        _canvas.DrawLine(x1, y1, x2, y2, paint);
        return this;
    }

    public RenderContext DashedLine(float x1, float y1, float x2, float y2, float width, uint color, float dashLen, float gapLen)
    {
        var paint = _paints.GetStroke(color, width);
        using var effect = SKPathEffect.CreateDash(new[] { dashLen, gapLen }, 0);
        paint.PathEffect = effect;
        _canvas.DrawLine(x1, y1, x2, y2, paint);
        paint.PathEffect = null;
        return this;
    }

    // === Paths ===
    public RenderContext PathBegin()
    {
        _currentPath?.Dispose();
        _currentPath = new SKPath();
        return this;
    }

    public RenderContext PathMoveTo(float x, float y)
    {
        _currentPath?.MoveTo(x, y);
        return this;
    }

    public RenderContext PathLineTo(float x, float y)
    {
        _currentPath?.LineTo(x, y);
        return this;
    }

    public RenderContext PathQuadTo(float x1, float y1, float x2, float y2)
    {
        _currentPath?.QuadTo(x1, y1, x2, y2);
        return this;
    }

    public RenderContext PathCubicTo(float x1, float y1, float x2, float y2, float x3, float y3)
    {
        _currentPath?.CubicTo(x1, y1, x2, y2, x3, y3);
        return this;
    }

    public RenderContext PathClose()
    {
        _currentPath?.Close();
        return this;
    }

    public RenderContext PathFill(uint color)
    {
        if (_currentPath != null)
        {
            _canvas.DrawPath(_currentPath, _paints.GetFill(color));
            _currentPath.Dispose();
            _currentPath = null;
        }
        return this;
    }

    public RenderContext PathStroke(float width, uint color, LineCap cap = LineCap.Butt, LineJoin join = LineJoin.Miter)
    {
        if (_currentPath != null)
        {
            var paint = _paints.GetStroke(color, width);
            paint.StrokeCap = cap switch
            {
                LineCap.Round => SKStrokeCap.Round,
                LineCap.Square => SKStrokeCap.Square,
                _ => SKStrokeCap.Butt
            };
            paint.StrokeJoin = join switch
            {
                LineJoin.Round => SKStrokeJoin.Round,
                LineJoin.Bevel => SKStrokeJoin.Bevel,
                _ => SKStrokeJoin.Miter
            };
            _canvas.DrawPath(_currentPath, paint);
            _currentPath.Dispose();
            _currentPath = null;
        }
        return this;
    }

    // === Text ===
    public RenderContext Text(float x, float y, string text, float size, uint color, FontId font = FontId.Regular, TextAlign align = TextAlign.Left)
    {
        // Auto-detect symbols and use symbol font
        var actualFont = (font == FontId.Regular || font == FontId.Bold) && SkiaPaintCache.ContainsSymbols(text)
            ? FontId.Symbols : font;

        // Try atlas-accelerated path (covers ASCII text, no font engine needed)
        if (actualFont != FontId.Symbols)
        {
            var atlas = _paints.GetAsciiAtlas(actualFont, size);
            if (GlyphAtlas.IsAllInAtlas(text, atlas))
            {
                atlas.DrawString(_canvas, text, x, y, color, align);
                return this;
            }
        }

        // Fallback: full font engine (symbols, non-ASCII)
        var skFont = _paints.GetFont(actualFont, size);
        var paint = _paints.GetFill(color);
        float xPos = align switch
        {
            TextAlign.Center => x - _paints.MeasureText(text, actualFont, size) / 2,
            TextAlign.Right => x - _paints.MeasureText(text, actualFont, size),
            _ => x
        };
        _canvas.DrawText(text, xPos, y, skFont, paint);
        return this;
    }

    public RenderContext TextCentered(float x, float y, float w, string text, float size, uint color, FontId font = FontId.Regular)
        => Text(x + w / 2, y, text, size, color, font, TextAlign.Center);

    public RenderContext TextBold(float x, float y, string text, float size, uint color, TextAlign align = TextAlign.Left)
        => Text(x, y, text, size, color, FontId.Bold, align);

    public float MeasureText(string text, float size, FontId font = FontId.Regular)
        => _paints.MeasureText(text, font, size);

    /// <summary>
    /// Zero-alloc numeric rendering via glyph atlas.
    /// Decomposes double to digits using integer math (no ToString), draws from atlas.
    /// </summary>
    public RenderContext Number(float x, float y, double value, int decimals, float size, uint color,
        FontId font = FontId.Regular, TextAlign align = TextAlign.Left)
    {
        var atlas = _paints.GetNumericAtlas(font, size);
        Span<byte> digits = stackalloc byte[24];
        int count = DecomposeNumber(value, decimals, digits);
        atlas.DrawDigits(_canvas, digits, count, x, y, color, align);
        return this;
    }

    /// <summary>
    /// Decompose a double into ASCII digit chars using integer math only.
    /// Returns number of chars written.
    /// </summary>
    static int DecomposeNumber(double value, int decimals, Span<byte> output)
    {
        int pos = 0;
        if (double.IsNaN(value) || double.IsInfinity(value))
        {
            output[pos++] = (byte)'-';
            return pos;
        }

        bool negative = value < 0;
        if (negative) value = -value;

        // Scale to integer
        long multiplier = 1;
        for (int i = 0; i < decimals; i++) multiplier *= 10;
        long scaled = (long)(value * multiplier + 0.5);

        // Extract integer and fractional parts
        long intPart = scaled / multiplier;
        long fracPart = scaled % multiplier;

        if (negative) output[pos++] = (byte)'-';

        // Integer digits (reverse order into temp, then copy)
        if (intPart == 0)
        {
            output[pos++] = (byte)'0';
        }
        else
        {
            int start = pos;
            while (intPart > 0)
            {
                output[pos++] = (byte)('0' + (int)(intPart % 10));
                intPart /= 10;
            }
            // Reverse
            for (int i = start, j = pos - 1; i < j; i++, j--)
                (output[i], output[j]) = (output[j], output[i]);
        }

        // Decimal point + fractional digits
        if (decimals > 0)
        {
            output[pos++] = (byte)'.';
            for (int d = decimals - 1; d >= 0; d--)
            {
                long div = 1;
                for (int i = 0; i < d; i++) div *= 10;
                output[pos++] = (byte)('0' + (int)(fracPart / div % 10));
            }
        }

        return pos;
    }

    // === Gradients ===
    public RenderContext LinearGradientRect(float x, float y, float w, float h, uint color1, uint color2, float angle = 90)
    {
        using var paint = new SKPaint { IsAntialias = true };
        var rad = angle * MathF.PI / 180f;
        var dx = MathF.Cos(rad) * w;
        var dy = MathF.Sin(rad) * h;
        paint.Shader = SKShader.CreateLinearGradient(
            new SKPoint(x, y),
            new SKPoint(x + dx, y + dy),
            new[] { SkiaPaintCache.UnpackColor(color1), SkiaPaintCache.UnpackColor(color2) },
            SKShaderTileMode.Clamp);
        _canvas.DrawRect(x, y, w, h, paint);
        return this;
    }

    public RenderContext RadialGradientCircle(float cx, float cy, float r, uint colorCenter, uint colorEdge)
    {
        using var paint = new SKPaint { IsAntialias = true };
        paint.Shader = SKShader.CreateRadialGradient(
            new SKPoint(cx, cy), r,
            new[] { SkiaPaintCache.UnpackColor(colorCenter), SkiaPaintCache.UnpackColor(colorEdge) },
            SKShaderTileMode.Clamp);
        _canvas.DrawCircle(cx, cy, r, paint);
        return this;
    }

    // === Transforms ===
    public RenderContext PushTransform(float tx = 0, float ty = 0, float rotation = 0, float scaleX = 1, float scaleY = 1)
    {
        _canvas.Save();
        _canvas.Translate(tx, ty);
        if (rotation != 0) _canvas.RotateDegrees(rotation);
        if (scaleX != 1 || scaleY != 1) _canvas.Scale(scaleX, scaleY);
        return this;
    }

    public RenderContext PopTransform()
    {
        _canvas.Restore();
        return this;
    }

    // === Clipping ===
    public RenderContext PushClip(float x, float y, float w, float h)
    {
        _canvas.Save();
        _canvas.ClipRect(new SKRect(x, y, x + w, y + h));
        return this;
    }

    public RenderContext PushClipRounded(float x, float y, float w, float h, float radius)
    {
        _canvas.Save();
        var rrect = new SKRoundRect(new SKRect(x, y, x + w, y + h), radius);
        _canvas.ClipRoundRect(rrect);
        return this;
    }

    public RenderContext PopClip()
    {
        _canvas.Restore();
        return this;
    }

    // === Layer ===
    public RenderContext PushLayer(float alpha = 1, BlendMode blend = BlendMode.Normal)
    {
        using var paint = new SKPaint { Color = new SKColor(255, 255, 255, (byte)(alpha * 255)) };
        paint.BlendMode = blend switch
        {
            BlendMode.Multiply => SKBlendMode.Multiply,
            BlendMode.Screen => SKBlendMode.Screen,
            BlendMode.Overlay => SKBlendMode.Overlay,
            BlendMode.Darken => SKBlendMode.Darken,
            BlendMode.Lighten => SKBlendMode.Lighten,
            BlendMode.ColorDodge => SKBlendMode.ColorDodge,
            BlendMode.ColorBurn => SKBlendMode.ColorBurn,
            BlendMode.HardLight => SKBlendMode.HardLight,
            BlendMode.SoftLight => SKBlendMode.SoftLight,
            BlendMode.Difference => SKBlendMode.Difference,
            BlendMode.Exclusion => SKBlendMode.Exclusion,
            _ => SKBlendMode.SrcOver
        };
        _canvas.SaveLayer(paint);
        return this;
    }

    public RenderContext PopLayer()
    {
        _canvas.Restore();
        return this;
    }

    // === State ===
    public RenderContext SetCursor(CursorType cursor)
    {
        // TODO: Set cursor via platform layer
        return this;
    }

    // === Special Renders ===
    public RenderContext RenderCrosshair(float x, float y, float w, float h, uint color)
    {
        var mx = State.MouseX;
        var my = State.MouseY;
        if (mx >= x && mx <= x + w && my >= y && my <= y + h)
        {
            var paint = _paints.GetStroke(color, 1);
            _canvas.DrawLine(x, my, x + w, my, paint);
            _canvas.DrawLine(mx, y, mx, y + h, paint);
        }
        return this;
    }

    public RenderContext RenderTexture(IntPtr textureHandle, float x, float y, float w, float h)
    {
        // TODO: Import texture from Metal and draw
        return this;
    }

    public RenderContext DrawImage(SKImage image, float x, float y, float w, float h)
    {
        var destRect = new SKRect(x, y, x + w, y + h);
        _canvas.DrawImage(image, destRect);
        return this;
    }

    // === Helpers ===
    public bool IsHovered(float x, float y, float w, float h) =>
        State.MouseX >= x && State.MouseX < x + w && State.MouseY >= y && State.MouseY < y + h;

    public bool WasClicked(float x, float y, float w, float h) =>
        IsHovered(x, y, w, h) && State.MouseClicked;

    public bool IsMouseDown(float x, float y, float w, float h) =>
        IsHovered(x, y, w, h) && State.MouseDown;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _currentPath?.Dispose();
    }
}
