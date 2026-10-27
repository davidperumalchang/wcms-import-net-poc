using System.Globalization;

namespace WcmsImport.Importer;

public enum BatchAction { Apply, Skip, Invalid }

public sealed record ImportSummary(int Created, int Updated, int Unchanged, int Failed);

public sealed record ImportEventData(Guid JobId, string JobStatus, ImportSummary Summary, string ChangedContentUrl);

/// <summary>The event stored in the outbox. Small on purpose: counts plus a link to fetch what changed.</summary>
public sealed record ImportEvent(string Id, string Type, string Version, string OccurredAt, string Tenant, ImportEventData Data);

/// <summary>
/// Job-level rules: pure functions that decide the final status and whether to notify.
/// Keeping the decisions here (and the SQL in ImportWorker) makes the rules easy to read and test.
/// </summary>
public static class JobRules
{
    public static readonly IReadOnlyList<string> FinalStatuses = [JobStatus.Completed, JobStatus.CompletedWithErrors, JobStatus.Failed];

    public static bool IsFinal(string status) => FinalStatuses.Contains(status);

    /// <summary>
    /// Decide what to do with a batch that starts at <paramref name="offset"/>, given the checkpoint stored in the job row.
    ///   stored == offset -> Apply    this batch has not been recorded yet
    ///   stored  > offset -> Skip     it was already committed (for example the commit worked but the worker never
    ///                                heard back and retried). Applying it again would count its items twice.
    ///   stored  < offset -> Invalid  the worker thinks it is ahead of the database. That is a bug, so stop.
    /// </summary>
    public static BatchAction DecideBatch(int stored, int offset) =>
        stored == offset ? BatchAction.Apply : stored > offset ? BatchAction.Skip : BatchAction.Invalid;

    /// <summary>Status when the job stops. A fatal error means we gave up part way.</summary>
    public static string FinalStatus(Job job, string? fatalError) =>
        fatalError is not null ? JobStatus.Failed
        : job.Failed > 0 ? JobStatus.CompletedWithErrors
        : JobStatus.Completed;

    /// <summary>
    /// Notify only if something was really saved. Counts come from the job row, i.e. the WHOLE job,
    /// so a job resumed after a crash still reports the batches saved before the crash.
    /// A FAILED job that saved some content still notifies, about the part that was saved.
    /// </summary>
    public static bool NeedsNotification(Job job) => job.Created + job.Updated > 0;

    public static ImportEvent BuildEvent(Job job, string status, string eventId, DateTimeOffset now) => new(
        Id: eventId,
        Type: "content.import.completed",
        Version: "1.0",
        OccurredAt: now.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture),
        Tenant: job.Tenant,
        Data: new ImportEventData(
            job.Id,
            status,
            new ImportSummary(job.Created, job.Updated, job.Unchanged, job.Failed),
            $"/content?importJobId={job.Id}"));
}
