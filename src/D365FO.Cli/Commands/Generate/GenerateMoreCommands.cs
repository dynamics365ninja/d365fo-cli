using D365FO.Core;
using D365FO.Core.Scaffolding;
using Spectre.Console.Cli;

using static D365FO.Core.ObjectTypes.ObjectTypeRegistry;

namespace D365FO.Cli.Commands.Generate;

/// <summary>Scaffolds an <c>AxDataEntityView</c>. ROADMAP §6.</summary>
public sealed class GenerateEntityCommand : Command<GenerateEntityCommand.Settings>
{
    public sealed class Settings : GenerateSettings
    {
        [CommandArgument(0, "<ENTITY>")]
        public string EntityName { get; init; } = "";

        [CommandOption("--table <TABLE>")]
        [System.ComponentModel.Description("Root data source table for the entity.")]
        public string? Table { get; init; }

        [CommandOption("--public-entity <NAME>")]
        public string? PublicEntity { get; init; }

        [CommandOption("--public-collection <NAME>")]
        public string? PublicCollection { get; init; }

        [CommandOption("--field <SPEC>")]
        [System.ComponentModel.Description("Repeatable: <name>[[:<dataField>[[:mandatory]]]].")]
        public string[] Fields { get; init; } = Array.Empty<string>();

        [CommandOption("--all-fields")]
        [System.ComponentModel.Description("Populate <Fields /> from the source table's columns. Requires the table to be indexed.")]
        public bool AllFields { get; init; }

        [CommandOption("--data-management")]
        [System.ComponentModel.Description("Emit DataManagementEnabled=Yes. Off by default: the staging table is NOT generated, so enabling this without an existing <ENTITY>Staging table fails the next build.")]
        public bool DataManagement { get; init; }

        [CommandOption("--staging-table <TABLE>")]
        [System.ComponentModel.Description("Staging table name when --data-management is set (default: <ENTITY>Staging).")]
        public string? StagingTable { get; init; }

        [CommandOption("--key <FIELD>")]
        [System.ComponentModel.Description("Repeatable: entity field forming the EntityKey. Derived from the table's alternate-key index when omitted — a public entity is invalid without one.")]
        public string[] Keys { get; init; } = Array.Empty<string>();

        [CommandOption("--relation <SPEC>")]
        [System.ComponentModel.Description("Repeatable: <Name>:<RelatedEntity>:<field>=<relatedField>[[,<field>=<relatedField>]][[:<Cardinality>]][[:<RelationshipType>]]. Example: --relation Customer:CustCustomerV3Entity:CustomerAccount=CustomerAccount:ZeroOne:Association")]
        public string[] Relations { get; init; } = Array.Empty<string>();

        [CommandOption("--computed-field <SPEC>")]
        [System.ComponentModel.Description("Repeatable: <Name>:<staticMethodName>[[:<EDT>]]. Emits an AxDataEntityViewUnmappedField with IsComputedField=Yes; write the method on the entity yourself — it must return the SQL text of the expression.")]
        public string[] ComputedFields { get; init; } = Array.Empty<string>();

        [CommandOption("--out-staging <PATH>")]
        [System.ComponentModel.Description("Where to write the DMF staging table generated alongside a --data-management entity (default: a sibling of the entity, under AxTable when --install-to is used).")]
        public string? OutStaging { get; init; }

        [CommandOption("--no-staging-table")]
        [System.ComponentModel.Description("With --data-management, do not generate the staging table. The entity then names a table that must already exist, or the next build fails.")]
        public bool NoStagingTable { get; init; }
    }

    public override int Execute(CommandContext ctx, Settings settings)
    {
        var kind = OutputMode.Resolve(settings.Output);
        if (string.IsNullOrWhiteSpace(settings.EntityName))
            return RenderHelpers.Render(kind, ToolResult<object>.Fail(D365FoErrorCodes.BadInput, "Entity name required."));
        if (string.IsNullOrWhiteSpace(settings.Table))
            return RenderHelpers.Render(kind, ToolResult<object>.Fail(D365FoErrorCodes.BadInput, "--table <TABLE> required."));
        var hasInstall = !string.IsNullOrWhiteSpace(settings.InstallTo);
        var hasOut = !string.IsNullOrWhiteSpace(settings.Out);
        if (!hasInstall && !hasOut)
            return RenderHelpers.Render(kind, ToolResult<object>.Fail(D365FoErrorCodes.BadInput, "--out or --install-to is required."));
        var outPath = settings.Out;
        if (hasInstall && !hasOut)
        {
            outPath = GenerateInstaller.ResolveInstallPath(kind, Folders.DataEntityView, settings.EntityName, settings.InstallTo!, out var fail);
            if (fail.HasValue) return fail.Value;
        }

        var fields = settings.Fields.Select(ParseField).ToList();
        var autoFromTable = false;
        if (fields.Count == 0 && settings.AllFields)
        {
            var repo = RepoFactory.Create();
            var tableDetails = repo.GetTableDetails(settings.Table!);
            if (tableDetails is null)
                return RenderHelpers.Render(kind, ToolResult<object>.Fail(
                    D365FoErrorCodes.TableNotFound,
                    $"Table '{settings.Table}' not found in the index. Extract the model first or pass explicit --field <SPEC>."));
            foreach (var f in tableDetails.Fields)
                fields.Add(new EntityFieldSpec(f.Name, f.Name, f.Mandatory));
            autoFromTable = true;
        }
        var (keys, keySource, keyFailure) = ResolveKeys(kind, settings, fields);
        if (keyFailure.HasValue) return keyFailure.Value;

        if (!TryParseRelations(settings.Relations, out var relations, out var relErr))
            return RenderHelpers.Render(kind, ToolResult<object>.Fail(D365FoErrorCodes.BadInput, relErr!));
        if (!TryParseComputedFields(settings.ComputedFields, out var computed, out var compErr))
            return RenderHelpers.Render(kind, ToolResult<object>.Fail(D365FoErrorCodes.BadInput, compErr!));

        var doc = XppScaffolder.DataEntity(
            settings.EntityName, settings.Table!, settings.PublicEntity, settings.PublicCollection, fields,
            settings.DataManagement, settings.StagingTable, entityCategory: null, keyFields: keys,
            relations: relations, computedFields: computed);
        try
        {
            // Grounding gate (issue #161): uniform across every generate subcommand.
            var gate = GenerateInstaller.Gate(settings, settings.EntityName, doc);
            if (gate.Failure is not null) return RenderHelpers.Render(kind, gate.Failure);

            var res = GenerateInstaller.Write(gate, doc, outPath!, settings.Overwrite);

            // The staging table is not optional decoration: DataManagementEnabled=Yes names a
            // table, and the build fails on the next compile if it is not there (issue #162).
            object? staging = null;
            if (settings.DataManagement && !settings.NoStagingTable)
            {
                var stagingName = string.IsNullOrWhiteSpace(settings.StagingTable)
                    ? settings.EntityName + "Staging"
                    : settings.StagingTable!;

                var stagingPath = settings.OutStaging;
                if (string.IsNullOrWhiteSpace(stagingPath))
                {
                    stagingPath = hasInstall && !hasOut
                        ? GenerateInstaller.ResolveInstallPath(kind, Folders.Table, stagingName, settings.InstallTo!, out var sf)
                        : System.IO.Path.Combine(System.IO.Path.GetDirectoryName(res.Path)!, stagingName + ".xml");
                    if (stagingPath is null)
                        return RenderHelpers.Render(kind, ToolResult<object>.Fail(D365FoErrorCodes.WriteFailed,
                            $"Wrote the entity but could not resolve where to put its staging table '{stagingName}'."));
                }

                var stagingRes = GenerateInstaller.Write(gate,
                    XppScaffolder.EntityStagingTable(stagingName, fields), stagingPath!, settings.Overwrite);
                staging = new { name = stagingName, path = stagingRes.Path, bytes = stagingRes.Bytes };
            }

            return GenerateInstaller.Done(kind, gate, settings, new
            {
                kind = "AxDataEntityView",
                name = settings.EntityName,
                table = settings.Table,
                path = res.Path,
                bytes = res.Bytes,
                backup = res.BackupPath,
                fieldCount = fields.Count,
                fieldsFromTable = autoFromTable,
                keyFields = keys,
                keySource,
                relations = relations.Select(r => new { r.Name, related = r.RelatedDataEntity, constraints = r.Constraints.Count }).ToList(),
                computedFields = computed.Select(c => new { c.Name, c.Method, c.Edt }).ToList(),
                stagingTable = staging,
                model = settings.InstallTo,
            });
        }
        catch (Exception ex)
        {
            return RenderHelpers.Render(kind, ToolResult<object>.Fail(D365FoErrorCodes.WriteFailed, ex.Message));
        }
    }

    /// <summary>
    /// Parse <c>--relation &lt;Name&gt;:&lt;RelatedEntity&gt;:&lt;f&gt;=&lt;rf&gt;[,…][:Cardinality][:RelationshipType]</c>.
    /// </summary>
    /// <remarks>
    /// The enum values are checked here rather than left to the writer: they come from the
    /// contract catalog, and a value outside the enum is XML008 — the whole entity fails to
    /// deserialize, so refusing at parse time with the list of valid values is strictly better
    /// than a rejected write.
    /// </remarks>
    private static bool TryParseRelations(
        string[] raw, out List<EntityRelationSpec> relations, out string? error)
    {
        relations = [];
        error = null;

        foreach (var spec in raw.Where(r => !string.IsNullOrWhiteSpace(r)))
        {
            var parts = spec.Split(':', StringSplitOptions.TrimEntries);
            if (parts.Length < 3)
            {
                error = $"Invalid --relation '{spec}'. Expected <Name>:<RelatedEntity>:<field>=<relatedField>[,…][:<Cardinality>][:<RelationshipType>].";
                return false;
            }

            var constraints = new List<EntityRelationConstraintSpec>();
            foreach (var pair in parts[2].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var kv = pair.Split('=', 2, StringSplitOptions.TrimEntries);
                if (kv.Length != 2 || kv[0].Length == 0 || kv[1].Length == 0)
                {
                    error = $"Invalid constraint '{pair}' in --relation '{spec}'. Expected <field>=<relatedField>.";
                    return false;
                }
                constraints.Add(new EntityRelationConstraintSpec(kv[0], kv[1]));
            }
            if (constraints.Count == 0)
            {
                error = $"--relation '{spec}' has no constraints. A relation that joins on nothing is not a relation.";
                return false;
            }

            string? cardinality = parts.Length > 3 && parts[3].Length > 0 ? parts[3] : null;
            string? relationshipType = parts.Length > 4 && parts[4].Length > 0 ? parts[4] : null;

            if (!EnumValueOk("Cardinality", cardinality, out error)) return false;
            if (!EnumValueOk("RelationshipType", relationshipType, out error)) return false;

            relations.Add(new EntityRelationSpec(
                parts[0], parts[1], constraints,
                Cardinality: cardinality,
                RelationshipType: relationshipType));
        }

        return true;
    }

    private static bool EnumValueOk(string enumName, string? value, out string? error)
    {
        error = null;
        if (string.IsNullOrWhiteSpace(value)) return true;

        var allowed = D365FO.Core.Metadata.MetadataContracts.EnumValues(enumName);
        if (allowed.Count == 0 || allowed.Contains(value, StringComparer.Ordinal)) return true;

        error = $"'{value}' is not a value of {enumName}. Valid: {string.Join(", ", allowed)}.";
        return false;
    }

    /// <summary>Parse <c>--computed-field &lt;Name&gt;:&lt;method&gt;[:&lt;EDT&gt;]</c>.</summary>
    private static bool TryParseComputedFields(
        string[] raw, out List<EntityComputedFieldSpec> computed, out string? error)
    {
        computed = [];
        error = null;

        foreach (var spec in raw.Where(c => !string.IsNullOrWhiteSpace(c)))
        {
            var parts = spec.Split(':', StringSplitOptions.TrimEntries);
            if (parts.Length < 2 || parts[0].Length == 0 || parts[1].Length == 0)
            {
                error = $"Invalid --computed-field '{spec}'. Expected <Name>:<staticMethodName>[:<EDT>].";
                return false;
            }
            computed.Add(new EntityComputedFieldSpec(
                parts[0], parts[1], parts.Length > 2 && parts[2].Length > 0 ? parts[2] : null));
        }

        return true;
    }

    private static EntityFieldSpec ParseField(string raw)
    {
        var parts = raw.Split(':', StringSplitOptions.TrimEntries);
        var name = parts.Length > 0 ? parts[0] : "";
        var data = parts.Length > 1 && parts[1].Length > 0 ? parts[1] : null;
        var mandatory = parts.Length > 2 && string.Equals(parts[2], "mandatory", StringComparison.OrdinalIgnoreCase);
        return new EntityFieldSpec(name, data, mandatory);
    }

    /// <summary>
    /// The EntityKey's fields, and where they came from. A public data entity without
    /// one is rejected by the metadata validator outright, so this refuses to guess:
    /// the table's own alternate key is the answer when the index knows it, an
    /// explicitly-mandatory field is the fallback, and otherwise the caller is asked
    /// rather than handed an entity that cannot load.
    /// </summary>
    private static (IReadOnlyList<string> Keys, string Source, int? Failure) ResolveKeys(
        OutputMode.Kind kind, Settings settings, List<EntityFieldSpec> fields)
    {
        if (settings.Keys.Length > 0)
            return (settings.Keys, "option", null);

        var mapped = fields
            .Select(f => f.DataField ?? f.Name)
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        // The table's alternate key is what a data entity key normally mirrors.
        try
        {
            var repo = RepoFactory.Create();
            var alternate = repo.GetTableIndexes(settings.Table!)
                .Where(i => i.AlternateKey && !string.IsNullOrWhiteSpace(i.FieldsCsv))
                .Select(i => i.FieldsCsv!.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                .FirstOrDefault(f => f.Length > 0 && f.All(mapped.Contains));

            if (alternate is not null) return (alternate, "alternate-key-index", null);
        }
        catch (Exception)
        {
            // No index configured — fall through to the field-level fallbacks. The
            // caller still gets an actionable error below if nothing else works.
        }

        var mandatory = fields.Where(f => f.IsMandatory).Select(f => f.DataField ?? f.Name).ToList();
        if (mandatory.Count > 0) return (mandatory, "mandatory-fields", null);

        return ([], "none", RenderHelpers.Render(kind, ToolResult<object>.Fail(
            D365FoErrorCodes.BadInput,
            $"Cannot determine an EntityKey for '{settings.EntityName}'.",
            $"A public data entity is invalid without one. Pass --key <FIELD> (repeatable), mark a field mandatory " +
            $"(--field <NAME>::mandatory), or index '{settings.Table}' so its alternate key can be read.")));
    }
}

/// <summary>Scaffolds <c>AxTableExtension</c> / <c>AxFormExtension</c> / <c>AxEdtExtension</c> / <c>AxEnumExtension</c>. ROADMAP §6.</summary>
public sealed class GenerateExtensionCommand : Command<GenerateExtensionCommand.Settings>
{
    public sealed class Settings : GenerateSettings
    {
        [CommandArgument(0, "<KIND>")]
        [System.ComponentModel.Description("Extension kind: Table, Form, Edt, Enum, View, Query, DataEntityView, SecurityDuty, SecurityRole.")]
        public string Kind { get; init; } = "";

        [CommandArgument(1, "<TARGET>")]
        public string Target { get; init; } = "";

        [CommandOption("--suffix <SUFFIX>")]
        [System.ComponentModel.Description("Extension suffix. Defaults to the InstallTo model name or 'Extension'.")]
        public string? Suffix { get; init; }

        [CommandOption("--field <SPEC>")]
        [System.ComponentModel.Description("Repeatable; Table only: <name>:<edt>[[:mandatory]]. Example: --field QuantityD:Qty:mandatory")]
        public string[] Fields { get; init; } = Array.Empty<string>();

        [CommandOption("--privilege <NAME>")]
        [System.ComponentModel.Description("Repeatable; SecurityDuty/SecurityRole only: privilege reference to add to the base duty/role.")]
        public string[] Privileges { get; init; } = Array.Empty<string>();

        [CommandOption("--duty <NAME>")]
        [System.ComponentModel.Description("Repeatable; SecurityRole only: duty reference to add to the base role.")]
        public string[] Duties { get; init; } = Array.Empty<string>();

        [CommandOption("--set-property <SPEC>")]
        [System.ComponentModel.Description("Repeatable; SecurityDuty/SecurityRole only: <Member>=<Value>. Overrides a property of the base object (Enabled, Label, Description, ContextString…) without overlaying it.")]
        public string[] PropertyModifications { get; init; } = Array.Empty<string>();
    }

    /// <summary>
    /// Parse <c>--field &lt;name&gt;:&lt;edt&gt;[:mandatory]</c>, the same spelling
    /// <c>generate table --field</c> takes — a field added through an extension is the same
    /// declaration, so it should not need a second syntax to remember.
    /// </summary>
    private static TableFieldSpec ParseField(string raw)
    {
        var parts = raw.Split(':', StringSplitOptions.TrimEntries);
        var name = parts.Length > 0 ? parts[0] : "";
        var edt = parts.Length > 1 ? parts[1] : null;
        var mandatory = parts.Length > 2 && string.Equals(parts[2], "mandatory", StringComparison.OrdinalIgnoreCase);
        return new TableFieldSpec(name, string.IsNullOrEmpty(edt) ? null : edt, null, mandatory);
    }

    /// <summary>Parse <c>--set-property &lt;Member&gt;=&lt;Value&gt;</c> pairs.</summary>
    /// <remarks>
    /// The member is not checked against the base type's contract here: an extension's
    /// PropertyModifications name members of the object being extended, and this command does
    /// not read that object. A misspelled member is caught where it shows — the provider
    /// ignores the modification — which is a weaker guarantee than the rest of the write path
    /// gives and is called out in the command's help rather than papered over.
    /// </remarks>
    private static bool TryParsePropertyModifications(
        string[] raw, out List<XppScaffolder.PropertyModificationSpec> mods, out string? error)
    {
        mods = [];
        error = null;

        foreach (var spec in raw.Where(p => !string.IsNullOrWhiteSpace(p)))
        {
            var kv = spec.Split('=', 2, StringSplitOptions.TrimEntries);
            if (kv.Length != 2 || kv[0].Length == 0)
            {
                error = $"Invalid --set-property '{spec}'. Expected <Member>=<Value>.";
                return false;
            }
            mods.Add(new XppScaffolder.PropertyModificationSpec(kv[0], kv[1]));
        }

        return true;
    }

    public override int Execute(CommandContext ctx, Settings settings)
    {
        var kind = OutputMode.Resolve(settings.Output);
        if (string.IsNullOrWhiteSpace(settings.Kind))
            return RenderHelpers.Render(kind, ToolResult<object>.Fail(D365FoErrorCodes.BadInput, "Extension kind required."));
        if (string.IsNullOrWhiteSpace(settings.Target))
            return RenderHelpers.Render(kind, ToolResult<object>.Fail(D365FoErrorCodes.BadInput, "Target object required."));
        var hasInstall = !string.IsNullOrWhiteSpace(settings.InstallTo);
        var hasOut = !string.IsNullOrWhiteSpace(settings.Out);
        if (!hasInstall && !hasOut)
            return RenderHelpers.Render(kind, ToolResult<object>.Fail(D365FoErrorCodes.BadInput, "--out or --install-to is required."));

        var suffix = settings.Suffix
            ?? (hasInstall ? settings.InstallTo! : "Extension");
        var normalizedKind = settings.Kind.Replace("-", "").Replace("_", "");
        var axFolder = normalizedKind.ToLowerInvariant() switch
        {
            "table" => "AxTableExtension",
            "form" => "AxFormExtension",
            "edt" => "AxEdtExtension",
            "enum" => "AxEnumExtension",
            "view" => "AxViewExtension",
            // A query's extension type is AxQuerySimpleExtension. There is no AxQueryExtension
            // type and no folder of that name on any AOS.
            "query" => "AxQuerySimpleExtension",
            "dataentityview" => "AxDataEntityViewExtension",
            "securityduty" => "AxSecurityDutyExtension",
            "securityrole" => "AxSecurityRoleExtension",
            _ => null,
        };
        if (axFolder is null)
            return RenderHelpers.Render(kind, ToolResult<object>.Fail(D365FoErrorCodes.BadInput,
                $"Unsupported extension kind: {settings.Kind}. Expected Table|Form|Edt|Enum|View|Query|DataEntityView|SecurityDuty|SecurityRole."));

        // A dotted target already names an extension (AOT object names never contain a dot) —
        // compose against its base so "CustTable.Ext" does not become "CustTable.Ext.Ext".
        var targetBase = settings.Target;
        var targetDot = targetBase.IndexOf('.');
        string? targetRewriteNote = null;
        if (targetDot > 0)
        {
            targetRewriteNote = $"Target \"{settings.Target}\" names an extension, not its base — composed against \"{targetBase[..targetDot]}\" instead.";
            targetBase = targetBase[..targetDot];
        }
        var fullName = $"{targetBase}.{suffix}";
        var outPath = settings.Out;
        if (hasInstall && !hasOut)
        {
            outPath = GenerateInstaller.ResolveInstallPath(kind, axFolder, fullName, settings.InstallTo!, out var fail);
            if (fail.HasValue) return fail.Value;
        }

        if (!TryParsePropertyModifications(settings.PropertyModifications, out var propertyMods, out var pmErr))
            return RenderHelpers.Render(kind, ToolResult<object>.Fail(D365FoErrorCodes.BadInput, pmErr!));
        if (propertyMods.Count > 0 && axFolder is not ("AxSecurityDutyExtension" or "AxSecurityRoleExtension"))
            return RenderHelpers.Render(kind, ToolResult<object>.Fail(
                D365FoErrorCodes.BadInput,
                $"--set-property applies to SecurityDuty and SecurityRole extensions only; {axFolder} has no PropertyModifications member, " +
                "so the overrides would be dropped on read."));

        var fieldSpecs = settings.Fields
            .Where(f => !string.IsNullOrWhiteSpace(f))
            .Select(ParseField)
            .ToList();
        if (fieldSpecs.Count > 0 && axFolder != "AxTableExtension")
            return RenderHelpers.Render(kind, ToolResult<object>.Fail(
                D365FoErrorCodes.BadInput,
                $"--field applies to Table extensions only; {axFolder} has no Fields member, " +
                "so the fields would be dropped on read."));
        if (fieldSpecs.Any(f => string.IsNullOrWhiteSpace(f.Name)))
            return RenderHelpers.Render(kind, ToolResult<object>.Fail(
                D365FoErrorCodes.BadInput, "Every --field needs a name: <name>:<edt>[:mandatory]."));
        if (fieldSpecs.FirstOrDefault(f => string.IsNullOrWhiteSpace(f.Edt)) is { } noEdt)
            return RenderHelpers.Render(kind, ToolResult<object>.Fail(
                D365FoErrorCodes.BadInput,
                $"--field '{noEdt.Name}' has no EDT. Every field on a table extension needs one: <name>:<edt>[:mandatory].",
                "Run `d365fo suggest edt <name>` to find one, or `d365fo generate edt` to create it."));

        var doc = axFolder switch
        {
            "AxSecurityDutyExtension" => XppScaffolder.SecurityDutyExtension(targetBase, suffix, settings.Privileges, propertyMods),
            "AxSecurityRoleExtension" => XppScaffolder.SecurityRoleExtension(targetBase, suffix, settings.Duties, settings.Privileges, propertyMods),
            // The scaffolder takes the base kind, not the type name — and the two differ for
            // queries, whose extension is AxQuerySimpleExtension.
            "AxQuerySimpleExtension" => XppScaffolder.Extension("query", targetBase, suffix),
            // The EDT case needs the index-backed base-type resolver to pin the concrete
            // AxEdt*Extension subtype; for a table extension the same resolver picks the
            // concrete AxTableField* subtype per field. The other kinds ignore it.
            _ => XppScaffolder.Extension(axFolder["Ax".Length..^"Extension".Length], targetBase, suffix,
                     GenerateInstaller.BuildEdtBaseTypeResolver(), fieldSpecs),
        };

        // Grounding gate: the target object must exist in the index; fail
        // closed under D365FO_GROUNDING_ENFORCE=true. The EDTs a field names are
        // required too — a hallucinated EDT is the failure this gate exists for.
        var gate = GenerateInstaller.Gate(settings, targetBase, doc,
            requiredSymbols: new[] { targetBase }
                .Concat(fieldSpecs.Select(f => f.Edt!))
                .ToArray());
        if (gate.Failure is not null) return RenderHelpers.Render(kind, gate.Failure);

        try
        {
            var res = GenerateInstaller.Write(gate, doc, outPath!, settings.Overwrite);
            return GenerateInstaller.Done(kind, gate, settings, new
            {
                kind = axFolder,
                name = fullName,
                target = targetBase,
                targetRewritten = targetRewriteNote,
                suffix,
                fields = fieldSpecs.Count == 0
                    ? null
                    : fieldSpecs.Select(f => new { name = f.Name, edt = f.Edt, mandatory = f.Mandatory }).ToArray(),
                path = res.Path,
                bytes = res.Bytes,
                backup = res.BackupPath,
                model = settings.InstallTo,
                grounding = gate.Grounding,
            });
        }
        catch (Exception ex)
        {
            return RenderHelpers.Render(kind, ToolResult<object>.Fail(D365FoErrorCodes.WriteFailed, ex.Message));
        }
    }
}

/// <summary>Scaffolds an event-handler subscriber class. ROADMAP §6.</summary>
public sealed class GenerateEventHandlerCommand : Command<GenerateEventHandlerCommand.Settings>
{
    public sealed class Settings : GenerateSettings
    {
        [CommandArgument(0, "<CLASS_NAME>")]
        public string ClassName { get; init; } = "";

        [CommandOption("--source-kind <KIND>")]
        [System.ComponentModel.Description("Form | FormDataSource | Table | Class.")]
        public string SourceKind { get; init; } = "Form";

        [CommandOption("--source-object <NAME>")]
        public string? SourceObject { get; init; }

        [CommandOption("--event <NAME>")]
        [System.ComponentModel.Description("E.g. OnInitialized / OnValidatingWrite / PostActivate.")]
        public string Event { get; init; } = "OnInitialized";

        [CommandOption("--method <NAME>")]
        public string HandlerMethod { get; init; } = "OnEvent";
    }

    public override int Execute(CommandContext ctx, Settings settings)
    {
        var kind = OutputMode.Resolve(settings.Output);
        if (string.IsNullOrWhiteSpace(settings.ClassName))
            return RenderHelpers.Render(kind, ToolResult<object>.Fail(D365FoErrorCodes.BadInput, "Class name required."));
        if (string.IsNullOrWhiteSpace(settings.SourceObject))
            return RenderHelpers.Render(kind, ToolResult<object>.Fail(D365FoErrorCodes.BadInput, "--source-object required."));

        var doc = XppScaffolder.EventHandler(settings.ClassName, settings.SourceKind, settings.SourceObject!, settings.Event, settings.HandlerMethod);

        // Grounding gate: the event source object must exist in the index;
        // fail closed under D365FO_GROUNDING_ENFORCE=true.
        var gate = GenerateInstaller.Gate(settings, settings.SourceObject!, doc,
            requiredSymbols: new[] { settings.SourceObject! });
        if (gate.Failure is not null) return RenderHelpers.Render(kind, gate.Failure);

        return GenerateInstaller.Emit(
            kind, gate, settings, "class", Folders.Class, settings.ClassName,
            doc,
            r => new
            {
                kind = "AxClass",
                role = "EventHandler",
                name = settings.ClassName,
                sourceKind = settings.SourceKind,
                sourceObject = settings.SourceObject,
                @event = settings.Event,
                method = settings.HandlerMethod,
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

/// <summary>Scaffolds a security privilege granting a single entry point. ROADMAP §6.</summary>
public sealed class GeneratePrivilegeCommand : Command<GeneratePrivilegeCommand.Settings>
{
    public sealed class Settings : GenerateSettings
    {
        [CommandArgument(0, "<NAME>")]
        public string Name { get; init; } = "";

        [CommandOption("--entry-point <NAME>")]
        public string? EntryPoint { get; init; }

        [CommandOption("--entry-kind <KIND>")]
        [System.ComponentModel.Description("MenuItemDisplay | MenuItemAction | MenuItemOutput | WebMenuItem.")]
        public string EntryKind { get; init; } = "MenuItemDisplay";

        [CommandOption("--entry-object <NAME>")]
        [System.ComponentModel.Description("Target object name when different from --entry-point.")]
        public string? EntryObject { get; init; }

        [CommandOption("--access <LEVEL>")]
        [System.ComponentModel.Description("NoAccess | Read | Update | Create | Correct | Delete.")]
        public string Access { get; init; } = "Read";

        [CommandOption("--label <TEXT>")]
        public string? Label { get; init; }

        [CommandOption("--data-entity <NAME>")]
        [System.ComponentModel.Description("Grant OData/DMF permissions on this data entity (emits <DataEntityPermissions>).")]
        public string? DataEntity { get; init; }

        [CommandOption("--data-entity-access <LEVEL>")]
        [System.ComponentModel.Description("view (Read only, default) | maintain (Correct/Create/Delete/Read/Update).")]
        public string DataEntityAccess { get; init; } = "view";

        [CommandOption("--into-role <PATH>")]
        [System.ComponentModel.Description("Path to an existing AxSecurityRole XML; after scaffolding, merge this privilege's Name into the role's <Privileges>.")]
        public string? IntoRole { get; init; }
    }

    public override int Execute(CommandContext ctx, Settings settings)
    {
        var kind = OutputMode.Resolve(settings.Output);
        if (string.IsNullOrWhiteSpace(settings.Name))
            return RenderHelpers.Render(kind, ToolResult<object>.Fail(D365FoErrorCodes.BadInput, "Privilege name required."));
        if (string.IsNullOrWhiteSpace(settings.EntryPoint) && string.IsNullOrWhiteSpace(settings.DataEntity))
            return RenderHelpers.Render(kind, ToolResult<object>.Fail(D365FoErrorCodes.BadInput, "--entry-point or --data-entity required."));
        if (!string.Equals(settings.DataEntityAccess, "view", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(settings.DataEntityAccess, "maintain", StringComparison.OrdinalIgnoreCase))
            return RenderHelpers.Render(kind, ToolResult<object>.Fail(D365FoErrorCodes.BadInput, "--data-entity-access must be view or maintain."));
        var hasInstall = !string.IsNullOrWhiteSpace(settings.InstallTo);
        var hasOut = !string.IsNullOrWhiteSpace(settings.Out);
        if (!hasInstall && !hasOut)
            return RenderHelpers.Render(kind, ToolResult<object>.Fail(D365FoErrorCodes.BadInput, "--out or --install-to is required."));
        var outPath = settings.Out;
        if (hasInstall && !hasOut)
        {
            outPath = GenerateInstaller.ResolveInstallPath(kind, Folders.SecurityPrivilege, settings.Name, settings.InstallTo!, out var fail);
            if (fail.HasValue) return fail.Value;
        }

        var doc = XppScaffolder.Privilege(settings.Name, settings.EntryPoint, settings.EntryKind, settings.EntryObject, settings.Access, settings.Label,
            settings.DataEntity, settings.DataEntityAccess);
        try
        {
            // Grounding gate (issue #161): uniform across every generate subcommand.
            var gate = GenerateInstaller.Gate(settings, settings.Name, doc);
            if (gate.Failure is not null) return RenderHelpers.Render(kind, gate.Failure);

            var res = GenerateInstaller.Write(gate, doc, outPath!, settings.Overwrite);
            object? intoRole = null;
            if (!string.IsNullOrWhiteSpace(settings.IntoRole))
            {
                if (SecurityRoleMerge.AddReferences(settings.IntoRole!, duties: null, privileges: new[] { settings.Name }, out var mergeResult, out var err))
                {
                    intoRole = mergeResult;
                }
                else
                {
                    return RenderHelpers.Render(kind, ToolResult<object>.Fail(D365FoErrorCodes.WriteFailed, err!));
                }
            }
            return GenerateInstaller.Done(kind, gate, settings, new
            {
                kind = "AxSecurityPrivilege",
                name = settings.Name,
                entryPoint = settings.EntryPoint,
                entryKind = settings.EntryKind,
                access = settings.Access,
                dataEntity = settings.DataEntity,
                dataEntityAccess = settings.DataEntity is null ? null : settings.DataEntityAccess,
                path = res.Path,
                bytes = res.Bytes,
                backup = res.BackupPath,
                model = settings.InstallTo,
                intoRole,
            });
        }
        catch (Exception ex)
        {
            return RenderHelpers.Render(kind, ToolResult<object>.Fail(D365FoErrorCodes.WriteFailed, ex.Message));
        }
    }
}

/// <summary>Scaffolds a security duty grouping one or more privileges. ROADMAP §6.</summary>
public sealed class GenerateDutyCommand : Command<GenerateDutyCommand.Settings>
{
    public sealed class Settings : GenerateSettings
    {
        [CommandArgument(0, "<NAME>")]
        public string Name { get; init; } = "";

        [CommandOption("--privilege <NAME>")]
        [System.ComponentModel.Description("Repeatable. Privileges to aggregate under this duty.")]
        public string[] Privileges { get; init; } = Array.Empty<string>();

        [CommandOption("--label <TEXT>")]
        public string? Label { get; init; }

        [CommandOption("--into-role <PATH>")]
        [System.ComponentModel.Description("Path to an existing AxSecurityRole XML; after scaffolding, merge this duty's Name into the role's <Duties>.")]
        public string? IntoRole { get; init; }

        [CommandOption("--description <TEXT>")]
        [System.ComponentModel.Description("Description shown to administrators in the security-configuration UI.")]
        public string? Description { get; init; }

        [CommandOption("--context-string <TEXT>")]
        [System.ComponentModel.Description("ContextString the role-assignment rules match on.")]
        public string? ContextString { get; init; }

        [CommandOption("--disabled")]
        [System.ComponentModel.Description("Emit Enabled=No — the duty ships switched off.")]
        public bool Disabled { get; init; }
    }

    public override int Execute(CommandContext ctx, Settings settings)
    {
        var kind = OutputMode.Resolve(settings.Output);
        if (string.IsNullOrWhiteSpace(settings.Name))
            return RenderHelpers.Render(kind, ToolResult<object>.Fail(D365FoErrorCodes.BadInput, "Duty name required."));
        if (settings.Privileges.Length == 0)
            return RenderHelpers.Render(kind, ToolResult<object>.Fail(D365FoErrorCodes.BadInput, "At least one --privilege required."));
        var hasInstall = !string.IsNullOrWhiteSpace(settings.InstallTo);
        var hasOut = !string.IsNullOrWhiteSpace(settings.Out);
        if (!hasInstall && !hasOut)
            return RenderHelpers.Render(kind, ToolResult<object>.Fail(D365FoErrorCodes.BadInput, "--out or --install-to is required."));
        var outPath = settings.Out;
        if (hasInstall && !hasOut)
        {
            outPath = GenerateInstaller.ResolveInstallPath(kind, Folders.SecurityDuty, settings.Name, settings.InstallTo!, out var fail);
            if (fail.HasValue) return fail.Value;
        }

        var doc = XppScaffolder.Duty(
            settings.Name, settings.Privileges, settings.Label,
            description: settings.Description,
            enabled: settings.Disabled ? false : null,
            contextString: settings.ContextString);
        try
        {
            // Grounding gate (issue #161): uniform across every generate subcommand.
            var gate = GenerateInstaller.Gate(settings, settings.Name, doc);
            if (gate.Failure is not null) return RenderHelpers.Render(kind, gate.Failure);

            var res = GenerateInstaller.Write(gate, doc, outPath!, settings.Overwrite);
            object? intoRole = null;
            if (!string.IsNullOrWhiteSpace(settings.IntoRole))
            {
                if (SecurityRoleMerge.AddReferences(settings.IntoRole!, duties: new[] { settings.Name }, privileges: null, out var mergeResult, out var err))
                {
                    intoRole = mergeResult;
                }
                else
                {
                    return RenderHelpers.Render(kind, ToolResult<object>.Fail(D365FoErrorCodes.WriteFailed, err!));
                }
            }
            return GenerateInstaller.Done(kind, gate, settings, new
            {
                kind = "AxSecurityDuty",
                name = settings.Name,
                privilegeCount = settings.Privileges.Length,
                privileges = settings.Privileges,
                path = res.Path,
                bytes = res.Bytes,
                backup = res.BackupPath,
                model = settings.InstallTo,
                intoRole,
            });
        }
        catch (Exception ex)
        {
            return RenderHelpers.Render(kind, ToolResult<object>.Fail(D365FoErrorCodes.WriteFailed, ex.Message));
        }
    }
}

/// <summary>
/// Scaffolds a new <c>AxSecurityRole</c> or, with <c>--add-to</c>, appends duty /
/// privilege references to an existing role document. ROADMAP §6.
/// </summary>
public sealed class GenerateRoleCommand : Command<GenerateRoleCommand.Settings>
{
    public sealed class Settings : GenerateSettings
    {
        [CommandArgument(0, "<NAME>")]
        public string Name { get; init; } = "";

        [CommandOption("--duty <NAME>")]
        [System.ComponentModel.Description("Repeatable. Duties referenced by this role.")]
        public string[] Duties { get; init; } = Array.Empty<string>();

        [CommandOption("--privilege <NAME>")]
        [System.ComponentModel.Description("Repeatable. Privileges referenced directly by this role.")]
        public string[] Privileges { get; init; } = Array.Empty<string>();

        [CommandOption("--label <TEXT>")]
        public string? Label { get; init; }

        [CommandOption("--description <TEXT>")]
        public string? Description { get; init; }

        [CommandOption("--add-to <PATH>")]
        [System.ComponentModel.Description("Path to an existing AxSecurityRole XML file; duties/privileges are merged in-place instead of creating a new file.")]
        public string? AddTo { get; init; }

        [CommandOption("--sub-role <NAME>")]
        [System.ComponentModel.Description("Repeatable. Roles composed into this one — how the shipped roles are actually built.")]
        public string[] SubRoles { get; init; } = Array.Empty<string>();

        [CommandOption("--context-string <TEXT>")]
        [System.ComponentModel.Description("ContextString the role-assignment rules match on.")]
        public string? ContextString { get; init; }

        [CommandOption("--disabled")]
        [System.ComponentModel.Description("Emit Enabled=No — the role ships switched off.")]
        public bool Disabled { get; init; }

        [CommandOption("--no-delete-from-ui")]
        [System.ComponentModel.Description("Emit CanBeDeletedFromUI=No — administrators cannot remove the role.")]
        public bool NoDeleteFromUI { get; init; }
    }

    public override int Execute(CommandContext ctx, Settings settings)
    {
        var kind = OutputMode.Resolve(settings.Output);

        if (!string.IsNullOrWhiteSpace(settings.AddTo))
            return ExecuteAddTo(kind, settings);

        if (string.IsNullOrWhiteSpace(settings.Name))
            return RenderHelpers.Render(kind, ToolResult<object>.Fail(D365FoErrorCodes.BadInput, "Role name required."));
        if (settings.Duties.Length == 0 && settings.Privileges.Length == 0 && settings.SubRoles.Length == 0)
            return RenderHelpers.Render(kind, ToolResult<object>.Fail(
                D365FoErrorCodes.BadInput,
                "At least one --duty, --privilege or --sub-role required."));

        var hasInstall = !string.IsNullOrWhiteSpace(settings.InstallTo);
        var hasOut = !string.IsNullOrWhiteSpace(settings.Out);
        if (!hasInstall && !hasOut)
            return RenderHelpers.Render(kind, ToolResult<object>.Fail(D365FoErrorCodes.BadInput, "--out or --install-to is required."));
        var outPath = settings.Out;
        if (hasInstall && !hasOut)
        {
            outPath = GenerateInstaller.ResolveInstallPath(kind, Folders.SecurityRole, settings.Name, settings.InstallTo!, out var fail);
            if (fail.HasValue) return fail.Value;
        }

        var doc = XppScaffolder.Role(
            settings.Name, settings.Duties, settings.Privileges, settings.Label, settings.Description,
            subRoles: settings.SubRoles,
            contextString: settings.ContextString,
            enabled: settings.Disabled ? false : null,
            canBeDeletedFromUI: settings.NoDeleteFromUI ? false : null);
        try
        {
            // Grounding gate (issue #161): uniform across every generate subcommand.
            var gate = GenerateInstaller.Gate(settings, settings.Name, doc);
            if (gate.Failure is not null) return RenderHelpers.Render(kind, gate.Failure);

            var res = GenerateInstaller.Write(gate, doc, outPath!, settings.Overwrite);
            return GenerateInstaller.Done(kind, gate, settings, new
            {
                kind = "AxSecurityRole",
                name = settings.Name,
                dutyCount = settings.Duties.Length,
                privilegeCount = settings.Privileges.Length,
                duties = settings.Duties,
                privileges = settings.Privileges,
                path = res.Path,
                bytes = res.Bytes,
                backup = res.BackupPath,
                model = settings.InstallTo,
            });
        }
        catch (Exception ex)
        {
            return RenderHelpers.Render(kind, ToolResult<object>.Fail(D365FoErrorCodes.WriteFailed, ex.Message));
        }
    }

    private static int ExecuteAddTo(OutputMode.Kind kind, Settings settings)
    {
        var path = settings.AddTo!;
        if (!System.IO.File.Exists(path))
            return RenderHelpers.Render(kind, ToolResult<object>.Fail(D365FoErrorCodes.BadInput, $"Role file not found: {path}"));
        if (settings.Duties.Length == 0 && settings.Privileges.Length == 0)
            return RenderHelpers.Render(kind, ToolResult<object>.Fail(
                D365FoErrorCodes.BadInput,
                "At least one --duty or --privilege required when using --add-to."));

        try
        {
            var doc = System.Xml.Linq.XDocument.Load(path);
            var changed = XppScaffolder.AddToRole(doc, settings.Duties, settings.Privileges);
            if (!changed)
            {
                return GenerateInstaller.Done(kind, gate: null, settings, new
                {
                    kind = "AxSecurityRole",
                    path,
                    changed = false,
                    note = "All supplied duties / privileges were already referenced.",
                });
            }

            var tmp = path + ".tmp";
            using (var fs = System.IO.File.Create(tmp))
                doc.Save(fs);
            var backup = path + ".bak";
            if (System.IO.File.Exists(backup)) System.IO.File.Delete(backup);
            System.IO.File.Move(path, backup);
            System.IO.File.Move(tmp, path);

            return GenerateInstaller.Done(kind, gate: null, settings, new
            {
                kind = "AxSecurityRole",
                path,
                changed = true,
                backup,
                addedDuties = settings.Duties,
                addedPrivileges = settings.Privileges,
            });
        }
        catch (Exception ex)
        {
            return RenderHelpers.Render(kind, ToolResult<object>.Fail(D365FoErrorCodes.WriteFailed, ex.Message));
        }
    }
}

/// <summary>
/// Scaffolds an <c>AxReport</c> + matching <c>SrsReportDataProviderBase</c> AxClass.
/// Mirrors upstream MCP <c>generate_smart_report</c>. ROADMAP §P3.
/// </summary>
public sealed class GenerateReportCommand : Command<GenerateReportCommand.Settings>
{
    public sealed class Settings : GenerateSettings
    {
        [CommandArgument(0, "<NAME>")]
        [System.ComponentModel.Description("AOT report name, e.g. FleetVehicleReport.")]
        public string Name { get; init; } = "";

        [CommandOption("--dp <CLASS>")]
        [System.ComponentModel.Description("Primary data provider class name. Defaults to <Name>DP.")]
        public string? DpClass { get; init; }

        [CommandOption("--tmp <TABLE>")]
        [System.ComponentModel.Description("Temp table class name used by the primary DP. Defaults to <Name>Tmp.")]
        public string? TmpTable { get; init; }

        [CommandOption("--dataset <NAME>")]
        [System.ComponentModel.Description("Primary dataset name inside the report. Defaults to <Name>DS.")]
        public string? DatasetName { get; init; }

        [CommandOption("--caption <TEXT>")]
        public string? Caption { get; init; }

        [CommandOption("--field <FIELD>")]
        [System.ComponentModel.Description("Tablix column field name (repeatable). Generates header + data rows in the tablix.")]
        public string[]? Fields { get; init; }

        [CommandOption("--parameter <SPEC>")]
        [System.ComponentModel.Description("Report parameter (repeatable). Format: Name or Name:Type. Type: String (default), Integer, DateTime, Boolean, Decimal.")]
        public string[]? Parameters { get; init; }

        [CommandOption("--extra-dataset <SPEC>")]
        [System.ComponentModel.Description("Additional dataset (repeatable). Format: DatasetName:DPClassName[[:Field1,Field2]]. Each produces its own table data region; fields default to --field.")]
        public string[]? ExtraDatasets { get; init; }

        [CommandOption("--out-dp <PATH>")]
        [System.ComponentModel.Description("Output path for the primary DP class XML. Defaults to sibling of --out named <DpClass>.xml.")]
        public string? OutDp { get; init; }

        [CommandOption("--out-contract <PATH>")]
        [System.ComponentModel.Description("Output path for the DataContract class XML. Auto-derived when --parameter is used.")]
        public string? OutContract { get; init; }

        [CommandOption("--pre-process")]
        [System.ComponentModel.Description("DP extends SrsReportDataProviderPreProcessTempDB: rows are written to the TempDB table BEFORE SSRS renders — the route for heavy computations. PreProcessTempDB is the base shipped code pairs a TempDB table with.")]
        public bool PreProcess { get; init; }

        [CommandOption("--controller-type <TYPE>")]
        [System.ComponentModel.Description("simple (default, SrsReportRunController) | print-mgmt (SrsPrintMgmtController with the abstract runPrintMgmt() implemented and initPrintMgmtReportRun() placeholders — there is no parmPrintMgmtDocType).")]
        public string? ControllerType { get; init; }

        [CommandOption("--ui-builder")]
        [System.ComponentModel.Description("Also generate <Name>UIBuilder (extends SrsReportDataContractUIBuilder), bound on the contract via SysOperationContractProcessing — for custom dialog lookups and dependent fields. Requires --parameter (the binding lives on the contract).")]
        public bool UiBuilder { get; init; }

        [CommandOption("--no-tmp-table")]
        [System.ComponentModel.Description("Skip the TempDB table each DP fills. The DP references it either way, so the model will not compile without one.")]
        public bool NoTmpTable { get; init; }

        [CommandOption("--no-controller")]
        [System.ComponentModel.Description("Skip the SrsReportRunController class.")]
        public bool NoController { get; init; }

        [CommandOption("--no-menu-item")]
        [System.ComponentModel.Description("Skip the output menu item that opens the report.")]
        public bool NoMenuItem { get; init; }
    }

    public override int Execute(CommandContext ctx, Settings settings)
    {
        var kind = OutputMode.Resolve(settings.Output);
        if (string.IsNullOrWhiteSpace(settings.Name))
            return RenderHelpers.Render(kind, ToolResult<object>.Fail(D365FoErrorCodes.BadInput, "Report name required."));

        var hasInstall = !string.IsNullOrWhiteSpace(settings.InstallTo);
        var hasOut     = !string.IsNullOrWhiteSpace(settings.Out);
        if (!hasInstall && !hasOut)
            return RenderHelpers.Render(kind, ToolResult<object>.Fail(D365FoErrorCodes.BadInput, "--out or --install-to is required."));

        // --- Parse extra datasets ---
        List<ReportDatasetSpec>? extraDatasets = null;
        if (settings.ExtraDatasets is { Length: > 0 })
        {
            extraDatasets = [];
            foreach (var raw in settings.ExtraDatasets)
            {
                var parts = raw.Split(':', 3);
                if (parts.Length < 2 || string.IsNullOrWhiteSpace(parts[1]))
                    return RenderHelpers.Render(kind, ToolResult<object>.Fail(D365FoErrorCodes.BadInput,
                        $"--extra-dataset '{raw}' must be in Name:DPClass[[:field,field]] format."));

                // A dataset with no fields is rejected outright ("The dataset X has no
                // fields specified"), and its table data region with it ("Missing Data
                // fields"). Its temp table would be empty too. Falling back to the
                // report's own --field list keeps the dataset, the temp table, the DP
                // getter and the design region describing the same columns.
                var fields = parts.Length > 2 && !string.IsNullOrWhiteSpace(parts[2])
                    ? parts[2].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    : settings.Fields;

                if (fields is not { Length: > 0 })
                    return RenderHelpers.Render(kind, ToolResult<object>.Fail(D365FoErrorCodes.BadInput,
                        $"--extra-dataset '{parts[0]}' has no fields.",
                        "Give them inline (Name:DPClass:Field1,Field2) or pass --field, which extra datasets inherit."));

                extraDatasets.Add(new ReportDatasetSpec(parts[0], parts[1], fields));
            }
        }

        // --- Parse parameters ---
        List<ReportParameterSpec>? paramSpecs = null;
        if (settings.Parameters is { Length: > 0 })
        {
            paramSpecs = settings.Parameters.Select(p =>
            {
                var parts = p.Split(':', 2);
                return new ReportParameterSpec(parts[0], parts.Length > 1 ? parts[1] : "String");
            }).ToList();
        }

        var controllerType = (settings.ControllerType ?? "simple").Trim().ToLowerInvariant();
        if (controllerType is not ("simple" or "print-mgmt" or "printmgmt"))
            return RenderHelpers.Render(kind, ToolResult<object>.Fail(D365FoErrorCodes.BadInput,
                $"Unknown --controller-type '{settings.ControllerType}'. Expected simple | print-mgmt."));
        var printMgmt = controllerType is "print-mgmt" or "printmgmt";
        if (printMgmt && settings.NoController)
            return RenderHelpers.Render(kind, ToolResult<object>.Fail(D365FoErrorCodes.BadInput,
                "--controller-type print-mgmt and --no-controller contradict each other."));
        if (settings.UiBuilder && (settings.Parameters is not { Length: > 0 }))
            return RenderHelpers.Render(kind, ToolResult<object>.Fail(D365FoErrorCodes.BadInput,
                "--ui-builder requires --parameter: the builder is bound on the DataContract via " +
                "SysOperationContractProcessing, and without parameters no contract is generated.",
                "Add at least one --parameter, or drop --ui-builder."));

        var spec = new ReportSpec(
            settings.Name,
            settings.DpClass,
            settings.TmpTable,
            settings.DatasetName,
            settings.Caption,
            extraDatasets is { Count: > 0 }
                ? [new ReportDatasetSpec(
                    string.IsNullOrWhiteSpace(settings.DatasetName) ? settings.Name + "DS" : settings.DatasetName,
                    string.IsNullOrWhiteSpace(settings.DpClass)     ? settings.Name + "DP" : settings.DpClass,
                    settings.Fields), .. extraDatasets]
                : null,
            settings.Fields,
            paramSpecs)
        {
            PreProcess = settings.PreProcess,
            PrintMgmtController = printMgmt,
            UiBuilder = settings.UiBuilder,
        };

        var hasContract = spec.Parameters is { Count: > 0 };

        // --- Resolve output paths ---
        string? reportPath, dpPath, contractPath;
        if (hasInstall && !hasOut)
        {
            reportPath = GenerateInstaller.ResolveInstallPath(kind, Folders.Report, settings.Name, settings.InstallTo!, out var f1);
            if (f1.HasValue) return f1.Value;

            if (!string.IsNullOrWhiteSpace(settings.OutDp))
            {
                dpPath = settings.OutDp;
            }
            else
            {
                dpPath = GenerateInstaller.ResolveInstallPath(kind, Folders.Class, spec.EffectiveDpClass, settings.InstallTo!, out var f2);
                if (f2.HasValue) return f2.Value;
            }

            if (hasContract)
            {
                if (!string.IsNullOrWhiteSpace(settings.OutContract))
                {
                    contractPath = settings.OutContract;
                }
                else
                {
                    contractPath = GenerateInstaller.ResolveInstallPath(kind, Folders.Class, spec.ContractClass, settings.InstallTo!, out var f3);
                    if (f3.HasValue) return f3.Value;
                }
            }
            else contractPath = null;
        }
        else
        {
            var dir = System.IO.Path.GetDirectoryName(settings.Out!)!;
            reportPath   = settings.Out!;
            dpPath       = settings.OutDp ?? System.IO.Path.Combine(dir, spec.EffectiveDpClass + ".xml");
            contractPath = hasContract
                ? (settings.OutContract ?? System.IO.Path.Combine(dir, spec.ContractClass + ".xml"))
                : null;
        }

        var dpDoc = XppScaffolder.ReportDp(spec);

        // Grounding gate (issue #161) — the DP class carries the report's X++.
        var gate = GenerateInstaller.Gate(settings, spec.Name, dpDoc);
        if (gate.Failure is not null) return RenderHelpers.Render(kind, gate.Failure);

        try
        {
            var reportResult   = GenerateInstaller.Write(gate, XppScaffolder.Report(spec), reportPath!, settings.Overwrite);
            var dpResult       = GenerateInstaller.Write(gate, dpDoc,                      dpPath!,     settings.Overwrite);

            ScaffoldFileWriter.WriteResult? contractResult = null;
            if (hasContract && contractPath is not null)
            {
                var contractDoc = XppScaffolder.ReportContract(spec);
                if (contractDoc is not null)
                    contractResult = GenerateInstaller.Write(gate, contractDoc, contractPath, settings.Overwrite);
            }

            // The rest of the stack. A report is not one object: the DP declares and selects
            // from a TempDB table, and something has to open the report — previously neither
            // was generated, so the very first build failed on a table that did not exist.
            string SiblingOf(string basePath, string objectName, string axFolder)
                => hasInstall && !hasOut
                    ? GenerateInstaller.ResolveInstallPath(kind, axFolder, objectName, settings.InstallTo!, out _)!
                    : System.IO.Path.Combine(System.IO.Path.GetDirectoryName(basePath)!, objectName + ".xml");

            var tmpTables = new List<object>();
            if (!settings.NoTmpTable)
            {
                foreach (var ds in spec.EffectiveDatasets)
                {
                    var tmpName = ds.DpClass + "Tmp";
                    var written = GenerateInstaller.Write(gate,
                        XppScaffolder.ReportTmpTable(ds), SiblingOf(dpPath!, tmpName, Folders.Table), settings.Overwrite);
                    tmpTables.Add(new { name = tmpName, path = written.Path, bytes = written.Bytes });
                }
            }

            ScaffoldFileWriter.WriteResult? controllerResult = null;
            if (!settings.NoController)
            {
                controllerResult = GenerateInstaller.Write(gate,
                    XppScaffolder.ReportController(spec),
                    SiblingOf(dpPath!, spec.EffectiveController, Folders.Class),
                    settings.Overwrite);
            }

            ScaffoldFileWriter.WriteResult? uiBuilderResult = null;
            if (spec.UiBuilder && XppScaffolder.ReportUiBuilder(spec) is { } uiBuilderDoc)
            {
                uiBuilderResult = GenerateInstaller.Write(gate,
                    uiBuilderDoc,
                    SiblingOf(dpPath!, spec.UiBuilderClass, Folders.Class),
                    settings.Overwrite);
            }

            ScaffoldFileWriter.WriteResult? menuItemResult = null;
            if (!settings.NoMenuItem)
            {
                // The menu item opens the controller when there is one, and the report directly
                // otherwise — pointing at a class that was not generated would be a dangling ref.
                var target = settings.NoController ? spec.Name : spec.EffectiveController;
                var targetType = settings.NoController ? MenuItemObjectType.Report : MenuItemObjectType.Class;

                menuItemResult = GenerateInstaller.Write(gate,
                    MenuItemScaffolder.MenuItem(
                        MenuItemKind.Output, spec.EffectiveMenuItem, target, targetType, spec.Caption),
                    SiblingOf(reportPath!, spec.EffectiveMenuItem, Folders.MenuItemOutput),
                    settings.Overwrite);
            }

            return GenerateInstaller.Done(kind, gate, settings, new
            {
                kind        = "AxReport",
                name        = spec.Name,
                dpClass     = spec.EffectiveDpClass,
                dpBase      = spec.PreProcess ? "SrsReportDataProviderPreProcessTempDB" : "SrsReportDataProviderBase",
                controllerType = settings.NoController ? null : (spec.PrintMgmtController ? "print-mgmt" : "simple"),
                contractClass = spec.ContractClass,
                datasets    = spec.EffectiveDatasets.Select(ds => new { ds.Name, ds.DpClass }).ToList(),
                parameters  = spec.Parameters?.Select(p => new { p.Name, p.DataType }).ToList(),
                report      = new { path = reportResult.Path,   bytes = reportResult.Bytes,   backup = reportResult.BackupPath },
                dp          = new { path = dpResult.Path,       bytes = dpResult.Bytes,       backup = dpResult.BackupPath },
                tmpTables,
                controller  = controllerResult is null ? null : new { name = spec.EffectiveController, path = controllerResult.Path, bytes = controllerResult.Bytes },
                uiBuilder   = uiBuilderResult is null ? null : new { name = spec.UiBuilderClass, path = uiBuilderResult.Path, bytes = uiBuilderResult.Bytes },
                menuItem    = menuItemResult is null ? null : new { name = spec.EffectiveMenuItem, path = menuItemResult.Path, bytes = menuItemResult.Bytes },
                contract    = contractResult is null ? null : new { path = contractResult.Path, bytes = contractResult.Bytes, backup = contractResult.BackupPath },
                model       = settings.InstallTo,
                grounding   = gate.Grounding,
            });
        }
        catch (Exception ex)
        {
            return RenderHelpers.Render(kind, ToolResult<object>.Fail(D365FoErrorCodes.WriteFailed, ex.Message));
        }
    }
}

/// <summary>
/// Idempotent &quot;add duty / privilege reference into an existing role&quot; merge,
/// used by <c>generate role --add-to</c> and by the <c>--into-role</c> flag on
/// <c>generate duty</c> / <c>generate privilege</c>. Loads the role XML,
/// merges via <see cref="XppScaffolder.AddToRole"/>, and writes atomically
/// with a <c>.bak</c> sibling.
/// </summary>
internal static class SecurityRoleMerge
{
    public static bool AddReferences(string path, string[]? duties, string[]? privileges, out object result, out string? error)
    {
        result = null!;
        if (!System.IO.File.Exists(path))
        {
            error = $"Role file not found: {path}";
            return false;
        }
        System.Xml.Linq.XDocument doc;
        try { doc = System.Xml.Linq.XDocument.Load(path); }
        catch (Exception ex) { error = $"Failed to parse role XML: {ex.Message}"; return false; }

        bool changed;
        try { changed = D365FO.Core.Scaffolding.XppScaffolder.AddToRole(doc, duties, privileges); }
        catch (InvalidOperationException ex) { error = ex.Message; return false; }

        if (!changed)
        {
            result = new { path, changed = false, note = "All supplied duties / privileges were already referenced." };
            error = null;
            return true;
        }

        try
        {
            var tmp = path + ".tmp";
            using (var fs = System.IO.File.Create(tmp)) doc.Save(fs);
            var backup = path + ".bak";
            if (System.IO.File.Exists(backup)) System.IO.File.Delete(backup);
            System.IO.File.Move(path, backup);
            System.IO.File.Move(tmp, path);
            result = new
            {
                path,
                changed = true,
                backup,
                addedDuties = duties ?? Array.Empty<string>(),
                addedPrivileges = privileges ?? Array.Empty<string>(),
            };
            error = null;
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }
}
