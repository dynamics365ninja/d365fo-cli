using D365FO.Cli.Commands.Index;
using D365FO.Core.Index;

namespace D365FO.Cli.Tests;

/// <summary>
/// <c>index refresh --prune</c> removes models that left the configured roots, and a refresh
/// without it removes nothing.
/// </summary>
[Collection("EnvIndexDb")]
public sealed class IndexRefreshPruneTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"d365fo-prune-{Guid.NewGuid():N}");
    private readonly string _packages;
    private readonly string _db;

    public IndexRefreshPruneTests()
    {
        _packages = Path.Combine(_root, "PackagesLocalDirectory");
        _db = Path.Combine(_root, "index.sqlite");

        var keep = Path.Combine(_packages, "ConFleet", "ConFleet", "AxClass");
        Directory.CreateDirectory(keep);
        File.WriteAllText(Path.Combine(keep, "ConFleetKeep.xml"),
            "<AxClass><Name>ConFleetKeep</Name><SourceCode><Declaration><![CDATA[public class ConFleetKeep { }]]></Declaration><Methods /></SourceCode></AxClass>");

        // Indexed from a clone that is no longer configured anywhere.
        var gone = Path.Combine(_root, "OldClone", "ConFleetTests", "ConFleetTests", "AxClass", "ConFleetTest.xml");
        var repo = new MetadataRepository(_db);
        repo.EnsureSchema();
        repo.ApplyExtract(ExtractBatch.Empty("ConFleetTests") with
        {
            IsCustom = true,
            Classes = new[] { new ExtractedClass("ConFleetTest", null, false, false, gone, Array.Empty<ExtractedMethod>()) },
        });
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    [Fact]
    public void Refresh_with_prune_deletes_a_model_that_left_the_configured_roots()
    {
        var output = Extract(prune: true);

        Assert.Contains("\"pruned\":[\"ConFleetTests\"]", output, StringComparison.Ordinal);
        var models = new MetadataRepository(_db).ListModels().Select(m => m.Name).ToList();
        Assert.DoesNotContain("ConFleetTests", models);
        Assert.Contains("ConFleet", models);
    }

    [Fact]
    public void Refresh_without_prune_leaves_a_departed_model_in_place()
    {
        // Deletion is the caller's decision. Reporting is what the default does.
        Extract(prune: false);

        Assert.Contains(new MetadataRepository(_db).ListModels(), m => m.Name == "ConFleetTests");
    }

    [Fact]
    public void Refresh_with_prune_against_another_root_keeps_the_models_of_the_configured_packages_root()
    {
        // `--packages` pointed at a custom clone must not make the configured standard root look gone.
        var stdRoot = Path.Combine(_root, "Standard");
        var stdClass = Path.Combine(stdRoot, "ApplicationSuite", "ApplicationSuite", "AxClass", "SalesTable.xml");
        Directory.CreateDirectory(Path.GetDirectoryName(stdClass)!);
        File.WriteAllText(stdClass, "<AxClass><Name>SalesTable</Name></AxClass>");
        new MetadataRepository(_db).ApplyExtract(ExtractBatch.Empty("ApplicationSuite") with
        {
            Classes = new[] { new ExtractedClass("SalesTable", null, false, false, stdClass, Array.Empty<ExtractedMethod>()) },
        });

        var old = Environment.GetEnvironmentVariable("D365FO_PACKAGES_PATH");
        Environment.SetEnvironmentVariable("D365FO_PACKAGES_PATH", stdRoot);
        try
        {
            var output = Extract(prune: true);

            Assert.Contains("\"pruned\":[\"ConFleetTests\"]", output, StringComparison.Ordinal);
            Assert.Contains(new MetadataRepository(_db).ListModels(), m => m.Name == "ApplicationSuite");
        }
        finally
        {
            Environment.SetEnvironmentVariable("D365FO_PACKAGES_PATH", old);
        }
    }

    private string Extract(bool prune)
    {
        var original = Console.Out;
        using var writer = new StringWriter();
        try
        {
            Console.SetOut(writer);
            Assert.Equal(0, IndexExtractCommand.ExtractCore(
                OutputMode.Kind.Json,
                packagesOverride: _packages,
                databaseOverride: _db,
                onlyModel: null,
                sinceIso: null,
                prune: prune));
            return writer.ToString();
        }
        finally
        {
            Console.SetOut(original);
        }
    }
}
