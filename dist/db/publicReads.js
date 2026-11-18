"use strict";
Object.defineProperty(exports, "__esModule", { value: true });
exports.pingDatabase = pingDatabase;
exports.createSqlBranding = createSqlBranding;
const brandingTypes_1 = require("../services/brandingTypes");
const system_1 = require("./system");
/** Readiness probe: one cheap statement, deliberately without retry so callers get a fast answer. */
async function pingDatabase() {
    try {
        await (0, system_1.systemQuery)('SELECT 1 AS ok', [], { retry: false });
        return true;
    }
    catch {
        return false;
    }
}
/**
 * Branding for the layout and the public sign-in page, read on the system pool (config tables have no FILTER).
 * Cached briefly per instance and invalidated on save; other instances pick changes up within `ttlMs`.
 * Never waits for a paused database: on error the last known value (or the defaults) is used.
 */
function createSqlBranding(ttlMs = 30_000, now = Date.now) {
    let cached;
    let logoCache; // bytes kept in memory by SHA, so the logo endpoint does not hit the database per request
    return {
        async get() {
            if (cached && now() - cached.at < ttlMs)
                return cached.value;
            try {
                const rows = await (0, system_1.systemQuery)(`SELECT SettingKey AS k, SettingValue AS v FROM crm.Settings WHERE SettingKey IN ('app.name', 'branding.accentColor')`, [], { retry: false });
                const logo = await (0, system_1.systemQuery)(`SELECT LOWER(CONVERT(char(64), Sha256, 2)) AS sha FROM crm.BrandingAssets WHERE AssetKey = 'logo'`, [], { retry: false });
                const map = new Map(rows.map((r) => [r.k, r.v]));
                const value = { appName: map.get('app.name') ?? brandingTypes_1.DEFAULT_BRANDING.appName, accentColor: map.get('branding.accentColor') ?? brandingTypes_1.DEFAULT_BRANDING.accentColor, logoSha: logo[0]?.sha ?? null };
                cached = { at: now(), value };
                return value;
            }
            catch {
                // keep serving something; retry after a short pause so a paused database is not hammered
                const fallback = cached?.value ?? brandingTypes_1.DEFAULT_BRANDING;
                cached = { at: now() - ttlMs + 5_000, value: fallback };
                return fallback;
            }
        },
        async logo() {
            if (logoCache) {
                const current = await this.get();
                if (current.logoSha === logoCache.sha256)
                    return logoCache;
                logoCache = undefined;
            }
            try {
                const rows = await (0, system_1.systemQuery)(`SELECT ContentType AS ct, Content AS c, LOWER(CONVERT(char(64), Sha256, 2)) AS sha FROM crm.BrandingAssets WHERE AssetKey = 'logo'`, [], { retry: false });
                const r = rows[0];
                if (r)
                    logoCache = { contentType: r.ct, content: r.c, sha256: r.sha };
                return logoCache ?? null;
            }
            catch {
                return null;
            }
        },
        invalidate() { cached = undefined; logoCache = undefined; },
    };
}
//# sourceMappingURL=publicReads.js.map