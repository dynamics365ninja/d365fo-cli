using D365FO.Core;
using D365FO.Core.Index;
using D365FO.Core.Scaffolding;
using Spectre.Console.Cli;

using static D365FO.Core.ObjectTypes.ObjectTypeRegistry;

namespace D365FO.Cli.Commands.Generate;

/// <summary>
/// Scaffolds a minimal, ATL-ready <c>SysTestCase</c> class (issue #107): a
/// <c>[SysTestMethod]</c> Arrange/Act/Assert skeleton, optionally attributed
/// with <c>[SysTestCaseDataDependency]</c> and wired for ATL via
/// <c>AtlDataRootNode</c>. MVP scope — no test-logic generation. The optional
/// <c>--class</c>/<c>--table</c>/<c>--method</c> source is resolved against
/// the index (must exist) but only informs naming/context.
/// </summary>
public sealed class GenerateSysTestCommand : Command<GenerateSysTestCommand.Settings>
{
    public sealed class Settings : GenerateSettings
    {
        [CommandArgument(0, "<NAME>")]
        [System.ComponentModel.Description("Test class name (e.g. CustServiceTest).")]
        public string Name { get; init; } = "";

        [CommandOption("--data-area-id <COMPANY>")]
        [System.ComponentModel.Description("Company emitted as [[SysTestCaseDataDependency('<Company>')]]. Omitted entirely when not set.")]
        public string? DataAreaId { get; init; }

        [CommandOption("--atl")]
        [System.ComponentModel.Description("Emit an AtlDataRootNode field and a setUpTestCase() that constructs it (after calling super()).")]
        public bool Atl { get; init; }

        [CommandOption("--class <CLASS>")]
        [System.ComponentModel.Description("Source class this test targets. Resolved against the index (must exist); used for naming/context only.")]
        public string? Class { get; init; }

        [CommandOption("--table <TABLE>")]
        [System.ComponentModel.Description("Source table this test targets. Resolved against the index (must exist); used for naming/context only.")]
        public string? Table { get; init; }

        [CommandOption("--method <METHOD>")]
        [System.ComponentModel.Description("Method on --class or --table (repeatable — one red-first test method is emitted per value). Resolved against the index (must exist); becomes the generated test method's subject.")]
        public string[]? Method { get; init; }
    }

    public override int Execute(CommandContext ctx, Settings settings)
    {
        var kind = OutputMode.Resolve(settings.Output);

        if (string.IsNullOrWhiteSpace(settings.Name))
            return RenderHelpers.Render(kind, ToolResult<object>.Fail(D365FoErrorCodes.BadInput, "Test class name required."));

        var methods = (settings.Method ?? Array.Empty<string>())
            .Where(m => !string.IsNullOrWhiteSpace(m)).ToArray();

        var hasClass = !string.IsNullOrWhiteSpace(settings.Class);
        var hasTable = !string.IsNullOrWhiteSpace(settings.Table);
        if (hasClass && hasTable)
            return RenderHelpers.Render(kind, ToolResult<object>.Fail(D365FoErrorCodes.BadInput, "Specify only one of --class or --table."));
        if (!hasClass && !hasTable && methods.Length > 0)
            return RenderHelpers.Render(kind, ToolResult<object>.Fail(D365FoErrorCodes.BadInput, "--method requires --class or --table."));

        string? sourceKind = null;
        string? sourceName = null;

        if (hasClass || hasTable)
        {
            var repo = RepoFactory.Create();
            var owner = (hasClass ? settings.Class : settings.Table)!;
            sourceKind = hasClass ? "class" : "table";
            sourceName = owner;

            if (hasClass)
            {
                if (repo.GetClassDetails(owner) is null)
                {
                    var hint = NameSuggester.HintFor(repo, NameSuggester.Kind.Class, owner)
                               ?? "Run 'd365fo index build' after extracting metadata.";
                    return RenderHelpers.Render(kind,
                        ToolResult<object>.Fail(D365FoErrorCodes.ClassNotFound, $"Class '{owner}' not found in index.", hint));
                }
            }
            else
            {
                if (repo.GetTableDetails(owner) is null)
                {
                    var hint = NameSuggester.HintFor(repo, NameSuggester.Kind.Table, owner)
                               ?? "Run 'd365fo index build' after extracting metadata.";
                    return RenderHelpers.Render(kind,
                        ToolResult<object>.Fail(D365FoErrorCodes.TableNotFound, $"Table '{owner}' not found in index.", hint));
                }
            }

            foreach (var method in methods)
            {
                if (repo.FindMethod(owner, method) is null)
                {
                    return RenderHelpers.Render(kind, ToolResult<object>.Fail(D365FoErrorCodes.MethodNotFound,
                        $"Method '{method}' not found on {sourceKind} '{owner}' in index.",
                        $"Use 'd365fo get {sourceKind} {owner}' to see the real method list."));
                }
            }
        }

        var hasInstall = !string.IsNullOrWhiteSpace(settings.InstallTo);
        var hasOut     = !string.IsNullOrWhiteSpace(settings.Out);
        if (!hasInstall && !hasOut)
            return RenderHelpers.Render(kind, ToolResult<object>.Fail(D365FoErrorCodes.BadInput, "--out or --install-to is required."));

        var outPath = settings.Out;
        if (hasInstall && !hasOut)
        {
            outPath = GenerateInstaller.ResolveInstallPath(kind, Folders.Class, settings.Name, settings.InstallTo!, out var fail);
            if (fail.HasValue) return fail.Value;
        }

        try
        {
            var doc = SysTestScaffolder.TestClass(settings.Name, settings.DataAreaId, settings.Atl,
                subjects: methods.Length > 0 ? methods : null,
                targetName: sourceName, targetIsTable: hasTable);
            // Grounding gate (issue #161): uniform across every generate subcommand.
            var gate = GenerateInstaller.Gate(settings, settings.Name, doc);
            if (gate.Failure is not null) return RenderHelpers.Render(kind, gate.Failure);

            var res = GenerateInstaller.Write(gate, doc, outPath!, settings.Overwrite);
            var testMethodNames = (methods.Length > 0 ? methods : new[] { "subject" })
                .Select(m => $"{m}_scenario_expectedResult").ToArray();

            return GenerateInstaller.Done(kind, gate, settings, new
            {
                kind       = "SysTest",
                name       = settings.Name,
                extends    = "SysTestCase",
                dataAreaId = settings.DataAreaId,
                atl        = settings.Atl,
                source     = sourceKind is null ? null : new { kind = sourceKind, name = sourceName, methods },
                testMethods = testMethodNames,
                // Red-first: every emitted method ends in this.fail(...) so the first run
                // is red on purpose — write the assertion, then make it green.
                redFirst   = true,
                path       = res.Path,
                bytes      = res.Bytes,
                backup     = res.BackupPath,
                model      = settings.InstallTo,
            });
        }
        catch (Exception ex)
        {
            return RenderHelpers.Render(kind, ToolResult<object>.Fail(D365FoErrorCodes.WriteFailed, ex.Message));
        }
    }
}
