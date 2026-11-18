import session from 'express-session';
import { logger } from '../logger';
import type { SessionRepo } from '../db/sessionRepo';

const CLEANUP_INTERVAL_MS = 15 * 60 * 1000;
const CLEANUP_BATCH = 1000;

/**
 * express-session store over app.Sessions (ADR-0004). Cleanup is opportunistic (after a served
 * request, at most every 15 minutes per instance) and never timer-driven, so a serverless database can auto-pause.
 */
export class SqlSessionStore extends session.Store {
  private lastCleanup: number;
  private cleaning = false;

  constructor(
    private readonly repo: SessionRepo,
    private readonly idleMinutes: number,
    private readonly now: () => number = Date.now,
  ) {
    super();
    this.lastCleanup = this.now();
  }

  private expiry(sess: session.SessionData): Date {
    const fromCookie = sess.cookie?.expires ? new Date(sess.cookie.expires) : null;
    return fromCookie ?? new Date(this.now() + this.idleMinutes * 60_000);
  }

  override get(sid: string, cb: (err?: unknown, session?: session.SessionData | null) => void): void {
    this.repo.get(sid).then(
      (data) => {
        if (data === null) return cb(null, null);
        try {
          cb(null, JSON.parse(data) as session.SessionData);
        } catch {
          cb(null, null); // corrupt row: treat as no session
        }
      },
      (err) => cb(err),
    );
  }

  override set(sid: string, sess: session.SessionData, cb?: (err?: unknown) => void): void {
    this.repo.set(sid, JSON.stringify(sess), this.expiry(sess)).then(() => cb?.(), (err) => cb?.(err));
  }

  override touch(sid: string, sess: session.SessionData, cb?: () => void): void {
    this.repo.touch(sid, this.expiry(sess)).then(() => cb?.(), (err) => {
      logger.warn({ err: (err as Error).message }, 'session touch failed');
      cb?.();
    });
  }

  override destroy(sid: string, cb?: (err?: unknown) => void): void {
    this.repo.destroy(sid).then(() => cb?.(), (err) => cb?.(err));
  }

  /** Call after a response has been sent. Fire-and-forget; errors are logged. */
  maybeCleanup(): void {
    const t = this.now();
    if (this.cleaning || t - this.lastCleanup < CLEANUP_INTERVAL_MS) return;
    this.cleaning = true;
    this.lastCleanup = t;
    void (async () => {
      try {
        while ((await this.repo.deleteExpired(CLEANUP_BATCH)) >= CLEANUP_BATCH) {
          /* keep deleting full batches */
        }
      } catch (err) {
        logger.warn({ err: (err as Error).message }, 'session cleanup failed');
      } finally {
        this.cleaning = false;
      }
    })();
  }
}
