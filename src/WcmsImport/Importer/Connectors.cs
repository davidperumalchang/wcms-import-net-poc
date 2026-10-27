using System.Text.Json;
using WcmsImport.Common;

namespace WcmsImport.Importer;

public sealed record SourcePage(int Total, IReadOnlyList<SourceItem> Items);

/// <summary>
/// "CMS Connector": one small adapter per source system.
///   Path(offset, limit) -> the URL to call
///   ToPage(json)        -> total + items in OUR standard shape (<see cref="SourceItem"/>)
/// Mapping is kept apart from HTTP so it can be tested with plain JSON.
///
/// OMITTED (production): authentication to the source (customer credentials from a secret store),
/// honouring the source's rate-limit headers, and cursors for sources that do not support offset paging.
/// The two sources below are simplified imitations of WordPress and a flat export, not the real APIs.
/// </summary>
public interface ISourceMapper
{
    string Path(int offset, int limit);
    SourcePage ToPage(string json);
}

public static class SourceMappers
{
    public static readonly IReadOnlyDictionary<string, ISourceMapper> All = new Dictionary<string, ISourceMapper>
    {
        ["wordpress"] = new WordPressMapper(),
        ["flat"] = new FlatExportMapper(),
    };
}

/// <summary>WordPress-style REST API (nested JSON).</summary>
public sealed class WordPressMapper : ISourceMapper
{
    private sealed record RenderedText(string Rendered = "");
    private sealed record Post(long Id, RenderedText? Title, string Slug, RenderedText? Content, string Status = "");
    private sealed record Response(int Total, List<Post> Items);

    public string Path(int offset, int limit) => $"/wp/posts?offset={offset}&limit={limit}";

    public SourcePage ToPage(string json)
    {
        var d = JsonSerializer.Deserialize<Response>(json, Defaults.Json) ?? throw new JsonException("empty response");
        return new SourcePage(d.Total, [.. d.Items.Select(w => new SourceItem(
            SourceId: w.Id.ToString(),
            Title: w.Title?.Rendered ?? "",
            Slug: w.Slug,
            Body: w.Content?.Rendered ?? "",
            Status: w.Status == "publish" ? "published" : "draft"))]);
    }
}

/// <summary>Flat export (different field names, a different value for "published", slug hidden inside a URL).</summary>
public sealed class FlatExportMapper : ISourceMapper
{
    private sealed record Row(string Ref, string Name = "", string Url = "", string Html = "", string Live = "");
    private sealed record Response(int Total, List<Row> Rows);

    public string Path(int offset, int limit) => $"/flat/export?offset={offset}&limit={limit}";

    public SourcePage ToPage(string json)
    {
        var d = JsonSerializer.Deserialize<Response>(json, Defaults.Json) ?? throw new JsonException("empty response");
        return new SourcePage(d.Total, [.. d.Rows.Select(r => new SourceItem(
            SourceId: r.Ref,
            Title: r.Name,
            Slug: r.Url.Split('/')[^1],
            Body: r.Html,
            Status: r.Live == "Y" ? "published" : "draft"))]);
    }
}

/// <summary>What the worker uses: FetchPageAsync(mapper, offset, limit) -> total + items.</summary>
public sealed class SourceClient(HttpClient http)
{
    public async Task<SourcePage> FetchPageAsync(ISourceMapper mapper, int offset, int limit)
    {
        using var response = await http.GetAsync(mapper.Path(offset, limit));
        if (!response.IsSuccessStatusCode) throw new HttpRequestException($"source returned {(int)response.StatusCode}");
        return mapper.ToPage(await response.Content.ReadAsStringAsync());
    }
}
