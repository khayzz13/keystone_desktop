/*---------------------------------------------------------------------------------------------
 *  Copyright (c) 2026 Kaedyn Limon. All rights reserved.
 *  Licensed under the MIT License. See LICENSE in the project root for license information.
 *--------------------------------------------------------------------------------------------*/

// AnimationFrame - Integration point between animation primitives and the render loop.
// Samples an animator and automatically keeps NeedsRedraw set while the animation is active.

using Keystone.Core.Rendering;

namespace Keystone.Core.Animation;

public static class AnimationFrame
{
    /// <summary>Sample a value animator. Keeps NeedsRedraw set while active.</summary>
    public static float Sample(FrameState state, ValueAnimator anim)
    {
        float v = anim.Sample(state.TimeMs);
        if (anim.IsActive) state.NeedsRedraw = true;
        return v;
    }

    /// <summary>Sample a transition. Keeps NeedsRedraw set while animating.</summary>
    public static T Sample<T>(FrameState state, Transition<T> transition) where T : struct
        => transition.Sample(state);

    /// <summary>Step a spring animator by frame delta. Keeps NeedsRedraw set while unsettled.</summary>
    public static float Sample(FrameState state, SpringAnimator spring)
    {
        spring.Step(state.DeltaMs);
        if (!spring.IsSettled)
            state.NeedsRedraw = true;
        else
            spring.Value = spring.Target; // snap to avoid float drift
        return spring.Value;
    }
}
