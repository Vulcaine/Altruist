/**
 * Flow layer. Base of the flow builders — mirror of C# `Altruist.Gaming.Flow.OrderedBuilder`: an
 * ordered list of uniquely named entries editable at setup. Every add appends unless a one-shot
 * position was set just before it (`before` / `after` an existing entry, or `replacing` one).
 * Duplicate names throw. The built object keeps the final order, which is its evaluation order.
 */
type Pending = { kind: 'before' | 'after' | 'replace'; anchor: string } | null;

export abstract class OrderedBuilder<TEntry> {
  private readonly entryNames: string[] = [];
  private readonly entries: TEntry[] = [];
  private pending: Pending = null;

  /** The entry names in their current order. */
  get names(): readonly string[] {
    return this.entryNames;
  }

  /** True when an entry with this name exists. */
  contains(name: string): boolean {
    return this.entryNames.indexOf(name) >= 0;
  }

  /** The next entry is inserted right before `anchor`. */
  before(anchor: string): this {
    return this.setPending('before', anchor);
  }

  /** The next entry is inserted right after `anchor`. */
  after(anchor: string): this {
    return this.setPending('after', anchor);
  }

  /** The next entry takes the place of the entry `name` (which is removed). */
  replacing(name: string): this {
    return this.setPending('replace', name);
  }

  /** Removes the entry `name` (throws when there is none). */
  remove(name: string): this {
    const i = this.indexOrThrow(name);
    this.entryNames.splice(i, 1);
    this.entries.splice(i, 1);
    return this;
  }

  /** Adds an entry at the end, or where the pending position says. */
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

  /** The entries in order (copied); throws if a position was set but no entry followed. */
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
