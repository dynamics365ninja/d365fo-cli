using System.Xml.Linq;
using D365FO.Core.Extract;
using D365FO.Core.Index;
using D365FO.Core.Validation;
using Xunit;

namespace D365FO.Core.Tests;

/// <summary>
/// What intrinsic members, Chain of Command wrappers, event handlers and obsolete calls bind
/// to, read from the declaration in the indexed AOT file.
/// </summary>
public sealed class ReferenceBindingTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"refbind-{Guid.NewGuid():N}");
    private readonly string _dbPath;
    private readonly MetadataRepository _repo;

    public ReferenceBindingTests()
    {
        Directory.CreateDirectory(_dir);
        _dbPath = Path.Combine(_dir, "index.sqlite");
        _repo = new MetadataRepository(_dbPath);
        _repo.EnsureSchema();
        Seed();
    }

    public void Dispose()
    {
        SqlitePool.ReleaseFor(_dbPath);
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private (string File, ExtractedMethod[] Methods) Aot(string type, params (string Name, string Source)[] methods)
    {
        var file = Path.Combine(_dir, type + ".xml");
        new XDocument(new XElement("AxClass",
            new XElement("Name", type),
            new XElement("SourceCode",
                new XElement("Methods", methods.Select(m => new XElement("Method",
                    new XElement("Name", m.Name),
                    new XElement("Source", new XCData(m.Source))))))))
            .Save(file);
        return (file, methods.Select(m => new ExtractedMethod(m.Name, null, null, false)).ToArray());
    }

    private void Seed()
    {
        var baseClass = Aot("PostBase",
            ("allowSave", "public boolean allowSave()\n{\n    return true;\n}"));
        var post = Aot("Poster",
            ("run", "public void run()\n{\n}"),
            ("count", "public int count(int _bookCount, AccountNum _account = '')\n{\n    return _bookCount;\n}"),
            ("construct", "public static Poster construct()\n{\n    return new Poster();\n}"),
            ("posted", "delegate void posted(int _bookCount, EventHandlerResult _result)\n{\n}"),
            ("created", "static delegate void created(Poster _poster)\n{\n}"),
            ("secret", "private void secret()\n{\n}"),
            ("hooked", "[Hookable(true)]\nprivate void hooked()\n{\n}"),
            ("sealedRun", "public final void sealedRun()\n{\n}"),
            ("openFinal", "[Wrappable(true)]\npublic final void openFinal()\n{\n}"),
            ("closed", "[Wrappable(false)]\npublic void closed()\n{\n}"),
            ("closedProtected", "[Wrappable(false)]\nprotected void closedProtected()\n{\n}"),
            ("noHook", "[Hookable(false)]\npublic void noHook()\n{\n}"),
            ("gone", "/// <summary>Old.</summary>\n[SysObsolete('Use run instead', true, 31\\12\\2020)]\npublic void gone()\n{\n}"),
            ("old", "[SysObsolete('Use run instead', false, 31\\12\\2020)]\npublic static void old()\n{\n}"));
        var table = Aot("OrderTable",
            ("validateWrite", "public boolean validateWrite()\n{\n    return super();\n}"),
            ("find", "public static OrderTable find(RecId _recId)\n{\n    return null;\n}"));

        _repo.ApplyExtract(new ExtractBatch(
            Model: "ApplicationSuite",
            Publisher: "Microsoft",
            Layer: "app",
            IsCustom: false,
            Tables: new[]
            {
                new ExtractedTable("OrderTable", null, table.File, Array.Empty<ExtractedTableField>()) { Methods = table.Methods },
            },
            Classes: new[]
            {
                new ExtractedClass("PostBase", null, false, false, baseClass.File, baseClass.Methods),
                new ExtractedClass("Poster", "PostBase", false, false, post.File, post.Methods),
                // Its base is a kernel class, which no index holds.
                new ExtractedClass("PosterForm", "FormRunBase", false, false, "missing.xml",
                    new[] { new ExtractedMethod("run", null, null, false) }),
            },
            Edts: new[] { new ExtractedEdt("AccountNum", null, "String", null, 20), new ExtractedEdt("CustAccount", null, "String", null, 20) },
            Enums: Array.Empty<ExtractedEnum>(),
            MenuItems: Array.Empty<ExtractedMenuItem>(),
            CocExtensions: new[]
            {
                new ExtractedCoc("Poster", "added", "Poster_Other_Extension"),
                new ExtractedCoc("Poster", "removed", "Poster_Mine_Extension"),
            },
            Labels: Array.Empty<ExtractedLabel>()));
    }

    private IReadOnlyList<ReferenceViolation> Resolve(string code) => ReferenceResolver.Resolve(code, _repo).Violations;

    private static string Coc(string method, string target = "classStr(Poster)", string name = "Poster_Mine_Extension") => $$"""
        [ExtensionOf({{target}})]
        final class {{name}}
        {
            {{method}}
        }
        """;

    // ── Intrinsic members ───────────────────────────────────────────────────

    [Theory]
    [InlineData("methodStr(Poster, run)")]
    [InlineData("staticMethodStr(Poster, construct)")]
    [InlineData("delegateStr(Poster, posted)")]
    [InlineData("staticDelegateStr(Poster, created)")]
    [InlineData("tableMethodStr(OrderTable, validateWrite)")]
    [InlineData("tableStaticMethodStr(OrderTable, find)")]
    // added by an extension class
    [InlineData("methodStr(Poster, added)")]
    // an Object method needs no declaration
    [InlineData("methodStr(Poster, toString)")]
    public void Intrinsic_member_declared_on_the_named_type_is_quiet(string call)
        => Assert.Empty(Resolve($"str s = {call};"));

    [Theory]
    [InlineData("methodStr(Poster, missing)", "unknown-method")]
    [InlineData("methodStr(Poster, allowSave)", "unknown-method")]            // declared on the base only
    [InlineData("methodStr(PosterForm, missing)", "unknown-method")]          // base not indexed
    [InlineData("tableMethodStr(OrderTable, postLoad)", "unknown-method")]    // kernel, not overridden
    [InlineData("delegateStr(Poster, missing)", "unknown-method")]
    [InlineData("methodStr(Poster, construct)", "wrong-member-kind")]
    [InlineData("staticMethodStr(Poster, run)", "wrong-member-kind")]
    [InlineData("delegateStr(Poster, run)", "wrong-member-kind")]
    [InlineData("delegateStr(Poster, created)", "wrong-member-kind")]
    [InlineData("staticDelegateStr(Poster, posted)", "wrong-member-kind")]
    [InlineData("tableMethodStr(OrderTable, find)", "wrong-member-kind")]
    [InlineData("methodStr(OrderTable, validateWrite)", "wrong-member-kind")] // a table takes tableMethodStr
    [InlineData("tableMethodStr(NoSuchTable, validateWrite)", "unknown-intrinsic-target")]
    public void Intrinsic_member_the_compiler_rejects_is_an_error(string call, string kind)
    {
        var finding = Assert.Single(Resolve($"str s = {call};"));
        Assert.Equal(kind, finding.Kind);
        Assert.Equal("error", finding.Severity);
    }

    [Fact]
    public void Inherited_member_message_names_the_declaring_type()
    {
        var finding = Assert.Single(Resolve("str s = methodStr(Poster, allowSave);"));
        Assert.Contains("methodStr(PostBase, allowSave)", finding.Detail);
    }

    [Fact]
    public void Member_the_snippet_declares_itself_is_not_missing()
        => Assert.Empty(Resolve("""
            class Poster extends PostBase
            {
                public void fresh()
                {
                    str s = methodStr(Poster, fresh);
                }
            }
            """));

    // ── Chain of Command ────────────────────────────────────────────────────

    [Theory]
    [InlineData("public void run() { next run(); }")]
    [InlineData("public boolean allowSave() { return next allowSave(); }")]     // inherited
    [InlineData("public int count(int _renamed, AccountNum _account) { return next count(_renamed, _account); }")]
    [InlineData("public static Poster construct() { return next construct(); }")]
    [InlineData("public final void openFinal() { next openFinal(); }")]
    [InlineData("protected void closedProtected() { next closedProtected(); }")]
    [InlineData("public void added() { next added(); }")]                       // added by another extension
    [InlineData("public str toString() { return next toString(); }")]           // kernel
    public void Wrapper_the_compiler_accepts_is_quiet(string method)
        => Assert.DoesNotContain(Resolve(Coc(method)), v => v.Kind == "coc-target");

    [Fact]
    public void Wrapper_of_a_kernel_table_method_is_quiet()
        => Assert.DoesNotContain(
            Resolve(Coc("public void postLoad() { next postLoad(); }", "tableStr(OrderTable)", "OrderTable_Extension")),
            v => v.Kind == "coc-target");

    [Theory]
    [InlineData("public void missing() { next missing(); }")]
    // only this extension itself, already indexed, still has it
    [InlineData("public void removed() { next removed(); }")]
    [InlineData("private void secret() { next secret(); }")]
    [InlineData("public void sealedRun() { next sealedRun(); }")]
    [InlineData("public void closed() { next closed(); }")]
    [InlineData("public void noHook() { next noHook(); }")]
    [InlineData("public int count(int _bookCount) { return next count(_bookCount); }")]
    [InlineData("public int count(str _bookCount, AccountNum _account) { return next count(_bookCount, _account); }")]
    [InlineData("public str count(int _bookCount, AccountNum _account) { return next count(_bookCount, _account); }")]
    [InlineData("public Poster construct() { return next construct(); }")]
    public void Wrapper_the_compiler_rejects_is_an_error(string method)
    {
        var finding = Assert.Single(Resolve(Coc(method)), v => v.Kind == "coc-target");
        Assert.Equal("error", finding.Severity);
    }

    [Fact]
    public void Wrapper_with_another_edt_is_a_warning()
    {
        var finding = Assert.Single(
            Resolve(Coc("public int count(int _bookCount, CustAccount _account) { return next count(_bookCount, _account); }")),
            v => v.Kind == "coc-target");
        Assert.Equal("warning", finding.Severity);
    }

    // ── Event handlers ──────────────────────────────────────────────────────

    private static string Handler(string attribute, string parameters) => $$"""
        class PosterHandler
        {
            {{attribute}}
            public static void handle({{parameters}})
            {
            }
        }
        """;

    private const string OnPosted = "[SubscribesTo(classStr(Poster), delegateStr(Poster, posted))]";

    [Fact]
    public void Handler_repeating_the_delegate_is_quiet()
        => Assert.DoesNotContain(Resolve(Handler(OnPosted, "int _bookCount, EventHandlerResult _result")),
            v => v.Severity == "error");

    [Theory]
    [InlineData("int _bookCount")]
    [InlineData("str _bookCount, EventHandlerResult _result")]
    public void Handler_with_other_parameters_is_an_error(string parameters)
    {
        var finding = Assert.Single(Resolve(Handler(OnPosted, parameters)), v => v.Kind == "handler-target");
        Assert.Equal("error", finding.Severity);
    }

    [Theory]
    [InlineData("noHook", true)]
    [InlineData("secret", true)]
    [InlineData("hooked", false)]
    [InlineData("run", false)]
    public void Pre_and_post_handlers_need_a_hookable_target(string method, bool refused)
    {
        var findings = Resolve(Handler($"[PostHandlerFor(classStr(Poster), methodStr(Poster, {method}))]", "XppPrePostArgs _args"));
        Assert.Equal(refused, findings.Any(v => v.Kind == "handler-target" && v.Severity == "error"));
    }

    // ── Obsolete calls ──────────────────────────────────────────────────────

    [Fact]
    public void Call_to_an_error_obsolete_method_is_an_error()
    {
        var finding = Assert.Single(Resolve("Poster poster = new Poster();\nposter.gone();"), v => v.Kind == "obsolete-method");
        Assert.Equal("error", finding.Severity);
    }

    [Fact]
    public void Call_to_a_warning_obsolete_method_is_a_warning()
    {
        var finding = Assert.Single(Resolve("Poster::old();"), v => v.Kind == "obsolete-method");
        Assert.Equal("warning", finding.Severity);
    }

    [Fact]
    public void Obsolete_code_calling_obsolete_code_is_not_an_error()
    {
        var findings = Resolve("""
            [SysObsolete('going too', false, 31\12\2020)]
            public void legacy(Poster _poster)
            {
                _poster.gone();
            }
            """);
        Assert.DoesNotContain(findings, v => v.Severity == "error");
    }
}
