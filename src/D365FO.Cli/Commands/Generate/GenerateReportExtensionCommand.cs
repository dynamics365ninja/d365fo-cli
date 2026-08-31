using D365FO.Core;
using D365FO.Core.Scaffolding;
using Spectre.Console.Cli;

using static D365FO.Core.ObjectTypes.ObjectTypeRegistry;

namespace D365FO.Cli.Commands.Generate;

/// <summary>
/// Scaffolds one of the three compiler-checked techniques for extending an SSRS report that
/// already ships (see <see cref="ReportExtensionScaffolder"/>): <c>dataset</c> (add data to
/// the shipped design's temp table), <c>custom-design</c> (a controller for your copy of the
/// report + the print-management delegate that resolves the document type to it), and
/// <c>menu-redirect</c> (a post-handler on the standard controller's static construct()).
/// </summary>
public sealed class GenerateReportExtensionCommand : Command<GenerateReportExtensionCommand.Settings>
{
    public sealed class Settings : GenerateSettings
    {
        [CommandArgument(0, "<PATTERN>")]
        [System.ComponentModel.Description("dataset | custom-design | menu-redirect.")]
        public string Pattern { get; init; } = "";

        [CommandOption("--dp <CLASS>")]
        [System.ComponentModel.Description("dataset: the standard report's data-provider class (e.g. AssetBarCodeDP).")]
        public string? Dp { get; init; }

        [CommandOption("--tmp-table <TABLE>")]
        [System.ComponentModel.Description("dataset: the DP's temp table your table extension added fields to (e.g. AssetBarCodeTmp).")]
        public string? TmpTable { get; init; }

        [CommandOption("--dataset-accessor <METHOD>")]
        [System.ComponentModel.Description("dataset: the DP's dataset accessor method — READ IT OFF THE DP, never derive it from the temp-table name (the platform ships the typo geAssetBarCodeTmp). With it you get the bulk PostHandlerFor shape; without it the per-row DataEventHandler, which needs no accessor.")]
        public string? DatasetAccessor { get; init; }

        [CommandOption("--report <NAME>")]
        [System.ComponentModel.Description("custom-design / menu-redirect: YOUR copy of the report (duplicate the standard report into your model first).")]
        public string? Report { get; init; }

        [CommandOption("--design <NAME>")]
        [System.ComponentModel.Description("custom-design / menu-redirect: the design name inside that report — read it off the AxReport; ssrsReportStr is compile-time checked against it.")]
        public string? Design { get; init; }

        [CommandOption("--document-type <VALUE>")]
        [System.ComponentModel.Description("custom-design: the PrintMgmtDocumentType enum value to resolve to your design. Omitted = no print-management handler is generated.")]
        public string? DocumentType { get; init; }

        [CommandOption("--base-controller <CLASS>")]
        [System.ComponentModel.Description("custom-design: controller base class (default SrsReportRunController; use the standard report's own controller to inherit its contract seeding).")]
        public string? BaseController { get; init; }

        [CommandOption("--controller <CLASS>")]
        [System.ComponentModel.Description("menu-redirect: the standard controller to repoint. Requires a STATIC construct() on it — the intrinsic fails the build otherwise.")]
        public string? Controller { get; init; }

        [CommandOption("--suffix <SUFFIX>")]
        [System.ComponentModel.Description("Class-name suffix for handler classes (default: --install-to model name, else 'Ext').")]
        public string? Suffix { get; init; }

        [CommandOption("--out-second <PATH>")]
        [System.ComponentModel.Description("custom-design: output path for the print-management handler class (defaults to a sibling of --out / the model folder).")]
        public string? OutSecond { get; init; }
    }

    public override int Execute(CommandContext ctx, Settings settings)
    {
        var kind = OutputMode.Resolve(settings.Output);

        var hasInstall = !string.IsNullOrWhiteSpace(settings.InstallTo);
        var hasOut = !string.IsNullOrWhiteSpace(settings.Out);
        if (!hasInstall && !hasOut)
            return RenderHelpers.Render(kind, ToolResult<object>.Fail(D365FoErrorCodes.BadInput, "--out or --install-to is required."));

        var suffix = string.IsNullOrWhiteSpace(settings.Suffix)
            ? (hasInstall ? settings.InstallTo! : "Ext")
            : settings.Suffix!;

        return settings.Pattern.Trim().ToLowerInvariant() switch
        {
            "dataset" => Dataset(kind, settings, suffix, hasInstall, hasOut),
            "custom-design" or "customdesign" => CustomDesign(kind, settings, hasInstall, hasOut),
            "menu-redirect" or "menuredirect" => MenuRedirect(kind, settings, suffix, hasInstall, hasOut),
            _ => RenderHelpers.Render(kind, ToolResult<object>.Fail(D365FoErrorCodes.BadInput,
                $"Unknown pattern '{settings.Pattern}'. Expected dataset | custom-design | menu-redirect.")),
        };
    }

    private static int Dataset(OutputMode.Kind kind, Settings settings, string suffix, bool hasInstall, bool hasOut)
    {
        if (string.IsNullOrWhiteSpace(settings.Dp) || string.IsNullOrWhiteSpace(settings.TmpTable))
            return RenderHelpers.Render(kind, ToolResult<object>.Fail(D365FoErrorCodes.BadInput,
                "dataset needs --dp <DPClass> and --tmp-table <TmpTable>."));

        var className = $"{settings.Dp}{suffix}_EventHandler";
        var doc = ReportExtensionScaffolder.DatasetExtension(settings.Dp!, settings.TmpTable!, className, settings.DatasetAccessor);

        // The accessor is the claim worth proving: a guessed one produces a scaffold that
        // looks right and does not compile.
        var gate = GenerateInstaller.Gate(settings, className, doc,
            requiredMethods: string.IsNullOrWhiteSpace(settings.DatasetAccessor)
                ? null
                : new[] { (settings.Dp!, settings.DatasetAccessor!) },
            requiredSymbols: new[] { settings.Dp!, settings.TmpTable! });
        if (gate.Failure is not null) return RenderHelpers.Render(kind, gate.Failure);

        return GenerateInstaller.Emit(
            kind, gate, settings, "class", Folders.Class, className, doc,
            r => new
            {
                kind = "AxClass",
                pattern = "dataset",
                name = className,
                shape = string.IsNullOrWhiteSpace(settings.DatasetAccessor)
                    ? "per-row [DataEventHandler] (no accessor needed)"
                    : "bulk [PostHandlerFor] over the finished temp table (linkPhysicalTableInstance)",
                dp = settings.Dp,
                tmpTable = settings.TmpTable,
                source = r.Source,
                path = r.Path,
                bytes = r.Bytes,
                backup = r.Backup,
                model = settings.InstallTo,
                grounding = gate.Grounding,
            },
            gate.Warnings);
    }

    private static int CustomDesign(OutputMode.Kind kind, Settings settings, bool hasInstall, bool hasOut)
    {
        if (string.IsNullOrWhiteSpace(settings.Report) || string.IsNullOrWhiteSpace(settings.Design))
            return RenderHelpers.Render(kind, ToolResult<object>.Fail(D365FoErrorCodes.BadInput,
                "custom-design needs --report <YourReportCopy> and --design <DesignName> " +
                "(read the design name off the AxReport — ssrsReportStr is compile-time checked against it)."));

        var baseController = string.IsNullOrWhiteSpace(settings.BaseController)
            ? "SrsReportRunController"
            : settings.BaseController!;
        var controllerName = $"{settings.Report}Controller";
        var handlerName = $"{settings.Report}PrintMgmtHandler";

        var controllerDoc = ReportExtensionScaffolder.CustomDesignController(settings.Report!, settings.Design!, baseController);

        // The report copy must already exist — the controller's ssrsReportStr names it.
        var gate = GenerateInstaller.Gate(settings, controllerName, controllerDoc,
            requiredSymbols: new[] { settings.Report! });
        if (gate.Failure is not null) return RenderHelpers.Render(kind, gate.Failure);

        string? controllerPath = settings.Out;
        if (hasInstall && !hasOut)
        {
            controllerPath = GenerateInstaller.ResolveInstallPath(kind, Folders.Class, controllerName, settings.InstallTo!, out var f1);
            if (f1.HasValue) return f1.Value;
        }

        try
        {
            var controllerRes = GenerateInstaller.Write(gate, controllerDoc, controllerPath!, settings.Overwrite);

            ScaffoldFileWriter.WriteResult? handlerRes = null;
            if (!string.IsNullOrWhiteSpace(settings.DocumentType))
            {
                var handlerDoc = ReportExtensionScaffolder.CustomDesignPrintMgmtHandler(settings.Report!, settings.Design!, settings.DocumentType!);
                var handlerPath = settings.OutSecond;
                if (string.IsNullOrWhiteSpace(handlerPath))
                {
                    handlerPath = hasInstall && !hasOut
                        ? GenerateInstaller.ResolveInstallPath(kind, Folders.Class, handlerName, settings.InstallTo!, out _)
                        : System.IO.Path.Combine(System.IO.Path.GetDirectoryName(controllerRes.Path)!, handlerName + ".xml");
                }
                handlerRes = GenerateInstaller.Write(gate, handlerDoc, handlerPath!, settings.Overwrite);
            }

            return GenerateInstaller.Done(kind, gate, settings, new
            {
                kind = "AxClass",
                pattern = "custom-design",
                report = settings.Report,
                design = settings.Design,
                controller = new { name = controllerName, path = controllerRes.Path, bytes = controllerRes.Bytes, backup = controllerRes.BackupPath },
                printMgmtHandler = handlerRes is null ? null : new { name = handlerName, path = handlerRes.Path, bytes = handlerRes.Bytes, backup = handlerRes.BackupPath },
                nextStep = "Extend the standard OUTPUT menu item and point its Object at " + controllerName +
                           " — without that the menu item still starts the standard controller and these classes never run.",
                model = settings.InstallTo,
                grounding = gate.Grounding,
            });
        }
        catch (Exception ex)
        {
            return RenderHelpers.Render(kind, ToolResult<object>.Fail(D365FoErrorCodes.WriteFailed, ex.Message));
        }
    }

    private static int MenuRedirect(OutputMode.Kind kind, Settings settings, string suffix, bool hasInstall, bool hasOut)
    {
        if (string.IsNullOrWhiteSpace(settings.Controller) || string.IsNullOrWhiteSpace(settings.Report) || string.IsNullOrWhiteSpace(settings.Design))
            return RenderHelpers.Render(kind, ToolResult<object>.Fail(D365FoErrorCodes.BadInput,
                "menu-redirect needs --controller <StandardController>, --report <YourReportCopy> and --design <DesignName>."));

        var className = $"{settings.Controller}{suffix}_EventHandler";
        var doc = ReportExtensionScaffolder.MenuRedirect(settings.Controller!, settings.Report!, settings.Design!, className);

        // construct() must exist AND be static — the light-touch redirect only works when
        // every route into the report goes through it (many controllers have no static
        // construct at all; the intrinsic then fails the build).
        var gate = GenerateInstaller.Gate(settings, className, doc,
            requiredMethods: new[] { (settings.Controller!, "construct") },
            requiredSymbols: new[] { settings.Controller!, settings.Report! });
        if (gate.Failure is not null) return RenderHelpers.Render(kind, gate.Failure);

        return GenerateInstaller.Emit(
            kind, gate, settings, "class", Folders.Class, className, doc,
            r => new
            {
                kind = "AxClass",
                pattern = "menu-redirect",
                name = className,
                controller = settings.Controller,
                report = settings.Report,
                design = settings.Design,
                source = r.Source,
                path = r.Path,
                bytes = r.Bytes,
                backup = r.Backup,
                model = settings.InstallTo,
                grounding = gate.Grounding,
            },
            gate.Warnings);
    }
}
