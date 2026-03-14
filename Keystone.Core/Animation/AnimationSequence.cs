/*---------------------------------------------------------------------------------------------
 *  Copyright (c) 2026 Kaedyn Limon. All rights reserved.
 *  Licensed under the MIT License. See LICENSE in the project root for license information.
 *--------------------------------------------------------------------------------------------*/

namespace Keystone.Core.Animation;

/// <summary>
/// Runs child animators in sequence. Done when the last child completes.
/// </summary>
public class AnimationSequence : IAnimator
{
    readonly IAnimator[] _children;
    int _currentIndex;
    bool _started;

    public AnimationSequence(params IAnimator[] children) => _children = children;

    public bool IsActive => _started && _currentIndex < _children.Length;

    public float Sample(ulong nowMs)
    {
        if (!_started || _children.Length == 0) return 0;
        while (_currentIndex < _children.Length)
        {
            var child = _children[_currentIndex];
            float v = child.Sample(nowMs);
            if (child.IsActive) return v;
            // Child finished — start the next one at this timestamp
            _currentIndex++;
            if (_currentIndex < _children.Length)
                _children[_currentIndex].Start(nowMs);
        }
        return _children[^1].Sample(nowMs);
    }

    public void Start(ulong nowMs)
    {
        _started = true;
        _currentIndex = 0;
        if (_children.Length > 0)
            _children[0].Start(nowMs);
    }
}
