using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using D365FO.Core;
using D365FO.Core.Bridge;
using D365FO.Core.Extract;
using D365FO.Core.Index;
using Xunit;

namespace D365FO.Core.Tests;

/// <summary>
/// Issue #112: structured method-level modify via D365FO.Bridge. Exercises
/// <see cref="MethodModifyEngine.ModifyCore"/> against a fake bridge (no real
/// process spawned) so the read → structured-replace → validate → write
/// sequence is verified without a D365FO VM.
/// </summary>
public sealed class MethodModifyEngineTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"methodmodify-{Guid.NewGuid():N}.sqlite");
    private readonly MetadataRepository _repo;

    public MethodModifyEngineTests()
    {
        _repo = new MetadataRepository(_dbPath);
        _repo.EnsureSchema();
        var batch = new ExtractBatch(
            Model: "ApplicationSuite",
            Publisher: "Microsoft",
            Layer: "app",
            IsCustom: false,
            Tables: Array.Empty<ExtractedTable>(),
            Classes: new[]
            {
                new ExtractedClass("CustBalance", null, false, false, "x", new[]
                {
                    new ExtractedMethod("calc", "real calc()", "real", false),
                }),
            },
            Edts: Array.Empty<ExtractedEdt>(),
            Enums: Array.Empty<ExtractedEnum>(),
            MenuItems: Array.Empty<ExtractedMenuItem>(),
            CocExtensions: Array.Empty<ExtractedCoc>(),
            Labels: Array.Empty<ExtractedLabel>());
        _repo.ApplyExtract(batch);
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

    private const string ClassXmlWithMethod =
        "<AxClass><Name>CustBalance</Name><SourceCode><Methods>" +
        "<Method><Name>calc</Name><Source><![CDATA[\n    real calc()\n    {\n        return 1;\n    }\n]]></Source></Method>" +
        "</Methods></SourceCode></AxClass>";

    [Fact]
    public async Task Modify_replaces_method_source_and_writes_back_through_bridge()
    {
        JsonObject? capturedUpdateArgs = null;
        using var harness = FakeBridge.Create(request =>
        {
            var method = (string?)request["method"];
            if (method == "readObjectXml")
            {
                return Response(request, new JsonObject { ["ok"] = true, ["xml"] = ClassXmlWithMethod });
            }
            if (method == "updateObject")
            {
                capturedUpdateArgs = (JsonObject?)request["params"];
                return Response(request, new JsonObject { ["ok"] = true });
            }
            throw new InvalidOperationException("unexpected method: " + method);
        });

        var req = new MethodModifyEngine.ModifyRequest("class", "CustBalance", "calc", "return 2;", Model: "ApplicationSuite");
        var result = MethodModifyEngine.ModifyCore(req, _repo, harness.Client);

        Assert.True(result.Ok);
        Assert.NotNull(capturedUpdateArgs);
        Assert.Equal("ApplicationSuite", (string?)capturedUpdateArgs!["model"]);
        // The whole method, not just the statements: the signature and braces survive (#231).
        Assert.Equal("\n    real calc()\n    {\n        return 2;\n    }\n",
            WrittenSource(capturedUpdateArgs["xml"]!.GetValue<string>(), "calc"));
    }

    [Fact]
    public void Modify_unknown_method_fails_METHOD_NOT_FOUND_with_known_methods_hint()
    {
        using var harness = FakeBridge.Create(request =>
            Response(request, new JsonObject { ["ok"] = true, ["xml"] = ClassXmlWithMethod }));

        var req = new MethodModifyEngine.ModifyRequest("class", "CustBalance", "doesNotExist", "return 2;", Model: "ApplicationSuite");
        var result = MethodModifyEngine.ModifyCore(req, _repo, harness.Client);

        Assert.False(result.Ok);
        Assert.Equal(D365FoErrorCodes.MethodNotFound, result.Error!.Code);
        Assert.Contains("calc", result.Error.Hint);
    }

    [Fact]
    public void Modify_unsupported_kind_fails_BAD_INPUT_without_calling_bridge()
    {
        using var harness = FakeBridge.Create(_ => throw new InvalidOperationException("bridge should not be called"));

        var req = new MethodModifyEngine.ModifyRequest("enum", "SomeEnum", "x", "y");
        var result = MethodModifyEngine.ModifyCore(req, _repo, harness.Client);

        Assert.False(result.Ok);
        Assert.Equal(D365FoErrorCodes.BadInput, result.Error!.Code);
    }

    [Fact]
    public void Modify_unresolvable_model_fails_before_touching_bridge()
    {
        using var harness = FakeBridge.Create(_ => throw new InvalidOperationException("bridge should not be called"));

        var req = new MethodModifyEngine.ModifyRequest("class", "NoSuchClassInIndex", "calc", "return 2;");
        var result = MethodModifyEngine.ModifyCore(req, _repo, harness.Client);

        Assert.False(result.Ok);
        Assert.Equal(D365FoErrorCodes.ClassNotFound, result.Error!.Code);
    }

    [Fact]
    public void Modify_bridge_read_failure_surfaces_as_failure()
    {
        using var harness = FakeBridge.Create(request =>
            Response(request, new JsonObject { ["ok"] = false, ["error"] = "NOT_FOUND", ["message"] = "no such object" }));

        var req = new MethodModifyEngine.ModifyRequest("class", "CustBalance", "calc", "return 2;", Model: "ApplicationSuite");
        var result = MethodModifyEngine.ModifyCore(req, _repo, harness.Client);

        Assert.False(result.Ok);
        Assert.Equal(D365FoErrorCodes.ClassNotFound, result.Error!.Code);
    }

    [Fact]
    public void LocateMethodsContainer_finds_table_shape_direct_child()
    {
        var doc = System.Xml.Linq.XDocument.Parse("<AxTable><Methods><Method><Name>m</Name></Method></Methods></AxTable>");
        var container = MethodModifyEngine.LocateMethodsContainer(doc.Root);
        Assert.NotNull(container);
        Assert.Equal("Methods", container!.Name.LocalName);
    }

    [Fact]
    public void LocateMethodsContainer_ignores_nested_datasource_methods()
    {
        var doc = System.Xml.Linq.XDocument.Parse(
            "<AxForm><SourceCode><DataSources><DataSource><Methods><Method><Name>m</Name></Method></Methods></DataSource></DataSources></SourceCode></AxForm>");
        var container = MethodModifyEngine.LocateMethodsContainer(doc.Root);
        Assert.Null(container);
    }

    // ---- issue #231: --body is the statements between the braces, not the whole <Source> ----

    private const string DocumentedClassXml =
        "<AxClass><Name>CustBalance</Name><SourceCode><Methods>" +
        "<Method><Name>calc</Name><Source><![CDATA[\n" +
        "    /// <summary>\n" +
        "    /// Calculates the balance. Not { a brace that counts.\n" +
        "    /// </summary>\n" +
        "    [Hookable(false)]\n" +
        "    public real calc()\n" +
        "    {\n" +
        "        return 1;\n" +
        "    }\n" +
        "]]></Source></Method>" +
        "</Methods></SourceCode></AxClass>";

    private (ToolResult<object> Result, string? WrittenXml, bool WriteCalled) RunModify(
        string classXml, MethodModifyEngine.ModifyRequest request)
    {
        string? written = null;
        var writeCalled = false;
        using var harness = FakeBridge.Create(r =>
        {
            var method = (string?)r["method"];
            if (method == "readObjectXml")
                return Response(r, new JsonObject { ["ok"] = true, ["xml"] = classXml });
            if (method == "updateObject")
            {
                writeCalled = true;
                written = (string?)((JsonObject?)r["params"])!["xml"];
                return Response(r, new JsonObject { ["ok"] = true });
            }
            throw new InvalidOperationException("unexpected method: " + method);
        });

        var journal = Path.Combine(Path.GetTempPath(), $"methodmodify-journal-{Guid.NewGuid():N}.sqlite");
        var result = MethodModifyEngine.ModifyCore(request, _repo, harness.Client, journal);
        return (result, written, writeCalled);
    }

    private static string WrittenSource(string xml, string method) =>
        System.Xml.Linq.XDocument.Parse(xml).Descendants("Method")
            .Single(m => (string?)m.Element("Name") == method).Element("Source")!.Value;

    [Fact]
    public void Body_replaced_inside_braces_keeps_doc_comment_attributes_and_signature()
    {
        var (result, xml, _) = RunModify(DocumentedClassXml,
            new MethodModifyEngine.ModifyRequest("class", "CustBalance", "calc", "real x = 2;\nreturn x;", Model: "ApplicationSuite"));

        Assert.True(result.Ok, result.Error?.Message);
        Assert.Equal(
            "\n" +
            "    /// <summary>\n" +
            "    /// Calculates the balance. Not { a brace that counts.\n" +
            "    /// </summary>\n" +
            "    [Hookable(false)]\n" +
            "    public real calc()\n" +
            "    {\n" +
            "        real x = 2;\n" +
            "        return x;\n" +
            "    }\n",
            WrittenSource(xml!, "calc"));
    }

    [Fact]
    public void Body_with_braces_in_strings_and_comments_of_the_old_body_replaces_the_whole_old_body()
    {
        const string xmlWithTrickyBody =
            "<AxClass><Name>CustBalance</Name><SourceCode><Methods>" +
            "<Method><Name>calc</Name><Source><![CDATA[\n" +
            "    public real calc()\n" +
            "    {\n" +
            "        str s = \"}\"; // }\n" +
            "        /* { */\n" +
            "        if (s == '{')\n" +
            "        {\n" +
            "            return 0;\n" +
            "        }\n" +
            "        return 1;\n" +
            "    }\n" +
            "]]></Source></Method>" +
            "</Methods></SourceCode></AxClass>";

        var (result, xml, _) = RunModify(xmlWithTrickyBody,
            new MethodModifyEngine.ModifyRequest("class", "CustBalance", "calc", "return 2;", Model: "ApplicationSuite"));

        Assert.True(result.Ok, result.Error?.Message);
        Assert.Equal("\n    public real calc()\n    {\n        return 2;\n    }\n", WrittenSource(xml!, "calc"));
    }

    [Theory]
    [InlineData("public real calc()\n{\n    return 2;\n}")]
    [InlineData("/// <summary>x</summary>\nreturn 2;")]
    [InlineData("[Hookable(false)]\npublic real calc()\n{\n    return 2;\n}")]
    [InlineData("real calculate()\n{\n    return 2;\n}")]
    public void Full_declaration_passed_as_body_is_refused_before_the_bridge_is_touched(string body)
    {
        using var harness = FakeBridge.Create(_ => throw new InvalidOperationException("bridge should not be called"));

        var result = MethodModifyEngine.ModifyCore(
            new MethodModifyEngine.ModifyRequest("class", "CustBalance", "calc", body, Model: "ApplicationSuite"),
            _repo, harness.Client);

        Assert.False(result.Ok);
        Assert.Equal(D365FoErrorCodes.BadInput, result.Error!.Code);
        Assert.Contains("--source", result.Error.Hint);
    }

    [Theory]
    [InlineData("[a, b] = conFunc();\nreturn a;")]
    [InlineData("if (x)\n{\n    return 2;\n}\nreturn 1;")]
    [InlineData("while select custTable where custTable.AccountNum == x\n{\n    i++;\n}\nreturn i;")]
    [InlineData("void helper()\n{\n}\nhelper();\nreturn 1;")]
    public void Statements_that_open_a_block_are_not_mistaken_for_a_declaration(string body)
    {
        Assert.Null(MethodSourceSplicer.RefuseDeclarationAsBody(body, "calc"));
    }

    [Fact]
    public void Body_is_spliced_into_a_method_whose_signature_uses_a_macro()
    {
        var classXml = "<AxClass><Name>CustBalance</Name><SourceCode><Methods>" +
                       "<Method><Name>calc</Name><Source><![CDATA[\n    public real calc(int _max = #MaxRows)\n    {\n        return 1;\n    }\n]]></Source></Method>" +
                       "</Methods></SourceCode></AxClass>";

        var (result, written, writeCalled) = RunModify(classXml,
            new MethodModifyEngine.ModifyRequest("class", "CustBalance", "calc", "return 2;", Model: "ApplicationSuite"));

        Assert.True(result.Ok, result.Error?.Message);
        Assert.True(writeCalled);
        Assert.Contains("public real calc(int _max = #MaxRows)\n    {\n        return 2;\n    }", written!.Replace("\r\n", "\n"));
    }

    [Theory]
    [InlineData("    abstract real calc()\n    {\n    }\n", "abstract")]
    [InlineData("    delegate void calc(real _x)\n    {\n    }\n", "delegate")]
    [InlineData("    public real calc()\n", "no braces")]
    [InlineData("    #ParmMethod(calc)\n", "macro")]
    public void Method_without_a_body_is_refused_and_nothing_is_written(string source, string why)
    {
        var classXml = "<AxClass><Name>CustBalance</Name><SourceCode><Methods>" +
                       "<Method><Name>calc</Name><Source><![CDATA[\n" + source + "]]></Source></Method>" +
                       "</Methods></SourceCode></AxClass>";

        var (result, _, writeCalled) = RunModify(classXml,
            new MethodModifyEngine.ModifyRequest("class", "CustBalance", "calc", "return 2;", Model: "ApplicationSuite"));

        Assert.False(result.Ok, why);
        Assert.False(writeCalled, why);
        Assert.Contains(result.Error!.Code, new[] { D365FoErrorCodes.BadInput, MethodSourceSplicer.BodyNotFound });
    }

    [Theory]
    [InlineData("<IsInterface>Yes</IsInterface><SourceCode>")]
    [InlineData("<SourceCode><Declaration><![CDATA[\n/// A { brace\npublic interface CustBalance\n{\n}\n]]></Declaration>")]
    public void Interface_method_is_refused_and_nothing_is_written(string interfaceMarker)
    {
        var interfaceXml =
            "<AxClass><Name>CustBalance</Name>" + interfaceMarker + "<Methods>" +
            "<Method><Name>calc</Name><Source><![CDATA[\n    public real calc()\n    {\n    }\n]]></Source></Method>" +
            "</Methods></SourceCode></AxClass>";

        var (result, _, writeCalled) = RunModify(interfaceXml,
            new MethodModifyEngine.ModifyRequest("class", "CustBalance", "calc", "return 2;", Model: "ApplicationSuite"));

        Assert.False(result.Ok);
        Assert.False(writeCalled);
        Assert.Equal(D365FoErrorCodes.BadInput, result.Error!.Code);
    }

    [Fact]
    public void Source_replaces_the_whole_method_including_its_signature()
    {
        const string source = "    public real calc(real _factor = 1)\n    {\n        return 2 * _factor;\n    }\n";
        var (result, xml, _) = RunModify(DocumentedClassXml,
            new MethodModifyEngine.ModifyRequest("class", "CustBalance", "calc", null, Model: "ApplicationSuite", NewSource: source));

        Assert.True(result.Ok, result.Error?.Message);
        Assert.Equal(source, WrittenSource(xml!, "calc"));
    }

    [Theory]
    [InlineData("    public real other()\n    {\n        return 2;\n    }\n", "declares 'other'")]
    [InlineData("return 2;", "declares no method")]
    [InlineData("    void calc() { }\n    void more() { }\n", "declares 2 methods")]
    public void Source_that_is_not_exactly_this_method_is_refused(string source, string message)
    {
        using var harness = FakeBridge.Create(_ => throw new InvalidOperationException("bridge should not be called"));

        var result = MethodModifyEngine.ModifyCore(
            new MethodModifyEngine.ModifyRequest("class", "CustBalance", "calc", null, Model: "ApplicationSuite", NewSource: source),
            _repo, harness.Client);

        Assert.False(result.Ok);
        Assert.Equal(D365FoErrorCodes.BadInput, result.Error!.Code);
        Assert.Contains(message, result.Error.Message);
    }

    [Fact]
    public void Body_and_source_together_are_refused()
    {
        using var harness = FakeBridge.Create(_ => throw new InvalidOperationException("bridge should not be called"));

        var result = MethodModifyEngine.ModifyCore(
            new MethodModifyEngine.ModifyRequest("class", "CustBalance", "calc", "return 2;", Model: "ApplicationSuite",
                NewSource: "real calc() { return 2; }"),
            _repo, harness.Client);

        Assert.False(result.Ok);
        Assert.Equal(D365FoErrorCodes.BadInput, result.Error!.Code);
    }

    // ---- issue #256: add a new method to an existing object ----

    private const string CustomClassXml =
        "<AxClass xmlns:i=\"http://www.w3.org/2001/XMLSchema-instance\"><Name>ContosoHelper</Name><SourceCode>" +
        "<Declaration><![CDATA[\n[SysObsolete('x', false)]\npublic class ContosoHelper\n{\n    int counter;\n}\n]]></Declaration>" +
        "<Methods><Method><Name>calc</Name><Source><![CDATA[\n    public real calc()\n    {\n        return 1;\n    }\n]]></Source></Method></Methods>" +
        "</SourceCode></AxClass>";

    private void SeedCustomModel() =>
        _repo.ApplyExtract(ExtractBatch.Empty("ContosoCustom") with
        {
            IsCustom = true,
            Classes = new[] { new ExtractedClass("ContosoHelper", null, false, false, "x", Array.Empty<ExtractedMethod>()) },
            Tables = new[] { new ExtractedTable("ContosoTable", null, "x", Array.Empty<ExtractedTableField>()) },
        });

    private (ToolResult<object> Result, string? WrittenXml, string Journal) RunAdd(
        string objectXml, MethodModifyEngine.AddRequest request)
    {
        SeedCustomModel();
        string? written = null;
        using var harness = FakeBridge.Create(r =>
        {
            var method = (string?)r["method"];
            if (method == "readObjectXml")
                return Response(r, new JsonObject { ["ok"] = true, ["xml"] = objectXml });
            if (method == "updateObject")
            {
                written = (string?)((JsonObject?)r["params"])!["xml"];
                return Response(r, new JsonObject { ["ok"] = true });
            }
            throw new InvalidOperationException("unexpected method: " + method);
        });

        // The journal lives in a "journal" folder next to the index, so each run gets its own folder.
        var journal = Path.Combine(Path.GetTempPath(), $"methodadd-{Guid.NewGuid():N}", "index.sqlite");
        var result = MethodModifyEngine.AddCore(request, _repo, harness.Client, journal);
        return (result, written, journal);
    }

    private static bool ResultIsStatic(ToolResult<object> result) =>
        System.Text.Json.JsonSerializer.SerializeToElement(result.Data).GetProperty("isStatic").GetBoolean();

    [Fact]
    public void Add_instance_method_appends_it_and_keeps_the_declaration_and_the_other_methods()
    {
        const string source = "    /// <summary>Twice the balance.</summary>\n    public real twice()\n    {\n        return this.calc() * 2;\n    }\n";
        var (result, xml, journal) = RunAdd(CustomClassXml,
            new MethodModifyEngine.AddRequest("class", "ContosoHelper", "twice", source));

        Assert.True(result.Ok, result.Error?.Message);
        Assert.False(ResultIsStatic(result));

        var doc = System.Xml.Linq.XDocument.Parse(xml!);
        var original = System.Xml.Linq.XDocument.Parse(CustomClassXml);
        Assert.Equal(original.Descendants("Declaration").Single().Value, doc.Descendants("Declaration").Single().Value);
        Assert.Equal(new[] { "calc", "twice" }, doc.Descendants("Method").Select(m => (string?)m.Element("Name")).ToArray());
        Assert.Equal("\n    public real calc()\n    {\n        return 1;\n    }\n", WrittenSource(xml!, "calc"));
        Assert.Equal(source, WrittenSource(xml!, "twice"));

        // Journaled with the exact pre-image, so `d365fo undo` takes the method out again.
        var stored = Assert.Single(D365FO.Core.Journal.ModificationJournal.ForIndex(journal).List(10));
        Assert.Equal(CustomClassXml, stored.PreImage);
        Assert.Equal("ContosoCustom", stored.Model);
    }

    [Theory]
    [InlineData("public static ContosoHelper construct()\n{\n    return new ContosoHelper();\n}\n")]
    [InlineData("/// <summary>static in a comment is not the modifier</summary>\nstatic ContosoHelper construct()\n{\n    return new ContosoHelper();\n}\n")]
    [InlineData("[Hookable(false)]\nSTATIC ContosoHelper construct()\n{\n    return new ContosoHelper();\n}\n")]
    public void Add_static_method_is_written_as_given_and_reported_static(string source)
    {
        var (result, xml, _) = RunAdd(CustomClassXml,
            new MethodModifyEngine.AddRequest("class", "ContosoHelper", "construct", source));

        Assert.True(result.Ok, result.Error?.Message);
        Assert.True(ResultIsStatic(result));
        Assert.Equal(source, WrittenSource(xml!, "construct"));
    }

    [Fact]
    public void Add_to_a_table_without_methods_creates_the_container()
    {
        const string tableXml = "<AxTable xmlns:i=\"http://www.w3.org/2001/XMLSchema-instance\"><Name>ContosoTable</Name>" +
                                "<SourceCode><Declaration><![CDATA[\npublic class ContosoTable extends common\n{\n}\n]]></Declaration></SourceCode>" +
                                "<Fields /></AxTable>";
        const string source = "public static ContosoTable find(RecId _recId)\n{\n    ContosoTable t;\n    select firstonly t where t.RecId == _recId;\n    return t;\n}\n";

        var (result, xml, _) = RunAdd(tableXml, new MethodModifyEngine.AddRequest("table", "ContosoTable", "find", source));

        Assert.True(result.Ok, result.Error?.Message);
        var methods = System.Xml.Linq.XDocument.Parse(xml!).Root!.Elements("Methods").Single();
        Assert.Equal("find", (string?)methods.Element("Method")!.Element("Name"));
    }

    [Fact]
    public void Add_of_a_name_the_object_already_has_is_refused_and_nothing_is_written()
    {
        var (result, xml, _) = RunAdd(CustomClassXml,
            new MethodModifyEngine.AddRequest("class", "ContosoHelper", "CALC", "public real calc()\n{\n    return 2;\n}\n"));

        Assert.False(result.Ok);
        Assert.Equal(D365FoErrorCodes.AlreadyExists, result.Error!.Code);
        Assert.Contains("modify method", result.Error.Hint);
        Assert.Null(xml);
    }

    [Theory]
    [InlineData("public real other()\n{\n    return 2;\n}\n", "declares 'other'")]
    [InlineData("return 2;", "declares no method")]
    [InlineData("void twice() { }\nvoid more() { }\n", "declares 2 methods")]
    [InlineData("public real twice()\n{\n    return 2;\n", "cannot be checked")]
    [InlineData("", "source is required")]
    public void Add_with_a_source_that_is_not_exactly_this_method_is_refused_before_the_bridge(string source, string message)
    {
        SeedCustomModel();
        using var harness = FakeBridge.Create(_ => throw new InvalidOperationException("bridge should not be called"));

        var result = MethodModifyEngine.AddCore(
            new MethodModifyEngine.AddRequest("class", "ContosoHelper", "twice", source), _repo, harness.Client);

        Assert.False(result.Ok);
        Assert.Equal(D365FoErrorCodes.BadInput, result.Error!.Code);
        Assert.Contains(message, result.Error.Message);
    }

    [Fact]
    public void Add_to_an_object_of_a_model_that_is_not_custom_is_refused_before_the_bridge()
    {
        using var harness = FakeBridge.Create(_ => throw new InvalidOperationException("bridge should not be called"));

        var result = MethodModifyEngine.AddCore(
            new MethodModifyEngine.AddRequest("class", "CustBalance", "twice", "public real twice()\n{\n    return 2;\n}\n"),
            _repo, harness.Client);

        Assert.False(result.Ok);
        Assert.Equal(D365FoErrorCodes.BadInput, result.Error!.Code);
        Assert.Contains("ApplicationSuite", result.Error.Message);
        Assert.Contains("generate coc CustBalance", result.Error.Hint);
    }

    [Fact]
    public void Add_to_a_class_the_index_does_not_know_fails_CLASS_NOT_FOUND()
    {
        using var harness = FakeBridge.Create(_ => throw new InvalidOperationException("bridge should not be called"));

        var result = MethodModifyEngine.AddCore(
            new MethodModifyEngine.AddRequest("class", "NoSuchClass", "twice", "public real twice()\n{\n    return 2;\n}\n"),
            _repo, harness.Client);

        Assert.Equal(D365FoErrorCodes.ClassNotFound, result.Error?.Code);
    }

    [Theory]
    [InlineData("edt")]
    [InlineData("enum")]
    public void Add_to_a_kind_without_members_to_call_is_refused(string kind)
    {
        Assert.Equal(D365FoErrorCodes.BadInput,
            MethodModifyEngine.CheckAddInput(new MethodModifyEngine.AddRequest(kind, "X", "m", "void m() { }"))?.Error?.Code);
    }

    [Fact]
    public void Add_with_an_unresolved_reference_is_blocked_and_nothing_is_written()
    {
        var (result, xml, _) = RunAdd(CustomClassXml,
            new MethodModifyEngine.AddRequest("class", "ContosoHelper", "q",
                "public str q()\n{\n    return queryStr(NoSuchQueryAnywhere256);\n}\n"));

        Assert.False(result.Ok);
        Assert.Equal(D365FoErrorCodes.ValidationFailed, result.Error!.Code);
        Assert.Null(xml);
    }

    [Theory]
    [InlineData("public static void main(Args _args) { }", true)]
    [InlineData("static void main(Args _args) { }", true)]
    [InlineData("/// static\n[SysObsolete('static')]\npublic void run() { }", false)]
    [InlineData("public void staticHelper() { }", false)]
    [InlineData("private static server int count() { }", true)]
    public void DeclaresStatic_reads_only_the_header(string source, bool expected)
    {
        Assert.Equal(expected, D365FO.Core.Validation.AotMethodSourceRules.DeclaresStatic(source));
    }

    private static JsonObject Response(JsonObject request, JsonObject result) => new()
    {
        ["jsonrpc"] = "2.0",
        ["id"] = request["id"]?.DeepClone(),
        ["result"] = result,
    };

    /// <summary>
    /// Minimal fake bridge: routes each request line to <paramref name="respondWith"/>
    /// and feeds the returned JSON-RPC envelope back as the next response line.
    /// Mirrors <c>BridgeClientTests.FakeBridge</c> (kept separate/internal to each
    /// test file rather than shared, per this repo's existing test-fixture style).
    /// </summary>
    private sealed class FakeBridge : IDisposable
    {
        public BridgeClient Client { get; }

        private FakeBridge(BridgeClient client) => Client = client;

        public static FakeBridge Create(Func<JsonObject, JsonObject> respondWith)
        {
            var reader = new DeferredResponseReader(respondWith);
            var client = new BridgeClient(writer: new TeeWriter(reader), reader: reader);
            return new FakeBridge(client);
        }

        public void Dispose() => Client.Dispose();
    }

    private sealed class TeeWriter : TextWriter
    {
        private readonly DeferredResponseReader signal;
        private readonly StringBuilder buffer = new();

        public TeeWriter(DeferredResponseReader signal) => this.signal = signal;

        public override Encoding Encoding => Encoding.UTF8;

        public override void Write(char value)
        {
            if (value == '\n')
            {
                var line = buffer.ToString();
                buffer.Clear();
                signal.OnRequest(line);
            }
            else
            {
                buffer.Append(value);
            }
        }

        public override void Write(string? value)
        {
            if (value is null) return;
            foreach (var ch in value) Write(ch);
        }

        public override void WriteLine(string? value)
        {
            Write(value);
            Write('\n');
        }
    }

    private sealed class DeferredResponseReader : TextReader
    {
        private readonly Func<JsonObject, JsonObject> respondWith;
        private readonly Queue<string> queued = new();

        public DeferredResponseReader(Func<JsonObject, JsonObject> respondWith) => this.respondWith = respondWith;

        public void OnRequest(string requestLine)
        {
            var parsed = JsonNode.Parse(requestLine) as JsonObject
                ?? throw new InvalidOperationException("bad request JSON");
            var response = respondWith(parsed);
            queued.Enqueue(response.ToJsonString());
        }

        public override string? ReadLine() => queued.Count == 0 ? null : queued.Dequeue();
    }
}
