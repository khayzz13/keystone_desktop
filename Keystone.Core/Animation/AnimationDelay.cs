/*---------------------------------------------------------------------------------------------
 *  Copyright (c) 2026 Kaedyn Limon. All rights reserved.
 *  Licensed under the MIT License. See LICENSE in the project root for license information.
 *--------------------------------------------------------------------------------------------*/

namespace Keystone.Core.Animation;

/// <summary>
/// Waits a specified duration, then runs a child animator.
/// </summary>
public class AnimationDelay : IAnimator
{
    readonly IAnimator _child;
    readonly uint _delayMs;
    ulong _startMs;
    bool _started;
    bool _delayElapsed;

    public AnimationDelay(uint delayMs, IAnimator child) { _delayMs = delayMs; _child = child; }

    public bool IsActive => _started && (!_delayElapsed || _child.IsActive);

    public float Sample(ulong nowMs)
    {
        if (!_started) return 0;
        if (!_delayElapsed)
        {
            if (nowMs - _startMs < _delayMs) return 0;
            _delayElapsed = true;
            _child.Start(nowMs);
        }
        return _child.Sample(nowMs);
    }

    public void Start(ulong nowMs) { _startMs = nowMs; _started = true; _delayElapsed = false; }
}
