// InputEvent - Lock-free input event for main thread → render thread communication
// Enqueued by WindowManager.RouteInputEvent, drained by WindowRenderThread.
// This file needs to be updated to be more comprehensive 
using Keystone.Core;

namespace Keystone.Core.Runtime;

public readonly struct InputEvent
{
    public readonly InputEventType Type;
    public readonly float X;
    public readonly float Y;
    public readonly float DeltaX;
    public readonly float DeltaY;
    public readonly ushort KeyCode;
    public readonly KeyModifiers Modifiers;

    public InputEvent(InputEventType type, float x = 0, float y = 0, float dx = 0, float dy = 0,
        ushort keyCode = 0, KeyModifiers modifiers = KeyModifiers.None)
    {
        Type = type; X = x; Y = y; DeltaX = dx; DeltaY = dy;
        KeyCode = keyCode; Modifiers = modifiers;
    }

    public static InputEvent MouseDown(float x, float y) => new(InputEventType.MouseDown, x, y);
    public static InputEvent MouseUp(float x, float y) => new(InputEventType.MouseUp, x, y);
    public static InputEvent RightMouseDown(float x, float y) => new(InputEventType.RightMouseDown, x, y);
    public static InputEvent MouseMove(float x, float y) => new(InputEventType.MouseMove, x, y);
    public static InputEvent Scroll(float dx, float dy) => new(InputEventType.ScrollWheel, dx: dx, dy: dy);
    public static InputEvent KeyDown(ushort code, KeyModifiers mods) => new(InputEventType.KeyDown, keyCode: code, modifiers: mods);
    public static InputEvent KeyUp(ushort code, KeyModifiers mods) => new(InputEventType.KeyUp, keyCode: code, modifiers: mods);
}
