/*---------------------------------------------------------------------------------------------
 *  Copyright (c) 2026 Kaedyn Limon. All rights reserved.
 *  Licensed under the MIT License. See LICENSE in the project root for license information.
 *--------------------------------------------------------------------------------------------*/

namespace Keystone.Core.Animation;

/// <summary>
/// Spring physics animator. Set Target, call Step() each frame.
/// Settles naturally — no duration, no easing. Good for interactive elements.
/// </summary>
public class SpringAnimator : IAnimator
{
    public float Value;
    public float Target;
    public float Velocity;

    public float Stiffness = 300f;
    public float Damping = 20f;
    public float Mass = 1f;

    const float SettleVelocity = 0.01f;
    const float SettleDistance = 0.01f;

    public bool IsSettled =>
        MathF.Abs(Velocity) < SettleVelocity && MathF.Abs(Value - Target) < SettleDistance;

    public SpringAnimator() { }

    public SpringAnimator(float stiffness = 300f, float damping = 20f, float mass = 1f)
    {
        Stiffness = stiffness;
        Damping = damping;
        Mass = mass;
    }

    /// <summary>Advance the spring by dtMs milliseconds. Semi-implicit Euler.</summary>
    public void Step(float dtMs)
    {
        if (IsSettled) return;

        float dt = dtMs / 1000f;
        float force = -Stiffness * (Value - Target);
        float dampForce = -Damping * Velocity;
        float accel = (force + dampForce) / Mass;
        Velocity += accel * dt;
        Value += Velocity * dt;
    }

    /// <summary>Snap immediately — zero velocity, value = target.</summary>
    public void SnapTo(float target)
    {
        Value = target;
        Target = target;
        Velocity = 0;
    }

    // IAnimator — springs are always active until settled, Start is a no-op
    bool IAnimator.IsActive => !IsSettled;
    float IAnimator.Sample(ulong nowMs) => Value;
    void IAnimator.Start(ulong nowMs) { }
}
