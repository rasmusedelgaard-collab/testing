/** WCAG relative luminance and contrast against white. */
export function parseHex(hex: string): [number, number, number] | null {
  const m = /^#([0-9a-fA-F]{6})$/.exec(hex);
  if (!m) return null;
  const n = parseInt(m[1] as string, 16);
  return [(n >> 16) & 255, (n >> 8) & 255, n & 255];
}
export function contrastWithWhite(hex: string): number {
  const rgb = parseHex(hex);
  if (!rgb) return 0;
  const [r, g, b] = rgb.map((v) => {
    const c = v / 255;
    return c <= 0.03928 ? c / 12.92 : ((c + 0.055) / 1.055) ** 2.4;
  }) as [number, number, number];
  const l = 0.2126 * r + 0.7152 * g + 0.0722 * b;
  return 1.05 / (l + 0.05);
}
/** Darkens by `amount` (0-1) for the hover colour. */
export function darken(hex: string, amount = 0.15): string {
  const rgb = parseHex(hex) ?? [29, 78, 216];
  return `#${rgb.map((v) => Math.round(v * (1 - amount)).toString(16).padStart(2, '0')).join('')}`.toUpperCase();
}
