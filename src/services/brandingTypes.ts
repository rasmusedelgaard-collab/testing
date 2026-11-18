export interface Branding { appName: string; accentColor: string; logoSha: string | null }
export interface LogoData { contentType: 'image/png' | 'image/svg+xml'; content: Buffer; sha256: string }
export const DEFAULT_BRANDING: Branding = { appName: 'CRM', accentColor: '#1D4ED8', logoSha: null };

/** Name, accent colour and logo for the layout and the public sign-in page. Never throws; falls back to defaults. */
export interface BrandingProvider {
  get(): Promise<Branding>;
  logo(): Promise<LogoData | null>;
  /** Called after an Admin saves branding so this instance shows the change immediately. */
  invalidate(): void;
}
