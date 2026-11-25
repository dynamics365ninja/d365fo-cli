using Xunit;

namespace D365FO.Core.Tests;

/// <summary>
/// A settings.json that cannot be read is not an empty one. Read as empty, a run used defaults in
/// silence, and the next save replaced the file with only the keys it was saving.
/// </summary>
[Collection(EnvironmentCollectionDefinition.Name)]
public sealed class ConfigUnreadableTests : IDisposable
{
    // A trailing comma and a value that is no string: both are how a hand edit breaks the file.
    private const string Broken = "{ \"D365FO_PACKAGES_PATH\": \"K:\\\\Packages\", \"D365FO_BRIDGE_ENABLED\": true }";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "d365fo-badcfg-" + Guid.NewGuid().ToString("N"));
    private readonly string? _savedConfigDir = Environment.GetEnvironmentVariable(D365FoSettings.ConfigDirKey);
    private readonly string? _savedProfile = Environment.GetEnvironmentVariable(D365FoProfiles.ProfileKey);
    private readonly string? _savedFlagProfile = D365FoProfiles.FlagProfile;

    public ConfigUnreadableTests()
    {
        Directory.CreateDirectory(_root);
        Environment.SetEnvironmentVariable(D365FoSettings.ConfigDirKey, _root);
        Environment.SetEnvironmentVariable(D365FoProfiles.ProfileKey, null);
        D365FoProfiles.FlagProfile = null;
        D365FoSettings.ClearCacheForTests();
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(D365FoSettings.ConfigDirKey, _savedConfigDir);
        Environment.SetEnvironmentVariable(D365FoProfiles.ProfileKey, _savedProfile);
        D365FoProfiles.FlagProfile = _savedFlagProfile;
        D365FoSettings.ClearCacheForTests();
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    private static string SettingsPath => D365FoSettings.GetDefaultConfigPath();

    [Fact]
    public void Saving_does_not_replace_a_file_that_cannot_be_read()
    {
        File.WriteAllText(SettingsPath, Broken);

        var error = Assert.Throws<ConfigUnreadableException>(() => D365FoSettings.SaveJsonConfig(
            new Dictionary<string, string> { ["D365FO_WORKSPACE_PATH"] = "C:\\ws" }));

        Assert.Contains(SettingsPath, error.Message);
        Assert.Equal(Broken, File.ReadAllText(SettingsPath));
    }

    [Fact]
    public void The_check_names_the_unreadable_file()
    {
        File.WriteAllText(SettingsPath, Broken);

        var error = D365FoSettings.CheckConfigFiles();

        Assert.NotNull(error);
        Assert.Equal(D365FoErrorCodes.ConfigUnreadable, error!.Code);
        Assert.Contains(SettingsPath, error.Message);
    }

    [Fact]
    public void The_active_profile_file_is_checked_too()
    {
        var path = D365FoProfiles.GetProfilePath("customer");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "{ nonsense");
        Environment.SetEnvironmentVariable(D365FoProfiles.ProfileKey, "customer");

        var error = D365FoSettings.CheckConfigFiles();

        Assert.NotNull(error);
        Assert.Contains(path, error!.Message);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("{ \"D365FO_WORKSPACE_PATH\": \"C:/ws\" }")]
    public void A_missing_empty_or_valid_file_passes_and_can_be_saved(string? content)
    {
        if (content is not null) File.WriteAllText(SettingsPath, content);

        Assert.Null(D365FoSettings.CheckConfigFiles());
        D365FoSettings.SaveJsonConfig(new Dictionary<string, string> { ["D365FO_CUSTOM_MODELS"] = "Con*" });
        Assert.Contains("Con*", File.ReadAllText(SettingsPath));
    }
}
