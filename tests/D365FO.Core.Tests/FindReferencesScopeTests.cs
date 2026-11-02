using D365FO.Core.Index;
using D365FO.Mcp;
using Xunit;

namespace D365FO.Core.Tests;

/// <summary>
/// Which models a method-body search reads, and from where. Two models: <c>TestModel</c> (the
/// MiniAot fixture, source-indexed) and <c>StdModel</c> (a standard model with no source index,
/// one class that calls into TestModel).
/// </summary>
/// <remarks>
/// The search used to switch to the index as soon as ANY model had FTS rows. Without --model that
/// answered class and table callers from the indexed models only, said nothing about the rest, and
/// still opened every form file of every model from disk (104 s on a UDE). With --model naming an
/// unindexed model it answered zero from an index that held nothing for it.
/// </remarks>
public sealed class FindReferencesScopeTests : IDisposable
{
    private static readonly string SamplesDir =
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "Samples", "MiniAot"));

    private static readonly string[] NoCustomModels = [];

    private readonly string _root = Path.Combine(Path.GetTempPath(), $"d365fo-refscope-{Guid.NewGuid():N}");
    private readonly string _db;
    private readonly MetadataRepository _repo;

    public FindReferencesScopeTests()
    {
        _db = Path.Combine(_root, "index.sqlite");
        var packages = Path.Combine(_root, "packages");
        CopyDir(Path.Combine(SamplesDir, "TestModel"), Path.Combine(packages, "TestModel"));

        var stdClasses = Path.Combine(packages, "StdModel", "StdModel", "AxClass");
        Directory.CreateDirectory(stdClasses);
        WriteClass(stdClasses, "StdVehicleCaller", "callService",
            "FmVehicleService::construct().run();");
        WriteClass(stdClasses, "StdOtherCaller", "callOther",
            "OtherService::construct();");

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

    private SourceRefResult Find(string needle, string? model = null, bool allModels = false,
        IEnumerable<string>? custom = null) =>
        MethodSourceSearch.Find(_repo, needle, kind: null, model: model, limit: 50,
            allModels: allModels, customModelPatterns: custom ?? NoCustomModels);

    [Fact]
    public void Default_scope_answers_indexed_models_from_fts_and_says_which_models_it_left_out()
    {
        var result = Find("FmVehicleService");

        Assert.Equal("fts", result.Via);
        Assert.All(result.Hits, h => Assert.Equal("TestModel", h.Model));
        // TestModel's forms are in the index too, so nothing is read from disk.
        Assert.Equal(0, result.FilesScanned);
        var cov = result.Coverage!;
        Assert.Equal("default", cov.Scope);
        Assert.Equal(["TestModel"], cov.FromIndex);
        Assert.Empty(cov.FromDisk);
        Assert.Equal(["StdModel"], cov.NotSearched);
        Assert.Contains("--all-models", cov.Note);
    }

    [Fact]
    public void All_models_scans_the_models_without_source_index_from_disk()
    {
        var result = Find("FmVehicleService", allModels: true);

        Assert.Contains(result.Hits, h => h is { Model: "StdModel", Name: "StdVehicleCaller", Method: "callService" });
        Assert.Contains(result.Hits, h => h.Model == "TestModel");
        var cov = result.Coverage!;
        Assert.Equal("all", cov.Scope);
        Assert.Equal(["TestModel"], cov.FromIndex);
        Assert.Equal(["StdModel"], cov.FromDisk);
        Assert.Empty(cov.NotSearched);
        // Only StdModel's files were opened: TestModel answered from the index.
        Assert.Equal(2, result.FilesScanned);
    }

    [Fact]
    public void A_custom_model_without_source_index_is_in_the_default_scope_and_read_from_disk()
    {
        var result = Find("FmVehicleService", custom: ["Std*"]);

        Assert.Contains(result.Hits, h => h.Model == "StdModel");
        Assert.Equal(["StdModel"], result.Coverage!.FromDisk);
        Assert.Empty(result.Coverage.NotSearched);
    }

    [Fact]
    public void Naming_a_model_without_source_index_scans_it_instead_of_answering_zero_from_fts()
    {
        var result = Find("FmVehicleService", model: "StdModel");

        Assert.Equal("scan", result.Via);
        Assert.Equal("StdVehicleCaller", Assert.Single(result.Hits).Name);
        Assert.Equal("model", result.Coverage!.Scope);
        Assert.Equal(["StdModel"], result.Coverage.FromDisk);
        Assert.Equal(["TestModel"], result.Coverage.NotSearched);
    }

    [Fact]
    public void An_empty_default_scope_answer_says_models_were_not_searched()
    {
        var result = Find("OtherService");

        Assert.Empty(result.Hits);
        Assert.Contains("not searched", result.Caveat);
        Assert.Contains("--all-models", result.Caveat);
    }

    [Fact]
    public void An_empty_answer_for_a_named_model_does_not_point_at_all_models()
    {
        var result = Find("OtherService", model: "TestModel");

        Assert.Empty(result.Hits);
        Assert.Equal(["StdModel"], result.Coverage!.NotSearched);
        Assert.DoesNotContain("--all-models", result.Caveat ?? "");
    }

    [Fact]
    public void Model_names_that_differ_only_in_case_do_not_break_the_search()
    {
        using (var conn = new Microsoft.Data.Sqlite.SqliteConnection(_repo.ConnectionString))
        {
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "INSERT INTO Models(Name, IsCustom, SourceIndexed) VALUES('testmodel', 0, 0);";
            cmd.ExecuteNonQuery();
        }

        var result = Find("FmVehicleService");

        Assert.Contains(result.Hits, h => h.Model == "TestModel");
        Assert.Equal(0, result.FilesScanned);
    }

    [Fact]
    public void With_nothing_source_indexed_or_custom_every_model_is_scanned_and_the_note_says_why()
    {
        Assert.True(IndexSync.Sync("TestModel", null, Path.Combine(_root, "packages"), _db, indexSource: false).Ok);

        var result = Find("FmVehicleService");

        Assert.Equal("scan", result.Via);
        Assert.Equal("all", result.Coverage!.Scope);
        Assert.Contains("--index-source", result.Coverage.Note);
        Assert.Contains(result.Hits, h => h.Model == "StdModel");
    }

    // ─── Owner.member without --xref ─────────────────────────────────────────

    [Theory]
    [InlineData("FmVehicleService.construct")]
    [InlineData("FmVehicleService::construct")]
    public void Owner_dot_member_finds_the_callers_instead_of_a_silent_zero(string needle)
    {
        var result = Find(needle, allModels: true);

        Assert.Equal(new MemberNeedle("FmVehicleService", "construct"), result.Member);
        Assert.Contains(result.Hits, h => h is { Name: "StdVehicleCaller", Method: "callService" });
        Assert.Contains(result.Hits, h => h is { Kind: "Form", Name: "FmVehicleList", Method: "RunServiceButton.clicked" });
        // A method of the owner itself counts even when it does not name its own class.
        Assert.Contains(result.Hits, h => h is { Name: "FmVehicleService", Method: "construct" });
        Assert.All(result.Hits, h => Assert.NotEmpty(h.Matches));
        Assert.Null(result.Hint);
    }

    [Fact]
    public void Owner_dot_member_drops_methods_that_name_the_member_but_not_the_owner()
    {
        var result = Find("FmVehicleService.construct", allModels: true);

        Assert.DoesNotContain(result.Hits, h => h.Name == "StdOtherCaller");
    }

    [Fact]
    public void Owner_dot_member_gives_the_same_hits_from_disk_as_from_the_index()
    {
        var fromIndex = Find("FmVehicleService.construct", model: "TestModel");
        Assert.True(IndexSync.Sync("TestModel", null, Path.Combine(_root, "packages"), _db, indexSource: false).Ok);
        var fromDisk = Find("FmVehicleService.construct", model: "TestModel");

        Assert.Equal("fts", fromIndex.Via);
        Assert.Equal("scan", fromDisk.Via);
        Assert.Equal(
            fromIndex.Hits.Select(h => (h.Kind, h.Name, h.Method)).OrderBy(x => x.ToString()),
            fromDisk.Hits.Select(h => (h.Kind, h.Name, h.Method)).OrderBy(x => x.ToString()));
    }

    // ─── hints for needles the index does not know ───────────────────────────

    [Fact]
    public void A_needle_the_index_does_not_know_gets_a_hint_not_an_error()
    {
        var unknown = Find("FmNoSuchSymbol");
        Assert.Contains("not a known object", unknown.Hint);

        Assert.Null(Find("FmVehicleService").Hint);
        Assert.Null(Find("callService").Hint);   // a method name
        Assert.Null(Find("VIN").Hint);           // a field name
        Assert.Contains("'NoSuchOwner' is not an object", Find("NoSuchOwner.construct").Hint);
    }

    // ─── MCP / CLI shape ─────────────────────────────────────────────────────

    [Fact]
    public void Find_references_adds_scope_and_coverage_without_touching_the_items()
    {
        var result = new ToolHandlers(_repo).FindReferences("FmVehicleService.construct", null, null, 50, allModels: true);

        Assert.True(result.Ok);
        var json = D365Json.Serialize(result);
        Assert.Contains("\"scope\":\"all\"", json, StringComparison.Ordinal);
        Assert.Contains("\"coverage\":{", json, StringComparison.Ordinal);
        Assert.Contains("\"kind\":\"member-text\"", json, StringComparison.Ordinal);
        Assert.Contains("\"methods\":[\"callService\"]", json, StringComparison.Ordinal);

        var withLines = D365Json.Serialize(new ToolHandlers(_repo).FindReferences(
            "FmVehicleService.construct", null, null, 50, lines: 1, allModels: true));
        Assert.Contains("\"scope\":\"all\"", withLines, StringComparison.Ordinal);
        Assert.Contains("\"method\":\"callService\"", withLines, StringComparison.Ordinal);
    }

    [Fact]
    public void All_models_with_xref_is_refused()
    {
        var result = new ToolHandlers(_repo).FindReferences("FmVehicleService", null, null, 50, xref: true, allModels: true);

        Assert.False(result.Ok);
        Assert.Equal(D365FoErrorCodes.BadInput, result.Error!.Code);
    }

    [Fact]
    public void All_models_with_a_named_model_is_refused()
    {
        var result = new ToolHandlers(_repo).FindReferences("FmVehicleService", null, "TestModel", 50, allModels: true);

        Assert.False(result.Ok);
        Assert.Equal(D365FoErrorCodes.BadInput, result.Error!.Code);
    }

    [Fact]
    public void A_long_model_list_in_coverage_is_reported_as_a_count()
    {
        var many = Enumerable.Range(0, MethodSourceSearch.CoverageListCap + 1).Select(i => "M" + i).ToList();
        var json = D365Json.Serialize(MethodSourceSearch.CoveragePayload(
            new SearchCoverage("default", ["TestModel"], [], many, Note: null)));

        Assert.Contains($"\"notSearched\":{{\"count\":{many.Count}}}", json, StringComparison.Ordinal);
        Assert.Contains("\"fromIndex\":{\"count\":1,\"models\":[\"TestModel\"]}", json, StringComparison.Ordinal);
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
