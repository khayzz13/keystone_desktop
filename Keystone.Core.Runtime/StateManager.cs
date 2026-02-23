// StateManager - Centralized transient state for engine infrastructure
// Manages per-window input, menus, drag, context menus, connection, and services

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;

using Keystone.Core;

namespace Keystone.Core.Runtime;

// Per-window input state (transient, not persisted)
public class InputState
{
    public float MouseX { get; set; }
    public float MouseY { get; set; }
    public bool MouseDown { get; set; }
    public bool MouseClicked { get; set; }
    public bool RightClick { get; set; }
    public float MouseScroll { get; set; }
    public ulong FrameSequence { get; set; }
}

// Menu UI state — apps define their own menu identifiers as strings
public class MenuState
{
    public string? Active { get; set; }
    public bool ContextMenuOpen { get; set; }
    public float ContextMenuX { get; set; }
    public float ContextMenuY { get; set; }
    public string? OpenColorPicker { get; set; }
}

// Drag state — generic fields only
public class DragState
{
    public bool IsDragging { get; set; }
    public float DragStartX { get; set; }
    public float DragStartY { get; set; }
}

// Connection/latency state
public class ConnectionState
{
    public bool IsConnected { get; set; }
    public long LatencyMs { get; set; } = -1;
    public long ClockDriftMs { get; set; }
    public DateTime LastUpdate { get; set; } = DateTime.MinValue;
}

// Service connection status
public enum ServiceStatus { Disconnected, Connecting, Connected, Failed }
public record ServiceInfo(string Name, ServiceStatus Status, string? Error = null, Action? Reconnect = null);

// Context menu state
public class ContextMenuState
{
    public bool IsOpen { get; set; }
    public float X { get; set; }
    public float Y { get; set; }
    public string? OpenSubmenuId { get; set; }
    public string? ActiveSliderId { get; set; }
    public string? ActiveColorId { get; set; }
}

// Transient state (not persisted) - uses ConcurrentDictionary to reduce lock contention
public class TransientState
{
    public ConcurrentDictionary<string, InputState> Input { get; } = new();
    public ConcurrentDictionary<string, MenuState> Menus { get; } = new();
    public ConcurrentDictionary<string, DragState> DragStates { get; } = new();
    public ConcurrentDictionary<string, ContextMenuState> ContextMenus { get; } = new();
    public ConcurrentDictionary<string, ServiceInfo> Services { get; } = new();
    public ConnectionState Connection { get; } = new();
}

// State manager singleton
public class StateManager
{
    private static StateManager? _instance;
    public static StateManager Instance => _instance ??= new StateManager();

    private readonly TransientState _transient = new();

    private StateManager() { }

    // --- Input State (lock-free via ConcurrentDictionary) ---
    public InputState GetInput(string windowId) =>
        _transient.Input.GetOrAdd(windowId, _ => new InputState());

    // --- Menu State (lock-free) ---
    public MenuState GetMenu(string windowId) =>
        _transient.Menus.GetOrAdd(windowId, _ => new MenuState());

    public void SetActiveMenu(string windowId, string? menu) =>
        GetMenu(windowId).Active = menu;

    // --- Drag State (lock-free) ---
    public DragState GetDragState(string windowId) =>
        _transient.DragStates.GetOrAdd(windowId, _ => new DragState());

    // --- Context Menu State (lock-free) ---
    public ContextMenuState GetContextMenu(string windowId) =>
        _transient.ContextMenus.GetOrAdd(windowId, _ => new ContextMenuState());

    // --- Window cleanup ---
    public void RemoveWindowState(string windowId)
    {
        _transient.Input.TryRemove(windowId, out _);
        _transient.Menus.TryRemove(windowId, out _);
        _transient.DragStates.TryRemove(windowId, out _);
        _transient.ContextMenus.TryRemove(windowId, out _);
    }

    // --- Connection State ---
    public ConnectionState GetConnection() => _transient.Connection;

    public void UpdateConnection(bool connected, long latencyMs, long clockDriftMs)
    {
        var conn = _transient.Connection;
        conn.IsConnected = connected;
        conn.LatencyMs = latencyMs;
        conn.ClockDriftMs = clockDriftMs;
        conn.LastUpdate = DateTime.UtcNow;
    }

    // --- Service Status ---
    public void RegisterService(string name, ServiceStatus status, string? error = null, Action? reconnect = null)
        => _transient.Services[name] = new ServiceInfo(name, status, error, reconnect);

    public void UpdateServiceStatus(string name, ServiceStatus status, string? error = null)
    {
        if (_transient.Services.TryGetValue(name, out var existing))
            _transient.Services[name] = existing with { Status = status, Error = error };
    }

    public IEnumerable<ServiceInfo> GetServices() => _transient.Services.Values;
    public ServiceInfo? GetService(string name) => _transient.Services.TryGetValue(name, out var s) ? s : null;
}
