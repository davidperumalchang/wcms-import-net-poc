using StackExchange.Redis;
using WcmsImport.Common;

namespace WcmsImport.Importer;

/// <summary>
/// "Import Service": takes jobs off the Redis Stream and hands each one to the import worker.
///
/// OMITTED (production): a cap on delivery attempts so a job that always crashes the worker does not get
/// re-claimed forever (move it to a dead-letter stream), and a smarter heartbeat.
///
/// Why a Stream with a consumer group (and not Pub/Sub):
///   - a message stays "pending" until we XACK it, so a crash does not lose the job
///   - each job goes to ONE consumer, even when several importer containers are running
///   - a pending job that has been idle too long can be claimed by another consumer
///
/// StackExchange.Redis multiplexes one connection and does not support blocking reads (XREADGROUP BLOCK),
/// so an empty stream is polled once a second instead.
/// </summary>
public sealed class ImportService(IConnectionMultiplexer redis, IServiceScopeFactory scopes, IConfiguration config, ILoggerFactory loggers)
    : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(2);

    private readonly string _consumer = Environment.MachineName;
    private readonly long _claimIdleMs = config.GetValue("CLAIM_IDLE_MS", 8000L);
    private readonly ILogger _log = loggers.CreateLogger("importer");

    private sealed record Message(RedisValue Id, Guid? JobId);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var db = redis.GetDatabase();
        await ImportQueue.EnsureGroupAsync(db);
        _log.LogInformation("consumer {Consumer} ready", _consumer);

        // A job in progress is NOT cancelled on shutdown: it would be reported FAILED. The token is only checked between
        // jobs. If the process is stopped mid-job, the message stays pending and another worker resumes from the checkpoint.
        while (!stoppingToken.IsCancellationRequested)
        {
            var msg = await ClaimStaleAsync(db) ?? await ReadNewAsync(db);
            if (msg is null)
            {
                await Task.Delay(PollInterval, stoppingToken);
                continue;
            }

            if (msg.JobId is not { } jobId)
            {
                _log.LogWarning("stream entry {StreamId} has no valid job id, dropping it", msg.Id);
                await db.StreamAcknowledgeAsync(ImportQueue.Stream, ImportQueue.Group, msg.Id);
                continue;
            }

            _log.LogInformation("picked up job {JobId} (stream id {StreamId})", jobId, msg.Id);
            using var heartbeatStop = new CancellationTokenSource();
            var heartbeat = HeartbeatAsync(db, msg.Id, heartbeatStop.Token);
            try
            {
                using var scope = scopes.CreateScope();
                await scope.ServiceProvider.GetRequiredService<ImportWorker>().RunJobAsync(jobId);
                await db.StreamAcknowledgeAsync(ImportQueue.Stream, ImportQueue.Group, msg.Id); // acknowledge LAST, only after the job is closed in the database
            }
            catch (Exception e)
            {
                _log.LogError("job {JobId} failed, leaving it pending: {Error}", jobId, e.Message);
            }
            finally
            {
                await heartbeatStop.CancelAsync();
                await heartbeat;
            }
        }
    }

    // 1) jobs that someone started and never acknowledged (for example a crashed worker)
    private async Task<Message?> ClaimStaleAsync(IDatabase db)
    {
        var r = await db.StreamAutoClaimAsync(ImportQueue.Stream, ImportQueue.Group, _consumer, _claimIdleMs, "0-0", count: 1);
        return ToMessage(r.ClaimedEntries);
    }

    // 2) brand new jobs
    private async Task<Message?> ReadNewAsync(IDatabase db) =>
        ToMessage(await db.StreamReadGroupAsync(ImportQueue.Stream, ImportQueue.Group, _consumer, StreamPosition.NewMessages, count: 1));

    private static Message? ToMessage(StreamEntry[]? entries)
    {
        foreach (var e in entries ?? [])
        {
            if (e.Id.IsNull) continue; // empty or deleted entry
            return new Message(e.Id, Guid.TryParse(e[ImportQueue.JobIdField].ToString(), out var jobId) ? jobId : null);
        }
        return null;
    }

    // Heartbeat: re-claiming our own message resets its idle time, so a long job does not look abandoned.
    private async Task HeartbeatAsync(IDatabase db, RedisValue messageId, CancellationToken ct)
    {
        using var timer = new PeriodicTimer(HeartbeatInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(ct))
            {
                try
                {
                    await db.StreamClaimIdsOnlyAsync(ImportQueue.Stream, ImportQueue.Group, _consumer, 0, [messageId]);
                }
                catch (RedisException)
                {
                    // best effort, the next beat tries again
                }
            }
        }
        catch (OperationCanceledException)
        {
            // job finished
        }
    }
}
