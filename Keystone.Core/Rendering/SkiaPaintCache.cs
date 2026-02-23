using SkiaSharp;

namespace Keystone.Core.Rendering;

/// <summary>
/// Caches and reuses SKPaint, SKFont, and GlyphAtlas objects to avoid per-frame allocations.
/// </summary>
public class SkiaPaintCache : IDisposable
{
    private readonly SKPaint _fillPaint;
    private readonly SKPaint _strokePaint;
    private readonly Dictionary<(FontId, float), SKFont> _fonts = new();
    private readonly Dictionary<(FontId, float), GlyphAtlas> _numericAtlases = new();
    private readonly Dictionary<(FontId, float), GlyphAtlas> _asciiAtlases = new();
    private readonly SKTypeface _regular;
    private readonly SKTypeface _bold;
    private readonly SKTypeface _symbols;
    private bool _disposed;

    // Unicode ranges for symbols that need symbol font
    private static bool NeedsSymbolFont(char c) =>
        c >= '\u2200' && c <= '\u22FF' ||  // Mathematical Operators (∆)
        c >= '\u2600' && c <= '\u26FF' ||  // Misc Symbols (★, ⚙)
        c >= '\u2700' && c <= '\u27BF' ||  // Dingbats
        c >= '\u0391' && c <= '\u03C9';    // Greek (Δ)

    public SkiaPaintCache()
    {
        _fillPaint = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Fill };
        _strokePaint = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke };

        _regular = SKTypeface.FromFamilyName("SF Pro", SKFontStyle.Normal)
                   ?? SKTypeface.FromFamilyName("Helvetica Neue", SKFontStyle.Normal)
                   ?? SKTypeface.Default;
        _bold = SKTypeface.FromFamilyName("SF Pro", SKFontStyle.Bold)
                ?? SKTypeface.FromFamilyName("Helvetica Neue", SKFontStyle.Bold)
                ?? SKTypeface.Default;
        _symbols = SKTypeface.FromFamilyName("Apple Symbols")
                   ?? SKTypeface.FromFamilyName("Menlo")
                   ?? SKTypeface.Default;
    }

    public SKTypeface Symbols => _symbols;

    public SKPaint GetFill(uint color)
    {
        _fillPaint.Color = UnpackColor(color);
        return _fillPaint;
    }

    public SKPaint GetStroke(uint color, float width)
    {
        _strokePaint.Color = UnpackColor(color);
        _strokePaint.StrokeWidth = width;
        _strokePaint.PathEffect = null;
        return _strokePaint;
    }

    public SKFont GetFont(FontId id, float size)
    {
        size = MathF.Round(size * 2) / 2;
        var key = (id, size);
        if (!_fonts.TryGetValue(key, out var font))
        {
            if (_fonts.Count > 200)
            {
                foreach (var f in _fonts.Values) f.Dispose();
                _fonts.Clear();
            }
            var typeface = id switch
            {
                FontId.Bold => _bold,
                FontId.Symbols => _symbols,
                _ => _regular
            };
            font = new SKFont(typeface, size);
            _fonts[key] = font;
        }
        return font;
    }

    /// <summary>Get or lazily build numeric glyph atlas (0-9, ., -, +, etc.)</summary>
    public GlyphAtlas GetNumericAtlas(FontId font, float size)
    {
        size = MathF.Round(size * 2) / 2;
        var key = (font, size);
        if (!_numericAtlases.TryGetValue(key, out var atlas))
        {
            if (_numericAtlases.Count > 50)
            {
                foreach (var a in _numericAtlases.Values) a.Dispose();
                _numericAtlases.Clear();
            }
            atlas = GlyphAtlas.BuildNumeric(this, font, size);
            _numericAtlases[key] = atlas;
        }
        return atlas;
    }

    /// <summary>Get or lazily build ASCII glyph atlas (chars 32-126)</summary>
    public GlyphAtlas GetAsciiAtlas(FontId font, float size)
    {
        size = MathF.Round(size * 2) / 2;
        var key = (font, size);
        if (!_asciiAtlases.TryGetValue(key, out var atlas))
        {
            if (_asciiAtlases.Count > 50)
            {
                foreach (var a in _asciiAtlases.Values) a.Dispose();
                _asciiAtlases.Clear();
            }
            atlas = GlyphAtlas.BuildAscii(this, font, size);
            _asciiAtlases[key] = atlas;
        }
        return atlas;
    }

    public static bool ContainsSymbols(string text)
    {
        foreach (char c in text)
            if (NeedsSymbolFont(c)) return true;
        return false;
    }

    private readonly Dictionary<(string, FontId, float), float> _measureCache = new();

    public float MeasureText(string text, FontId fontId, float size)
    {
        size = MathF.Round(size * 2) / 2;
        var key = (text, fontId, size);
        if (_measureCache.TryGetValue(key, out var cached))
            return cached;

        if (_measureCache.Count > 4000)
            _measureCache.Clear();

        // Try atlas-based measurement first (faster, no font engine)
        if (fontId != FontId.Symbols && !ContainsSymbols(text))
        {
            var atlas = GetAsciiAtlas(fontId, size);
            if (GlyphAtlas.IsAllInAtlas(text, atlas))
            {
                var w = atlas.MeasureString(text);
                _measureCache[key] = w;
                return w;
            }
        }

        var font = GetFont(fontId, size);
        var width = font.MeasureText(text);
        _measureCache[key] = width;
        return width;
    }

    public static SKColor UnpackColor(uint color)
    {
        return new SKColor(
            (byte)((color >> 24) & 0xFF),
            (byte)((color >> 16) & 0xFF),
            (byte)((color >> 8) & 0xFF),
            (byte)(color & 0xFF));
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _fillPaint.Dispose();
        _strokePaint.Dispose();
        _regular.Dispose();
        _bold.Dispose();
        _symbols.Dispose();
        foreach (var font in _fonts.Values) font.Dispose();
        foreach (var atlas in _numericAtlases.Values) atlas.Dispose();
        foreach (var atlas in _asciiAtlases.Values) atlas.Dispose();
        _fonts.Clear();
    }
}
