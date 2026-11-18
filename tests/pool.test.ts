import sql from 'mssql';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { ResettingConnectionPool, type ResettableConnection } from '../src/db/pool';

type ResetCb = (err?: Error | null) => void;

function setup(resetBehaviour: (cb: ResetCb) => void) {
  const order: string[] = [];
  const conn: ResettableConnection = {
    reset: vi.fn((cb: ResetCb) => {
      order.push('reset');
      resetBehaviour(cb);
    }),
    close: vi.fn(() => void order.push('close')),
  };
  const baseRelease = vi.spyOn(sql.ConnectionPool.prototype as unknown as { release(c: unknown): void }, 'release').mockImplementation(() => void order.push('release'));
  const pool = new ResettingConnectionPool({ server: 'localhost' });
  return { pool, conn, order, baseRelease };
}

afterEach(() => vi.restoreAllMocks());

describe('ResettingConnectionPool.release', () => {
  it('resets the connection (sp_reset_connection) BEFORE handing it back to the pool', () => {
    const { pool, conn, order, baseRelease } = setup((cb) => cb(null));
    pool.release(conn);
    expect(order).toEqual(['reset', 'release']);
    expect(baseRelease).toHaveBeenCalledWith(conn);
  });

  it('waits for an asynchronous reset to finish', async () => {
    let finish: ResetCb = () => undefined;
    const { pool, conn, order } = setup((cb) => (finish = cb));
    pool.release(conn);
    expect(order).toEqual(['reset']); // not released yet
    finish(null);
    expect(order).toEqual(['reset', 'release']);
  });

  it('closes the connection if the reset fails, so the pool destroys it', () => {
    const { pool, conn, order } = setup((cb) => cb(new Error('reset failed')));
    pool.release(conn);
    expect(order).toEqual(['reset', 'close', 'release']);
  });

  it('closes the connection if reset throws synchronously', () => {
    const { pool, conn, order } = setup(() => {
      throw new Error('socket gone');
    });
    pool.release(conn);
    expect(order).toEqual(['reset', 'close', 'release']);
  });

  it('never returns an un-reset connection even when close() throws', () => {
    const { pool, conn, order } = setup((cb) => cb(new Error('x')));
    (conn.close as ReturnType<typeof vi.fn>).mockImplementation(() => {
      throw new Error('already closed');
    });
    expect(() => pool.release(conn)).not.toThrow();
    expect(order).toEqual(['reset', 'release']);
  });
});
