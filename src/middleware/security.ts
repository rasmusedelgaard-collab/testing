import helmet from 'helmet';
import type { RequestHandler } from 'express';
import type { AppConfig } from '../config';

/** Strict CSP (overview 10): no inline script or style anywhere. */
export function securityHeaders(config: AppConfig): RequestHandler {
  return helmet({
    contentSecurityPolicy: {
      useDefaults: false,
      directives: {
        defaultSrc: ["'self'"],
        scriptSrc: ["'self'"],
        styleSrc: ["'self'"],
        imgSrc: ["'self'", 'data:'],
        objectSrc: ["'none'"],
        frameAncestors: ["'none'"],
        baseUri: ["'self'"],
        formAction: ["'self'", 'https://login.microsoftonline.com'],
        ...(config.secureCookies ? { upgradeInsecureRequests: [] } : {}),
      },
    },
    referrerPolicy: { policy: 'same-origin' },
    strictTransportSecurity: config.secureCookies ? { maxAge: 31536000, includeSubDomains: true } : false,
    crossOriginEmbedderPolicy: false,
  });
}
