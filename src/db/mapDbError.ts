import { ConflictError, DbUnavailableError, ForbiddenError, ValidationError } from '../errors';
import { isTransient, sqlErrorNumber } from './transient';

export const RLS_BLOCK = 33504;
export const STALE_CONTEXT = 51000;

/**
 * Translates driver errors into application errors (overview 8, step 7).
 * Unknown errors are returned unchanged so the error handler answers with a generic 500.
 */
export function mapDbError(err: unknown): unknown {
  const n = sqlErrorNumber(err);
  switch (n) {
    case RLS_BLOCK:
      return new ForbiddenError("You don't have access to this page.", { cause: err });
    case 547:
      return new ConflictError("This record is still linked to other records, or links to a record that doesn't exist.", { cause: err });
    case 2627:
    case 2601:
      return new ValidationError('A record with the same unique value already exists.', { cause: err });
    default:
      break;
  }
  if (isTransient(err)) return new DbUnavailableError({ cause: err });
  return err;
}
