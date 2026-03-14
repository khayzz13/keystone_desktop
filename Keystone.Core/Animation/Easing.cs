/*---------------------------------------------------------------------------------------------
 *  Copyright (c) 2026 Kaedyn Limon. All rights reserved.
 *  Licensed under the MIT License. See LICENSE in the project root for license information.
 *--------------------------------------------------------------------------------------------*/

namespace Keystone.Core.Animation;

public static class Easing
{
    public static float Linear(float t) => t;
    public static float CubicIn(float t) => t * t * t;
    public static float CubicOut(float t) { float u = 1 - t; return 1 - u * u * u; }
    public static float CubicInOut(float t) =>
        t < 0.5f ? 4 * t * t * t : 1 - MathF.Pow(-2 * t + 2, 3) / 2;

    public static float QuadIn(float t) => t * t;
    public static float QuadOut(float t) => 1 - (1 - t) * (1 - t);
    public static float QuadInOut(float t) =>
        t < 0.5f ? 2 * t * t : 1 - MathF.Pow(-2 * t + 2, 2) / 2;

    public static float BounceOut(float t)
    {
        const float n1 = 7.5625f, d1 = 2.75f;
        if (t < 1 / d1) return n1 * t * t;
        if (t < 2 / d1) return n1 * (t -= 1.5f / d1) * t + 0.75f;
        if (t < 2.5f / d1) return n1 * (t -= 2.25f / d1) * t + 0.9375f;
        return n1 * (t -= 2.625f / d1) * t + 0.984375f;
    }

    public static float BounceIn(float t) => 1 - BounceOut(1 - t);

    public static float ElasticOut(float t)
    {
        if (t is 0 or 1) return t;
        return MathF.Pow(2, -10 * t) * MathF.Sin((t * 10 - 0.75f) * (2 * MathF.PI / 3)) + 1;
    }

    public static float ElasticIn(float t)
    {
        if (t is 0 or 1) return t;
        return -MathF.Pow(2, 10 * t - 10) * MathF.Sin((t * 10 - 10.75f) * (2 * MathF.PI / 3));
    }

    public static float BackOut(float t)
    {
        const float c1 = 1.70158f, c3 = c1 + 1;
        return 1 + c3 * MathF.Pow(t - 1, 3) + c1 * MathF.Pow(t - 1, 2);
    }

    public static float BackIn(float t)
    {
        const float c1 = 1.70158f, c3 = c1 + 1;
        return c3 * t * t * t - c1 * t * t;
    }
}
