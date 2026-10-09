/**
 * Flow layer. Base of the flow builders — mirror of C# `Altruist.Gaming.Flow.OrderedBuilder`: an
 * ordered list of uniquely named entries editable at setup. Every add appends unless a one-shot
 * position was set just before it (`before` / `after` an existing entry, or `replacing` one).
 * Duplicate names throw. The built object keeps the final order, which is its evaluation order.
 */
type Pending = { kind: 'before' | 'after' | 'replace'; anchor: string } | null;

/**
 * Base of the flow builders ({@link FirstMatchBuilder}, {@link FirstMatchActionsBuilder},
 * {@link TickPipelineBuilder}, {@link EntitySteps}, {@link ModifierStackBuilder}): an ordered list of
 * uniquely named entries, editable by name at setup so a later module can insert, replace or remove
 * a rule / step without touching the original definition. Mirrors C#
 * `OrderedBuilder<TSelf, TEntry>` (TS uses polymorphic `this` instead of `TSelf`).
 *
 * Throws (`Error`) on: an empty name, a duplicate name, an unknown anchor, a second pending
 * position, or `build()` with a position pending but no entry after it.
 * @typeParam TEntry - The subclass's entry record (internal).
 * @example
 * ```ts
 * const b = TickPipeline.create<Ctx>().step('input', readInput).step('physics', stepWorld);
 * b.before('physics').step('ai', runAgents); // input, ai, physics
 * b.replacing('input').step('replay', readReplay);
 * ```
 */
export abstract class OrderedBuilder<TEntry> {
  private readonly entryNames: string[] = [];
  private readonly entries: TEntry[] = [];
  private pending: Pending = null;

  /** The entry names in their current order (live view; do not mutate). C# `OrderedBuilder.Names`. */
  get names(): readonly string[] {
    return this.entryNames;
  }

  /** True when an entry with this name exists. C# `OrderedBuilder.Contains`. */
  contains(name: string): boolean {
    return this.entryNames.indexOf(name) >= 0;
  }

  /** The next added entry is inserted right before `anchor` (one-shot; throws if `anchor` is unknown or
   * a position is already pending). C# `OrderedBuilder.Before`. @returns this. */
  before(anchor: string): this {
    return this.setPending('before', anchor);
  }

  /** The next added entry is inserted right after `anchor` (one-shot). C# `OrderedBuilder.After`. @returns this. */
  after(anchor: string): this {
    return this.setPending('after', anchor);
  }

  /** The next added entry takes the place of the entry `name` (which is removed when it is added; the
   * new entry may reuse the name). C# `OrderedBuilder.Replacing`. @returns this. */
  replacing(name: string): this {
    return this.setPending('replace', name);
  }

  /** Removes the entry `name` now (throws when there is none). C# `OrderedBuilder.Remove`. @returns this. */
  remove(name: string): this {
    const i = this.indexOrThrow(name);
    this.entryNames.splice(i, 1);
    this.entries.splice(i, 1);
    return this;
  }

  /** For subclasses: adds an entry at the end, or where the pending position says, and clears the
   * pending position. Throws on an empty or duplicate name. */
  protected put(name: string, entry: TEntry): this {
    if (!name) throw new Error('Entries need a name.');
    const pending = this.pending;
    this.pending = null;
    let at = this.entryNames.length;
    if (pending) {
      at = this.indexOrThrow(pending.anchor);
      if (pending.kind === 'after') at += 1;
      else if (pending.kind === 'replace') {
        this.entryNames.splice(at, 1);
        this.entries.splice(at, 1);
      }
    }
    if (this.entryNames.indexOf(name) >= 0) throw new Error(`An entry named '${name}' already exists.`);
    this.entryNames.splice(at, 0, name);
    this.entries.splice(at, 0, entry);
    return this;
  }

  /** For subclasses' `build()`: the entries in order (copied); throws if a position was set but no
   * entry followed. */
  protected snapshot(): { names: string[]; entries: TEntry[] } {
    if (this.pending) throw new Error(`A position (${this.pending.kind} '${this.pending.anchor}') was set but no entry followed it.`);
    return { names: [...this.entryNames], entries: [...this.entries] };
  }

  private setPending(kind: 'before' | 'after' | 'replace', anchor: string): this {
    if (this.pending) throw new Error(`A position (${this.pending.kind} '${this.pending.anchor}') is already pending.`);
    this.indexOrThrow(anchor);
    this.pending = { kind, anchor };
    return this;
  }

  private indexOrThrow(name: string): number {
    const i = this.entryNames.indexOf(name);
    if (i < 0) throw new Error(`No entry named '${name}'.`);
    return i;
  }
}
