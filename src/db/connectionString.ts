/**
 * Strict parser for ADO.NET style SQL Server connection strings ("Server=...;Database=...;User Id=...;Password=...;").
 * Only SQL-login strings are supported. Anything unknown or weakening (Encrypt=false, integrated security, other
 * authentication) is refused. Error messages name keys only and NEVER contain a value, because values include the password.
 */
export interface ParsedConnectionString {
  server: string;
  port?: number;
  database: string;
  user: string;
  password: string;
  trustServerCertificate: boolean;
}
export class ConnectionStringError extends Error {}

/** Keys we understand, lower-cased with spaces removed. Mapped keys are used; the others are accepted and ignored. */
const SERVER = ['server', 'datasource', 'addr', 'address', 'networkaddress'];
const DATABASE = ['database', 'initialcatalog'];
const USER = ['userid', 'uid', 'user'];
const PASSWORD = ['password', 'pwd'];
const IGNORED = ['persistsecurityinfo', 'multipleactiveresultsets', 'connecttimeout', 'connectiontimeout', 'timeout', 'applicationname', 'app', 'pooling', 'workstationid', 'wsid'];
const REFUSED = ['integratedsecurity', 'trustedconnection', 'authentication', 'attachdbfilename', 'failoverpartner', 'applicationintent', 'multisubnetfailover'];

function readPairs(input: string): Array<[string, string]> {
  const pairs: Array<[string, string]> = [];
  let i = 0;
  const n = input.length;
  while (i < n) {
    while (i < n && (input[i] === ';' || /\s/.test(input[i] as string))) i++;
    if (i >= n) break;
    const eq = input.indexOf('=', i);
    if (eq < 0) throw new ConnectionStringError('malformed: a segment has no "="');
    const key = input.slice(i, eq).trim();
    if (!key || key.includes(';')) throw new ConnectionStringError('malformed: empty or invalid key');
    i = eq + 1;
    while (i < n && /\s/.test(input[i] as string)) i++;
    let value = '';
    const quote = input[i];
    if (quote === '"' || quote === "'") {
      i++;
      for (;;) {
        if (i >= n) throw new ConnectionStringError(`malformed: unterminated quoted value for "${key}"`);
        if (input[i] === quote) {
          if (input[i + 1] === quote) { value += quote; i += 2; continue; } // doubled quote = literal quote
          i++;
          break;
        }
        value += input[i];
        i++;
      }
      while (i < n && /\s/.test(input[i] as string)) i++;
      if (i < n && input[i] !== ';') throw new ConnectionStringError(`malformed: text after a quoted value for "${key}"`);
    } else {
      const end = input.indexOf(';', i);
      value = input.slice(i, end < 0 ? n : end).trim();
      i = end < 0 ? n : end;
    }
    pairs.push([key, value]);
  }
  return pairs;
}

const truthy = (v: string) => /^(true|yes|1)$/i.test(v);
const falsy = (v: string) => /^(false|no|0)$/i.test(v);

export function parseConnectionString(input: string): ParsedConnectionString {
  if (input.length > 2000) throw new ConnectionStringError('too long');
  const out: Partial<ParsedConnectionString> & { trustServerCertificate: boolean } = { trustServerCertificate: false };
  const seen = new Set<string>();
  for (const [rawKey, value] of readPairs(input)) {
    const key = rawKey.toLowerCase().replace(/\s+/g, '');
    if (seen.has(key)) throw new ConnectionStringError(`duplicate key "${rawKey}"`);
    seen.add(key);
    if (SERVER.includes(key)) {
      // "tcp:host,1433" or "host,1433" or "host" (named instances are not supported)
      const m = /^(?:tcp:)?([A-Za-z0-9._-]+)(?:,(\d{1,5}))?$/.exec(value);
      if (!m) throw new ConnectionStringError(`unsupported "${rawKey}" value (expected host or host,port)`);
      out.server = m[1];
      if (m[2]) {
        const port = Number(m[2]);
        if (port < 1 || port > 65535) throw new ConnectionStringError('port must be between 1 and 65535');
        out.port = port;
      }
    } else if (DATABASE.includes(key)) {
      if (!value || value.length > 128) throw new ConnectionStringError(`invalid "${rawKey}"`);
      out.database = value;
    } else if (USER.includes(key)) {
      if (!value || value.length > 128) throw new ConnectionStringError(`invalid "${rawKey}"`);
      out.user = value;
    } else if (PASSWORD.includes(key)) {
      if (!value || value.length > 256) throw new ConnectionStringError(`invalid "${rawKey}"`);
      out.password = value;
    } else if (key === 'trustservercertificate') {
      if (!truthy(value) && !falsy(value)) throw new ConnectionStringError('invalid "TrustServerCertificate" (use True or False)');
      out.trustServerCertificate = truthy(value);
    } else if (key === 'encrypt') {
      if (falsy(value)) throw new ConnectionStringError('"Encrypt=False" is not supported: connections are always encrypted');
      if (!truthy(value) && !/^(strict|mandatory|optional)$/i.test(value)) throw new ConnectionStringError('invalid "Encrypt"');
    } else if (IGNORED.includes(key)) {
      continue;
    } else if (REFUSED.includes(key)) {
      throw new ConnectionStringError(`"${rawKey}" is not supported (SQL login strings only)`);
    } else {
      throw new ConnectionStringError(`unknown key "${rawKey}"`);
    }
  }
  const missing = [['Server', out.server], ['Database', out.database], ['User Id', out.user], ['Password', out.password]].filter(([, v]) => !v).map(([k]) => k);
  if (missing.length) throw new ConnectionStringError(`missing ${missing.join(', ')}`);
  return out as ParsedConnectionString;
}
