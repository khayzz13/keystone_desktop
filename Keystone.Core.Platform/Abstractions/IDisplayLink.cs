namespace Keystone.Core.Platform;

/// <summary>
/// VSync timing abstraction. CADisplayLink on macOS, timer-based on Linux.
/// Main thread uses WaitForVsync(). Render threads subscribe via Subscribe().
/// </summary>
public interface IDisplayLink : IDisposable
{
    void Start();
    void Stop();
    bool WaitForVsync(int timeoutMs = 100);
    ManualResetEventSlim Subscribe();
    void Unsubscribe(ManualResetEventSlim signal);
    void Resubscribe(ManualResetEventSlim signal);
}
