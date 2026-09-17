using Xunit;

namespace D365FO.Core.Tests;

/// <summary>
/// Named configuration profiles (issue #210): selection precedence, the
/// env → profile → settings.json chain, the per-profile index DB, name
/// validation and the global <c>--profile</c> argument stripping.
/// </summary>
/// <remarks>
/// Every test runs against a throw-away config root (<c>D365FO_CONFIG_DIR</c>)
/// with the selection env var and the keys it asserts on cleared, so the real
/// <c>%LOCALAPPDATA%\d365fo-cli</c> on the host is never read or written.
/// </remarks>
[Collection(EnvironmentCollectionDefinition.Name)]
public sealed class D365FoProfilesTests : IDisposable
{
    private static readonly string[] IsolatedVars =
    {
        D365FoSettings.ConfigDirKey,
        D365FoProfiles.ProfileKey,
        "D365FO_PACKAGES_PATH",
        "D365FO_INDEX_DB",
        "D365FO_CUSTOM_PACKAGES_PATH",
        "D365FO_EXTRA_PACKAGES_PATH",
        "D365FO_TEST_PROFILE_KEY_XYZ",
    };

    private readonly string _root;
    private readonly Dictionary<string, string?> _saved = new();

    public D365FoProfilesTests()
    {
        foreach (var v in IsolatedVars)
        {
            _saved[v] = Environment.GetEnvironmentVariable(v);
            Environment.SetEnvironmentVariable(v, null);
        }
        _root = Path.Combine(Path.GetTempPath(), "d365fo-profiles-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        Environment.SetEnvironmentVariable(D365FoSettings.ConfigDirKey, _root);
        D365FoProfiles.FlagProfile = null;
        D365FoSettings.ConfigPathOverrideForTests = null;
        D365FoSettings.ClearCacheForTests();
    }

    public void Dispose()
    {
        foreach (var (k, v) in _saved) Environment.SetEnvironmentVariable(k, v);
        D365FoProfiles.FlagProfile = null;
        D365FoSettings.ClearCacheForTests();
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    private void WriteGlobal(string json) => File.WriteAllText(Path.Combine(_root, "settings.json"), json);

    private void WriteProfile(string name, string json)
    {
        Directory.CreateDirectory(Path.Combine(_root, "profiles"));
        File.WriteAllText(Path.Combine(_root, "profiles", name + ".json"), json);
    }

    // ---- no profile: unchanged behavior ------------------------------------

    [Fact]
    public void Without_a_profile_settings_json_and_default_db_apply_as_before()
    {
        WriteGlobal("""{"D365FO_PACKAGES_PATH": "C:/Global"}""");
        WriteProfile("custA", """{"D365FO_PACKAGES_PATH": "C:/CustA"}""");

        Assert.Null(D365FoProfiles.GetActive());
        var cfg = D365FoSettings.FromEnvironment();
        Assert.Equal("C:/Global", cfg.PackagesPath);
        Assert.Equal(Path.Combine(_root, "d365fo-index.sqlite"), cfg.DatabasePath);
        Assert.Null(D365FoProfiles.CheckActive());
    }

    // ---- precedence: env > profile > settings.json --------------------------

    [Fact]
    public void Profile_overrides_settings_json()
    {
        WriteGlobal("""{"D365FO_PACKAGES_PATH": "C:/Global", "D365FO_TEST_PROFILE_KEY_XYZ": "C:/GlobalWs"}""");
        WriteProfile("custA", """{"D365FO_PACKAGES_PATH": "C:/CustA"}""");
        Environment.SetEnvironmentVariable(D365FoProfiles.ProfileKey, "custA");

        Assert.Equal(("C:/CustA", "profile:custA"), D365FoSettings.ResolveWithSource("D365FO_PACKAGES_PATH"));
        // Keys the profile does not set still fall through to settings.json.
        Assert.Equal(("C:/GlobalWs", "settings"), D365FoSettings.ResolveWithSource("D365FO_TEST_PROFILE_KEY_XYZ"));
    }

    [Fact]
    public void Env_var_overrides_profile()
    {
        WriteProfile("custA", """{"D365FO_PACKAGES_PATH": "C:/CustA"}""");
        Environment.SetEnvironmentVariable(D365FoProfiles.ProfileKey, "custA");
        Environment.SetEnvironmentVariable("D365FO_PACKAGES_PATH", "C:/Env");

        Assert.Equal(("C:/Env", "env"), D365FoSettings.ResolveWithSource("D365FO_PACKAGES_PATH"));
    }

    [Fact]
    public void Selection_precedence_is_flag_then_env_then_settings()
    {
        WriteGlobal("""{"D365FO_PROFILE": "fromSettings"}""");
        Assert.Equal(("fromSettings", ProfileSource.Settings), Sel());

        Environment.SetEnvironmentVariable(D365FoProfiles.ProfileKey, "fromEnv");
        Assert.Equal(("fromEnv", ProfileSource.Environment), Sel());

        D365FoProfiles.FlagProfile = "fromFlag";
        Assert.Equal(("fromFlag", ProfileSource.Flag), Sel());

        static (string, ProfileSource) Sel()
        {
            var a = D365FoProfiles.GetActive()!;
            return (a.Name, a.Source);
        }
    }

    [Fact]
    public void Deprecated_alias_in_profile_beats_new_key_in_settings_json()
    {
        WriteGlobal("""{"D365FO_CUSTOM_PACKAGES_PATH": "C:/Global"}""");
        WriteProfile("custA", """{"D365FO_EXTRA_PACKAGES_PATH": "C:/ProfileLegacy"}""");
        Environment.SetEnvironmentVariable(D365FoProfiles.ProfileKey, "custA");

        Assert.Equal(new[] { "C:/ProfileLegacy" }, D365FoSettings.FromEnvironment().CustomPackagesPaths);
    }

    [Fact]
    public void Deprecated_env_var_still_beats_profile()
    {
        WriteProfile("custA", """{"D365FO_CUSTOM_PACKAGES_PATH": "C:/Profile"}""");
        Environment.SetEnvironmentVariable(D365FoProfiles.ProfileKey, "custA");
        Environment.SetEnvironmentVariable("D365FO_EXTRA_PACKAGES_PATH", "C:/EnvLegacy");

        Assert.Equal(new[] { "C:/EnvLegacy" }, D365FoSettings.FromEnvironment().CustomPackagesPaths);
    }

    // ---- index DB per profile ------------------------------------------------

    [Fact]
    public void Profile_gets_its_own_default_db_and_ignores_the_global_one()
    {
        WriteGlobal("""{"D365FO_INDEX_DB": "C:/global.sqlite"}""");
        WriteProfile("custA", "{}");
        Environment.SetEnvironmentVariable(D365FoProfiles.ProfileKey, "custA");

        Assert.Equal(
            Path.Combine(_root, "profiles", "custA", "d365fo-index.sqlite"),
            D365FoSettings.FromEnvironment().DatabasePath);
    }

    [Fact]
    public void Profile_db_value_and_env_var_still_win()
    {
        WriteProfile("custA", """{"D365FO_INDEX_DB": "C:/custA.sqlite"}""");
        Environment.SetEnvironmentVariable(D365FoProfiles.ProfileKey, "custA");
        Assert.Equal(("C:/custA.sqlite", "profile:custA"), D365FoSettings.ResolveDatabasePath());

        Environment.SetEnvironmentVariable("D365FO_INDEX_DB", "C:/env.sqlite");
        Assert.Equal("C:/env.sqlite", D365FoSettings.FromEnvironment().DatabasePath);
        Assert.Equal("C:/flag.sqlite", D365FoSettings.FromEnvironment("C:/flag.sqlite").DatabasePath);
    }

    [Fact]
    public void Selected_but_missing_profile_still_points_at_its_own_db()
    {
        // `init --profile <new>` resolves the DB before the profile file exists.
        WriteGlobal("""{"D365FO_INDEX_DB": "C:/global.sqlite"}""");
        D365FoProfiles.FlagProfile = "brandNew";

        Assert.Equal(
            Path.Combine(_root, "profiles", "brandNew", "d365fo-index.sqlite"),
            D365FoSettings.ResolveDatabasePath().Path);
    }

    // ---- guardrail -------------------------------------------------------------

    [Fact]
    public void Missing_profile_is_a_structured_error_not_a_fallback()
    {
        WriteGlobal("""{"D365FO_PACKAGES_PATH": "C:/Global"}""");
        WriteProfile("other", "{}");
        Environment.SetEnvironmentVariable(D365FoProfiles.ProfileKey, "nope");

        var err = D365FoProfiles.CheckActive();
        Assert.NotNull(err);
        Assert.Equal(D365FoErrorCodes.ProfileNotFound, err!.Code);
        Assert.Contains("nope", err.Message);
        Assert.Contains("other", err.Hint);
    }

    [Theory]
    [InlineData("..")]
    [InlineData(@"..\evil")]
    [InlineData("a/b")]
    [InlineData("-dash")]
    [InlineData(".hidden")]
    [InlineData("has space")]
    public void Invalid_selected_name_is_rejected_and_never_read(string name)
    {
        Environment.SetEnvironmentVariable(D365FoProfiles.ProfileKey, name);

        var active = D365FoProfiles.GetActive()!;
        Assert.False(active.IsValidName);
        Assert.Null(active.FilePath);
        Assert.Equal(D365FoErrorCodes.InvalidProfileName, D365FoProfiles.CheckActive()!.Code);
        Assert.Throws<ArgumentException>(() => D365FoProfiles.GetProfilePath(name));
    }

    [Theory]
    [InlineData("custA", true)]
    [InlineData("contoso-uat_2.1", true)]
    [InlineData("A", true)]
    [InlineData("", false)]
    [InlineData("x/y", false)]
    [InlineData("_lead", false)]
    public void Name_validation(string name, bool expected)
        => Assert.Equal(expected, D365FoProfiles.IsValidName(name));

    // ---- persistence -------------------------------------------------------------

    [Fact]
    public void Save_creates_profile_and_list_reports_it()
    {
        var path = D365FoProfiles.Save("custB", new Dictionary<string, string> { ["D365FO_PACKAGES_PATH"] = "C:/B" });

        Assert.Equal(Path.Combine(_root, "profiles", "custB.json"), path);
        Assert.Equal(new[] { "custB" }, D365FoProfiles.List());
        D365FoProfiles.FlagProfile = "custB";
        Assert.Equal("C:/B", D365FoSettings.Resolve("D365FO_PACKAGES_PATH"));
        Assert.Null(D365FoProfiles.CheckActive());
    }

    [Fact]
    public void SaveJsonConfig_can_remove_the_persisted_selection()
    {
        D365FoSettings.SaveJsonConfig(new Dictionary<string, string> { [D365FoProfiles.ProfileKey] = "custA" });
        Assert.Equal("custA", D365FoProfiles.GetActive()!.Name);

        D365FoSettings.SaveJsonConfig(new Dictionary<string, string>(), remove: [D365FoProfiles.ProfileKey]);
        Assert.Null(D365FoProfiles.GetActive());
    }

    // ---- --profile argument stripping --------------------------------------------

    public static TheoryData<string[], string?, string[]> StripCases() => new()
    {
        { ["--profile", "custA", "search", "class", "Foo"], "custA", ["search", "class", "Foo"] },
        { ["search", "class", "Foo", "--profile", "custA"], "custA", ["search", "class", "Foo"] },
        { ["--profile=custA", "doctor"], "custA", ["doctor"] },
        // `init --persist-profile` is a different option and must survive.
        { ["init", "--persist-profile"], null, ["init", "--persist-profile"] },
        // Everything after a literal `--` is passed through untouched.
        { ["x", "--", "--profile", "keep"], null, ["x", "--", "--profile", "keep"] },
    };

    public static TheoryData<string[]> MissingValueCases() => new()
    {
        { ["doctor", "--profile"] },
        { ["--profile", "--output", "json"] },
        { ["--profile=", "doctor"] },
    };

    [Theory]
    [MemberData(nameof(StripCases))]
    public void StripProfileArg_removes_only_the_global_option(string[] args, string? profile, string[] rest)
    {
        var (outArgs, outProfile, error) = D365FoProfiles.StripProfileArg(args);
        Assert.Null(error);
        Assert.Equal(profile, outProfile);
        Assert.Equal(rest, outArgs);
    }

    [Theory]
    [MemberData(nameof(MissingValueCases))]
    public void StripProfileArg_reports_a_missing_value(string[] args)
    {
        var (_, profile, error) = D365FoProfiles.StripProfileArg(args);
        Assert.Null(profile);
        Assert.NotNull(error);
    }
}
