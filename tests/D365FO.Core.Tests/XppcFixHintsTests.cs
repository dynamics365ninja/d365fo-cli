using D365FO.Core.Validation;
using Xunit;

namespace D365FO.Core.Tests;

/// <summary>
/// Regression cover for the scored hint matcher that replaced the ordered
/// <c>if (message.Contains(...))</c> chain. The first two tests are the exact
/// false positives that chain produced.
/// </summary>
public class XppcFixHintsTests
{
    [Fact]
    public void Missing_label_beats_the_generic_identifier_rule()
    {
        // The old chain tested "does not exist" for the identifier rule *before*
        // the label rule, so this message got "verify it with search any" instead
        // of label-creation advice.
        var best = XppcFixHints.Best("The label @SYS12345 does not exist.");
        Assert.NotNull(best);
        Assert.Equal("XPPC-LABEL-MISSING", best!.RuleId);
        Assert.Equal("label-translation", best.Knowledge);
    }

    [Fact]
    public void The_word_label_alone_does_not_trigger_label_advice()
    {
        // The old chain fired on any message containing "label" plus "unknown"/"not
        // exist"; a control-property complaint has nothing to do with label ids.
        Assert.Empty(XppcFixHints.Match("Control 'Label' must be bound to a data source property."));
    }

    [Theory]
    [InlineData("';' expected.", "XPPC-MISSING-SEMICOLON")]
    [InlineData("Unknown type 'CustTableXyz'.", "XPPC-UNKNOWN-IDENTIFIER")]
    [InlineData("'foo' is not a valid method on CustTable.", "XPPC-METHOD-MISSING")]
    [InlineData("The expression does not denote a class.", "XPPC-EXTENSIONOF-INTRINSIC")]
    [InlineData("Wrong number of arguments in call to 'insert'.", "XPPC-ARITY-MISMATCH")]
    [InlineData("Cannot extend final class 'CustTable'.", "XPPC-FINAL-NOT-WRAPPABLE")]
    [InlineData("Object 'MyClass' is not referenced by this model.", "XPPC-MODEL-REFERENCE")]
    [InlineData("Cannot convert from 'str' to 'int'.", "XPPC-TYPE-MISMATCH")]
    // Ported from upstream d365fo-mcp-server's d365foErrorHelp.ts error catalog.
    [InlineData("SYS10028: you must call next salute() in the extension method.", "XPPC-COC-MISSING-NEXT")]
    [InlineData("Overlayering is not allowed for element 'CustTable'.", "XPPC-OVERLAYERING")]
    [InlineData("Element MyMenu cannot be deserialized as AxMenu.", "XPPC-METADATA-DESERIALIZE")]
    [InlineData("BPUpgradeCodeToday: today() must not be used.", "XPPC-BP-TODAY")]
    [InlineData("BPCheckNestedLoopInCode: nested while select detected.", "XPPC-NESTED-LOOP")]
    [InlineData("TTS level is not 0 at the end of the operation.", "XPPC-TTS-UNBALANCED")]
    [InlineData("Exception::UpdateConflict was thrown while updating CustTable.", "XPPC-UPDATE-CONFLICT")]
    [InlineData("The record is not selected for update.", "XPPC-FORUPDATE-MISSING")]
    [InlineData("CLRError: System.NullReferenceException in the interop call.", "XPPC-CLR-ERROR")]
    [InlineData("The number sequence for MyId is not set up for company USMF.", "XPPC-NUMBER-SEQUENCE")]
    [InlineData("The field 'CreditMaxx' does not exist on table CustTable.", "XPPC-FIELD-MISSING")]
    [InlineData("CSUV1: the value cannot be assigned to a variable of this type.", "XPPC-TYPE-MISMATCH")]
    public void Maps_known_messages_to_their_rule(string message, string expectedRule)
    {
        var best = XppcFixHints.Best(message);
        Assert.NotNull(best);
        Assert.Equal(expectedRule, best!.RuleId);
    }

    [Fact]
    public void Specific_semicolon_rule_outranks_the_generic_syntax_catch_all()
    {
        var matches = XppcFixHints.Match("';' expected.");
        Assert.Equal("XPPC-MISSING-SEMICOLON", matches[0].RuleId);
        Assert.DoesNotContain(matches, m => m.RuleId == "XPPC-SYNTAX");
    }

    [Fact]
    public void Unrecognised_message_gets_no_hint_rather_than_the_nearest_one()
    {
        Assert.Empty(XppcFixHints.Match("Out of memory during AOT compilation"));
        Assert.Null(XppcDiagnostics.FixHint("Out of memory during AOT compilation"));
    }

    [Fact]
    public void Empty_input_is_safe()
    {
        Assert.Empty(XppcFixHints.Match(null));
        Assert.Empty(XppcFixHints.Match("   "));
    }

    [Fact]
    public void Diagnostic_surfaces_rule_id_and_knowledge_topic()
    {
        var log = "Compile Error: Class Method dynamics://M/C/m: [(1,2)]: The expression does not denote a class.";
        var d = Assert.Single(XppcDiagnostics.Parse(log));
        Assert.Equal("XPPC-EXTENSIONOF-INTRINSIC", d.HintRule);
        Assert.Equal("coc-extension-authoring", d.Knowledge);
        Assert.Contains("tableStr()", d.Hint);
    }

    [Fact]
    public void Every_rule_declares_at_least_one_positive_condition()
    {
        // A rule with neither AllOf nor AnyOf would match every message.
        Assert.All(XppcFixHints.Rules, r =>
            Assert.True(r.AllOf.Length > 0 || r.AnyOf.Length > 0, $"{r.Id} has no positive condition"));
    }

    [Fact]
    public void Rule_ids_are_unique()
    {
        var ids = XppcFixHints.Rules.Select(r => r.Id).ToList();
        Assert.Equal(ids.Count, ids.Distinct().Count());
    }

    [Fact]
    public void Every_knowledge_pointer_resolves_to_a_real_topic()
    {
        // A hint that points at a renamed or deleted topic sends the agent to a
        // `knowledge get` that fails — worse than no pointer at all.
        foreach (var rule in XppcFixHints.Rules.Where(r => r.Knowledge is not null))
        {
            Assert.True(
                D365FO.Core.Knowledge.KnowledgeBase.Get(rule.Knowledge) is not null,
                $"{rule.Id} points at knowledge topic '{rule.Knowledge}', which does not exist");
        }
    }

    [Fact]
    public void Update_conflict_outranks_the_generic_tts_rule()
    {
        // "UpdateConflict" messages mention the transaction too; the recoverable-retry
        // advice must win over the generic unbalanced-tts advice.
        var best = XppcFixHints.Best("ttscommit failed: Exception::UpdateConflict on CustTable.");
        Assert.NotNull(best);
        Assert.Equal("XPPC-UPDATE-CONFLICT", best!.RuleId);
    }

    [Fact]
    public void Field_rule_outranks_the_generic_identifier_rule()
    {
        var matches = XppcFixHints.Match("The field 'Foo' does not exist on table CustTable.");
        Assert.Equal("XPPC-FIELD-MISSING", matches[0].RuleId);
    }
}
