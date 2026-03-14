/*---------------------------------------------------------------------------------------------
 *  Copyright (c) 2026 Kaedyn Limon. All rights reserved.
 *  Licensed under the MIT License. See LICENSE in the project root for license information.
 *--------------------------------------------------------------------------------------------*/

using System;

namespace Keystone.Core.UI;

public static class FlexNodeExtensions
{
    /// <summary>
    /// Fluent in-place configuration helper for FlexNode trees.
    /// </summary>
    public static FlexNode Set(this FlexNode node, Action<FlexNode> configure)
    {
        configure(node);
        return node;
    }
}
