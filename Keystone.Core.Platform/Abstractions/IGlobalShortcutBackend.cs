namespace Keystone.Core.Platform.Abstractions;

/// <summary>
/// Platform-specific backend for registering/unregistering OS-level global hotkeys.
/// </summary>
public interface IGlobalShortcutBackend
{
    /// <summary>
    /// Register a global hotkey. Returns false if the accelerator is already taken
    /// by another process or cannot be parsed.
    /// </summary>
    bool Register(string accelerator, Action onFired);

    /// <summary>Unregister a previously registered accelerator.</summary>
    void Unregister(string accelerator);

    /// <summary>Tear down the backend (called on shutdown).</summary>
    void Dispose();
}
