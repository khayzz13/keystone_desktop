/*---------------------------------------------------------------------------------------------
 *  Copyright (c) 2026 Kaedyn Limon. All rights reserved.
 *  Licensed under the MIT License. See LICENSE in the project root for license information.
 *--------------------------------------------------------------------------------------------*/

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
