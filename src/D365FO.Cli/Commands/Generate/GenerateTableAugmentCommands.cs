using D365FO.Core;
using D365FO.Core.Index;
using D365FO.Core.Scaffolding;
using Spectre.Console.Cli;

namespace D365FO.Cli.Commands.Generate;

/// <summary>
/// Shared plumbing for the two table-augmentation commands (<c>table-relation</c> /
/// <c>find-methods</c>): resolve the table from the index, and merge into an existing AxTable
/// XML through the SAME gated writer every generate command uses — so the grounding gate and
/// <c>--verify</c> apply here too, and the write is atomic with a backup sibling.
/// </summary>
internal static class TableAugment
{
    public static (TableDetails? Table, int? Failure) ResolveTable(OutputMode.Kind kind, string name, out MetadataRepository repo)
    {
        repo = null!;
        try { repo = RepoFactory.Create(); }
        catch (Exception ex)
        {
            return (null, RenderHelpers.Render(kind, ToolResult<object>.Fail("NO_INDEX",
                $"This command requires the SQLite index: {ex.Message}",
                "Run `d365fo index build` then `d365fo index extract` first.")));
        }

        var details = repo.GetTableDetails(name);
        if (details is null)
        {
            var hint = NameSuggester.HintFor(repo, NameSuggester.Kind.Table, name)
                       ?? "Run 'd365fo index build' after extracting metadata.";
            return (null, RenderHelpers.Render(kind,
                ToolResult<object>.Fail(D365FoErrorCodes.TableNotFound, $"Table '{name}' not found in index.", hint)));
        }
        return (details, null);
    }

    /// <summary>
    /// These commands augment an EXISTING table, so the shared scaffold-output options do not
    /// apply — refusing them beats accepting-and-quietly-dropping them.
    /// </summary>
    public static int? RejectScaffoldOutputOptions(OutputMode.Kind kind, GenerateSettings settings)
    {
        if (!string.IsNullOrWhiteSpace(settings.Out) || !string.IsNullOrWhiteSpace(settings.InstallTo))
        {
            return RenderHelpers.Render(kind, ToolResult<object>.Fail(D365FoErrorCodes.BadInput,
                "This command augments an EXISTING table — pass --apply-to <path-to-AxTable-xml> " +
                "(or nothing, to print the fragments), not --out/--install-to."));
        }
        return null;
    }

    /// <summary>Load an existing AxTable XML, mutate it, and write through the gated writer.</summary>
    public static (object? Applied, int? Failure) Apply(
        OutputMode.Kind kind,
        GroundingGate.GateResult gate,
        string path,
        Func<System.Xml.Linq.XDocument, IReadOnlyList<string>> mutate)
    {
        if (!File.Exists(path))
            return (null, RenderHelpers.Render(kind, ToolResult<object>.Fail(
                D365FoErrorCodes.WriteFailed, $"Table file not found: {path}")));

        System.Xml.Linq.XDocument doc;
        try { doc = System.Xml.Linq.XDocument.Load(path); }
        catch (Exception ex)
        {
            return (null, RenderHelpers.Render(kind, ToolResult<object>.Fail(
                D365FoErrorCodes.WriteFailed, $"Failed to parse table XML: {ex.Message}")));
        }

        IReadOnlyList<string> added;
        try { added = mutate(doc); }
        catch (InvalidOperationException ex)
        {
            return (null, RenderHelpers.Render(kind, ToolResult<object>.Fail(D365FoErrorCodes.WriteFailed, ex.Message)));
        }

        if (added.Count == 0)
        {
            return (new
            {
                path,
                added,
                note = "Everything derived already exists on the table — nothing written.",
            }, null);
        }

        try
        {
            var res = GenerateInstaller.Write(gate, doc, path, overwrite: true);
            return (new { path = res.Path, added, backup = res.BackupPath }, null);
        }
        catch (Exception ex)
        {
            return (null, RenderHelpers.Render(kind, ToolResult<object>.Fail(D365FoErrorCodes.WriteFailed, ex.Message)));
        }
    }
}

/// <summary>
/// <c>generate table-relation</c> — port of the upstream TRUDUtils "Create Table Relation":
/// for a field whose EDT carries an implicit reference to another table, generate the
/// explicit <c>&lt;AxTableRelation&gt;</c> D365FO now requires (EDT relations must be
/// migrated to table relations — <c>BPErrorEDTNotMigrated</c>). The reference table comes
/// from the indexed EDT metadata, so this works with no bridge and no VM.
/// </summary>
public sealed class GenerateTableRelationCommand : Command<GenerateTableRelationCommand.Settings>
{
    public sealed class Settings : GenerateSettings
    {
        [CommandArgument(0, "<TABLE>")]
        [System.ComponentModel.Description("Table whose fields to derive relations for (e.g. MyOrderLine).")]
        public string Table { get; init; } = "";

        [CommandOption("--field <NAME>")]
        [System.ComponentModel.Description("Specific field(s) to derive relations for (repeatable). Omit to scan every EDT-referencing field.")]
        public string[]? Fields { get; init; }

        [CommandOption("--apply-to <PATH>")]
        [System.ComponentModel.Description("Merge the relations into this existing AxTable XML (atomic write, .bak sibling; a relation the table already declares is skipped). Omit to print the fragments only.")]
        public string? ApplyTo { get; init; }
    }

    public override int Execute(CommandContext ctx, Settings settings)
    {
        var kind = OutputMode.Resolve(settings.Output);
        if (string.IsNullOrWhiteSpace(settings.Table))
            return RenderHelpers.Render(kind, ToolResult<object>.Fail(D365FoErrorCodes.BadInput, "Table name required."));
        if (TableAugment.RejectScaffoldOutputOptions(kind, settings) is int rejected) return rejected;

        var (table, failure) = TableAugment.ResolveTable(kind, settings.Table, out var repo);
        if (failure.HasValue) return failure.Value;

        var relations = TableAugmentScaffolder.DeriveRelations(
            table!, repo.GetEdt, settings.Fields, out var skipped);

        if (relations.Count == 0)
        {
            return RenderHelpers.Render(kind, ToolResult<object>.Fail("NO_RELATIONS",
                $"No EDT-backed table relations found for '{settings.Table}'." +
                (skipped.Count > 0 ? " " + string.Join("; ", skipped) : ""),
                "Only fields whose EDT declares a reference table produce a relation. " +
                "Use `d365fo get edt <Name>` to check an EDT's ReferenceTable."));
        }

        // The EDT name is the conventional PK field on the target table — verify it when
        // the index knows the target, and say so when it does not hold.
        var warnings = new List<string>();
        foreach (var rel in relations)
        {
            var related = repo.GetTableDetails(rel.RelatedTable);
            if (related is null)
            {
                warnings.Add($"{rel.Field}: related table '{rel.RelatedTable}' is not in the index — the constraint's RelatedField '{rel.RelatedField}' could not be verified.");
            }
            else if (!related.Fields.Any(f => string.Equals(f.Name, rel.RelatedField, StringComparison.OrdinalIgnoreCase)))
            {
                warnings.Add($"{rel.Field}: '{rel.RelatedTable}' declares no field '{rel.RelatedField}' — fix the RelatedField before building.");
            }
        }
        warnings.AddRange(skipped);

        // The relations are claims about OTHER tables — that is exactly what the gate proves.
        var gate = GenerateInstaller.Gate(settings, table!.Table.Name, doc: null,
            requiredSymbols: relations.Select(r => r.RelatedTable).Distinct(StringComparer.OrdinalIgnoreCase));
        if (gate.Failure is not null) return RenderHelpers.Render(kind, gate.Failure);
        warnings.AddRange(gate.Warnings);

        object? applied = null;
        if (!string.IsNullOrWhiteSpace(settings.ApplyTo))
        {
            (applied, var applyFailure) = TableAugment.Apply(kind, gate, settings.ApplyTo!,
                doc => TableAugmentScaffolder.MergeRelations(doc, relations));
            if (applyFailure.HasValue) return applyFailure.Value;
        }

        return GenerateInstaller.Done(kind, gate, settings, new
        {
            kind = "AxTableRelation",
            table = table.Table.Name,
            count = relations.Count,
            relations = relations.Select(r => new
            {
                name = r.Field,
                relatedTable = r.RelatedTable,
                constraint = $"{r.Field} == {r.RelatedTable}.{r.RelatedField}",
                xml = TableAugmentScaffolder.RelationElement(r).ToString(),
            }),
            applied,
            hint = applied is null
                ? "Re-run with --apply-to <path-to-AxTable-xml> to merge these into the table, or paste each fragment into its <Relations> block."
                : null,
        }, warnings);
    }
}

/// <summary>
/// <c>generate find-methods</c> — port of the upstream TRUDUtils "Create Find Method":
/// the standard static <c>find()</c>/<c>exists()</c>/<c>findRecId()</c> for a table, keyed
/// on its alternate-key (or first unique) index, following Microsoft's shipped convention
/// (selectForUpdate guard, firstonly, key null-guard).
/// </summary>
public sealed class GenerateFindMethodsCommand : Command<GenerateFindMethodsCommand.Settings>
{
    public sealed class Settings : GenerateSettings
    {
        [CommandArgument(0, "<TABLE>")]
        [System.ComponentModel.Description("Table to generate find methods for (e.g. CustTable).")]
        public string Table { get; init; } = "";

        [CommandOption("--key <FIELD>")]
        [System.ComponentModel.Description("Explicit key field(s) for find()/exists(), in order (repeatable). Overrides index detection.")]
        public string[]? Keys { get; init; }

        [CommandOption("--no-exists")]
        [System.ComponentModel.Description("Skip exists().")]
        public bool NoExists { get; init; }

        [CommandOption("--no-find-recid")]
        [System.ComponentModel.Description("Skip findRecId().")]
        public bool NoFindRecId { get; init; }

        [CommandOption("--apply-to <PATH>")]
        [System.ComponentModel.Description("Merge the methods into this existing AxTable XML's <SourceCode> (atomic write, .bak sibling; a method the table already declares is skipped, never overwritten). Omit to print the X++ only.")]
        public string? ApplyTo { get; init; }
    }

    public override int Execute(CommandContext ctx, Settings settings)
    {
        var kind = OutputMode.Resolve(settings.Output);
        if (string.IsNullOrWhiteSpace(settings.Table))
            return RenderHelpers.Render(kind, ToolResult<object>.Fail(D365FoErrorCodes.BadInput, "Table name required."));
        if (TableAugment.RejectScaffoldOutputOptions(kind, settings) is int rejected) return rejected;

        var (table, failure) = TableAugment.ResolveTable(kind, settings.Table, out _);
        if (failure.HasValue) return failure.Value;

        var tableName = table!.Table.Name; // canonical AOT spelling, not the caller's casing
        var keys = TableAugmentScaffolder.ResolveKeyFields(table, settings.Keys);

        var warnings = new List<string>();
        if (keys.Count == 0)
        {
            warnings.Add(
                "No unique key could be determined (no alternate-key or unique index in the index data), so only " +
                "findRecId() is generated — RecId always exists. Pass --key <Field> to get key-based find()/exists().");
        }

        var methods = TableAugmentScaffolder.BuildFindMethods(
            tableName, keys, includeExists: !settings.NoExists, includeFindRecId: !settings.NoFindRecId);

        if (methods.Count == 0)
            return RenderHelpers.Render(kind, ToolResult<object>.Fail(D365FoErrorCodes.BadInput,
                "Nothing to generate: no unique key and findRecId() was skipped too."));

        var clash = methods.Where(m => table.Methods.Any(
            existing => string.Equals(existing.Name, m.Name, StringComparison.OrdinalIgnoreCase)))
            .Select(m => m.Name).ToList();
        if (clash.Count > 0)
            warnings.Add($"The table already declares: {string.Join(", ", clash)} — those are skipped on --apply-to; compare before replacing by hand.");

        // The key fields' types are claims about EDTs — the gate proves them.
        var gate = GenerateInstaller.Gate(settings, tableName, doc: null,
            requiredSymbols: keys.Select(k => k.Type)
                .Where(t => t.Length > 0 && char.IsUpper(t[0]))
                .Distinct(StringComparer.OrdinalIgnoreCase));
        if (gate.Failure is not null) return RenderHelpers.Render(kind, gate.Failure);
        warnings.AddRange(gate.Warnings);

        object? applied = null;
        if (!string.IsNullOrWhiteSpace(settings.ApplyTo))
        {
            (applied, var applyFailure) = TableAugment.Apply(kind, gate, settings.ApplyTo!,
                doc => TableAugmentScaffolder.MergeMethods(doc, tableName, methods));
            if (applyFailure.HasValue) return applyFailure.Value;
        }

        return GenerateInstaller.Done(kind, gate, settings, new
        {
            kind = "AxTableMethods",
            table = tableName,
            keyFields = keys.Select(k => new { field = k.Field, type = k.Type }),
            methods = methods.Select(m => new { name = m.Name, source = m.Source }),
            applied,
            hint = applied is null
                ? "Re-run with --apply-to <path-to-AxTable-xml> to merge these into the table's <SourceCode>."
                : null,
        }, warnings);
    }
}
