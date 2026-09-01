using System.Xml.Linq;
using D365FO.Core.Extract;
using D365FO.Core.Index;
using D365FO.Core.Scaffolding;
using D365FO.Core.Validation;
using Xunit;

namespace D365FO.Core.Tests;

/// <summary>
/// Golden quality-gate suite — locks the offline grounding chain so it can
/// never silently regress (port of the upstream MCP server's
/// tests/golden/quality-gate.test.ts concept):
///
///   1. Every source-bearing scaffolder's output passes `validate xpp` with
///      ZERO errors.
///   2. Scaffolded output passes `validate references` clean against a
///      fixture mini-index.
///   3. Injected hallucinations (fake table, fake field, fake label, wrong
///      arity) are flagged as errors — the gate must never go blind.
/// </summary>
public class GoldenQualityGateTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"golden-{Guid.NewGuid():N}.sqlite");
    private readonly MetadataRepository _repo;

    public GoldenQualityGateTests()
    {
        _repo = new MetadataRepository(_dbPath);
        _repo.EnsureSchema();
        SeedFixtureIndex();
    }

    public void Dispose()
    {
        SqlitePool.ReleaseFor(_dbPath);
        foreach (var ext in new[] { "", "-wal", "-shm" })
        {
            var p = _dbPath + ext;
            if (File.Exists(p)) { try { File.Delete(p); } catch { } }
        }
    }

    /// <summary>Mini-index: the objects the golden scaffolds reference.</summary>
    private void SeedFixtureIndex()
    {
        _repo.ApplyExtract(new ExtractBatch(
            Model: "ApplicationSuite",
            Publisher: "Microsoft",
            Layer: "app",
            IsCustom: false,
            Tables: new[]
            {
                new ExtractedTable("CustTable", "@SYS1234", "x", new[]
                {
                    new ExtractedTableField("AccountNum", "ExtendedDataType", "CustAccount", null, true),
                })
                {
                    TableGroup = "Main",
                    ClusteredIndex = "AccountIdx",
                    Indexes = new[] { new ExtractedTableIndex("AccountIdx", false, true, new[] { "AccountNum" }) },
                    Methods = new[] { new ExtractedMethod("validateWrite", "boolean validateWrite()", "boolean", false) },
                },
            },
            Classes: new[]
            {
                new ExtractedClass("SalesFormLetter", null, true, false, "x", new[]
                {
                    new ExtractedMethod("salute", "void salute(str message = \"Hi\")", "void", false),
                }),
            },
            Edts: new[] { new ExtractedEdt("CustAccount", null, "String", null, 20) },
            Enums: Array.Empty<ExtractedEnum>(),
            MenuItems: Array.Empty<ExtractedMenuItem>(),
            CocExtensions: Array.Empty<ExtractedCoc>(),
            Labels: new[] { new ExtractedLabel("MyModel", "en-us", "TableLabel", "Golden table") }));
    }

    private static string Source(XDocument doc) =>
        string.Join("\n", doc.Root!.Descendants()
            .Where(e => e.Name.LocalName is "Declaration" or "Source")
            .Select(e => e.Value));

    private static void AssertNoXppErrors(string xpp, string context)
    {
        var errors = XppValidator.Validate(xpp)
            .Where(v => v.Severity == "error")
            .ToList();
        Assert.True(errors.Count == 0,
            $"{context}: scaffolded X++ must pass validate xpp clean, got: " +
            string.Join("; ", errors.Select(e => $"{e.Rule} {e.Excerpt}")));
    }

    private void AssertNoReferenceErrors(string xpp, string context)
    {
        var errors = ReferenceResolver.Resolve(xpp, _repo).Violations
            .Where(v => v.Severity == "error")
            .ToList();
        Assert.True(errors.Count == 0,
            $"{context}: scaffolded X++ must resolve clean against the index, got: " +
            string.Join("; ", errors.Select(e => $"{e.Kind} {e.Identifier}")));
    }

    // ── 1+2: scaffolder outputs pass both gates ─────────────────────────────

    [Fact]
    public void Coc_on_class_passes_both_gates()
    {
        var src = Source(XppScaffolder.CocExtension("SalesFormLetter", "class", "salute"));
        AssertNoXppErrors(src, "generate coc (class)");
        AssertNoReferenceErrors(src, "generate coc (class)");
    }

    [Fact]
    public void Coc_on_table_uses_tableStr_and_passes_both_gates()
    {
        var doc = XppScaffolder.CocExtension("CustTable", "table", "validateWrite");
        var src = Source(doc);
        Assert.Contains("tableStr(CustTable)", src); // not classStr — compiler would reject it
        AssertNoXppErrors(src, "generate coc (table)");
        AssertNoReferenceErrors(src, "generate coc (table)");
    }

    // ── #65: <Methods> must be nested inside <SourceCode>, not a sibling.
    // A misplaced <Methods> deserializes to a class with no methods at all —
    // the AOT loads it "empty". Source()'s Descendants() walk can't catch this,
    // so assert the structure explicitly.
    [Fact]
    public void Coc_methods_are_nested_inside_sourcecode()
    {
        var doc = XppScaffolder.CocExtension("CustTable", "table", "validateWrite");
        var sourceCode = doc.Root!.Element("SourceCode");
        Assert.NotNull(sourceCode);

        // The method lives under SourceCode/Methods, and nowhere else.
        var nestedMethod = sourceCode!.Element("Methods")?.Elements("Method")
            .FirstOrDefault(m => (string?)m.Element("Name") == "validateWrite");
        Assert.NotNull(nestedMethod);
        Assert.Null(doc.Root!.Element("Methods")); // not a sibling of SourceCode
    }

    [Fact]
    public void Class_scaffold_passes_xpp_gate()
    {
        var src = Source(XppScaffolder.Class("GoldenHelper"));
        AssertNoXppErrors(src, "generate class");
        AssertNoReferenceErrors(src, "generate class");
    }

    [Fact]
    public void Table_scaffold_passes_xml_table_gate()
    {
        var doc = XppScaffolder.Table("GoldenTable", "@MyModel:TableLabel",
            new[] { new TableFieldSpec("AccountNum", "CustAccount", null, Mandatory: true) },
            pattern: TablePattern.Main);

        // Contract order is applied on the way out — by GenerateInstaller.Emit for the bridge
        // and by ScaffoldFileWriter for the disk path — so validate the shape that actually
        // ships. Without it XML006 fires here: the raw scaffold lists Fields before
        // FieldGroups, and a shipped AxTable puts FieldGroups first.
        ContractOrderCanonicalizer.Apply(doc);

        var violations = XppValidator.Validate(doc.ToString(), XppValidator.CodeTypeXmlTable)
            .Where(v => v.Severity == "error")
            .ToList();
        Assert.True(violations.Count == 0,
            "generate table: XML must pass property rules (Label, TableGroup, AlternateKey), got: " +
            string.Join("; ", violations.Select(v => $"{v.Rule} {v.Excerpt}")));
    }

    [Fact]
    public void Event_handler_scaffold_passes_xpp_gate()
    {
        var src = Source(XppScaffolder.EventHandler("GoldenHandler", "Table", "CustTable", "Inserted", "OnInserted"));
        AssertNoXppErrors(src, "generate event-handler");
    }

    [Fact]
    public void RunBase_and_sysoperation_scaffolds_pass_xpp_gate()
    {
        AssertNoXppErrors(Source(RunBaseScaffolder.RunBaseClass("GoldenJob", isBatch: true)), "generate runbase");
        AssertNoXppErrors(Source(SysOperationScaffolder.Contract("GoldenContract")), "generate sysoperation contract");
        AssertNoXppErrors(Source(SysOperationScaffolder.Controller("GoldenController", "GoldenService", "process")), "generate sysoperation controller");
    }

    // ── 3: hallucinations must be caught ────────────────────────────────────

    [Fact]
    public void Hallucinated_table_is_caught()
    {
        var r = ReferenceResolver.Resolve("str s = tableStr(CustTableThatDoesNotExist);", _repo);
        Assert.Contains(r.Violations, v => v.Severity == "error" && v.Kind == "unknown-intrinsic-target");
    }

    [Fact]
    public void Hallucinated_field_is_caught()
    {
        var code = """
            CustTable custTable;
            select custTable;
            str s = custTable.CustomerName;
            """;
        var r = ReferenceResolver.Resolve(code, _repo);
        Assert.Contains(r.Violations, v => v.Severity == "error" && v.Kind == "unknown-field");
    }

    [Fact]
    public void Hallucinated_label_in_known_file_is_caught()
    {
        var r = ReferenceResolver.Resolve("info(\"@MyModel:NoSuchLabel\");", _repo);
        Assert.Contains(r.Violations, v => v.Severity == "error" && v.Kind == "unknown-label");
    }

    [Fact]
    public void Wrong_arity_is_caught()
    {
        var r = ReferenceResolver.Resolve("SalesFormLetter::salute(1, 2, 3);", _repo);
        Assert.Contains(r.Violations, v => v.Severity == "error" && v.Kind == "arity-mismatch");
    }

    [Fact]
    public void Coc_wrapper_with_copied_default_param_is_caught()
    {
        var bad = """
            [ExtensionOf(classStr(SalesFormLetter))]
            final class SalesFormLetter_Extension
            {
                public void salute(str message = "Hi")
                {
                    next salute(message);
                }
            }
            """;
        Assert.Contains(XppValidator.Validate(bad), v => v.Rule == "COC001" && v.Severity == "error");
    }
}
