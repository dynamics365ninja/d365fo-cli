using System.Collections;
using D365FO.Core.Extract;
using D365FO.Core.Index;
using D365FO.Mcp;
using Xunit;

namespace D365FO.Core.Tests;

/// <summary>
/// <c>find refs</c> / <c>find_references</c> answer "who uses X" with one row per object by
/// default; the per-method rows with sample lines and paths are opt-in.
/// </summary>
/// <remarks>
/// An outside benchmark measured the per-method shape at 3–30× the tokens of a names-only
/// answer to the same question. Covered over both corpora the text scan can read: the opt-in
/// method-source FTS index, and the disk scan without it.
/// </remarks>
public class FindReferencesNamesOnlyTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"d365fo-refnames-{Guid.NewGuid():N}");
    private readonly string _dbPath;
    private readonly MetadataRepository _repo;

    public FindReferencesNamesOnlyTests()
    {
        var classes = Path.Combine(_root, "packages", "ConPkg", "ConModel", "AxClass");
        Directory.CreateDirectory(classes);
        File.WriteAllText(Path.Combine(classes, "ConCaller.xml"), Class("ConCaller",
            ("first", "ConTarget t = ConTarget::construct();"),
            ("second", "ConTarget::construct().run();\n        ConTarget::construct().run();"),
            ("third", "ConTarget::doIt();"),
            ("unrelated", "info('nothing here');")));
        File.WriteAllText(Path.Combine(classes, "ConOther.xml"), Class("ConOther",
            ("only", "ConTarget::doIt();")));

        _dbPath = Path.Combine(_root, "index.sqlite");
        _repo = new MetadataRepository(_dbPath);
        _repo.EnsureSchema();
    }

    public void Dispose()
    {
        SqlitePool.ReleaseFor(_dbPath);
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { /* best-effort temp cleanup */ }
        GC.SuppressFinalize(this);
    }

    private static string Class(string name, params (string Method, string Body)[] methods)
    {
        var body = string.Concat(methods.Select(m =>
            "      <Method>\n" +
            $"        <Name>{m.Method}</Name>\n" +
            $"        <Source><![CDATA[    public void {m.Method}()\n    {{\n        {m.Body}\n    }}]]></Source>\n" +
            "      </Method>\n"));
        return "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n" +
               "<AxClass xmlns:i=\"http://www.w3.org/2001/XMLSchema-instance\">\n" +
               $"  <Name>{name}</Name>\n" +
               "  <SourceCode>\n" +
               $"    <Declaration><![CDATA[public class {name} {{}}]]></Declaration>\n" +
               "    <Methods>\n" + body + "    </Methods>\n" +
               "  </SourceCode>\n" +
               "</AxClass>\n";
    }

    private void Ingest(bool indexSource)
    {
        var ex = new MetadataExtractor { CaptureMethodSource = indexSource };
        foreach (var batch in ex.ExtractAll(Path.Combine(_root, "packages")))
            _repo.ApplyExtract(batch, sourceFingerprint: null, indexMethodSource: indexSource);
    }

    private static object? Prop(object o, string name) => o.GetType().GetProperty(name)?.GetValue(o);

    private static List<object> Items(ToolResult<object> r) =>
        ((IEnumerable)Prop(r.Data!, "items")!).Cast<object>().ToList();

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Default_is_one_row_per_object_with_its_matching_methods(bool indexSource)
    {
        Ingest(indexSource);

        var r = new ToolHandlers(_repo).FindReferences("ConTarget", kind: null, model: null);

        Assert.True(r.Ok);
        Assert.Equal("names", Prop(r.Data!, "mode"));
        var items = Items(r);
        Assert.Equal(2, items.Count);
        Assert.Equal(2, Prop(r.Data!, "count"));
        Assert.Equal(4, Prop(r.Data!, "matchedMethods"));

        var caller = items.Single(i => (string)Prop(i, "name")! == "ConCaller");
        Assert.Equal(new[] { "first", "second", "third" },
            ((IEnumerable<string>)Prop(caller, "methods")!).OrderBy(m => m));
        // No sample lines and no path: those are what `lines` is for.
        Assert.Null(caller.GetType().GetProperty("matches"));
        Assert.Null(caller.GetType().GetProperty("path"));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Lines_returns_one_row_per_method_with_that_many_sample_lines(bool indexSource)
    {
        Ingest(indexSource);

        var r = new ToolHandlers(_repo).FindReferences("ConTarget", kind: null, model: null, lines: 1);

        Assert.True(r.Ok);
        Assert.Equal("methods", Prop(r.Data!, "mode"));
        var items = Items(r);
        Assert.Equal(4, items.Count);
        var second = items.Single(i => (string)Prop(i, "method")! == "second");
        Assert.Single(((IEnumerable)Prop(second, "matches")!).Cast<object>());
        Assert.NotNull(Prop(second, "path"));
    }

    [Fact]
    public void The_limit_caps_objects_and_says_there_are_more()
    {
        Ingest(indexSource: true);

        var r = new ToolHandlers(_repo).FindReferences("ConTarget", kind: null, model: null, limit: 1);

        Assert.Single(Items(r));
        Assert.Equal(true, Prop(r.Data!, "truncated"));
    }

    [Fact]
    public void An_unknown_kind_fails_instead_of_matching_nothing()
    {
        Ingest(indexSource: true);

        var r = new ToolHandlers(_repo).FindReferences("ConTarget", kind: "clas", model: null);

        Assert.False(r.Ok);
        Assert.Equal(D365FoErrorCodes.BadInput, r.Error!.Code);
    }

    [Fact]
    public void A_known_kind_still_narrows_the_scan()
    {
        Ingest(indexSource: true);

        Assert.Equal(2, Prop(new ToolHandlers(_repo).FindReferences("ConTarget", "Class", null).Data!, "count"));
        Assert.Equal(0, Prop(new ToolHandlers(_repo).FindReferences("ConTarget", "form", null).Data!, "count"));
    }
}
