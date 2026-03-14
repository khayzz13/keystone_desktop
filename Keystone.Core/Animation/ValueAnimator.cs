/*---------------------------------------------------------------------------------------------
 *  Copyright (c) 2026 Kaedyn Limon. All rights reserved.
 *  Licensed under the MIT License. See LICENSE in the project root for license information.
 *--------------------------------------------------------------------------------------------*/

namespace Keystone.Core.Animation;

/// <summary>
/// Scalar tween from From to To over DurationMs with easing.
/// Reusable via Start(). Zero allocation on hot path.
/// </summary>
public class ValueAnimator : IAnimator
{
    public float From;
    public float To;
    public uint DurationMs;
    public Func<float, float> EasingFunc;

    ulong _startMs;
    bool _started;

    public bool IsComplete { get; private set; }
    public bool IsActive => _started && !IsComplete;

    public ValueAnimator() => EasingFunc = Easing.Linear;

    public ValueAnimator(float from, float to, uint durationMs, Func<float, float>? easing = null)
    {
        From = from;
        To = to;
        DurationMs = durationMs;
        EasingFunc = easing ?? Easing.Linear;
    }

    public void Start(ulong nowMs)
    {
        _startMs = nowMs;
        _started = true;
        IsComplete = false;
    }

    public float Sample(ulong nowMs)
    {
        if (!_started) return From;
        if (IsComplete) return To;
        if (DurationMs == 0) { IsComplete = true; return To; }

        float elapsed = nowMs - _startMs;
        float t = Math.Clamp(elapsed / DurationMs, 0f, 1f);
        float eased = EasingFunc(t);

        if (t >= 1f) IsComplete = true;
        return From + (To - From) * eased;
    }

    public void Reset()
    {
        _started = false;
        IsComplete = false;
    }

    /// <summary>Retarget mid-animation. Preserves current value as new From, adjusts for remaining duration.</summary>
    public void Retarget(float newTo, ulong nowMs)
    {
        float current = Sample(nowMs);
        From = current;
        To = newTo;
        _startMs = nowMs;
        _started = true;
        IsComplete = false;
    }

    /// <summary>Reverse direction mid-animation. Current progress is mirrored.</summary>
    public void Reverse(ulong nowMs)
    {
        float current = Sample(nowMs);
        (From, To) = (To, From);
        // Mirror progress — if we were 70% done going forward, we're 30% done going back
        float elapsed = _started ? Math.Clamp((float)(nowMs - _startMs) / DurationMs, 0f, 1f) : 0f;
        float remaining = 1f - elapsed;
        _startMs = nowMs - (ulong)(remaining * DurationMs);
        _started = true;
        IsComplete = false;
    }
}
