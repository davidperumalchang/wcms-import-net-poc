using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace WcmsImport.Importer;

/// <summary>One page in OUR standard shape. Every connector maps its source to this.</summary>
public sealed record SourceItem(string SourceId, string Title, string Slug, string Body, string Status);

/// <summary>The stored row that currently holds a slug.</summary>
public sealed record SlugOwner(string Source, string SourceId);

public sealed record ValidationResult(SourceItem Item, IReadOnlyList<string> Errors, IReadOnlyList<string> Warnings);

public sealed record SlugDecision(string Slug, string? Warning = null);

/// <summary>Item-level rules: pure functions, no I/O, so they can be unit tested without a database.</summary>
public static partial class Validation
{
    public const int BodyLimit = 20000;

    [GeneratedRegex("<script", RegexOptions.IgnoreCase, matchTimeoutMilliseconds: 1000)]
    private static partial Regex ScriptOpen();

    [GeneratedRegex(@"<script[\s\S]*?</script>", RegexOptions.IgnoreCase, matchTimeoutMilliseconds: 1000)]
    private static partial Regex ScriptBlock();

    /// <summary>
    /// Check one item. An item with errors is rejected; warnings mean we fixed something.
    /// The input is never modified (records are immutable; a fixed copy is returned).
    ///
    /// OMITTED (production): this only strips &lt;script&gt; blocks. A real sanitiser is an allow-list parser
    /// (event handlers such as onclick= and javascript: URLs survive here). Also omitted: link rewriting,
    /// locale handling, custom fields, media/asset import, and a configurable per-tenant body limit.
    /// </summary>
    public static ValidationResult Validate(SourceItem raw)
    {
        var item = raw;
        var errors = new List<string>();
        var warnings = new List<string>();
        if (string.IsNullOrWhiteSpace(item.Title)) errors.Add("title is required");
        if (item.Body.Length > BodyLimit) errors.Add($"body too large ({item.Body.Length} > {BodyLimit})");
        if (ScriptOpen().IsMatch(item.Body))
        {
            item = item with { Body = ScriptBlock().Replace(item.Body, "") };
            warnings.Add("script tag removed");
        }
        return new ValidationResult(item, errors, warnings);
    }

    /// <summary>Fingerprint of the parts of an item we store. Same fingerprint = nothing to update.</summary>
    public static string Fingerprint(SourceItem i) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\u241F', i.Title, i.Slug, i.Body, i.Status))));

    /// <summary>
    /// Slugs must be unique per tenant. <paramref name="owner"/> is the stored row that currently holds item.Slug (or null).
    /// If another page owns it, the new page gets "&lt;slug&gt;-&lt;source&gt;-&lt;sourceId&gt;" plus a warning.
    /// Deterministic on purpose: re-importing the same page produces the same slug, so it stays "unchanged".
    /// </summary>
    public static SlugDecision UniqueSlug(SourceItem item, SlugOwner? owner, string source)
    {
        if (owner is null || (owner.Source == source && owner.SourceId == item.SourceId)) return new SlugDecision(item.Slug);
        var slug = $"{item.Slug}-{source}-{item.SourceId}";
        return new SlugDecision(slug, $"slug \"{item.Slug}\" already used, renamed to \"{slug}\"");
    }
}
