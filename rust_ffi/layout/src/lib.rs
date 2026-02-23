//! Keystone Layout - Taffy flexbox/grid engine with C FFI
//!
//! Exposes a flat C API for C# P/Invoke. Each LayoutTree is an opaque handle
//! wrapping a TaffyTree. Nodes are referenced by u64 IDs.

use taffy::prelude::*;
use taffy::{GridTemplateComponent, MinMax, Overflow};

// ============================================================================
// Opaque handle
// ============================================================================

pub struct LayoutTree {
    tree: TaffyTree,
}

// ============================================================================
// Tree lifecycle
// ============================================================================

#[no_mangle]
pub extern "C" fn layout_tree_new() -> *mut LayoutTree {
    Box::into_raw(Box::new(LayoutTree {
        tree: TaffyTree::new(),
    }))
}

#[no_mangle]
pub extern "C" fn layout_tree_free(ptr: *mut LayoutTree) {
    if !ptr.is_null() {
        unsafe { drop(Box::from_raw(ptr)) };
    }
}

// ============================================================================
// Node creation
// ============================================================================

#[no_mangle]
pub extern "C" fn layout_new_node(tree: &mut LayoutTree) -> u64 {
    tree.tree.new_leaf(Style::default()).unwrap().into()
}

#[no_mangle]
pub extern "C" fn layout_new_node_with_children(
    tree: &mut LayoutTree, children: *const u64, count: usize,
) -> u64 {
    let kids: Vec<NodeId> = unsafe {
        std::slice::from_raw_parts(children, count)
            .iter().map(|&id| NodeId::from(id)).collect()
    };
    tree.tree.new_with_children(Style::default(), &kids).unwrap().into()
}

#[no_mangle]
pub extern "C" fn layout_add_child(tree: &mut LayoutTree, parent: u64, child: u64) {
    let _ = tree.tree.add_child(NodeId::from(parent), NodeId::from(child));
}

#[no_mangle]
pub extern "C" fn layout_remove_node(tree: &mut LayoutTree, node: u64) {
    let _ = tree.tree.remove(NodeId::from(node));
}

// ============================================================================
// Style setters
// ============================================================================

#[no_mangle]
pub extern "C" fn layout_set_display(tree: &mut LayoutTree, node: u64, display: u8) {
    mutate_style(tree, node, |s| {
        s.display = match display {
            1 => Display::None,
            2 => Display::Grid,
            3 => Display::Block,
            _ => Display::Flex,
        };
    });
}

#[no_mangle]
pub extern "C" fn layout_set_flex_direction(tree: &mut LayoutTree, node: u64, dir: u8) {
    mutate_style(tree, node, |s| {
        s.flex_direction = match dir {
            1 => FlexDirection::Row,
            2 => FlexDirection::ColumnReverse,
            3 => FlexDirection::RowReverse,
            _ => FlexDirection::Column,
        };
    });
}

#[no_mangle]
pub extern "C" fn layout_set_flex_wrap(tree: &mut LayoutTree, node: u64, wrap: u8) {
    mutate_style(tree, node, |s| {
        s.flex_wrap = match wrap {
            1 => FlexWrap::Wrap,
            2 => FlexWrap::WrapReverse,
            _ => FlexWrap::NoWrap,
        };
    });
}

#[no_mangle]
pub extern "C" fn layout_set_flex_grow(tree: &mut LayoutTree, node: u64, val: f32) {
    mutate_style(tree, node, |s| s.flex_grow = val);
}

#[no_mangle]
pub extern "C" fn layout_set_flex_shrink(tree: &mut LayoutTree, node: u64, val: f32) {
    mutate_style(tree, node, |s| s.flex_shrink = val);
}

#[no_mangle]
pub extern "C" fn layout_set_flex_basis(tree: &mut LayoutTree, node: u64, val: f32) {
    mutate_style(tree, node, |s| s.flex_basis = Dimension::length(val));
}

#[no_mangle]
pub extern "C" fn layout_set_align_items(tree: &mut LayoutTree, node: u64, val: u8) {
    mutate_style(tree, node, |s| s.align_items = Some(map_align_items(val)));
}

#[no_mangle]
pub extern "C" fn layout_set_justify_content(tree: &mut LayoutTree, node: u64, val: u8) {
    mutate_style(tree, node, |s| s.justify_content = Some(map_justify_content(val)));
}

#[no_mangle]
pub extern "C" fn layout_set_align_self(tree: &mut LayoutTree, node: u64, val: u8) {
    mutate_style(tree, node, |s| s.align_self = Some(map_align_self(val)));
}

#[no_mangle]
pub extern "C" fn layout_set_width(tree: &mut LayoutTree, node: u64, val: f32) {
    mutate_style(tree, node, |s| s.size.width = Dimension::length(val));
}

#[no_mangle]
pub extern "C" fn layout_set_height(tree: &mut LayoutTree, node: u64, val: f32) {
    mutate_style(tree, node, |s| s.size.height = Dimension::length(val));
}

#[no_mangle]
pub extern "C" fn layout_set_width_percent(tree: &mut LayoutTree, node: u64, val: f32) {
    mutate_style(tree, node, |s| s.size.width = Dimension::percent(val / 100.0));
}

#[no_mangle]
pub extern "C" fn layout_set_height_percent(tree: &mut LayoutTree, node: u64, val: f32) {
    mutate_style(tree, node, |s| s.size.height = Dimension::percent(val / 100.0));
}

#[no_mangle]
pub extern "C" fn layout_set_min_width(tree: &mut LayoutTree, node: u64, val: f32) {
    mutate_style(tree, node, |s| s.min_size.width = Dimension::length(val));
}

#[no_mangle]
pub extern "C" fn layout_set_min_height(tree: &mut LayoutTree, node: u64, val: f32) {
    mutate_style(tree, node, |s| s.min_size.height = Dimension::length(val));
}

#[no_mangle]
pub extern "C" fn layout_set_max_width(tree: &mut LayoutTree, node: u64, val: f32) {
    mutate_style(tree, node, |s| s.max_size.width = Dimension::length(val));
}

#[no_mangle]
pub extern "C" fn layout_set_max_height(tree: &mut LayoutTree, node: u64, val: f32) {
    mutate_style(tree, node, |s| s.max_size.height = Dimension::length(val));
}

#[no_mangle]
pub extern "C" fn layout_set_padding(tree: &mut LayoutTree, node: u64, edge: u8, val: f32) {
    mutate_style(tree, node, |s| set_edge_lp(&mut s.padding, edge, val));
}

#[no_mangle]
pub extern "C" fn layout_set_margin(tree: &mut LayoutTree, node: u64, edge: u8, val: f32) {
    mutate_style(tree, node, |s| set_edge_lpa(&mut s.margin, edge, val));
}

#[no_mangle]
pub extern "C" fn layout_set_gap_row(tree: &mut LayoutTree, node: u64, val: f32) {
    mutate_style(tree, node, |s| s.gap.height = LengthPercentage::length(val));
}

#[no_mangle]
pub extern "C" fn layout_set_gap_column(tree: &mut LayoutTree, node: u64, val: f32) {
    mutate_style(tree, node, |s| s.gap.width = LengthPercentage::length(val));
}

#[no_mangle]
pub extern "C" fn layout_set_gap_all(tree: &mut LayoutTree, node: u64, val: f32) {
    mutate_style(tree, node, |s| {
        s.gap.width = LengthPercentage::length(val);
        s.gap.height = LengthPercentage::length(val);
    });
}

#[no_mangle]
pub extern "C" fn layout_set_position_type(tree: &mut LayoutTree, node: u64, val: u8) {
    mutate_style(tree, node, |s| {
        s.position = match val {
            1 => Position::Absolute,
            _ => Position::Relative,
        };
    });
}

#[no_mangle]
pub extern "C" fn layout_set_position(tree: &mut LayoutTree, node: u64, edge: u8, val: f32) {
    mutate_style(tree, node, |s| {
        let v = LengthPercentageAuto::length(val);
        match edge {
            0 => s.inset.left = v,
            1 => s.inset.top = v,
            2 => s.inset.right = v,
            3 => s.inset.bottom = v,
            _ => { s.inset.left = v; s.inset.top = v; s.inset.right = v; s.inset.bottom = v; }
        }
    });
}

#[no_mangle]
pub extern "C" fn layout_set_aspect_ratio(tree: &mut LayoutTree, node: u64, val: f32) {
    mutate_style(tree, node, |s| s.aspect_ratio = Some(val));
}

// ============================================================================
// CSS Grid — template + placement
// ============================================================================

#[no_mangle]
pub extern "C" fn layout_set_grid_template_columns(
    tree: &mut LayoutTree, node: u64, vals: *const f32, count: usize,
) {
    let tracks = parse_track_list(vals, count);
    mutate_style(tree, node, |s| s.grid_template_columns = tracks.clone());
}

#[no_mangle]
pub extern "C" fn layout_set_grid_template_rows(
    tree: &mut LayoutTree, node: u64, vals: *const f32, count: usize,
) {
    let tracks = parse_track_list(vals, count);
    mutate_style(tree, node, |s| s.grid_template_rows = tracks.clone());
}

#[no_mangle]
pub extern "C" fn layout_set_grid_placement(
    tree: &mut LayoutTree, node: u64,
    row: i16, col: i16, span_rows: u16, span_cols: u16,
) {
    mutate_style(tree, node, |s| {
        if row != 0 {
            s.grid_row = Line {
                start: GridPlacement::from_line_index(row),
                end: GridPlacement::from_span(span_rows.max(1)),
            };
        }
        if col != 0 {
            s.grid_column = Line {
                start: GridPlacement::from_line_index(col),
                end: GridPlacement::from_span(span_cols.max(1)),
            };
        }
    });
}

// ============================================================================
// Overflow
// ============================================================================

#[no_mangle]
pub extern "C" fn layout_set_overflow(tree: &mut LayoutTree, node: u64, overflow: u8) {
    mutate_style(tree, node, |s| {
        let v = match overflow {
            1 => Overflow::Hidden,
            2 => Overflow::Scroll,
            _ => Overflow::Visible,
        };
        s.overflow.x = v;
        s.overflow.y = v;
    });
}

// ============================================================================
// Layout computation + results
// ============================================================================

#[no_mangle]
pub extern "C" fn layout_compute(tree: &mut LayoutTree, node: u64, width: f32, height: f32) {
    let avail = Size {
        width: AvailableSpace::Definite(width),
        height: AvailableSpace::Definite(height),
    };
    let _ = tree.tree.compute_layout(NodeId::from(node), avail);
}

#[no_mangle]
pub extern "C" fn layout_get_result(
    tree: &LayoutTree, node: u64,
    out_x: &mut f32, out_y: &mut f32, out_w: &mut f32, out_h: &mut f32,
) {
    if let Ok(layout) = tree.tree.layout(NodeId::from(node)) {
        *out_x = layout.location.x;
        *out_y = layout.location.y;
        *out_w = layout.size.width;
        *out_h = layout.size.height;
    }
}

#[no_mangle]
pub extern "C" fn layout_child_count(tree: &LayoutTree, node: u64) -> usize {
    tree.tree.child_count(NodeId::from(node))
}

#[no_mangle]
pub extern "C" fn layout_get_child(tree: &LayoutTree, node: u64, index: usize) -> u64 {
    tree.tree.child_at_index(NodeId::from(node), index).unwrap().into()
}

// ============================================================================
// Helpers
// ============================================================================

fn mutate_style(tree: &mut LayoutTree, node: u64, f: impl FnOnce(&mut Style)) {
    let _ = tree.tree.set_style(NodeId::from(node), {
        let mut style = tree.tree.style(NodeId::from(node)).unwrap().clone();
        f(&mut style);
        style
    });
}

fn map_align_items(val: u8) -> AlignItems {
    match val {
        1 => AlignItems::FlexStart,
        2 => AlignItems::Center,
        3 => AlignItems::FlexEnd,
        4 => AlignItems::Stretch,
        5 => AlignItems::Baseline,
        _ => AlignItems::FlexStart,
    }
}

fn map_justify_content(val: u8) -> JustifyContent {
    match val {
        1 => JustifyContent::FlexStart,
        2 => JustifyContent::Center,
        3 => JustifyContent::FlexEnd,
        4 => JustifyContent::SpaceBetween,
        5 => JustifyContent::SpaceAround,
        6 => JustifyContent::SpaceEvenly,
        _ => JustifyContent::FlexStart,
    }
}

fn map_align_self(val: u8) -> AlignSelf {
    match val {
        1 => AlignSelf::FlexStart,
        2 => AlignSelf::Center,
        3 => AlignSelf::FlexEnd,
        4 => AlignSelf::Stretch,
        5 => AlignSelf::Baseline,
        _ => AlignSelf::FlexStart,
    }
}

fn set_edge_lp(rect: &mut Rect<LengthPercentage>, edge: u8, val: f32) {
    let v = LengthPercentage::length(val);
    match edge {
        0 => rect.left = v,
        1 => rect.top = v,
        2 => rect.right = v,
        3 => rect.bottom = v,
        _ => { rect.left = v; rect.top = v; rect.right = v; rect.bottom = v; }
    }
}

fn set_edge_lpa(rect: &mut Rect<LengthPercentageAuto>, edge: u8, val: f32) {
    let v = LengthPercentageAuto::length(val);
    match edge {
        0 => rect.left = v,
        1 => rect.top = v,
        2 => rect.right = v,
        3 => rect.bottom = v,
        _ => { rect.left = v; rect.top = v; rect.right = v; rect.bottom = v; }
    }
}

/// Parse track list from f32 array. val > 0 = px, val < 0 = fr, val == 0 = auto.
fn parse_track_list(vals: *const f32, count: usize) -> Vec<GridTemplateComponent<String>> {
    let slice = unsafe { std::slice::from_raw_parts(vals, count) };
    slice.iter().map(|&v| {
        let tsf = if v > 0.0 {
            MinMax { min: MinTrackSizingFunction::length(v), max: MaxTrackSizingFunction::length(v) }
        } else if v < 0.0 {
            MinMax { min: MinTrackSizingFunction::length(0.0), max: MaxTrackSizingFunction::fr(v.abs()) }
        } else {
            MinMax { min: MinTrackSizingFunction::auto(), max: MaxTrackSizingFunction::auto() }
        };
        GridTemplateComponent::from(tsf)
    }).collect()
}
