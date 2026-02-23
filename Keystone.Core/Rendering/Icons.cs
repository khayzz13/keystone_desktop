// Icons - Loads SVG icons from icons/ folder, parses them into RenderContext draw commands.
// Auto-generates default SVG files for any missing icons on first load.

using System.Globalization;
using System.Xml.Linq;

namespace Keystone.Core.Rendering;

public static class Icons
{
    private static readonly Dictionary<string, IconDef> _icons = new();
    private static bool _loaded;

    // Framework icon names — auto-generated as default SVGs if missing from icons/ dir.
    // Apps add custom icons via Icons.Register() at runtime.
    private static readonly string[] _allNames =
    [
        "dashboard", "settings", "plugins", "browser",
        "lock", "unlock", "delete", "collapse_left",
        "plus", "minus", "search", "refresh",
        "chevron_down", "chevron_right", "close", "pin"
    ];

    /// <summary>Initialize — call once at startup with the icons/ directory path.</summary>
    public static void Load(string iconsDir)
    {
        _icons.Clear();
        if (!Directory.Exists(iconsDir))
            Directory.CreateDirectory(iconsDir);

        GenerateMissing(iconsDir);

        foreach (var file in Directory.GetFiles(iconsDir, "*.svg"))
        {
            var name = Path.GetFileNameWithoutExtension(file);
            var def = ParseSvg(File.ReadAllText(file));
            if (def != null) _icons[name] = def;
        }

        _loaded = true;
        Console.WriteLine($"[Icons] Loaded {_icons.Count} icons from {iconsDir}");
    }

    /// <summary>Draw centered at (cx, cy).</summary>
    public static void Draw(RenderContext ctx, string name, float cx, float cy, float size, uint color)
    {
        if (!_loaded || !_icons.TryGetValue(name, out var icon)) return;
        DrawIcon(ctx, icon, cx - size / 2f, cy - size / 2f, size, color);
    }

    /// <summary>Draw centered at (cx, cy) using IconDef directly.</summary>
    public static void Draw(RenderContext ctx, IconDef icon, float cx, float cy, float size, uint color)
        => DrawIcon(ctx, icon, cx - size / 2f, cy - size / 2f, size, color);

    /// <summary>Draw at top-left (x, y).</summary>
    public static void DrawAt(RenderContext ctx, string name, float x, float y, float size, uint color)
    {
        if (!_loaded || !_icons.TryGetValue(name, out var icon)) return;
        DrawIcon(ctx, icon, x, y, size, color);
    }

    static void DrawIcon(RenderContext ctx, IconDef icon, float x, float y, float size, uint color)
    {
        var scale = size / MathF.Max(icon.VbW, icon.VbH);
        ctx.PushTransform(x, y, scaleX: scale, scaleY: scale);
        foreach (var cmd in icon.Commands) cmd(ctx, color);
        ctx.PopTransform();
    }

    /// <summary>Get icon by name.</summary>
    public static IconDef? Get(string name) => _icons.GetValueOrDefault(name);

    /// <summary>Register an icon from SVG content at runtime. Apps call this to add custom icons.</summary>
    public static void Register(string name, string svgContent)
    {
        var def = ParseSvg(svgContent);
        if (def != null) _icons[name] = def;
    }

    // ═══════════════════════════════════════════════════════════
    //  SVG PARSER
    // ═══════════════════════════════════════════════════════════

    static IconDef? ParseSvg(string svgText)
    {
        try
        {
            var doc = XDocument.Parse(svgText);
            var svg = doc.Root!;
            var ns = svg.Name.Namespace;
            var vb = ParseViewBox(svg.Attribute("viewBox")?.Value ?? "0 0 16 16");
            var commands = new List<Action<RenderContext, uint>>();

            var rootFill = svg.Attribute("fill")?.Value;
            var rootStroke = svg.Attribute("stroke")?.Value;
            var rootSw = svg.Attribute("stroke-width")?.Value;

            foreach (var el in svg.Elements())
                ParseElement(el, commands, rootFill, rootStroke, rootSw);

            return new IconDef(vb.w, vb.h, commands.ToArray());
        }
        catch { return null; }
    }

    static void ParseElement(XElement el, List<Action<RenderContext, uint>> cmds,
        string? parentFill, string? parentStroke, string? parentSw)
    {
        var tag = el.Name.LocalName;
        var fill = el.Attribute("fill")?.Value ?? parentFill;
        var stroke = el.Attribute("stroke")?.Value ?? parentStroke;
        var sw = Fl(el.Attribute("stroke-width")?.Value ?? parentSw ?? "1");
        var hasFill = fill is not null and not "none";
        var hasStroke = stroke is not null and not "none";

        switch (tag)
        {
            case "rect":
            {
                var x = Fl(el, "x"); var y = Fl(el, "y");
                var w = Fl(el, "width"); var h = Fl(el, "height");
                var rx = Fl(el, "rx", 0);
                if (hasFill) cmds.Add(rx > 0
                    ? (ctx, c) => ctx.RoundedRect(x, y, w, h, rx, c)
                    : (ctx, c) => ctx.Rect(x, y, w, h, c));
                if (hasStroke) cmds.Add(rx > 0
                    ? (ctx, c) => ctx.RoundedRectStroke(x, y, w, h, rx, sw, c)
                    : (ctx, c) => ctx.RectStroke(x, y, w, h, sw, c));
                break;
            }
            case "circle":
            {
                var cx = Fl(el, "cx"); var cy = Fl(el, "cy"); var r = Fl(el, "r");
                if (hasFill) cmds.Add((ctx, c) => ctx.Circle(cx, cy, r, c));
                if (hasStroke) cmds.Add((ctx, c) => ctx.CircleStroke(cx, cy, r, sw, c));
                break;
            }
            case "ellipse":
            {
                var cx = Fl(el, "cx"); var cy = Fl(el, "cy");
                var rx = Fl(el, "rx"); var ry = Fl(el, "ry");
                if (hasFill) cmds.Add((ctx, c) => ctx.Ellipse(cx, cy, rx, ry, c));
                if (hasStroke) cmds.Add((ctx, c) => ctx.EllipseStroke(cx, cy, rx, ry, sw, c));
                break;
            }
            case "line":
            {
                var x1 = Fl(el, "x1"); var y1 = Fl(el, "y1");
                var x2 = Fl(el, "x2"); var y2 = Fl(el, "y2");
                cmds.Add((ctx, c) => ctx.Line(x1, y1, x2, y2, sw, c));
                break;
            }
            case "path":
            {
                var d = el.Attribute("d")?.Value;
                if (d == null) break;
                var pathCmds = SvgPathParser.Parse(d);
                if (hasFill)
                    cmds.Add((ctx, c) => { ctx.PathBegin(); foreach (var pc in pathCmds) pc(ctx); ctx.PathFill(c); });
                if (hasStroke)
                    cmds.Add((ctx, c) => { ctx.PathBegin(); foreach (var pc in pathCmds) pc(ctx); ctx.PathStroke(sw, c); });
                if (!hasFill && !hasStroke)
                    cmds.Add((ctx, c) => { ctx.PathBegin(); foreach (var pc in pathCmds) pc(ctx); ctx.PathStroke(sw, c); });
                break;
            }
        }
    }

    // ═══════════════════════════════════════════════════════════
    //  HELPERS
    // ═══════════════════════════════════════════════════════════

    static float Fl(XElement el, string attr, float def = 0)
    {
        var v = el.Attribute(attr)?.Value;
        return v != null && float.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var f) ? f : def;
    }

    static float Fl(string? s) =>
        s != null && float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var f) ? f : 1;

    static (float w, float h) ParseViewBox(string vb)
    {
        var p = vb.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return p.Length >= 4
            ? (float.Parse(p[2], CultureInfo.InvariantCulture), float.Parse(p[3], CultureInfo.InvariantCulture))
            : (16, 16);
    }

    // ═══════════════════════════════════════════════════════════
    //  AUTO-GENERATE MISSING DEFAULT SVGs
    // ═══════════════════════════════════════════════════════════

    static void GenerateMissing(string dir)
    {
        foreach (var name in _allNames)
        {
            var path = Path.Combine(dir, name + ".svg");
            if (File.Exists(path)) continue;
            var svg = DefaultSvg(name);
            if (svg != null)
            {
                File.WriteAllText(path, svg);
                Console.WriteLine($"[Icons] Generated default: {name}.svg");
            }
        }
    }

    static string? DefaultSvg(string name) => name switch
    {
        "dashboard" =>"""<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 16 16" fill="none" stroke="currentColor" stroke-width="1.2"><rect x="1" y="1" width="6" height="6"/><rect x="9" y="1" width="6" height="6"/><rect x="1" y="9" width="6" height="6"/><rect x="9" y="9" width="6" height="6"/></svg>""",
        "settings" => """<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 16 16" fill="none" stroke="currentColor" stroke-width="2"><circle cx="8" cy="8" r="3" stroke-width="1.5"/><line x1="8" y1="1" x2="8" y2="3"/><line x1="8" y1="13" x2="8" y2="15"/><line x1="1" y1="8" x2="3" y2="8"/><line x1="13" y1="8" x2="15" y2="8"/><line x1="3" y1="3" x2="4.5" y2="4.5"/><line x1="11.5" y1="11.5" x2="13" y2="13"/><line x1="13" y1="3" x2="11.5" y2="4.5"/><line x1="3" y1="13" x2="4.5" y2="11.5"/></svg>""",
        "plugins" => """<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 16 16" fill="currentColor" stroke="currentColor"><rect x="3" y="3" width="10" height="10" fill="none" stroke-width="1.5"/><rect x="7" y="1" width="2" height="3" stroke="none"/><rect x="1" y="7" width="3" height="2" stroke="none"/></svg>""",
        "browser" => """<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 16 16" fill="none" stroke="currentColor"><circle cx="8" cy="8" r="6.5" stroke-width="1.2"/><line x1="1.5" y1="8" x2="14.5" y2="8" stroke-width="1"/><ellipse cx="8" cy="8" rx="3.5" ry="6.5" stroke-width="1"/></svg>""",
        "lock" => """<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 16 16" fill="currentColor" stroke="currentColor"><rect x="3" y="8" width="10" height="7" rx="1.5" stroke="none"/><path d="M5 8L5 5Q5 2 8 2Q11 2 11 5L11 8" fill="none" stroke-width="1.5"/><circle cx="8" cy="11.5" r="1.5" fill="#000" stroke="none"/></svg>""",
        "unlock" => """<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 16 16" fill="currentColor" stroke="currentColor"><rect x="3" y="8" width="10" height="7" rx="1.5" stroke="none"/><path d="M5 8L5 5Q5 2 8 2Q11 2 11 5" fill="none" stroke-width="1.5"/><circle cx="8" cy="11.5" r="1.5" fill="#000" stroke="none"/></svg>""",
        "delete" => """<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 16 16" fill="none" stroke="currentColor" stroke-width="2"><line x1="3" y1="3" x2="13" y2="13"/><line x1="13" y1="3" x2="3" y2="13"/></svg>""",
        "collapse_left" => """<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 16 16" fill="none" stroke="currentColor" stroke-width="2"><path d="M10 3L5 8L10 13"/></svg>""",
        "plus" => """<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 16 16" fill="none" stroke="currentColor" stroke-width="2"><line x1="8" y1="3" x2="8" y2="13"/><line x1="3" y1="8" x2="13" y2="8"/></svg>""",
        "minus" => """<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 16 16" fill="none" stroke="currentColor" stroke-width="2"><line x1="3" y1="8" x2="13" y2="8"/></svg>""",
        "search" => """<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 16 16" fill="none" stroke="currentColor"><circle cx="7" cy="7" r="4" stroke-width="1.5"/><line x1="10" y1="10" x2="14" y2="14" stroke-width="2"/></svg>""",
        "refresh" => """<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 16 16" fill="none" stroke="currentColor" stroke-width="1.5"><path d="M13 8Q13 3 8 3Q3 3 3 8Q3 13 8 13"/><path d="M13 5L13 9L9 9"/></svg>""",
        "chevron_down" => """<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 16 16" fill="none" stroke="currentColor" stroke-width="2"><path d="M4 6L8 10L12 6"/></svg>""",
        "chevron_right" => """<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 16 16" fill="none" stroke="currentColor" stroke-width="2"><path d="M6 4L10 8L6 12"/></svg>""",
        "close" => """<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 16 16" fill="none" stroke="currentColor"><circle cx="8" cy="8" r="6" stroke-width="1.2"/><line x1="5" y1="5" x2="11" y2="11" stroke-width="1.5"/><line x1="11" y1="5" x2="5" y2="11" stroke-width="1.5"/></svg>""",
        "pin" => """<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 16 16" fill="currentColor" stroke="currentColor"><path d="M6 2L10 2L11 7L5 7Z" stroke="none"/><line x1="8" y1="7" x2="8" y2="14" stroke-width="1.5"/><line x1="4" y1="7" x2="12" y2="7" stroke-width="1.5"/></svg>""",
        _ => null
    };
}

/// <summary>Parsed icon — viewbox dimensions + draw commands.</summary>
public class IconDef
{
    public float VbW { get; }
    public float VbH { get; }
    public Action<RenderContext, uint>[] Commands { get; }
    public IconDef(float vbW, float vbH, Action<RenderContext, uint>[] commands)
    {
        VbW = vbW;
        VbH = vbH;
        Commands = commands;
    }
}
