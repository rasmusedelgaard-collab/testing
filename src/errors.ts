/** Application errors with an HTTP meaning; the error handler renders them. */
export class AppError extends Error {
  constructor(
    public readonly status: number,
    message: string,
    options?: { cause?: unknown },
  ) {
    super(message, options);
    this.name = new.target.name;
  }
}

export class ForbiddenError extends AppError {
  constructor(message = 'Forbidden', options?: { cause?: unknown }) {
    super(403, message, options);
  }
}

export class NotFoundError extends AppError {
  constructor(message = 'Not found') {
    super(404, message);
  }
}

/** FK violation (547) or optimistic-concurrency miss. */
export class ConflictError extends AppError {
  constructor(message: string, options?: { cause?: unknown }) {
    super(409, message, options);
  }
}

/** Unique-constraint violation (2627/2601) surfaced to the form as a field-level validation error. */
export class ValidationError extends AppError {
  constructor(message: string, options?: { cause?: unknown }) {
    super(422, message, options);
  }
}

/** Database paused/unavailable (transient error after retries or after the work started). */
export class DbUnavailableError extends AppError {
  constructor(options?: { cause?: unknown }) {
    super(503, 'Database unavailable', options);
  }
}

/** Optimistic-concurrency miss: the row changed since the form was loaded. `recordPath` links to the current version. */
export class EditConflictError extends ConflictError {
  constructor(public readonly recordPath: string) {
    super('Someone else changed this record while you were editing.');
  }
}
