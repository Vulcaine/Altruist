/**
 * Flow layer. Entities in ascending id order with O(1) lookup — mirror of C# `EntityRegistry<T>`
 * (number ids). `items` is the id-ordered array to iterate (do not mutate it directly).
 */
export class EntityRegistry<T> {
  private readonly keys: number[] = [];
  private readonly list: T[] = [];
  private readonly byId = new Map<number, T>();

  /** The entities in ascending id order. */
  get items(): readonly T[] {
    return this.list;
  }

  /** The ids in ascending order. */
  get ids(): readonly number[] {
    return this.keys;
  }

  get count(): number {
    return this.list.length;
  }

  at(index: number): T {
    return this.list[index]!;
  }

  /** Adds an entity (throws when the id exists); returns its position. */
  add(id: number, item: T): number {
    if (this.byId.has(id)) throw new Error(`Entity ${id} already exists.`);
    const i = this.lowerBound(id);
    this.keys.splice(i, 0, id);
    this.list.splice(i, 0, item);
    this.byId.set(id, item);
    return i;
  }

  remove(id: number): boolean {
    if (!this.byId.delete(id)) return false;
    const i = this.lowerBound(id);
    this.keys.splice(i, 1);
    this.list.splice(i, 1);
    return true;
  }

  has(id: number): boolean {
    return this.byId.has(id);
  }

  get(id: number): T | undefined {
    return this.byId.get(id);
  }

  /** Position of `id` in id order, or -1. */
  indexOf(id: number): number {
    const i = this.lowerBound(id);
    return i < this.keys.length && this.keys[i] === id ? i : -1;
  }

  clear(): void {
    this.keys.length = 0;
    this.list.length = 0;
    this.byId.clear();
  }

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
