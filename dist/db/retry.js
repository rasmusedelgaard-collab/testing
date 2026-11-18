"use strict";
Object.defineProperty(exports, "__esModule", { value: true });
exports.backoffDelay = backoffDelay;
exports.withDbRetry = withDbRetry;
const logger_1 = require("../logger");
const transient_1 = require("./transient");
const STEPS_MS = [1000, 2000, 4000, 8000];
const LATER_STEP_MS = 15000;
function backoffDelay(attempt, random) {
    const base = STEPS_MS[attempt] ?? LATER_STEP_MS;
    return Math.round(base * (0.75 + random() * 0.5)); // +-25% jitter
}
const defaultSleep = (ms) => new Promise((resolve) => setTimeout(resolve, ms));
/**
 * Retries `fn` while it fails with a transient error (paused serverless database, throttling),
 * until the wait budget is spent. ONLY wrap work that cannot have committed anything: connection
 * acquisition, the session-context batch, and idempotent system statements (overview 8.1).
 */
async function withDbRetry(fn, opts = {}) {
    const { budgetMs = 60000, sleep = defaultSleep, random = Math.random, now = Date.now, label = 'db' } = opts;
    const started = now();
    let waited = 0;
    for (let attempt = 0;; attempt++) {
        try {
            return await fn();
        }
        catch (err) {
            if (!(0, transient_1.isTransient)(err))
                throw err;
            const delay = backoffDelay(attempt, random);
            if (waited + delay > budgetMs) {
                logger_1.logger.error({ label, attempt: attempt + 1, elapsedMs: now() - started, errorNumber: (0, transient_1.sqlErrorNumber)(err) }, 'database still unavailable; retry budget exhausted');
                throw err;
            }
            logger_1.logger.warn({ label, attempt: attempt + 1, elapsedMs: now() - started, errorNumber: (0, transient_1.sqlErrorNumber)(err) }, 'transient database error; retrying');
            await sleep(delay);
            waited += delay;
        }
    }
}
//# sourceMappingURL=retry.js.map