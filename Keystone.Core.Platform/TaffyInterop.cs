// TaffyInterop - P/Invoke bindings to libkeystone_layout (Taffy flexbox/grid)
using System.Runtime.InteropServices;

namespace Keystone.Core.Platform;

public static class Taffy
{
    private const string Lib = "libkeystone_layout";

    // Enum mappings (match Rust u8 values)
    public enum Display : byte { Flex = 0, None = 1, Grid = 2, Block = 3 }
    public enum FlexDirection : byte { Column = 0, Row = 1, ColumnReverse = 2, RowReverse = 3 }
    public enum FlexWrap : byte { NoWrap = 0, Wrap = 1, WrapReverse = 2 }
    public enum AlignItems : byte { Auto = 0, FlexStart = 1, Center = 2, FlexEnd = 3, Stretch = 4, Baseline = 5 }
    public enum JustifyContent : byte { Auto = 0, FlexStart = 1, Center = 2, FlexEnd = 3, SpaceBetween = 4, SpaceAround = 5, SpaceEvenly = 6 }
    public enum PositionType : byte { Relative = 0, Absolute = 1 }
    public enum Edge : byte { Left = 0, Top = 1, Right = 2, Bottom = 3, All = 4 }

    // Tree lifecycle
    [DllImport(Lib)] public static extern IntPtr layout_tree_new();
    [DllImport(Lib)] public static extern void layout_tree_free(IntPtr tree);

    // Node creation
    [DllImport(Lib)] public static extern ulong layout_new_node(IntPtr tree);
    [DllImport(Lib)] public static extern ulong layout_new_node_with_children(IntPtr tree, ulong[] children, nuint count);
    [DllImport(Lib)] public static extern void layout_add_child(IntPtr tree, ulong parent, ulong child);
    [DllImport(Lib)] public static extern void layout_remove_node(IntPtr tree, ulong node);

    // Style: display, direction, wrap
    [DllImport(Lib)] public static extern void layout_set_display(IntPtr tree, ulong node, byte display);
    [DllImport(Lib)] public static extern void layout_set_flex_direction(IntPtr tree, ulong node, byte dir);
    [DllImport(Lib)] public static extern void layout_set_flex_wrap(IntPtr tree, ulong node, byte wrap);

    // Style: flex grow/shrink/basis
    [DllImport(Lib)] public static extern void layout_set_flex_grow(IntPtr tree, ulong node, float val);
    [DllImport(Lib)] public static extern void layout_set_flex_shrink(IntPtr tree, ulong node, float val);
    [DllImport(Lib)] public static extern void layout_set_flex_basis(IntPtr tree, ulong node, float val);

    // Style: alignment
    [DllImport(Lib)] public static extern void layout_set_align_items(IntPtr tree, ulong node, byte val);
    [DllImport(Lib)] public static extern void layout_set_justify_content(IntPtr tree, ulong node, byte val);
    [DllImport(Lib)] public static extern void layout_set_align_self(IntPtr tree, ulong node, byte val);

    // Style: dimensions (fixed)
    [DllImport(Lib)] public static extern void layout_set_width(IntPtr tree, ulong node, float val);
    [DllImport(Lib)] public static extern void layout_set_height(IntPtr tree, ulong node, float val);
    [DllImport(Lib)] public static extern void layout_set_min_width(IntPtr tree, ulong node, float val);
    [DllImport(Lib)] public static extern void layout_set_min_height(IntPtr tree, ulong node, float val);
    [DllImport(Lib)] public static extern void layout_set_max_width(IntPtr tree, ulong node, float val);
    [DllImport(Lib)] public static extern void layout_set_max_height(IntPtr tree, ulong node, float val);

    // Style: dimensions (percentage)
    [DllImport(Lib)] public static extern void layout_set_width_percent(IntPtr tree, ulong node, float val);
    [DllImport(Lib)] public static extern void layout_set_height_percent(IntPtr tree, ulong node, float val);

    // Style: spacing
    [DllImport(Lib)] public static extern void layout_set_padding(IntPtr tree, ulong node, byte edge, float val);
    [DllImport(Lib)] public static extern void layout_set_margin(IntPtr tree, ulong node, byte edge, float val);
    [DllImport(Lib)] public static extern void layout_set_gap_row(IntPtr tree, ulong node, float val);
    [DllImport(Lib)] public static extern void layout_set_gap_column(IntPtr tree, ulong node, float val);
    [DllImport(Lib)] public static extern void layout_set_gap_all(IntPtr tree, ulong node, float val);

    // Style: position
    [DllImport(Lib)] public static extern void layout_set_position_type(IntPtr tree, ulong node, byte val);
    [DllImport(Lib)] public static extern void layout_set_position(IntPtr tree, ulong node, byte edge, float val);

    // Style: aspect ratio
    [DllImport(Lib)] public static extern void layout_set_aspect_ratio(IntPtr tree, ulong node, float val);

    // Style: overflow (0=visible, 1=hidden, 2=scroll)
    [DllImport(Lib)] public static extern void layout_set_overflow(IntPtr tree, ulong node, byte val);

    // CSS Grid: template
    [DllImport(Lib)] public static extern void layout_set_grid_template_columns(IntPtr tree, ulong node, float[] vals, nuint count);
    [DllImport(Lib)] public static extern void layout_set_grid_template_rows(IntPtr tree, ulong node, float[] vals, nuint count);

    // CSS Grid: placement
    [DllImport(Lib)] public static extern void layout_set_grid_placement(IntPtr tree, ulong node, short row, short col, ushort spanRows, ushort spanCols);

    // Layout computation
    [DllImport(Lib)] public static extern void layout_compute(IntPtr tree, ulong node, float width, float height);

    // Layout results
    [DllImport(Lib)] public static extern void layout_get_result(IntPtr tree, ulong node,
        out float x, out float y, out float w, out float h);
    [DllImport(Lib)] public static extern nuint layout_child_count(IntPtr tree, ulong node);
    [DllImport(Lib)] public static extern ulong layout_get_child(IntPtr tree, ulong node, nuint index);
}
