/*---------------------------------------------------------------------------------------------
 *  Copyright (c) 2026 Kaedyn Limon. All rights reserved.
 *  Licensed under the MIT License. See LICENSE in the project root for license information.
 *--------------------------------------------------------------------------------------------*/

using Keystone.Core.Rendering;

namespace Keystone.Core.Animation;

/// <summary>
/// State-driven animation: "when this value changes, animate from old to new."
/// Generic over value type — float and uint (color) have built-in lerps.
/// </summary>
public class Transition<T> where T : struct
{
    readonly uint _durationMs;
    readonly Func<float, float> _easing;
    readonly Func<T, T, float, T> _lerp;
    readonly ValueAnimator _anim = new();

    T _current;
    T _target;
    bool _initialized;

    public Transition(uint durationMs, Func<float, float>? easing = null, Func<T, T, float, T>? lerp = null)
    {
        _durationMs = durationMs;
        _easing = easing ?? Easing.CubicOut;
        _lerp = lerp ?? DefaultLerp;
    }

    /// <summary>Set the target value. Animates if different from current target.</summary>
    public void Set(T value, ulong nowMs)
    {
        if (!_initialized) { _current = _target = value; _initialized = true; return; }
        if (EqualityComparer<T>.Default.Equals(value, _target)) return;
        // Capture current interpolated position as new "from"
        if (_anim.IsActive) _current = _lerp(_current, _target, _anim.Sample(nowMs));
        else _current = _target;
        _target = value;
        _anim.From = 0; _anim.To = 1;
        _anim.DurationMs = _durationMs;
        _anim.EasingFunc = _easing;
        _anim.Start(nowMs);
    }

    /// <summary>Sample the current interpolated value.</summary>
    public T Sample(FrameState state)
    {
        if (!_anim.IsActive) { _current = _target; return _target; }
        float t = _anim.Sample(state.TimeMs);
        if (_anim.IsActive) state.NeedsRedraw = true;
        return _lerp(_current, _target, t);
    }

    /// <summary>Snap to value immediately, no animation.</summary>
    public void Snap(T value) { _current = _target = value; _anim.Reset(); _initialized = true; }

    public T Current => _anim.IsActive ? _lerp(_current, _target, _anim.IsComplete ? 1f : 0f) : _target;

    static T DefaultLerp(T a, T b, float t)
    {
        if (typeof(T) == typeof(float))
            return (T)(object)((float)(object)a! + ((float)(object)b! - (float)(object)a!) * t);
        if (typeof(T) == typeof(uint))
            return (T)(object)ColorLerp.Lerp((uint)(object)a!, (uint)(object)b!, t);
        return t < 0.5f ? a : b;
    }
}
