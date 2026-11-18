"use strict";
Object.defineProperty(exports, "__esModule", { value: true });
exports.auditText = auditText;
exports.createRow = createRow;
exports.deleteRow = deleteRow;
exports.updateRows = updateRows;
const entities_1 = require("../domain/entities");
const MAX = 4000;
/** Invariant text: '.' decimals, ISO dates, truncated. Booleans as 0/1. */
function auditText(v) {
    if (v === null || v === undefined)
        return null;
    const s = typeof v === 'boolean' ? (v ? '1' : '0') : String(v);
    return s.length > MAX ? s.slice(0, MAX) : s;
}
function createRow(entity, id, label, ownerId, teamId) {
    return { entityType: entities_1.ENTITIES[entity].auditType, entityId: id, action: 'C', fieldName: null, oldValue: null, newValue: auditText(label), ownerId, teamId };
}
/** Owner and team are those BEFORE the delete. */
function deleteRow(entity, id, label, ownerId, teamId) {
    return { entityType: entities_1.ENTITIES[entity].auditType, entityId: id, action: 'D', fieldName: null, oldValue: auditText(label), newValue: null, ownerId, teamId };
}
/** One row per changed audited field. `oldVals`/`newVals` come from UPDATE ... OUTPUT deleted/inserted. */
function updateRows(entity, id, oldVals, newVals, ownerId, teamId, label = (_f, v) => v) {
    const out = [];
    for (const field of entities_1.ENTITIES[entity].auditFields) {
        const o = auditText(label(field, oldVals[field] ?? null));
        const n = auditText(label(field, newVals[field] ?? null));
        if (o !== n)
            out.push({ entityType: entities_1.ENTITIES[entity].auditType, entityId: id, action: 'U', fieldName: field, oldValue: o, newValue: n, ownerId, teamId });
    }
    return out;
}
//# sourceMappingURL=audit.js.map