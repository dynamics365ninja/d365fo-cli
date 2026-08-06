using D365FO.Core;
using D365FO.Core.Eval;
using D365FO.Core.Index;
using D365FO.Core.Knowledge;
using Xunit;

namespace D365FO.Core.Tests;

/// <summary>
/// The knowledge-audit gate — the CI form of <c>d365fo knowledge audit</c>.
///
/// Generated code is gated fail-closed (<c>validate references</c>, <c>validate xpp</c>, the
/// build), but until now the knowledge <i>shipped to the model</i> was gated by nothing. This
/// is the asymmetry upstream <c>d365fo-mcp-server</c> closed with <c>apiSymbols.test.ts</c> +
/// <c>exampleValidation.test.ts</c>, and its own eval README explicitly flagged this repo's
/// skill corpus as never having had that treatment.
///
/// Three gates, ordered by what each machine can prove:
/// <list type="bullet">
/// <item><description>always — every X++/XML example passes the offline BP validator;</description></item>
/// <item><description>always — every extracted reference is covered by the committed snapshot
/// or the reviewed allowlist, so a knowledge edit cannot ship un-audited;</description></item>
/// <item><description>when a full standard index is present — resolve live and assert zero
/// findings, which is what produces the snapshot in the first place.</description></item>
/// </list>
/// </summary>
public class KnowledgeAuditTests
{
    private static readonly string RepoRoot =
        EvalPaths.FindRepoRoot() ?? throw new InvalidOperationException("Could not locate repo root for tests.");

    private static readonly KnowledgeAuditAllow Allow =
        KnowledgeAuditStore.LoadAllow(EvalPaths.KnowledgeAllowPath(RepoRoot));

    private static readonly IReadOnlyList<KnowledgeRef> Refs = KnowledgeRefExtractor.ExtractAll();

    // ── The gates ────────────────────────────────────────────────────────────

    [Fact]
    public void Extracts_a_non_trivial_set_of_references()
    {
        // Guards against the extractor silently returning nothing, which would make every
        // gate below vacuously pass.
        Assert.True(Refs.Count > 50, $"only {Refs.Count} reference(s) extracted from the corpus");
    }

    [Fact]
    public void Every_reference_is_covered_by_the_committed_snapshot()
    {
        var snapshot = KnowledgeAuditStore.LoadSnapshot(EvalPaths.KnowledgeSnapshotPath(RepoRoot));
        Assert.NotNull(snapshot);

        var uncovered = KnowledgeAudit.VerifyAgainstSnapshot(Refs, snapshot!, Allow.Symbols);
        Assert.True(uncovered.Count == 0,
            $"{uncovered.Count} reference(s) not in the audited snapshot — re-run " +
            "`d365fo knowledge audit --capture` against a real index and commit the result:\n" +
            string.Join("\n", uncovered.Select(r => $"  {r.TopicId} · {r.Field} · {r.Kind} · {r.Name}{(r.Member is null ? "" : "::" + r.Member)}")));
    }

    [Fact]
    public void Snapshot_carries_no_dead_keys()
    {
        var snapshot = KnowledgeAuditStore.LoadSnapshot(EvalPaths.KnowledgeSnapshotPath(RepoRoot));
        Assert.NotNull(snapshot);

        // A snapshot that keeps entries for references the corpus no longer makes has stopped
        // describing the corpus — it would rubber-stamp a rewrite that removed them.
        var stale = KnowledgeAudit.StaleSnapshotKeys(Refs, snapshot!);
        Assert.True(stale.Count == 0,
            $"{stale.Count} stale snapshot key(s) — re-capture:\n  {string.Join("\n  ", stale)}");
    }

    [Fact]
    public void No_example_teaches_BP_error_Xpp()
    {
        var examples = KnowledgeExamples.Collect();
        Assert.True(examples.Count > 15, $"only {examples.Count} example(s) collected from the corpus");

        var (unexpected, deadPins) = KnowledgeExamples.Gate(examples, Allow.Examples);

        Assert.True(unexpected.Count == 0,
            "BP errors in knowledge examples:\n" +
            string.Join("\n", unexpected.Select(v => $"  {v.Key} -> {v.Fix}")));

        // A pin that no longer fires means the example stopped demonstrating the anti-pattern
        // it was excused for; the excuse must go with it.
        Assert.True(deadPins.Count == 0,
            "pinned wrong-vs-right demos that no longer fire (remove them from " +
            $"eval/knowledge-audit.allow.json):\n  {string.Join("\n  ", deadPins)}");
    }

    [Fact]
    public void Every_named_api_resolves_against_a_live_standard_index()
    {
        var repo = LiveStandardIndex();
        if (repo is null) return; // no full standard index here — the snapshot gate above is authoritative

        var result = KnowledgeAudit.Audit(Refs, repo, Allow.Symbols);
        Assert.True(result.Findings.Count == 0, "\n" + KnowledgeAudit.Render(result));
    }

    /// <summary>
    /// The configured index, but only when it is the real standard index. A dev machine or CI
    /// often carries a small fixture index; auditing against that reports every standard
    /// symbol as unknown, so this returns null rather than producing a confident lie.
    /// </summary>
    private static MetadataRepository? LiveStandardIndex()
    {
        try
        {
            var path = D365FoSettings.FromEnvironment().DatabasePath;
            if (!File.Exists(path)) return null;
            var repo = new MetadataRepository(path);
            return KnowledgeAudit.IsFullStandardIndex(repo) ? repo : null;
        }
        catch
        {
            return null;
        }
    }

    // ── Extractor unit tests (pure, no index) ────────────────────────────────

    private static IReadOnlyList<KnowledgeRef> ExtractFrom(string body) =>
        KnowledgeRefExtractor.Extract(new KnowledgeTopic("t", "d", null, body));

    [Fact]
    public void Recognises_every_reference_shape()
    {
        var refs = ExtractFrom("""
            Prose mentioning CustTable::find() and nothing else.

            ```xpp
            [SysEntryPointAttribute]
            public class Sample extends RunBaseBatch
            {
                public void run()
                {
                    CustTable custTable;
                    NumberSeq seq = NumberSeq::newGetNum(CustParameters::numRefCustAccount());
                    Args args = new Args(tableStr(VendTable));
                }
            }
            ```
            """);

        Assert.Contains(refs, r => r.Name == "CustTable" && r.Kind == KnowledgeRefKinds.StaticCall && r.Member == "find");
        Assert.Contains(refs, r => r.Name == "RunBaseBatch" && r.Kind == KnowledgeRefKinds.Extends);
        Assert.Contains(refs, r => r.Name == "Args" && r.Kind == KnowledgeRefKinds.New);
        Assert.Contains(refs, r => r.Name == "SysEntryPointAttribute" && r.Kind == KnowledgeRefKinds.Attribute);
        Assert.Contains(refs, r => r.Name == "VendTable" && r.Kind == KnowledgeRefKinds.Intrinsic);
        Assert.Contains(refs, r => r.Name == "CustTable" && r.Kind == KnowledgeRefKinds.Declaration);
        // `class Sample` is declared by the example itself, so it is not expected in the AOT.
        Assert.DoesNotContain(refs, r => r.Name == "Sample");
    }

    [Fact]
    public void Skips_placeholders_slots_and_markup()
    {
        var refs = ExtractFrom("""
            See [BPUpgradeCodeToday](https://learn.microsoft.com/x) and run
            `<Module>Parameters::numRef<Edt>()` against `<Table>`.

            ```xpp
            MyVehicleTable myTable;
            container c = [NoYes::Yes, NoYes::No];
            ```

            ```xml
            <AxTable><Name>FmVehicle</Name></AxTable>
            ```
            """);

        // A markdown link is link text, not an X++ attribute.
        Assert.DoesNotContain(refs, r => r.Name == "BPUpgradeCodeToday");
        // `<Module>Parameters` names "the parameters table of the module you pick".
        Assert.DoesNotContain(refs, r => r.Name == "Parameters");
        // `My…` is the corpus convention for a hypothetical element.
        Assert.DoesNotContain(refs, r => r.Name == "MyVehicleTable");
        // A bracket containing '::' is a container literal, not an attribute.
        Assert.DoesNotContain(refs, r => r.Kind == KnowledgeRefKinds.Attribute && r.Name == "NoYes");
        // AOT XML node types are markup, not indexed symbols.
        Assert.DoesNotContain(refs, r => r.Name == "AxTable");
    }

    [Fact]
    public void Locates_a_finding_at_its_section_and_block()
    {
        var refs = ExtractFrom("""
            # Title

            ## Firing the event

            ```xpp
            CustTable::find();
            ```
            """);

        var hit = Assert.Single(refs);
        Assert.Equal("§Firing the event · ```xpp", hit.Field);
        Assert.Equal("t|static-call|CustTable::find", hit.Key);
    }

    // ── Audit unit tests (fake index) ────────────────────────────────────────

    private sealed class FakeLookup(
        Dictionary<string, string[]> symbols,
        Dictionary<string, string[]>? members = null,
        HashSet<string>? bases = null) : IKnowledgeSymbolLookup
    {
        public KnowledgeSymbolHit? Resolve(string name)
        {
            var key = symbols.Keys.FirstOrDefault(k => string.Equals(k, name, StringComparison.OrdinalIgnoreCase));
            return key is null ? null : new KnowledgeSymbolHit(key, symbols[key]);
        }

        public bool IsReferencedBase(string name) => bases?.Contains(name) == true;

        public bool HasMember(string canonical, string member) =>
            members is not null && members.TryGetValue(canonical, out var m) && m.Contains(member);
    }

    private static KnowledgeRef Ref(string kind, string name, string? member = null) =>
        new("t", name, member, kind, "§x · prose", $"t|{kind}|{name}{(member is null ? "" : "::" + member)}");

    [Fact]
    public void Reports_unknown_types_casing_and_missing_members()
    {
        var lookup = new FakeLookup(
            new() { ["CustTable"] = ["table"], ["NoYes"] = ["enum"] },
            new() { ["CustTable"] = ["find"] });

        var result = KnowledgeAudit.Audit(
        [
            Ref(KnowledgeRefKinds.StaticCall, "CustTable", "find"),      // clean
            Ref(KnowledgeRefKinds.StaticCall, "custtable", "find"),      // casing
            Ref(KnowledgeRefKinds.StaticCall, "CustTable", "findNope"),  // unknown member
            Ref(KnowledgeRefKinds.StaticCall, "NoYes", "Yes"),           // enum value, not a call
            Ref(KnowledgeRefKinds.New, "NotAThing"),                     // unknown type
        ], lookup);

        // Resolved counts the *type*: the missing-member reference still names a real table,
        // and only the misspelled one fails to resolve at all.
        Assert.Equal(3, result.Resolved);
        Assert.Collection(result.Findings.OrderBy(f => f.Status, StringComparer.Ordinal),
            f => Assert.Equal(KnowledgeAuditFinding.Casing, f.Status),
            f => Assert.Equal(KnowledgeAuditFinding.UnknownMember, f.Status),
            f => Assert.Equal(KnowledgeAuditFinding.UnknownType, f.Status));
    }

    [Fact]
    public void Accepts_attributes_written_without_their_suffix_and_referenced_bases()
    {
        var lookup = new FakeLookup(
            new() { ["DataContractAttribute"] = ["class"] },
            bases: ["IFeatureMetadata"]);

        var result = KnowledgeAudit.Audit(
        [
            Ref(KnowledgeRefKinds.Attribute, "DataContract"),
            Ref(KnowledgeRefKinds.Extends, "IFeatureMetadata"),
        ], lookup);

        Assert.Empty(result.Findings);
        Assert.Equal(2, result.Resolved);
    }

    [Fact]
    public void Allowlist_covers_by_name_and_by_dotnet_namespace()
    {
        var lookup = new FakeLookup([]);
        var result = KnowledgeAudit.Audit(
        [
            Ref(KnowledgeRefKinds.StaticCall, "DateTimeUtil", "getToday"),
            Ref(KnowledgeRefKinds.New, "System.Text.StringBuilder"),
        ], lookup, new Dictionary<string, string> { ["DateTimeUtil"] = "kernel class" });

        Assert.Empty(result.Findings);
        Assert.Equal(2, result.Allowed);
    }

    [Fact]
    public void Snapshot_round_trips_only_clean_keys()
    {
        var refs = new[] { Ref(KnowledgeRefKinds.New, "Good"), Ref(KnowledgeRefKinds.New, "Bad") };
        var lookup = new FakeLookup(new() { ["Good"] = ["class"] });
        var result = KnowledgeAudit.Audit(refs, lookup);

        var snapshot = KnowledgeAudit.BuildSnapshot(refs, result, "2026-01-01T00:00:00Z", DateTimeOffset.UnixEpoch);
        Assert.Equal(["t|new|Good"], snapshot.Ok);
        Assert.EndsWith("Z", snapshot.CapturedAt);

        // The failing reference stays uncovered, so the CI gate keeps failing until it is fixed.
        var uncovered = KnowledgeAudit.VerifyAgainstSnapshot(refs, snapshot);
        Assert.Equal("Bad", Assert.Single(uncovered).Name);
    }
}
