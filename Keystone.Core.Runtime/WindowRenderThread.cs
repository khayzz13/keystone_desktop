// WindowRenderThread - Per-window render thread with demand-driven idle
// When active: wakes on VSync, renders if needed.
// When idle: unsubscribes from VSync, sleeps until RequestRedraw sets the signal.
// Same signal for both paths — no secondary wake mechanism.

#if MACOS
using Foundation;
#endif
using Keystone.Core.Rendering;

namespace Keystone.Core.Runtime;

public class WindowRenderThread : IDisposable
{
    Thread? _thread;
    readonly IWindowGpuContext _gpu;
    readonly ManualResetEventSlim _vsyncSignal;
    readonly ManagedWindow _window;
    volatile bool _running;
    bool _disposed;

    public IWindowGpuContext Gpu => _gpu;

    public WindowRenderThread(ManagedWindow window, IWindowGpuContext gpu, ManualResetEventSlim vsyncSignal)
    {
        _window = window;
        _gpu = gpu;
        _vsyncSignal = vsyncSignal;
    }

    public void Start()
    {
        _running = true;
        _thread = new Thread(ThreadLoop) { Name = $"Render-{_window.Id}", IsBackground = true };
        _thread.Start();
    }

    void ThreadLoop()
    {
        _gpu.WarmUpShaders();

        while (_running)
        {
            // When VSync-subscribed: wakes at display refresh rate.
            // When suspended: blocks until RequestRedraw() sets the signal directly.
            _vsyncSignal.Wait();
            _vsyncSignal.Reset();
            if (!_running) break;

#if MACOS
            // Per-frame autorelease pool — NextDrawable() and other Metal/AppKit calls
            // return autoreleased ObjC objects. Without this, IOSurfaces from old drawables
            // accumulate on the thread's default pool and never drain.
            using var pool = new NSAutoreleasePool();
#endif
            try
            {
                if (_window.ShouldRender())
                {
                    _window.ResumeVSync();
                    _window.RenderOnThread(_gpu);
                }
                else
                {
                    _window.TrySuspendVSync();
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[RenderThread] {_window.Id}: {ex.Message}");
            }
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _running = false;
        _vsyncSignal.Set();
        _thread?.Join(2000);
        _gpu.Dispose();
        // Don't dispose _vsyncSignal — owned by ManagedWindow
    }
}
