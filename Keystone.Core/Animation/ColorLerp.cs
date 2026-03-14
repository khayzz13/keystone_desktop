/*---------------------------------------------------------------------------------------------
 *  Copyright (c) 2026 Kaedyn Limon. All rights reserved.
 *  Licensed under the MIT License. See LICENSE in the project root for license information.
 *--------------------------------------------------------------------------------------------*/

namespace Keystone.Core.Animation;

/// <summary>
/// Color interpolation for RGBA uint colors (0xRRGGBBAAu format).
/// </summary>
public static class ColorLerp
{
    /// <summary>Lerp between two RGBA colors. t=0 returns a, t=1 returns b.</summary>
    public static uint Lerp(uint a, uint b, float t)
    {
        if (t <= 0) return a;
        if (t >= 1) return b;
        uint ra = (a >> 24) & 0xFF, ga = (a >> 16) & 0xFF, ba2 = (a >> 8) & 0xFF, aa = a & 0xFF;
        uint rb = (b >> 24) & 0xFF, gb = (b >> 16) & 0xFF, bb = (b >> 8) & 0xFF, ab = b & 0xFF;
        uint r = (uint)(ra + (rb - ra) * t);
        uint g = (uint)(ga + (gb - ga) * t);
        uint bl = (uint)(ba2 + (bb - ba2) * t);
        uint al = (uint)(aa + (ab - aa) * t);
        return (r << 24) | (g << 16) | (bl << 8) | al;
    }

    /// <summary>Lerp with a ValueAnimator's current progress.</summary>
    public static uint Sample(uint from, uint to, ValueAnimator anim, ulong nowMs)
    {
        float v = anim.Sample(nowMs);
        float t = anim.IsActive ? (v - anim.From) / (anim.To - anim.From) : (anim.IsComplete ? 1 : 0);
        return Lerp(from, to, t);
    }
}
