// D3DSkiaWindow — Shared D3D12 device holder + D3DGpuContext factory.
// Equivalent of SkiaWindow.cs (Metal) and VulkanSkiaWindow.cs (Vulkan).
// The ID3D12Device is thread-safe (shared). Each window has its own D3DGpuContext.

using Vortice.Direct3D;
using Vortice.Direct3D12;
using Vortice.DXGI;
using SkiaSharp;

namespace Keystone.Core.Graphics.Skia.D3D;

public class D3DSkiaWindow : IDisposable
{
    static D3DSkiaWindow? _shared;
    public static D3DSkiaWindow Shared =>
        _shared ?? throw new InvalidOperationException("D3DSkiaWindow not initialized");

    readonly ID3D12Device _device;
    readonly ID3D12CommandQueue _commandQueue;
    readonly IDXGIFactory4 _dxgiFactory;
    readonly IDXGIAdapter1 _adapter;
    bool _disposed;

    public ID3D12Device Device => _device;
    public ID3D12CommandQueue CommandQueue => _commandQueue;
    public IDXGIFactory4 DxgiFactory => _dxgiFactory;
    public IDXGIAdapter1 Adapter => _adapter;

    D3DSkiaWindow(ID3D12Device device, ID3D12CommandQueue commandQueue, IDXGIFactory4 dxgiFactory, IDXGIAdapter1 adapter)
    {
        _device = device;
        _commandQueue = commandQueue;
        _dxgiFactory = dxgiFactory;
        _adapter = adapter;
    }

    public static void Initialize()
    {
        if (_shared != null) return;

#if DEBUG
        // Enable D3D12 debug layer in debug builds
        if (D3D12.D3D12GetDebugInterface(out ID3D12Debug? debug).Success)
        {
            debug!.EnableDebugLayer();
            debug.Dispose();
        }
#endif

        // Create DXGI factory
        DXGI.CreateDXGIFactory2(false, out IDXGIFactory4? factory).CheckError();

        // Enumerate adapters to find a discrete GPU; skip WARP software adapter
        ID3D12Device? device = null;
        IDXGIAdapter1? chosenAdapter = null;

        for (uint i = 0; factory!.EnumAdapters1(i, out IDXGIAdapter1 adapter).Success; i++)
        {
            var desc = adapter.Description1;
            if ((desc.Flags & AdapterFlags.Software) != 0)
            {
                adapter.Dispose();
                continue;
            }

            if (D3D12.D3D12CreateDevice(adapter, FeatureLevel.Level_11_0, out device).Success)
            {
                chosenAdapter = adapter;
                break;
            }
            adapter.Dispose();
        }

        // Fallback: let D3D12 pick (uses default adapter)
        if (device == null)
        {
            D3D12.D3D12CreateDevice(null, FeatureLevel.Level_11_0, out device).CheckError();
            // Get the adapter D3D12 chose
            factory.EnumAdapters1(0, out chosenAdapter).CheckError();
        }

        // Create direct command queue
        var queueDesc = new CommandQueueDescription(CommandListType.Direct);
        var commandQueue = device!.CreateCommandQueue<ID3D12CommandQueue>(queueDesc);

        _shared = new D3DSkiaWindow(device, commandQueue, factory, chosenAdapter!);
        Console.WriteLine("[D3DSkiaWindow] Initialized — shared D3D12 device");
    }

    /// <summary>Create a per-window GPU context with its own swap chain and GRContext.</summary>
    public static D3DGpuContext CreateWindowContext(IntPtr hwnd) =>
        D3DGpuContext.Create(Shared, hwnd);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _commandQueue.Dispose();
        _adapter.Dispose();
        _device.Dispose();
        _dxgiFactory.Dispose();
    }
}
