/*
Copyright 2025 Aron Gere
Licensed under the Apache License, Version 2.0
*/

using Altruist.Physx;
using Altruist.Physx.TwoD;

namespace Altruist.Gaming.TwoD
{
    /// <summary>The physics settings of an organizer-managed 2D world: the index's gravity and fixed step,
    /// with fixed stepping on, so a world advances in whole steps of <see cref="IWorldIndex2D.FixedDeltaTime"/>
    /// whatever the frame time (like the 3D engine). At most <see cref="MaxCatchUpSeconds"/> of simulated time
    /// is run per frame; a longer frame drops the rest instead of spiralling.</summary>
    public static class WorldPhysicsSettings2D
    {
        /// <summary>The longest frame time simulated per call (seconds), the same cap as the 3D engine.</summary>
        public const float MaxCatchUpSeconds = 0.25f;

        /// <summary>Settings for <paramref name="index"/>'s physics engine.</summary>
        /// <param name="index">The world description (gravity, fixed step).</param>
        /// <exception cref="ArgumentOutOfRangeException">The index's fixed step is not positive.</exception>
        public static PhysxWorldSettings2D For(IWorldIndex2D index)
        {
            var fixedDt = index.FixedDeltaTime;
            if (!(fixedDt > 0f))
                throw new ArgumentOutOfRangeException(nameof(index), fixedDt, $"World {index.Index} needs a positive fixedDeltaTime.");

            return new PhysxWorldSettings2D
            {
                Gravity = index.Gravity,
                FixedDeltaTime = fixedDt,
                MaxSubSteps = Math.Max(1, (int)MathF.Ceiling(MaxCatchUpSeconds / fixedDt)),
            };
        }
    }
}
