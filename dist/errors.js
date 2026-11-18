"use strict";
Object.defineProperty(exports, "__esModule", { value: true });
exports.EditConflictError = exports.DbUnavailableError = exports.ValidationError = exports.ConflictError = exports.NotFoundError = exports.ForbiddenError = exports.AppError = void 0;
/** Application errors with an HTTP meaning; the error handler renders them. */
class AppError extends Error {
    status;
    constructor(status, message, options) {
        super(message, options);
        this.status = status;
        this.name = new.target.name;
    }
}
exports.AppError = AppError;
class ForbiddenError extends AppError {
    constructor(message = 'Forbidden', options) {
        super(403, message, options);
    }
}
exports.ForbiddenError = ForbiddenError;
class NotFoundError extends AppError {
    constructor(message = 'Not found') {
        super(404, message);
    }
}
exports.NotFoundError = NotFoundError;
/** FK violation (547) or optimistic-concurrency miss. */
class ConflictError extends AppError {
    constructor(message, options) {
        super(409, message, options);
    }
}
exports.ConflictError = ConflictError;
/** Unique-constraint violation (2627/2601) surfaced to the form as a field-level validation error. */
class ValidationError extends AppError {
    constructor(message, options) {
        super(422, message, options);
    }
}
exports.ValidationError = ValidationError;
/** Database paused/unavailable (transient error after retries or after the work started). */
class DbUnavailableError extends AppError {
    constructor(options) {
        super(503, 'Database unavailable', options);
    }
}
exports.DbUnavailableError = DbUnavailableError;
/** Optimistic-concurrency miss: the row changed since the form was loaded. `recordPath` links to the current version. */
class EditConflictError extends ConflictError {
    recordPath;
    constructor(recordPath) {
        super('Someone else changed this record while you were editing.');
        this.recordPath = recordPath;
    }
}
exports.EditConflictError = EditConflictError;
//# sourceMappingURL=errors.js.map