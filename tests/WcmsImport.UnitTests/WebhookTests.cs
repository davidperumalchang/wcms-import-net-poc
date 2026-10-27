using WcmsImport.Notifier;

namespace WcmsImport.UnitTests;

public class WebhookTests
{
    private const string Body = """{"id":"evt_1"}""";

    [Fact]
    public void Sign_output_has_the_expected_prefix_and_is_stable()
    {
        Assert.Matches("^sha256=[0-9a-f]{64}$", Webhook.Sign("s", Body));
        Assert.Equal(Webhook.Sign("s", Body), Webhook.Sign("s", Body));
    }

    [Fact]
    public void Verify_accepts_a_correct_signature() => Assert.True(Webhook.Verify("s", Body, Webhook.Sign("s", Body)));

    [Fact]
    public void Verify_rejects_a_wrong_secret() => Assert.False(Webhook.Verify("other", Body, Webhook.Sign("s", Body)));

    [Fact]
    public void Verify_rejects_a_tampered_body() => Assert.False(Webhook.Verify("s", Body + " ", Webhook.Sign("s", Body)));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("sha256=abc")]
    public void Verify_rejects_a_missing_or_malformed_header(string? header) => Assert.False(Webhook.Verify("s", Body, header));

    [Fact]
    public void Retry_waits_grow_2s_4s_8s_16s_and_then_the_event_is_dead()
    {
        Assert.Equal([2, 4, 8, 16], new[] { 1, 2, 3, 4 }.Select(a => Webhook.PlanRetry(a).WaitSec));
        Assert.Equal(new RetryPlan(Dead: true), Webhook.PlanRetry(Webhook.MaxAttempts));
    }
}
