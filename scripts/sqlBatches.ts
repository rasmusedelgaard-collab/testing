import { createHash } from 'node:crypto';

/** Splits a T-SQL script on lines containing only GO (case-insensitive), dropping empty batches. */
export function splitBatches(script: string): string[] {
  const batches: string[] = [];
  let current: string[] = [];
  for (const line of script.replace(/\r\n/g, '\n').split('\n')) {
    if (/^\s*GO\s*(--.*)?$/i.test(line)) {
      batches.push(current.join('\n'));
      current = [];
    } else {
      current.push(line);
    }
  }
  batches.push(current.join('\n'));
  return batches.map((b) => b.trim()).filter((b) => b.length > 0);
}

export function checksum(script: string): string {
  return createHash('sha256').update(script.replace(/\r\n/g, '\n')).digest('hex');
}

/** Migration files are NNN_name.sql; returns them sorted by version. */
export function parseMigrationName(file: string): { version: number; name: string } | null {
  const m = /^(\d{3})_(.+)\.sql$/.exec(file);
  return m ? { version: Number(m[1]), name: m[2] as string } : null;
}
