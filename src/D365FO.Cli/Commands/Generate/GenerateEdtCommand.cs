using D365FO.Core;
using D365FO.Core.Scaffolding;
using Spectre.Console.Cli;

using static D365FO.Core.ObjectTypes.ObjectTypeRegistry;

namespace D365FO.Cli.Commands.Generate;

/// <summary>Scaffolds an <c>AxEdt</c> Extended Data Type.</summary>
public sealed class GenerateEdtCommand : Command<GenerateEdtCommand.Settings>
{
    public sealed class Settings : GenerateSettings
    {
        [CommandArgument(0, "<NAME>")]
        [System.ComponentModel.Description("EDT name.")]
        public string Name { get; init; } = "";

        [CommandOption("--extends <BASE>")]
        [System.ComponentModel.Description("Parent EDT to extend (e.g. Name, AccountNum). Takes precedence over --base-type.")]
        public string? Extends { get; init; }

        [CommandOption("--base-type <TYPE>")]
        [System.ComponentModel.Description("Primitive type when no --extends is given. String (default) | Int | Int64 | Real | Date | UtcDateTime | Boolean | Time | Guid | Container | Enum. Infers a sensible standard parent EDT.")]
        public string? BaseType { get; init; }

        [CommandOption("--size <N>")]
        [System.ComponentModel.Description("StringSize override for string-based EDTs.")]
        public int? Size { get; init; }

        [CommandOption("--label <TEXT>")]
        [System.ComponentModel.Description("Label text or @File:Key label reference.")]
        public string? Label { get; init; }

        [CommandOption("--enum-type <XPPENUM>")]
        [System.ComponentModel.Description("Backing X++ enum name for Enum-type EDTs (e.g. NoYes). If omitted, inferred from --extends; otherwise not emitted.")]
        public string? EnumType { get; init; }
    }

    public override int Execute(CommandContext ctx, Settings settings)
    {
        var kind = OutputMode.Resolve(settings.Output);

        if (string.IsNullOrWhiteSpace(settings.Name))
            return RenderHelpers.Render(kind, ToolResult<object>.Fail(D365FoErrorCodes.BadInput, "EDT name required."));

        // With only --extends, the parent's indexed base type decides the concrete AxEdt* type
        // (issue #233) — `--extends AmountCur` is a Real EDT, whatever its name suggests. The
        // name heuristic stays the last resort for a parent the index cannot type, and says so.
        var effectiveBaseType = settings.BaseType;
        var warnings = new List<string>();
        if (string.IsNullOrWhiteSpace(effectiveBaseType) && !string.IsNullOrWhiteSpace(settings.Extends))
        {
            effectiveBaseType = ResolveBaseTypeOfExtends(settings.Extends!);
            if (effectiveBaseType is null)
                warnings.Add(
                    $"{FieldTypeResolver.UnresolvedCode}: the index has no base type for '{settings.Extends}', so the EDT's " +
                    "type was guessed from the name. Pass --base-type, or run `d365fo index refresh` if the index predates schema v21.");
        }

        var effectiveEnumType = settings.EnumType;
        if (string.IsNullOrWhiteSpace(effectiveEnumType) &&
            string.Equals(effectiveBaseType, "Enum", StringComparison.OrdinalIgnoreCase) &&
            !string.IsNullOrWhiteSpace(settings.Extends))
        {
            // EnumType derivation from --extends is not always identity (e.g. AccessToSensitiveData -> NoYes).
            effectiveEnumType = ResolveEnumTypeFromExtendsChain(settings.Extends!);
        }

        var doc = XppScaffolder.Edt(settings.Name, settings.Extends, effectiveBaseType, settings.Size, settings.Label, effectiveEnumType);

        // An EDT that extends an EDT the index has never heard of, or is backed by a
        // non-existent enum, is exactly the hallucination the gate exists to catch.
        var gate = GenerateInstaller.Gate(
            settings, settings.Name, doc,
            requiredSymbols: new[] { settings.Extends, effectiveEnumType }
                .Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s!));
        if (gate.Failure is not null) return RenderHelpers.Render(kind, gate.Failure);

        return GenerateInstaller.Emit(
            kind, gate, settings, "edt", Folders.Edt, settings.Name,
            doc,
            r => new
            {
                kind       = "AxEdt",
                name       = settings.Name,
                extends    = settings.Extends,
                baseType   = effectiveBaseType,
                enumType   = effectiveEnumType,
                stringSize = settings.Size,
                label      = settings.Label,
                source     = r.Source,
                path       = r.Path,
                bytes      = r.Bytes,
                backup     = r.Backup,
                model      = settings.InstallTo,
                grounding  = gate.Grounding,
            },
            [.. warnings, .. gate.Warnings]);
    }

    private static string? ResolveBaseTypeOfExtends(string extendsName)
    {
        try { return RepoFactory.Create().GetEdtBaseType(extendsName); }
        catch { return null; }
    }

    private static string? ResolveEnumTypeFromExtendsChain(string extendsName)
    {
        if (string.IsNullOrWhiteSpace(extendsName))
            return null;

        try
        {
            var repo = RepoFactory.Create();
            var current = extendsName;
            var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // Follow extends links defensively; break on cycles or unreasonable depth.
            for (var depth = 0; depth < 16 && !string.IsNullOrWhiteSpace(current); depth++)
            {
                if (!visited.Add(current)) break;

                var edt = repo.GetEdt(current);
                if (edt is null)
                {
                    // Defer to scaffolder inference (e.g. NoYesId -> NoYes) when chain lookup is unavailable.
                    return null;
                }

                if (!string.IsNullOrWhiteSpace(edt.EnumType))
                    return edt.EnumType!;

                current = edt.Extends;
            }
        }
        catch
        {
            // Best-effort inference; fallback handled below.
        }

        return null;
    }
}
