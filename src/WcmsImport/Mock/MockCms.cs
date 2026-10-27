namespace WcmsImport.Mock;

/// <summary>
/// "Source CMS": a fake old CMS with 50 pages, some of them deliberately dirty.
///   pages 5, 22, 39        -> no title
///   pages 7, 20, 33, 46    -> contain a &lt;script&gt; tag
///   pages 14, 25, 36, 47   -> slug clashes with page 1
///   pages 4, 23, 42        -> body too large
///   every 9th page         -> draft
/// </summary>
public static class MockCms
{
    public const int DefaultPort = 5000;
    private const int MaxLimit = 100;
    private static readonly int[] Editable = [2, 6, 10, 12];

    private sealed record Page(int N, string Title, string Slug, string Body, bool Draft);

    private static Page MakePage(int n, int editVersion)
    {
        string title = $"Page {n}", slug = $"page-{n}", body = $"<p>Body of page {n}</p>";
        if (n % 17 == 5) title = "";
        if (n % 13 == 7) body += "<script>alert(1)</script>";
        if (n % 11 == 3 && n > 3) slug = "page-1";
        if (n % 19 == 4) body = "<p>" + new string('x', 21000) + "</p>";
        if (editVersion > 0 && Editable.Contains(n)) body += $"<p>edited v{editVersion}</p>";
        return new Page(n, title, slug, body, Draft: n % 9 == 0);
    }

    public static Task RunAsync(string[] args)
    {
        var builder = Common.Hosting.CreateWebBuilder(args, DefaultPort);
        var count = builder.Configuration.GetValue("COUNT", 50);
        var app = builder.Build();
        var editVersion = 0;

        IEnumerable<Page> Slice(int offset, int limit)
        {
            var version = Volatile.Read(ref editVersion);
            return Enumerable.Range(offset + 1, Math.Max(0, Math.Min(limit, count - offset))).Select(n => MakePage(n, version));
        }
        static bool BadPaging(int offset, int limit) => offset < 0 || limit < 0 || limit > MaxLimit;

        app.MapGet("/wp/posts", (int? offset, int? limit) =>
        {
            int o = offset ?? 0, l = limit ?? 10;
            if (BadPaging(o, l)) return Results.BadRequest(new { error = $"offset must be >= 0 and limit between 0 and {MaxLimit}" });
            return Results.Ok(new
            {
                total = count,
                items = Slice(o, l).Select(p => new
                {
                    id = p.N,
                    title = new { rendered = p.Title },
                    slug = p.Slug,
                    content = new { rendered = p.Body },
                    status = p.Draft ? "draft" : "publish",
                }),
            });
        });

        app.MapGet("/flat/export", (int? offset, int? limit) =>
        {
            int o = offset ?? 0, l = limit ?? 10;
            if (BadPaging(o, l)) return Results.BadRequest(new { error = $"offset must be >= 0 and limit between 0 and {MaxLimit}" });
            return Results.Ok(new
            {
                total = count,
                rows = Slice(o, l).Select(p => new
                {
                    @ref = p.N.ToString(),
                    name = p.Title,
                    url = $"https://old.example.com/{p.Slug}",
                    html = p.Body,
                    live = p.Draft ? "N" : "Y",
                }),
            });
        });

        app.MapPost("/edit", () =>
        {
            var version = Interlocked.Increment(ref editVersion);
            return Results.Ok(new { message = $"edited pages {string.Join(", ", Editable)} (version {version})" });
        });

        app.MapFallback(() => Results.NotFound(new { error = "not found" }));

        app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("mock-cms")
            .LogInformation("listening on :{Port}", builder.Configuration.GetValue("PORT", DefaultPort));
        return app.RunAsync();
    }
}
