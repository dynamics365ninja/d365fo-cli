using Xunit;

namespace D365FO.Core.Tests;

[Collection(EnvironmentCollectionDefinition.Name)]
public class D365FoSettingsResolveTests
{
    // Use unlikely key names so a real settings.json on the test host cannot
    // collide with these deterministic env-var assertions.
    private const string Key = "D365FO_TEST_RESOLVE_KEY_XYZ";
    private const string FlagKey = "D365FO_TEST_RESOLVE_FLAG_XYZ";

    [Fact]
    public void Resolve_returns_env_value_when_set()
    {
        var prev = Environment.GetEnvironmentVariable(Key);
        try
        {
            Environment.SetEnvironmentVariable(Key, "hello");
            Assert.Equal("hello", D365FoSettings.Resolve(Key));
        }
        finally
        {
            Environment.SetEnvironmentVariable(Key, prev);
        }
    }

    [Fact]
    public void Resolve_returns_null_when_unset_in_env_and_json()
    {
        var prev = Environment.GetEnvironmentVariable(Key);
        try
        {
            Environment.SetEnvironmentVariable(Key, null);
            Assert.Null(D365FoSettings.Resolve(Key));
        }
        finally
        {
            Environment.SetEnvironmentVariable(Key, prev);
        }
    }

    [Theory]
    [InlineData("1", true)]
    [InlineData("true", true)]
    [InlineData("TRUE", true)]
    [InlineData("0", false)]
    [InlineData("false", false)]
    [InlineData("yes", false)]
    public void ResolveFlag_parses_truthy_values(string value, bool expected)
    {
        var prev = Environment.GetEnvironmentVariable(FlagKey);
        try
        {
            Environment.SetEnvironmentVariable(FlagKey, value);
            Assert.Equal(expected, D365FoSettings.ResolveFlag(FlagKey));
        }
        finally
        {
            Environment.SetEnvironmentVariable(FlagKey, prev);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ResolveFlag_returns_default_when_unset(bool defaultValue)
    {
        var prev = Environment.GetEnvironmentVariable(FlagKey);
        try
        {
            Environment.SetEnvironmentVariable(FlagKey, null);
            Assert.Equal(defaultValue, D365FoSettings.ResolveFlag(FlagKey, defaultValue));
        }
        finally
        {
            Environment.SetEnvironmentVariable(FlagKey, prev);
        }
    }

    // ---- JSON fallback tests -------------------------------------------------

    [Fact]
    public void Resolve_falls_back_to_settings_json_when_env_not_set()
    {
        var tmp = Path.GetTempFileName();
        try
        {
            File.WriteAllText(tmp, $"{{\"{Key}\": \"from-json\"}}");
            D365FoSettings.ConfigPathOverrideForTests = tmp;
            D365FoSettings.ClearCacheForTests();

            // Env var must be absent so the JSON fallback is reached.
            var prev = Environment.GetEnvironmentVariable(Key);
            Environment.SetEnvironmentVariable(Key, null);
            try
            {
                Assert.Equal("from-json", D365FoSettings.Resolve(Key));
            }
            finally
            {
                Environment.SetEnvironmentVariable(Key, prev);
            }
        }
        finally
        {
            D365FoSettings.ConfigPathOverrideForTests = null;
            D365FoSettings.ClearCacheForTests();
            File.Delete(tmp);
        }
    }

    [Fact]
    public void Resolve_env_var_takes_priority_over_settings_json()
    {
        var tmp = Path.GetTempFileName();
        try
        {
            File.WriteAllText(tmp, $"{{\"{Key}\": \"from-json\"}}");
            D365FoSettings.ConfigPathOverrideForTests = tmp;
            D365FoSettings.ClearCacheForTests();

            var prev = Environment.GetEnvironmentVariable(Key);
            Environment.SetEnvironmentVariable(Key, "from-env");
            try
            {
                Assert.Equal("from-env", D365FoSettings.Resolve(Key));
            }
            finally
            {
                Environment.SetEnvironmentVariable(Key, prev);
            }
        }
        finally
        {
            D365FoSettings.ConfigPathOverrideForTests = null;
            D365FoSettings.ClearCacheForTests();
            File.Delete(tmp);
        }
    }

    [Fact]
    public void Resolve_settings_json_keys_are_case_insensitive()
    {
        var tmp = Path.GetTempFileName();
        try
        {
            // Store the key in lowercase; resolve with the canonical uppercase name.
            File.WriteAllText(tmp, $"{{\"{Key.ToLowerInvariant()}\": \"case-ok\"}}");
            D365FoSettings.ConfigPathOverrideForTests = tmp;
            D365FoSettings.ClearCacheForTests();

            var prev = Environment.GetEnvironmentVariable(Key);
            Environment.SetEnvironmentVariable(Key, null);
            try
            {
                Assert.Equal("case-ok", D365FoSettings.Resolve(Key));
            }
            finally
            {
                Environment.SetEnvironmentVariable(Key, prev);
            }
        }
        finally
        {
            D365FoSettings.ConfigPathOverrideForTests = null;
            D365FoSettings.ClearCacheForTests();
            File.Delete(tmp);
        }
    }

    // ---- D365FO_CUSTOM_PACKAGES_PATH / deprecated-alias fallback -------------

    [Fact]
    public void FromEnvironment_reads_custom_packages_from_new_var()
    {
        WithCustomPackagesEnv(custom: @"C:\Custom", extra: null, () =>
        {
            var cfg = D365FoSettings.FromEnvironment();
            Assert.Equal(new[] { @"C:\Custom" }, cfg.CustomPackagesPaths);
        });
    }

    [Fact]
    public void FromEnvironment_falls_back_to_deprecated_extra_var()
    {
        WithCustomPackagesEnv(custom: null, extra: @"C:\Legacy", () =>
        {
            var cfg = D365FoSettings.FromEnvironment();
            Assert.Equal(new[] { @"C:\Legacy" }, cfg.CustomPackagesPaths);
        });
    }

    [Fact]
    public void FromEnvironment_new_var_wins_over_deprecated_extra_var()
    {
        WithCustomPackagesEnv(custom: @"C:\Custom", extra: @"C:\Legacy", () =>
        {
            var cfg = D365FoSettings.FromEnvironment();
            Assert.Equal(new[] { @"C:\Custom" }, cfg.CustomPackagesPaths);
        });
    }

    private static void WithCustomPackagesEnv(string? custom, string? extra, Action body)
    {
        var prevCustom = Environment.GetEnvironmentVariable("D365FO_CUSTOM_PACKAGES_PATH");
        var prevExtra = Environment.GetEnvironmentVariable("D365FO_EXTRA_PACKAGES_PATH");
        try
        {
            Environment.SetEnvironmentVariable("D365FO_CUSTOM_PACKAGES_PATH", custom);
            Environment.SetEnvironmentVariable("D365FO_EXTRA_PACKAGES_PATH", extra);
            body();
        }
        finally
        {
            Environment.SetEnvironmentVariable("D365FO_CUSTOM_PACKAGES_PATH", prevCustom);
            Environment.SetEnvironmentVariable("D365FO_EXTRA_PACKAGES_PATH", prevExtra);
        }
    }

    [Fact]
    public void ResolveFlag_reads_flag_from_settings_json()
    {
        var tmp = Path.GetTempFileName();
        try
        {
            File.WriteAllText(tmp, $"{{\"{FlagKey}\": \"true\"}}");
            D365FoSettings.ConfigPathOverrideForTests = tmp;
            D365FoSettings.ClearCacheForTests();

            var prev = Environment.GetEnvironmentVariable(FlagKey);
            Environment.SetEnvironmentVariable(FlagKey, null);
            try
            {
                Assert.True(D365FoSettings.ResolveFlag(FlagKey));
            }
            finally
            {
                Environment.SetEnvironmentVariable(FlagKey, prev);
            }
        }
        finally
        {
            D365FoSettings.ConfigPathOverrideForTests = null;
            D365FoSettings.ClearCacheForTests();
            File.Delete(tmp);
        }
    }
}
