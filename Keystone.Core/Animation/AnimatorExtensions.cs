/*---------------------------------------------------------------------------------------------
 *  Copyright (c) 2026 Kaedyn Limon. All rights reserved.
 *  Licensed under the MIT License. See LICENSE in the project root for license information.
 *--------------------------------------------------------------------------------------------*/

namespace Keystone.Core.Animation;

/// <summary>
/// Fluent composition sugar for IAnimator.
/// </summary>
public static class AnimatorExtensions
{
    public static AnimationSequence Then(this IAnimator first, IAnimator second)
        => new(first, second);

    public static AnimationParallel With(this IAnimator first, IAnimator second)
        => new(first, second);

    public static AnimationDelay After(this IAnimator child, uint delayMs)
        => new(delayMs, child);
}
