using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using D365FO.Core;
using D365FO.Core.Index;
using Xunit;

namespace D365FO.Core.Tests;

/// <summary>
/// Issue #272: <c>labels audit</c> over a model folder written to disk, with other models' labels
/// and uses in the index.
/// </summary>
public sealed class LabelAuditTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"labelaudit-{Guid.NewGuid():N}");
    private readonly string _model;
    private readonly string _dbPath;
    private readonly MetadataRepository _repo;

    public LabelAuditTests()
    {
        _model = Path.Combine(_root, "ContosoExt", "ContosoExt");
        _dbPath = Path.Combine(_root, "index.sqlite");

        Write("AxLabelFile/LabelResources/en-US/ContosoExt.en-US.label.txt",
            "﻿Order=Order\n ;Comment under a key is not a key\nInCode=Text used in code\nOnlyEnglish=Only in English\n" +
            "Unused=Nobody uses this\nUsedElsewhere=Used by another model's code\nUsedInOtherLabel=Used by another model's metadata\n");
        Write("AxLabelFile/LabelResources/cs/ContosoExt.cs.label.txt",
            "Order=Objednávka\nInCode=Text v kódu\nUnused=Nikdo\nUsedElsewhere=Jinde\nUsedInOtherLabel=Jinde\nOnlyCzech=Jen česky\n");

        Write("AxTable/ContosoOrderTable.xml", """
            <?xml version="1.0" encoding="utf-8"?>
            <AxTable xmlns:i="http://www.w3.org/2001/XMLSchema-instance">
              <Name>ContosoOrderTable</Name>
              <Label>@ContosoExt:Order</Label>
              <HelpText>@ContosoExt:OrderNotFound</HelpText>
              <Fields>
                <AxTableField i:type="AxTableFieldString">
                  <Name>Note</Name>
                  <Label>@SYS12345</Label>
                  <HelpText>@SYS99999999</HelpText>
                </AxTableField>
              </Fields>
            </AxTable>
            """);
        Write("AxClass/ContosoOrderService.xml", """
            <?xml version="1.0" encoding="utf-8"?>
            <AxClass xmlns:i="http://www.w3.org/2001/XMLSchema-instance">
              <Name>ContosoOrderService</Name>
              <SourceCode>
                <Declaration><![CDATA[
            public class ContosoOrderService
            {
            }
            ]]></Declaration>
                <Methods>
                  <Method>
                    <Name>run</Name>
                    <Source><![CDATA[
                public void run()
                {
                    // "@ContosoExt:InComment" in a comment is not a use
                    info("@ContosoExt:InCode");
                    warning('@ContosoExt:OnlyEnglish');
                    error("@ContosoExt:MissingInCode");
                    str mail = "someone@contoso.com";
                }
            ]]></Source>
                  </Method>
                </Methods>
              </SourceCode>
            </AxClass>
            """);

        _repo = new MetadataRepository(_dbPath);
        _repo.EnsureSchema();
        _repo.ApplyExtract(ExtractBatch.Empty("ApplicationPlatform") with
        {
            Labels = new[] { new ExtractedLabel("SYS", "en-us", "@SYS12345", "Note") },
        });
        // Another model that uses two of ContosoExt's labels: one in a Label column, one in code.
        _repo.ApplyExtract(ExtractBatch.Empty("ContosoOther") with
        {
            IsCustom = true,
            Tables = new[] { new ExtractedTable("ContosoOtherTable", "@ContosoExt:UsedInOtherLabel", "x", Array.Empty<ExtractedTableField>()) },
            Classes = new[]
            {
                new ExtractedClass("ContosoOtherClass", null, false, false, "x", new[]
                {
                    new ExtractedMethod("run", "public void run()", "void", false)
                    {
                        Body = "public void run() { info(\"@ContosoExt:UsedElsewhere\"); }",
                    },
                }),
            },
        }, sourceFingerprint: null, indexMethodSource: true);
    }

    public void Dispose()
    {
        SqlitePool.ReleaseFor(_dbPath);
        try { Directory.Delete(_root, true); } catch { }
    }

    private void Write(string relative, string content)
    {
        var path = Path.Combine(_model, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    private JsonElement Audit(bool allModels = false, string? checks = null, int limit = 50)
    {
        var result = LabelAudit.Run(_repo, null, _model, checks, allModels, limit);
        Assert.True(result.Ok, result.Error?.Message);
        return JsonSerializer.SerializeToElement(result.Data);
    }

    private static List<string> Labels(JsonElement data, string list) =>
        data.GetProperty(list).EnumerateArray()
            .Select(e => e.ValueKind == JsonValueKind.String ? e.GetString()! : e.GetProperty("label").GetString()!)
            .ToList();

    [Fact]
    public void Undefined_lists_tokens_no_label_file_defines_in_properties_and_in_code_strings()
    {
        var data = Audit();

        Assert.Equal(new[] { "@ContosoExt:MissingInCode", "@ContosoExt:OrderNotFound", "@SYS99999999" },
            Labels(data, "undefined"));

        var inCode = data.GetProperty("undefined").EnumerateArray().Single(e => e.GetProperty("label").GetString() == "@ContosoExt:MissingInCode");
        var use = inCode.GetProperty("usedIn")[0];
        Assert.Equal("Class", use.GetProperty("Kind").GetString());
        Assert.Equal("ContosoOrderService", use.GetProperty("Object").GetString());
        Assert.Equal("run", use.GetProperty("Method").GetString());

        var inField = data.GetProperty("undefined").EnumerateArray().Single(e => e.GetProperty("label").GetString() == "@SYS99999999");
        Assert.Equal("Fields/Note", inField.GetProperty("usedIn")[0].GetProperty("Element").GetString());
        Assert.Equal("HelpText", inField.GetProperty("usedIn")[0].GetProperty("Property").GetString());
    }

    [Fact]
    public void Untranslated_lists_keys_missing_in_some_of_their_file_languages()
    {
        var data = Audit(checks: "untranslated");

        var findings = data.GetProperty("untranslated").EnumerateArray()
            .ToDictionary(e => e.GetProperty("label").GetString()!, e => e.GetProperty("missingIn")[0].GetString());
        Assert.Equal(2, findings.Count);
        Assert.Equal("cs", findings["@ContosoExt:OnlyEnglish"]);
        Assert.Equal("en-US", findings["@ContosoExt:OnlyCzech"]);
        Assert.Equal(JsonValueKind.Null, data.GetProperty("undefined").ValueKind);
    }

    [Fact]
    public void Unused_counts_only_the_model_by_default_and_the_index_with_all_models()
    {
        Assert.Equal(
            new[] { "@ContosoExt:OnlyCzech", "@ContosoExt:Unused", "@ContosoExt:UsedElsewhere", "@ContosoExt:UsedInOtherLabel" },
            Labels(Audit(checks: "unused"), "unused"));

        var all = Audit(allModels: true, checks: "unused");
        Assert.Equal(new[] { "@ContosoExt:OnlyCzech", "@ContosoExt:Unused" }, Labels(all, "unused"));
        Assert.Equal("all indexed", all.GetProperty("unusedScope").GetProperty("models").GetString());
    }

    [Fact]
    public void Counts_are_complete_when_the_lists_are_capped()
    {
        var data = Audit(limit: 1);

        Assert.Equal(3, data.GetProperty("counts").GetProperty("undefined").GetInt32());
        Assert.Single(data.GetProperty("undefined").EnumerateArray());
        Assert.True(data.GetProperty("truncated").GetBoolean());
        Assert.Equal(2, data.GetProperty("scanned").GetProperty("xmlFiles").GetInt32());
    }

    [Fact]
    public void A_comment_mail_address_and_comment_line_in_a_label_file_are_not_tokens()
    {
        var data = Audit();
        var used = data.GetProperty("scanned").GetProperty("labelTokensUsed").GetInt32();

        // Order, OrderNotFound, SYS12345, SYS99999999, InCode, OnlyEnglish, MissingInCode.
        Assert.Equal(7, used);
        Assert.DoesNotContain("@ContosoExt:InComment", Labels(data, "undefined"));
    }

    [Theory]
    [InlineData("nope", "Unknown check")]
    [InlineData("all", "Unknown check")]
    public void An_unknown_check_is_refused(string checks, string message)
    {
        var result = LabelAudit.Run(_repo, null, _model, checks, false, 50);
        Assert.Equal(D365FoErrorCodes.BadInput, result.Error?.Code);
        Assert.Contains(message, result.Error!.Message);
    }

    [Fact]
    public void Without_a_model_or_path_the_call_is_refused()
    {
        Assert.Equal(D365FoErrorCodes.BadInput, LabelAudit.Run(_repo, null, null, null, false, 50).Error?.Code);
    }

    [Theory]
    [InlineData("@ContosoExt:Order", "ContosoExt", "Order")]
    [InlineData("@SYS12345", "SYS", "12345")]
    public void Parse_splits_file_and_key(string token, string file, string firstKey)
    {
        var (f, keys) = LabelAudit.Parse(token);
        Assert.Equal(file, f);
        Assert.Equal(firstKey, keys[0]);
    }
}
