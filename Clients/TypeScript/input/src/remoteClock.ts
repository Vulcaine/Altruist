/**
 * Maps a remote device's timestamps (e.g. a phone used as a controller, `performance.now()` there)
 * onto the local clock, keeping their spacing: a swipe that took 40 ms on the phone takes 40 ms
 * here, whatever the network jitter. The offset is the smallest `arrival - remote` of the last
 * `window` messages (the least delayed one), so a mapped time is never after its arrival, and mapped
 * times never go back.
 */
export class RemoteClock {
  private readonly offsets: number[] = [];
  private last = -Infinity;
  private readonly window: number;

  constructor(window = 64) {
    this.window = window;
  }

  /** The local time of `remoteT`, given that the message arrived at `arrivalT` (local). */
  toLocal(remoteT: number, arrivalT: number): number {
    this.offsets.push(arrivalT - remoteT);
    if (this.offsets.length > this.window) this.offsets.shift();
    const offset = Math.min(...this.offsets);
    this.last = Math.max(this.last, Math.min(remoteT + offset, arrivalT));
    return this.last;
  }

  /** Forget the estimate (a new device, a reconnect). */
  reset(): void {
    this.offsets.length = 0;
    this.last = -Infinity;
  }
}
