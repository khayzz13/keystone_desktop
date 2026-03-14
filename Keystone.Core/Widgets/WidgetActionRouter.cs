/*---------------------------------------------------------------------------------------------
 *  Copyright (c) 2026 Kaedyn Limon. All rights reserved.
 *  Licensed under the MIT License. See LICENSE in the project root for license information.
 *--------------------------------------------------------------------------------------------*/

// WidgetActionRouter - Dispatches typed widget actions from ButtonRegistry.
// Widget hits are fully typed (Widget reference + int actionId) — no string
// encoding or parsing. The ButtonRegistry stores WidgetButtonHit alongside
// regular ButtonHit. HitTestResult.WidgetHit carries the typed action through.

using Keystone.Core.Rendering;
using Keystone.Core.UI;

namespace Keystone.Core.Widgets;

public static class WidgetActionRouter
{
    /// <summary>
    /// Process clicks from the previous frame's ButtonRegistry.
    /// Widget actions are dispatched directly via typed WidgetAction.
    /// Returns the string action if the click hit a non-widget button (for plugin handling),
    /// or null if no click occurred / action was consumed by a widget.
    /// </summary>
    public static string? ProcessClicks(FrameState state, ButtonRegistry buttons)
    {
        if (!state.MouseClicked) return null;

        var hit = buttons.HitTest(state.MouseX, state.MouseY);
        if (hit == null) return null;

        // Typed widget action — dispatch directly, no string involved
        if (hit.WidgetHit.HasValue)
        {
            var wa = hit.WidgetHit.Value;
            wa.Widget.HandleAction(wa.ActionId);
            return null;
        }

        return hit.Action;
    }
}
