namespace Altruist.Client.Inventory.Events;

/// <summary>
/// Fired by the mirror when a slot's contents are updated (placed or
/// modified). Carries the new <see cref="ItemSnapshot"/> as immutable data —
/// handlers must not mutate it.
///
/// <para>Empty-slot transitions are reported through
/// <see cref="SlotClearedEvent"/> rather than this event, so a UI handler can
/// route them differently without inspecting <see cref="ItemSnapshot.IsEmpty"/>.
/// </para>
/// </summary>
/// <param name="Snapshot">The item now anchored at <c>Snapshot.Cell</c>.</param>
public sealed record SlotChangedEvent(ItemSnapshot Snapshot);
