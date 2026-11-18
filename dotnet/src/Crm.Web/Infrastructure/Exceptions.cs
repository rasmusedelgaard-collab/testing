namespace Crm.Web.Infrastructure;

/// <summary>Application errors with an HTTP meaning; the error page renders them.</summary>
public abstract class AppException(int status, string message, Exception? inner = null) : Exception(message, inner)
{
    public int Status { get; } = status;
}

public sealed class ForbiddenException(string message = "Forbidden", Exception? inner = null) : AppException(403, message, inner);

public sealed class NotFoundException(string message = "Not found") : AppException(404, message);

/// <summary>FK violation (547) or a business conflict.</summary>
public class ConflictException(string message, Exception? inner = null) : AppException(409, message, inner);

/// <summary>Optimistic-concurrency miss: the row changed since the form was loaded. RecordPath links to the current version.</summary>
public sealed class EditConflictException(string recordPath) : ConflictException("Someone else changed this record while you were editing.")
{
    public string RecordPath { get; } = recordPath;
}

/// <summary>Unique-constraint violation (2627/2601), surfaced as a field-level validation error.</summary>
public sealed class UniqueViolationException(string message, Exception? inner = null) : AppException(422, message, inner);

/// <summary>Database paused/unavailable (transient error after retries, or after the work started).</summary>
public sealed class DbWakingException(Exception? inner = null) : AppException(503, "Database unavailable", inner);
