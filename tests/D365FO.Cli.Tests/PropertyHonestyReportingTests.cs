using D365FO.Cli.Commands.Generate;
using Xunit;

namespace D365FO.Cli.Tests;

/// <summary>
/// R6 (issue #161) end to end: a generate call reports any requested value that did not survive
/// to disk.
/// </summary>
/// <remarks>
/// The Core-level rules live in <c>PropertyHonestyTests</c>; what is checked here is that the
/// wiring is live — that a real command, run normally, actually reconciles its own options
/// against the document it wrote. A check like this is worth nothing if it silently becomes a
/// no-op, and the failure mode of a reflection-driven request list is exactly that.
/// </remarks>
[Collection("EnvIndexDb")]
public sealed class PropertyHonestyReportingTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"d365fo-honesty-{Guid.NewGuid():N}");

    public PropertyHonestyReportingTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private string Out(string name) => Path.Combine(_dir, name + ".xml");

    [Fact]
    public void A_requested_value_the_scaffolder_discards_is_reported()
    {
        // --primary-key names the fields of the alternate-key index, and the scaffolder builds
        // that index from the fields it was actually given. A name that is not one of them is
        // dropped in silence: the command succeeds and the index does not mention it.
        var settings = new GenerateTableCommand.Settings
        {
            Name = "ConHonestyVehicle",
            Fields = ["Plate:Name"],
            PrimaryKey = ["NotAFieldOfThisTable"],
            Out = Out("ConHonestyVehicle"),
            Overwrite = true,
            Output = "json",
        };

        var exit = new GenerateTableCommand().Execute(null!, settings);
        Assert.Equal(0, exit);

        var written = File.ReadAllText(settings.Out!);
        Assert.DoesNotContain("NotAFieldOfThisTable", written, StringComparison.OrdinalIgnoreCase);

        // The same reconciliation the command runs, over the document it produced.
        var gaps = D365FO.Core.Scaffolding.PropertyHonesty.Reconcile(
            GenerateInstaller.RequestedValues(settings), written);

        var gap = Assert.Single(gaps);
        Assert.Equal("--primary-key", gap.Option);
        Assert.Equal("NotAFieldOfThisTable", gap.Missing);
    }

    [Fact]
    public void A_call_whose_options_all_survive_reports_nothing()
    {
        var settings = new GenerateTableCommand.Settings
        {
            Name = "ConHonestyClean",
            Fields = ["Plate:Name:mandatory", "Colour:ConColour"],
            Pattern = "main",
            ConfigurationKey = "ConFleetKey",
            FormRef = "ConHonestyCleanMenuItem",
            Out = Out("ConHonestyClean"),
            Overwrite = true,
            Output = "json",
        };

        var exit = new GenerateTableCommand().Execute(null!, settings);
        Assert.Equal(0, exit);

        var gaps = D365FO.Core.Scaffolding.PropertyHonesty.Reconcile(
            GenerateInstaller.RequestedValues(settings), File.ReadAllText(settings.Out!));

        Assert.Empty(gaps);
    }

    [Fact]
    public void The_reconciliation_reads_the_command_s_real_options()
    {
        // The list is derived by reflection, so the thing that can rot is the derivation
        // itself: an empty list makes every honesty check pass for the wrong reason.
        var settings = new GenerateEnumCommand.Settings
        {
            Name = "ConHonestyStatus",
            Values = ["None:0", "Active:1"],
            Label = "@Con:StatusLabel",
            Out = Out("ConHonestyStatus"),
            Overwrite = true,
            Output = "json",
        };

        var requested = GenerateInstaller.RequestedValues(settings);

        Assert.Contains(requested, r => r is { Option: "<NAME>", Value: "ConHonestyStatus" });
        Assert.Contains(requested, r => r is { Option: "--label", Value: "@Con:StatusLabel" });
        Assert.Equal(2, requested.Count(r => r.Option == "--value"));
    }
}
