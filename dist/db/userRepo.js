"use strict";
var __importDefault = (this && this.__importDefault) || function (mod) {
    return (mod && mod.__esModule) ? mod : { "default": mod };
};
Object.defineProperty(exports, "__esModule", { value: true });
exports.userRepo = exports.USER_DEACTIVATED = void 0;
const mssql_1 = __importDefault(require("mssql"));
const system_1 = require("./system");
/** Error number raised by crm.usp_UpsertUserOnLogin for a deactivated user. */
exports.USER_DEACTIVATED = 50011;
/** The only write path to crm.Users: crm.usp_UpsertUserOnLogin (idempotent MERGE, so retry-safe). */
/** Is the user still active? A missing row counts as inactive. Read-only and idempotent. */
exports.userRepo = {
    async isActive(userId) {
        const rows = await (0, system_1.systemQuery)('SELECT IsActive FROM crm.Users WHERE UserId = @id', [{ name: 'id', type: mssql_1.default.Int, value: userId }]);
        return rows[0]?.IsActive === true;
    },
    async upsertOnLogin(i) {
        const rows = await (0, system_1.systemQuery)(`EXEC crm.usp_UpsertUserOnLogin @EntraOid = @EntraOid, @TenantId = @TenantId, @DisplayName = @DisplayName,
         @Email = @Email, @Role = @Role, @GroupIdsJson = @GroupIdsJson, @GroupsOverage = @GroupsOverage`, [
            { name: 'EntraOid', type: mssql_1.default.UniqueIdentifier, value: i.entraOid },
            { name: 'TenantId', type: mssql_1.default.UniqueIdentifier, value: i.tenantId },
            { name: 'DisplayName', type: mssql_1.default.NVarChar(200), value: i.displayName },
            { name: 'Email', type: mssql_1.default.NVarChar(254), value: i.email },
            { name: 'Role', type: mssql_1.default.VarChar(10), value: i.role },
            { name: 'GroupIdsJson', type: mssql_1.default.NVarChar(mssql_1.default.MAX), value: JSON.stringify(i.groupIds) },
            { name: 'GroupsOverage', type: mssql_1.default.Bit, value: i.groupsOverage },
        ]);
        const row = rows[0];
        if (!row)
            throw new Error('usp_UpsertUserOnLogin returned no row');
        return { userId: row.UserId, teamId: row.TeamId, teamStatus: row.TeamStatus, teamMatches: row.TeamMatches };
    },
};
//# sourceMappingURL=userRepo.js.map