using D365FO.Cli;
using D365FO.Cli.Commands.Generate;
using Xunit;

namespace D365FO.Cli.Tests;

/// <summary>
/// <c>--install-to</c> writes the scaffold itself when the metadata provider cannot do the job. A
/// provider that read the document and refused it has done its job: writing the same document
/// to disk anyway puts the invalid object into the model with only a warning.
/// </summary>
[Collection("EnvIndexDb")]
public sealed class InstallBridgeRejectionTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"d365fo-reject-{Guid.NewGuid():N}");
    private readonly Dictionary<string, string?> _saved = new();
    private readonly Func<string, string, string, string?, (bool ok, string? error)> _bridge = GenerateInstaller.SaveViaBridge;

    public InstallBridgeRejectionTests()
    {
        var store = Path.Combine(_root, "packages");
        Directory.CreateDirectory(Path.Combine(store, "ConFleet", "ConFleet", "AxEnum"));
        Set("D365FO_CUSTOM_PACKAGES_PATH", store);
        Set("D365FO_PACKAGES_PATH", store);
        Set("D365FO_INDEX_DB", Path.Combine(_root, "index.sqlite"));
        Set("D365FO_BRIDGE_ENABLED", "0");
    }

    private void Set(string name, string value)
    {
        _saved[name] = Environment.GetEnvironmentVariable(name);
        Environment.SetEnvironmentVariable(name, value);
    }

    public void Dispose()
    {
        GenerateInstaller.SaveViaBridge = _bridge;
        foreach (var (name, value) in _saved) Environment.SetEnvironmentVariable(name, value);
        try { Directory.Delete(_root, recursive: true); } catch { /* temp */ }
    }

    private static (GenerateInstaller.InstallOutcome Outcome, string? Path, int? Failure) Plan(string error)
    {
        GenerateInstaller.SaveViaBridge = (_, _, _, _) => (false, error);
        var (outcome, path, failure, _) = GenerateInstaller.PlanInstall(
            OutputMode.Kind.Json, "enum", "AxEnum", "ConColor", "ConFleet", "<AxEnum><Name>ConColor</Name></AxEnum>");
        return (outcome, path, failure);
    }

    [Theory]
    [InlineData("XML_DESERIALIZE_FAILED: 'Maybe' is not a valid value of NoYes.")]
    [InlineData("XML_PARSE_FAILED: Unexpected end of file.")]
    public void A_document_the_provider_refused_is_not_written(string error)
    {
        var plan = Plan(error);

        Assert.Equal(GenerateInstaller.InstallOutcome.Failed, plan.Outcome);
        Assert.Null(plan.Path);
        Assert.Equal(1, plan.Failure);
    }

    [Theory]
    [InlineData("bridge is not available (set D365FO_BRIDGE_ENABLED=1 and D365FO_BRIDGE_PATH).")]
    [InlineData("METADATA_UNAVAILABLE: IMetadataProvider failed to initialise.")]
    [InlineData("CREATE_FAILED: Model 'ConFleet' was not found.")]
    [InlineData("TYPE_NOT_FOUND: Could not resolve Ax type for root element 'AxEnum'.")]
    public void A_provider_that_cannot_do_the_job_still_falls_back_to_the_scaffold(string error)
    {
        var plan = Plan(error);

        Assert.Equal(GenerateInstaller.InstallOutcome.WriteScaffold, plan.Outcome);
        Assert.EndsWith(Path.Combine("AxEnum", "ConColor.xml"), plan.Path);
    }
}
