using System.Text;
using Npgsql;
using WcmsImport.Common;

namespace WcmsImport.Notifier;

/// <summary>
/// "Notification service": polls the outbox table and sends each event to the upstream system.
///   - FOR UPDATE SKIP LOCKED: several notifier containers can run without sending the same row twice
///   - failures are retried with a growing wait, then the row is marked 'dead' (see Webhook)
///   - each event is signed (HMAC) and carries an id so the receiver can ignore duplicates
///   - delivery is at-least-once: if we crash after the receiver answered but before we mark the row 'sent',
///     the event is sent again. That is why the receiver de-duplicates on the event id.
///
/// OMITTED (production):
///   - a registry of subscribers: today there is ONE url from the configuration, so one broken subscriber
///     blocks the queue's retries. Real design: one delivery row per (event, subscriber), each retried alone
///   - a secret per subscriber (with rotation), and a way to replay 'dead' rows (API or admin screen)
///   - holding the row lock during the HTTP call is fine for a demo; production would claim the row, release
///     the lock, send, then record the result, so slow receivers do not hold database connections
///   - metrics and alerts (outbox age, dead count), rate limiting per subscriber
/// </summary>
public sealed class NotificationService(NpgsqlDataSource db, IHttpClientFactory httpClients, IConfiguration config, ILoggerFactory loggers)
    : BackgroundService
{
    public const string HttpClientName = "upstream";
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(1);

    private readonly string _url = config.Required("UPSTREAM_URL");
    private readonly string _secret = config.Required("WEBHOOK_SECRET");
    private readonly ILogger _log = loggers.CreateLogger("notifier");

    private sealed record OutboxRow(string Id, string Payload, int Attempts);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _log.LogInformation("polling the outbox every second");
        while (!stoppingToken.IsCancellationRequested)
        {
            if (!await SendOneAsync()) await Task.Delay(PollInterval, stoppingToken);
        }
    }

    private async Task<bool> SendOneAsync()
    {
        try
        {
            await using var conn = await db.OpenConnectionAsync();
            await using var tx = await conn.BeginTransactionAsync();
            var row = await conn.Command(
                    "SELECT id, payload::text, attempts FROM outbox WHERE status='pending' AND next_attempt_at <= now() ORDER BY created_at LIMIT 1 FOR UPDATE SKIP LOCKED")
                .FirstOrDefaultAsync(r => new OutboxRow(r.GetString(0), r.GetString(1), r.GetInt32(2)));
            if (row is null) return false;

            var attempt = row.Attempts + 1;
            try
            {
                await DeliverAsync(row, attempt);
                await conn.Command("UPDATE outbox SET status='sent', attempts=$2, sent_at=now(), last_error=NULL WHERE id=$1", row.Id, attempt).RunAsync();
                _log.LogInformation("{EventId} delivered (attempt {Attempt})", row.Id, attempt);
            }
            catch (Exception e)
            {
                var error = e is TaskCanceledException ? "upstream timed out" : e.Message;
                var plan = Webhook.PlanRetry(attempt);
                if (plan.Dead)
                {
                    await conn.Command("UPDATE outbox SET status='dead', attempts=$2, last_error=$3 WHERE id=$1", row.Id, attempt, error).RunAsync();
                    _log.LogWarning("{EventId} gave up after {Attempt} attempts -> dead ({Error})", row.Id, attempt, error);
                }
                else
                {
                    await conn.Command(
                        "UPDATE outbox SET attempts=$2, last_error=$3, next_attempt_at = now() + make_interval(secs => $4) WHERE id=$1",
                        row.Id, attempt, error, plan.WaitSec).RunAsync();
                    _log.LogWarning("{EventId} failed ({Error}), attempt {Attempt}, retry in {Wait}s", row.Id, error, attempt, plan.WaitSec);
                }
            }
            await tx.CommitAsync();
            return true;
        }
        catch (Exception e) // the transaction is rolled back on dispose; the row is picked up again on the next poll
        {
            _log.LogError("error: {Error}", e.Message);
            return false;
        }
    }

    private async Task DeliverAsync(OutboxRow row, int attempt)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, _url)
        {
            Content = new StringContent(row.Payload, Encoding.UTF8, "application/json"),
        };
        request.Headers.Add(Webhook.EventIdHeader, row.Id);
        request.Headers.Add(Webhook.AttemptHeader, attempt.ToString());
        request.Headers.Add(Webhook.SignatureHeader, Webhook.Sign(_secret, row.Payload));

        using var response = await httpClients.CreateClient(HttpClientName).SendAsync(request);
        if (!response.IsSuccessStatusCode) throw new HttpRequestException($"upstream replied {(int)response.StatusCode}");
    }
}
