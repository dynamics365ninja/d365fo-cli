using D365FO.Core;
using Xunit;

namespace D365FO.Cli.Tests;

/// <summary>
/// The CLI entry point's handling of the global <c>--profile</c> option and the
/// missing-profile guardrail (issue #210). Exercised through
/// <see cref="ProfileArgs"/> rather than <c>CliApp.Build().RunAsync()</c>, which
/// must not be re-entered in-process.
/// </summary>
[Collection("EnvIndexDb")]
public sealed class ProfileArgsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "d365fo-profileargs-" + Guid.NewGuid().ToString("N"));
    private readonly string? _prevDir = Environment.GetEnvironmentVariable(D365FoSettings.ConfigDirKey);
    private readonly string? _prevProfile = Environment.GetEnvironmentVariable(D365FoProfiles.ProfileKey);

    public ProfileArgsTests()
    {
        Directory.CreateDirectory(_root);
        Environment.SetEnvironmentVariable(D365FoSettings.ConfigDirKey, _root);
        Environment.SetEnvironmentVariable(D365FoProfiles.ProfileKey, null);
        D365FoProfiles.FlagProfile = null;
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(D365FoSettings.ConfigDirKey, _prevDir);
        Environment.SetEnvironmentVariable(D365FoProfiles.ProfileKey, _prevProfile);
        D365FoProfiles.FlagProfile = null;
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public void Existing_profile_is_selected_and_exported_to_child_processes()
    {
        D365FoProfiles.Save("custA", new Dictionary<string, string>());

        var (args, error) = ProfileArgs.Apply(["--profile", "custA", "search", "class", "Foo"]);

        Assert.Null(error);
        Assert.Equal(new[] { "search", "class", "Foo" }, args);
        Assert.Equal("custA", D365FoProfiles.FlagProfile);
        Assert.Equal("custA", Environment.GetEnvironmentVariable(D365FoProfiles.ProfileKey));
        Assert.Equal(ProfileSource.Flag, D365FoProfiles.GetActive()!.Source);
    }

    [Fact]
    public void Missing_profile_blocks_ordinary_commands()
    {
        var (_, error) = ProfileArgs.Apply(["--profile", "ghost", "search", "class", "Foo"]);

        Assert.NotNull(error);
        Assert.Equal(D365FoErrorCodes.ProfileNotFound, error!.Code);
    }

    [Theory]
    [InlineData("init")]
    [InlineData("config")]
    [InlineData("doctor")]
    [InlineData("version")]
    public void Missing_profile_is_allowed_for_commands_that_create_or_diagnose_it(string command)
    {
        var (args, error) = ProfileArgs.Apply(["--profile", "ghost", command]);

        Assert.Null(error);
        Assert.Equal(new[] { command }, args);
    }

    [Fact]
    public void Help_is_never_blocked()
    {
        Environment.SetEnvironmentVariable(D365FoProfiles.ProfileKey, "ghost");
        Assert.Null(ProfileArgs.Apply(["search", "class", "--help"]).Error);
        Assert.Null(ProfileArgs.Apply([]).Error);
    }

    [Fact]
    public void Profile_without_a_value_is_bad_input()
    {
        var (_, error) = ProfileArgs.Apply(["doctor", "--profile"]);
        Assert.Equal(D365FoErrorCodes.BadInput, error!.Code);
    }

    [Fact]
    public void Invalid_name_is_rejected_and_not_exported()
    {
        var (_, error) = ProfileArgs.Apply(["--profile", "../x", "search", "class", "Foo"]);

        Assert.Equal(D365FoErrorCodes.InvalidProfileName, error!.Code);
        Assert.Null(Environment.GetEnvironmentVariable(D365FoProfiles.ProfileKey));
    }

    [Fact]
    public void No_profile_selected_changes_nothing()
    {
        var (args, error) = ProfileArgs.Apply(["search", "class", "Foo"]);
        Assert.Null(error);
        Assert.Equal(new[] { "search", "class", "Foo" }, args);
        Assert.Null(D365FoProfiles.FlagProfile);
    }
}
