import { readdirSync, readFileSync } from 'node:fs';
import path from 'node:path';
import { describe, expect, it } from 'vitest';
import { checksum, parseMigrationName, splitBatches } from '../scripts/sqlBatches';

describe('splitBatches', () => {
  it('splits on GO lines only and drops empty batches', () => {
    expect(splitBatches('SELECT 1;\nGO\n\nSELECT 2; -- GO here\nGO  \nGO\n')).toEqual(['SELECT 1;', 'SELECT 2; -- GO here']);
    expect(splitBatches('SELECT 1\r\ngo\r\nSELECT 2')).toEqual(['SELECT 1', 'SELECT 2']);
    expect(splitBatches('-- GOAL\nSELECT 1')).toEqual(['-- GOAL\nSELECT 1']);
  });
});

describe('migrations on disk', () => {
  const dir = path.resolve(__dirname, '../db/migrations');
  const files = readdirSync(dir).filter((f) => f.endsWith('.sql')).sort();

  it('are numbered 001.. without gaps', () => {
    expect(files.map((f) => parseMigrationName(f)?.version)).toEqual(files.map((_, i) => i + 1));
  });
  it('checksums are stable and sensitive to content', () => {
    expect(checksum('a\r\nb')).toBe(checksum('a\nb'));
    expect(checksum('a')).not.toBe(checksum('b'));
  });
  it('002 creates both security policies enabled and schema-bound', () => {
    const text = readFileSync(path.join(dir, '002_rls.sql'), 'utf8');
    expect(text).toContain('CREATE SECURITY POLICY sec.CrmRowPolicy');
    expect(text).toContain('CREATE SECURITY POLICY sec.CrmConfigPolicy');
    expect(text.match(/WITH \(STATE = ON, SCHEMABINDING = ON\)/g)).toHaveLength(2);
  });
  it('seed defaults visibility.mode to private', () => {
    expect(readFileSync(path.join(dir, '004_seed.sql'), 'utf8')).toMatch(/'visibility\.mode',\s+N'private'/);
  });
  it('003 grants no DELETE on audit log and nothing on sec/dbo', () => {
    const text = readFileSync(path.join(dir, '003_grants.sql'), 'utf8');
    expect(text).toMatch(/GRANT SELECT, INSERT\s+ON crm\.AuditLog/);
    expect(text).not.toMatch(/ON (sec|dbo)\./);
  });
});
