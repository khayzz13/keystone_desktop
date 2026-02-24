// WindowsGlobalShortcut — global hotkey via Win32 RegisterHotKey / WM_HOTKEY.
// Runs a dedicated message-pump thread to receive WM_HOTKEY messages.
// No special permissions required on Windows.

using System.Runtime.InteropServices;
using Keystone.Core.Platform.Abstractions;

namespace Keystone.Core.Platform.Windows;

public sealed class WindowsGlobalShortcut : IGlobalShortcutBackend
{
    private readonly Dictionary<string, (int id, Action callback)> _registered = new();
    private int _nextId = 0xBF00; // use a high range to avoid collisions
    private IntPtr _msgHwnd = IntPtr.Zero;
    private Thread? _thread;
    private volatile bool _running;

    public WindowsGlobalShortcut()
    {
        _running = true;
        _thread = new Thread(MessagePump) { IsBackground = true, Name = "GlobalShortcut" };
        _thread.Start();
        // Wait briefly for the HWND to be created on the message thread
        SpinWait.SpinUntil(() => _msgHwnd != IntPtr.Zero, 1000);
    }

    public bool Register(string accelerator, Action onFired)
    {
        if (!TryParseAccelerator(accelerator, out var vkCode, out var modifiers))
        {
            Console.WriteLine($"[WindowsGlobalShortcut] Cannot parse accelerator: {accelerator}");
            return false;
        }

        var id = _nextId++;
        var tcs = new TaskCompletionSource<bool>();

        // RegisterHotKey must be called on the message thread
        PostToMessageThread(() =>
        {
            var ok = HotKeyWin32.RegisterHotKey(_msgHwnd, id, modifiers, vkCode);
            if (!ok) Console.WriteLine($"[WindowsGlobalShortcut] RegisterHotKey failed for: {accelerator}");
            tcs.TrySetResult(ok);
        });

        if (!tcs.Task.Wait(2000) || !tcs.Task.Result) return false;

        lock (_registered) _registered[accelerator] = (id, onFired);
        return true;
    }

    public void Unregister(string accelerator)
    {
        int id;
        lock (_registered)
        {
            if (!_registered.TryGetValue(accelerator, out var entry)) return;
            id = entry.id;
            _registered.Remove(accelerator);
        }
        PostToMessageThread(() => HotKeyWin32.UnregisterHotKey(_msgHwnd, id));
    }

    public void Dispose()
    {
        _running = false;
        if (_msgHwnd != IntPtr.Zero)
            HotKeyWin32.PostMessage(_msgHwnd, 0x0012 /* WM_QUIT */, IntPtr.Zero, IntPtr.Zero);
        _thread?.Join(2000);
    }

    private readonly Queue<Action> _threadQueue = new();

    private void PostToMessageThread(Action action)
    {
        lock (_threadQueue) _threadQueue.Enqueue(action);
        if (_msgHwnd != IntPtr.Zero)
            HotKeyWin32.PostMessage(_msgHwnd, HotKeyWin32.WM_APP_EXEC, IntPtr.Zero, IntPtr.Zero);
    }

    private void MessagePump()
    {
        // Create a message-only window to receive WM_HOTKEY
        _msgHwnd = HotKeyWin32.CreateWindowEx(0, "STATIC", "KeystoneHotkey",
            0, 0, 0, 0, 0, (IntPtr)(-3) /* HWND_MESSAGE */, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);

        while (_running)
        {
            var got = HotKeyWin32.GetMessage(out var msg, IntPtr.Zero, 0, 0);
            if (got == 0 || got == -1) break;

            if (msg.message == HotKeyWin32.WM_HOTKEY)
            {
                var id = (int)(msg.wParam.ToInt64());
                Action? cb = null;
                lock (_registered)
                {
                    foreach (var (_, entry) in _registered)
                        if (entry.id == id) { cb = entry.callback; break; }
                }
                try { cb?.Invoke(); } catch { }
                continue;
            }

            if (msg.message == HotKeyWin32.WM_APP_EXEC)
            {
                Action[]? pending;
                lock (_threadQueue)
                {
                    pending = _threadQueue.ToArray();
                    _threadQueue.Clear();
                }
                foreach (var a in pending) { try { a(); } catch { } }
                continue;
            }

            HotKeyWin32.TranslateMessage(ref msg);
            HotKeyWin32.DispatchMessage(ref msg);
        }
    }

    // ── Accelerator parser ─────────────────────────────────────────────────

    private static bool TryParseAccelerator(string accelerator, out uint vkCode, out uint modifiers)
    {
        vkCode = 0; modifiers = 0;
        var parts = accelerator.Split('+');
        if (parts.Length == 0) return false;

        var key = parts[^1].Trim().ToUpperInvariant();
        for (int i = 0; i < parts.Length - 1; i++)
        {
            modifiers |= parts[i].Trim().ToUpperInvariant() switch
            {
                "CONTROL" or "CTRL" => HotKeyWin32.MOD_CONTROL,
                "SHIFT" => HotKeyWin32.MOD_SHIFT,
                "ALT" or "OPTION" => HotKeyWin32.MOD_ALT,
                "META" or "WIN" => HotKeyWin32.MOD_WIN,
                "COMMANDORCONTROL" or "CTRLORCMD" => HotKeyWin32.MOD_CONTROL, // Windows = Ctrl
                _ => 0u
            };
        }

        vkCode = key switch
        {
            var k when k.Length == 1 && k[0] >= 'A' && k[0] <= 'Z' => (uint)k[0],
            var k when k.Length == 1 && k[0] >= '0' && k[0] <= '9' => (uint)k[0],
            "F1" => 0x70, "F2" => 0x71, "F3" => 0x72, "F4" => 0x73,
            "F5" => 0x74, "F6" => 0x75, "F7" => 0x76, "F8" => 0x77,
            "F9" => 0x78, "F10" => 0x79, "F11" => 0x7A, "F12" => 0x7B,
            "ENTER" or "RETURN" => 0x0D,
            "ESCAPE" or "ESC" => 0x1B,
            "SPACE" => 0x20,
            "TAB" => 0x09,
            "DELETE" => 0x2E,
            "BACKSPACE" => 0x08,
            "LEFT" => 0x25, "UP" => 0x26, "RIGHT" => 0x27, "DOWN" => 0x28,
            _ => uint.MaxValue
        };
        return vkCode != uint.MaxValue;
    }
}

internal static class HotKeyWin32
{
    public const uint MOD_ALT = 0x0001;
    public const uint MOD_CONTROL = 0x0002;
    public const uint MOD_SHIFT = 0x0004;
    public const uint MOD_WIN = 0x0008;
    public const uint MOD_NOREPEAT = 0x4000;

    public const uint WM_HOTKEY = 0x0312;
    public const uint WM_APP_EXEC = 0x8001; // private app message

    [StructLayout(LayoutKind.Sequential)]
    public struct MSG
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam;
        public IntPtr lParam;
        public uint time;
        public int ptX, ptY;
    }

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll")]
    public static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern IntPtr CreateWindowEx(
        uint dwExStyle, string lpClassName, string lpWindowName,
        uint dwStyle, int x, int y, int w, int h,
        IntPtr hWndParent, IntPtr hMenu, IntPtr hInstance, IntPtr lpParam);

    [DllImport("user32.dll")]
    public static extern int GetMessage(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

    [DllImport("user32.dll")]
    public static extern bool TranslateMessage(ref MSG lpMsg);

    [DllImport("user32.dll")]
    public static extern IntPtr DispatchMessage(ref MSG lpMsg);

    [DllImport("user32.dll")]
    public static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
}
