"use strict";
Object.defineProperty(exports, "__esModule", { value: true });
exports.closeDatabase = void 0;
exports.systemQuery = systemQuery;
const retry_1 = require("./retry");
const pool_1 = require("./pool");
/**
 * Runs a statement on systemPool (no RLS context ever set). Only for idempotent work: session store,
 * login upsert, public reads. Retries transient errors, so the whole statement must be safe to repeat.
 */
async function systemQuery(text, params = [], opts = {}) {
    const run = async () => {
        const pool = await (0, pool_1.getSystemPool)();
        const request = pool.request();
        for (const p of params)
            request.input(p.name, p.type, p.value);
        const result = await request.query(text);
        return result.recordset;
    };
    const rows = opts.retry === false ? await run() : await (0, retry_1.withDbRetry)(run, { label: 'system' });
    return rows ?? [];
}
/** Closes both pools (graceful shutdown). */
exports.closeDatabase = pool_1.closePools;
//# sourceMappingURL=system.js.map