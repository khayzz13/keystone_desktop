// MacOSGlobalShortcut — global hotkey registration via Carbon RegisterEventHotKey.
// This is the same mechanism Electron/Tauri use on macOS.
// Requires no special entitlements (unlike CGEventTap which needs Accessibility).

using System.Runtime.InteropServices;
using Keystone.Core.Platform.Abstractions;

namespace Keystone.Core.Platform.MacOS;

[StructLayout(LayoutKind.Sequential)]
internal struct EventHotKeyID { public uint signature; public uint id; }

[StructLayout(LayoutKind.Sequential)]
internal struct EventTypeSpec { public uint eventClass; public uint eventKind; }

internal delegate int EventHandlerUPP(IntPtr nextHandler, IntPtr theEvent, IntPtr userData);

public sealed class MacOSGlobalShortcut : IGlobalShortcutBackend
{
    private readonly Dictionary<string, (uint hotKeyId, IntPtr refHandle)> _registered = new();
    private readonly Dictionary<uint, Action> _callbacks = new();
    private uint _nextId = 1;
    private IntPtr _handlerRef;
    private EventHandlerUPP? _handlerUPP;
    private bool _installed;

    public MacOSGlobalShortcut()
    {
        InstallEventHandler();
    }

    public bool Register(string accelerator, Action onFired)
    {
        if (!TryParseAccelerator(accelerator, out var keyCode, out var modifiers))
        {
            Console.WriteLine($"[MacOSGlobalShortcut] Cannot parse accelerator: {accelerator}");
            return false;
        }

        var id = _nextId++;
        var hotKeyId = new EventHotKeyID { signature = 0x4B535431 /* KST1 */, id = id };

        var result = Carbon.RegisterEventHotKey(keyCode, modifiers, hotKeyId,
            Carbon.GetApplicationEventTarget(), 0, out var refHandle);
        if (result != 0)
        {
            Console.WriteLine($"[MacOSGlobalShortcut] RegisterEventHotKey failed: {result} for {accelerator}");
            return false;
        }

        _registered[accelerator] = (id, refHandle);
        _callbacks[id] = onFired;
        return true;
    }

    public void Unregister(string accelerator)
    {
        if (!_registered.TryGetValue(accelerator, out var entry)) return;
        Carbon.UnregisterEventHotKey(entry.refHandle);
        _callbacks.Remove(entry.hotKeyId);
        _registered.Remove(accelerator);
    }

    public void Dispose()
    {
        foreach (var entry in _registered.Values)
            Carbon.UnregisterEventHotKey(entry.refHandle);
        _registered.Clear();
        _callbacks.Clear();
        if (_installed && _handlerRef != IntPtr.Zero)
            Carbon.RemoveEventHandler(_handlerRef);
    }

    private void InstallEventHandler()
    {
        _handlerUPP = HandleHotKeyEvent;
        var eventTypes = new[]
        {
            new EventTypeSpec { eventClass = Carbon.kEventClassKeyboard, eventKind = Carbon.kEventHotKeyPressed }
        };
        var result = Carbon.InstallApplicationEventHandler(
            _handlerUPP, (uint)eventTypes.Length, eventTypes,
            IntPtr.Zero, out _handlerRef);
        _installed = result == 0;
        if (!_installed)
            Console.WriteLine($"[MacOSGlobalShortcut] InstallApplicationEventHandler failed: {result}");
    }

    private int HandleHotKeyEvent(IntPtr nextHandler, IntPtr theEvent, IntPtr userData)
    {
        var hotKeyId = new EventHotKeyID();
        Carbon.GetEventParameter(theEvent, Carbon.kEventParamDirectObject,
            Carbon.typeEventHotKeyID, IntPtr.Zero,
            (uint)Marshal.SizeOf<EventHotKeyID>(), IntPtr.Zero, ref hotKeyId);
        if (_callbacks.TryGetValue(hotKeyId.id, out var cb))
        {
            try { cb(); } catch { }
        }
        return 0; // noErr
    }

    // ── Accelerator parser ─────────────────────────────────────────────────

    private static bool TryParseAccelerator(string accelerator, out uint keyCode, out uint modifiers)
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
                "CONTROL" or "CTRL" => Carbon.controlKey,
                "SHIFT" => Carbon.shiftKey,
                "ALT" or "OPTION" => Carbon.optionKey,
                "META" or "COMMAND" or "CMD" => Carbon.cmdKey,
                "COMMANDORCONTROL" or "CTRLORCMD" => Carbon.cmdKey, // macOS = Cmd
                _ => 0u
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
            _ => uint.MaxValue
        };
        return keyCode != uint.MaxValue;
    }
}

// ── Carbon P/Invoke ────────────────────────────────────────────────────────

internal static class Carbon
{
    private const string Lib = "/System/Library/Frameworks/Carbon.framework/Carbon";

    public const uint kEventClassKeyboard = 0x6B657962; // 'keyb'
    public const uint kEventHotKeyPressed = 5;
    public const uint kEventParamDirectObject = 0x2D2D2D2D; // '----'
    public const uint typeEventHotKeyID = 0x686B6579; // 'hkey'

    public const uint controlKey = 0x1000;
    public const uint optionKey = 0x0800;
    public const uint cmdKey = 0x0100;
    public const uint shiftKey = 0x0200;

    [DllImport(Lib)]
    public static extern int RegisterEventHotKey(
        uint inHotKeyCode, uint inHotKeyModifiers,
        EventHotKeyID inHotKeyID, IntPtr inTarget,
        uint inOptions, out IntPtr outRef);

    [DllImport(Lib)]
    public static extern int UnregisterEventHotKey(IntPtr inHotKey);

    [DllImport(Lib)]
    public static extern IntPtr GetApplicationEventTarget();

    [DllImport(Lib)]
    public static extern int InstallApplicationEventHandler(
        EventHandlerUPP inHandler, uint inNumTypes,
        [In] EventTypeSpec[] inList,
        IntPtr inUserData, out IntPtr outRef);

    [DllImport(Lib)]
    public static extern int RemoveEventHandler(IntPtr inHandlerRef);

    [DllImport(Lib)]
    public static extern int GetEventParameter(
        IntPtr inEvent, uint inName, uint inDesiredType,
        IntPtr outActualType, uint inBufferSize,
        IntPtr outActualSize, ref EventHotKeyID outData);
}
