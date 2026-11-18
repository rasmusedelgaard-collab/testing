import { logger } from '../logger';
import { isTransient, sqlErrorNumber } from './transient';

export interface RetryOptions {
  /** Total time we are willing to spend waiting between attempts. */
  budgetMs?: number;
  sleep?: (ms: number) => Promise<void>;
  random?: () => number;
  now?: () => number;
  label?: string;
}

const STEPS_MS = [1000, 2000, 4000, 8000];
const LATER_STEP_MS = 15000;

export function backoffDelay(attempt: number, random: () => number): number {
  const base = STEPS_MS[attempt] ?? LATER_STEP_MS;
  return Math.round(base * (0.75 + random() * 0.5)); // +-25% jitter
}

const defaultSleep = (ms: number) => new Promise<void>((resolve) => setTimeout(resolve, ms));

/**
 * Retries `fn` while it fails with a transient error (paused serverless database, throttling),
 * until the wait budget is spent. ONLY wrap work that cannot have committed anything: connection
 * acquisition, the session-context batch, and idempotent system statements (overview 8.1).
 */
export async function withDbRetry<T>(fn: () => Promise<T>, opts: RetryOptions = {}): Promise<T> {
  const { budgetMs = 60000, sleep = defaultSleep, random = Math.random, now = Date.now, label = 'db' } = opts;
  const started = now();
  let waited = 0;
  for (let attempt = 0; ; attempt++) {
    try {
      return await fn();
    } catch (err) {
      if (!isTransient(err)) throw err;
      const delay = backoffDelay(attempt, random);
      if (waited + delay > budgetMs) {
        logger.error({ label, attempt: attempt + 1, elapsedMs: now() - started, errorNumber: sqlErrorNumber(err) }, 'database still unavailable; retry budget exhausted');
        throw err;
      }
      logger.warn({ label, attempt: attempt + 1, elapsedMs: now() - started, errorNumber: sqlErrorNumber(err) }, 'transient database error; retrying');
      await sleep(delay);
      waited += delay;
    }
  }
}
