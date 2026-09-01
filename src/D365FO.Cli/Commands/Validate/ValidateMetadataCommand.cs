using D365FO.Cli.Commands.Get;
using D365FO.Core;
using Spectre.Console.Cli;
using D365FO.Core.Bridge;

namespace D365FO.Cli.Commands.Validate;

/// <summary>
/// Round-trips AOT XML through Microsoft's own <c>IMetadataProvider</c> serializer and
/// reports what it could not keep — the only check that answers "is this file actually
/// valid metadata?" rather than "does it look right to us".
/// </summary>
/// <remarks>
/// <para>
/// The offline validators (<c>validate xpp</c>, <c>validate references</c>,
/// <c>validate form-pattern</c>) reason about the XML we expect. This one hands the file
/// to the metadata assemblies and asks. Two ways to fail: the document does not
/// deserialize at all (abstract root without <c>i:type</c>, a root element no AOT type
/// matches), or it deserializes but the round-trip loses elements — a property the type
/// does not declare is silently ignored by <c>XmlSerializer</c>, so a misspelled or
/// invented property leaves a file that looks correct and is missing data.
/// </para>
/// <para>
/// Nothing is written and no model is touched, so this is safe against a live
/// installation. Requires <c>D365FO_BRIDGE_ENABLED=1</c> plus the metadata assemblies
/// (<c>D365FO_BIN_PATH</c>) — off a D365FO machine it exits cleanly as skipped, since
/// generation has to keep working offline.
/// </para>
/// Exit codes: 0 = valid (or skipped), 1 = command failure, 2 = invalid.
/// </remarks>
public sealed class ValidateMetadataCommand : Command<ValidateMetadataCommand.Settings>
{
    public sealed class Settings : D365OutputSettings
    {
        [CommandArgument(0, "[PATH]")]
        [System.ComponentModel.Description("AOT XML file, or a directory to validate every *.xml under it. Omit to read one document from stdin.")]
        public string? Path { get; init; }

        [CommandOption("--kind <KIND>")]
        [System.ComponentModel.Description("Object kind hint (table, class, workflowtemplate, …). Only needed when the root element alone cannot resolve the type.")]
        public string? Kind { get; init; }

        [CommandOption("--recursive")]
        [System.ComponentModel.Description("With a directory PATH, descend into subdirectories.")]
        public bool Recursive { get; init; }
    }

    public override int Execute(CommandContext ctx, Settings settings)
    {
        var kind = OutputMode.Resolve(settings.Output);

        var files = ResolveFiles(settings, out var stdinXml, out var inputError);
        if (inputError is not null)
            return RenderHelpers.Render(kind, ToolResult<object>.Fail("INPUT_NOT_FOUND", inputError,
                "Pass a file or directory path, or pipe XML via stdin."));

        if (!BridgeGate.ShouldTry())
        {
            return RenderHelpers.Render(kind, ToolResult<object>.Success(new
            {
                skipped = true,
                reason = "The metadata provider is not enabled here. Set D365FO_BRIDGE_ENABLED=1 (and D365FO_BIN_PATH) " +
                         "on a machine with the D365FO metadata assemblies to validate against Microsoft's own serializer.",
            }));
        }

        var results = new List<object>();
        var invalid = 0;
        var skipped = false;

        foreach (var (path, xml) in EnumerateDocuments(files, stdinXml))
        {
            var verdict = BridgeGate.TryValidateArtifact(settings.Kind, xml);
            if (verdict is null)
            {
                skipped = true;
                break;
            }

            if (!verdict.Valid) invalid++;
            results.Add(new
            {
                path,
                rootElement = verdict.RootElement,
                clrType = verdict.ClrType,
                deserialized = verdict.Deserialized,
                valid = verdict.Valid,
                errorCode = verdict.ErrorCode,
                errorMessage = verdict.ErrorMessage,
                droppedCount = verdict.Dropped.Count,
                dropped = verdict.Dropped,
            });
        }

        if (skipped)
        {
            return RenderHelpers.Render(kind, ToolResult<object>.Success(new
            {
                skipped = true,
                reason = "The bridge did not answer. Check D365FO_BRIDGE_PATH points at D365FO.Bridge.exe and " +
                         "D365FO_BIN_PATH at the folder holding Microsoft.Dynamics.AX.Metadata.dll.",
            }));
        }

        var payload = ToolResult<object>.Success(new
        {
            total = results.Count,
            valid = results.Count - invalid,
            invalid,
            verdict = invalid == 0
                ? "Every document round-trips through the metadata provider without loss."
                : $"{invalid} of {results.Count} document(s) the provider cannot read as written — a dropped element is a property the type does not have.",
            documents = results,
        });

        var exit = RenderHelpers.Render(kind, payload);
        return invalid > 0 ? 2 : exit;
    }

    private static IReadOnlyList<string> ResolveFiles(Settings settings, out string? stdinXml, out string? error)
    {
        stdinXml = null;
        error = null;

        if (string.IsNullOrWhiteSpace(settings.Path))
        {
            var (xml, readError) = ValidateInput.ReadCode(null);
            if (readError is not null) { error = readError; return Array.Empty<string>(); }
            stdinXml = xml;
            return Array.Empty<string>();
        }

        if (File.Exists(settings.Path)) return new[] { settings.Path! };

        if (Directory.Exists(settings.Path))
        {
            var option = settings.Recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
            var found = Directory.GetFiles(settings.Path!, "*.xml", option).OrderBy(f => f, StringComparer.Ordinal).ToArray();
            if (found.Length == 0) error = $"No *.xml files under '{settings.Path}'.";
            return found;
        }

        error = $"Path not found: {settings.Path}";
        return Array.Empty<string>();
    }

    private static IEnumerable<(string Path, string Xml)> EnumerateDocuments(IReadOnlyList<string> files, string? stdinXml)
    {
        if (stdinXml is not null)
        {
            yield return ("<stdin>", stdinXml);
            yield break;
        }

        foreach (var f in files)
        {
            string xml;
            try { xml = File.ReadAllText(f); }
            catch (IOException) { continue; }
            yield return (f, xml);
        }
    }
}
