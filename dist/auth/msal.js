"use strict";
Object.defineProperty(exports, "__esModule", { value: true });
exports.idClaimsSchema = void 0;
exports.createMsalClient = createMsalClient;
const msal_node_1 = require("@azure/msal-node");
const zod_1 = require("zod");
const guid = zod_1.z.string().regex(/^[0-9a-fA-F-]{36}$/);
exports.idClaimsSchema = zod_1.z.object({
    oid: guid,
    tid: guid,
    nonce: zod_1.z.string().optional(),
    name: zod_1.z.string().optional(),
    email: zod_1.z.string().optional(),
    preferred_username: zod_1.z.string().optional(),
    roles: zod_1.z.array(zod_1.z.string()).optional(),
    groups: zod_1.z.array(zod_1.z.string()).optional(),
    hasgroups: zod_1.z.boolean().optional(),
    _claim_names: zod_1.z.record(zod_1.z.string(), zod_1.z.unknown()).optional(),
});
const SCOPES = ['openid', 'profile', 'email'];
function createMsalClient(config) {
    const { tenantId, clientId, clientSecret } = config.entra;
    const authority = `https://login.microsoftonline.com/${tenantId}`;
    const app = new msal_node_1.ConfidentialClientApplication({
        auth: { clientId, clientSecret, authority },
    });
    return {
        authUrl: ({ state, nonce, codeChallenge }) => app.getAuthCodeUrl({
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
            for (const account of await cache.getAllAccounts())
                await cache.removeAccount(account);
            return exports.idClaimsSchema.parse(result.idTokenClaims);
        },
        logoutUrl() {
            const url = new URL(`${authority}/oauth2/v2.0/logout`);
            url.searchParams.set('post_logout_redirect_uri', `${config.baseUrl}/signed-out`);
            return url.toString();
        },
    };
}
//# sourceMappingURL=msal.js.map