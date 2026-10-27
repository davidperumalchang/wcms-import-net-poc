using System.Text.Json;
using System.Text.Json.Serialization;
using Npgsql;
using WcmsImport.Common;

namespace WcmsImport.Importer;

public static class JobStatus
{
    public const string Queued = "QUEUED";
    public const string Running = "RUNNING";
    public const string Completed = "COMPLETED";
    public const string CompletedWithErrors = "COMPLETED_WITH_ERRORS";
    public const string Failed = "FAILED";
}

/// <summary>Demo-only failure switches, set per job through POST /imports "options". See <see cref="Faults"/>.</summary>
public sealed record JobOptions(
    int? FailBatch = null,
    int? LostReplyBatch = null,
    int? FailClose = null,
    int? CrashAfterBatch = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] bool Crashed = false);

/// <summary>One row of the jobs table.</summary>
public sealed record Job(
    Guid Id,
    string Tenant,
    string Source,
    string Status,
    JobOptions Options,
    int? Total,
    int Checkpoint,
    int Processed,
    int Created,
    int Updated,
    int Unchanged,
    int Failed,
    string? Error)
{
    public static Job FromReader(NpgsqlDataReader r) => new(
        r.Get<Guid>("id"),
        r.Get<string>("tenant")!,
        r.Get<string>("source")!,
        r.Get<string>("status")!,
        JsonSerializer.Deserialize<JobOptions>(r.Get<string>("options") ?? "{}", Defaults.Json) ?? new JobOptions(),
        r.Get<int?>("total"),
        r.Get<int>("checkpoint"),
        r.Get<int>("processed"),
        r.Get<int>("created"),
        r.Get<int>("updated"),
        r.Get<int>("unchanged"),
        r.Get<int>("failed"),
        r.Get<string>("error"));
}
