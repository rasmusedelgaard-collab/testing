import sql from 'mssql';
import { getConfig } from '../config';
import { logger } from '../logger';
import { buildSqlConfig } from './sqlConfig';

/**
 * The minimal part of a tedious Connection we use. reset() runs sp_reset_connection (TDS
 * RESETCONNECTION), which clears SESSION_CONTEXT, SET options, temp tables and open transactions.
 */
export interface ResettableConnection {
  reset(callback: (err?: Error | null) => void): void;
  close(): void;
}

/**
 * mssql 12.x does not reset a connection when it goes back to the pool, so read-only
 * SESSION_CONTEXT keys would leak into the next request. We reset explicitly (ADR-0002).
 * If the reset fails the connection is closed; tarn then fails validation and destroys it.
 */
export class ResettingConnectionPool extends sql.ConnectionPool {
  override release(connection: unknown): this {
    const conn = connection as ResettableConnection;
    const give = () => {
      super.release(connection as never);
    };
    try {
      conn.reset((err) => {
        if (err) {
          logger.error({ err: err.message }, 'connection reset failed; discarding connection');
          safeClose(conn);
        }
        give();
      });
    } catch (err) {
      logger.error({ err: (err as Error).message }, 'connection reset threw; discarding connection');
      safeClose(conn);
      give();
    }
    return this;
  }
}

function safeClose(conn: ResettableConnection): void {
  try {
    conn.close();
  } catch {
    /* already closed */
  }
}

type Kind = 'rls' | 'system';
interface Slot {
  pool?: sql.ConnectionPool;
  connecting?: Promise<sql.ConnectionPool>;
}
const slots: Record<Kind, Slot> = { rls: {}, system: {} };

function createPool(kind: Kind): sql.ConnectionPool {
  const db = getConfig().db;
  if (!db) throw new Error('Database is not configured (demo mode)');
  const max = kind === 'rls' ? db.poolMax : 4;
  const cfg = buildSqlConfig(db, { max });
  const pool = kind === 'rls' ? new ResettingConnectionPool(cfg) : new sql.ConnectionPool(cfg);
  pool.on('error', (err: Error) => logger.error({ pool: kind, err: err.message }, 'pool error'));
  return pool;
}

/** Returns a connected pool. The connect attempt is shared; a failed attempt is discarded so the next call retries. */
async function connected(kind: Kind): Promise<sql.ConnectionPool> {
  const slot = slots[kind];
  if (slot.pool?.connected) return slot.pool;
  if (!slot.connecting) {
    const pool = createPool(kind);
    slot.connecting = pool
      .connect()
      .then((p) => {
        slot.pool = p;
        return p;
      })
      .finally(() => {
        slot.connecting = undefined;
      });
  }
  return slot.connecting;
}

/** Used only by withRlsContext (enforced by lint + tests/importRestrictions.test.ts). */
export function getRlsPool(): Promise<sql.ConnectionPool> {
  return connected('rls');
}

/** For statements that must run without RLS context: sessions, login upsert, public reads. Used via src/db/system.ts. */
export function getSystemPool(): Promise<sql.ConnectionPool> {
  return connected('system');
}

export async function closePools(): Promise<void> {
  await Promise.all(
    (Object.keys(slots) as Kind[]).map(async (k) => {
      const p = slots[k].pool;
      slots[k] = {};
      if (p) await p.close().catch(() => undefined);
    }),
  );
}
