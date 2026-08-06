using System.Xml.Linq;
using D365FO.Core.Eval;
using D365FO.Core.Validation;
using Xunit;

namespace D365FO.Core.Tests.Eval;

/// <summary>
/// The offline half of the L3 build oracle (plan item 4.2): provisioning goldens
/// into a real model layout, and attributing a compiler's diagnostics back to the
/// case whose golden produced the object. The compile itself needs a Windows host
/// with a D365FO installation and is exercised by <c>d365fo eval verify-build</c>
/// there — nothing here pretends to have run a compiler.
/// </summary>
public class L3BuildOracleTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"d365fo-l3-tests-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    private string Golden(string caseId, string root, string name, string? extra = null)
    {
        var dir = Path.Combine(_dir, "goldens", caseId);
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, name + ".xml");
        new XDocument(new XElement(root, new XElement("Name", name), extra is null ? null : new XElement("Note", extra)))
            .Save(path);
        return path;
    }

    private static EvalCase Case(string id, bool pending = false) => new(
        Id: id, Title: id, Tier: 1, Instruction: "…", CanonicalArgs: null,
        TargetArtifactTypes: [], GoldenPath: id, Tags: [], Ignore: [],
        RequiresFixtureIndex: false, GoldenPending: pending);

    [Fact]
    public void Provision_lays_goldens_out_in_the_folder_the_registry_names()
    {
        Golden("L1-table-basic", "AxTable", "FmVehicle");
        Golden("L1-class-basic", "AxClass", "FmVehicleService");

        var model = L3ModelProvisioner.Provision(
            [Case("L1-table-basic"), Case("L1-class-basic")],
            Path.Combine(_dir, "goldens"), Path.Combine(_dir, "work"), "EvalModel");

        Assert.Equal(2, model.Artifacts.Count);
        Assert.Contains(model.Artifacts, a => a.RelativePath == Path.Combine("AxTable", "FmVehicle.xml"));
        Assert.Contains(model.Artifacts, a => a.RelativePath == Path.Combine("AxClass", "FmVehicleService.xml"));
        Assert.True(File.Exists(Path.Combine(model.ModelRoot, "EvalModel", "AxTable", "FmVehicle.xml")));
    }

    [Fact]
    public void Provision_writes_a_model_descriptor_the_toolchain_can_read()
    {
        Golden("L1-table-basic", "AxTable", "FmVehicle");

        var model = L3ModelProvisioner.Provision(
            [Case("L1-table-basic")], Path.Combine(_dir, "goldens"), Path.Combine(_dir, "work"), "EvalModel");

        var descriptor = XDocument.Load(Path.Combine(model.ModelRoot, "Descriptor", "EvalModel.xml"));
        Assert.Equal("AxModelInfo", descriptor.Root!.Name.LocalName);
        Assert.Equal("EvalModel", descriptor.Root.Element("Name")!.Value);

        // The elements the compiler dies without: ModelModule (its absence throws
        // ArgumentNullException inside ModelKey before anything is compiled) and a
        // numeric Layer, not the layer's name.
        Assert.Equal("EvalModel", descriptor.Root.Element("ModelModule")!.Value);
        Assert.Equal(L3ModelProvisioner.UsrLayer.ToString(), descriptor.Root.Element("Layer")!.Value);

        // Referencing only ApplicationSuite makes the standard Name EDT unresolvable.
        var references = descriptor.Root.Element("ModuleReferences")!.Elements().Select(e => e.Value).ToList();
        Assert.Equal(L3ModelProvisioner.StandardModuleReferences, references);
    }

    [Fact]
    public void Provision_copies_the_fixture_into_the_same_module_as_the_goldens()
    {
        Golden("L1-view-basic", "AxView", "ConFmVehicleView");
        var model = L3ModelProvisioner.Provision(
            [Case("L1-view-basic")], Path.Combine(_dir, "goldens"), Path.Combine(_dir, "work"), "EvalModel");

        var fixtureRoot = Path.Combine(_dir, "fixture");
        Directory.CreateDirectory(Path.Combine(fixtureRoot, "TestModel", "Descriptor"));
        new XDocument(new XElement("AxModelInfo", new XElement("Name", "TestModel"))).Save(
            Path.Combine(fixtureRoot, "TestModel", "Descriptor", "TestModel.xml"));
        Directory.CreateDirectory(Path.Combine(fixtureRoot, "TestModel", "TestModel", "AxTable"));
        new XDocument(new XElement("AxTable", new XElement("Name", "FmVehicle"))).Save(
            Path.Combine(fixtureRoot, "TestModel", "TestModel", "AxTable", "FmVehicle.xml"));

        var added = L3ModelProvisioner.ProvisionFixtureInto(fixtureRoot, Path.Combine(model.ModelRoot, "EvalModel"));

        Assert.Equal(Path.Combine("AxTable", "FmVehicle.xml"), Assert.Single(added));
        Assert.True(File.Exists(Path.Combine(model.ModelRoot, "EvalModel", "AxTable", "FmVehicle.xml")));
    }

    /// <summary>
    /// A quietly dropped case would make "every golden compiles" mean less than it
    /// looks like — the same class of confident lie the triage rubric warns about.
    /// </summary>
    [Fact]
    public void Provision_reports_what_it_skipped_and_why()
    {
        Golden("L1-pending", "AxTable", "FmPending");
        Golden("L1-unknown-root", "AxNotAThing", "FmMystery");

        var model = L3ModelProvisioner.Provision(
            [Case("L1-pending", pending: true), Case("L1-unknown-root"), Case("L1-no-golden")],
            Path.Combine(_dir, "goldens"), Path.Combine(_dir, "work"), "EvalModel");

        Assert.Empty(model.Artifacts);
        Assert.Equal(3, model.Skipped.Count);
        Assert.Contains(model.Skipped, s => s.StartsWith("L1-pending:") && s.Contains("golden_pending"));
        Assert.Contains(model.Skipped, s => s.StartsWith("L1-unknown-root:") && s.Contains("AxNotAThing"));
        Assert.Contains(model.Skipped, s => s.StartsWith("L1-no-golden:"));
    }

    [Fact]
    public void Attribution_blames_the_case_that_provisioned_the_named_object()
    {
        Golden("L1-table-basic", "AxTable", "FmVehicle");
        Golden("L1-class-basic", "AxClass", "FmVehicleService");
        var cases = new[] { Case("L1-table-basic"), Case("L1-class-basic") };
        var model = L3ModelProvisioner.Provision(cases, Path.Combine(_dir, "goldens"), Path.Combine(_dir, "work"), "EvalModel");

        var log = """
            Compile Error: Class Method dynamics://EvalModel/FmVehicleService/run: [(12,5)]: Variable qty has not been declared.
            Best Practice Warning: Table Field dynamics://EvalModel/FmVehicle/VIN: [(3,1)]: Field has no label.
            """;

        var (verdicts, unattributed) = BuildVerdictAttribution.Attribute(model, cases, XppcDiagnostics.Parse(log));

        var cls = Assert.Single(verdicts, v => v.CaseId == "L1-class-basic");
        Assert.Equal(BuildVerdict.Errors, cls.Verdict);
        Assert.Equal(1, cls.Errors);

        var table = Assert.Single(verdicts, v => v.CaseId == "L1-table-basic");
        Assert.Equal(BuildVerdict.Clean, table.Verdict);
        Assert.Equal(1, table.Warnings);

        Assert.Empty(unattributed);
    }

    /// <summary>
    /// A diagnostic naming an object no case provisioned is reported separately —
    /// spreading it across every case would send the improver at innocent code.
    /// </summary>
    [Fact]
    public void A_diagnostic_naming_an_unprovisioned_object_is_not_blamed_on_anyone()
    {
        Golden("L1-table-basic", "AxTable", "FmVehicle");
        var cases = new[] { Case("L1-table-basic") };
        var model = L3ModelProvisioner.Provision(cases, Path.Combine(_dir, "goldens"), Path.Combine(_dir, "work"), "EvalModel");

        var log = "Compile Error: Class Method dynamics://EvalModel/SomeOtherClass/run: [(1,1)]: Something went wrong.";
        var (verdicts, unattributed) = BuildVerdictAttribution.Attribute(model, cases, XppcDiagnostics.Parse(log));

        Assert.Equal(BuildVerdict.Clean, Assert.Single(verdicts).Verdict);
        Assert.Single(unattributed);
    }

    [Fact]
    public void A_case_with_nothing_provisioned_is_skipped_not_clean()
    {
        var cases = new[] { Case("L1-nothing") };
        var model = L3ModelProvisioner.Provision(cases, Path.Combine(_dir, "goldens"), Path.Combine(_dir, "work"), "EvalModel");

        var (verdicts, _) = BuildVerdictAttribution.Attribute(model, cases, []);

        var v = Assert.Single(verdicts);
        Assert.Equal(BuildVerdict.Skipped, v.Verdict);
        Assert.NotNull(v.SkipReason);
    }

    [Fact]
    public void The_verification_file_round_trips_with_its_counts()
    {
        var verdicts = new[]
        {
            new GoldenBuildCaseVerdict("a", BuildVerdict.Clean, 0, 0, [], []),
            new GoldenBuildCaseVerdict("b", BuildVerdict.Errors, 2, 1, ["XPPC-LABEL-MISSING"], ["boom"]),
            new GoldenBuildCaseVerdict("c", BuildVerdict.Skipped, 0, 0, [], [], "no golden"),
        };

        var verification = GoldenBuildVerification.Build(
            "VM01", @"K:\AosService\PackagesLocalDirectory", "xppc.exe", "-metadata=…", "EvalModel",
            DateTimeOffset.Parse("2026-08-06T10:00:00Z"), verdicts);

        var path = Path.Combine(_dir, "golden-build-verification.json");
        verification.Save(path);
        var read = GoldenBuildVerification.Load(path);

        Assert.NotNull(read);
        Assert.Equal(3, read!.Total);
        Assert.Equal(1, read.Clean);
        Assert.Equal(1, read.Failed);
        Assert.Equal(1, read.Skipped);
        Assert.Equal("XPPC-LABEL-MISSING", Assert.Single(read.Cases.Single(c => c.CaseId == "b").RuleIds));
    }

    [Fact]
    public void A_scorecard_that_never_saw_a_compiler_reports_null_not_false()
    {
        var score = new EvalScoreCard(true, 0, true, 0, true, XmlGoldenDiff.Empty);

        Assert.Null(score.BuildClean);
        Assert.Equal(0, score.BuildErrors);
    }
}
