// LinuxGlobalShortcut — stub implementation.
// X11 XGrabKey works on X11 sessions but is not viable under Wayland (the dominant
// session type on modern Linux). A proper Wayland implementation requires the
// GlobalShortcuts portal (xdg-desktop-portal 1.7+) via DBus — deferred.

using Keystone.Core.Platform.Abstractions;

namespace Keystone.Core.Platform.Linux;

public sealed class LinuxGlobalShortcut : IGlobalShortcutBackend
{
    public LinuxGlobalShortcut()
    {
        Console.WriteLine("[LinuxGlobalShortcut] Global shortcuts are not yet supported on Linux " +
            "(requires Wayland GlobalShortcuts portal — deferred). Register() will return false.");
    }

    public bool Register(string accelerator, Action onFired)
    {
        Console.WriteLine($"[LinuxGlobalShortcut] Register({accelerator}): not supported — returning false");
        return false;
    }

    public void Unregister(string accelerator) { }

    public void Dispose() { }
}
