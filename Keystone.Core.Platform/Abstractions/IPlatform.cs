/*---------------------------------------------------------------------------------------------
 *  Copyright (c) 2026 Kaedyn Limon. All rights reserved.
 *  Licensed under the MIT License. See LICENSE in the project root for license information.
 *--------------------------------------------------------------------------------------------*/

using Keystone.Core;
using Keystone.Core.Rendering;

namespace Keystone.Core.Platform;

public record WindowConfig(
    double X, double Y, double Width, double Height,
    bool Floating = false, string TitleBarStyle = "hidden",
    bool Renderless = false, bool Headless = false,
    double? MinWidth = null, double? MinHeight = null,
    double? MaxWidth = null, double? MaxHeight = null,
    double? AspectRatio = null, double? Opacity = null,
    bool Fullscreen = false, bool Resizable = true);

public record OpenDialogOptions(
    string? Title = null, bool Multiple = false, string[]? FileExtensions = null);

public record SaveDialogOptions(
    string? Title = null, string? DefaultName = null);

public record MessageBoxOptions(
    string Title = "", string Message = "", string[]? Buttons = null);

/// <summary>Information about a display/monitor.</summary>
public record DisplayInfo(double X, double Y, double Width, double Height, double ScaleFactor, bool IsPrimary);

/// <summary>Current power supply state. BatteryPercent = -1 when unknown (desktop/AC-only).</summary>
public record PowerStatus(bool OnBattery, int BatteryPercent);

public interface IPlatform
{
    void Initialize();
    void Quit();
    void PumpRunLoop(double seconds = 0.01);

    (double x, double y, double w, double h) GetMainScreenFrame();

    void SetCursor(CursorType cursor);
    (double x, double y) GetMouseLocation();
    bool IsMouseButtonDown();

    void BringAllWindowsToFront();
    INativeWindow CreateWindow(WindowConfig config);
    INativeWindow CreateOverlayWindow(WindowConfig config);

    Task<string[]?> ShowOpenDialogAsync(OpenDialogOptions opts);
    Task<string?> ShowSaveDialogAsync(SaveDialogOptions opts);
    Task<int> ShowMessageBoxAsync(MessageBoxOptions opts);

    void OpenExternal(string url);
    void OpenPath(string path);

    // ── Clipboard ──────────────────────────────────────────────────────────────
    string? ClipboardReadText();
    void ClipboardWriteText(string text);
    void ClipboardClear();
    bool ClipboardHasText();

    // ── Screen ─────────────────────────────────────────────────────────────────
    IReadOnlyList<DisplayInfo> GetAllDisplays();

    // ── System state ───────────────────────────────────────────────────────────
    bool IsDarkMode();
    PowerStatus GetPowerStatus();

    // ── Power management ──────────────────────────────────────────────────────
    /// <summary>Prevent idle system sleep. Returns an opaque token to pass to EndPreventSleep.</summary>
    object? BeginPreventSleep(string reason);
    void EndPreventSleep(object? token);

    /// <summary>Fired when the system is about to sleep (lid close, forced sleep).</summary>
    event Action? OnSystemWillSleep;
    /// <summary>Fired when the system wakes from sleep.</summary>
    event Action? OnSystemDidWake;

    // ── Notifications ──────────────────────────────────────────────────────────
    Task ShowOsNotification(string title, string body);

    void InitializeMenu(Action<string> onMenuAction, KeystoneConfig? config = null);
    void AddMenuItem(string menu, string title, string action, string shortcut = "");
    void AddToolScripts(string[] scriptNames);

    void SetWindowListProvider(Func<IEnumerable<(string id, string title)>> provider);

    // ── Single instance ─────────────────────────────────────────────────
    /// <summary>Acquire a process-wide single-instance lock. Returns false if another instance owns it.</summary>
    bool TryAcquireSingleInstanceLock(string appId);
    event Action<string[], string>? OnSecondInstance;

    // ── OS open events ──────────────────────────────────────────────────
    event Action<string[]>? OnOpenUrls;
    event Action<string>? OnOpenFile;

    // ── Protocol registration ───────────────────────────────────────────
    bool SetAsDefaultProtocolClient(string scheme);
    bool RemoveAsDefaultProtocolClient(string scheme);
    bool IsDefaultProtocolClient(string scheme);
}
