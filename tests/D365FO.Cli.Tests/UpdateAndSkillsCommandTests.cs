using D365FO.Cli.Commands.Ops;
using D365FO.Core.Ops;
using Xunit;

namespace D365FO.Cli.Tests;

public sealed class UpdateAndSkillsCommandTests
{
    [Theory]
    [InlineData(InstallKind.Npm, true, "npm install -g d365fo-cli@0.3.1")]
    [InlineData(InstallKind.Installer, true, "$env:D365FO_CLI_VERSION = 'v0.3.1'")]
    [InlineData(InstallKind.Installer, false, "D365FO_CLI_VERSION=v0.3.1 bash")]
    [InlineData(InstallKind.Source, true, "git checkout v0.3.1")]
    [InlineData(InstallKind.Archive, true, "archive of v0.3.1")]
    public void Each_installation_is_told_how_to_reach_a_version(InstallKind kind, bool windows, string expected) =>
        Assert.Contains(expected, UpdateCommand.Instructions(kind, "0.3.1", windows));

    [Theory]
    [InlineData("update")]
    [InlineData("skills")]
    public void Commands_that_read_no_configuration_run_despite_a_broken_profile(string command) =>
        Assert.True(ProfileArgs.IsExempt([command]));
}
