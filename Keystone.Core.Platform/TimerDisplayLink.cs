// TimerDisplayLink - Timer-based VSync approximation for non-macOS platforms.
// Uses a dedicated thread sleeping at ~60Hz. Same broadcast pattern as macOS DisplayLink.

namespace Keystone.Core.Platform;

public class TimerDisplayLink : IDisplayLink
{
    readonly SemaphoreSlim _vsyncSignal = new(0, 1);
    readonly List<ManualResetEventSlim> _subscribers = new();
    readonly object _subLock = new();
    Thread? _thread;
    volatile bool _disposed;

    public void Start()
    {
        _thread = new Thread(() =>
        {
            while (!_disposed)
            {
                Thread.Sleep(16); // ~60Hz

                // Signal main thread
                if (_vsyncSignal.CurrentCount == 0)
                    _vsyncSignal.Release();

                // Broadcast to all render threads
                lock (_subLock)
                {
                    for (int i = 0; i < _subscribers.Count; i++)
                        try { _subscribers[i].Set(); }
                        catch (ObjectDisposedException) { }
                }
            }
        }) { IsBackground = true, Name = "TimerDisplayLink" };
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
        lock (_subLock) _subscribers.Clear();
        _vsyncSignal.Dispose();
    }
}
