import { ENTITIES } from '../domain/entities';
import type { EntityKey, Row, Value } from '../domain/types';

export interface AuditRow {
  entityType: string;
  entityId: number;
  action: 'C' | 'U' | 'D';
  fieldName: string | null;
  oldValue: string | null;
  newValue: string | null;
  ownerId: number;
  teamId: number | null;
}

const MAX = 4000;
/** Invariant text: '.' decimals, ISO dates, truncated. Booleans as 0/1. */
export function auditText(v: Value | undefined): string | null {
  if (v === null || v === undefined) return null;
  const s = typeof v === 'boolean' ? (v ? '1' : '0') : String(v);
  return s.length > MAX ? s.slice(0, MAX) : s;
}

export function createRow(entity: EntityKey, id: number, label: string, ownerId: number, teamId: number | null): AuditRow {
  return { entityType: ENTITIES[entity].auditType, entityId: id, action: 'C', fieldName: null, oldValue: null, newValue: auditText(label), ownerId, teamId };
}

/** Owner and team are those BEFORE the delete. */
export function deleteRow(entity: EntityKey, id: number, label: string, ownerId: number, teamId: number | null): AuditRow {
  return { entityType: ENTITIES[entity].auditType, entityId: id, action: 'D', fieldName: null, oldValue: auditText(label), newValue: null, ownerId, teamId };
}

/** One row per changed audited field. `oldVals`/`newVals` come from UPDATE ... OUTPUT deleted/inserted. */
export function updateRows(entity: EntityKey, id: number, oldVals: Row, newVals: Row, ownerId: number, teamId: number | null, label: (field: string, v: Value) => Value = (_f, v) => v): AuditRow[] {
  const out: AuditRow[] = [];
  for (const field of ENTITIES[entity].auditFields) {
    const o = auditText(label(field, oldVals[field] ?? null));
    const n = auditText(label(field, newVals[field] ?? null));
    if (o !== n) out.push({ entityType: ENTITIES[entity].auditType, entityId: id, action: 'U', fieldName: field, oldValue: o, newValue: n, ownerId, teamId });
  }
  return out;
}
