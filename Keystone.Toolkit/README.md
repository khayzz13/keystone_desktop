# Keystone.Toolkit

Pre-built UI component library for native GPU/Skia windows. Sits on top of `Keystone.Core` (FlexNode, RenderContext, ButtonRegistry) and provides themed, composable building blocks so window plugins don't hand-code `ctx.Rect` / `ctx.Text` for standard UI patterns.

Everything is static methods returning `FlexNode` trees (for scene graph windows) or drawing directly via `RenderContext` (for immediate-mode windows). No state, no instances, no lifecycle — pure functions that produce UI.

---

## Dependencies

`Keystone.Core` only. No platform or graphics references — works on macOS and Linux.

---

## Files

| File | What it provides |
|------|-----------------|
| `Theme.cs` | Global color/size tokens — all static mutable fields, override at startup to retheme |
| `Layout.cs` | Container components: chrome panels, cards, strips, section headers, stat cards, status rows, badges, dividers, empty states |
| `Controls.cs` | Interactive components: spinners, checkboxes, toggle buttons, tab strips, chips, dropdowns, preset rows, action buttons, text inputs |
| `Buttons.cs` | Immediate-mode button rendering with `ButtonRegistry` hitbox registration (text, icon, close, minimize, toggle, tab, menu item, drag region) |
| `TitleBar.cs` | Standard window title bar with tabs, close/minimize/float-toggle, bind mode, drag region |
| `BindTitleBar.cs` | Title bar variant for bound window groups (wider, close-bind action) |
| `ContextMenu.cs` | Right-click context menu system with buttons, separators, sliders, color wheels, submenus |
| `DataTable.cs` | Scrollable data table with fixed header, alternating row colors, per-cell styling |
| `NotificationOverlay.cs` | Toast notification stack (top-right corner) — auto-dismiss info/warning, persistent errors |
| `Format.cs` | Formatting utilities: age ("3m ago"), volume ("1.2M"), bytes ("3.4 MB"), percent, duration |

---

## Theme

All colors and sizes are static fields on `Theme`. Override them at startup to retheme every toolkit component:

```csharp
Theme.BgSurface = 0x1e1e23ff;
Theme.Accent = 0x4a6fa5ff;
Theme.TextPrimary = 0xccccccff;
Theme.TitleBarHeight = 44f;
```

Call `Theme.Reset()` to restore defaults. The default palette is a dark theme with blue accent.

### Color categories

- **Surface hierarchy**: `BgBase` (deepest) → `BgSurface` → `BgElevated` → `BgChrome` → `BgStrip`
- **Interactive states**: `BgButton`, `BgButtonHover`, `BgHover`, `BgPressed`
- **Semantic**: `Accent`, `AccentBright`, `Success`, `Warning`, `Danger`
- **Text**: `TextPrimary`, `TextSecondary`, `TextMuted`, `TextSubtle`

### Standard sizes

`TitleBarHeight` (44), `BindTitleBarHeight` (48), `StripHeight` (40), `PadX` (10), `GapX` (8), `CornerRadius` (4), `BtnSize` (24).

---

## Layout — Containers and Display

Returns `FlexNode` trees. Compose into scene graph via `BuildScene()`.

```csharp
// Chrome panel with stroke border
var panel = Layout.ChromePanel(pad: 14, radius: 10);

// Card with dark background
var card = Layout.Card(pad: 14, gap: 8);

// Toolbar strip
var strip = Layout.Strip();

// Section header: "SETTINGS" + divider line
var header = Layout.SectionHeader("Settings");

// Stat cards
var stats = Layout.StatRow3(
    "CPU", "42%", Theme.Success,
    "MEM", "1.2G", Theme.Warning,
    "GPU", "88%", Theme.Danger);

// Setting row: label left, value right
var row = Layout.SettingRow("Theme", "Dark", action: "toggle_theme");

// Status indicator
var status = Layout.StatusRow("Gateway", "Connected", Theme.Success);

// Connection dot with latency
var dot = Layout.ConnectionDot(connected: true, latencyMs: 23);
```

Also: `Layout.Divider()`, `Layout.Label()`, `Layout.Badge()`, `Layout.EmptyState()`, `Layout.WarningPanel()`.

---

## Controls — Interactive Components

Returns `FlexNode` trees with `Action` strings wired for the action router.

```csharp
// Spinner: [-] [value] [+]
var spinner = Controls.Spinner("5", "dec_qty", "inc_qty");
var mini = Controls.MiniSpinner("10", "dec", "inc");

// Checkbox + label
var check = Controls.CheckRow("Enable logging", isChecked, "toggle_logging");

// Toggle button
var toggle = Controls.ToggleButton("Auto", "toggle_auto", isActive);

// Tab strip
var tabs = Controls.TabStrip(
    new[] { Mode.Day, Mode.Week, Mode.Month },
    new[] { "Day", "Week", "Month" },
    currentMode, "set_mode");

// Chip filters
var chips = Controls.ChipRow(
    new[] { "All", "Open", "Closed" },
    new[] { "filter:all", "filter:open", "filter:closed" },
    activeIndex: 0);

// Dropdown
var dropdown = Controls.Dropdown("USD", currencies, "set_currency",
    isOpen: dropdownOpen, toggleAction: "toggle_dropdown", selectedIndex: 0);

// Preset row: [1] [2] [5] [10]
var presets = Controls.PresetRow(new[] { 1, 2, 5, 10 }, current: 5, "set_qty");

// Large action button
var buy = Controls.ActionButton("BUY", "AAPL", "buy", Theme.Success);

// Text input
var input = Controls.TextInput(textEntry, placeholder: "Search...");
var labeled = Controls.LabeledInput("Symbol", textEntry, "e.g. AAPL");
```

---

## Buttons — Immediate Mode

For windows using `Render(RenderContext ctx)` instead of scene graph. Each method draws directly on the canvas and registers a hitbox in `ButtonRegistry`.

```csharp
// Text button — returns rendered width
float w = Buttons.Text(ctx, buttons, x, y, 28, "Save", "save", 13f);

// Icon button
Buttons.Icon(ctx, buttons, x, y, 24, "⚙", "settings", 14f);

// Close / minimize
Buttons.Close(ctx, buttons, x, y, 24);
Buttons.Minimize(ctx, buttons, x, y, 24);

// Toggle
Buttons.Toggle(ctx, buttons, x, y, 60, 28, "Auto", "toggle", isActive, 13f);

// Tab
Buttons.Tab(ctx, buttons, x, y, 28, "Chart", "tab:chart", isActive, 13f);

// Menu item (dropdown row)
Buttons.MenuItem(ctx, buttons, x, y, 160, 24, "Delete", "delete", selected: false, 12f);

// Invisible drag region
Buttons.DragRegion(buttons, x, y, w, h);
```

---

## TitleBar

Standard window title bar. Handles close, minimize, float toggle, tab groups, bind mode, and drag regions. Works in both modes:

```csharp
// Scene graph — returns FlexNode
var titleBar = TitleBar.Build(state, width);

// Immediate mode — draws directly
TitleBar.Render(ctx, buttons, x, y, width);
```

`BindTitleBar.Render()` is the variant for bound window groups — wider, with close-bind instead of close-window.

---

## ContextMenu

Right-click menu system. Create a `ContextMenuState` per window, open with items, render each frame:

```csharp
// State (create once, reuse)
var menuState = new ContextMenuState();

// Open on right-click
if (state.RightClick)
    ContextMenu.Open(menuState, state.MouseX, state.MouseY, new List<ContextItem>
    {
        new() { Id = "copy", Label = "Copy", OnClick = () => DoCopy() },
        new() { Type = ContextItemType.Separator },
        new() { Id = "color", Label = "Color", Type = ContextItemType.ColorWheel,
                ColorValue = 0xFF0000FF, OnColorChange = c => SetColor(c) },
        new() { Id = "opacity", Label = "Opacity", Type = ContextItemType.Slider,
                SliderValue = 0.8f, SliderMin = 0, SliderMax = 1,
                OnSliderChange = v => SetOpacity(v) },
        new() { Id = "more", Label = "More...", Type = ContextItemType.Submenu,
                Submenu = new() { new() { Id = "del", Label = "Delete", OnClick = () => Delete() } } }
    });

// Render (immediate mode, call every frame)
ContextMenu.Render(ctx, menuState);
```

Item types: `Button`, `Separator`, `Slider`, `ColorWheel`, `Submenu`.

---

## DataTable

Scrollable table with fixed header and builder pattern:

```csharp
var table = DataTableFactory.Create(scrollState,
    headers: new[] { "Symbol", "Price", "Change" },
    widths:  new[] { 100f, 80f, 0f })  // 0 = flex-grow
    .AddRow(new[] { "AAPL", "185.20", "+1.3%" },
            colors: new[] { Theme.TextPrimary, Theme.TextPrimary, Theme.Success })
    .AddRow(new[] { "TSLA", "242.10", "-0.8%" },
            colors: new[] { Theme.TextPrimary, Theme.TextPrimary, Theme.Danger })
    .Build();
```

---

## NotificationOverlay

Global toast notifications. Auto-subscribes to `Notifications.OnNotification`. Info/warning auto-dismiss after 5s; errors persist.

```csharp
// Scene graph — append to your root
var toasts = NotificationOverlay.Build();
if (toasts != null) root.Child(toasts);

// Immediate mode — call at end of Render()
NotificationOverlay.Render(ctx);
```

---

## Format

Stateless formatting utilities:

```csharp
Format.FormatAge(TimeSpan.FromMinutes(45))   // "45m ago"
Format.FormatVolume(1_500_000)                // "1.5M"
Format.FormatBytes(3_500_000)                 // "3.3 MB"
Format.FormatPercent(0.1234)                  // "12.34%"
Format.FormatDuration(TimeSpan.FromSeconds(90)) // "1:30"
```
