import { createHash } from 'node:crypto';

export const MAX_LOGO_BYTES = 512_000;
export type LogoCheck = { ok: true; contentType: 'image/png'; sha256: string } | { ok: false; message: string };

const PNG = Buffer.from([0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a]);
const BAD_TYPE = 'Logo must be a PNG, 500 KB or smaller.';
const SVG_REFUSED = 'SVG logos are not supported. Upload a PNG, 500 KB or smaller.';

/**
 * PNG only. SVG can carry script and external references that a text filter cannot reliably remove, so it is refused
 * outright. The type is decided by the file content (magic bytes plus a first IHDR chunk), never by filename or declared MIME.
 */
export function checkLogo(buf: Buffer): LogoCheck {
  if (buf.length === 0 || buf.length > MAX_LOGO_BYTES) return { ok: false, message: BAD_TYPE };
  const isPng = buf.length >= 33 && buf.subarray(0, 8).equals(PNG) && buf.subarray(12, 16).toString('latin1') === 'IHDR';
  if (isPng) return { ok: true, contentType: 'image/png', sha256: createHash('sha256').update(buf).digest('hex') };
  const head = buf.subarray(0, 512).toString('utf8').toLowerCase();
  if (head.includes('<svg') || head.includes('<?xml')) return { ok: false, message: SVG_REFUSED };
  return { ok: false, message: BAD_TYPE };
}
