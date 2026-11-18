import type { RequestHandler } from 'express';

/** Small fixed-window per-client request limiter (in memory, per instance). Not a substitute for a WAF. */
export function rateLimit(opts: { max: number; windowMs: number; now?: () => number }): RequestHandler {
  const now = opts.now ?? Date.now;
  const hits = new Map<string, { count: number; start: number }>();
  return (req, res, next) => {
    const key = req.ip ?? req.socket.remoteAddress ?? 'anonymous';
    const t = now();
    const e = hits.get(key);
    if (!e || t - e.start >= opts.windowMs) {
      if (hits.size > 10_000) for (const [k, v] of hits) if (t - v.start >= opts.windowMs) hits.delete(k);
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
export function cachedProbe(fn: () => Promise<boolean>, ttlMs: number, now: () => number = Date.now): () => Promise<boolean> {
  let last: { at: number; ok: boolean } | undefined;
  let inflight: Promise<boolean> | undefined;
  return async () => {
    if (last && now() - last.at < ttlMs) return last.ok;
    inflight ??= fn().then((ok) => { last = { at: now(), ok }; return ok; }).finally(() => { inflight = undefined; });
    return inflight;
  };
}
