"use strict";
Object.defineProperty(exports, "__esModule", { value: true });
exports.rateLimit = rateLimit;
exports.cachedProbe = cachedProbe;
/** Small fixed-window per-client request limiter (in memory, per instance). Not a substitute for a WAF. */
function rateLimit(opts) {
    const now = opts.now ?? Date.now;
    const hits = new Map();
    return (req, res, next) => {
        const key = req.ip ?? req.socket.remoteAddress ?? 'anonymous';
        const t = now();
        const e = hits.get(key);
        if (!e || t - e.start >= opts.windowMs) {
            if (hits.size > 10_000)
                for (const [k, v] of hits)
                    if (t - v.start >= opts.windowMs)
                        hits.delete(k);
            hits.set(key, { count: 1, start: t });
            return next();
        }
        e.count += 1;
        if (e.count > opts.max) {
            res.set('Retry-After', String(Math.ceil((opts.windowMs - (t - e.start)) / 1000)));
            return res.status(429).type('text/plain').send('Too many requests');
        }
        next();
    };
}
/** Caches an async boolean for `ttlMs` so a probe endpoint cannot be used to hammer the database. */
function cachedProbe(fn, ttlMs, now = Date.now) {
    let last;
    let inflight;
    return async () => {
        if (last && now() - last.at < ttlMs)
            return last.ok;
        inflight ??= fn().then((ok) => { last = { at: now(), ok }; return ok; }).finally(() => { inflight = undefined; });
        return inflight;
    };
}
//# sourceMappingURL=rateLimit.js.map