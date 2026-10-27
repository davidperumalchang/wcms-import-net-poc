using System.Net;
using System.Text.Json;

namespace WcmsImport.E2ETests;

// End-to-end checks against the RUNNING stack. Start it first:  docker compose up --build -d
// Run:  dotnet test tests/WcmsImport.E2ETests
//
// Test 6 uses the lostReplyBatch switch and test 7 the failClose switch (src/WcmsImport/Importer/Faults.cs).
// Test 6 proves a repeated batch is not counted twice. Test 7 proves a failing closing step does not mark the job FAILED.
// These tests share one database and run in order, because each one builds on the content the last one saved.
// Start from a clean stack for repeatable results:  docker compose down -v && docker compose up --build -d
//
// OMITTED: running the stack from the test (Testcontainers), parallel isolation, and automating the crash
// scenario (needs a restart policy and waiting out the Redis claim timeout, so it stays a manual demo).
[TestCaseOrderer("WcmsImport.E2ETests.ByNameOrderer", "WcmsImport.E2ETests")]
public class PipelineTests(Stack stack) : IClassFixture<Stack>
{
    private static bool HasIssue(JsonElement[] errors, string level, string text) =>
        errors.Any(e => e.Str("level") == level && e.Str("message")!.Contains(text));

    [Fact]
    public async Task T1_a_first_import_saves_good_pages_rejects_bad_ones_and_fixes_small_problems()
    {
        var (id, job) = await stack.RunImportAsync("wordpress");
        Assert.Equal("COMPLETED_WITH_ERRORS", job.Str("status")); // the mock CMS contains pages with no title / too large
        Assert.Equal(50, job.Int("total"));
        Assert.Equal(50, job.Int("processed"));
        Assert.Equal(50, job.Int("created") + job.Int("failed"));
        Assert.True(job.Int("created") > 40 && job.Int("failed") > 0);

        JsonElement[] errors = [.. (await stack.GetAsync($"{Stack.Api}/imports/{id}/errors")).Body.EnumerateArray()];
        Assert.True(HasIssue(errors, "error", "title is required"), "missing title is reported");
        Assert.True(HasIssue(errors, "warning", "script tag removed"), "script removal is reported");
        Assert.True(HasIssue(errors, "warning", "already used"), "slug clash is reported");

        var content = (await stack.GetAsync($"{Stack.Api}/content?importJobId={id}")).Body;
        Assert.Equal(job.Int("created"), content.Int("count"));
        Assert.All(content.GetProperty("items").EnumerateArray(),
            c => Assert.DoesNotContain("<script", c.Str("title") + c.Str("slug"), StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task T2_the_notification_row_exists_and_was_delivered_to_upstream_exactly_once()
    {
        var ob = await stack.WaitForAsync(async () =>
        {
            var rows = await stack.OutboxAsync();
            return rows.Length == 1 && rows[0].Str("status") == "sent" ? rows : null;
        }, "the notifier to deliver the event");
        Assert.Single(ob); // one event per job

        var r = await stack.ReceivedAsync();
        Assert.Equal(1, r.Int("count"));
        Assert.Equal(0, r.Int("duplicatesIgnored"));
        var evt = r.GetProperty("events")[0];
        Assert.Equal("content.import.completed", evt.Str("type"));
        Assert.True(evt.GetProperty("data").GetProperty("summary").Int("created") > 40);
    }

    [Fact]
    public async Task T3_importing_again_changes_nothing_and_sends_no_new_notification()
    {
        var before = await stack.ContentCountAsync();
        var (_, job) = await stack.RunImportAsync("wordpress");
        Assert.Equal(0, job.Int("created"));
        Assert.Equal(0, job.Int("updated"));
        Assert.True(job.Int("unchanged") > 40);
        Assert.Equal(before, await stack.ContentCountAsync()); // no duplicates

        await Task.Delay(3000); // give a wrongly created event time to appear
        Assert.Single(await stack.OutboxAsync()); // no new outbox row
        Assert.Equal(1, (await stack.ReceivedAsync()).Int("count")); // upstream heard nothing new
    }

    [Fact]
    public async Task T4_after_the_source_is_edited_only_the_edited_pages_are_updated()
    {
        await stack.PostAsync($"{Stack.Cms}/edit");
        var (_, job) = await stack.RunImportAsync("wordpress");
        Assert.Equal(4, job.Int("updated"));
        Assert.Equal(0, job.Int("created"));
        await stack.WaitForAsync<object>(async () => (await stack.ReceivedAsync()).Int("count") == 2 ? true : null, "the second notification");
    }

    [Fact]
    public async Task T5_a_batch_that_fails_once_is_rolled_back_and_retried_with_no_duplicates_and_correct_counts()
    {
        var before = await stack.ContentCountAsync();
        var (_, job) = await stack.RunImportAsync("flat", new { failBatch = 3 }); // flat uses new source ids, so every page is new
        Assert.Equal(50, job.Int("processed"));
        Assert.Equal(50, job.Int("created") + job.Int("failed") + job.Int("unchanged") + job.Int("updated"));
        var after = await stack.ContentCountAsync();
        Assert.Equal(job.Int("created"), after - before); // rows added equals the created count, so batch 3 was not saved twice
        // Let this job's notification land before the next test arms an upstream outage, or it would eat the failures.
        await stack.WaitForAsync<object>(async () => (await stack.ReceivedAsync()).Int("count") == 3 ? true : null, "the third notification");
    }

    [Fact]
    public async Task T6_when_a_commit_succeeds_but_the_reply_is_lost_the_retry_skips_the_batch_and_counts_stay_right()
    {
        // Content is already imported, so every page is unchanged. If batch 3 were applied twice,
        // its 10 items would be counted twice and processed would be 60 instead of 50.
        var (_, job) = await stack.RunImportAsync("wordpress", new { lostReplyBatch = 3 });
        Assert.Equal(50, job.Int("total"));
        Assert.Equal(50, job.Int("processed")); // each item counted once
        Assert.Equal(50, job.Int("unchanged") + job.Int("failed"));
        Assert.Equal(0, job.Int("created") + job.Int("updated"));
    }

    [Fact]
    public async Task T7_when_the_closing_transaction_fails_it_is_retried_and_the_job_is_NOT_reported_as_failed()
    {
        await stack.PostAsync($"{Stack.Cms}/edit"); // so there is something to notify about
        var sentBefore = (await stack.ReceivedAsync()).Int("count");
        var (id, job) = await stack.RunImportAsync("wordpress", new { failClose = 2 }); // closing fails twice, then succeeds
        Assert.NotEqual("FAILED", job.Str("status")); // all batches were saved, so the job must not be marked FAILED
        Assert.Equal(JsonValueKind.Null, job.GetProperty("error").ValueKind);
        Assert.Equal(50, job.Int("processed"));
        Assert.Equal(4, job.Int("updated"));

        var rows = (await stack.OutboxAsync()).Where(r => r.Str("job_id") == id).ToArray();
        Assert.Single(rows); // the failed attempts rolled back, so there is exactly one outbox row
        await stack.WaitForAsync<object>(async () => (await stack.ReceivedAsync()).Int("count") == sentBefore + 1 ? true : null, "the notification for this job");
    }

    [Fact]
    public async Task T8_when_upstream_is_down_the_notifier_retries_then_delivers()
    {
        await stack.PostAsync($"{Stack.Cms}/edit");
        await stack.PostAsync($"{Stack.Upstream}/fail?next=2");
        var sentBefore = (await stack.ReceivedAsync()).Int("count");
        var (id, _) = await stack.RunImportAsync("wordpress");

        var row = await stack.WaitForAsync<JsonElement?>(async () =>
        {
            var r = (await stack.OutboxAsync()).FirstOrDefault(x => x.Str("job_id") == id);
            return r.ValueKind == JsonValueKind.Object && r.Str("status") == "sent" ? r : null;
        }, "the retried delivery", 60_000);
        Assert.True(row!.Value.Int("attempts") >= 3, $"expected at least 3 attempts, got {row.Value.Int("attempts")}");
        Assert.Equal(sentBefore + 1, (await stack.ReceivedAsync()).Int("count"));
    }

    [Fact]
    public async Task T9_an_unknown_source_is_rejected_before_any_job_is_created()
    {
        var (status, _) = await stack.PostAsync($"{Stack.Api}/imports", new { source = "nope" });
        Assert.Equal(HttpStatusCode.BadRequest, status);
    }
}
