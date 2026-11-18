/** WCAG relative luminance and contrast against white. */
export function parseHex(hex: string): [number, number, number] | null {
  const m = /^#([0-9a-fA-F]{6})$/.exec(hex);
  if (!m) return null;
  const n = parseInt(m[1] as string, 16);
  return [(n >> 16) & 255, (n >> 8) & 255, n & 255];
}
function luminance(rgb: [number, number, number]): number {
  const [r, g, b] = rgb.map((v) => {
    const c = v / 255;
    return c <= 0.03928 ? c / 12.92 : ((c + 0.055) / 1.055) ** 2.4;
  }) as [number, number, number];
  return 0.2126 * r + 0.7152 * g + 0.0722 * b;
}

/** WCAG contrast ratio of `hex` against another colour. 0 when either is not #RRGGBB. */
export function contrastAgainst(hex: string, bg: string): number {
  const a = parseHex(hex);
  const b = parseHex(bg);
  if (!a || !b) return 0;
  const [hi, lo] = [luminance(a), luminance(b)].sort((x, y) => y - x) as [number, number];
  return (hi + 0.05) / (lo + 0.05);
}

export const contrastWithWhite = (hex: string): number => contrastAgainst(hex, '#FFFFFF');
/** The accent colour is drawn as text/links on white AND on the page background (--color-bg), so it must pass on both. */
export const PAGE_BG = '#F9FAFB';
export const accentContrast = (hex: string): number => Math.min(contrastAgainst(hex, '#FFFFFF'), contrastAgainst(hex, PAGE_BG));
/** Darkens by `amount` (0-1) for the hover colour. */
export function darken(hex: string, amount = 0.15): string {
  const rgb = parseHex(hex) ?? [29, 78, 216];
  return `#${rgb.map((v) => Math.round(v * (1 - amount)).toString(16).padStart(2, '0')).join('')}`.toUpperCase();
}
