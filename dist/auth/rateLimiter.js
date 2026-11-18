"use strict";
Object.defineProperty(exports, "__esModule", { value: true });
exports.AttemptLimiter = void 0;
/** In-memory fixed-window limiter for failed access-code attempts. Per instance; fine for the single-host test site. */
class AttemptLimiter {
    maxAttempts;
    windowMs;
    now;
    entries = new Map();
    constructor(maxAttempts = 5, windowMs = 15 * 60 * 1000, now = Date.now) {
        this.maxAttempts = maxAttempts;
        this.windowMs = windowMs;
        this.now = now;
    }
    current(key) {
        const e = this.entries.get(key);
        if (e && this.now() - e.windowStart >= this.windowMs) {
            this.entries.delete(key);
            return undefined;
        }
        return e;
    }
    isLocked(key) {
        return (this.current(key)?.count ?? 0) >= this.maxAttempts;
    }
    recordFailure(key) {
        const e = this.current(key);
        if (e)
            e.count += 1;
        else
            this.entries.set(key, { count: 1, windowStart: this.now() });
        if (this.entries.size > 10_000)
            this.prune();
    }
    reset(key) {
        this.entries.delete(key);
    }
    prune() {
        for (const k of this.entries.keys())
            this.current(k);
    }
}
exports.AttemptLimiter = AttemptLimiter;
//# sourceMappingURL=rateLimiter.js.map