// SvgPathParser - Parses SVG path `d` attribute into RenderContext draw commands
// Supports: M/m L/l H/h V/v Q/q C/c Z/z (absolute + relative)
// Extracted from Icons.cs for reuse by FlexRenderer (Bun SVG path primitive)

using System.Globalization;

namespace Keystone.Core.Rendering;

public static class SvgPathParser
{
    public static Action<RenderContext>[] Parse(string d)
    {
        var cmds = new List<Action<RenderContext>>();
        var i = 0;
        float cx = 0, cy = 0;

        while (i < d.Length)
        {
            SkipWs(d, ref i);
            if (i >= d.Length) break;
            var ch = d[i];
            if (!char.IsLetter(ch)) { i++; continue; }
            i++;
            var abs = char.IsUpper(ch);
            ch = char.ToUpper(ch);

            switch (ch)
            {
                case 'M':
                {
                    var x = NextF(d, ref i); var y = NextF(d, ref i);
                    if (!abs) { x += cx; y += cy; }
                    cx = x; cy = y;
                    var fx = x; var fy = y;
                    cmds.Add(c => c.PathMoveTo(fx, fy));
                    while (HasF(d, i))
                    {
                        x = NextF(d, ref i); y = NextF(d, ref i);
                        if (!abs) { x += cx; y += cy; }
                        cx = x; cy = y;
                        var lx = x; var ly = y;
                        cmds.Add(c => c.PathLineTo(lx, ly));
                    }
                    break;
                }
                case 'L':
                    while (HasF(d, i))
                    {
                        var x = NextF(d, ref i); var y = NextF(d, ref i);
                        if (!abs) { x += cx; y += cy; }
                        cx = x; cy = y;
                        var lx = x; var ly = y;
                        cmds.Add(c => c.PathLineTo(lx, ly));
                    }
                    break;
                case 'H':
                    while (HasF(d, i))
                    {
                        var x = NextF(d, ref i);
                        if (!abs) x += cx;
                        cx = x;
                        var hx = x; var hy = cy;
                        cmds.Add(c => c.PathLineTo(hx, hy));
                    }
                    break;
                case 'V':
                    while (HasF(d, i))
                    {
                        var y = NextF(d, ref i);
                        if (!abs) y += cy;
                        cy = y;
                        var vx = cx; var vy = y;
                        cmds.Add(c => c.PathLineTo(vx, vy));
                    }
                    break;
                case 'Q':
                    while (HasF(d, i))
                    {
                        var x1 = NextF(d, ref i); var y1 = NextF(d, ref i);
                        var x = NextF(d, ref i); var y = NextF(d, ref i);
                        if (!abs) { x1 += cx; y1 += cy; x += cx; y += cy; }
                        cx = x; cy = y;
                        var qx1 = x1; var qy1 = y1; var qx = x; var qy = y;
                        cmds.Add(c => c.PathQuadTo(qx1, qy1, qx, qy));
                    }
                    break;
                case 'C':
                    while (HasF(d, i))
                    {
                        var x1 = NextF(d, ref i); var y1 = NextF(d, ref i);
                        var x2 = NextF(d, ref i); var y2 = NextF(d, ref i);
                        var x = NextF(d, ref i); var y = NextF(d, ref i);
                        if (!abs) { x1 += cx; y1 += cy; x2 += cx; y2 += cy; x += cx; y += cy; }
                        cx = x; cy = y;
                        var a = x1; var b = y1; var e = x2; var f = y2; var g = x; var h = y;
                        cmds.Add(c => c.PathCubicTo(a, b, e, f, g, h));
                    }
                    break;
                case 'Z':
                    cmds.Add(c => c.PathClose());
                    break;
            }
        }
        return cmds.ToArray();
    }

    static void SkipWs(string s, ref int i)
    {
        while (i < s.Length && (s[i] is ' ' or ',' or '\n' or '\r' or '\t')) i++;
    }

    static bool HasF(string s, int i)
    {
        while (i < s.Length && (s[i] is ' ' or ',' or '\n' or '\r' or '\t')) i++;
        return i < s.Length && (char.IsDigit(s[i]) || s[i] is '-' or '.' or '+');
    }

    static float NextF(string s, ref int i)
    {
        SkipWs(s, ref i);
        var start = i;
        if (i < s.Length && s[i] is '-' or '+') i++;
        while (i < s.Length && (char.IsDigit(s[i]) || s[i] == '.')) i++;
        if (i < s.Length && s[i] is 'e' or 'E')
        {
            i++;
            if (i < s.Length && s[i] is '-' or '+') i++;
            while (i < s.Length && char.IsDigit(s[i])) i++;
        }
        return float.TryParse(s.AsSpan(start, i - start), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : 0;
    }
}
