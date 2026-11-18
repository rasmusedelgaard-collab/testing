"use strict";
Object.defineProperty(exports, "__esModule", { value: true });
exports.mapRole = mapRole;
exports.hasAtLeast = hasAtLeast;
const APP_ROLES = [
    ['CRM.Admin', 'Admin'],
    ['CRM.Manager', 'Manager'],
    ['CRM.User', 'User'],
];
/** Highest app role wins. Unknown values are ignored. null means "no CRM access". */
function mapRole(roles) {
    if (!Array.isArray(roles))
        return null;
    const present = new Set(roles.filter((r) => typeof r === 'string'));
    for (const [claim, role] of APP_ROLES)
        if (present.has(claim))
            return role;
    return null;
}
const RANK = { User: 1, Manager: 2, Admin: 3 };
function hasAtLeast(actual, required) {
    return RANK[actual] >= RANK[required];
}
//# sourceMappingURL=roles.js.map