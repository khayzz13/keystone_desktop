// Keystone namespace — flattened imports for the engine host executable.
// App developers get these automatically via Keystone.Toolkit's build props.

// Pull entire namespaces into scope globally
global using Keystone.Core;
global using Keystone.Core.Plugins;
global using Keystone.Core.Rendering;
global using Keystone.Core.UI;
global using Keystone.Core.Runtime;
global using Keystone.Toolkit;

// Type aliases for the most common types — available as `Keystone.X` with `using Keystone;`
namespace Keystone
{
    // === App bootstrap ===
    // KeystoneApp — fluent builder: KeystoneApp.Create("My App", "com.me.app").Window("main").Run();

    // === Plugin interfaces ===
    // These are the entry points for building native windows and services.
    // IWindowPlugin, WindowPluginBase, IServicePlugin, ICorePlugin, ILibraryPlugin, ILogicPlugin

    // === Rendering ===
    // RenderContext — immediate-mode drawing (Rect, Line, Text, Path, etc.)
    // SceneBuilder — retained-mode fluent API that mirrors RenderContext
    // SceneNode, GroupNode, FlexGroupNode, CanvasNode — scene graph nodes
    // FrameState — per-frame state (mouse, size, tabs, bind info)

    // === Layout ===
    // Flex — static factory: Flex.Row(), Flex.Column(), Flex.Text(), Flex.Web()
    // FlexNode — flat data bag, laid out by Taffy FFI, rendered by FlexRenderer
    // ButtonRegistry — register hit regions during render, reverse-iterate for z-order hit test

    // === Toolkit ===
    // Theme — configurable color/size system, override at startup to retheme
    // TitleBar, BindTitleBar — window chrome
    // Controls — Spinner, Checkbox, Toggle, TabStrip, Chip, Dropdown
    // Layout — ChromePanel, Card, Strip, WarningPanel, StatCard, SettingRow
    // Buttons — immediate-mode button helpers
    // ContextMenu — self-contained right-click menu
    // Format — FormatAge, FormatVolume, FormatBytes, FormatPrice
    // DataTableFactory — scrollable data table builder
}
