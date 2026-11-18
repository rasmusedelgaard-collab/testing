import type { EntityKey } from '../domain/types';

/** Static SQL fragments per entity. No user input is ever concatenated: filter/sort keys are whitelisted in domain/entities.ts. */
export interface SqlDef {
  cols: string;
  joins: string;
  insertExtra?: { col: string; expr: string };
  updateExtra?: { col: string; expr: string };
  clearLinks: string[];
}

const ts = (c: string) => `CONVERT(varchar(23), ${c}, 126) + 'Z'`;
export const COMMON_COLS = `e.OwnerId, e.TeamId, uo.DisplayName AS OwnerName, e.CreatedBy, uc.DisplayName AS CreatedByName, ${ts('e.CreatedAt')} AS CreatedAt,
  e.ModifiedBy, um.DisplayName AS ModifiedByName, ${ts('e.ModifiedAt')} AS ModifiedAt, CONVERT(varchar(20), e.RowVer, 1) AS RowVer`;
export const COMMON_JOINS = `JOIN crm.Users uo ON uo.UserId = e.OwnerId JOIN crm.Users uc ON uc.UserId = e.CreatedBy JOIN crm.Users um ON um.UserId = e.ModifiedBy`;
const contactName = (a: string) => `LTRIM(CONCAT(${a}.FirstName, N' ', ${a}.LastName))`;

const wonLost = `EXISTS (SELECT 1 FROM crm.PipelineStages WHERE StageId = @StageId AND StageType IN ('won','lost'))`;

export const SQL: Record<EntityKey, SqlDef> = {
  accounts: {
    cols: `e.Name, e.Website, e.Phone, e.IndustryValueId, iv.Label AS IndustryLabel, e.Street, e.PostalCode, e.City, e.CountryCode, e.Notes`,
    joins: `LEFT JOIN crm.PicklistValues iv ON iv.ValueId = e.IndustryValueId`,
    clearLinks: ['UPDATE crm.Contacts SET AccountId = NULL WHERE AccountId = @id', 'UPDATE crm.Activities SET AccountId = NULL WHERE AccountId = @id'],
  },
  contacts: {
    cols: `e.AccountId, a.Name AS AccountName, e.FirstName, e.LastName, e.JobTitle, e.Email, e.Phone, e.Mobile`,
    joins: `LEFT JOIN crm.Accounts a ON a.AccountId = e.AccountId`,
    clearLinks: ['UPDATE crm.Opportunities SET PrimaryContactId = NULL WHERE PrimaryContactId = @id', 'UPDATE crm.Activities SET ContactId = NULL WHERE ContactId = @id'],
  },
  opportunities: {
    cols: `e.Name, e.AccountId, a.Name AS AccountName, e.PrimaryContactId, ${contactName('ct')} AS PrimaryContactName, e.StageId, s.Name AS StageName, s.StageType, s.Probability, s.SortOrder AS StageSort,
  e.Amount, e.CurrencyCode, CONVERT(char(10), e.CloseDate, 23) AS CloseDate, e.LeadSourceValueId, ls.Label AS LeadSourceLabel, e.LostReasonValueId, lr.Label AS LostReasonLabel, ${ts('e.ClosedAt')} AS ClosedAt`,
    joins: `LEFT JOIN crm.Accounts a ON a.AccountId = e.AccountId LEFT JOIN crm.Contacts ct ON ct.ContactId = e.PrimaryContactId JOIN crm.PipelineStages s ON s.StageId = e.StageId
  LEFT JOIN crm.PicklistValues ls ON ls.ValueId = e.LeadSourceValueId LEFT JOIN crm.PicklistValues lr ON lr.ValueId = e.LostReasonValueId`,
    insertExtra: { col: 'ClosedAt', expr: `CASE WHEN ${wonLost} THEN SYSUTCDATETIME() END` },
    updateExtra: { col: 'ClosedAt', expr: `CASE WHEN ${wonLost} THEN COALESCE(ClosedAt, SYSUTCDATETIME()) END` },
    clearLinks: ['UPDATE crm.Activities SET OpportunityId = NULL WHERE OpportunityId = @id'],
  },
  activities: {
    cols: `e.ActivityType, e.Subject, e.Description, e.AccountId, a.Name AS AccountName, e.ContactId, ${contactName('ct')} AS ContactName, e.OpportunityId, o.Name AS OpportunityName,
  CONVERT(char(10), e.DueDate, 23) AS DueDate, CONVERT(char(5), e.DueTime, 108) AS DueTime, e.IsDone, ${ts('e.DoneAt')} AS DoneAt`,
    joins: `LEFT JOIN crm.Accounts a ON a.AccountId = e.AccountId LEFT JOIN crm.Contacts ct ON ct.ContactId = e.ContactId LEFT JOIN crm.Opportunities o ON o.OpportunityId = e.OpportunityId`,
    insertExtra: { col: 'DoneAt', expr: 'CASE WHEN @IsDone = 1 THEN SYSUTCDATETIME() END' },
    updateExtra: { col: 'DoneAt', expr: 'CASE WHEN @IsDone = 1 THEN COALESCE(DoneAt, SYSUTCDATETIME()) END' },
    clearLinks: [],
  },
};

/** SQL condition for a whitelisted filter key/value, or null. Enum values are chosen from a fixed set, never echoed. */
export function filterClause(entity: EntityKey, key: string, value: string): string | null {
  const eq = (col: string) => `${col} = @f_${key}`;
  switch (`${entity}.${key}`) {
    case 'accounts.owner': case 'contacts.owner': case 'opportunities.owner': case 'activities.owner': return eq('e.OwnerId');
    case 'accounts.industry': return eq('e.IndustryValueId');
    case 'contacts.account': case 'opportunities.account': case 'activities.account': return eq('e.AccountId');
    case 'opportunities.stage': return eq('e.StageId');
    case 'opportunities.contact': return eq('e.PrimaryContactId');
    case 'opportunities.from': return 'e.CloseDate >= @f_from';
    case 'opportunities.to': return 'e.CloseDate <= @f_to';
    case 'opportunities.status': return value === 'all' ? null : 's.StageType = @f_status';
    case 'activities.type': return eq('e.ActivityType');
    case 'activities.contact': return eq('e.ContactId');
    case 'activities.opportunity': return eq('e.OpportunityId');
    case 'activities.status': return value === 'open' ? 'e.IsDone = 0' : value === 'done' ? 'e.IsDone = 1' : null;
    case 'activities.due':
      return value === 'overdue' ? 'e.IsDone = 0 AND e.DueDate < @today'
        : value === 'today' ? 'e.DueDate = @today'
        : value === 'week' ? 'e.DueDate >= @today AND e.DueDate <= DATEADD(day, 6, CAST(@today AS date))' : null;
    default: return null;
  }
}
