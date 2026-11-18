"use strict";
var __importDefault = (this && this.__importDefault) || function (mod) {
    return (mod && mod.__esModule) ? mod : { "default": mod };
};
Object.defineProperty(exports, "__esModule", { value: true });
exports.testUserRepo = void 0;
const mssql_1 = __importDefault(require("mssql"));
const system_1 = require("./system");
const SELECT = `SELECT u.UserId, CONVERT(char(36), u.EntraOid) AS EntraOid, u.DisplayName, u.Email, u.Role, u.TeamId, t.Name AS TeamName
  FROM crm.Users u LEFT JOIN crm.Teams t ON t.TeamId = u.TeamId
  WHERE u.IsActive = 1 AND u.EntraOid IN (SELECT TRY_CAST([value] AS uniqueidentifier) FROM OPENJSON(@oids))`;
const map = (r) => ({
    userId: r.UserId,
    oid: r.EntraOid.toLowerCase(),
    name: r.DisplayName,
    email: r.Email,
    role: r.Role,
    teamId: r.TeamId,
    teamName: r.TeamName,
});
exports.testUserRepo = {
    async list(oids) {
        const rows = await (0, system_1.systemQuery)(`${SELECT} ORDER BY u.UserId`, [{ name: 'oids', type: mssql_1.default.NVarChar(mssql_1.default.MAX), value: JSON.stringify(oids) }]);
        return rows.map(map);
    },
    async find(oid, allowedOids) {
        if (!allowedOids.includes(oid))
            return null;
        const rows = await this.list([oid]);
        return rows[0] ?? null;
    },
};
//# sourceMappingURL=testUserRepo.js.map