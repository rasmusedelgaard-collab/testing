import { ConfidentialClientApplication } from '@azure/msal-node';
import { z } from 'zod';
import type { AppConfig } from '../config';

const guid = z.string().regex(/^[0-9a-fA-F-]{36}$/);

export const idClaimsSchema = z.object({
  oid: guid,
  tid: guid,
  nonce: z.string().optional(),
  name: z.string().optional(),
  email: z.string().optional(),
  preferred_username: z.string().optional(),
  roles: z.array(z.string()).optional(),
  groups: z.array(z.string()).optional(),
  hasgroups: z.boolean().optional(),
  _claim_names: z.record(z.string(), z.unknown()).optional(),
});
export type IdClaims = z.infer<typeof idClaimsSchema>;

/** Narrow seam so routes can be tested without Entra. */
export interface OidcClient {
  authUrl(p: { state: string; nonce: string; codeChallenge: string }): Promise<string>;
  redeem(p: { code: string; codeVerifier: string }): Promise<IdClaims>;
  logoutUrl(): string;
}

const SCOPES = ['openid', 'profile', 'email'];

export function createMsalClient(config: AppConfig): OidcClient {
  const { tenantId, clientId, clientSecret } = config.entra;
  const authority = `https://login.microsoftonline.com/${tenantId}`;
  const app = new ConfidentialClientApplication({
    auth: { clientId, clientSecret, authority },
  });
  return {
    authUrl: ({ state, nonce, codeChallenge }) =>
      app.getAuthCodeUrl({
        scopes: SCOPES,
        redirectUri: config.redirectUri,
        state,
        nonce,
        codeChallenge,
        codeChallengeMethod: 'S256',
        responseMode: 'query',
      }),
    async redeem({ code, codeVerifier }) {
      const result = await app.acquireTokenByCode({ code, scopes: SCOPES, redirectUri: config.redirectUri, codeVerifier });
      // Tokens are never persisted: drop the in-memory cache entry right away (ADR-0003).
      const cache = app.getTokenCache();
      for (const account of await cache.getAllAccounts()) await cache.removeAccount(account);
      return idClaimsSchema.parse(result.idTokenClaims);
    },
    logoutUrl() {
      const url = new URL(`${authority}/oauth2/v2.0/logout`);
      url.searchParams.set('post_logout_redirect_uri', `${config.baseUrl}/signed-out`);
      return url.toString();
    },
  };
}
