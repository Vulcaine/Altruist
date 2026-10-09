/**
 * Flow layer. Entities in ascending id order with O(1) lookup — mirror of C# `EntityRegistry<T>`
 * (number ids). `items` is the id-ordered array to iterate (do not mutate it directly).
 */
/** Id-keyed collection iterated in ascending id order (deterministic regardless of insertion order).
 * Mirrors C# `EntityRegistry<T>` (= `EntityRegistry<int, T>`). Add / remove are O(n) (array splice);
 * lookups are O(1).
 * @example
 * ```ts
 * const units = new EntityRegistry<Unit>();
 * units.add(7, a); units.add(3, b);
 * for (const u of units) update(u); // b, then a
 * ```
 */
export class EntityRegistry<T> {
  private readonly keys: number[] = [];
  private readonly list: T[] = [];
  private readonly byId = new Map<number, T>();

  /** The entities in ascending id order (live array; do not mutate). */
  get items(): readonly T[] {
    return this.list;
  }

  /** The ids in ascending order (live array; do not mutate). C# `EntityRegistry.Ids`. */
  get ids(): readonly number[] {
    return this.keys;
  }

  /** Number of entities. C# `EntityRegistry.Count`. */
  get count(): number {
    return this.list.length;
  }

  /** The entity at `index` in id order. C# indexer `registry[index]`. */
  at(index: number): T {
    return this.list[index]!;
  }

  /** Adds an entity (throws when the id exists); returns its position in id order. C# `EntityRegistry.Add`. */
  add(id: number, item: T): number {
    if (this.byId.has(id)) throw new Error(`Entity ${id} already exists.`);
    const i = this.lowerBound(id);
    this.keys.splice(i, 0, id);
    this.list.splice(i, 0, item);
    this.byId.set(id, item);
    return i;
  }

  /** Removes the entity with `id`; false when there was none. C# `EntityRegistry.Remove`. */
  remove(id: number): boolean {
    if (!this.byId.delete(id)) return false;
    const i = this.lowerBound(id);
    this.keys.splice(i, 1);
    this.list.splice(i, 1);
    return true;
  }

  /** True when an entity with `id` exists. C# `EntityRegistry.Contains`. */
  has(id: number): boolean {
    return this.byId.has(id);
  }

  /** The entity with `id`, or undefined. C# `EntityRegistry.Find` / `TryGet`. */
  get(id: number): T | undefined {
    return this.byId.get(id);
  }

  /** Position of `id` in id order, or -1. C# `EntityRegistry.IndexOf`. */
  indexOf(id: number): number {
    const i = this.lowerBound(id);
    return i < this.keys.length && this.keys[i] === id ? i : -1;
  }

  /** Removes every entity. C# `EntityRegistry.Clear`. */
  clear(): void {
    this.keys.length = 0;
    this.list.length = 0;
    this.byId.clear();
  }

  /** Iterates the entities in ascending id order. C# `EntityRegistry.GetEnumerator`. */
  [Symbol.iterator](): Iterator<T> {
    return this.list[Symbol.iterator]();
  }

  private lowerBound(id: number): number {
    let lo = 0;
    let hi = this.keys.length;
    while (lo < hi) {
      const mid = (lo + hi) >>> 1;
      if (this.keys[mid]! < id) lo = mid + 1;
      else hi = mid;
    }
    return lo;
  }
}
