/**
 * @altruist/prediction — client fixed-step and prediction machinery for Altruist rooms.
 *
 *   FixedStepClock        fixed-timestep accumulator (C# `FixedStepClock`, bit-identical)
 *   runFixedSteps         per-frame step loop with per-step input cutoffs
 *   PredictedSession      prediction, rewind + replay reconciliation, pacing, smoothing
 *   CorrectionSmoother    render-side correction offsets (decayOffset, clampOffset)
 *   InterpolationBuffer   remote-state interpolation (lerp, lerpAngle)
 *
 * Inputs (sequencing, packets, pacing) come from `@altruist/input`.
 */
export * from './fixedStepClock.ts';
export * from './correction.ts';
export * from './interpolation.ts';
export * from './predictedSession.ts';
