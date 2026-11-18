"use strict";
var __importDefault = (this && this.__importDefault) || function (mod) {
    return (mod && mod.__esModule) ? mod : { "default": mod };
};
Object.defineProperty(exports, "__esModule", { value: true });
exports.SqlSessionStore = void 0;
const express_session_1 = __importDefault(require("express-session"));
const logger_1 = require("../logger");
const CLEANUP_INTERVAL_MS = 15 * 60 * 1000;
const CLEANUP_BATCH = 1000;
/**
 * express-session store over app.Sessions (ADR-0004). Cleanup is opportunistic (after a served
 * request, at most every 15 minutes per instance) and never timer-driven, so a serverless database can auto-pause.
 */
class SqlSessionStore extends express_session_1.default.Store {
    repo;
    idleMinutes;
    now;
    lastCleanup;
    cleaning = false;
    constructor(repo, idleMinutes, now = Date.now) {
        super();
        this.repo = repo;
        this.idleMinutes = idleMinutes;
        this.now = now;
        this.lastCleanup = this.now();
    }
    expiry(sess) {
        const fromCookie = sess.cookie?.expires ? new Date(sess.cookie.expires) : null;
        return fromCookie ?? new Date(this.now() + this.idleMinutes * 60_000);
    }
    get(sid, cb) {
        this.repo.get(sid).then((data) => {
            if (data === null)
                return cb(null, null);
            try {
                cb(null, JSON.parse(data));
            }
            catch {
                cb(null, null); // corrupt row: treat as no session
            }
        }, (err) => cb(err));
    }
    set(sid, sess, cb) {
        this.repo.set(sid, JSON.stringify(sess), this.expiry(sess)).then(() => cb?.(), (err) => cb?.(err));
    }
    touch(sid, sess, cb) {
        this.repo.touch(sid, this.expiry(sess)).then(() => cb?.(), (err) => {
            logger_1.logger.warn({ err: err.message }, 'session touch failed');
            cb?.();
        });
    }
    destroy(sid, cb) {
        this.repo.destroy(sid).then(() => cb?.(), (err) => cb?.(err));
    }
    /** Call after a response has been sent. Fire-and-forget; errors are logged. */
    maybeCleanup() {
        const t = this.now();
        if (this.cleaning || t - this.lastCleanup < CLEANUP_INTERVAL_MS)
            return;
        this.cleaning = true;
        this.lastCleanup = t;
        void (async () => {
            try {
                while ((await this.repo.deleteExpired(CLEANUP_BATCH)) >= CLEANUP_BATCH) {
                    /* keep deleting full batches */
                }
            }
            catch (err) {
                logger_1.logger.warn({ err: err.message }, 'session cleanup failed');
            }
            finally {
                this.cleaning = false;
            }
        })();
    }
}
exports.SqlSessionStore = SqlSessionStore;
//# sourceMappingURL=sessionStore.js.map