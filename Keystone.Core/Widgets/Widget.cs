/*---------------------------------------------------------------------------------------------
 *  Copyright (c) 2026 Kaedyn Limon. All rights reserved.
 *  Licensed under the MIT License. See LICENSE in the project root for license information.
 *--------------------------------------------------------------------------------------------*/

// Widget - Base class for stateful, reusable UI components.
// Widgets own their state, handle their own actions, and produce FlexNode trees.
// The framework handles mount lifecycle and action routing.
//
// Action routing is fully typed — no string encoding. Widget subclasses define
// integer action IDs as constants and handle them in HandleAction(int).
// FlexNode.WidgetAction pairs the widget reference with the action ID directly.

using Keystone.Core.Animation;
using Keystone.Core.Rendering;
using Keystone.Core.UI;

namespace Keystone.Core.Widgets;

public abstract class Widget
{
    /// <summary>Stable identity for diffing.</summary>
    public string Tag { get; }

    protected Widget(string tag) => Tag = tag;

    /// <summary>Build the visual FlexNode tree. Called each frame by FlexRenderer.</summary>
    public abstract FlexNode Build(FrameState state);

    /// <summary>Handle a typed action targeted at this widget.</summary>
    public virtual void HandleAction(int actionId) { }

    /// <summary>Called once when the widget is first rendered in a live tree.</summary>
    public virtual void OnMount() { }

    /// <summary>Request a redraw on the next frame.</summary>
    protected void Invalidate() => _needsRedraw = true;

    /// <summary>Sample a value animator, keeping NeedsRedraw set while active.</summary>
    protected float Animate(FrameState state, ValueAnimator anim)
        => AnimationFrame.Sample(state, anim);

    /// <summary>Step a spring animator, keeping NeedsRedraw set while unsettled.</summary>
    protected float Animate(FrameState state, SpringAnimator spring)
        => AnimationFrame.Sample(state, spring);

    // --- Framework-internal state ---

    internal bool _needsRedraw;
    internal bool _mounted;
    internal FlexNode? _lastBuild;
}

/// <summary>
/// Typed widget action — pairs a widget reference with an integer action ID.
/// Stored on FlexNode. No string encoding, no parsing.
/// </summary>
public readonly record struct WidgetAction(Widget Widget, int ActionId);
