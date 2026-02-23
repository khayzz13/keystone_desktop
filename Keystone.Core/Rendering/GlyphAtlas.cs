// GlyphAtlas - Pre-baked glyph texture atlas for fast text rendering
// Build once per (font, size) pair. Draw text as DrawImage blits from the atlas.
// Numeric atlas: "0123456789.,-+$% " (17 glyphs) — for zero-alloc number rendering
// ASCII atlas: chars 32-126 (95 glyphs) — for general text acceleration

using SkiaSharp;

namespace Keystone.Core.Rendering;

public class GlyphAtlas : IDisposable
{
    struct GlyphEntry
    {
        public float U, V;      // top-left in atlas (pixels)
        public float W, H;      // glyph bounding box
        public float Advance;   // horizontal advance
        public float BearingY;  // baseline-relative Y offset
    }

    readonly SKImage _image;
    readonly GlyphEntry[] _entries;
    readonly char _baseChar;
    readonly int _glyphCount;
    readonly float _lineHeight;
    readonly SKPaint _drawPaint = new() { IsAntialias = true };
    uint _lastColor;
    bool _disposed;

    GlyphAtlas(SKImage image, GlyphEntry[] entries, char baseChar, int glyphCount, float lineHeight)
    {
        _image = image;
        _entries = entries;
        _baseChar = baseChar;
        _glyphCount = glyphCount;
        _lineHeight = lineHeight;
    }

    public SKImage Image => _image;
    public float LineHeight => _lineHeight;

    public bool HasGlyph(char c)
    {
        int idx = c - _baseChar;
        return idx >= 0 && idx < _glyphCount;
    }

    public float GetAdvance(char c)
    {
        int idx = c - _baseChar;
        return (idx >= 0 && idx < _glyphCount) ? _entries[idx].Advance : 0;
    }

    /// <summary>Measure string width using atlas advance widths (no font engine).</summary>
    public float MeasureString(string text)
    {
        float w = 0;
        foreach (char c in text)
        {
            int idx = c - _baseChar;
            if (idx >= 0 && idx < _glyphCount) w += _entries[idx].Advance;
        }
        return w;
    }

    /// <summary>Measure digit span width (for zero-alloc number path).</summary>
    public float MeasureDigits(ReadOnlySpan<byte> digits, int count)
    {
        float w = 0;
        for (int i = 0; i < count; i++)
        {
            char c = (char)digits[i];
            int idx = c - _baseChar;
            if (idx >= 0 && idx < _glyphCount) w += _entries[idx].Advance;
        }
        return w;
    }

    /// <summary>Check if all chars in text are in this atlas.</summary>
    public static bool IsAllInAtlas(string text, GlyphAtlas atlas)
    {
        foreach (char c in text)
            if (!atlas.HasGlyph(c)) return false;
        return true;
    }

    void SetTint(uint color)
    {
        if (color == _lastColor) return;
        _lastColor = color;
        _drawPaint.ColorFilter?.Dispose();
        _drawPaint.ColorFilter = SKColorFilter.CreateBlendMode(
            SkiaPaintCache.UnpackColor(color), SKBlendMode.Modulate);
    }

    /// <summary>Draw a string from the atlas to canvas at (x, y) baseline.</summary>
    public void DrawString(SKCanvas canvas, string text, float x, float y, uint color, TextAlign align)
    {
        SetTint(color);
        float totalW = (align != TextAlign.Left) ? MeasureString(text) : 0;
        float drawX = align switch
        {
            TextAlign.Right => x - totalW,
            TextAlign.Center => x - totalW / 2,
            _ => x
        };

        foreach (char c in text)
        {
            int idx = c - _baseChar;
            if (idx < 0 || idx >= _glyphCount) continue;
            ref var e = ref _entries[idx];
            if (e.W > 0 && e.H > 0)
            {
                var src = new SKRect(e.U, e.V, e.U + e.W, e.V + e.H);
                var dst = new SKRect(drawX, y - e.BearingY, drawX + e.W, y - e.BearingY + e.H);
                canvas.DrawImage(_image, src, dst, _drawPaint);
            }
            drawX += e.Advance;
        }
    }

    /// <summary>Draw digits from stackalloc span (zero-alloc number rendering).</summary>
    public void DrawDigits(SKCanvas canvas, ReadOnlySpan<byte> digits, int count,
        float x, float y, uint color, TextAlign align)
    {
        SetTint(color);
        float totalW = (align != TextAlign.Left) ? MeasureDigits(digits, count) : 0;
        float drawX = align switch
        {
            TextAlign.Right => x - totalW,
            TextAlign.Center => x - totalW / 2,
            _ => x
        };

        for (int i = 0; i < count; i++)
        {
            char c = (char)digits[i];
            int idx = c - _baseChar;
            if (idx < 0 || idx >= _glyphCount) continue;
            ref var e = ref _entries[idx];
            if (e.W > 0 && e.H > 0)
            {
                var src = new SKRect(e.U, e.V, e.U + e.W, e.V + e.H);
                var dst = new SKRect(drawX, y - e.BearingY, drawX + e.W, y - e.BearingY + e.H);
                canvas.DrawImage(_image, src, dst, _drawPaint);
            }
            drawX += e.Advance;
        }
    }

    // --- Build Methods ---

    const string NumericChars = "0123456789.,-+$% ";
    const int AsciiStart = 32;
    const int AsciiEnd = 126;

    public static GlyphAtlas BuildNumeric(SkiaPaintCache paints, FontId font, float size)
        => BuildFromChars(paints, font, size, NumericChars);

    public static GlyphAtlas BuildAscii(SkiaPaintCache paints, FontId font, float size)
    {
        var chars = new char[AsciiEnd - AsciiStart + 1];
        for (int i = 0; i < chars.Length; i++)
            chars[i] = (char)(AsciiStart + i);
        return BuildFromChars(paints, font, size, new string(chars));
    }

    static GlyphAtlas BuildFromChars(SkiaPaintCache paints, FontId fontId, float size, string chars)
    {
        var skFont = paints.GetFont(fontId, size);
        var paint = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Fill, Color = SKColors.White };

        float lineHeight = size * 1.4f;
        int padding = 2;
        int count = chars.Length;
        var entries = new GlyphEntry[count];

        // First pass: measure all glyphs
        float totalWidth = 0;
        float maxHeight = 0;
        var widths = new float[count];
        for (int i = 0; i < count; i++)
        {
            var str = chars[i].ToString();
            widths[i] = skFont.MeasureText(str, out var bounds);
            entries[i].Advance = widths[i];
            entries[i].W = bounds.Width > 0 ? bounds.Width + 2 : 0; // +2 for antialiasing
            entries[i].H = bounds.Height > 0 ? bounds.Height + 2 : 0;
            entries[i].BearingY = -bounds.Top + 1; // baseline offset
            totalWidth += entries[i].W + padding;
            maxHeight = Math.Max(maxHeight, entries[i].H + padding);
        }

        // Create atlas bitmap (single row strip)
        int atlasW = (int)Math.Ceiling(totalWidth) + padding;
        int atlasH = (int)Math.Ceiling(maxHeight) + padding;
        if (atlasW < 1) atlasW = 1;
        if (atlasH < 1) atlasH = 1;

        using var bitmap = new SKBitmap(atlasW, atlasH, SKColorType.Rgba8888, SKAlphaType.Premul);
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(SKColors.Transparent);

        // Second pass: render glyphs into atlas
        float x = padding / 2f;
        for (int i = 0; i < count; i++)
        {
            if (entries[i].W <= 0) { entries[i].U = 0; entries[i].V = 0; continue; }
            entries[i].U = x;
            entries[i].V = padding / 2f;
            canvas.DrawText(chars[i].ToString(), x + 1, entries[i].BearingY + padding / 2f, skFont, paint);
            x += entries[i].W + padding;
        }

        canvas.Flush();
        var image = SKImage.FromBitmap(bitmap);
        paint.Dispose();

        // Determine base char for index mapping (chars may not be sorted)
        char baseChar = chars[0], maxChar = chars[0];
        for (int i = 1; i < count; i++)
        {
            if (chars[i] < baseChar) baseChar = chars[i];
            if (chars[i] > maxChar) maxChar = chars[i];
        }
        int range = maxChar - baseChar + 1;

        // Build sparse → dense mapping via full-range entries array
        var fullEntries = new GlyphEntry[range];
        for (int i = 0; i < count; i++)
        {
            int idx = chars[i] - baseChar;
            if (idx >= 0 && idx < range) fullEntries[idx] = entries[i];
        }

        return new GlyphAtlas(image, fullEntries, baseChar, range, lineHeight);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _drawPaint.ColorFilter?.Dispose();
        _drawPaint.Dispose();
        _image.Dispose();
    }
}
