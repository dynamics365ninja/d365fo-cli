using System.Linq;
using D365FO.Core.Guardrails;
using D365FO.Core.Index;
using Xunit;

namespace D365FO.Core.Tests;

/// <summary>
/// The two label-token shapes the AOT actually writes. Grounded on a real
/// PackagesLocalDirectory: every &lt;Label&gt; element of the shipped
/// AccountsPayableMobile model is the colon form (<c>@AccountsPayableMobile:Amount</c>),
/// whose key half carries no digit at all — the legacy letters-then-digits split
/// resolved none of them.
/// </summary>
public class ResolveLabelTokenTests
{
    [Fact]
    public void Resolves_the_colon_token_shape_within_its_own_label_file()
    {
        WithRepo(repo =>
        {
            // No language filter: every indexed translation of that one label, and
            // nothing from the other file that also has an "Amount".
            var hits = repo.ResolveLabel("@AccountsPayableMobile:Amount");
            Assert.All(hits, h => Assert.Equal("AccountsPayableMobile", h.File));
            Assert.Equal(
                new[] { "Amount due", "Částka" },
                hits.Select(h => h.Value).OrderBy(v => v, StringComparer.Ordinal));

            // The '@' is optional on either shape.
            Assert.Equal(hits.Count, repo.ResolveLabel("AccountsPayableMobile:Amount").Count);
        });
    }

    [Fact]
    public void Colon_token_never_answers_from_a_different_label_file()
    {
        // "Amount" exists in both files; the token names one of them.
        WithRepo(repo =>
        {
            var hits = repo.ResolveLabel("@Fleet:Amount");
            Assert.Collection(hits, h => Assert.Equal("Rental amount", h.Value));
        });
    }

    [Fact]
    public void Colon_token_is_language_filtered_like_the_legacy_one()
    {
        WithRepo(repo =>
        {
            var cs = repo.ResolveLabel("@AccountsPayableMobile:Amount", new[] { "cs" });
            Assert.Collection(cs, h => Assert.Equal("Částka", h.Value));
        });
    }

    [Fact]
    public void Legacy_token_still_resolves()
    {
        WithRepo(repo =>
        {
            Assert.Collection(
                repo.ResolveLabel("@SYS12345"),
                h => Assert.Equal("Ledger chart of accounts", h.Value));
        });
    }

    [Fact]
    public void Unknown_colon_token_resolves_to_nothing()
    {
        WithRepo(repo => Assert.Empty(repo.ResolveLabel("@AccountsPayableMobile:NoSuchLabel")));
    }

    [Theory]
    [InlineData("@AccountsPayableMobile:Amount", "Amount due [[@AccountsPayableMobile:Amount]]")]
    [InlineData("@SYS12345", "Ledger chart of accounts [[@SYS12345]]")]
    // A file id ending in digits must not be chopped into the legacy shape.
    [InlineData("@Fleet2:Amount", "@Fleet2:Amount")]
    // Nothing resolvable: the text survives untouched.
    [InlineData("mailto:someone@example.com", "mailto:someone@example.com")]
    public void Inliner_replaces_both_shapes_and_leaves_the_rest_alone(string input, string expected)
    {
        WithRepo(repo =>
        {
            var node = System.Text.Json.Nodes.JsonNode.Parse($"{{\"label\":{System.Text.Json.JsonSerializer.Serialize(input)}}}")!;
            LabelInliner.WalkAndReplace(node, repo, new[] { "en-us" });
            Assert.Equal(expected, node["label"]!.GetValue<string>());
        });
    }

    private static void WithRepo(Action<MetadataRepository> body)
    {
        var db = Path.Combine(Path.GetTempPath(), $"d365fo-lbltoken-{Guid.NewGuid():N}.sqlite");
        try
        {
            var repo = new MetadataRepository(db);
            repo.EnsureSchema();
            Insert(db, 1, "AccountsPayableMobile", "en-us", "Amount", "Amount due");
            Insert(db, 2, "AccountsPayableMobile", "cs", "Amount", "Částka");
            Insert(db, 3, "Fleet", "en-us", "Amount", "Rental amount");
            // Storage shape of the legacy extractor: the key carries the whole token.
            Insert(db, 4, "SYS", "en-us", "@SYS12345", "Ledger chart of accounts");
            body(repo);
        }
        finally
        {
            try { File.Delete(db); } catch { }
        }
    }

    private static void Insert(string db, long labelId, string file, string lang, string key, string value)
    {
        using var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={db}");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "INSERT INTO Labels(LabelId, LabelFile, Language, Key, Value) VALUES(@id,@f,@lg,@k,@v)";
        cmd.Parameters.AddWithValue("@id", labelId);
        cmd.Parameters.AddWithValue("@f", file);
        cmd.Parameters.AddWithValue("@lg", lang);
        cmd.Parameters.AddWithValue("@k", key);
        cmd.Parameters.AddWithValue("@v", value);
        cmd.ExecuteNonQuery();
    }
}
