/** Physics layer: velocity / angular operations, gravity, drag, contacts, ray casts, ballistics.
 * Built on the math layer only. */
export type { ActivatableBody, AngularBody, Body2DLike, LinearBody, PositionedBody, RayCastWorld } from './body2D.ts';
export * as Velocity2D from './velocity2D.ts';
export * as Kinematics from './kinematics.ts';
export * as Ballistics2D from './ballistics2D.ts';
export type { FlightState } from './ballistics2D.ts';
export { ContactNormals2D } from './contactNormals2D.ts';
export * as BodyMotion2D from './bodyMotion2D.ts';
export * as BodyState2D from './bodyState2D.ts';
export type { BodyState2D as BodyState2DValue } from './bodyState2D.ts';
export { ClosestRayHit2D, rayCastClosest } from './closestRayHit2D.ts';
export { ContactImpact2D } from './contactImpact2D.ts';
export { ContactRouter2D, RoutedContact2D, tagOf, touchingContacts, touchingContactsOf, tryRoute } from './contactRouter2D.ts';
export type { ContactBodyLike, ContactEdgeBodyLike, ContactEdgeLike, ContactFixtureLike, ContactLike, ContactListLike, ContactWorldLike, TagGuard, WorldManifoldLike } from './contactRouter2D.ts';
