/*---------------------------------------------------------------------------------------------
 *  Copyright (c) 2026 Kaedyn Limon. All rights reserved.
 *  Licensed under the MIT License. See LICENSE in the project root for license information.
 *--------------------------------------------------------------------------------------------*/

#if MACOS
using Keystone.Core.Graphics.Skia;
using Keystone.Core.Platform.MacOS;
#else
using Keystone.Core.Graphics.Skia.Vulkan;
#endif
using Keystone.Core;
using Keystone.Core.Platform;
using Keystone.Core.Rendering;

namespace Keystone.Core.Runtime;

/// <summary>
/// Owns GPU context lifecycle, VSync management, paint cache, and render thread.
/// The render loop body (RenderOnThread) stays in ManagedWindow as coordinator logic.
/// </summary>
internal sealed class WindowGpuHost : IDisposable
{
    readonly IDisplayLink _displayLink;

    WindowRenderThread? _renderThread;
    ManualResetEventSlim? _vsyncSignal;
    SkiaPaintCache? _paintCache;
    volatile bool _vsyncActive = true;

    public ManualResetEventSlim? VsyncSignal => _vsyncSignal;
    public WindowRenderThread? RenderThread => _renderThread;
    public IWindowGpuContext? GpuContext => _renderThread?.Gpu;
    public SkiaPaintCache? PaintCache => _paintCache;

    public WindowGpuHost(IDisplayLink displayLink) => _displayLink = displayLink;

    /// <summary>Initialize GPU context from native surface. Returns true if context was created.</summary>
    public bool Initialize(object gpuSurface, float scale, ManagedWindow window)
    {
        var gpu = CreateGpuContext(gpuSurface, scale);
        if (gpu == null) return false;

        _paintCache = new SkiaPaintCache();
        _vsyncSignal = _displayLink.Subscribe();
        _renderThread = new WindowRenderThread(window, gpu, _vsyncSignal);
        return true;
    }

    public void Start() => _renderThread?.Start();

    public void WakeRenderThread() => _vsyncSignal?.Set();

    /// <summary>Called by render thread: resubscribe to VSync if suspended.</summary>
    public void ResumeVSync()
    {
        if (_vsyncActive) return;
        _displayLink.Resubscribe(_vsyncSignal!);
        _vsyncActive = true;
    }

    /// <summary>Called by render thread: unsubscribe from VSync when idle.</summary>
    public void TrySuspendVSync()
    {
        if (!_vsyncActive) return;
        _displayLink.Unsubscribe(_vsyncSignal!);
        _vsyncActive = false;
    }

    static IWindowGpuContext? CreateGpuContext(object gpuSurface, double scale)
    {
#if MACOS
        if (gpuSurface is CoreAnimation.CAMetalLayer metalLayer)
        {
            MacOSPlatform.ConfigureMetalLayer(metalLayer, SkiaWindow.Shared.Device, scale);
            return SkiaWindow.CreateWindowContext();
        }
#else
        if (gpuSurface is IntPtr gdkSurface && gdkSurface != IntPtr.Zero)
            return VulkanSkiaWindow.CreateWindowContext(gdkSurface);
#endif
        return null;
    }

    public void Dispose()
    {
        _renderThread?.Dispose();
        if (_vsyncSignal != null)
        {
            _displayLink.Unsubscribe(_vsyncSignal);
            _vsyncSignal.Dispose();
        }
        _paintCache?.Dispose();
    }
}
