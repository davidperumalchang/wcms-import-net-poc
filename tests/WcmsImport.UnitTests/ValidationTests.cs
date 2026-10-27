using WcmsImport.Importer;

namespace WcmsImport.UnitTests;

public class ValidationTests
{
    private static SourceItem Page(string sourceId = "1", string title = "Pricing", string slug = "pricing", string body = "<p>hi</p>", string status = "published") =>
        new(sourceId, title, slug, body, status);

    [Fact]
    public void A_clean_page_passes_with_no_errors_or_warnings()
    {
        var r = Validation.Validate(Page());
        Assert.Empty(r.Errors);
        Assert.Empty(r.Warnings);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void A_missing_or_blank_title_is_an_error(string title) =>
        Assert.Equal(["title is required"], Validation.Validate(Page(title: title)).Errors);

    [Fact]
    public void A_body_over_the_limit_is_an_error_a_body_at_the_limit_is_fine()
    {
        Assert.Single(Validation.Validate(Page(body: new string('x', Validation.BodyLimit + 1))).Errors);
        Assert.Empty(Validation.Validate(Page(body: new string('x', Validation.BodyLimit))).Errors);
    }

    [Fact]
    public void Script_tags_are_removed_and_reported_as_a_warning_the_page_is_still_accepted()
    {
        var r = Validation.Validate(Page(body: "<p>a</p><script>steal()</script><p>b</p>"));
        Assert.Equal("<p>a</p><p>b</p>", r.Item.Body);
        Assert.Equal(["script tag removed"], r.Warnings);
        Assert.Empty(r.Errors);
    }

    [Fact]
    public void Script_tags_are_matched_case_insensitively_and_across_lines()
    {
        var r = Validation.Validate(Page(body: "<p>a</p><SCRIPT type=\"x\">\nsteal()\n</Script><p>b</p>"));
        Assert.Equal("<p>a</p><p>b</p>", r.Item.Body);
    }

    [Fact]
    public void Validate_does_not_modify_its_input()
    {
        var input = Page(body: "<script>x</script>");
        Validation.Validate(input);
        Assert.Equal("<script>x</script>", input.Body);
    }

    [Fact]
    public void Fingerprint_is_stable_and_changes_when_stored_content_changes()
    {
        Assert.Equal(Validation.Fingerprint(Page()), Validation.Fingerprint(Page()));
        Assert.NotEqual(Validation.Fingerprint(Page()), Validation.Fingerprint(Page(body: "<p>changed</p>")));
        Assert.NotEqual(Validation.Fingerprint(Page()), Validation.Fingerprint(Page(status: "draft")));
    }

    [Fact]
    public void Fingerprint_ignores_fields_we_do_not_store() =>
        Assert.Equal(Validation.Fingerprint(Page()), Validation.Fingerprint(Page(sourceId: "999")));

    [Fact]
    public void UniqueSlug_keeps_the_slug_when_nobody_owns_it() =>
        Assert.Equal(new SlugDecision("pricing"), Validation.UniqueSlug(Page(), null, "wordpress"));

    [Fact]
    public void UniqueSlug_keeps_the_slug_when_the_same_page_already_owns_it()
    {
        var owner = new SlugOwner("wordpress", "1");
        Assert.Equal(new SlugDecision("pricing"), Validation.UniqueSlug(Page(), owner, "wordpress"));
    }

    [Fact]
    public void UniqueSlug_renames_when_a_different_page_owns_the_slug_and_says_so()
    {
        var r = Validation.UniqueSlug(Page(sourceId: "2"), new SlugOwner("wordpress", "1"), "wordpress");
        Assert.Equal("pricing-wordpress-2", r.Slug);
        Assert.Contains("already used", r.Warning);
    }

    [Fact]
    public void UniqueSlug_also_renames_when_the_owner_came_from_a_different_source()
    {
        var r = Validation.UniqueSlug(Page(), new SlugOwner("flat", "1"), "wordpress");
        Assert.Equal("pricing-wordpress-1", r.Slug);
    }
}
