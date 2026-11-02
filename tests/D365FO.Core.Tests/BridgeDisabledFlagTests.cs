using D365FO.Core.Bridge;
using D365FO.Core.Journal;
using Xunit;

namespace D365FO.Core.Tests;

/// <summary>
/// <c>D365FO_BRIDGE_ENABLED=0</c> switches the bridge off for writes too. Before, the write
/// helpers asked only whether the executable existed, so <c>generate --install-to</c> created
/// objects through the live metadata provider while <c>doctor</c> reported the bridge disabled.
/// </summary>
[Collection(EnvironmentCollectionDefinition.Name)]
public sealed class BridgeDisabledFlagTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "d365fo-bridgeflag-" + Guid.NewGuid().ToString("N"));
    private readonly string? _enabled = Environment.GetEnvironmentVariable("D365FO_BRIDGE_ENABLED");
    private readonly string? _path = Environment.GetEnvironmentVariable("D365FO_BRIDGE_PATH");
    private readonly string? _configDir = Environment.GetEnvironmentVariable(D365FoSettings.ConfigDirKey);
    private readonly string? _profile = Environment.GetEnvironmentVariable(D365FoProfiles.ProfileKey);
    private readonly string? _flagProfile = D365FoProfiles.FlagProfile;

    public BridgeDisabledFlagTests()
    {
        Directory.CreateDirectory(_dir);
        // An empty config root and no profile, so a D365FO_BRIDGE_ENABLED in the host's
        // settings.json or active profile cannot stand in for the "unset" case.
        Environment.SetEnvironmentVariable(D365FoSettings.ConfigDirKey, _dir);
        Environment.SetEnvironmentVariable(D365FoProfiles.ProfileKey, null);
        D365FoProfiles.FlagProfile = null;
        D365FoSettings.ClearCacheForTests();
        // An executable that exists, so only the flag can stop the call. It is not a real
        // program: if the gate let the call through, starting it would fail with a different error.
        var exe = Path.Combine(_dir, "D365FO.Bridge.exe");
        File.WriteAllText(exe, "not a program");
        Environment.SetEnvironmentVariable("D365FO_BRIDGE_PATH", exe);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("D365FO_BRIDGE_ENABLED", _enabled);
        Environment.SetEnvironmentVariable("D365FO_BRIDGE_PATH", _path);
        Environment.SetEnvironmentVariable(D365FoSettings.ConfigDirKey, _configDir);
        Environment.SetEnvironmentVariable(D365FoProfiles.ProfileKey, _profile);
        D365FoProfiles.FlagProfile = _flagProfile;
        D365FoSettings.ClearCacheForTests();
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    [Theory]
    [InlineData("0")]
    [InlineData("false")]
    public void Install_through_the_provider_is_refused_when_the_bridge_is_switched_off(string flag)
    {
        Environment.SetEnvironmentVariable("D365FO_BRIDGE_ENABLED", flag);

        var (ok, error) = BridgeGate.TrySaveObject("table", "Foo", "MyModel", "<AxTable />");

        Assert.False(ok);
        Assert.Contains("D365FO_BRIDGE_ENABLED", error);
        Assert.True(BridgeGate.ExplicitlyDisabled());
    }

    [Fact]
    public void Update_and_delete_are_refused_too_when_the_bridge_is_switched_off()
    {
        Environment.SetEnvironmentVariable("D365FO_BRIDGE_ENABLED", "0");

        Assert.Contains("D365FO_BRIDGE_ENABLED", BridgeGate.TryUpdateObject("table", "Foo", "MyModel", "<AxTable />").error);
        Assert.Contains("D365FO_BRIDGE_ENABLED", BridgeGate.TryDeleteObject("table", "Foo", "MyModel").error);
        Assert.Null(BridgeGate.TryGetModelFolder("MyModel"));
    }

    [Fact]
    public void Modify_object_and_method_are_refused_when_the_bridge_is_switched_off()
    {
        Environment.SetEnvironmentVariable("D365FO_BRIDGE_ENABLED", "0");
        // Explicit options naming an executable that exists: only the flag can refuse.
        var options = new BridgeOptions { ExecutablePath = Environment.GetEnvironmentVariable("D365FO_BRIDGE_PATH") };

        var obj = ObjectModifyEngine.Modify(new ObjectModifyEngine.ModifyRequest
        {
            Operation = ObjectModifyEngine.Operation.SetProperty,
            Kind = "table",
            ObjectName = "Foo",
            Member = "Label",
            Value = "@SYS1",
            Model = "MyModel",
        }, null, options);
        var method = MethodModifyEngine.Modify(
            new MethodModifyEngine.ModifyRequest("class", "Foo", "bar", "return;", Model: "MyModel"), null, options);

        Assert.Equal(D365FoErrorCodes.BridgeRequired, obj.Error?.Code);
        Assert.Contains("switched off", obj.Error?.Message);
        Assert.Equal(D365FoErrorCodes.BridgeRequired, method.Error?.Code);
        Assert.Contains("switched off", method.Error?.Message);
    }

    [Fact]
    public void Undo_of_a_bridge_write_is_refused_and_kept_when_the_bridge_is_switched_off()
    {
        Environment.SetEnvironmentVariable("D365FO_BRIDGE_ENABLED", "0");
        var journal = new ModificationJournal(Path.Combine(_dir, "journal"));
        journal.Append(new JournalEntry(
            Id: Guid.NewGuid().ToString("N"), TimestampUtc: DateTimeOffset.UtcNow, Command: "generate table --install-to",
            TargetType: JournalTargetType.AotObject, Kind: "table", ObjectName: "Foo", SecondaryKey: null,
            Model: "MyModel", Operation: JournalOperation.Create, WritePath: JournalWritePath.Bridge,
            TargetPath: null, PreImage: null, IsTombstone: true, RnrProjDelta: null));

        var result = UndoEngine.Undo(journal, 1, dryRun: false);

        Assert.False(result.AllOk);
        Assert.Contains("D365FO_BRIDGE_ENABLED", result.Steps[0].Error);
        Assert.Single(journal.List(10));
    }

    [Fact]
    public void Verify_says_the_bridge_is_switched_off_rather_than_missing()
    {
        Environment.SetEnvironmentVariable("D365FO_BRIDGE_ENABLED", "0");

        var (outcome, detail) = BridgeGate.TryVerifyObject("table", "Foo");

        Assert.Equal(BridgeGate.VerifyOutcome.Skipped, outcome);
        Assert.Contains("D365FO_BRIDGE_ENABLED", detail);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("1")]
    [InlineData("true")]
    public void An_unset_or_enabled_flag_is_not_a_switch_off(string? flag)
    {
        // Unset keeps the old behaviour (use the executable when it is there), so an
        // installation that never set the flag is not broken.
        Environment.SetEnvironmentVariable("D365FO_BRIDGE_ENABLED", flag);

        Assert.False(BridgeGate.ExplicitlyDisabled());
    }
}
