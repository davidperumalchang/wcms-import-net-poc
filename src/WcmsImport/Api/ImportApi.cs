using System.Text.Json;
using Npgsql;
using StackExchange.Redis;
using WcmsImport.Common;
using WcmsImport.Importer;

namespace WcmsImport.Api;

/// <summary>Demo-only failure switches a caller may set (see <see cref="Faults"/>).</summary>
public sealed record ImportOptions(int? FailBatch, int? LostReplyBatch, int? FailClose, int? CrashAfterBatch)
{
    private const int Max = 1000;

    public bool IsValid() => new[] { FailBatch, LostReplyBatch, FailClose, CrashAfterBatch }.All(v => v is null or (>= 1 and <= Max));

    public JobOptions ToJobOptions() => new(FailBatch, LostReplyBatch, FailClose, CrashAfterBatch);
}

public sealed record CreateImportRequest(string? Source, ImportOptions? Options);

/// <summary>
/// "API to Import" + "Job Queue Service"
///
/// OMITTED (production): authentication and authorisation (the tenant would come from the caller's token),
/// an Idempotency-Key header on POST /imports (a client retry currently creates a second job), pagination,
/// rate limiting, a cancel endpoint, and a sweeper that re-enqueues jobs stuck in QUEUED (covers a crash
/// between the INSERT and the XADD below).
/// GET /outbox and GET /content without a job id are demo helpers and would not be public.
///
/// POST /imports does two things, in this order:
///   1. record the job in Postgres (status QUEUED)   &lt;- the database is the source of truth
///   2. add the job id to the Redis Stream           &lt;- the stream only says "someone should work on this"
/// </summary>
public static class ImportApi
{
    public const int DefaultPort = 3000;

    public static async Task RunAsync(string[] args)
    {
        var builder = Hosting.CreateWebBuilder(args, DefaultPort);
        builder.Services.AddPostgres(builder.Configuration).AddRedis(builder.Configuration);

        var app = builder.Build();
        var log = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("api");
        await ImportQueue.EnsureGroupAsync(app.Services.GetRequiredService<IConnectionMultiplexer>().GetDatabase());

        // Details go to the log (the middleware logs the exception); the caller gets a generic message.
        app.UseExceptionHandler(e => e.Run(ctx =>
        {
            ctx.Response.StatusCode = StatusCodes.Status500InternalServerError;
            return ctx.Response.WriteAsJsonAsync(new { error = "internal error" });
        }));

        MapEndpoints(app, log);
        log.LogInformation("listening on :{Port}", builder.Configuration.GetValue("PORT", DefaultPort));
        await app.RunAsync();
    }

    private static void MapEndpoints(WebApplication app, ILogger log)
    {
        // Start an import. Returns right away; the work happens in the importer service.
        app.MapPost("/imports", async (CreateImportRequest body, NpgsqlDataSource db, IConnectionMultiplexer redis) =>
        {
            if (body.Source is null || !SourceMappers.All.ContainsKey(body.Source))
                return Results.BadRequest(new { error = $"source must be one of {string.Join(", ", SourceMappers.All.Keys)}" });
            if (body.Options is { } o && !o.IsValid())
                return Results.BadRequest(new { error = "options must be whole numbers between 1 and 1000" });

            var id = Guid.NewGuid();
            var options = JsonSerializer.Serialize(body.Options?.ToJobOptions() ?? new JobOptions(), Defaults.Json);
            await db.Command("INSERT INTO jobs (id, tenant, source, status, options) VALUES ($1, $2, $3, $4, $5::jsonb)",
                id, Defaults.Tenant, body.Source, JobStatus.Queued, options).RunAsync();
            await redis.GetDatabase().StreamAddAsync(ImportQueue.Stream, ImportQueue.JobIdField, id.ToString());
            log.LogInformation("job {JobId} queued ({Source})", id, body.Source);
            return Results.Accepted($"/imports/{id}", new { jobId = id, status = JobStatus.Queued, statusUrl = $"/imports/{id}" });
        });

        // Job status, counts and notification status
        app.MapGet("/imports/{id:guid}", async (Guid id, NpgsqlDataSource db) =>
        {
            var job = (await db.Command("SELECT * FROM jobs WHERE id = $1", id).RowsAsync()).FirstOrDefault();
            if (job is null) return Results.NotFound(new { error = "job not found" });
            var notification = (await db.Command("SELECT id, status, attempts, last_error FROM outbox WHERE job_id = $1", id).RowsAsync()).FirstOrDefault();
            return Results.Ok(new { job, notification });
        });

        // Per-item errors and warnings
        app.MapGet("/imports/{id:guid}/errors", async (Guid id, NpgsqlDataSource db) =>
            Results.Ok(await db.Command("SELECT source_id, level, message FROM job_errors WHERE job_id = $1 ORDER BY id", id).RowsAsync()));

        // The "pull link" that upstream systems use to see what changed
        app.MapGet("/content", async (Guid? importJobId, NpgsqlDataSource db) =>
        {
            var items = await db.Command(
                "SELECT source_id, title, slug, status FROM contents WHERE ($1::uuid IS NULL OR job_id = $1::uuid) ORDER BY source_id::int",
                importJobId).RowsAsync();
            return Results.Ok(new { count = items.Count, items });
        });

        // Demo helper: look at the outbox table
        app.MapGet("/outbox", async (NpgsqlDataSource db) =>
            Results.Ok(await db.Command("SELECT id, job_id, status, attempts, next_attempt_at, last_error FROM outbox ORDER BY created_at").RowsAsync()));

        app.MapFallback(() => Results.NotFound(new
        {
            error = "not found",
            @try = new[] { "POST /imports", "GET /imports/:id", "GET /imports/:id/errors", "GET /content?importJobId=", "GET /outbox" },
        }));
    }
}
