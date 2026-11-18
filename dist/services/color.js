"use strict";
Object.defineProperty(exports, "__esModule", { value: true });
exports.accentContrast = exports.PAGE_BG = exports.contrastWithWhite = void 0;
exports.parseHex = parseHex;
exports.contrastAgainst = contrastAgainst;
exports.darken = darken;
/** WCAG relative luminance and contrast against white. */
function parseHex(hex) {
    const m = /^#([0-9a-fA-F]{6})$/.exec(hex);
    if (!m)
        return null;
    const n = parseInt(m[1], 16);
    return [(n >> 16) & 255, (n >> 8) & 255, n & 255];
}
function luminance(rgb) {
    const [r, g, b] = rgb.map((v) => {
        const c = v / 255;
        return c <= 0.03928 ? c / 12.92 : ((c + 0.055) / 1.055) ** 2.4;
    });
    return 0.2126 * r + 0.7152 * g + 0.0722 * b;
}
/** WCAG contrast ratio of `hex` against another colour. 0 when either is not #RRGGBB. */
function contrastAgainst(hex, bg) {
    const a = parseHex(hex);
    const b = parseHex(bg);
    if (!a || !b)
        return 0;
    const [hi, lo] = [luminance(a), luminance(b)].sort((x, y) => y - x);
    return (hi + 0.05) / (lo + 0.05);
}
const contrastWithWhite = (hex) => contrastAgainst(hex, '#FFFFFF');
exports.contrastWithWhite = contrastWithWhite;
/** The accent colour is drawn as text/links on white AND on the page background (--color-bg), so it must pass on both. */
exports.PAGE_BG = '#F9FAFB';
const accentContrast = (hex) => Math.min(contrastAgainst(hex, '#FFFFFF'), contrastAgainst(hex, exports.PAGE_BG));
exports.accentContrast = accentContrast;
/** Darkens by `amount` (0-1) for the hover colour. */
function darken(hex, amount = 0.15) {
    const rgb = parseHex(hex) ?? [29, 78, 216];
    return `#${rgb.map((v) => Math.round(v * (1 - amount)).toString(16).padStart(2, '0')).join('')}`.toUpperCase();
}
//# sourceMappingURL=color.js.map