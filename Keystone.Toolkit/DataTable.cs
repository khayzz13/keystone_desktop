// DataTable - Scrollable data table with fixed header and alternating row colors
// Ported from FlexControls.DataTableBuilder

using Keystone.Core;
using Keystone.Core.Rendering;
using Keystone.Core.UI;

namespace Keystone.Toolkit;

public static class DataTableFactory
{
    /// <summary>Create a data table builder. Call AddRow() then Build() to get the FlexNode.</summary>
    public static DataTableBuilder Create(ScrollState scroll, string[] headers, float[] widths,
        float headerFontSize = 15, float rowFontSize = 15, float rowHeight = 32, float headerHeight = 34)
        => new(scroll, headers, widths, headerFontSize, rowFontSize, rowHeight, headerHeight);
}

public class DataTableBuilder
{
    readonly ScrollState _scroll;
    readonly string[] _headers;
    readonly float[] _widths;
    readonly float _headerFontSize, _rowFontSize, _rowHeight, _headerHeight;
    readonly List<(FlexNode[] cells, string? action)> _rows = new();

    internal DataTableBuilder(ScrollState scroll, string[] headers, float[] widths,
        float headerFontSize, float rowFontSize, float rowHeight, float headerHeight)
    {
        _scroll = scroll; _headers = headers; _widths = widths;
        _headerFontSize = headerFontSize; _rowFontSize = rowFontSize;
        _rowHeight = rowHeight; _headerHeight = headerHeight;
    }

    /// <summary>Add a row of pre-built cells. Widths are applied automatically from column defs.</summary>
    public DataTableBuilder AddRow(FlexNode[] cells, string? action = null)
    { _rows.Add((cells, action)); return this; }

    /// <summary>Add a row from text values with optional colors.</summary>
    public DataTableBuilder AddRow(string[] values, uint[]? colors = null, FontId[]? fonts = null, string? action = null)
    {
        var cells = new FlexNode[values.Length];
        for (int i = 0; i < values.Length; i++)
        {
            var c = colors != null && i < colors.Length ? colors[i] : Theme.TextPrimary;
            var f = fonts != null && i < fonts.Length ? fonts[i] : FontId.Regular;
            cells[i] = Flex.Text(values[i], _rowFontSize, c, f);
        }
        _rows.Add((cells, action)); return this;
    }

    public FlexNode Build()
    {
        var col = Flex.Column(gap: 0);
        col.FlexGrow = 1;

        // Header
        var header = Flex.Row(gap: 0, pad: 10);
        header.BgColor = Theme.BgStrip; header.MinHeight = _headerHeight;
        for (int i = 0; i < _headers.Length; i++)
        {
            var h = Flex.Text(_headers[i], _headerFontSize, Theme.TextMuted, FontId.Bold);
            if (i < _widths.Length && _widths[i] > 0) h.Width = _widths[i];
            else h.FlexGrow = 1;
            header.Child(h);
        }
        col.Child(header);

        // Scrollable rows
        var scroll = Flex.Scrollable(_scroll);
        scroll.FlexGrow = 1;
        for (int r = 0; r < _rows.Count; r++)
        {
            var (cells, action) = _rows[r];
            var row = Flex.Row(gap: 0, pad: 10);
            row.MinHeight = _rowHeight;
            row.BgColor = r % 2 == 0 ? 0x1e1e26ff : 0x00000000u;
            if (action != null)
            {
                row.HoverBgColor = 0x2a2a36ffu;
                row.Action = action;
            }
            for (int c = 0; c < cells.Length; c++)
            {
                if (c < _widths.Length && _widths[c] > 0) cells[c].Width = _widths[c];
                else cells[c].FlexGrow = 1;
                row.Child(cells[c]);
            }
            scroll.Child(row);
        }
        col.Child(scroll);
        return col;
    }
}
