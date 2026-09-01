using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using D365FO.Cli.Commands.Generate;
using D365FO.Core.Scaffolding;
using Xunit;

namespace D365FO.Cli.Tests;

/// <summary>
/// Issue #180: no <c>generate</c> subcommand may accept <c>--verify</c> and quietly do nothing
/// with it.
/// </summary>
/// <remarks>
/// <para>
/// <c>--verify</c> is declared on the shared <c>GenerateSettings</c>, so all thirty subcommands
/// advertise it in <c>--help</c>. The verification itself used to live inside
/// <c>GenerateInstaller.EmitCore</c>, which only six of them reached — the other twenty-four
/// printed the flag, ignored it, and produced output indistinguishable from a run that really
/// had read the artefact back. That is worse than a normally ignored flag: the whole point of
/// <c>--verify</c> is to catch a document the metadata reader will refuse.
/// </para>
/// <para>
/// The fix moved verification to <c>GenerateInstaller.Done</c>, the single success exit every
/// generate command now renders through, and had the writer record what it emitted so a
/// multi-file command verifies each artefact rather than one nominated file. This suite is the
/// structural guard on that, in the shape of <see cref="GenerateGateSurfaceTests"/>: it reads
/// the command sources and fails when one renders its own success envelope, which is the only
/// way left to write around the verification.
/// </para>
/// </remarks>
public class GenerateVerifySurfaceTests
{
    private static string GenerateCommandsDirectory()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "src", "D365FO.Cli")))
            dir = dir.Parent;

        Assert.NotNull(dir);
        return Path.Combine(dir!.FullName, "src", "D365FO.Cli", "Commands", "Generate");
    }

    /// <summary>Source files holding the generate subcommands, minus the shared installer itself.</summary>
    private static IEnumerable<(string Name, string Source)> CommandSources()
        => Directory.EnumerateFiles(GenerateCommandsDirectory(), "*.cs")
            .Where(f => Path.GetFileName(f) != "GenerateCommands.cs")
            .Select(f => (Path.GetFileName(f), File.ReadAllText(f)));

    [Fact]
    public void No_generate_command_renders_its_own_success_envelope()
    {
        var offenders = CommandSources()
            .Where(f => Regex.IsMatch(f.Source, @"ToolResult<object>\s*\.\s*Success\s*\("))
            .Select(f => f.Name)
            .ToArray();

        Assert.Empty(offenders);
    }

    [Fact]
    public void The_shared_installer_is_the_only_place_that_builds_a_generate_success_envelope()
    {
        // GenerateCommands.cs holds GenerateInstaller and the table/class/CoC/form commands.
        // Exactly one Success call may live there — the one inside the shared terminal that
        // runs the verification first.
        var installer = File.ReadAllText(Path.Combine(GenerateCommandsDirectory(), "GenerateCommands.cs"));
        var calls = Regex.Matches(installer, @"ToolResult<object>\s*\.\s*Success\s*\(").Count;

        Assert.Equal(1, calls);
    }

    [Fact]
    public void Every_generate_command_source_that_writes_also_finishes_through_the_shared_terminal()
    {
        var offenders = new List<string>();

        foreach (var (name, source) in CommandSources())
        {
            var writes = Regex.IsMatch(source, @"GenerateInstaller\s*\.\s*(Write|Emit|EmitString)\s*\(")
                         || source.Contains("AtomicSave(", StringComparison.Ordinal);
            if (!writes) continue;

            // Emit/EmitString end in the shared terminal themselves; a command that reaches the
            // writer directly has to render through Done.
            var finishes = Regex.IsMatch(source, @"GenerateInstaller\s*\.\s*(Done|Emit|EmitString)\s*\(");
            if (!finishes) offenders.Add(name);
        }

        Assert.Empty(offenders);
    }

    [Fact]
    public void The_writer_names_every_artefact_the_way_the_metadata_provider_would()
    {
        // What --verify looks an artefact up by is derived from the document rather than
        // declared per command — a per-command table is exactly what goes stale when someone
        // adds a second output file.
        Assert.Equal(
            ("table", "ConVehicle"),
            GenerateInstaller.IdentityOf(XElement.Parse("<AxTable><Name>ConVehicle</Name></AxTable>")));

        // Namespaced contracts (menu items are V1, workflows and reports V2, forms V6) still
        // resolve: the root's local name is the registry key.
        Assert.Equal(
            ("menuitemaction", "ConVehicleServiceMenuItem"),
            GenerateInstaller.IdentityOf(XElement.Parse(
                "<AxMenuItemAction xmlns=\"Microsoft.Dynamics.AX.Metadata.V1\">" +
                "<Name>ConVehicleServiceMenuItem</Name></AxMenuItemAction>")));

        // Abstract roots keep their folder name and carry the concrete subtype in i:type,
        // so the kind is still the base one the bridge reads through.
        Assert.Equal(
            ("edt", "ConPlateNumber"),
            GenerateInstaller.IdentityOf(XElement.Parse("<AxEdt><Name>ConPlateNumber</Name></AxEdt>")));

        // Nothing to look up is reported as nothing, not guessed at.
        Assert.Equal((null, null), GenerateInstaller.IdentityOf(null));
        Assert.Equal(((string?)null, (string?)"ConVehicle"),
            GenerateInstaller.IdentityOf(XElement.Parse("<NotAnAotRoot><Name>ConVehicle</Name></NotAnAotRoot>")));
    }
}

/// <summary>
/// Which bridge answers count as "the metadata reader refuses this artefact" — the only ones
/// allowed to fail a generate command.
/// </summary>
/// <remarks>
/// Every read failure used to collapse to "unreadable", which was harmless while <c>--verify</c>
/// reached six subcommands and never met most kinds. Extending it to all thirty put real
/// envelopes through that mapping, and two of them are not verdicts about the file at all: the
/// bridge's own <c>XmlSerializer</c> cannot reflect <c>AxMenuItemAction</c> or
/// <c>AxSecurityPrivilege</c>, so a correctly written menu item came back as
/// <c>SERIALIZE_FAILED</c> — after <c>IMetadataProvider</c> had already handed the object over.
/// Failing a good write on that is worse than not checking. The envelopes below are the ones a
/// live AOS actually returned.
/// </remarks>
public class BridgeVerifyVerdictTests
{
    private static System.Text.Json.Nodes.JsonObject Envelope(string json)
        => (System.Text.Json.Nodes.JsonNode.Parse(json) as System.Text.Json.Nodes.JsonObject)!;

    [Fact]
    public void A_provider_that_returned_the_object_verifies()
    {
        var (outcome, detail) = D365FO.Cli.Commands.Get.BridgeGate.VerdictFrom(
            "query", Envelope("""{"ok":true,"kind":"query","name":"ConVerifyQuery","xml":"<AxQuerySimple />"}"""));

        Assert.Equal(D365FO.Cli.Commands.Get.BridgeGate.VerifyOutcome.Readable, outcome);
        Assert.Null(detail);
    }

    [Fact]
    public void An_object_the_provider_would_not_return_is_the_failure_the_flag_exists_for()
    {
        // What a document the reader refuses to deserialize looks like from here: the provider
        // simply does not hand it back.
        var (outcome, detail) = D365FO.Cli.Commands.Get.BridgeGate.VerdictFrom(
            "query", Envelope("""{"ok":false,"error":"NOT_FOUND","message":"query 'ConVerifyQuery' was not returned by IMetadataProvider."}"""));

        Assert.Equal(D365FO.Cli.Commands.Get.BridgeGate.VerifyOutcome.Unreadable, outcome);
        Assert.Contains("could not load the object back", detail);
    }

    [Fact]
    public void A_type_the_bridge_cannot_serialise_still_counts_as_loaded()
    {
        var (outcome, detail) = D365FO.Cli.Commands.Get.BridgeGate.VerdictFrom(
            "menuitemaction", Envelope("""{"ok":false,"error":"SERIALIZE_FAILED","message":"InvalidOperationException: There was an error reflecting type 'Microsoft.Dynamics.AX.Metadata.MetaModel.AxMenuItemAction'."}"""));

        Assert.Equal(D365FO.Cli.Commands.Get.BridgeGate.VerifyOutcome.Readable, outcome);
        Assert.Contains("could not render it back as XML", detail);
    }

    [Theory]
    // A kind the bridge has no read channel for, a runtime that is not there, and a bridge that
    // never answered are all "nothing was checked" — none of them is evidence against the write.
    [InlineData("""{"ok":false,"error":"INVALID_KIND","message":"kind must be one of: …"}""")]
    [InlineData("""{"ok":false,"error":"METADATA_UNAVAILABLE","message":"IMetadataProvider failed to initialise."}""")]
    public void Answers_that_check_nothing_are_skips_not_verdicts(string json)
    {
        var (outcome, detail) = D365FO.Cli.Commands.Get.BridgeGate.VerdictFrom("tile", Envelope(json));

        Assert.Equal(D365FO.Cli.Commands.Get.BridgeGate.VerifyOutcome.Skipped, outcome);
        Assert.False(string.IsNullOrWhiteSpace(detail));
    }

    [Fact]
    public void A_bridge_that_never_answered_is_a_skip()
    {
        var (outcome, detail) = D365FO.Cli.Commands.Get.BridgeGate.VerdictFrom("class", null);

        Assert.Equal(D365FO.Cli.Commands.Get.BridgeGate.VerifyOutcome.Skipped, outcome);
        Assert.Contains("did not answer", detail);
    }
}

/// <summary>
/// The behavioural half of issue #180: the flag now reports what it did, on a subcommand from
/// the group that used to ignore it.
/// </summary>
// Captures Console.Out, which is process-global — serialised against the other
// console-capturing classes rather than left to xUnit's per-class parallelism.
[Collection("EnvIndexDb")]
public class GenerateVerifyPayloadTests
{
    private static (int Exit, JsonDocument Json) Run(Func<int> execute)
    {
        var saved = Console.Out;
        var writer = new StringWriter();
        try
        {
            Console.SetOut(writer);
            var exit = execute();
            return (exit, JsonDocument.Parse(writer.ToString()));
        }
        finally
        {
            Console.SetOut(saved);
        }
    }

    private static string NewDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "d365fo-verify-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static (int Exit, JsonDocument Json) RunQuery(string dir, bool verify)
        => Run(() => new GenerateQueryCommand().Execute(null!, new GenerateQueryCommand.Settings
        {
            Name = "ConVehicleQuery",
            DataSources = ["FmVehicle"],
            Out = Path.Combine(dir, "ConVehicleQuery.xml"),
            Output = "json",
            Verify = verify,
        }));

    [Fact]
    public void The_writer_records_every_file_a_multi_file_command_emits()
    {
        // The commands most likely to produce a shape the metadata reader refuses are the
        // multi-file emitters — business events, custom services, reports, workflows, number
        // sequences. Verifying one nominated artefact per invocation would leave the rest
        // unread, so the ledger --verify walks is built by the writer, once per file.
        var dir = NewDir();
        try
        {
            var gate = GroundingGate.Check(token: null, targetObject: "ConVehicleNoteEvent", doc: null);
            Assert.Null(gate.Failure);

            GenerateInstaller.Write(
                gate,
                D365FO.Core.Scaffolding.BusinessEventScaffolder.EventClass(
                    "ConVehicleNoteEvent", "ConVehicleNoteEventContract", "FleetManagement", primaryTable: null),
                Path.Combine(dir, "ConVehicleNoteEvent.xml"));

            GenerateInstaller.Write(
                gate,
                D365FO.Core.Scaffolding.BusinessEventScaffolder.ContractClass(
                    "ConVehicleNoteEventContract", payload: null),
                Path.Combine(dir, "ConVehicleNoteEventContract.xml"));

            Assert.Equal(
                ["ConVehicleNoteEvent", "ConVehicleNoteEventContract"],
                gate.Artefacts.Select(a => a.Name).ToArray());
            Assert.All(gate.Artefacts, a => Assert.Equal("class", a.AxKind));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void A_run_that_did_not_ask_to_verify_says_so_rather_than_looking_verified()
    {
        var dir = NewDir();
        try
        {
            var (exit, json) = RunQuery(dir, verify: false);

            Assert.Equal(0, exit);
            var data = json.RootElement.GetProperty("data");
            Assert.Equal("not-requested", data.GetProperty("verify").GetProperty("status").GetString());
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void Verify_against_an_out_path_reports_a_skip_with_its_reason()
    {
        // `generate query` is one of the twenty-four subcommands where --verify was inert: it
        // writes through GenerateInstaller.Write and never went near EmitCore. The provider
        // resolves objects by name inside the packages paths, so --out cannot be verified —
        // the point is that this is now said out loud instead of passing for a clean verify.
        var dir = NewDir();
        try
        {
            var (exit, json) = RunQuery(dir, verify: true);

            Assert.Equal(0, exit);
            var verify = json.RootElement.GetProperty("data").GetProperty("verify");
            Assert.Equal("skipped", verify.GetProperty("status").GetString());
            Assert.Contains("--install-to", verify.GetProperty("detail").GetString());

            var warnings = json.RootElement.GetProperty("warnings").EnumerateArray()
                .Select(w => w.GetString() ?? "").ToArray();
            Assert.Contains(warnings, w => w.StartsWith("--verify skipped:", StringComparison.Ordinal));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }
}
