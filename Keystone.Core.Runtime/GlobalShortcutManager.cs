// GlobalShortcutManager — process-wide keyboard shortcut registration.
// On hotkey fire, pushes to channel "globalShortcut:{accelerator}" via BunManager
// so any subscribed window receives it via subscribe() in the browser SDK.
//
// Accelerator format mirrors Electron: modifier+key, e.g. "CommandOrControl+Shift+P"
// Modifiers: Control/Ctrl, Shift, Alt, Meta/Command/Cmd, CommandOrControl
// Keys: A-Z, 0-9, F1-F12, and common named keys (Enter, Escape, Space, etc.)

using Keystone.Core.Management.Bun;
using Keystone.Core.Platform.Abstractions;

namespace Keystone.Core.Runtime;

/// <summary>
/// Singleton manager for global keyboard shortcuts.
/// Wire up at startup: GlobalShortcutManager.Initialize(backend).
/// </summary>
public static class GlobalShortcutManager
{
    private static IGlobalShortcutBackend? _backend;
    private static readonly Dictionary<string, string> _registered = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Bind the platform backend. Must be called once before any Register/Unregister calls.
    /// </summary>
    public static void Initialize(IGlobalShortcutBackend backend)
    {
        _backend = backend;
    }

    /// <summary>
    /// Register a global shortcut. Returns false if taken or unsupported on this platform.
    /// ownerWindowId is stored for future per-window scoping.
    /// </summary>
    public static bool Register(string accelerator, string ownerWindowId)
    {
        if (_backend == null) return false;
        if (_registered.ContainsKey(accelerator)) return true; // already registered by us

        var ok = _backend.Register(accelerator, () => OnFired(accelerator));
        if (ok) _registered[accelerator] = ownerWindowId;
        return ok;
    }

    public static void Unregister(string accelerator)
    {
        if (_backend == null || !_registered.ContainsKey(accelerator)) return;
        _backend.Unregister(accelerator);
        _registered.Remove(accelerator);
    }

    public static bool IsRegistered(string accelerator)
        => _registered.ContainsKey(accelerator);

    public static void UnregisterAll()
    {
        foreach (var acc in _registered.Keys.ToList())
            _backend?.Unregister(acc);
        _registered.Clear();
    }

    public static void Shutdown()
    {
        UnregisterAll();
        _backend?.Dispose();
        _backend = null;
    }

    private static void OnFired(string accelerator)
    {
        BunManager.Instance.Push($"globalShortcut:{accelerator}", new { accelerator });
        Console.WriteLine($"[GlobalShortcut] Fired: {accelerator}");
    }
}
