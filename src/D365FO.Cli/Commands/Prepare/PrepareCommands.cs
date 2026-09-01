using D365FO.Core;
using D365FO.Core.Index;
using Spectre.Console.Cli;

namespace D365FO.Cli.Commands.Prepare;

/// <summary>
/// <c>prepare change</c> — single-round context aggregator for D365FO extension
/// work. The CLI surface of the unified <c>prepare</c> MCP tool
/// (<c>mode=change</c>): one call replaces the search → get → find coc → suggest
/// extension sequence (4–6 agentic rounds) and returns an object-bound grounding
/// token (30-min TTL) proving the model looked at the real codebase before
/// writing code. The aggregation lives in <see cref="D365FO.Mcp.ToolHandlers.PrepareChange"/>.
/// </summary>
public sealed class PrepareChangeCommand : Command<PrepareChangeCommand.Settings>
{
    public sealed class Settings : D365OutputSettings
    {
        [CommandArgument(0, "<OBJECT>")]
        [System.ComponentModel.Description("Name of the D365FO object to extend or modify (class, table, form, …). Example: CustTable, SalesFormLetter.")]
        public string ObjectName { get; init; } = "";

        [CommandOption("--goal <TEXT>")]
        [System.ComponentModel.Description("One-sentence description of the intended change. Example: \"Add CoC on CustTable.validateWrite to enforce a custom rule.\"")]
        public string? Goal { get; init; }

        [CommandOption("--method <NAME>")]
        [System.ComponentModel.Description("Target method name when the change involves a specific method (CoC or event handlers).")]
        public string? Method { get; init; }

        [CommandOption("--type <KIND>")]
        [System.ComponentModel.Description("Object kind: class|table|form|query|view|enum|edt|data-entity|map|report. Auto-detected from the index when omitted.")]
        public string? Type { get; init; }

        [CommandOption("--proposed-name <NAME>")]
        [System.ComponentModel.Description("Proposed name for the new extension class/object — naming validation runs when provided.")]
        public string? ProposedName { get; init; }

        [CommandOption("--prefix <PREFIX>")]
        [System.ComponentModel.Description("Publisher prefix used for naming validation of --proposed-name.")]
        public string? Prefix { get; init; }
    }

    public override int Execute(CommandContext ctx, Settings settings)
    {
        var kind = OutputMode.Resolve(settings.Output);
        if (string.IsNullOrWhiteSpace(settings.ObjectName))
            return RenderHelpers.Render(kind, ToolResult<object>.Fail("BAD_INPUT", "Object name required."));

        MetadataRepository repo;
        try { repo = RepoFactory.Create(); }
        catch (Exception ex)
        {
            return RenderHelpers.Render(kind, ToolResult<object>.Fail("NO_INDEX",
                $"prepare change requires the SQLite index: {ex.Message}",
                "Run `d365fo index build` then `d365fo index extract` first."));
        }

        var result = new D365FO.Mcp.ToolHandlers(repo).PrepareChange(
            settings.ObjectName, settings.Goal, settings.Method, settings.Type,
            settings.ProposedName, settings.Prefix);
        return RenderHelpers.Render(kind, result);
    }
}

/// <summary>
/// <c>prepare create</c> — single-round context aggregator for NEW D365FO
/// objects. The CLI surface of the unified <c>prepare</c> MCP tool
/// (<c>mode=create</c>): one call replaces the search → validate name → suggest
/// edt → search labels sequence and returns collision check, naming validation,
/// similar objects, EDT suggestions, reusable labels, mined property defaults,
/// and a grounding token. The aggregation lives in
/// <see cref="D365FO.Mcp.ToolHandlers.PrepareCreate"/>.
/// </summary>
public sealed class PrepareCreateCommand : Command<PrepareCreateCommand.Settings>
{
    public sealed class Settings : D365OutputSettings
    {
        [CommandArgument(0, "<NAME>")]
        [System.ComponentModel.Description("Proposed BASE name of the new object (the same value you would pass to `d365fo generate`). Example: ImportParameters.")]
        public string Name { get; init; } = "";

        [CommandOption("--type <KIND>")]
        [System.ComponentModel.Description("Object kind: class|table|form|enum|edt|query|view|data-entity|report|menu-item|privilege|duty|role.")]
        public string Type { get; init; } = "class";

        [CommandOption("--goal <TEXT>")]
        [System.ComponentModel.Description("One-sentence description of what the new object is for.")]
        public string? Goal { get; init; }

        [CommandOption("--field <NAME>")]
        [System.ComponentModel.Description("For tables/views: planned field names (repeatable). Each gets EDT suggestions from the index.")]
        public string[]? Fields { get; init; }

        [CommandOption("--prefix <PREFIX>")]
        [System.ComponentModel.Description("Publisher prefix the generated object will carry (e.g. Contoso). Collision check covers both base and prefixed name.")]
        public string? Prefix { get; init; }
    }

    public override int Execute(CommandContext ctx, Settings settings)
    {
        var kind = OutputMode.Resolve(settings.Output);
        if (string.IsNullOrWhiteSpace(settings.Name))
            return RenderHelpers.Render(kind, ToolResult<object>.Fail("BAD_INPUT", "Object name required."));

        MetadataRepository repo;
        try { repo = RepoFactory.Create(); }
        catch (Exception ex)
        {
            return RenderHelpers.Render(kind, ToolResult<object>.Fail("NO_INDEX",
                $"prepare create requires the SQLite index: {ex.Message}",
                "Run `d365fo index build` then `d365fo index extract` first."));
        }

        var result = new D365FO.Mcp.ToolHandlers(repo).PrepareCreate(
            settings.Name, settings.Type, settings.Goal, settings.Fields, settings.Prefix);
        return RenderHelpers.Render(kind, result);
    }
}

/// <summary>
/// <c>prepare test</c> — single-round context aggregator for WRITING A SYSTEST.
/// The CLI surface of the unified <c>prepare</c> MCP tool (<c>mode=test</c>):
/// methods worth covering with their real signatures, test classes that already
/// cover the target, whether the model references TestEssentials, the scaffold
/// call, the red-first order and a grounding token. The aggregation lives in
/// <see cref="D365FO.Mcp.ToolHandlers.PrepareTest"/>.
///
/// Resolves a TABLE as well as a class. The index stores declared members only, so a
/// table that has never overridden <c>validateWrite</c> has no row for it — which is
/// exactly the method a caller naming <c>CustTable.validateWrite</c> came for. The
/// inherited data methods are listed from <see cref="D365FO.Core.Knowledge.TableDataMethods"/>
/// alongside the declared overrides, and the answer carries the shape a table test needs
/// (arrange a buffer, assert the verdict AND the infolog line, keep the accepting case).
/// </summary>
public sealed class PrepareTestCommand : Command<PrepareTestCommand.Settings>
{
    public sealed class Settings : D365OutputSettings
    {
        [CommandArgument(0, "<TARGET>")]
        [System.ComponentModel.Description("The class or table under test. A table method may be named in one go: FmVehicleService, CustTable, or CustTable.validateWrite.")]
        public string ObjectName { get; init; } = "";

        [CommandOption("--goal <TEXT>")]
        [System.ComponentModel.Description("One-sentence description of the behaviour the tests should pin down.")]
        public string? Goal { get; init; }

        [CommandOption("--method <NAME>")]
        [System.ComponentModel.Description("Focus the suggestions on one method of the target.")]
        public string? Method { get; init; }

        [CommandOption("--model <NAME>")]
        [System.ComponentModel.Description("Model that will hold the test — enables the TestEssentials descriptor check (needs D365FO_PACKAGES_PATH).")]
        public string? Model { get; init; }
    }

    public override int Execute(CommandContext ctx, Settings settings)
    {
        var kind = OutputMode.Resolve(settings.Output);
        if (string.IsNullOrWhiteSpace(settings.ObjectName))
            return RenderHelpers.Render(kind, ToolResult<object>.Fail("BAD_INPUT", "Class or table name required."));

        MetadataRepository repo;
        try { repo = RepoFactory.Create(); }
        catch (Exception ex)
        {
            return RenderHelpers.Render(kind, ToolResult<object>.Fail("NO_INDEX",
                $"prepare test requires the SQLite index: {ex.Message}",
                "Run `d365fo index build` then `d365fo index extract` first."));
        }

        var result = new D365FO.Mcp.ToolHandlers(repo).PrepareTest(
            settings.ObjectName, settings.Goal, settings.Method, settings.Model);
        return RenderHelpers.Render(kind, result);
    }
}
