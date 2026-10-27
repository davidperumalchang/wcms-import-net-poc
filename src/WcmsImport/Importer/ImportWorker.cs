using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using Npgsql;
using WcmsImport.Common;

namespace WcmsImport.Importer;

/// <summary>
/// "Import worker": transform -> validate -> load, one batch per database transaction.
///
/// Transaction layout (see docs/DESIGN.md):
///   - each batch: pages + checkpoint + counters commit together, or not at all
///   - final step: job status + outbox row commit together, or not at all
/// A retry after any failure is safe because LoadAsync is idempotent (keyed by tenant, source, source_id).
///
/// OMITTED (production):
///   - per-tenant concurrency limits and fair scheduling (one tenant could occupy every importer)
///   - cancel endpoint, job timeout, and a lease on the job row. Today Redis is the only guard against two
///     workers running the same job; a lease with fencing on the job row would make that airtight
///   - the closing step is retried 3 times in-process; after that it relies on Redis re-delivery (no alert, no dead-letter)
///   - a limit on how many times a crashing job may be re-claimed (a poison job would loop forever)
///   - metrics and tracing; reading very large pages as a stream
///   - media/assets, custom-field mapping, link rewriting
/// </summary>
public sealed class ImportWorker(NpgsqlDataSource db, SourceClient source, IConfiguration config, ILoggerFactory loggers)
{
    private static readonly TimeSpan BatchDelay = TimeSpan.FromMilliseconds(400); // slows the demo down so you can watch progress

    private readonly int _batchSize = config.GetValue("BATCH_SIZE", 10);
    private readonly ILogger _log = loggers.CreateLogger("worker");

    private enum LoadOutcome { Created, Updated, Unchanged }

    private sealed class BatchCounts
    {
        public int Created, Updated, Unchanged, Failed;

        public void Add(LoadOutcome outcome)
        {
            switch (outcome)
            {
                case LoadOutcome.Created: Created++; break;
                case LoadOutcome.Updated: Updated++; break;
                case LoadOutcome.Unchanged: Unchanged++; break;
            }
        }
    }

    private static Task AddIssueAsync(NpgsqlConnection conn, Guid jobId, string sourceId, string level, string message) =>
        conn.Command("INSERT INTO job_errors (job_id, source_id, level, message) VALUES ($1,$2,$3,$4)", jobId, sourceId, level, message).RunAsync();

    // ---------- load: idempotent save keyed by (tenant, source, source_id) ----------
    private static async Task<LoadOutcome> LoadAsync(NpgsqlConnection conn, Job job, SourceItem item, List<string> warnings)
    {
        var owner = await conn.Command("SELECT source, source_id FROM contents WHERE tenant=$1 AND slug=$2", Defaults.Tenant, item.Slug)
            .FirstOrDefaultAsync(r => new SlugOwner(r.GetString(0), r.GetString(1)));
        var decision = Validation.UniqueSlug(item, owner, job.Source);
        if (decision.Warning is not null) warnings.Add(decision.Warning);
        item = item with { Slug = decision.Slug };

        var hash = Validation.Fingerprint(item);
        var existing = await conn.Command("SELECT hash FROM contents WHERE tenant=$1 AND source=$2 AND source_id=$3", Defaults.Tenant, job.Source, item.SourceId)
            .FirstOrDefaultAsync(r => r.GetString(0));

        if (existing is null)
        {
            await conn.Command(
                "INSERT INTO contents (tenant, source, source_id, title, slug, body, status, hash, job_id) VALUES ($1,$2,$3,$4,$5,$6,$7,$8,$9)",
                Defaults.Tenant, job.Source, item.SourceId, item.Title, item.Slug, item.Body, item.Status, hash, job.Id).RunAsync();
            return LoadOutcome.Created;
        }
        if (existing == hash) return LoadOutcome.Unchanged;
        await conn.Command(
            "UPDATE contents SET title=$4, slug=$5, body=$6, status=$7, hash=$8, job_id=$9, updated_at=now() WHERE tenant=$1 AND source=$2 AND source_id=$3",
            Defaults.Tenant, job.Source, item.SourceId, item.Title, item.Slug, item.Body, item.Status, hash, job.Id).RunAsync();
        return LoadOutcome.Updated;
    }

    // ---------- one batch = one transaction ----------
    // The job row is locked first and its checkpoint compared with where this batch starts. That makes the batch
    // safe to repeat: if an earlier attempt already committed (the commit worked but the reply was lost), the
    // checkpoint is ahead of `offset`, the batch is skipped, and its counters are NOT added a second time.
    // The lock also stops two workers from applying the same batch at the same time.
    private async Task<int> SaveBatchAsync(Job job, int offset, IReadOnlyList<SourceItem> items, int batchNo, int attempt)
    {
        await using var conn = await db.OpenConnectionAsync();
        await using var tx = await conn.BeginTransactionAsync(); // disposed without commit = rolled back
        var n = new BatchCounts();

        var stored = await conn.Command("SELECT checkpoint FROM jobs WHERE id=$1 FOR UPDATE", job.Id).FirstOrDefaultAsync(r => r.GetInt32(0));
        switch (JobRules.DecideBatch(stored, offset))
        {
            case BatchAction.Invalid:
                throw new InvalidOperationException($"checkpoint {stored} is behind this batch (starts at {offset})");
            case BatchAction.Skip:
                await tx.RollbackAsync(); // nothing to do, release the lock
                _log.LogInformation("batch {BatchNo} was already committed (checkpoint={Checkpoint}), skipping it", batchNo, stored);
                return stored;
        }

        foreach (var raw in items)
        {
            var (item, errors, warnings) = Validation.Validate(raw);
            if (errors.Count > 0)
            {
                n.Failed++;
                foreach (var m in errors) await AddIssueAsync(conn, job.Id, raw.SourceId, "error", m);
                continue;
            }
            // A savepoint lets the database undo ONE page without undoing the whole batch.
            await tx.SaveAsync("item");
            var itemWarnings = warnings.ToList();
            try
            {
                n.Add(await LoadAsync(conn, job, item, itemWarnings));
                await tx.ReleaseAsync("item");
                foreach (var m in itemWarnings) await AddIssueAsync(conn, job.Id, raw.SourceId, "warning", m);
            }
            catch (PostgresException e)
            {
                await tx.RollbackAsync("item");
                n.Failed++;
                await AddIssueAsync(conn, job.Id, raw.SourceId, "error", $"database rejected item: {e.MessageText}");
            }
        }

        if (Faults.ShouldFailBatch(job, batchNo, attempt)) throw new InvalidOperationException("simulated deadlock"); // demo only, see Faults

        var next = offset + items.Count;
        await conn.Command(
            "UPDATE jobs SET checkpoint=$2, processed=processed+$3, created=created+$4, updated=updated+$5, unchanged=unchanged+$6, failed=failed+$7, updated_at=now() WHERE id=$1",
            job.Id, next, items.Count, n.Created, n.Updated, n.Unchanged, n.Failed).RunAsync();
        await tx.CommitAsync();
        return next;
    }

    // Retry with a growing wait. OMITTED (production): telling temporary errors (deadlock, timeout) from permanent
    // ones. Here every error is retried 3 times; permanent per-item errors are already handled by the savepoint.
    private async Task<T> WithRetryAsync<T>(string label, Func<int, Task<T>> fn, int tries = 3)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await fn(attempt);
            }
            catch (Exception e) when (attempt < tries)
            {
                var wait = TimeSpan.FromSeconds(Math.Pow(2, attempt - 1));
                _log.LogWarning("{Label} failed ({Error}), attempt {Attempt}/{Tries}, retrying in {Wait}s", label, e.Message, attempt, tries, wait.TotalSeconds);
                await Task.Delay(wait);
            }
        }
    }

    // ---------- final transaction: close the job AND write the notification row together ----------
    // Safe to repeat: if the job is already closed (an earlier attempt committed but its reply was lost), this does
    // nothing. That is what lets RunJobAsync retry it without risking a second outbox row or a changed status.
    private async Task<string> CloseJobAsync(Guid jobId, string? fatalError, int attempt)
    {
        await using var conn = await db.OpenConnectionAsync();
        await using var tx = await conn.BeginTransactionAsync();
        var job = await conn.Command("SELECT * FROM jobs WHERE id=$1 FOR UPDATE", jobId).FirstOrDefaultAsync(Job.FromReader)
            ?? throw new InvalidOperationException($"job {jobId} disappeared");
        if (JobRules.IsFinal(job.Status))
        {
            await tx.CommitAsync(); // already closed, nothing to do
            return job.Status;
        }

        var status = JobRules.FinalStatus(job, fatalError);
        await conn.Command("UPDATE jobs SET status=$2, error=$3, updated_at=now() WHERE id=$1", jobId, status, fatalError).RunAsync();

        if (JobRules.NeedsNotification(job))
        {
            var eventId = "evt_" + RandomNumberGenerator.GetHexString(8, lowercase: true);
            var payload = JsonSerializer.Serialize(JobRules.BuildEvent(job, status, eventId, DateTimeOffset.UtcNow), Defaults.Json);
            // UNIQUE(job_id): even if this step runs twice, there is still only one event per job.
            await conn.Command("INSERT INTO outbox (id, job_id, payload) VALUES ($1,$2,$3::jsonb) ON CONFLICT (job_id) DO NOTHING", eventId, jobId, payload).RunAsync();
            _log.LogInformation("job {JobId} {Status}; outbox row {EventId} saved in the same transaction", jobId, status, eventId);
        }
        else
        {
            _log.LogInformation("job {JobId} {Status}; nothing changed, no notification", jobId, status);
        }
        if (Faults.ShouldFailClose(job, attempt)) throw new InvalidOperationException("simulated failure of the closing transaction"); // demo only, see Faults
        await tx.CommitAsync();
        return status;
    }

    // ---------- run (or resume) one job ----------
    public async Task RunJobAsync(Guid jobId)
    {
        var job = await db.Command("SELECT * FROM jobs WHERE id=$1", jobId).FirstOrDefaultAsync(Job.FromReader);
        if (job is null) { _log.LogWarning("job {JobId} not found, skipping", jobId); return; }
        if (JobRules.IsFinal(job.Status)) { _log.LogInformation("job {JobId} already {Status}, skipping", jobId, job.Status); return; }

        await db.Command("UPDATE jobs SET status=$2, updated_at=now() WHERE id=$1", jobId, JobStatus.Running).RunAsync();
        var offset = job.Checkpoint; // 0 for a new job, or where a crashed worker stopped
        _log.LogInformation("{Mode} job {JobId} at item {Offset}", offset > 0 ? "RESUMING" : "starting", jobId, offset);

        // Two phases, kept apart on purpose. Only a failure in the WORK phase (reading and saving batches) marks the job
        // FAILED. A failure of the closing step must never do that: by then every batch is saved, so reporting FAILED
        // would be wrong. The closing step is retried instead, and if it still fails the error is thrown to the caller,
        // which leaves the Redis message pending so another worker can claim it and finish closing (every batch is
        // already done, so it goes straight to this step).
        string? fatalError = null;
        try
        {
            var mapper = SourceMappers.All.TryGetValue(job.Source, out var m) ? m : throw new InvalidOperationException($"unknown source {job.Source}");
            int? total = null;
            while (total is null || offset < total)
            {
                var page = await WithRetryAsync("read from source", _ => source.FetchPageAsync(mapper, offset, _batchSize));
                total = page.Total;
                await db.Command("UPDATE jobs SET total=$2 WHERE id=$1", jobId, total).RunAsync();
                if (page.Items.Count == 0) break;

                var batchNo = offset / _batchSize + 1;
                var start = offset;
                offset = await WithRetryAsync($"batch {batchNo}", async attempt =>
                {
                    var next = await SaveBatchAsync(job, start, page.Items, batchNo, attempt);
                    if (Faults.ShouldLoseReply(job, batchNo, attempt)) throw new InvalidOperationException("simulated lost commit reply"); // demo only, see Faults
                    return next;
                });
                _log.LogInformation("batch {BatchNo} committed, checkpoint={Checkpoint}/{Total}", batchNo, offset, total);

                if (Faults.ShouldCrash(job, batchNo)) // demo only, see Faults
                {
                    await db.Command("""UPDATE jobs SET options = options || '{"crashed":true}'::jsonb WHERE id=$1""", jobId).RunAsync();
                    // Written directly because the console logger is asynchronous and would not flush before the kill.
                    Console.Error.WriteLine($"[worker] !!! simulated crash after batch {batchNo} !!!");
                    Process.GetCurrentProcess().Kill();
                }
                await Task.Delay(BatchDelay);
            }
        }
        catch (Exception e)
        {
            fatalError = e.Message;
            _log.LogError("job {JobId} gave up: {Error}", jobId, e.Message);
        }
        await WithRetryAsync("close job", attempt => CloseJobAsync(jobId, fatalError, attempt));
    }
}
