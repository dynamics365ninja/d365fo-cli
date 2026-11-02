using D365FO.Core.Bridge;
using Xunit;

namespace D365FO.Core.Tests;

/// <summary>
/// Issue #231: <c>modify method --body</c> puts statements between the method's outer braces.
/// These pin the text the splice produces, line endings and indentation included, because the
/// AOT keeps both and a write that changes them shows up as noise in every diff.
/// </summary>
public sealed class MethodSourceSplicerTests
{
    [Fact]
    public void Crlf_and_tab_indentation_of_the_method_are_preserved()
    {
        const string source = "\r\n\t/// <summary>\r\n\t/// Doc.\r\n\t/// </summary>\r\n\tpublic real calc()\r\n\t{\r\n\t\treturn 1;\r\n\t}\r\n";

        var result = MethodSourceSplicer.Splice(source, "real x = 2;\nif (x)\n{\n    x++;\n}\nreturn x;", "calc");

        Assert.Null(result.Error);
        Assert.Equal(
            "\r\n\t/// <summary>\r\n\t/// Doc.\r\n\t/// </summary>\r\n\tpublic real calc()\r\n\t{\r\n" +
            "\t\treal x = 2;\r\n\t\tif (x)\r\n\t\t{\r\n\t\t    x++;\r\n\t\t}\r\n\t\treturn x;\r\n" +
            "\t}\r\n",
            result.Source);
    }

    [Fact]
    public void Lf_source_stays_lf_when_the_body_arrives_with_crlf()
    {
        const string source = "\n    void run()\n    {\n    }\n";

        var result = MethodSourceSplicer.Splice(source, "a();\r\nb();\r\n", "run");

        Assert.Equal("\n    void run()\n    {\n        a();\n        b();\n    }\n", result.Source);
    }

    [Fact]
    public void Body_indented_by_the_caller_is_dedented_before_it_is_placed()
    {
        const string source = "\n    void run()\n    {\n        old();\n    }\n";

        var result = MethodSourceSplicer.Splice(source, "\n            a();\n            if (x)\n            {\n                b();\n            }\n", "run");

        Assert.Equal("\n    void run()\n    {\n        a();\n        if (x)\n        {\n            b();\n        }\n    }\n", result.Source);
    }

    [Fact]
    public void Braces_in_a_doc_comment_or_an_attribute_string_do_not_move_the_body()
    {
        const string source = "\n    /// Returns { or }.\n    [SysObsolete('use {other}', false)]\n    str brace()\n    {\n        return '}';\n    }\n";

        var result = MethodSourceSplicer.Splice(source, "return '{';", "brace");

        Assert.Equal("\n    /// Returns { or }.\n    [SysObsolete('use {other}', false)]\n    str brace()\n    {\n        return '{';\n    }\n", result.Source);
    }

    [Fact]
    public void One_line_method_gets_its_body_on_its_own_lines()
    {
        var result = MethodSourceSplicer.Splice("    void run() { old(); }", "a();", "run");

        Assert.Equal("    void run() {\n        a();\n    }", result.Source);
    }

    [Fact]
    public void Lines_inside_a_multi_line_verbatim_string_keep_their_text()
    {
        const string source = "\n    str sql()\n    {\n        return '';\n    }\n";

        var result = MethodSourceSplicer.Splice(source, "return @'SELECT\n  *\nFROM t';", "sql");

        Assert.Equal("\n    str sql()\n    {\n        return @'SELECT\n  *\nFROM t';\n    }\n", result.Source);
    }

    [Fact]
    public void Text_after_the_closing_brace_makes_the_body_ambiguous_and_is_refused()
    {
        var result = MethodSourceSplicer.Splice("    void a() { }\n    void b() { }\n", "x();", "a");

        Assert.Null(result.Source);
        Assert.Equal(MethodSourceSplicer.BodyNotFound, result.Code);
    }
}
