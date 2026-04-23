using D365FO.Core;
using Xunit;

namespace D365FO.Core.Tests;

public class StringSanitizerTests
{
    [Fact]
    public void Preserves_printable_and_newlines()
    {
        Assert.Equal("hello\nworld\t!", StringSanitizer.Sanitize("hello\nworld\t!"));
    }

    [Fact]
    public void Strips_control_characters()
    {
        var input = "safe\u0001text\u0007here";
        Assert.Equal("safetexthere", StringSanitizer.Sanitize(input));
    }

    [Fact]
    public void Null_passthrough()
    {
        Assert.Null(StringSanitizer.Sanitize(null));
    }
}

public class ToolResultTests
{
    [Fact]
    public void Success_has_ok_true_and_no_error()
    {
        var r = ToolResult<int>.Success(42);
        Assert.True(r.Ok);
        Assert.Equal(42, r.Data);
        Assert.Null(r.Error);
    }

    [Fact]
    public void Fail_sets_code_and_message()
    {
        var r = ToolResult<int>.Fail("X", "m", "h");
        Assert.False(r.Ok);
        Assert.Equal("X", r.Error!.Code);
        Assert.Equal("m", r.Error.Message);
        Assert.Equal("h", r.Error.Hint);
    }
}
