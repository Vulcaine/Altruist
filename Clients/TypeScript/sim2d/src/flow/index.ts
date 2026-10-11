/** Flow layer: structural building blocks of a deterministic simulation — rule selectors, tick
 * pipelines, modifier stacks, utility selection, overlap latches, timers, input edges, entity
 * registries, event sinks and the state machine. Mirrors C# `Altruist.Gaming.Flow` (and the
 * `Altruist.Gaming` state machine). Built on the math layer only; no physics, no game concepts. */
export { OrderedBuilder } from './orderedBuilder.ts';
export { FirstMatch, FirstMatchActions, FirstMatchBuilder, FirstMatchActionsBuilder, NO_MATCH } from './firstMatch.ts';
export type { NoMatch } from './firstMatch.ts';
export { TickPipeline, TickPipelineBuilder, EntitySteps } from './tickPipeline.ts';
export { ModifierStack, ModifierStackBuilder } from './modifierStack.ts';
export type { ModifierOp } from './modifierStack.ts';
export { UtilitySelector, UtilitySelectorBuilder } from './utilitySelector.ts';
export type { NoiseSource, OptionSettings, UtilityOverride, UtilityScores } from './utilitySelector.ts';
export * as IdMask32 from './idMask32.ts';
export { OverlapLatch32, OverlapLatch } from './overlapLatch.ts';
export * as Countdown from './countdown.ts';
export { TimerSet } from './timerSet.ts';
export { ButtonEdges, InputLatch } from './inputLatch.ts';
export * as Stick from './stick.ts';
export { EntityRegistry } from './entityRegistry.ts';
export { EventSink, nullEventSink } from './eventSink.ts';
export type { EventSinkLike } from './eventSink.ts';
export * as StateContext from './stateContext.ts';
export type { StateContextCore, StateWindow } from './stateContext.ts';
export { AIContext, AIStateMachine, StateMachine, StateMachineBuilder } from './stateMachine.ts';
export type { StateConfig, StateHandler, StateHook, StateMachineDef } from './stateMachine.ts';
