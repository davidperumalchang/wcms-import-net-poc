using System.Text;
using System.Text.Json;
using WcmsImport.Common;
using WcmsImport.Notifier;

namespace WcmsImport.Mock;

/// <summary>
/// "Upstream": stands in for a search indexer or CDN. It checks the signature,
/// ignores events it has seen before (same event id), and can be told to fail on purpose.
/// </summary>
public static class MockUpstream
{
    public const int DefaultPort = 4000;

    private sealed class State
    {
        public readonly Lock Gate = new();
        public readonly HashSet<string> SeenIds = [];
        public readonly List<JsonElement> Events = []; // in arrival order
        public int FailNext;
        public int Duplicates;
    }

    public static Task RunAsync(string[] args)
    {
        var builder = Hosting.CreateWebBuilder(args, DefaultPort);
        var secret = builder.Configuration.Required("WEBHOOK_SECRET");
        var app = builder.Build();
        var log = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("upstream");
        var state = new State();

        app.MapPost("/hook", async (HttpRequest req) =>
        {
            using var reader = new StreamReader(req.Body, Encoding.UTF8);
            var data = await reader.ReadToEndAsync();

            lock (state.Gate)
            {
                if (state.FailNext > 0)
                {
                    state.FailNext--;
                    log.LogWarning("simulated outage, replying 503 ({Remaining} more to go)", state.FailNext);
                    return Results.Json(new { error = "simulated outage" }, statusCode: StatusCodes.Status503ServiceUnavailable);
                }
            }
            if (!Webhook.Verify(secret, data, req.Headers[Webhook.SignatureHeader].FirstOrDefault()))
            {
                log.LogWarning("bad signature, rejected");
                return Results.Json(new { error = "bad signature" }, statusCode: StatusCodes.Status401Unauthorized);
            }

            JsonElement evt;
            string id;
            try
            {
                evt = JsonDocument.Parse(data).RootElement.Clone();
                id = evt.GetProperty("id").GetString() ?? throw new JsonException("id is null");
            }
            catch (Exception e) when (e is JsonException or KeyNotFoundException or InvalidOperationException)
            {
                return Results.BadRequest(new { error = "malformed event" });
            }

            lock (state.Gate)
            {
                if (!state.SeenIds.Add(id))
                {
                    state.Duplicates++;
                    log.LogInformation("duplicate {EventId}, ignored", id);
                }
                else
                {
                    state.Events.Add(evt);
                    var d = evt.GetProperty("data");
                    log.LogInformation("received {EventId}: {Summary} (job {JobStatus})", id, d.GetProperty("summary").GetRawText(), d.GetProperty("jobStatus").GetString());
                }
            }
            return Results.Ok(new { ok = true });
        });

        // Demo helper: make the next N deliveries fail with 503
        app.MapPost("/fail", (int? next) =>
        {
            var n = Math.Clamp(next ?? 1, 0, 100);
            lock (state.Gate) state.FailNext = n;
            return Results.Ok(new { message = $"next {n} deliveries will fail" });
        });

        app.MapGet("/received", () =>
        {
            lock (state.Gate)
                return Results.Ok(new { count = state.Events.Count, duplicatesIgnored = state.Duplicates, events = state.Events.ToArray() });
        });

        app.MapFallback(() => Results.NotFound(new { error = "not found" }));

        log.LogInformation("listening on :{Port}", builder.Configuration.GetValue("PORT", DefaultPort));
        return app.RunAsync();
    }
}
