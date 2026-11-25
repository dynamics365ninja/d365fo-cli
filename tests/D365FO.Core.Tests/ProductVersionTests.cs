using System.Text.RegularExpressions;
using D365FO.Core;
using Xunit;

namespace D365FO.Core.Tests;

public class ProductVersionTests
{
    [Fact]
    public void The_build_reports_a_semantic_version_rather_than_the_sdk_default()
    {
        // Issue #284: an unset version is reported as 1.0.0, which names no release.
        Assert.Matches(new Regex(@"^\d+\.\d+\.\d+(-[0-9A-Za-z.-]+)?$"), ProductVersion.Version);
        Assert.NotEqual("1.0.0", ProductVersion.Version);
        Assert.StartsWith(ProductVersion.Version, ProductVersion.Informational);
    }

    [Theory]
    [InlineData("0.2.0+abc123", "0.2.0", "abc123")]
    [InlineData("0.3.0-dev", "0.3.0-dev", null)]
    public void Split_separates_the_version_from_the_commit(string informational, string version, string? commit)
    {
        Assert.Equal((version, commit), ProductVersion.Split(informational));
    }

    [Theory]
    [InlineData("0.2.0+abc", "0.2.0+abc", true)]
    [InlineData("0.2.0+abc", "0.2.0", true)] // a source-archive build records no commit
    [InlineData("0.2.0+abc", "0.2.0+def", false)]
    [InlineData("0.2.0+abc", "0.3.0+abc", false)]
    [InlineData("0.2.0+abc", "1.0.0", false)] // a bridge built before versions were stamped
    public void SameBuild_compares_version_and_commit(string first, string second, bool same)
    {
        Assert.Equal(same, ProductVersion.SameBuild(first, second));
    }
}
