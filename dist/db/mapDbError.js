"use strict";
Object.defineProperty(exports, "__esModule", { value: true });
exports.STALE_CONTEXT = exports.RLS_BLOCK = void 0;
exports.mapDbError = mapDbError;
const errors_1 = require("../errors");
const transient_1 = require("./transient");
exports.RLS_BLOCK = 33504;
exports.STALE_CONTEXT = 51000;
/**
 * Translates driver errors into application errors (overview 8, step 7).
 * Unknown errors are returned unchanged so the error handler answers with a generic 500.
 */
function mapDbError(err) {
    const n = (0, transient_1.sqlErrorNumber)(err);
    switch (n) {
        case exports.RLS_BLOCK:
            return new errors_1.ForbiddenError("You don't have access to this page.", { cause: err });
        case 547:
            return new errors_1.ConflictError("This record is still linked to other records, or links to a record that doesn't exist.", { cause: err });
        case 2627:
        case 2601:
            return new errors_1.ValidationError('A record with the same unique value already exists.', { cause: err });
        default:
            break;
    }
    if ((0, transient_1.isTransient)(err))
        return new errors_1.DbUnavailableError({ cause: err });
    return err;
}
//# sourceMappingURL=mapDbError.js.map