"use strict";
var __importDefault = (this && this.__importDefault) || function (mod) {
    return (mod && mod.__esModule) ? mod : { "default": mod };
};
Object.defineProperty(exports, "__esModule", { value: true });
exports.ResettingConnectionPool = void 0;
exports.getRlsPool = getRlsPool;
exports.getSystemPool = getSystemPool;
exports.closePools = closePools;
const mssql_1 = __importDefault(require("mssql"));
const config_1 = require("../config");
const logger_1 = require("../logger");
const sqlConfig_1 = require("./sqlConfig");
/**
 * mssql 12.x does not reset a connection when it goes back to the pool, so read-only
 * SESSION_CONTEXT keys would leak into the next request. We reset explicitly (ADR-0002).
 * If the reset fails the connection is closed; tarn then fails validation and destroys it.
 */
const baseRelease = () => mssql_1.default.ConnectionPool.prototype.release;
class ResettingConnectionPool extends mssql_1.default.ConnectionPool {
    // mssql's typings omit release(), but the runtime class has it and tedious transactions call it.
    release(connection) {
        const conn = connection;
        const give = () => {
            baseRelease().call(this, connection);
        };
        try {
            conn.reset((err) => {
                if (err) {
                    logger_1.logger.error({ err: err.message }, 'connection reset failed; discarding connection');
                    safeClose(conn);
                }
                give();
            });
        }
        catch (err) {
            logger_1.logger.error({ err: err.message }, 'connection reset threw; discarding connection');
            safeClose(conn);
            give();
        }
        return this;
    }
}
exports.ResettingConnectionPool = ResettingConnectionPool;
function safeClose(conn) {
    try {
        conn.close();
    }
    catch {
        /* already closed */
    }
}
const slots = { rls: {}, system: {} };
function createPool(kind) {
    const db = (0, config_1.getConfig)().db;
    if (!db)
        throw new Error('Database is not configured (demo mode)');
    const max = kind === 'rls' ? db.poolMax : 4;
    const cfg = (0, sqlConfig_1.buildSqlConfig)(db, { max });
    const pool = kind === 'rls' ? new ResettingConnectionPool(cfg) : new mssql_1.default.ConnectionPool(cfg);
    pool.on('error', (err) => logger_1.logger.error({ pool: kind, err: err.message }, 'pool error'));
    return pool;
}
/** Returns a connected pool. The connect attempt is shared; a failed attempt is discarded so the next call retries. */
async function connected(kind) {
    const slot = slots[kind];
    if (slot.pool?.connected)
        return slot.pool;
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
function getRlsPool() {
    return connected('rls');
}
/** For statements that must run without RLS context: sessions, login upsert, public reads. Used via src/db/system.ts. */
function getSystemPool() {
    return connected('system');
}
async function closePools() {
    await Promise.all(Object.keys(slots).map(async (k) => {
        const p = slots[k].pool;
        slots[k] = {};
        if (p)
            await p.close().catch(() => undefined);
    }));
}
//# sourceMappingURL=pool.js.map