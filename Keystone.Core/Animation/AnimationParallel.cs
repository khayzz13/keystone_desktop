/*---------------------------------------------------------------------------------------------
 *  Copyright (c) 2026 Kaedyn Limon. All rights reserved.
 *  Licensed under the MIT License. See LICENSE in the project root for license information.
 *--------------------------------------------------------------------------------------------*/

namespace Keystone.Core.Animation;

/// <summary>
/// Runs all child animators simultaneously. Done when all children complete.
/// </summary>
public class AnimationParallel : IAnimator
{
    readonly IAnimator[] _children;
    bool _started;

    public AnimationParallel(params IAnimator[] children) => _children = children;

    public bool IsActive
    {
        get
        {
            if (!_started) return false;
            foreach (var c in _children)
                if (c.IsActive) return true;
            return false;
        }
    }

    public float Sample(ulong nowMs)
    {
        float last = 0;
        foreach (var c in _children) last = c.Sample(nowMs);
        return last;
    }

    public void Start(ulong nowMs)
    {
        _started = true;
        foreach (var c in _children) c.Start(nowMs);
    }
}
