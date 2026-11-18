/** Only relative paths starting with a single "/" are accepted (open-redirect guard). */
export function safeReturnTo(value: unknown, fallback = '/'): string {
  if (typeof value !== 'string' || value.length === 0 || value.length > 2000) return fallback;
  if (!value.startsWith('/') || value.startsWith('//') || value.includes('\\')) return fallback;
  for (let i = 0; i < value.length; i++) if (value.charCodeAt(i) < 32) return fallback; // control chars / header injection
  return value;
}
