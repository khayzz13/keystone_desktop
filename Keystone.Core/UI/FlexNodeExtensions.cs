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
