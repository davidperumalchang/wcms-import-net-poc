using System.Security.Cryptography;
using System.Text;

namespace WcmsImport.Notifier;

public sealed record RetryPlan(bool Dead, int? WaitSec = null);

/// <summary>Webhook rules shared by the notifier (sender) and the mock upstream (receiver). Pure functions.</summary>
public static class Webhook
{
    public const int MaxAttempts = 5;

    public const string EventIdHeader = "X-WCMS-Event-Id";
    public const string AttemptHeader = "X-WCMS-Attempt";
    public const string SignatureHeader = "X-WCMS-Signature";

    /// <summary>Signature header value for a request body: "sha256=&lt;hex hmac&gt;".</summary>
    public static string Sign(string secret, string body) =>
        "sha256=" + Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(body)));

    /// <summary>True only if the header matches the body. Constant-time compare so timing leaks nothing.</summary>
    public static bool Verify(string secret, string body, string? header) =>
        header is not null
        && CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(Sign(secret, body)), Encoding.UTF8.GetBytes(header));

    /// <summary>After a failed attempt (1-based): give up, or wait 2s, 4s, 8s, 16s. Production would wait minutes to hours.</summary>
    public static RetryPlan PlanRetry(int attempt) =>
        attempt >= MaxAttempts ? new RetryPlan(Dead: true) : new RetryPlan(Dead: false, WaitSec: 1 << attempt);
}
