using Keystone.Core.Rendering;

namespace Keystone.Core.Plugins;

/// <summary>
/// Result of a HitTest call - contains action and cursor info
/// </summary>
public class HitTestResult
{
    /// <summary>
    /// Action to execute on click (e.g. "spawn:my_window", "close_window")
    /// Null if this is just a hover with no click action
    /// </summary>
    public string? Action { get; set; }

    /// <summary>
    /// Cursor to display when hovering this region
    /// </summary>
    public CursorType Cursor { get; set; } = CursorType.Default;

    public HitTestResult() { }

    public HitTestResult(string? action, CursorType cursor = CursorType.Pointer)
    {
        Action = action;
        Cursor = cursor;
    }

    public static HitTestResult Click(string action) => new(action, CursorType.Pointer);
    public static HitTestResult Hover(CursorType cursor) => new(null, cursor);
}
