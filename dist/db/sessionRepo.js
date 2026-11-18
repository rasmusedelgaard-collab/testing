"use strict";
var __importDefault = (this && this.__importDefault) || function (mod) {
    return (mod && mod.__esModule) ? mod : { "default": mod };
};
Object.defineProperty(exports, "__esModule", { value: true });
exports.sessionRepo = void 0;
const mssql_1 = __importDefault(require("mssql"));
const system_1 = require("./system");
exports.sessionRepo = {
    async get(sid) {
        const rows = await (0, system_1.systemQuery)('SELECT Data FROM app.Sessions WHERE Sid = @sid AND ExpiresAt > SYSUTCDATETIME()', [{ name: 'sid', type: mssql_1.default.VarChar(128), value: sid }]);
        return rows[0]?.Data ?? null;
    },
    async set(sid, data, expiresAt) {
        await (0, system_1.systemQuery)(`MERGE app.Sessions WITH (HOLDLOCK) AS s
       USING (SELECT @sid AS Sid) AS src ON s.Sid = src.Sid
       WHEN MATCHED THEN UPDATE SET Data = @data, ExpiresAt = @exp
       WHEN NOT MATCHED THEN INSERT (Sid, Data, ExpiresAt) VALUES (@sid, @data, @exp);`, [
            { name: 'sid', type: mssql_1.default.VarChar(128), value: sid },
            { name: 'data', type: mssql_1.default.NVarChar(mssql_1.default.MAX), value: data },
            { name: 'exp', type: mssql_1.default.DateTime2(0), value: expiresAt },
        ]);
    },
    async touch(sid, expiresAt) {
        await (0, system_1.systemQuery)('UPDATE app.Sessions SET ExpiresAt = @exp WHERE Sid = @sid', [
            { name: 'sid', type: mssql_1.default.VarChar(128), value: sid },
            { name: 'exp', type: mssql_1.default.DateTime2(0), value: expiresAt },
        ]);
    },
    async destroy(sid) {
        await (0, system_1.systemQuery)('DELETE FROM app.Sessions WHERE Sid = @sid', [{ name: 'sid', type: mssql_1.default.VarChar(128), value: sid }]);
    },
    async deleteExpired(batch) {
        const rows = await (0, system_1.systemQuery)(`DELETE TOP (@batch) FROM app.Sessions WHERE ExpiresAt < SYSUTCDATETIME();
       SELECT @@ROWCOUNT AS n;`, [{ name: 'batch', type: mssql_1.default.Int, value: batch }]);
        return rows[0]?.n ?? 0;
    },
};
//# sourceMappingURL=sessionRepo.js.map