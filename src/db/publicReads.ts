import { DEFAULT_BRANDING, type Branding, type BrandingProvider, type LogoData } from '../services/brandingTypes';
import { systemQuery } from './system';

/** Readiness probe: one cheap statement, deliberately without retry so callers get a fast answer. */
export async function pingDatabase(): Promise<boolean> {
  try {
    await systemQuery('SELECT 1 AS ok', [], { retry: false });
    return true;
  } catch {
    return false;
  }
}

/**
 * Branding for the layout and the public sign-in page, read on the system pool (config tables have no FILTER).
 * Cached briefly per instance and invalidated on save; other instances pick changes up within `ttlMs`.
 * Never waits for a paused database: on error the last known value (or the defaults) is used.
 */
export function createSqlBranding(ttlMs = 30_000, now: () => number = Date.now): BrandingProvider {
  let cached: { at: number; value: Branding } | undefined;
  return {
    async get() {
      if (cached && now() - cached.at < ttlMs) return cached.value;
      try {
        const rows = await systemQuery<{ k: string; v: string }>(
          `SELECT SettingKey AS k, SettingValue AS v FROM crm.Settings WHERE SettingKey IN ('app.name', 'branding.accentColor')`, [], { retry: false });
        const logo = await systemQuery<{ sha: string }>(`SELECT LOWER(CONVERT(char(64), Sha256, 2)) AS sha FROM crm.BrandingAssets WHERE AssetKey = 'logo'`, [], { retry: false });
        const map = new Map(rows.map((r) => [r.k, r.v]));
        const value: Branding = { appName: map.get('app.name') ?? DEFAULT_BRANDING.appName, accentColor: map.get('branding.accentColor') ?? DEFAULT_BRANDING.accentColor, logoSha: logo[0]?.sha ?? null };
        cached = { at: now(), value };
        return value;
      } catch {
        // keep serving something; retry after a short pause so a paused database is not hammered
        const fallback = cached?.value ?? DEFAULT_BRANDING;
        cached = { at: now() - ttlMs + 5_000, value: fallback };
        return fallback;
      }
    },
    async logo(): Promise<LogoData | null> {
      try {
        const rows = await systemQuery<{ ct: LogoData['contentType']; c: Buffer; sha: string }>(`SELECT ContentType AS ct, Content AS c, LOWER(CONVERT(char(64), Sha256, 2)) AS sha FROM crm.BrandingAssets WHERE AssetKey = 'logo'`, [], { retry: false });
        const r = rows[0];
        return r ? { contentType: r.ct, content: r.c, sha256: r.sha } : null;
      } catch {
        return null;
      }
    },
    invalidate() { cached = undefined; },
  };
}
