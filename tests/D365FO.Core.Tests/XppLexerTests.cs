using D365FO.Core.Validation;
using Xunit;

namespace D365FO.Core.Tests;

public class XppLexerTests
{
    [Fact]
    public void Mask_preserves_length_and_newlines()
    {
        var code = "info(\"line one\");\n// comment\nselect t; /* block\nstill block */ x;";
        var masked = XppLexer.Mask(code);
        Assert.Equal(code.Length, masked.Length);
        Assert.Equal(code.Count(c => c == '\n'), masked.Count(c => c == '\n'));
    }

    [Fact]
    public void Mask_blanks_double_quoted_content_but_keeps_delimiters()
    {
        var masked = XppLexer.Mask("info(\"left join\");");
        Assert.DoesNotContain("left join", masked);
        Assert.Contains("\"", masked);
    }

    [Fact]
    public void Mask_blanks_single_quoted_content()
    {
        // The upstream regression: a masker recognising only double quotes let
        // strFind(text, ',', 1, len) read as a wrong arity and a GUID mask as C# `??`.
        var masked = XppLexer.Mask("strFind(text, ',', 1, len); guid g = '????????-????';");
        Assert.DoesNotContain(",',", masked.Replace(" ", ""));
        Assert.DoesNotContain("??", masked);
    }

    [Fact]
    public void Mask_handles_escaped_quote_inside_string()
    {
        var masked = XppLexer.Mask("str s = \"a \\\" b\"; int x = 1;");
        Assert.Contains("int x = 1;", masked);
    }

    [Fact]
    public void Mask_verbatim_string_ignores_backslash()
    {
        // In @"C:\path\" the backslash does not escape the quote — the literal ends there.
        var masked = XppLexer.Mask("str p = @\"C:\\temp\\\"; int y = 2;");
        Assert.Contains("int y = 2;", masked);
    }

    [Fact]
    public void Mask_line_comment_runs_to_end_of_line_only()
    {
        var masked = XppLexer.Mask("// today()\ntoday();");
        Assert.DoesNotContain("today", masked.Split('\n')[0]);
        Assert.Contains("today();", masked.Split('\n')[1]);
    }

    [Fact]
    public void Mask_block_comment_spans_lines()
    {
        var masked = XppLexer.Mask("/* select\nleft join */ x;");
        Assert.DoesNotContain("select", masked);
        Assert.DoesNotContain("left join", masked);
        Assert.Contains("x;", masked);
    }

    [Fact]
    public void Scan_reports_span_kinds()
    {
        var scan = XppLexer.Scan("\"a\" 'b' // c\n/* d */ @'e'");
        Assert.Equal(3, scan.Spans.Count(s => s.Kind == XppSpanKind.String));
        Assert.Equal(1, scan.Spans.Count(s => s.Kind == XppSpanKind.LineComment));
        Assert.Equal(1, scan.Spans.Count(s => s.Kind == XppSpanKind.BlockComment));
        Assert.Contains(scan.Spans, s => s is { Kind: XppSpanKind.String, Verbatim: true });
    }
}

public class CompilerFactsTests
{
    [Fact]
    public void Keywords_come_from_the_shipped_compiler()
    {
        Assert.Equal(115, CompilerFacts.Keywords.Count);
        Assert.True(CompilerFacts.IsReservedKeyword("having"));
        Assert.True(CompilerFacts.IsReservedKeyword("FOREACH")); // case-insensitive
        // `in` is reserved but exempted — never reported.
        Assert.False(CompilerFacts.IsReservedKeyword("in"));
        Assert.False(CompilerFacts.IsReservedKeyword("myVariable"));
    }

    [Fact]
    public void Intrinsics_carry_argument_counts()
    {
        var classStr = CompilerFacts.IntrinsicInfo("classstr");
        Assert.NotNull(classStr);
        Assert.Equal(1, classStr!.Value.Args);
        Assert.Null(CompilerFacts.IntrinsicInfo("notAnIntrinsic"));
    }

    [Fact]
    public void Runtime_functions_expose_min_max_and_variadic()
    {
        var date2Str = CompilerFacts.RuntimeFunctionInfo("date2str");
        Assert.NotNull(date2Str);
        // The compiler's own answer: 7 or 8 arguments (optional trailing DateFlags).
        Assert.Equal(7, date2Str!.Value.Arity.Min);
        Assert.Equal(8, date2Str.Value.Arity.Max);

        var strFmt = CompilerFacts.RuntimeFunctionInfo("strfmt");
        Assert.NotNull(strFmt);
        Assert.Null(strFmt!.Value.Arity.Max); // variadic
        Assert.True(strFmt.Value.Arity.Accepts(12));
    }

    [Fact]
    public void Unknown_and_obsolete_functions_are_known()
    {
        Assert.True(CompilerFacts.IsUnknownFunction("dateMin"));   // AX 2012, gone
        Assert.True(CompilerFacts.IsObsoleteFunction("dateStartWk"));
        Assert.False(CompilerFacts.IsUnknownFunction("abs"));
    }
}
