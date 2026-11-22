/**
 * @altruist/ai — game AI for deterministic simulations, the TypeScript twin of Altruist.Gaming's AI
 * helpers (C# `RecedingPlanner<TCand, TFrame>`). Game-agnostic: the game supplies its candidates,
 * their inputs and a rollout on its own simulation.
 */
export { RecedingPlanner } from './recedingPlanner.ts';
export type { PlannedPlan, PlannerOps, PlannerSettings } from './recedingPlanner.ts';
