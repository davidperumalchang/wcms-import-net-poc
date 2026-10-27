using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Xunit.Abstractions;
using Xunit.Sdk;

[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace WcmsImport.E2ETests;

/// <summary>Runs the tests in a class in method-name order (T1_, T2_, ...), because each one builds on the last.</summary>
public sealed class ByNameOrderer : ITestCaseOrderer
{
    public IEnumerable<TTestCase> OrderTestCases<TTestCase>(IEnumerable<TTestCase> testCases) where TTestCase : ITestCase =>
        testCases.OrderBy(t => t.TestMethod.Method.Name, StringComparer.Ordinal);
}

/// <summary>HTTP helpers for the RUNNING stack. Checked once before the tests: reachable and empty.</summary>
public sealed class Stack : IAsyncLifetime
{
    public static readonly string Api = Environment.GetEnvironmentVariable("API_URL") ?? "http://localhost:3000";
    public static readonly string Upstream = Environment.GetEnvironmentVariable("UPSTREAM_URL") ?? "http://localhost:4000";
    public static readonly string Cms = Environment.GetEnvironmentVariable("CMS_URL") ?? "http://localhost:5000";

    private static readonly string[] Final = ["COMPLETED", "COMPLETED_WITH_ERRORS", "FAILED"];
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(10) };

    public async Task InitializeAsync()
    {
        try
        {
            using var r = await _http.GetAsync($"{Api}/outbox");
            r.EnsureSuccessStatusCode();
        }
        catch (HttpRequestException)
        {
            throw new InvalidOperationException($"API not reachable at {Api}. Start the stack first: docker compose up --build -d");
        }
        var existing = (await GetAsync($"{Api}/content")).Body.GetProperty("count").GetInt32();
        if (existing != 0)
            throw new InvalidOperationException("database already has content. Reset first: docker compose down -v && docker compose up --build -d");
    }

    public Task DisposeAsync()
    {
        _http.Dispose();
        return Task.CompletedTask;
    }

    public async Task<(HttpStatusCode Status, JsonElement Body)> GetAsync(string url)
    {
        using var r = await _http.GetAsync(url);
        return (r.StatusCode, await r.Content.ReadFromJsonAsync<JsonElement>());
    }

    public async Task<(HttpStatusCode Status, JsonElement Body)> PostAsync(string url, object? body = null)
    {
        using var r = await _http.PostAsync(url, body is null ? null : JsonContent.Create(body));
        return (r.StatusCode, await r.Content.ReadFromJsonAsync<JsonElement>());
    }

    public async Task<T> WaitForAsync<T>(Func<Task<T?>> check, string what, int timeoutMs = 90_000)
    {
        var end = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < end)
        {
            if (await check() is { } value) return value;
            await Task.Delay(500);
        }
        throw new XunitException($"timed out waiting for {what}");
    }

    public async Task<(string Id, JsonElement Job)> RunImportAsync(string source, object? options = null)
    {
        var (status, body) = await PostAsync($"{Api}/imports", new { source, options });
        Assert.Equal(HttpStatusCode.Accepted, status); // POST /imports answers 202 straight away
        var id = body.GetProperty("jobId").GetString()!;
        var job = await WaitForAsync<JsonElement?>(async () =>
        {
            var j = (await GetAsync($"{Api}/imports/{id}")).Body.GetProperty("job");
            return Final.Contains(j.GetProperty("status").GetString()) ? j : null;
        }, $"job {id}");
        return (id, job!.Value);
    }

    public async Task<JsonElement> ReceivedAsync() => (await GetAsync($"{Upstream}/received")).Body;

    public async Task<int> ContentCountAsync() => (await GetAsync($"{Api}/content")).Body.GetProperty("count").GetInt32();

    public async Task<JsonElement[]> OutboxAsync() => [.. (await GetAsync($"{Api}/outbox")).Body.EnumerateArray()];
}

public static class JsonExtensions
{
    public static int Int(this JsonElement e, string name) => e.GetProperty(name).GetInt32();

    public static string? Str(this JsonElement e, string name) => e.GetProperty(name).GetString();
}
