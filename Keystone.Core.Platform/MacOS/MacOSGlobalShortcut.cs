// MacOSGlobalShortcut — global hotkey registration via NSEvent.addGlobalMonitorForEvents.
// No entitlements required (unlike CGEventTap which needs Accessibility).
// Uses addLocalMonitorForEvents for when the app is focused, addGlobalMonitorForEvents for background.

using System.Runtime.InteropServices;
using Keystone.Core.Platform.Abstractions;
using ObjCRuntime;
using AppKit;
using Foundation;

namespace Keystone.Core.Platform.MacOS;

public sealed class MacOSGlobalShortcut : IGlobalShortcutBackend
{
    private readonly Dictionary<string, (NSEventModifierMask mods, ushort keyCode, Action callback)> _registered = new();
    private NSObject? _globalMonitor;
    private NSObject? _localMonitor;

    public MacOSGlobalShortcut()
    {
        InstallMonitors();
    }

    public bool Register(string accelerator, Action onFired)
    {
        if (!TryParseAccelerator(accelerator, out var keyCode, out var modifiers))
        {
            Console.WriteLine($"[MacOSGlobalShortcut] Cannot parse accelerator: {accelerator}");
            return false;
        }
        _registered[accelerator] = (modifiers, keyCode, onFired);
        return true;
    }

    public void Unregister(string accelerator)
    {
        _registered.Remove(accelerator);
    }

    public void Dispose()
    {
        if (_globalMonitor != null)
        {
            NSEvent.RemoveMonitor(_globalMonitor);
            _globalMonitor = null;
        }
        if (_localMonitor != null)
        {
            NSEvent.RemoveMonitor(_localMonitor);
            _localMonitor = null;
        }
        _registered.Clear();
    }

    private void InstallMonitors()
    {
        // Global: fires when app is NOT focused
        _globalMonitor = NSEvent.AddGlobalMonitorForEventsMatchingMask(
            NSEventMask.KeyDown, HandleKeyEvent);

        // Local: fires when app IS focused
        _localMonitor = NSEvent.AddLocalMonitorForEventsMatchingMask(
            NSEventMask.KeyDown, e => { HandleKeyEvent(e); return e; });
    }

    private void HandleKeyEvent(NSEvent e)
    {
        var keyCode = e.KeyCode;
        // Mask off device-dependent bits
        var mods = e.ModifierFlags & (NSEventModifierMask.ShiftKeyMask
            | NSEventModifierMask.ControlKeyMask
            | NSEventModifierMask.AlternateKeyMask
            | NSEventModifierMask.CommandKeyMask);

        foreach (var entry in _registered.Values)
        {
            if (entry.keyCode == keyCode && entry.mods == mods)
            {
                try { entry.callback(); } catch { }
                return;
            }
        }
    }

    // ── Accelerator parser ─────────────────────────────────────────────────

    private static bool TryParseAccelerator(string accelerator, out ushort keyCode, out NSEventModifierMask modifiers)
    {
        keyCode = 0; modifiers = 0;
        var parts = accelerator.Split('+');
        if (parts.Length == 0) return false;

        var key = parts[^1].Trim().ToUpperInvariant();
        modifiers = 0;
        for (int i = 0; i < parts.Length - 1; i++)
        {
            modifiers |= parts[i].Trim().ToUpperInvariant() switch
            {
                "CONTROL" or "CTRL" => NSEventModifierMask.ControlKeyMask,
                "SHIFT" => NSEventModifierMask.ShiftKeyMask,
                "ALT" or "OPTION" => NSEventModifierMask.AlternateKeyMask,
                "META" or "COMMAND" or "CMD" => NSEventModifierMask.CommandKeyMask,
                "COMMANDORCONTROL" or "CTRLORCMD" => NSEventModifierMask.CommandKeyMask,
                _ => 0
            };
        }

        keyCode = key switch
        {
            "A" => 0x00, "B" => 0x0B, "C" => 0x08, "D" => 0x02, "E" => 0x0E,
            "F" => 0x03, "G" => 0x05, "H" => 0x04, "I" => 0x22, "J" => 0x26,
            "K" => 0x28, "L" => 0x25, "M" => 0x2E, "N" => 0x2D, "O" => 0x1F,
            "P" => 0x23, "Q" => 0x0C, "R" => 0x0F, "S" => 0x01, "T" => 0x11,
            "U" => 0x20, "V" => 0x09, "W" => 0x0D, "X" => 0x07, "Y" => 0x10,
            "Z" => 0x06,
            "0" => 0x1D, "1" => 0x12, "2" => 0x13, "3" => 0x14, "4" => 0x15,
            "5" => 0x17, "6" => 0x16, "7" => 0x1A, "8" => 0x1C, "9" => 0x19,
            "F1" => 0x7A, "F2" => 0x78, "F3" => 0x63, "F4" => 0x76,
            "F5" => 0x60, "F6" => 0x61, "F7" => 0x62, "F8" => 0x64,
            "F9" => 0x65, "F10" => 0x6D, "F11" => 0x67, "F12" => 0x6F,
            "ENTER" or "RETURN" => 0x24,
            "ESCAPE" or "ESC" => 0x35,
            "SPACE" => 0x31,
            "TAB" => 0x30,
            "DELETE" or "BACKSPACE" => 0x33,
            "LEFT" => 0x7B, "RIGHT" => 0x7C, "DOWN" => 0x7D, "UP" => 0x7E,
            _ => ushort.MaxValue
        };
        return keyCode != ushort.MaxValue;
    }
}
