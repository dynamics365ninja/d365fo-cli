using D365FO.Core.Extract;
using D365FO.Core.Index;
using Xunit;

namespace D365FO.Core.Tests;

public class NewHelperTests
{
    [Fact]
    public void Slice_ReturnsInclusiveRange_AndClamps()
    {
        var body = "a\nb\nc\nd\ne";
        Assert.Equal("b\nc", XppSourceReader.Slice(body, 2, 3));
        Assert.Equal("a\nb\nc\nd\ne", XppSourceReader.Slice(body, 0, 99));
        Assert.Null(XppSourceReader.Slice(body, 5, 1));
    }

    [Fact]
    public void AroundPattern_ReturnsContextAroundHits()
    {
        var body = string.Join('\n', Enumerable.Range(1, 10).Select(i => $"line{i}"));
        var hit = XppSourceReader.AroundPattern(body, "line5", contextLines: 1);
        Assert.Contains("4: line4", hit);
        Assert.Contains("5: line5", hit);
        Assert.Contains("6: line6", hit);
        Assert.DoesNotContain("line3", hit);
        Assert.DoesNotContain("line7", hit);
    }

    [Fact]
    public void AroundPattern_BadRegex_ReturnsEmpty()
    {
        var body = "anything";
        Assert.Equal(string.Empty, XppSourceReader.AroundPattern(body, "[unclosed"));
    }

    [Fact]
    public void NameSuggester_Distance_BasicProperties()
    {
        Assert.Equal(0, NameSuggester.Distance("abc", "ABC"));
        Assert.Equal(1, NameSuggester.Distance("abc", "abd"));
        Assert.Equal(3, NameSuggester.Distance("", "abc"));
    }
}
