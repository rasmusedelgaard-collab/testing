import type { ErrorRequestHandler, RequestHandler } from 'express';
import { AppError, DbUnavailableError, EditConflictError } from '../errors';
import { logger } from '../logger';

export const notFoundHandler: RequestHandler = (req, res) => {
  res.status(404).render('pages/error', { title: 'Not found', status: 404, reference: req.id, heading: "We can't find that page or record.", message: 'It may have been deleted, or you may not have access.' });
};

export const errorHandler: ErrorRequestHandler = (err: unknown, req, res, next) => {
  if (res.headersSent) return next(err);
  if (err instanceof DbUnavailableError) {
    res.set('Retry-After', '15');
    return res.status(503).render('pages/waking', { title: 'Waking up the database', status: 503, reference: req.id, isPost: req.method !== 'GET' });
  }
  const httpStatus = (err as { status?: unknown; type?: unknown } | null)?.status;
  if (httpStatus === 413 || (err as { type?: unknown } | null)?.type === 'entity.too.large') {
    return res.status(413).render('pages/error', { title: 'Too large', status: 413, reference: req.id, heading: 'This form is too large.', message: 'Shorten your text and try again. Nothing was saved.' });
  }
  if (err instanceof EditConflictError) {
    return res.status(409).render('pages/conflict', { title: 'Edit conflict', status: 409, reference: req.id, recordPath: err.recordPath });
  }
  if (err instanceof AppError) {
    if (err.status === 403) {
      return res.status(403).render('pages/error', { title: 'Access denied', status: 403, reference: req.id, heading: "You don't have access to this page.", message: 'If you think you should, ask your administrator.' });
    }
    if (err.status === 404) return notFoundHandler(req, res, next);
    return res.status(err.status).render('pages/error', { title: 'Problem', status: err.status, reference: req.id, heading: err.status === 409 ? "That can't be done right now." : 'Check your input.', message: err.message });
  }
  logger.error({ reqId: req.id, err: err instanceof Error ? { name: err.name, message: err.message, stack: err.stack } : String(err) }, 'unhandled error');
  res.status(500).render('pages/error', { title: 'Error', status: 500, reference: req.id, heading: 'Something went wrong on our side.', message: 'Try again in a moment.' });
};
