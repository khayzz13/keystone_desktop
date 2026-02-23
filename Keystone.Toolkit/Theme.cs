// Theme - Configurable color/size system for all toolkit components
// Apps override at startup to retheme everything.

namespace Keystone.Toolkit;

public static class Theme
{
    // ═══════════════════════════════════════════════════════════
    //  Surface hierarchy (darkest → lightest)
    // ═══════════════════════════════════════════════════════════

    public static uint BgBase       = 0x1a1a22ff;   // deepest background (recessed inputs)
    public static uint BgSurface    = 0x1e1e23ff;   // window background
    public static uint BgElevated   = 0x24242eff;   // panels, cards
    public static uint BgChrome     = 0x22222cff;   // chrome panels with stroke
    public static uint BgStrip      = 0x252530ff;   // toolbar strips
    public static uint BgHover      = 0x3a3a48ff;   // hover states
    public static uint BgPressed    = 0x32323eff;   // pressed states

    // ═══════════════════════════════════════════════════════════
    //  Chrome & controls
    // ═══════════════════════════════════════════════════════════

    public static uint BgButton     = 0x2a2a36ff;
    public static uint BgButtonHover = 0x3a3a48ff;
    public static uint BgButtonDark = 0x32323eff;
    public static uint BgMedium     = 0x2a2a32ff;   // title bar, tab bg
    public static uint BgLight      = 0x3a3a44ff;   // lighter interactive
    public static uint Stroke       = 0x33333eff;   // borders, dividers
    public static uint Divider      = 0x2e2e3aff;   // section dividers
    public static uint ButtonBorder = 0x3a3a46ff;

    // ═══════════════════════════════════════════════════════════
    //  Accent & semantic
    // ═══════════════════════════════════════════════════════════

    public static uint Accent       = 0x4a6fa5ff;   // primary accent (blue)
    public static uint AccentBright = 0x4a9effff;   // bright accent
    public static uint AccentHeader = 0x6a8abcff;   // section header text
    public static uint Success      = 0x26a69aff;   // green
    public static uint Warning      = 0xffca28ff;   // yellow
    public static uint Danger       = 0xef5350ff;   // red
    public static uint WarningBg    = 0x351818ff;   // warning panel bg
    public static uint WarningStroke = 0x553030ff;

    // ═══════════════════════════════════════════════════════════
    //  Text
    // ═══════════════════════════════════════════════════════════

    public static uint TextPrimary   = 0xccccccff;
    public static uint TextSecondary = 0x888888ff;
    public static uint TextMuted     = 0x667788ff;
    public static uint TextSubtle    = 0xffffffaa;

    // ═══════════════════════════════════════════════════════════
    //  Standard sizes
    // ═══════════════════════════════════════════════════════════

    public static float TitleBarHeight = 44f;
    public static float BindTitleBarHeight = 48f;
    public static float StripHeight  = 40f;
    public static float PadX         = 10f;
    public static float GapX         = 8f;
    public static float CornerRadius = 4f;
    public static float BtnSize      = 24f;

    // ═══════════════════════════════════════════════════════════
    //  Reset to defaults
    // ═══════════════════════════════════════════════════════════

    public static void Reset()
    {
        BgBase = 0x1a1a22ff; BgSurface = 0x1e1e23ff; BgElevated = 0x24242eff;
        BgChrome = 0x22222cff; BgStrip = 0x252530ff; BgHover = 0x3a3a48ff; BgPressed = 0x32323eff;
        BgButton = 0x2a2a36ff; BgButtonHover = 0x3a3a48ff; BgButtonDark = 0x32323eff;
        BgMedium = 0x2a2a32ff; BgLight = 0x3a3a44ff; Stroke = 0x33333eff; Divider = 0x2e2e3aff;
        ButtonBorder = 0x3a3a46ff;
        Accent = 0x4a6fa5ff; AccentBright = 0x4a9effff; AccentHeader = 0x6a8abcff;
        Success = 0x26a69aff; Warning = 0xffca28ff; Danger = 0xef5350ff;
        WarningBg = 0x351818ff; WarningStroke = 0x553030ff;
        TextPrimary = 0xccccccff; TextSecondary = 0x888888ff; TextMuted = 0x667788ff; TextSubtle = 0xffffffaa;
        TitleBarHeight = 44f; BindTitleBarHeight = 48f; StripHeight = 40f;
        PadX = 10f; GapX = 8f; CornerRadius = 4f; BtnSize = 24f;
    }
}
