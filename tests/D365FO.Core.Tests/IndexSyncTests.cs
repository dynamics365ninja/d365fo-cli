using D365FO.Core.Index;
using Xunit;

namespace D365FO.Core.Tests;

/// <summary>
/// The single-model re-index: what it accepts, and what it refuses rather than guessing.
/// </summary>
public class IndexSyncTests
{
    /// <summary>
    /// Paths are composed rather than written out: a literal <c>K:\…\x.xml</c> is one path
    /// segment on Linux, so a hard-coded Windows path tests nothing there — and the CI checkout
    /// at <c>/work/&lt;repo&gt;/&lt;repo&gt;</c> is itself a doubled folder, which is how the first
    /// version of this passed on Windows and answered "d365fo-cli" on the runners.
    /// </summary>
    public static TheoryData<string, string?> Paths()
    {
        string P(params string[] parts) => Path.Combine(parts);
        var root = P("packages", "root");

        return new TheoryData<string, string?>
        {
            // The layout repeats the model name, and the file sits under its Ax<Kind> folder.
            { P(root, "ConFleet", "ConFleet", "AxTable", "ConFleetVehicle.xml"), "ConFleet" },
            { P(root, "ConFleet", "ConFleet", "AxClass", "ConFleetPosting.xml"), "ConFleet" },
            // The model folder itself, with and without a trailing separator.
            { P(root, "ConFleet", "ConFleet"), "ConFleet" },
            { P(root, "ConFleet", "ConFleet") + Path.DirectorySeparatorChar, "ConFleet" },

            // No repeated segment: an ordinary directory is not a model.
            { P("work", "repo", "src", "Something.xml"), null },
            { P("AxTable", "ConFleetVehicle.xml"), null },
            // Repeated, but the file is not under an AOT folder — a coincidence, not a layout.
            { P("work", "d365fo-cli", "d365fo-cli", "README.md"), null },
            // Repeated at the wrong depth: the model would be two levels up, and is not.
            { P(root, "ConFleet", "ConFleet", "AxTable", "nested", "ConFleetVehicle.xml"), null },
        };
    }

    [Theory]
    [MemberData(nameof(Paths))]
    public void The_model_is_read_off_the_repeated_folder_name(string path, string? expected)
        => Assert.Equal(expected, IndexSync.ModelFromPath(path));

    [Fact]
    public void A_bare_name_is_the_model_even_when_a_folder_of_that_name_is_in_the_current_folder()
    {
        // The repository keeps the model in a folder of the same name, and the caller runs
        // `index sync <Model>` from the repository root, as doctor tells them to. The folder goes
        // in the current folder as it is: changing it would race the tests that run in parallel.
        var model = "ConFleetSync" + Guid.NewGuid().ToString("N");
        var folder = Directory.CreateDirectory(model);
        try
        {
            Assert.Equal((model, (string?)null), IndexSync.SplitTarget(model));
        }
        finally
        {
            folder.Delete();
        }
    }

    [Theory]
    [InlineData("ConFleet", "ConFleet", null)]
    [InlineData("  ConFleet  ", "ConFleet", null)]
    [InlineData("work/ConFleet/ConFleet/AxClass/ConFleetPosting.xml", null, "work/ConFleet/ConFleet/AxClass/ConFleetPosting.xml")]
    [InlineData("", null, null)]
    [InlineData(null, null, null)]
    public void A_target_is_a_path_only_when_it_has_a_separator_or_is_a_file(
        string? target, string? model, string? path)
        => Assert.Equal((model, path), IndexSync.SplitTarget(target, _ => false));

    [Fact]
    public void A_bare_file_name_from_inside_an_aot_folder_stays_a_path()
        => Assert.Equal(((string?)null, "ConFleetPosting.xml"),
            IndexSync.SplitTarget("ConFleetPosting.xml", f => f == "ConFleetPosting.xml"));

    [Fact]
    public void A_target_with_the_platform_separator_is_a_path()
    {
        var target = Path.Combine("ConFleet", "ConFleet");
        Assert.Equal(((string?)null, target), IndexSync.SplitTarget(target, _ => false));
    }

    [Fact]
    public void Naming_neither_a_model_nor_a_path_is_refused_rather_than_re_indexing_everything()
    {
        var result = IndexSync.Sync(model: null, path: null);

        Assert.False(result.Ok);
        Assert.Equal(D365FoErrorCodes.BadInput, result.Error!.Code);
        // The refusal has to say where the full re-index lives, or the caller's next guess is to
        // pass something arbitrary and wait.
        Assert.Contains("index refresh", result.Error.Hint, StringComparison.Ordinal);
    }

    [Fact]
    public void A_path_that_names_no_model_is_refused_with_the_layout_it_expected()
    {
        var result = IndexSync.Sync(model: null, path: Path.Combine("work", "repo", "src", "Something.xml"));

        Assert.False(result.Ok);
        Assert.Equal(D365FoErrorCodes.BadInput, result.Error!.Code);
        Assert.Contains("<Model>", result.Error.Hint, StringComparison.Ordinal);
    }

    [Fact]
    public void A_model_no_packages_root_holds_is_named_in_the_refusal()
    {
        var result = IndexSync.Sync(
            model: "ConFleetNoSuchModel",
            path: null,
            packagesOverride: Path.Combine(Path.GetTempPath(), "d365fo-no-such-root-" + Guid.NewGuid().ToString("N")));

        Assert.False(result.Ok);
        Assert.Equal("MODEL_NOT_FOUND", result.Error!.Code);
        Assert.Contains("ConFleetNoSuchModel", result.Error.Message, StringComparison.Ordinal);
    }
}
