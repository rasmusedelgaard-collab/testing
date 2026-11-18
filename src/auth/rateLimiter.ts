/** In-memory fixed-window limiter for failed access-code attempts. Per instance; fine for the single-host test site. */
export class AttemptLimiter {
  private readonly entries = new Map<string, { count: number; windowStart: number }>();

  constructor(
    private readonly maxAttempts = 5,
    private readonly windowMs = 15 * 60 * 1000,
    private readonly now: () => number = Date.now,
  ) {}

  private current(key: string) {
    const e = this.entries.get(key);
    if (e && this.now() - e.windowStart >= this.windowMs) {
      this.entries.delete(key);
      return undefined;
    }
    return e;
  }

  isLocked(key: string): boolean {
    return (this.current(key)?.count ?? 0) >= this.maxAttempts;
  }

  recordFailure(key: string): void {
    const e = this.current(key);
    if (e) e.count += 1;
    else this.entries.set(key, { count: 1, windowStart: this.now() });
    if (this.entries.size > 10_000) this.prune();
  }

  reset(key: string): void {
    this.entries.delete(key);
  }

  private prune(): void {
    for (const k of this.entries.keys()) this.current(k);
  }
}
