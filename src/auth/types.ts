import type { Role, RlsContext } from '../db/withRlsContext';

export type { Role, RlsContext };

/** Identity kept in the server-side session. No tokens, ever. */
export interface SessionUser {
  userId: number;
  oid: string;
  name: string;
  email: string | null;
  role: Role;
  teamId: number | null;
  /** Epoch ms of sign-in; drives the absolute session timeout. */
  authAt: number;
}

declare module 'express-session' {
  interface SessionData {
    user?: SessionUser;
    csrfToken?: string;
    /** Transient Entra login state, deleted after the callback. */
    auth?: { state: string; nonce: string; pkceVerifier: string; returnTo: string };
    /** Test-access-code mode: set once the shared code was accepted, until a user is chosen. */
    testAccess?: { verifiedAt: number };
    flash?: { kind: 'success' | 'error'; message: string };
  }
}

declare module 'express-serve-static-core' {
  interface Request {
    id: string;
    /** Set by requireAuth: the identity handed to withRlsContext. */
    rls?: RlsContext;
  }
}
