/**
 * Versioned JSON settings in a `Storage`-like key/value store (localStorage in browsers), safe
 * everywhere: every storage access is wrapped in try/catch, so private mode, a full quota, blocked
 * site data or a server-side render never throw — the store then keeps the value in memory for
 * this page load.
 *
 * Use it for small per-device preferences (dev tool settings, last playlist, volume). Never for
 * secrets (tokens belong in memory or HttpOnly cookies) or for state that must reach the server.
 */

/** The subset of the Web Storage API the store needs (localStorage, sessionStorage, a test double). */
export interface StorageLike {
  getItem(key: string): string | null;
  setItem(key: string, value: string): void;
  removeItem(key: string): void;
}

/** In-memory {@link StorageLike}: the fallback when no real storage is available, and for tests. */
export class MemoryStorage implements StorageLike {
  private readonly items = new Map<string, string>();
  /** The stored value, or null. */
  getItem(key: string): string | null {
    return this.items.get(key) ?? null;
  }
  /** Stores a value. */
  setItem(key: string, value: string): void {
    this.items.set(key, String(value));
  }
  /** Removes a value. */
  removeItem(key: string): void {
    this.items.delete(key);
  }
}

/**
 * `globalThis.localStorage` when it exists and can be touched, else null (Node, workers, storage
 * blocked by the browser: even reading the property can throw there).
 */
export function browserStorage(kind: 'local' | 'session' = 'local'): StorageLike | null {
  try {
    const s = (globalThis as { localStorage?: StorageLike; sessionStorage?: StorageLike })[kind === 'local' ? 'localStorage' : 'sessionStorage'];
    return s ?? null;
  } catch {
    return null;
  }
}

/** Options of {@link JsonStore}. */
export interface JsonStoreOptions<T> {
  /** Storage key (prefix it with your game, e.g. `mygame.audio`). */
  key: string;
  /** Current data version; stored as `{ v, data }`. Bump it when the shape changes. */
  version: number;
  /** Value when nothing (valid) is stored. Copied with `structuredClone` before it is returned. */
  defaults: T;
  /**
   * Turns stored data of an older version into the current shape (`fromVersion` < `version`).
   * Without it, data of another version is discarded (defaults). Return undefined to discard.
   */
  migrate?: (data: unknown, fromVersion: number) => T | undefined;
  /** Cleans up a loaded or saved value (clamp ranges, fill missing fields). Default: identity. */
  normalize?: (value: unknown) => T;
  /** Storage (default {@link browserStorage}; {@link MemoryStorage} when there is none). */
  storage?: StorageLike | null;
}

/**
 * See the module comment. `load()` reads (version-checked, migrated, normalized), `save()` writes,
 * `clear()` removes. Synchronous.
 *
 * @example
 * ```ts
 * const audio = new JsonStore({ key: 'mygame.audio', version: 2, defaults: { music: 0.8, sfx: 1 },
 *   migrate: (old, v) => (v === 1 ? { music: (old as { volume: number }).volume, sfx: 1 } : undefined) });
 * const settings = audio.load();
 * audio.save({ ...settings, music: 0.5 });
 * ```
 */
export class JsonStore<T> {
  private readonly storage: StorageLike;
  private readonly fallback = new MemoryStorage();
  private usingFallback: boolean;

  private readonly options: JsonStoreOptions<T>;

  /** Creates the store (nothing is read until {@link load}). */
  constructor(options: JsonStoreOptions<T>) {
    this.options = options;
    const s = options.storage === undefined ? browserStorage() : options.storage;
    this.storage = s ?? this.fallback;
    this.usingFallback = !s;
  }

  /** True when values only live in memory (no storage, or it failed). */
  get inMemory(): boolean {
    return this.usingFallback;
  }

  /** The stored value, or the defaults when nothing valid is stored. Never throws. */
  load(): T {
    const raw = this.read();
    if (raw === null) return this.defaults();
    try {
      const parsed = JSON.parse(raw) as unknown;
      if (!parsed || typeof parsed !== 'object' || !('v' in parsed)) return this.defaults();
      const { v, data } = parsed as { v: unknown; data: unknown };
      if (v === this.options.version) return this.normalize(data);
      if (typeof v === 'number' && v < this.options.version && this.options.migrate) {
        const migrated = this.options.migrate(data, v);
        return migrated === undefined ? this.defaults() : this.normalize(migrated);
      }
    } catch {
      /* corrupt JSON or a throwing migration: defaults */
    }
    return this.defaults();
  }

  /** Stores `value` (normalized). Returns the stored value. Never throws (falls back to memory). */
  save(value: T): T {
    const v = this.normalize(value);
    const text = JSON.stringify({ v: this.options.version, data: v });
    try {
      this.storage.setItem(this.options.key, text);
    } catch {
      this.usingFallback = true;
      this.fallback.setItem(this.options.key, text);
    }
    if (this.usingFallback && this.storage !== this.fallback) this.fallback.setItem(this.options.key, text);
    return v;
  }

  /** Removes the stored value. Never throws. */
  clear(): void {
    try {
      this.storage.removeItem(this.options.key);
    } catch {
      /* ignore */
    }
    this.fallback.removeItem(this.options.key);
  }

  private read(): string | null {
    if (this.usingFallback && this.storage !== this.fallback) {
      const mem = this.fallback.getItem(this.options.key);
      if (mem !== null) return mem;
    }
    try {
      return this.storage.getItem(this.options.key);
    } catch {
      this.usingFallback = true;
      return this.fallback.getItem(this.options.key);
    }
  }

  private normalize(value: unknown): T {
    return this.options.normalize ? this.options.normalize(value) : (value as T);
  }

  private defaults(): T {
    const d = structuredClone(this.options.defaults);
    return this.options.normalize ? this.options.normalize(d) : d;
  }
}
