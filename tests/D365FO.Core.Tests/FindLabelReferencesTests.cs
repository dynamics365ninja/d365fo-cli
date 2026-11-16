using System.Text.RegularExpressions;
using D365FO.Core.Index;
using D365FO.Mcp;
using Xunit;

namespace D365FO.Core.Tests;

/// <summary>
/// Where-used for a label reference (<c>@SYS12345</c>, <c>@File:Key</c>). Two models:
/// <c>TestModel</c> (the MiniAot fixture, source-indexed, so in the default scope) and
/// <c>StdModel</c> (a standard model with no source index) that uses <c>@SYS340678</c> in code,
/// in a table's Label, in a field's Label and in a field's HelpText.
/// </summary>
/// <remarks>
/// The needle became <c>\b@SYS340678\b</c>. <c>@</c> is not a word character, so the <c>\b</c>
/// before it matched only after a letter or digit, never after the quote or space a label follows:
/// every disk scan for a label answered zero. And labels are mostly used in metadata properties,
/// which the search never read: <c>--all-models</c> spent five minutes looking in the wrong place.
/// </remarks>
public sealed class FindLabelReferencesTests : IDisposable
{
    private static readonly string SamplesDir =
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "Samples", "MiniAot"));

    private static readonly string[] NoCustomModels = [];

    private readonly string _root = Path.Combine(Path.GetTempPath(), $"d365fo-labelrefs-{Guid.NewGuid():N}");
    private readonly string _db;
    private readonly MetadataRepository _repo;

    public FindLabelReferencesTests()
    {
        _db = Path.Combine(_root, "index.sqlite");
        var packages = Path.Combine(_root, "packages");
        CopyDir(Path.Combine(SamplesDir, "TestModel"), Path.Combine(packages, "TestModel"));
        WriteClass(Path.Combine(packages, "TestModel", "TestModel", "AxClass"), "FmLookalike", "describe",
            "str s = \"Fleet Found\";");

        var std = Path.Combine(packages, "StdModel", "StdModel");
        Directory.CreateDirectory(Path.Combine(std, "AxClass"));
        Directory.CreateDirectory(Path.Combine(std, "AxTable"));
        WriteClass(Path.Combine(std, "AxClass"), "StdLabelUser", "check",
            "if (!ok) throw error(\"@SYS340678\");");
        WriteClass(Path.Combine(std, "AxClass"), "StdLongerLabelUser", "check",
            "warning(\"@SYS3406789\");");
        File.WriteAllText(Path.Combine(std, "AxTable", "StdLabelTable.xml"), """
            <?xml version="1.0" encoding="utf-8"?>
            <AxTable xmlns:i="http://www.w3.org/2001/XMLSchema-instance">
              <Name>StdLabelTable</Name>
              <Label>@SYS340678</Label>
              <Fields>
                <AxTableField i:type="AxTableFieldString">
                  <Name>Code</Name>
                  <Label>@SYS340678</Label>
                  <HelpText>@SYS340678</HelpText>
                </AxTableField>
                <AxTableField i:type="AxTableFieldString">
                  <Name>Other</Name>
                  <Label>@SYS34067</Label>
                </AxTableField>
              </Fields>
            </AxTable>
            """);

        _repo = new MetadataRepository(_db);
        _repo.EnsureSchema();
        Assert.True(IndexSync.Sync("TestModel", null, packages, _db, indexSource: true).Ok);
        Assert.True(IndexSync.Sync("StdModel", null, packages, _db, indexSource: false).Ok);
    }

    public void Dispose()
    {
        SqlitePool.ReleaseFor(_db);
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private SourceRefResult Find(string needle, string? model = null, bool allModels = false, int lines = 3) =>
        MethodSourceSearch.Find(_repo, needle, kind: null, model: model, limit: 50, sampleLinesPerHit: lines,
            allModels: allModels, customModelPatterns: NoCustomModels);

    // ─── the token match ─────────────────────────────────────────────────────

    [Theory]
    [InlineData("throw error(\"@SYS340678\");")]
    [InlineData("info( @SYS340678 );")]
    [InlineData("<Label>@SYS340678</Label>")]
    [InlineData("strFmt('@SYS340678', x)")]
    public void A_label_token_matches_after_a_quote_a_space_or_a_tag(string text)
    {
        // What the needle used to become: never true after a non-word character.
        Assert.DoesNotMatch(new Regex(@"\b@SYS340678\b", RegexOptions.IgnoreCase), text);
        Assert.Matches(MethodSourceSearch.TokenRegex("@SYS340678"), text);
    }

    [Theory]
    [InlineData("@SYS12", "\"@SYS123\"")]
    [InlineData("@SYS123", "\"@SYS12\"")]
    [InlineData("@Fleet:Vehicle", "\"@Fleet:VehicleLine\"")]
    [InlineData("@Fleet", "\"@Fleet:Vehicle\"")]
    [InlineData("@SYS12", "x@@SYS12")]
    public void A_label_token_does_not_match_a_longer_or_shorter_label(string needle, string text) =>
        Assert.DoesNotMatch(MethodSourceSearch.TokenRegex(needle), text);

    [Theory]
    [InlineData("@SYS340678", true)]
    [InlineData("@Fleet:Vehicle", true)]
    [InlineData(" @Fleet:Vehicle ", true)]
    [InlineData("CustTable", false)]
    [InlineData("Owner.member", false)]
    [InlineData("@", false)]
    public void A_label_reference_is_recognised_by_its_shape(string needle, bool expected) =>
        Assert.Equal(expected, MethodSourceSearch.IsLabelReference(needle));

    // ─── code ────────────────────────────────────────────────────────────────

    [Fact]
    public void A_label_in_code_is_found_by_the_disk_scan_instead_of_answering_zero()
    {
        var result = Find("@SYS340678", model: "StdModel");

        Assert.Equal("scan", result.Via);
        var hit = Assert.Single(result.Hits);
        Assert.Equal("StdLabelUser", hit.Name);
        Assert.Contains("@SYS340678", Assert.Single(hit.Matches).Text);
    }

    [Fact]
    public void A_label_in_code_is_found_from_the_source_index_with_its_line()
    {
        var result = Find("@Fleet:Found", model: "TestModel");

        Assert.Equal("fts", result.Via);
        var hit = Assert.Single(result.Hits);
        Assert.Equal(("FmVehicleService", "run"), (hit.Name, hit.Method));
        Assert.Contains("@Fleet:Found", Assert.Single(hit.Matches).Text);
    }

    [Fact]
    public void The_source_index_drops_a_method_that_only_has_the_label_words()
    {
        // The FTS tokenizer reads "@Fleet:Found" as the words "fleet found". FmLookalike has
        // them without the '@', which is not a use of the label.
        var result = Find("@Fleet:Found", model: "TestModel", lines: 0);

        Assert.Equal("fts", result.Via);
        Assert.Equal("FmVehicleService", Assert.Single(result.Hits).Name);
        Assert.Empty(result.Hits[0].Matches); // lines: 0 still returns no sample lines
    }

    // ─── metadata properties ─────────────────────────────────────────────────

    [Fact]
    public void A_label_in_metadata_names_the_object_the_element_and_the_property()
    {
        var result = Find("@Fleet:Vehicle");

        var props = result.Properties!;
        Assert.Contains(props, p => p is { Kind: "Table", Name: "FmVehicle", Model: "TestModel", Element: null, Property: "Label", Via: "disk" });
        Assert.Contains(props, p => p is { Kind: "Form", Name: "FmVehicleList", Element: "Design", Property: "Caption" });
        Assert.Contains(props, p => p.Name == "FmVehicleList" && p.Property == "Text"
                                    && p.Element!.StartsWith("Design/", StringComparison.Ordinal)
                                    && p.Element.EndsWith("/RunServiceButton", StringComparison.Ordinal));
        // @Fleet:VehicleLine and @Fleet:VehicleStatus are other labels.
        Assert.DoesNotContain(props, p => p.Name is "FmVehicleLine" or "FmVehicleStatus");
    }

    [Fact]
    public void A_model_outside_the_scope_is_answered_from_the_indexed_label_columns()
    {
        var result = Find("@SYS340678");

        var props = result.Properties!;
        Assert.Contains(props, p => p is { Kind: "Table", Name: "StdLabelTable", Model: "StdModel", Element: null, Property: "Label", Via: "index" });
        Assert.Contains(props, p => p is { Name: "StdLabelTable", Element: "Fields/Code", Property: "Label", Via: "index" });
        // HelpText is not in the index, and StdModel's XML is not read in the default scope.
        Assert.DoesNotContain(props, p => p.Property == "HelpText");
        // @SYS34067 is a shorter label.
        Assert.DoesNotContain(props, p => p.Element == "Fields/Other");

        var cov = result.PropertyCoverage!;
        Assert.Equal(["StdModel"], cov.FromIndex);
        Assert.Equal(["TestModel"], cov.FromDisk);
        Assert.Contains("HelpText", cov.Note);
        Assert.Contains("--model", cov.Note);
        // A label found in metadata is not "unused": no not-searched caveat.
        Assert.Null(result.Caveat);
    }

    [Fact]
    public void Naming_a_model_reads_its_xml_so_every_property_is_searched()
    {
        var result = Find("@SYS340678", model: "StdModel");

        var props = result.Properties!;
        Assert.Contains(props, p => p is { Element: "Fields/Code", Property: "HelpText", Via: "disk" });
        Assert.Contains(props, p => p is { Element: "Fields/Code", Property: "Label", Via: "disk" });
        Assert.Contains(props, p => p is { Element: null, Property: "Label", Via: "disk" });
        // Read from disk, so not again from the index; and code is not reported as a property.
        Assert.Equal(3, props.Count);
        Assert.Equal(["StdModel"], result.PropertyCoverage!.FromDisk);
        Assert.Empty(result.PropertyCoverage.FromIndex);
    }

    [Fact]
    public void All_models_widens_the_code_scan_but_answers_standard_metadata_from_the_index()
    {
        var result = Find("@SYS340678", allModels: true);

        Assert.Contains(result.Hits, h => h is { Name: "StdLabelUser", Model: "StdModel" });
        Assert.DoesNotContain(result.Hits, h => h.Name == "StdLongerLabelUser");
        Assert.Contains(result.Properties!, p => p is { Model: "StdModel", Via: "index" });
        Assert.Equal(["StdModel"], result.PropertyCoverage!.FromIndex);
    }

    [Fact]
    public void A_needle_that_is_not_a_label_has_no_property_search()
    {
        var result = Find("FmVehicleService");

        Assert.Null(result.Properties);
        Assert.Null(result.PropertyCoverage);
    }

    [Fact]
    public void An_unknown_label_gets_a_hint_not_an_error()
    {
        Assert.Contains("not a label in the index", Find("@Fleet:NoSuchLabel").Hint);
    }

    // ─── MCP / CLI shape ─────────────────────────────────────────────────────

    [Fact]
    public void Find_references_lists_properties_and_their_coverage_for_a_label()
    {
        var json = D365Json.Serialize(new ToolHandlers(_repo).FindReferences("@SYS340678", null, "StdModel", 50));

        Assert.Contains("\"properties\":[", json, StringComparison.Ordinal);
        Assert.Contains("\"property\":\"HelpText\"", json, StringComparison.Ordinal);
        Assert.Contains("\"element\":\"Fields/Code\"", json, StringComparison.Ordinal);
        Assert.Contains("\"propertyCount\":3", json, StringComparison.Ordinal);
        Assert.Contains("\"coverage\":{", json, StringComparison.Ordinal);
        Assert.Contains("\"properties\":{\"fromIndex\":{\"count\":0", json, StringComparison.Ordinal);
        Assert.Contains("\"methods\":[\"check\"]", json, StringComparison.Ordinal);

        var withLines = D365Json.Serialize(new ToolHandlers(_repo).FindReferences("@SYS340678", null, "StdModel", 50, lines: 1));
        Assert.Contains("\"property\":\"HelpText\"", withLines, StringComparison.Ordinal);

        var plain = D365Json.Serialize(new ToolHandlers(_repo).FindReferences("FmVehicleService", null, null, 50));
        Assert.DoesNotContain("\"properties\"", plain, StringComparison.Ordinal);
    }

    private static void WriteClass(string dir, string name, string method, string statement) =>
        File.WriteAllText(Path.Combine(dir, name + ".xml"), $$"""
            <?xml version="1.0" encoding="utf-8"?>
            <AxClass xmlns:i="http://www.w3.org/2001/XMLSchema-instance">
              <Name>{{name}}</Name>
              <SourceCode>
                <Declaration><![CDATA[
            class {{name}}
            {
            }
            ]]></Declaration>
                <Methods>
                  <Method>
                    <Name>{{method}}</Name>
                    <Source><![CDATA[
            public void {{method}}()
            {
                {{statement}}
            }
            ]]></Source>
                  </Method>
                </Methods>
              </SourceCode>
            </AxClass>
            """);

    private static void CopyDir(string from, string to)
    {
        foreach (var dir in Directory.EnumerateDirectories(from, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(Path.Combine(to, Path.GetRelativePath(from, dir)));
        Directory.CreateDirectory(to);
        foreach (var file in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
            File.Copy(file, Path.Combine(to, Path.GetRelativePath(from, file)), overwrite: true);
    }
}
