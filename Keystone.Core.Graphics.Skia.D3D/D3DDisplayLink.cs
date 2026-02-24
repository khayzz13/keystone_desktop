// D3DDisplayLink — VSync timing for Windows via IDXGIOutput::WaitForVBlank().
// Same broadcast pattern as macOS DisplayLink.cs and TimerDisplayLink.cs.
// Replaces TimerDisplayLink on Windows with true hardware VSync.

using Vortice.DXGI;
using Keystone.Core.Platform;

namespace Keystone.Core.Graphics.Skia.D3D;

public class D3DDisplayLink : IDisplayLink
{
    readonly SemaphoreSlim _vsyncSignal = new(0, 1);
    readonly List<ManualResetEventSlim> _subscribers = new();
    readonly object _subLock = new();
    Thread? _thread;
    volatile bool _disposed;
    IDXGIOutput? _output;

    public D3DDisplayLink()
    {
        // Get the primary output from the D3D shared device
        try
        {
            var factory = D3DSkiaWindow.Shared.DxgiFactory;
            if (factory.EnumAdapters1(0, out IDXGIAdapter1 adapter).Success)
            {
                using (adapter)
                {
                    adapter.EnumOutputs(0, out IDXGIOutput output).CheckError();
                    _output = output;
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[D3DDisplayLink] Failed to get DXGI output, falling back to timer: {ex.Message}");
            _output = null;
        }
    }

    public void Start()
    {
        _thread = new Thread(() =>
        {
            while (!_disposed)
            {
                if (_output != null)
                {
                    // True hardware VSync — blocks until next vblank
                    _output.WaitForVBlank();
                }
                else
                {
                    // Fallback: ~60Hz timer
                    Thread.Sleep(16);
                }

                // Signal main thread WaitForVsync callers
                if (_vsyncSignal.CurrentCount == 0)
                    _vsyncSignal.Release();

                // Broadcast to all render thread subscribers
                lock (_subLock)
                {
                    for (int i = 0; i < _subscribers.Count; i++)
                        try { _subscribers[i].Set(); }
                        catch (ObjectDisposedException) { }
                }
            }
        }) { IsBackground = true, Name = "D3DDisplayLink" };
        _thread.Start();
    }

    public void Stop() => _disposed = true;

    public bool WaitForVsync(int timeoutMs = 100) => _vsyncSignal.Wait(timeoutMs);

    public ManualResetEventSlim Subscribe()
    {
        var signal = new ManualResetEventSlim(false);
        lock (_subLock) _subscribers.Add(signal);
        return signal;
    }

    public void Unsubscribe(ManualResetEventSlim signal)
    {
        lock (_subLock) _subscribers.Remove(signal);
    }

    public void Resubscribe(ManualResetEventSlim signal)
    {
        lock (_subLock)
        {
            if (!_subscribers.Contains(signal))
                _subscribers.Add(signal);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _thread?.Join(2000);
        _output?.Dispose();
        _output = null;
        lock (_subLock) _subscribers.Clear();
        _vsyncSignal.Dispose();
    }
}
