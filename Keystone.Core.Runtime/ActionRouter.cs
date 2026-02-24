using System.Diagnostics;
using Keystone.Core.Management;
using Keystone.Core.Management.Bun;
using Keystone.Core.Platform;

namespace Keystone.Core.Runtime;

/// <summary>
/// Routes actions from HitTest results to appropriate handlers.
/// Built-in actions handle spawn/close/menu/tab/workspace/bind/layout.
/// Apps register OnUnhandledAction for domain-specific actions.
/// </summary>
public class ActionRouter
{
    private readonly WindowManager _windowManager;

    /// <summary>
    /// Callback for app-defined action strings. Fired after all built-in patterns fail.
    /// Parameters: (action, sourceWindowId)
    /// </summary>
    public static event Action<string, string>? OnUnhandledAction;

    public ActionRouter(WindowManager windowManager)
    {
        _windowManager = windowManager;
    }

    public void Execute(string action, string sourceWindowId)
    {
        if (string.IsNullOrEmpty(action))
            return;

        // Silent actions — app-internal prefixes that don't route through here
        if (action.StartsWith("s_") || action.StartsWith("__"))
            return;

        // Handle slot-prefixed actions from BindContainer (format: slot:{slotId}:{action})
        if (action.StartsWith("slot:"))
        {
            var firstColon = action.IndexOf(':', 5);
            if (firstColon > 5)
            {
                var slotId = action[5..firstColon];
                var innerAction = action[(firstColon + 1)..];
                Execute(innerAction, slotId);
                return;
            }
        }

        if (action.StartsWith("spawn:"))
            _windowManager.SpawnWindow(action[6..]);
        else if (action == "close_window")
            _windowManager.CloseWindow(sourceWindowId);
        else if (action.StartsWith("menu:"))
            _windowManager.ToggleMenu(sourceWindowId, action[5..]);
        else if (action.StartsWith("tool:"))
            _windowManager.SetTool(sourceWindowId, action[5..]);
        else if (action == "bind_mode")
            _windowManager.EnterBindMode();
        else if (action == "bind_select")
            _windowManager.ToggleBindSelection(sourceWindowId);
        else if (action == "bind_execute")
            _windowManager.ExecuteBind();
        else if (action.StartsWith("popout:"))
        {
            var parts = action[7..].Split(':');
            if (parts.Length == 2 && int.TryParse(parts[1], out var slotIndex))
                _windowManager.PopoutFromBind(parts[0], slotIndex);
        }
        else if (action == "open_layouts")
        {
            _windowManager.ShowWorkspacePanel = !_windowManager.ShowWorkspacePanel;
            if (!_windowManager.ShowWorkspacePanel)
                _windowManager.CloseOverlay();
        }
        else if (action == "drag_start")
            _windowManager.StartDrag(sourceWindowId);
        else if (action == "close_bind")
            _windowManager.DissolveBindContainer(sourceWindowId);
        else if (action == "minimize")
            _windowManager.MinimizeWindow(sourceWindowId);
        else if (action == "toggle_float")
            _windowManager.ToggleAlwaysOnTop(sourceWindowId);
        else if (action == "quit_app")
            _windowManager.QuitApp();
        else if (action == "dev_console")
        {
            var logPath = Environment.GetEnvironmentVariable("KEYSTONE_LOG")
                ?? Path.Combine(Path.GetTempPath(), "keystone.log");
            Process.Start(new ProcessStartInfo(logPath) { UseShellExecute = true });
        }
        else if (action == "bring_all_front")
            _windowManager.BringAllWindowsToFront();
        else if (action.StartsWith("run_tool:"))
            ScriptManager.RunTool(action[9..]);
        else if (action.StartsWith("open_url:"))
        {
            var url = action[9..];
            if (!string.IsNullOrEmpty(url))
                Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        else if (action.StartsWith("reconnect:"))
        {
            var serviceName = action[10..];
            StateManager.Instance.GetService(serviceName)?.Reconnect?.Invoke();
        }
        else if (action.StartsWith("tab_drag:"))
            _windowManager.StartTabDrag(sourceWindowId, action[9..]);
        else if (action.StartsWith("tab_close:"))
            _windowManager.CloseTabInGroup(sourceWindowId, action[10..]);
        else if (action.StartsWith("tab_select:"))
            _windowManager.SelectTabInGroup(sourceWindowId, action[11..]);
        else if (action.StartsWith("tab_popout:"))
            _windowManager.PopoutTab(sourceWindowId, action[11..]);
        else if (action.StartsWith("tab_merge:"))
            _windowManager.MergeIntoTabGroup(action[10..], sourceWindowId);
        else if (action.StartsWith("workspace_save:"))
            _windowManager.SaveWorkspace(action[15..]);
        else if (action.StartsWith("workspace_load:"))
            _windowManager.LoadWorkspace(action[15..]);
        else if (action.StartsWith("workspace_delete:"))
            _windowManager.DeleteWorkspace(action[17..]);
        else if (action == "workspace_save_current")
        {
            var count = _windowManager.GetWorkspaces().Count + 1;
            _windowManager.SaveWorkspace($"Workspace {count}");
        }
        else if (action.StartsWith("layout_save:"))
        {
            var parts = action[12..].Split(':', 2);
            if (parts.Length == 2) _windowManager.SaveLayout(parts[0], parts[1]);
        }
        else if (action.StartsWith("layout_load:"))
        {
            var parts = action[12..].Split(':', 2);
            if (parts.Length == 2) _windowManager.LoadLayout(parts[0], parts[1]);
        }
        else if (action.StartsWith("layout_delete:"))
            _windowManager.DeleteLayout(action[14..]);
        else if (action.StartsWith("bun:"))
            BunManager.Instance.HandleAction(action[4..]);
        // SDK-compatible window actions (fired via keystone().action() from bridge typed namespaces)
        else if (action == "window:minimize")
            _windowManager.MinimizeWindow(sourceWindowId);
        else if (action == "window:maximize")
            _windowManager.MaximizeWindow(sourceWindowId);
        else if (action == "window:close")
            _windowManager.CloseWindow(sourceWindowId);
        else if (action == "app:quit")
            _windowManager.QuitApp();
        else if (action.StartsWith("shell:openExternal:"))
        {
            var url = action[19..];
            if (!string.IsNullOrEmpty(url))
                Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        else
            OnUnhandledAction?.Invoke(action, sourceWindowId);
    }

    public static bool IsGlobalAction(string action)
    {
        if (string.IsNullOrEmpty(action)) return false;

        // All non-silent actions are global — built-in or app-defined via OnUnhandledAction
        return !action.StartsWith("s_") && !action.StartsWith("__");
    }
}
