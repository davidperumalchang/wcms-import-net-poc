using System.Text.Json;
using System.Text.Json.Nodes;
using WcmsImport.Common;
using WcmsImport.Importer;

namespace WcmsImport.UnitTests;

public class JobRulesTests
{
    private static readonly Guid JobId = Guid.Parse("00000000-0000-0000-0000-000000000001");

    private static Job MakeJob(int created = 5, int updated = 0, int unchanged = 0, int failed = 0) =>
        new(JobId, "acme", "wordpress", JobStatus.Running, new JobOptions(), Total: 50, Checkpoint: 0, Processed: 0,
            created, updated, unchanged, failed, Error: null);

    [Fact]
    public void No_failures_is_COMPLETED() => Assert.Equal(JobStatus.Completed, JobRules.FinalStatus(MakeJob(), null));

    [Fact]
    public void Some_failed_items_is_COMPLETED_WITH_ERRORS() =>
        Assert.Equal(JobStatus.CompletedWithErrors, JobRules.FinalStatus(MakeJob(failed: 2), null));

    [Fact]
    public void A_fatal_error_is_FAILED_even_if_items_were_saved() =>
        Assert.Equal(JobStatus.Failed, JobRules.FinalStatus(MakeJob(), "db down"));

    [Fact]
    public void Notify_when_something_was_created() => Assert.True(JobRules.NeedsNotification(MakeJob(created: 1)));

    [Fact]
    public void Notify_when_something_was_updated() => Assert.True(JobRules.NeedsNotification(MakeJob(created: 0, updated: 1)));

    [Fact]
    public void Do_not_notify_when_everything_was_unchanged_or_failed() =>
        Assert.False(JobRules.NeedsNotification(MakeJob(created: 0, updated: 0, unchanged: 50, failed: 3)));

    [Fact]
    public void A_FAILED_job_that_saved_content_still_notifies_about_what_was_saved()
    {
        var j = MakeJob(created: 40);
        Assert.Equal(JobStatus.Failed, JobRules.FinalStatus(j, "gave up"));
        Assert.True(JobRules.NeedsNotification(j));
    }

    [Theory]
    [InlineData(JobStatus.Completed, true)]
    [InlineData(JobStatus.CompletedWithErrors, true)]
    [InlineData(JobStatus.Failed, true)]
    [InlineData(JobStatus.Queued, false)]
    [InlineData(JobStatus.Running, false)]
    public void IsFinal_knows_the_end_states(string status, bool expected) => Assert.Equal(expected, JobRules.IsFinal(status));

    [Fact]
    public void Event_is_small_counts_and_a_link_not_content()
    {
        var e = JobRules.BuildEvent(MakeJob(created: 96, failed: 4), JobStatus.CompletedWithErrors, "evt_1", new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var actual = JsonNode.Parse(JsonSerializer.Serialize(e, Defaults.Json));
        var expected = JsonNode.Parse($$"""
            {
              "id": "evt_1", "type": "content.import.completed", "version": "1.0", "occurredAt": "2026-01-01T00:00:00.000Z", "tenant": "acme",
              "data": {
                "jobId": "{{JobId}}", "jobStatus": "COMPLETED_WITH_ERRORS",
                "summary": { "created": 96, "updated": 0, "unchanged": 0, "failed": 4 },
                "changedContentUrl": "/content?importJobId={{JobId}}"
              }
            }
            """);
        Assert.True(JsonNode.DeepEquals(expected, actual), actual?.ToJsonString());
    }

    [Fact]
    public void OccurredAt_is_always_UTC()
    {
        var e = JobRules.BuildEvent(MakeJob(), JobStatus.Completed, "evt_1", new DateTimeOffset(2026, 1, 1, 8, 0, 0, TimeSpan.FromHours(8)));
        Assert.Equal("2026-01-01T00:00:00.000Z", e.OccurredAt);
    }

    [Fact]
    public void A_batch_that_starts_at_the_checkpoint_is_applied() => Assert.Equal(BatchAction.Apply, JobRules.DecideBatch(20, 20));

    [Theory]
    [InlineData(30, 20)] // the retry of batch 3 after its commit succeeded
    [InlineData(50, 0)]
    public void A_batch_behind_the_checkpoint_was_already_committed_so_skip_it(int stored, int offset) =>
        Assert.Equal(BatchAction.Skip, JobRules.DecideBatch(stored, offset));

    [Fact]
    public void A_batch_ahead_of_the_checkpoint_is_a_bug() => Assert.Equal(BatchAction.Invalid, JobRules.DecideBatch(10, 20));
}
