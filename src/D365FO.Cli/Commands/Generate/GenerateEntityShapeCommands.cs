using System.Xml.Linq;
using D365FO.Core;
using D365FO.Core.Scaffolding;
using Spectre.Console.Cli;

using static D365FO.Core.ObjectTypes.ObjectTypeRegistry;

namespace D365FO.Cli.Commands.Generate;

/// <summary>Scaffolds an <c>AxCompositeDataEntityView</c>: a header/lines bundle of existing entities.</summary>
public sealed class GenerateCompositeEntityCommand : Command<GenerateCompositeEntityCommand.Settings>
{
    public sealed class Settings : GenerateSettings
    {
        [CommandArgument(0, "<NAME>")]
        [System.ComponentModel.Description("Composite entity name.")]
        public string Name { get; init; } = "";

        [CommandOption("--root <SPEC>")]
        [System.ComponentModel.Description("Repeatable root entity: <dataEntity>[[:<referenceName>]]. The reference name defaults to the entity name. Example: --root FMCustomerEntity")]
        public string[] Roots { get; init; } = Array.Empty<string>();

        [CommandOption("--embedded <SPEC>")]
        [System.ComponentModel.Description("Repeatable embedded entity: <dataEntity>:<relation>[[:<parentReference>[[:<referenceName>]]]]. The relation is the one on the child entity that binds it to its parent; the parent defaults to the first root. Example: --embedded FMRentalEntity:FMCustomer")]
        public string[] Embedded { get; init; } = Array.Empty<string>();

        [CommandOption("--label <KEY>")]
        public string? Label { get; init; }

        [CommandOption("--tags <TEXT>")]
        public string? Tags { get; init; }

        [CommandOption("--modules <MODULES>")]
        [System.ComponentModel.Description("Module(s) the entity is filed under, e.g. AccountsReceivable.")]
        public string? Modules { get; init; }

        [CommandOption("--entity-category <CATEGORY>")]
        [System.ComponentModel.Description("Document | Reference | Transaction | Master | Parameters.")]
        public string? EntityCategory { get; init; }
    }

    public override int Execute(CommandContext ctx, Settings settings)
    {
        var kind = OutputMode.Resolve(settings.Output);
        if (string.IsNullOrWhiteSpace(settings.Name))
            return RenderHelpers.Render(kind, ToolResult<object>.Fail(D365FoErrorCodes.BadInput, "Composite entity name required."));
        if (settings.Roots.Length == 0)
            return RenderHelpers.Render(kind, ToolResult<object>.Fail(D365FoErrorCodes.BadInput,
                "At least one --root <dataEntity> required.",
                hint: "A composite entity bundles EXISTING data entities; generate them first with `d365fo generate entity`."));

        // Build the tree: roots first, then each embedded entity under its named parent.
        var nodes = new Dictionary<string, (string Entity, string? Relation, List<CompositeEntityReferenceSpec> Children)>(StringComparer.OrdinalIgnoreCase);
        var rootOrder = new List<string>();
        foreach (var raw in settings.Roots)
        {
            var parts = raw.Split(':', 2, StringSplitOptions.TrimEntries);
            if (string.IsNullOrEmpty(parts[0]))
                return RenderHelpers.Render(kind, ToolResult<object>.Fail(D365FoErrorCodes.BadInput, $"Invalid --root '{raw}'. Expected <dataEntity>[:<referenceName>]."));
            var refName = parts.Length > 1 && parts[1].Length > 0 ? parts[1] : parts[0];
            if (nodes.ContainsKey(refName))
                return RenderHelpers.Render(kind, ToolResult<object>.Fail(D365FoErrorCodes.BadInput, $"Reference name '{refName}' is used twice."));
            nodes[refName] = (parts[0], null, new List<CompositeEntityReferenceSpec>());
            rootOrder.Add(refName);
        }

        // Embedded specs may reference a parent declared later in the same list, so resolve
        // parents after every name is known.
        var embedded = new List<(string RefName, string Entity, string Relation, string Parent)>();
        foreach (var raw in settings.Embedded)
        {
            var parts = raw.Split(':', 4, StringSplitOptions.TrimEntries);
            if (parts.Length < 2 || string.IsNullOrEmpty(parts[0]) || string.IsNullOrEmpty(parts[1]))
                return RenderHelpers.Render(kind, ToolResult<object>.Fail(D365FoErrorCodes.BadInput,
                    $"Invalid --embedded '{raw}'. Expected <dataEntity>:<relation>[:<parentReference>[:<referenceName>]]."));
            var parent = parts.Length > 2 && parts[2].Length > 0 ? parts[2] : rootOrder[0];
            var refName = parts.Length > 3 && parts[3].Length > 0 ? parts[3] : parts[0];
            if (nodes.ContainsKey(refName))
                return RenderHelpers.Render(kind, ToolResult<object>.Fail(D365FoErrorCodes.BadInput, $"Reference name '{refName}' is used twice."));
            nodes[refName] = (parts[0], parts[1], new List<CompositeEntityReferenceSpec>());
            embedded.Add((refName, parts[0], parts[1], parent));
        }
        foreach (var e in embedded)
        {
            if (!nodes.ContainsKey(e.Parent))
                return RenderHelpers.Render(kind, ToolResult<object>.Fail(D365FoErrorCodes.BadInput,
                    $"--embedded '{e.Entity}' names parent '{e.Parent}', which is neither a root nor an embedded reference."));
        }

        CompositeEntityReferenceSpec Build(string refName)
        {
            var n = nodes[refName];
            var children = embedded.Where(e => string.Equals(e.Parent, refName, StringComparison.OrdinalIgnoreCase))
                .Select(e => Build(e.RefName)).ToList();
            return new CompositeEntityReferenceSpec(refName, n.Entity, n.Relation, children);
        }

        XDocument doc;
        try
        {
            doc = EntityShapeScaffolder.CompositeDataEntityView(settings.Name, rootOrder.Select(Build),
                settings.Label, settings.Tags, settings.Modules, settings.EntityCategory);
        }
        catch (ArgumentException ex)
        {
            return RenderHelpers.Render(kind, ToolResult<object>.Fail(D365FoErrorCodes.BadInput, ex.Message));
        }

        if (!GenerateViewCommand.TryResolveOutPath(kind, settings, Folders.CompositeDataEntityView, settings.Name, out var outPath, out var pathFailure))
            return pathFailure;

        try
        {
            // Every referenced entity must exist; the composite is nothing but references.
            var gate = GenerateInstaller.Gate(settings, settings.Name, doc,
                requiredSymbols: nodes.Values.Select(n => n.Entity).Distinct(StringComparer.OrdinalIgnoreCase));
            if (gate.Failure is not null) return RenderHelpers.Render(kind, gate.Failure);

            var res = GenerateInstaller.Write(gate, doc, outPath!, settings.Overwrite);
            return GenerateInstaller.Done(kind, gate, settings, new
            {
                kind = "AxCompositeDataEntityView",
                name = settings.Name,
                roots = rootOrder.Select(r => nodes[r].Entity),
                embedded = embedded.Select(e => new { entity = e.Entity, relation = e.Relation, under = e.Parent }),
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
}

/// <summary>Scaffolds an <c>AxAggregateDataEntity</c> over an aggregate measurement.</summary>
public sealed class GenerateAggregateEntityCommand : Command<GenerateAggregateEntityCommand.Settings>
{
    public sealed class Settings : GenerateSettings
    {
        [CommandArgument(0, "<NAME>")]
        [System.ComponentModel.Description("Aggregate entity name.")]
        public string Name { get; init; } = "";

        [CommandOption("--measurement <NAME>")]
        [System.ComponentModel.Description("AxAggregateMeasurement the entity projects. Required.")]
        public string? Measurement { get; init; }

        [CommandOption("--measure <SPEC>")]
        [System.ComponentModel.Description("Repeatable measure field: <field>:<measureGroup>:<measure>:<edt>. Example: --measure NoRentals:FMRentalCharges:NoRentals:BIRCount")]
        public string[] Measures { get; init; } = Array.Empty<string>();

        [CommandOption("--dimension <SPEC>")]
        [System.ComponentModel.Description("Repeatable dimension field: <field>:<measureGroup>:<dimension>:<attribute>:<edt>. Example: --dimension VehicleColor:FMRentalCharges:FMVehicle:VehicleColor:FMColorName")]
        public string[] Dimensions { get; init; } = Array.Empty<string>();

        [CommandOption("--label <KEY>")]
        public string? Label { get; init; }
    }

    public override int Execute(CommandContext ctx, Settings settings)
    {
        var kind = OutputMode.Resolve(settings.Output);
        if (string.IsNullOrWhiteSpace(settings.Name))
            return RenderHelpers.Render(kind, ToolResult<object>.Fail(D365FoErrorCodes.BadInput, "Aggregate entity name required."));
        if (string.IsNullOrWhiteSpace(settings.Measurement))
            return RenderHelpers.Render(kind, ToolResult<object>.Fail(D365FoErrorCodes.BadInput,
                "--measurement <NAME> required.",
                hint: "An aggregate entity projects an existing AxAggregateMeasurement; its measure groups, measures and dimensions are what the fields map onto."));
        if (settings.Measures.Length == 0 && settings.Dimensions.Length == 0)
            return RenderHelpers.Render(kind, ToolResult<object>.Fail(D365FoErrorCodes.BadInput, "At least one --measure or --dimension required."));

        var fields = new List<AggregateEntityFieldSpec>();
        foreach (var raw in settings.Measures)
        {
            var p = raw.Split(':', StringSplitOptions.TrimEntries);
            if (p.Length != 4 || p.Any(string.IsNullOrEmpty))
                return RenderHelpers.Render(kind, ToolResult<object>.Fail(D365FoErrorCodes.BadInput,
                    $"Invalid --measure '{raw}'. Expected <field>:<measureGroup>:<measure>:<edt>."));
            fields.Add(new AggregateEntityFieldSpec(p[0], p[1], p[3], Measure: p[2]));
        }
        foreach (var raw in settings.Dimensions)
        {
            var p = raw.Split(':', StringSplitOptions.TrimEntries);
            if (p.Length != 5 || p.Any(string.IsNullOrEmpty))
                return RenderHelpers.Render(kind, ToolResult<object>.Fail(D365FoErrorCodes.BadInput,
                    $"Invalid --dimension '{raw}'. Expected <field>:<measureGroup>:<dimension>:<attribute>:<edt>."));
            fields.Add(new AggregateEntityFieldSpec(p[0], p[1], p[4], Dimension: p[2], Attribute: p[3]));
        }

        XDocument doc;
        try { doc = EntityShapeScaffolder.AggregateDataEntity(settings.Name, settings.Measurement!, fields, settings.Label); }
        catch (ArgumentException ex)
        {
            return RenderHelpers.Render(kind, ToolResult<object>.Fail(D365FoErrorCodes.BadInput, ex.Message));
        }

        if (!GenerateViewCommand.TryResolveOutPath(kind, settings, Folders.AggregateDataEntity, settings.Name, out var outPath, out var pathFailure))
            return pathFailure;

        try
        {
            // The EDTs typing the fields are AOT objects the index knows. The measurement,
            // its groups, measures and attributes are not indexed, so they are reported as
            // unverified rather than pretended checked.
            var gate = GenerateInstaller.Gate(settings, settings.Name, doc,
                requiredSymbols: fields.Select(f => f.ExtendedDataType).Distinct(StringComparer.OrdinalIgnoreCase));
            if (gate.Failure is not null) return RenderHelpers.Render(kind, gate.Failure);

            var res = GenerateInstaller.Write(gate, doc, outPath!, settings.Overwrite);
            return GenerateInstaller.Done(kind, gate, settings, new
            {
                kind = "AxAggregateDataEntity",
                name = settings.Name,
                measurement = settings.Measurement,
                measures = fields.Count(f => f.IsMeasure),
                dimensions = fields.Count(f => !f.IsMeasure),
                path = res.Path,
                bytes = res.Bytes,
                backup = res.BackupPath,
                model = settings.InstallTo,
            }, new[]
            {
                $"The measurement '{settings.Measurement}', its measure groups, measures and dimension attributes are not in the index and were not verified; "
                + "the build is what proves them. Run `d365fo build` (or `d365fo oracle probe` on this file) before relying on the entity.",
            });
        }
        catch (Exception ex)
        {
            return RenderHelpers.Render(kind, ToolResult<object>.Fail(D365FoErrorCodes.WriteFailed, ex.Message));
        }
    }
}
