using D365FO.Core.Index;
using Xunit;

namespace D365FO.Core.Tests;

/// <summary>
/// The text scan does not report a method's own declaration as a reference to it, and says that
/// a table's declarative uses are out of its sight.
/// </summary>
public sealed class FindReferencesDeclarationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"d365fo-declrefs-{Guid.NewGuid():N}");
    private readonly string _db;
    private readonly MetadataRepository _repo;
    private readonly string _packages;

    public FindReferencesDeclarationTests()
    {
        _db = Path.Combine(_root, "index.sqlite");
        _packages = Path.Combine(_root, "packages");
        var model = Path.Combine(_packages, "DeclModel", "DeclModel");
        Directory.CreateDirectory(Path.Combine(model, "AxClass"));
        Directory.CreateDirectory(Path.Combine(model, "AxTable"));
        WriteClass(model, "DeclOnly", "recalculate", "this.run();");
        WriteClass(model, "Recursive", "recalculate", "if (depth) this.recalculate(depth - 1);");
        WriteClass(model, "Caller", "run", "worker.recalculate();");
        // The name only in a comment and a string of its own body.
        WriteClass(model, "Talker", "recalculate", "// recalculate() later\n    info('recalculate()');");
        File.WriteAllText(Path.Combine(model, "AxTable", "DeclTable.xml"),
            "<AxTable><Name>DeclTable</Name><Fields /></AxTable>");

        _repo = new MetadataRepository(_db);
        _repo.EnsureSchema();
    }

    public void Dispose()
    {
        SqlitePool.ReleaseFor(_db);
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private static void WriteClass(string model, string name, string method, string body) =>
        File.WriteAllText(Path.Combine(model, "AxClass", name + ".xml"), $"""
            <?xml version="1.0" encoding="utf-8"?>
            <AxClass>
              <Name>{name}</Name>
              <SourceCode>
                <Declaration><![CDATA[class {name} {'{'} {'}'}]]></Declaration>
                <Methods>
                  <Method>
                    <Name>{method}</Name>
                    <Source><![CDATA[public void {method}(int depth = 0)
            {'{'}
                {body}
            {'}'}]]></Source>
                  </Method>
                </Methods>
              </SourceCode>
            </AxClass>
            """);

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void A_declaration_is_not_a_reference_but_a_recursive_call_is(bool indexSource)
    {
        Assert.True(IndexSync.Sync("DeclModel", null, _packages, _db, indexSource: indexSource).Ok);

        var result = MethodSourceSearch.Find(_repo, "recalculate", kind: null, model: "DeclModel", limit: 50);

        Assert.Equal(indexSource ? "fts" : "scan", result.Via);
        Assert.Equal(new[] { "Caller", "Recursive" }, result.Hits.Select(h => h.Name).Order());
        var recursive = result.Hits.Single(h => h.Name == "Recursive");
        Assert.Contains("this.recalculate", Assert.Single(recursive.Matches).Text);
    }

    // A method of the owner counts even when it does not name its own class.
    [Fact]
    public void The_owners_own_recursive_call_is_a_reference_to_the_member()
    {
        Assert.True(IndexSync.Sync("DeclModel", null, _packages, _db, indexSource: false).Ok);

        var result = MethodSourceSearch.Find(_repo, "Recursive.recalculate", kind: null, model: "DeclModel", limit: 50);

        Assert.Equal("Recursive", Assert.Single(result.Hits).Name);
    }

    [Fact]
    public void A_table_answer_says_that_metadata_uses_are_not_in_it()
    {
        Assert.True(IndexSync.Sync("DeclModel", null, _packages, _db, indexSource: false).Ok);

        var result = MethodSourceSearch.Find(_repo, "DeclTable", kind: null, model: "DeclModel", limit: 50);

        Assert.Contains("--structural", result.Hint);
    }
}
