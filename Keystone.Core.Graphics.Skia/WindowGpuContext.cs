// WindowGpuContext - Per-window GPU state (GRContext + command queue + present)
// Each window gets its own Metal command queue and SkiaSharp GRContext.
// GRContext is NOT thread-safe — must stay on its owning render thread.
// IMTLDevice is shared (thread-safe per Apple docs).

using CoreAnimation;
using Metal;
using Keystone.Core.Rendering;
using SkiaSharp;

namespace Keystone.Core.Graphics.Skia;

public class WindowGpuContext : IWindowGpuContext
{
    readonly IMTLDevice _device;
    readonly IMTLCommandQueue _queue;
    readonly GRContext _grContext;
    SKSurface? _surface;
    GRBackendRenderTarget? _backendRT;
    ICAMetalDrawable? _drawable;
    IMTLTexture? _drawableTexture; // must dispose explicitly — retains IOSurface
    bool _disposed;

    public IMTLDevice Device => _device;
    public IMTLCommandQueue Queue => _queue;
    public GRContext GRContext => _grContext;

    WindowGpuContext(IMTLDevice device, IMTLCommandQueue queue, GRContext grContext)
    {
        _device = device;
        _queue = queue;
        _grContext = grContext;
    }

    public static WindowGpuContext Create(IMTLDevice device)
    {
        var queue = device.CreateCommandQueue()
            ?? throw new InvalidOperationException("Failed to create per-window command queue");
        var backendContext = new GRMtlBackendContext { Device = device, Queue = queue };
        var grContext = GRContext.CreateMetal(backendContext)
            ?? throw new InvalidOperationException("Failed to create per-window GRContext");
        grContext.SetResourceCacheLimit(64 * 1024 * 1024); // 64MB cap per window
        return new WindowGpuContext(device, queue, grContext);
    }

    /// <summary>
    /// Pre-compile Metal shaders by exercising all common draw paths on a tiny offscreen surface.
    /// Call from render thread before first visible frame.
    /// </summary>
    public void WarmUpShaders()
    {
        using var surface = SKSurface.Create(_grContext, false, new SKImageInfo(64, 64));
        if (surface == null) return;
        var c = surface.Canvas;
        using var paint = new SKPaint { IsAntialias = true, Color = SKColors.White };

        // Fill + rounded rect pipelines
        c.DrawRect(0, 0, 32, 32, paint);
        c.DrawRoundRect(new SKRoundRect(new SKRect(0, 0, 16, 16), 4), paint);

        // Stroke pipeline
        paint.Style = SKPaintStyle.Stroke;
        paint.StrokeWidth = 1;
        c.DrawLine(0, 0, 32, 32, paint);
        paint.Style = SKPaintStyle.Fill;

        // Texture/image pipeline
        using var bmp = new SKBitmap(4, 4, SKColorType.Rgba8888, SKAlphaType.Premul);
        using var img = SKImage.FromBitmap(bmp);
        c.DrawImage(img, new SKRect(0, 0, 4, 4));

        // Color-filtered texture pipeline (atlas glyph tinting via Modulate blend)
        using var colorFilter = SKColorFilter.CreateBlendMode(SKColors.Red, SKBlendMode.Modulate);
        paint.ColorFilter = colorFilter;
        c.DrawImage(img, new SKRect(0, 0, 4, 4), paint);
        paint.ColorFilter = null;

        // Text pipeline
        using var font = new SKFont(SKTypeface.Default, 14);
        c.DrawText("0", 0, 14, font, paint);

        c.Flush();
        _grContext.Flush();
    }

    /// <summary>
    /// Begin frame: acquire drawable, create surface, return canvas.
    /// </summary>
    public SKCanvas? BeginFrame(CAMetalLayer layer, int width, int height)
    {
        if (_disposed) return null;
        var drawable = layer.NextDrawable();
        if (drawable == null) return null;

        var texture = drawable.Texture;
        var textureInfo = new GRMtlTextureInfo(texture);
        var backendRT = new GRBackendRenderTarget(width, height, textureInfo);
        var surface = SKSurface.Create(_grContext, backendRT, GRSurfaceOrigin.TopLeft, SKColorType.Bgra8888);
        if (surface == null)
        {
            backendRT.Dispose();
            (texture as IDisposable)?.Dispose();
            (drawable as IDisposable)?.Dispose();
            return null;
        }

        // Dispose previous frame resources if not yet presented
        DisposeFrameResources();

        _surface = surface;
        _backendRT = backendRT;
        _drawable = drawable;
        _drawableTexture = texture;
        return surface.Canvas;
    }

    /// <summary>
    /// Flush GPU + present drawable. Called from render thread after encoding.
    /// </summary>
    public void FinishAndPresent()
    {
        if (_surface == null || _drawable == null) return;

        _surface.Canvas.Flush();
        _grContext.Flush();
        _grContext.Submit(synchronous: true);

        using var cmdBuffer = _queue.CommandBuffer();
        if (cmdBuffer != null)
        {
            cmdBuffer.PresentDrawable(_drawable);
            cmdBuffer.Commit();
            cmdBuffer.WaitUntilCompleted();
        }

        DisposeFrameResources();
        _grContext.PurgeUnlockedResources(false);
    }

    /// <summary>
    /// Import Metal texture into SkiaSharp (for compute shader output).
    /// </summary>
    public SKImage? CreateImageFromTexture(IntPtr mtlTextureHandle, int width, int height)
    {
        var texture = ObjCRuntime.Runtime.GetINativeObject<IMTLTexture>(mtlTextureHandle, false);
        if (texture == null) return null;
        var textureInfo = new GRMtlTextureInfo(texture);
        var backendTexture = new GRBackendTexture(width, height, false, textureInfo);
        return SKImage.FromTexture(_grContext, backendTexture, GRSurfaceOrigin.TopLeft,
            SKColorType.Rgba8888, SKAlphaType.Premul);
    }

    /// <summary>Get GRContext resource cache usage for diagnostics.</summary>
    public (int count, long bytes) GetCacheStats()
    {
        _grContext.GetResourceCacheUsage(out var count, out var bytes);
        return (count, bytes);
    }

    /// <summary>Aggressively purge ALL unlocked GPU resources (IOSurface-backed textures, etc).
    /// Call from render thread only.</summary>
    public void ForceFullPurge()
    {
        _grContext.Flush();
        _grContext.Submit(synchronous: true);
        _grContext.SetResourceCacheLimit(0);
        _grContext.PurgeUnlockedResources(true);
        _grContext.SetResourceCacheLimit(64 * 1024 * 1024);
    }

    // === IWindowGpuContext (object-typed BeginFrame for platform-agnostic render thread) ===

    SKCanvas? IWindowGpuContext.BeginFrame(object gpuSurface, int width, int height)
        => BeginFrame((CAMetalLayer)gpuSurface, width, height);

    void IWindowGpuContext.SetDrawableSize(object gpuSurface, uint width, uint height)
        => ((CAMetalLayer)gpuSurface).DrawableSize = new CoreGraphics.CGSize(width, height);

    // === IGpuContext (object-typed for cross-assembly access) ===
    object IGpuContext.Device => _device;
    object IGpuContext.Queue => _queue;
    object IGpuContext.GraphicsContext => _grContext;
    object? IGpuContext.ImportTexture(IntPtr textureHandle, int width, int height)
        => CreateImageFromTexture(textureHandle, width, height);

    void DisposeFrameResources()
    {
        _surface?.Dispose();
        _backendRT?.Dispose();
        (_drawableTexture as IDisposable)?.Dispose();
        (_drawable as IDisposable)?.Dispose();
        _surface = null;
        _backendRT = null;
        _drawableTexture = null;
        _drawable = null;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        DisposeFrameResources();
        // Flush pending GPU work, then evict all cached textures/IOSurfaces before teardown
        _grContext.Flush();
        _grContext.Submit(synchronous: true);
        _grContext.SetResourceCacheLimit(0);
        _grContext.PurgeUnlockedResources(false);
        _grContext.Dispose();
        (_queue as IDisposable)?.Dispose();
    }
}
