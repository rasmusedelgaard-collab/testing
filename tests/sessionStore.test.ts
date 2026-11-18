import type { SessionData } from 'express-session';
import { describe, expect, it, vi } from 'vitest';
import { SqlSessionStore } from '../src/auth/sessionStore';
import type { SessionRepo } from '../src/db/sessionRepo';

const makeRepo = (over: Partial<SessionRepo> = {}): SessionRepo => ({
  get: vi.fn(async () => null),
  set: vi.fn(async () => undefined),
  touch: vi.fn(async () => undefined),
  destroy: vi.fn(async () => undefined),
  deleteExpired: vi.fn(async () => 0),
  ...over,
});
const call = <T>(fn: (cb: (err?: unknown, v?: T) => void) => void) => new Promise<{ err?: unknown; v?: T }>((resolve) => fn((err, v) => resolve({ err, v })));

describe('SqlSessionStore', () => {
  it('get parses stored JSON, and returns null for missing or corrupt rows', async () => {
    const repo = makeRepo({ get: vi.fn(async () => '{"csrfToken":"abc"}') });
    const store = new SqlSessionStore(repo, 60);
    expect((await call<SessionData | null>((cb) => store.get('sid', cb))).v).toMatchObject({ csrfToken: 'abc' });
    const none = new SqlSessionStore(makeRepo(), 60);
    expect((await call((cb) => none.get('sid', cb))).v).toBeNull();
    const bad = new SqlSessionStore(makeRepo({ get: vi.fn(async () => '{nope') }), 60);
    expect((await call((cb) => bad.get('sid', cb))).v).toBeNull();
  });

  it('set stores JSON with the cookie expiry, falling back to the idle timeout', async () => {
    const repo = makeRepo();
    const now = Date.parse('2026-01-01T00:00:00Z');
    const store = new SqlSessionStore(repo, 30, () => now);
    const expires = new Date(now + 5 * 60_000);
    await call((cb) => store.set('s1', { cookie: { expires } } as unknown as SessionData, cb));
    expect(repo.set).toHaveBeenCalledWith('s1', expect.stringContaining('cookie'), expires);
    await call((cb) => store.set('s2', { cookie: {} } as unknown as SessionData, cb));
    expect((repo.set as ReturnType<typeof vi.fn>).mock.calls[1]![2]).toEqual(new Date(now + 30 * 60_000));
  });

  it('propagates repository errors to the callback', async () => {
    const store = new SqlSessionStore(makeRepo({ get: vi.fn(async () => { throw new Error('db down'); }) }), 60);
    expect((await call((cb) => store.get('x', cb))).err).toBeInstanceOf(Error);
  });

  it('destroy and touch delegate to the repository', async () => {
    const repo = makeRepo();
    const store = new SqlSessionStore(repo, 60);
    await call((cb) => store.destroy('s', cb));
    expect(repo.destroy).toHaveBeenCalledWith('s');
    await new Promise<void>((resolve) => store.touch('s', { cookie: {} } as unknown as SessionData, resolve));
    expect(repo.touch).toHaveBeenCalled();
  });

  describe('lazy cleanup', () => {
    it('does nothing within 15 minutes of creation, then runs once, then waits another 15 minutes', async () => {
      let now = 0;
      const repo = makeRepo();
      const store = new SqlSessionStore(repo, 60, () => now);
      store.maybeCleanup();
      now = 14 * 60_000;
      store.maybeCleanup();
      expect(repo.deleteExpired).not.toHaveBeenCalled();
      now = 15 * 60_000;
      store.maybeCleanup();
      await vi.waitFor(() => expect(repo.deleteExpired).toHaveBeenCalledTimes(1));
      now = 20 * 60_000;
      store.maybeCleanup();
      await Promise.resolve();
      expect(repo.deleteExpired).toHaveBeenCalledTimes(1);
      now = 31 * 60_000;
      store.maybeCleanup();
      await vi.waitFor(() => expect(repo.deleteExpired).toHaveBeenCalledTimes(2));
    });

    it('repeats while full batches are deleted and never throws', async () => {
      let now = 0;
      const results = [1000, 1000, 5];
      const repo = makeRepo({ deleteExpired: vi.fn(async () => results.shift() ?? 0) });
      const store = new SqlSessionStore(repo, 60, () => now);
      now = 16 * 60_000;
      store.maybeCleanup();
      await vi.waitFor(() => expect(repo.deleteExpired).toHaveBeenCalledTimes(3));
      const failing = makeRepo({ deleteExpired: vi.fn(async () => { throw new Error('db paused'); }) });
      const store2 = new SqlSessionStore(failing, 60, () => now);
      now += 16 * 60_000;
      expect(() => store2.maybeCleanup()).not.toThrow();
    });

    it('has no timers: constructing a store schedules nothing', () => {
      vi.useFakeTimers();
      new SqlSessionStore(makeRepo(), 60);
      expect(vi.getTimerCount()).toBe(0);
      vi.useRealTimers();
    });
  });
});
