using D365FO.Core;
using D365FO.Core.Index;
using D365FO.Core.Scaffolding;
using Spectre.Console.Cli;

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
        [System.ComponentModel.Description("Method on --class or --table. Resolved against the index (must exist); becomes the generated test method's subject.")]
        public string? Method { get; init; }
    }

    public override int Execute(CommandContext ctx, Settings settings)
    {
        var kind = OutputMode.Resolve(settings.Output);

        if (string.IsNullOrWhiteSpace(settings.Name))
            return RenderHelpers.Render(kind, ToolResult<object>.Fail(D365FoErrorCodes.BadInput, "Test class name required."));

        var hasClass = !string.IsNullOrWhiteSpace(settings.Class);
        var hasTable = !string.IsNullOrWhiteSpace(settings.Table);
        if (hasClass && hasTable)
            return RenderHelpers.Render(kind, ToolResult<object>.Fail(D365FoErrorCodes.BadInput, "Specify only one of --class or --table."));
        if (!hasClass && !hasTable && !string.IsNullOrWhiteSpace(settings.Method))
            return RenderHelpers.Render(kind, ToolResult<object>.Fail(D365FoErrorCodes.BadInput, "--method requires --class or --table."));

        string? subject = null;
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

            if (!string.IsNullOrWhiteSpace(settings.Method))
            {
                if (repo.FindMethod(owner, settings.Method!) is null)
                {
                    return RenderHelpers.Render(kind, ToolResult<object>.Fail(D365FoErrorCodes.MethodNotFound,
                        $"Method '{settings.Method}' not found on {sourceKind} '{owner}' in index.",
                        $"Use 'd365fo get {sourceKind} {owner}' to see the real method list."));
                }
                subject = settings.Method;
            }
        }

        var hasInstall = !string.IsNullOrWhiteSpace(settings.InstallTo);
        var hasOut     = !string.IsNullOrWhiteSpace(settings.Out);
        if (!hasInstall && !hasOut)
            return RenderHelpers.Render(kind, ToolResult<object>.Fail(D365FoErrorCodes.BadInput, "--out or --install-to is required."));

        var outPath = settings.Out;
        if (hasInstall && !hasOut)
        {
            outPath = GenerateInstaller.ResolveInstallPath(kind, "AxClass", settings.Name, settings.InstallTo!, out var fail);
            if (fail.HasValue) return fail.Value;
        }

        try
        {
            var doc = SysTestScaffolder.TestClass(settings.Name, settings.DataAreaId, settings.Atl, subject);
            var res = ScaffoldFileWriter.Write(doc, outPath!, settings.Overwrite);
            var testMethodName = $"{subject ?? "subject"}_scenario_expectedResult";

            return RenderHelpers.Render(kind, ToolResult<object>.Success(new
            {
                kind       = "SysTest",
                name       = settings.Name,
                extends    = "SysTestCase",
                dataAreaId = settings.DataAreaId,
                atl        = settings.Atl,
                source     = sourceKind is null ? null : new { kind = sourceKind, name = sourceName, method = settings.Method },
                testMethod = testMethodName,
                path       = res.Path,
                bytes      = res.Bytes,
                backup     = res.BackupPath,
                model      = settings.InstallTo,
            }));
        }
        catch (Exception ex)
        {
            return RenderHelpers.Render(kind, ToolResult<object>.Fail(D365FoErrorCodes.WriteFailed, ex.Message));
        }
    }
}
