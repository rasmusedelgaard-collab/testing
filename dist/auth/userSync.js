"use strict";
Object.defineProperty(exports, "__esModule", { value: true });
exports.extractGroups = extractGroups;
exports.applyTeamPolicy = applyTeamPolicy;
exports.syncUser = syncUser;
const logger_1 = require("../logger");
/** Overage: the token carries `_claim_names.groups`, or `groups` is missing while `hasgroups` is true. */
function extractGroups(claims) {
    const overage = Boolean(claims._claim_names && 'groups' in claims._claim_names) || (claims.groups === undefined && claims.hasgroups === true);
    if (overage)
        return { groupIds: [], overage: true };
    return { groupIds: (claims.groups ?? []).map((g) => g.toLowerCase()), overage: false };
}
/**
 * A user must be in exactly ONE linked team group. The stored procedure decides the match; this
 * re-checks the outcome (fail closed) and logs why a user ended up without a team. There is no
 * "lowest TeamId wins": several matches mean TeamId NULL (own records only).
 */
function applyTeamPolicy(result, oid) {
    switch (result.teamStatus) {
        case 'ok':
            return result.teamMatches === 1 ? result.teamId : null;
        case 'multiple':
            logger_1.logger.warn({ oid, teamMatches: result.teamMatches }, 'user in several team groups; no team applied');
            return null;
        case 'overage':
            logger_1.logger.error({ oid }, 'groups claim overage; no team applied');
            return null;
        default:
            return null;
    }
}
async function syncUser(repo, input) {
    const { claims, role } = input;
    const groups = extractGroups(claims);
    const result = await repo.upsertOnLogin({
        entraOid: claims.oid,
        tenantId: input.tenantId,
        displayName: claims.name ?? claims.preferred_username ?? claims.email ?? 'Unknown user',
        email: claims.email ?? claims.preferred_username ?? null,
        role,
        groupIds: groups.groupIds,
        groupsOverage: groups.overage,
    });
    return {
        userId: result.userId,
        oid: claims.oid,
        name: claims.name ?? claims.preferred_username ?? claims.email ?? 'Unknown user',
        email: claims.email ?? claims.preferred_username ?? null,
        role,
        teamId: applyTeamPolicy(result, claims.oid),
        authAt: Date.now(),
    };
}
//# sourceMappingURL=userSync.js.map