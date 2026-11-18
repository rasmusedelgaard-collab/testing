"use strict";
Object.defineProperty(exports, "__esModule", { value: true });
exports.sqlErrorNumber = sqlErrorNumber;
exports.errorCode = errorCode;
exports.isTransient = isTransient;
/** Error numbers that mean "the database is (re)starting or briefly unavailable" (overview 8.1). */
const TRANSIENT_NUMBERS = new Set([40613, 40197, 40501, 40540, 49918, 49919, 49920, 4221, 10928, 10929]);
const TRANSIENT_CODES = new Set(['ETIMEOUT', 'ESOCKET', 'ECONNRESET', 'ECONNCLOSED']);
/** Extracts the SQL Server error number from mssql/tedious error shapes, or undefined. */
function sqlErrorNumber(err) {
    const e = err;
    if (!e || typeof e !== 'object')
        return undefined;
    const candidates = [e.number, e.originalError?.number, e.originalError?.info?.number];
    for (const c of candidates)
        if (typeof c === 'number')
            return c;
    if (e.cause && e.cause !== err)
        return sqlErrorNumber(e.cause);
    return undefined;
}
function errorCode(err) {
    const e = err;
    if (!e || typeof e !== 'object')
        return undefined;
    if (typeof e.code === 'string')
        return e.code;
    if (typeof e.originalError?.code === 'string')
        return e.originalError.code;
    return undefined;
}
function isTransient(err) {
    const n = sqlErrorNumber(err);
    if (n !== undefined)
        return TRANSIENT_NUMBERS.has(n);
    const code = errorCode(err);
    if (code !== undefined && TRANSIENT_CODES.has(code))
        return true;
    const e = err;
    const preceding = e?.precedingErrors;
    if (Array.isArray(preceding))
        return preceding.some((p) => isTransient(p));
    return false;
}
//# sourceMappingURL=transient.js.map