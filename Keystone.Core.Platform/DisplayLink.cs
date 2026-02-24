// DisplayLink - CADisplayLink on a dedicated background thread's NSRunLoop
// Fires off the main thread (like CVDisplayLink did) so WaitForVsync() doesn't deadlock.
// Main thread uses WaitForVsync(). Render threads subscribe via Subscribe().

using CoreAnimation;
using Foundation;
using ObjCRuntime;

namespace Keystone.Core.Platform;

public class DisplayLink : IDisplayLink
{
    CADisplayLink? _displayLink;
    readonly DisplayLinkTarget _target;
    readonly SemaphoreSlim _vsyncSignal = new(0, 1);
    bool _disposed;

    // Dedicated thread + run loop for CADisplayLink (fires callback off main thread)
    Thread? _thread;
    NSRunLoop? _runLoop;
    readonly ManualResetEventSlim _runLoopReady = new(false);

    // Broadcast: per-window render thread subscribers
    readonly List<ManualResetEventSlim> _subscribers = new();
    readonly object _subLock = new();

    public DisplayLink()
    {
        _target = new DisplayLinkTarget(OnVsync);
        _displayLink = CADisplayLink.Create(_target, new Selector("onDisplayLink:"));
    }

    public void Start()
    {
        _thread = new Thread(() =>
        {
            _runLoop = NSRunLoop.Current;
            _displayLink?.AddToRunLoop(_runLoop, NSRunLoopMode.Common);
            _runLoopReady.Set();
            // Block this thread — keeps the run loop alive so CADisplayLink keeps firing
            while (!_disposed)
                _runLoop.RunUntil(NSDate.FromTimeIntervalSinceNow(1.0));
        }) { IsBackground = true, Name = "CADisplayLink" };
        _thread.Start();
        _runLoopReady.Wait();
    }

    public void Stop()
    {
        if (_runLoop != null)
            _displayLink?.RemoveFromRunLoop(_runLoop, NSRunLoopMode.Common);
    }

    /// <summary>Main thread: wait for next VSync.</summary>
    public bool WaitForVsync(int timeoutMs = 100) => _vsyncSignal.Wait(timeoutMs);

    /// <summary>Subscribe a render thread's signal. Returns the signal to wait on.</summary>
    public ManualResetEventSlim Subscribe()
    {
        var signal = new ManualResetEventSlim(false);
        lock (_subLock) _subscribers.Add(signal);
        return signal;
    }

    /// <summary>Unsubscribe — stop receiving VSync signals.</summary>
    public void Unsubscribe(ManualResetEventSlim signal)
    {
        lock (_subLock) _subscribers.Remove(signal);
    }

    /// <summary>Re-add a previously unsubscribed signal (no allocation).</summary>
    public void Resubscribe(ManualResetEventSlim signal)
    {
        lock (_subLock)
        {
            if (!_subscribers.Contains(signal))
                _subscribers.Add(signal);
        }
    }

    void OnVsync()
    {
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

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;                    // signals the run loop thread to exit
        _displayLink?.Invalidate();
        _displayLink = null;
        _thread?.Join(2000);                 // wait for run loop thread to finish
        lock (_subLock) _subscribers.Clear();
        _vsyncSignal.Dispose();
        _runLoopReady.Dispose();
    }
}

/// <summary>
/// Target object for CADisplayLink callback.
/// CADisplayLink.Create requires an NSObject target + selector.
/// </summary>
class DisplayLinkTarget : NSObject
{
    readonly Action _callback;

    public DisplayLinkTarget(Action callback) => _callback = callback;

    [Export("onDisplayLink:")]
    void OnDisplayLink(CADisplayLink link) => _callback();
}
