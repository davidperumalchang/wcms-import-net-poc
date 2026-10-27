using WcmsImport.Importer;

namespace WcmsImport.UnitTests;

public class ConnectorTests
{
    private static readonly ISourceMapper WordPress = SourceMappers.All["wordpress"];
    private static readonly ISourceMapper Flat = SourceMappers.All["flat"];

    [Fact]
    public void WordPress_maps_nested_json_to_our_standard_shape()
    {
        var page = WordPress.ToPage("""
            {
              "total": 2,
              "items": [
                { "id": 7, "title": { "rendered": "Hello" }, "slug": "hello", "content": { "rendered": "<p>x</p>" }, "status": "publish" },
                { "id": 8, "title": { "rendered": "Wip" }, "slug": "wip", "content": { "rendered": "" }, "status": "draft" }
              ]
            }
            """);
        Assert.Equal(2, page.Total);
        Assert.Equal(new SourceItem("7", "Hello", "hello", "<p>x</p>", "published"), page.Items[0]);
        Assert.Equal("draft", page.Items[1].Status);
    }

    [Fact]
    public void Flat_maps_different_field_names_and_takes_the_slug_from_the_url()
    {
        var page = Flat.ToPage("""
            { "total": 1, "rows": [{ "ref": "49", "name": "Page 49", "url": "https://old.example.com/page-49", "html": "<p>y</p>", "live": "N" }] }
            """);
        Assert.Equal(new SourceItem("49", "Page 49", "page-49", "<p>y</p>", "draft"), page.Items[0]);
    }

    [Fact]
    public void Both_sources_produce_the_same_shape_so_the_worker_needs_no_source_specific_code()
    {
        var a = WordPress.ToPage("""
            { "total": 1, "items": [{ "id": 1, "title": { "rendered": "a" }, "slug": "a", "content": { "rendered": "b" }, "status": "publish" }] }
            """).Items[0];
        var b = Flat.ToPage("""
            { "total": 1, "rows": [{ "ref": "1", "name": "a", "url": "http://x/a", "html": "b", "live": "Y" }] }
            """).Items[0];
        Assert.Equal(a, b);
    }

    [Fact]
    public void A_missing_title_maps_to_empty_so_validation_rejects_it()
    {
        var page = WordPress.ToPage("""
            { "total": 1, "items": [{ "id": 3, "slug": "s", "content": { "rendered": "b" }, "status": "publish" }] }
            """);
        Assert.Equal("", page.Items[0].Title);
    }

    [Fact]
    public void Paging_paths_carry_the_offset_and_limit()
    {
        Assert.Equal("/wp/posts?offset=20&limit=10", WordPress.Path(20, 10));
        Assert.Equal("/flat/export?offset=0&limit=5", Flat.Path(0, 5));
    }
}
