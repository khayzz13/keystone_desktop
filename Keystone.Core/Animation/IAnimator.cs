/*---------------------------------------------------------------------------------------------
 *  Copyright (c) 2026 Kaedyn Limon. All rights reserved.
 *  Licensed under the MIT License. See LICENSE in the project root for license information.
 *--------------------------------------------------------------------------------------------*/

namespace Keystone.Core.Animation;

public interface IAnimator
{
    float Sample(ulong nowMs);
    bool IsActive { get; }
    void Start(ulong nowMs);
}
