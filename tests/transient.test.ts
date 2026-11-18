import { describe, expect, it, vi } from 'vitest';
import { backoffDelay, withDbRetry } from '../src/db/retry';
import { isTransient, sqlErrorNumber } from '../src/db/transient';

const sqlErr = (number: number) => Object.assign(new Error('x'), { number });

describe('isTransient', () => {
  it.each([40613, 40197, 40501, 40540, 49918, 49919, 49920, 4221, 10928, 10929])('treats %i as transient', (n) => {
    expect(isTransient(sqlErr(n))).toBe(true);
  });
  it.each(['ETIMEOUT', 'ESOCKET', 'ECONNRESET', 'ECONNCLOSED'])('treats code %s as transient', (code) => {
    expect(isTransient(Object.assign(new Error('x'), { code }))).toBe(true);
  });
  it.each([33504, 547, 51000, 18456, 2627])('never retries %i', (n) => {
    expect(isTransient(sqlErr(n))).toBe(false);
  });
  it('reads numbers from wrapped errors', () => {
    expect(sqlErrorNumber({ originalError: { info: { number: 40613 } } })).toBe(40613);
    expect(isTransient({ originalError: { number: 40613 } })).toBe(true);
    expect(isTransient(new Error('plain'))).toBe(false);
    expect(isTransient(null)).toBe(false);
  });
});

describe('withDbRetry', () => {
  it('retries transient errors with backoff and then succeeds', async () => {
    const sleeps: number[] = [];
    let calls = 0;
    const result = await withDbRetry(
      async () => {
        calls++;
        if (calls < 3) throw sqlErr(40613);
        return 'ok';
      },
      { sleep: async (ms) => void sleeps.push(ms), random: () => 0.5 },
    );
    expect(result).toBe('ok');
    expect(sleeps).toEqual([1000, 2000]);
  });
  it('does not retry non-transient errors', async () => {
    const fn = vi.fn(async () => {
      throw sqlErr(33504);
    });
    await expect(withDbRetry(fn, { sleep: async () => undefined })).rejects.toMatchObject({ number: 33504 });
    expect(fn).toHaveBeenCalledTimes(1);
  });
  it('gives up when the wait budget is spent', async () => {
    const fn = vi.fn(async () => {
      throw sqlErr(40613);
    });
    let total = 0;
    await expect(withDbRetry(fn, { budgetMs: 60000, random: () => 0.5, sleep: async (ms) => void (total += ms) })).rejects.toMatchObject({ number: 40613 });
    expect(total).toBeLessThanOrEqual(60000);
    expect(total).toBeGreaterThan(30000);
  });
  it('backoff steps grow 1s,2s,4s,8s then 15s', () => {
    expect([0, 1, 2, 3, 4, 9].map((a) => backoffDelay(a, () => 0.5))).toEqual([1000, 2000, 4000, 8000, 15000, 15000]);
  });
});
