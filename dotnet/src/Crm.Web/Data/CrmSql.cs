namespace Crm.Web.Data;

/// <summary>Static SQL fragments per entity (port of crmSql.ts). No user input is ever concatenated: keys are whitelisted in EntityDefinitions.</summary>
public sealed record SqlDef(string Cols, string Joins, (string Col, string Expr)? InsertExtra, (string Col, string Expr)? UpdateExtra, string[] ClearLinks);

public static class CrmSql
{
    private static string Ts(string c) => $"CONVERT(varchar(23), {c}, 126) + 'Z'";

    public static readonly string CommonCols = $"""
        e.OwnerId, e.TeamId, uo.DisplayName AS OwnerName, e.CreatedBy, uc.DisplayName AS CreatedByName, {Ts("e.CreatedAt")} AS CreatedAt,
          e.ModifiedBy, um.DisplayName AS ModifiedByName, {Ts("e.ModifiedAt")} AS ModifiedAt, e.RowVer AS RowVer
        """;

    public const string CommonJoins = "JOIN crm.Users uo ON uo.UserId = e.OwnerId JOIN crm.Users uc ON uc.UserId = e.CreatedBy JOIN crm.Users um ON um.UserId = e.ModifiedBy";

    private static string ContactName(string a) => $"LTRIM(CONCAT({a}.FirstName, N' ', {a}.LastName))";

    private const string WonLost = "EXISTS (SELECT 1 FROM crm.PipelineStages WHERE StageId = @StageId AND StageType IN ('won','lost'))";

    public static readonly IReadOnlyDictionary<string, SqlDef> Entities = new Dictionary<string, SqlDef>(StringComparer.Ordinal)
    {
        ["accounts"] = new(
            "e.Name, e.Website, e.Phone, e.IndustryValueId, iv.Label AS IndustryLabel, iv.Code AS IndustryCode, e.Street, e.PostalCode, e.City, e.CountryCode, e.Notes",
            "LEFT JOIN crm.PicklistValues iv ON iv.ValueId = e.IndustryValueId",
            null, null,
            ["UPDATE crm.Contacts SET AccountId = NULL WHERE AccountId = @id", "UPDATE crm.Activities SET AccountId = NULL WHERE AccountId = @id"]),
        ["contacts"] = new(
            "e.AccountId, a.Name AS AccountName, e.FirstName, e.LastName, e.JobTitle, e.Email, e.Phone, e.Mobile",
            "LEFT JOIN crm.Accounts a ON a.AccountId = e.AccountId",
            null, null,
            ["UPDATE crm.Opportunities SET PrimaryContactId = NULL WHERE PrimaryContactId = @id", "UPDATE crm.Activities SET ContactId = NULL WHERE ContactId = @id"]),
        ["opportunities"] = new(
            $"""
            e.Name, e.AccountId, a.Name AS AccountName, e.PrimaryContactId, {ContactName("ct")} AS PrimaryContactName, e.StageId, s.Name AS StageName, s.StageType, s.Probability, s.SortOrder AS StageSort,
              e.Amount, e.CurrencyCode, CONVERT(char(10), e.CloseDate, 23) AS CloseDate, e.LeadSourceValueId, ls.Label AS LeadSourceLabel, e.LostReasonValueId, lr.Label AS LostReasonLabel, {Ts("e.ClosedAt")} AS ClosedAt
            """,
            """
            LEFT JOIN crm.Accounts a ON a.AccountId = e.AccountId LEFT JOIN crm.Contacts ct ON ct.ContactId = e.PrimaryContactId JOIN crm.PipelineStages s ON s.StageId = e.StageId
              LEFT JOIN crm.PicklistValues ls ON ls.ValueId = e.LeadSourceValueId LEFT JOIN crm.PicklistValues lr ON lr.ValueId = e.LostReasonValueId
            """,
            ("ClosedAt", $"CASE WHEN {WonLost} THEN SYSUTCDATETIME() END"),
            ("ClosedAt", $"CASE WHEN {WonLost} THEN COALESCE(ClosedAt, SYSUTCDATETIME()) END"),
            ["UPDATE crm.Activities SET OpportunityId = NULL WHERE OpportunityId = @id"]),
        ["activities"] = new(
            $"""
            e.ActivityType, e.Subject, e.Description, e.AccountId, a.Name AS AccountName, e.ContactId, {ContactName("ct")} AS ContactName, e.OpportunityId, o.Name AS OpportunityName,
              CONVERT(char(10), e.DueDate, 23) AS DueDate, CONVERT(char(5), e.DueTime, 108) AS DueTime, e.IsDone, {Ts("e.DoneAt")} AS DoneAt
            """,
            "LEFT JOIN crm.Accounts a ON a.AccountId = e.AccountId LEFT JOIN crm.Contacts ct ON ct.ContactId = e.ContactId LEFT JOIN crm.Opportunities o ON o.OpportunityId = e.OpportunityId",
            ("DoneAt", "CASE WHEN @IsDone = 1 THEN SYSUTCDATETIME() END"),
            ("DoneAt", "CASE WHEN @IsDone = 1 THEN COALESCE(DoneAt, SYSUTCDATETIME()) END"),
            []),
    };

    /// <summary>SQL condition for a whitelisted filter key/value, or null. Enum values are chosen from a fixed set, never echoed.</summary>
    public static string? FilterClause(string entity, string key, string value)
    {
        string Eq(string col) => $"{col} = @f_{key}";
        switch ($"{entity}.{key}")
        {
            case "accounts.owner" or "contacts.owner" or "opportunities.owner" or "activities.owner": return Eq("e.OwnerId");
            case "accounts.industry": return Eq("e.IndustryValueId");
            case "contacts.account" or "opportunities.account" or "activities.account": return Eq("e.AccountId");
            case "opportunities.stage": return Eq("e.StageId");
            case "opportunities.contact": return Eq("e.PrimaryContactId");
            case "opportunities.from": return "e.CloseDate >= @f_from";
            case "opportunities.to": return "e.CloseDate <= @f_to";
            case "opportunities.status": return value == "all" ? null : value == "closed" ? "s.StageType IN ('won', 'lost')" : "s.StageType = @f_status";
            case "activities.type": return value == "actionable" ? "e.ActivityType <> 'note'" : Eq("e.ActivityType");
            case "activities.contact": return Eq("e.ContactId");
            case "activities.opportunity": return Eq("e.OpportunityId");
            case "activities.status": return value == "open" ? "e.IsDone = 0" : value == "done" ? "e.IsDone = 1" : null;
            case "activities.due":
                return value == "overdue" ? "e.IsDone = 0 AND e.DueDate < @today"
                    : value == "due" ? "e.IsDone = 0 AND e.DueDate <= @today"
                    : value == "today" ? "e.DueDate = @today"
                    : value == "week" ? "e.DueDate > @today AND e.DueDate <= DATEADD(day, 7, CAST(@today AS date))" : null;
            default: return null;
        }
    }
}
