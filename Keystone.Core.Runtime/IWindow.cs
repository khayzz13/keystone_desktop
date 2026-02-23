using Keystone.Core;
// I dont really see why this file exists, when it could probably be somewhere else. 
namespace Keystone.Core.Runtime;

public enum WindowLayoutMode
{
    Standalone,
    Bind,
    TabGroup
}

public enum InputEventType
{
    Unknown,
    MouseDown,
    MouseUp,
    RightMouseDown,
    RightMouseUp,
    MouseMove,
    KeyDown,
    KeyUp,
    ScrollWheel
}
