namespace Altruist.Client.Inventory.Events;

/// <summary>
/// Fired by the mirror when a slot is emptied — either a server-side delete
/// or a wire packet with an empty <see cref="ItemSnapshot.ItemKey"/>. For
/// multi-cell items, this fires once per cleared anchor; the linked cells
/// are invalidated internally without separate events.
/// </summary>
/// <param name="Window">Container id.</param>
/// <param name="Cell">Anchor cell that was cleared.</param>
public sealed record SlotClearedEvent(byte Window, ushort Cell);
