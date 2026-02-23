// SkiaWindow - Shared Metal device holder + WindowGpuContext factory
// The device is thread-safe (shared). Each window creates its own GpuContext.

using Metal;

namespace Keystone.Core.Graphics.Skia;

public class SkiaWindow : IDisposable
{
    static SkiaWindow? _shared;
    public static SkiaWindow Shared => _shared ?? throw new InvalidOperationException("SkiaWindow not initialized");

    readonly IMTLDevice _device;
    bool _disposed;

    public IMTLDevice Device => _device;
    public IntPtr DeviceHandle => _device.Handle;

    SkiaWindow(IMTLDevice device) => _device = device;

    public static void Initialize()
    {
        if (_shared != null) return;
        var device = MTLDevice.SystemDefault
            ?? throw new InvalidOperationException("No Metal device available");
        _shared = new SkiaWindow(device);
        Console.WriteLine("[SkiaWindow] Initialized — shared Metal device");
    }

    /// <summary>
    /// Create a per-window GPU context (own GRContext + command queue).
    /// </summary>
    public static WindowGpuContext CreateWindowContext() =>
        WindowGpuContext.Create(Shared._device);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        (_device as IDisposable)?.Dispose();
    }
}
