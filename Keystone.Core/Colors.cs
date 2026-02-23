// Colors - Theme color definitions (Core Plugin)
// Foundational plugin loaded before all others
// Can be extended by adding more color definitions
// Note: Pure static library, no interface implementation needed (avoids circular dependency)

namespace Keystone.Core;

public static class Colors
{
    // Color utility functions
    public static uint Rgba(byte r, byte g, byte b, byte a = 255) => ((uint)r << 24) | ((uint)g << 16) | ((uint)b << 8) | a;
    public static uint Rgb(byte r, byte g, byte b) => Rgba(r, g, b, 255);
    public static uint Hex(uint hex) => hex; // Already in RGBA format

    // Common colors
    public static uint Black => 0x000000ff;
    public static uint White => 0xffffffff;
    public static uint Transparent => 0x00000000;

    // UI colors (dark theme)
    public static uint BgDark => 0x1e1e23ff;
    public static uint BgMedium => 0x2a2a32ff;
    public static uint BgLight => 0x3a3a44ff;
    public static uint TextPrimary => 0xccccccff;
    public static uint TextSecondary => 0x888888ff;
    public static uint Accent => 0x4a9eff;
    public static uint Green => 0x26a69aff;
    public static uint Yellow => 0xffca28ff;
    public static uint Red => 0xef5350ff;

}
