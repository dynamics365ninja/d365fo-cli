using D365FO.Core.Index;
using Xunit;

namespace D365FO.Core.Tests;

/// <summary>
/// The single-model re-index: what it accepts, and what it refuses rather than guessing.
/// </summary>
public class IndexSyncTests
{
    [Theory]
    // The packages layout repeats the model name: <root>\<Model>\<Model>\Ax<Kind>\<Name>.xml
    [InlineData(@"K:\AosService\PackagesLocalDirectory\ConFleet\ConFleet\AxTable\ConFleetVehicle.xml", "ConFleet")]
    [InlineData(@"K:\AosService\PackagesLocalDirectory\ConFleet\ConFleet\AxClass\ConFleetPosting.xml", "ConFleet")]
    // The model folder itself, not a file in it.
    [InlineData(@"K:\AosService\PackagesLocalDirectory\ConFleet\ConFleet", "ConFleet")]
    public void The_model_is_read_off_the_repeated_folder_name(string path, string expected)
        => Assert.Equal(expected, IndexSync.ModelFromPath(path));

    [Theory]
    // No repeated segment: an ordinary directory is not a model just because a file sits in it.
    [InlineData(@"C:\work\repo\src\Something.xml")]
    [InlineData(@"C:\AxTable\ConFleetVehicle.xml")]
    public void A_path_outside_the_packages_layout_names_no_model(string path)
        => Assert.Null(IndexSync.ModelFromPath(path));

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
        var result = IndexSync.Sync(model: null, path: @"C:\work\repo\src\Something.xml");

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
