import { createHash } from 'node:crypto';

export const MAX_LOGO_BYTES = 512_000;
export type LogoCheck = { ok: true; contentType: 'image/png' | 'image/svg+xml'; sha256: string } | { ok: false; message: string };

const PNG = Buffer.from([0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a]);
const BAD_TYPE = 'Logo must be PNG or SVG, 500 KB or smaller.';
const UNSAFE = "This SVG contains script or external content and can't be used.";

/** Rejects SVG features that can execute code or load external content (overview 10). The logo is only ever shown through <img>. */
export function svgIsSafe(text: string): boolean {
  const t = text.toLowerCase();
  if (/<script|<foreignobject|<iframe|<embed|<object|<!doctype|<!entity|<!\[cdata\[/.test(t)) return false;
  if (/\bon[a-z]+\s*=/.test(t)) return false;
  if (/javascript:|vbscript:|data:text\/html/.test(t)) return false;
  if (/url\(\s*['"]?\s*(?!#)/.test(t)) return false;
  if (/@import/.test(t)) return false;
  for (const m of t.matchAll(/(?:xlink:)?href\s*=\s*(?:"([^"]*)"|'([^']*)')/g)) {
    const v = (m[1] ?? m[2] ?? '').trim();
    if (!v.startsWith('#')) return false;
  }
  return true;
}

/** Content decides the type, never the filename or declared MIME. */
export function checkLogo(buf: Buffer): LogoCheck {
  if (buf.length === 0 || buf.length > MAX_LOGO_BYTES) return { ok: false, message: BAD_TYPE };
  const sha256 = createHash('sha256').update(buf).digest('hex');
  if (buf.subarray(0, 8).equals(PNG)) return { ok: true, contentType: 'image/png', sha256 };
  if (buf.includes(0)) return { ok: false, message: BAD_TYPE };
  const text = buf.toString('utf8');
  if (text.includes('�')) return { ok: false, message: BAD_TYPE };
  if (!/^\s*(<\?xml[^>]*\?>\s*)?(<!--[\s\S]*?-->\s*)*<svg[\s>]/i.test(text)) return { ok: false, message: BAD_TYPE };
  if (!svgIsSafe(text)) return { ok: false, message: UNSAFE };
  return { ok: true, contentType: 'image/svg+xml', sha256 };
}
