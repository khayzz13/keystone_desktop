// D3DGpuContext — Per-window D3D12 GPU state implementing IWindowGpuContext.
// Each window gets its own IDXGISwapChain3 + GRContext.
// Equivalent of WindowGpuContext.cs (Metal) and VulkanGpuContext.cs (Vulkan).

using Vortice.Direct3D12;
using Vortice.DXGI;
using Keystone.Core.Rendering;
using SkiaSharp;

namespace Keystone.Core.Graphics.Skia.D3D;

public class D3DGpuContext : IWindowGpuContext
{
    const int BufferCount = 3; // triple-buffered
    const Format SwapChainFormat = Format.B8G8R8A8_UNorm;

    readonly D3DSkiaWindow _shared;
    readonly IDXGISwapChain3 _swapChain;
    readonly GRContext _grContext;

    // Per-frame resources
    SKSurface? _skiaSurface;
    GRBackendRenderTarget? _backendRT;
    bool _disposed;

    public GRContext GRContext => _grContext;

    D3DGpuContext(D3DSkiaWindow shared, IDXGISwapChain3 swapChain, GRContext grContext)
    {
        _shared = shared;
        _swapChain = swapChain;
        _grContext = grContext;
    }

    public static D3DGpuContext Create(D3DSkiaWindow shared, IntPtr hwnd)
    {
        var device = shared.Device;
        var commandQueue = shared.CommandQueue;
        var factory = shared.DxgiFactory;

        // Create swap chain for the HWND
        var swapChainDesc = new SwapChainDescription1
        {
            Width = 0,  // auto from HWND
            Height = 0,
            Format = SwapChainFormat,
            SampleDescription = new Vortice.DXGI.SampleDescription(1, 0),
            BufferUsage = Usage.RenderTargetOutput,
            BufferCount = BufferCount,
            SwapEffect = SwapEffect.FlipDiscard,
            Flags = SwapChainFlags.AllowTearing,
            AlphaMode = AlphaMode.Premultiplied,
        };

        var swapChain1 = factory.CreateSwapChainForHwnd(commandQueue, hwnd, swapChainDesc, null, null);

        // Disable Alt+Enter fullscreen toggle
        factory.MakeWindowAssociation(hwnd, WindowAssociationFlags.IgnoreAltEnter);

        var swapChain = swapChain1.QueryInterface<IDXGISwapChain3>();
        swapChain1.Dispose();

        // Create Skia GRContext for D3D12
        var backendContext = new GRD3DBackendContext
        {
            Adapter = shared.Adapter.NativePointer,
            Device = device.NativePointer,
            Queue = commandQueue.NativePointer,
        };

        var grContext = GRContext.CreateDirect3D(backendContext)
            ?? throw new InvalidOperationException("Failed to create D3D12 GRContext");
        grContext.SetResourceCacheLimit(64 * 1024 * 1024); // 64MB per window

        return new D3DGpuContext(shared, swapChain, grContext);
    }

    // === IWindowGpuContext ===

    public void WarmUpShaders()
    {
        using var surface = SKSurface.Create(_grContext, false, new SKImageInfo(64, 64));
        if (surface == null) return;
        var c = surface.Canvas;
        using var paint = new SKPaint { IsAntialias = true, Color = SKColors.White };

        c.DrawRect(0, 0, 32, 32, paint);
        c.DrawRoundRect(new SKRoundRect(new SKRect(0, 0, 16, 16), 4), paint);

        paint.Style = SKPaintStyle.Stroke;
        paint.StrokeWidth = 1;
        c.DrawLine(0, 0, 32, 32, paint);
        paint.Style = SKPaintStyle.Fill;

        using var bmp = new SKBitmap(4, 4, SKColorType.Rgba8888, SKAlphaType.Premul);
        using var img = SKImage.FromBitmap(bmp);
        c.DrawImage(img, new SKRect(0, 0, 4, 4));

        using var font = new SKFont(SKTypeface.Default, 14);
        c.DrawText("0", 0, 14, font, paint);

        c.Flush();
        _grContext.Flush();
    }

    public SKCanvas? BeginFrame(object gpuSurface, int width, int height)
    {
        if (_disposed) return null;

        DisposeFrameResources();

        // Get the current back buffer
        var bufferIndex = _swapChain.CurrentBackBufferIndex;
        using var backBuffer = _swapChain.GetBuffer<ID3D12Resource>(bufferIndex);

        // Wrap in Skia D3D12 texture info
        var textureInfo = new GRD3DTextureResourceInfo
        {
            Resource = backBuffer.NativePointer,
            ResourceState = (uint)ResourceStates.Present,
            Format = (uint)SwapChainFormat,
            SampleCount = 1,
            LevelCount = 1,
        };

        _backendRT = new GRBackendRenderTarget(width, height, textureInfo);
        _skiaSurface = SKSurface.Create(_grContext, _backendRT,
            GRSurfaceOrigin.TopLeft, SKColorType.Bgra8888);

        if (_skiaSurface == null)
        {
            DisposeFrameResources();
            return null;
        }

        return _skiaSurface.Canvas;
    }

    public void FinishAndPresent()
    {
        if (_skiaSurface == null) return;

        _skiaSurface.Canvas.Flush();
        _grContext.Flush();
        _grContext.Submit(synchronous: true);

        // Present with vsync (SyncInterval=1).
        _swapChain.Present(1, PresentFlags.None);

        DisposeFrameResources();
        _grContext.PurgeUnlockedResources(false);
    }

    public void SetDrawableSize(object gpuSurface, uint width, uint height)
    {
        if (_disposed || width == 0 || height == 0) return;

        // Flush and wait before resize
        _grContext.Flush();
        _grContext.Submit(synchronous: true);
        DisposeFrameResources();

        _swapChain.ResizeBuffers((uint)BufferCount, width, height,
            SwapChainFormat, SwapChainFlags.AllowTearing);
    }

    public void ForceFullPurge()
    {
        _grContext.Flush();
        _grContext.Submit(synchronous: true);
        _grContext.SetResourceCacheLimit(0);
        _grContext.PurgeUnlockedResources(true);
        _grContext.SetResourceCacheLimit(64 * 1024 * 1024);
    }

    public (int count, long bytes) GetCacheStats()
    {
        _grContext.GetResourceCacheUsage(out var count, out var bytes);
        return (count, bytes);
    }

    // === IGpuContext ===

    object IGpuContext.Device => _shared.Device;
    object IGpuContext.Queue => _shared.CommandQueue;
    object IGpuContext.GraphicsContext => _grContext;

    object? IGpuContext.ImportTexture(IntPtr textureHandle, int width, int height)
    {
        var textureInfo = new GRD3DTextureResourceInfo
        {
            Resource = textureHandle,
            ResourceState = (uint)ResourceStates.PixelShaderResource,
            Format = (uint)Format.R8G8B8A8_UNorm,
            SampleCount = 1,
            LevelCount = 1,
        };
        var backendTexture = new GRBackendTexture(width, height, textureInfo);
        return SKImage.FromTexture(_grContext, backendTexture,
            GRSurfaceOrigin.TopLeft, SKColorType.Rgba8888, SKAlphaType.Premul);
    }

    void DisposeFrameResources()
    {
        _skiaSurface?.Dispose();
        _backendRT?.Dispose();
        _skiaSurface = null;
        _backendRT = null;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        DisposeFrameResources();
        _grContext.Flush();
        _grContext.Submit(synchronous: true);
        _grContext.SetResourceCacheLimit(0);
        _grContext.PurgeUnlockedResources(false);
        _grContext.Dispose();
        _swapChain.Dispose();
    }
}
