using D365FO.Core.Knowledge;
using Xunit;

namespace D365FO.Core.Tests;

/// <summary>
/// The catalog exists to stop a moniker being guessed, so what it must never do is answer
/// confidently when it does not know.
/// </summary>
public class BpMonikerCatalogTests
{
    [Fact]
    public void The_shipped_snapshot_is_populated_and_mostly_canonical()
    {
        // A snapshot that failed to embed would make every `validate` answer "not a moniker",
        // which is the one wrong answer that reads exactly like a right one.
        Assert.True(BpMonikerCatalog.IsPopulated, "the shipped bp-monikers.json did not load");
        Assert.True(BpMonikerCatalog.Snapshot.Monikers.Count(m => m.Canonical) > 300,
            "far fewer canonical monikers than a real installation declares — the rule-set scan probably found nothing");
    }

    [Fact]
    public void A_real_moniker_is_canonical_and_carries_the_installations_own_message()
    {
        var found = BpMonikerCatalog.Find("BPErrorPrivilegeNotCoveredByDuty");

        Assert.NotNull(found);
        Assert.True(found!.Canonical);
        Assert.Contains("duty", found.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_name_that_reads_exactly_like_a_rule_is_not_one()
    {
        // This is the whole point: nothing about the spelling distinguishes it from the real
        // moniker above, so only a lookup can tell them apart.
        Assert.Null(BpMonikerCatalog.Find("BPCheckNamingConventions"));
    }

    [Fact]
    public void Lookup_is_case_sensitive_and_offers_the_right_casing()
    {
        // xppbp and the suppression reader match exactly, so a case-insensitive "yes" would hand
        // back a name that suppresses nothing.
        Assert.Null(BpMonikerCatalog.Find("bperrorprivilegenotcoveredbyduty"));

        var variants = BpMonikerCatalog.CaseVariants("bperrorprivilegenotcoveredbyduty");
        Assert.Contains("BPErrorPrivilegeNotCoveredByDuty", variants);
    }

    [Fact]
    public void Search_matches_words_the_rule_name_does_not_contain()
    {
        // "not referenced on any duty" is in the message, not the name.
        var hits = BpMonikerCatalog.Search("privilege referenced duty", 10);
        Assert.Contains(hits, m => m.Name == "BPErrorPrivilegeNotCoveredByDuty");
    }

    [Fact]
    public void Search_puts_real_rules_before_resource_strings()
    {
        var hits = BpMonikerCatalog.Search("duty", 50);
        var firstNonCanonical = hits.ToList().FindIndex(m => !m.Canonical);
        if (firstNonCanonical < 0) return; // nothing non-canonical matched; ordering is moot

        Assert.DoesNotContain(hits.Skip(firstNonCanonical), m => m.Canonical);
    }

    [Fact]
    public void A_suppression_block_has_the_element_order_the_shipped_files_use()
    {
        var block = BpMonikerCatalog.SuppressionBlock(
            "BPErrorPrivilegeNotCoveredByDuty",
            "dynamics://SecurityPrivilege/MyPrivilege",
            justification: "Deliberate.");

        var order = new[] { "DiagnosticType", "Severity", "Path", "Moniker", "Message", "Justification" };
        var positions = order.Select(e => block.IndexOf("<" + e + ">", StringComparison.Ordinal)).ToArray();

        Assert.DoesNotContain(-1, positions);
        Assert.Equal(positions.OrderBy(p => p).ToArray(), positions);
        Assert.Contains("dynamics://SecurityPrivilege/MyPrivilege", block);
    }

    [Fact]
    public void A_suppression_block_escapes_what_would_break_the_file()
    {
        var block = BpMonikerCatalog.SuppressionBlock(
            "BPSomething", "dynamics://Table/T", message: "a < b & c > d");

        Assert.Contains("a &lt; b &amp; c &gt; d", block);
    }

    [Fact]
    public void An_omitted_justification_leaves_something_a_reviewer_will_notice()
    {
        var block = BpMonikerCatalog.SuppressionBlock("BPSomething", "dynamics://Table/T");
        Assert.Contains("TODO", block);
    }
}
