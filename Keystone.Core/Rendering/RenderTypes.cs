// Core rendering types - enums, IGpuContext, and FrameState

using System;

namespace Keystone.Core.Rendering;

public enum TextAlign : byte { Left = 0, Center = 1, Right = 2 }
public enum FontId : byte { Regular = 0, Bold = 1, Cocogoose = 2, CocogooseBold = 3, Symbols = 4 }
public enum CursorType : byte { Default = 0, Pointer = 1, Text = 2, Crosshair = 3, Move = 4, ResizeNS = 5, ResizeEW = 6, ResizeNESW = 7, ResizeNWSE = 8, NotAllowed = 9, Grab = 10, Grabbing = 11 }
public enum BlendMode : byte { Normal = 0, Multiply = 1, Screen = 2, Overlay = 3, Darken = 4, Lighten = 5, ColorDodge = 6, ColorBurn = 7, HardLight = 8, SoftLight = 9, Difference = 10, Exclusion = 11 }
public enum FlexIcon : byte { None = 0, Close = 1, Minimize = 2 }
public enum LineCap : byte { Butt = 0, Round = 1, Square = 2 }
public enum LineJoin : byte { Miter = 0, Round = 1, Bevel = 2 }

/// <summary>
/// GPU context abstraction for logic plugins and compute shaders.
/// Implemented by WindowGpuContext (per-window Metal device + command queue + GRContext).
/// Object-typed accessors avoid cross-assembly dependency on Keystone.Core.Graphics.Skia —
/// GPU-aware plugins cast to concrete Metal/Skia types as needed.
/// </summary>
public interface IGpuContext
{
    /// <summary>Shared GPU device (IMTLDevice / VkDevice). Thread-safe.</summary>
    object Device { get; }
    /// <summary>Per-window command queue (IMTLCommandQueue / VkQueue).</summary>
    object Queue { get; }
    /// <summary>Per-window graphics context (GRContext) for Skia GPU operations. NOT thread-safe.</summary>
    object GraphicsContext { get; }
    /// <summary>Import a native texture into Skia as an image. Returns SKImage? or null.</summary>
    object? ImportTexture(IntPtr textureHandle, int width, int height);
}

/// <summary>
/// Per-window GPU rendering context used by the render thread.
/// Implemented by WindowGpuContext (Metal) and VulkanGpuContext (Vulkan).
/// Abstracts the BeginFrame → draw on SKCanvas → FinishAndPresent cycle.
/// </summary>
public interface IWindowGpuContext : IGpuContext, IDisposable
{
    /// <summary>Pre-compile GPU shaders on a tiny offscreen surface. Call once before first frame.</summary>
    void WarmUpShaders();

    /// <summary>Acquire a drawable, create an SKSurface, return the canvas. Null if acquisition fails.</summary>
    SkiaSharp.SKCanvas? BeginFrame(object gpuSurface, int width, int height);

    /// <summary>Flush Skia, present the drawable, dispose frame resources.</summary>
    void FinishAndPresent();

    /// <summary>Set the drawable surface size (called when window resizes).</summary>
    void SetDrawableSize(object gpuSurface, uint width, uint height);

    /// <summary>Aggressively purge all unlocked GPU resources. Call from render thread only.</summary>
    void ForceFullPurge();

    /// <summary>Get resource cache usage for diagnostics.</summary>
    (int count, long bytes) GetCacheStats();
}

/// <summary>
/// Frame state - mutable for plugin rendering
/// </summary>
public class FrameState
{
    public ulong Sequence { get; set; }
    public float Width { get; set; }
    public float Height { get; set; }
    public float ScaleFactor { get; set; }
    public float MouseX { get; set; }
    public float MouseY { get; set; }
    public bool MouseDown { get; set; }
    public bool MouseClicked { get; set; }
    public bool RightClick { get; set; }
    public float MouseScroll { get; set; }
    public string WindowType { get; set; } = "";
    public uint WindowId { get; set; }
    public ulong TimeMs { get; set; }
    public uint DeltaMs { get; set; }
    public uint FrameCount { get; set; }

    // Global app state
    public bool BindModeActive { get; set; }
    public bool IsSelectedForBind { get; set; }
    public bool AlwaysOnTop { get; set; } = false;

    // Bind container state (when this plugin is inside a bind)
    public bool IsInBind { get; set; }
    public string? BindContainerId { get; set; }
    public int BindSlotIndex { get; set; }

    // Tab group state
    public bool IsInTabGroup { get; set; }
    public string? TabGroupId { get; set; }
    public string[]? TabIds { get; set; }
    public string[]? TabTitles { get; set; }
    public string? ActiveTabId { get; set; }

    // Overlay anchor (set by plugin to position overlay below a button)
    public float OverlayAnchorX { get; set; }

    // Window title (set by WindowManager from plugin.WindowTitle)
    public string WindowTitle { get; set; } = "";

    // Per-window GPU context (set by render thread, used by compute shaders)
    public IGpuContext? GpuContext { get; set; }

    // Set by BuildScene plugins to request another frame (animations, toolbar slide)
    public bool NeedsRedraw { get; set; }

    // WebView requests from FlexRenderer (render thread writes, ManagedWindow reads after frame)
    // isSlot=true: Bun component → shared host WebView with CSS-positioned slots
    // isSlot=false: external URL → dedicated WKWebView
    public List<(string key, string url, float x, float y, float w, float h, bool isSlot)>? WebViewRequests;
}
