// <copyright file="ObjectModifyEngine.cs" company="d365fo-cli contributors">
// MIT
// </copyright>

using System.Text.Json.Nodes;
using System.Xml.Linq;
using D365FO.Core.FormPatterns;
using D365FO.Core.Index;
using D365FO.Core.Journal;
using D365FO.Core.ObjectTypes;
using D365FO.Core.Scaffolding;

namespace D365FO.Core.Bridge;

/// <summary>
/// Structured, live edits to an existing AOT object beyond a method body:
/// <c>d365fo modify property</c>, <c>modify add-field</c>, <c>modify add-enum-value</c>,
/// and <c>modify add-control</c>.
///
/// <para>Same contract as <see cref="MethodModifyEngine"/> — read the live object as XML
/// through the bridge, edit the parsed <see cref="XDocument"/> with element navigation
/// (never string surgery), write it back through <c>IMetadataProvider</c>, and never fall
/// back to touching on-disk XML. Two things it adds:</para>
///
/// <list type="number">
/// <item><description><b>Extension fallback.</b> The base object usually belongs to a
/// Microsoft or ISV model that must not be modified in place. When the target resolves to
/// a model outside <c>D365FO_CUSTOM_MODELS</c> — or when the caller asks for it —
/// the write is redirected to the <c>&lt;Target&gt;.&lt;Suffix&gt;</c> extension object in a
/// custom model, creating that extension if it does not exist yet. That is what the
/// bridge's new <c>tableExtension</c>/<c>formExtension</c>/… kinds are
/// for.</description></item>
/// <item><description><b>Journaling.</b> Every write records its exact pre-image in the
/// modification journal, so <c>d365fo undo</c> reverts it. <c>modify method</c> was the
/// one write path in the CLI that never did this — it captured the pre-image XML for the
/// edit and then discarded it, leaving the edit un-undoable. It now journals through the
/// same helper as everything here.</description></item>
/// </list>
/// </summary>
public static class ObjectModifyEngine
{
    /// <summary>What a caller wants changed.</summary>
    public enum Operation
    {
        /// <summary>Set (or add) a simple property element on the object root.</summary>
        SetProperty,

        /// <summary>Add a field to a table.</summary>
        AddField,

        /// <summary>Add a value to a base enum.</summary>
        AddEnumValue,

        /// <summary>Add a control to a form's Design tree.</summary>
        AddControl,
    }

    /// <summary>Inputs for one structured modification.</summary>
    public sealed record ModifyRequest
    {
        public required Operation Operation { get; init; }

        /// <summary>Base object kind: class | table | edt | enum | form.</summary>
        public required string Kind { get; init; }

        /// <summary>Base object name — always the base object, never the extension.</summary>
        public required string ObjectName { get; init; }

        /// <summary>Member being added/changed: property name, field name, enum value, or control name.</summary>
        public required string Member { get; init; }

        /// <summary>Value for <see cref="Operation.SetProperty"/>.</summary>
        public string? Value { get; init; }

        /// <summary>EDT for <see cref="Operation.AddField"/>; normalized control type for <see cref="Operation.AddControl"/>.</summary>
        public string? Type { get; init; }

        public string? Label { get; init; }

        /// <summary><see cref="Operation.AddField"/>: mark the field mandatory.</summary>
        public bool Mandatory { get; init; }

        /// <summary><see cref="Operation.AddControl"/>: name of the container to add into. Defaults to the Design root.</summary>
        public string? Parent { get; init; }

        /// <summary><see cref="Operation.AddControl"/>: datasource/field to bind to.</summary>
        public string? DataSource { get; init; }

        public string? DataField { get; init; }

        /// <summary>Owning model. Resolved from the index when omitted.</summary>
        public string? Model { get; init; }

        /// <summary>
        /// Force the extension path even when the base object is writable, and/or name the
        /// suffix. Null means "decide automatically"; a non-null value always extends.
        /// </summary>
        public string? ExtensionSuffix { get; init; }

        /// <summary>Model the extension is created in. Defaults to the first configured custom model.</summary>
        public string? ExtensionModel { get; init; }

        /// <summary>Never modify the base object in place; fail instead of falling back.</summary>
        public bool RequireExtension { get; init; }
    }

    private static readonly IReadOnlySet<string> SupportedKinds =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "class", "table", "edt", "enum", "form" };

    /// <summary>
    /// The AOT type that extends <paramref name="kind"/>, or null when there is none.
    /// </summary>
    /// <remarks>
    /// This used to be two hand-maintained dictionaries — one for the kind the bridge is
    /// asked for, one for the root element to scaffold — and they drifted from the
    /// registry the bridge itself resolves collections through, which is how a redirect
    /// to a perfectly writable <c>AxTableExtension</c> came back as INVALID_KIND (#171).
    /// One lookup, one source of truth. It also drops two wrong entries the tables
    /// carried: <c>edt</c> had no root at all and fell through to a mis-cased
    /// "AxedtExtension", and <c>query</c> named an <c>AxQueryExtension</c> type that no
    /// MetaModel assembly declares.
    /// </remarks>
    private static ObjectTypeInfo? ExtensionTypeFor(string kind)
        => ObjectTypeRegistry.ExtensionOf(kind);

    /// <summary>Entry point for the CLI and the MCP tool. Spawns the bridge and fails closed when it is absent.</summary>
    public static ToolResult<object> Modify(
        ModifyRequest request, MetadataRepository? repo, BridgeOptions? bridgeOptions = null)
    {
        var options = bridgeOptions ?? MethodModifyEngine.DefaultBridgeOptions();
        if (!BridgeClient.IsAvailable(options))
        {
            return ToolResult<object>.Fail(D365FoErrorCodes.BridgeRequired,
                "d365fo modify requires D365FO.Bridge (.NET Framework 4.8, Windows-only, IMetadataProvider-backed) — " +
                "it is not available in this environment (non-Windows OS, or the bridge executable could not be resolved).",
                "Run on a D365FO VM with D365FO_BRIDGE_ENABLED=1 and D365FO_BRIDGE_PATH / D365FO_PACKAGES_PATH set. " +
                "This command intentionally has no raw-XML fallback (see docs/MIGRATION_FROM_MCP.md).");
        }

        using var client = new BridgeClient(options);
        return ModifyCore(request, repo, client);
    }

    /// <summary>
    /// The bridge round-trip, decoupled from process spawning so tests can inject a fake
    /// client. <paramref name="journalDbOverride"/> redirects the journal to a specific
    /// index instead of the ambient one — a test seam, so verifying journaling never
    /// requires mutating process-wide environment variables (which leaks across xUnit's
    /// parallel test classes).
    /// </summary>
    internal static ToolResult<object> ModifyCore(
        ModifyRequest request, MetadataRepository? repo, BridgeClient client, string? journalDbOverride = null)
    {
        var kind = (request.Kind ?? string.Empty).Trim().ToLowerInvariant();
        var validation = ValidateRequest(request, kind);
        if (validation is not null) return validation;

        var warnings = new List<string>();

        // ---- 1. Resolve the owning model, then decide base-vs-extension ----
        var baseModel = request.Model ?? ResolveModel(kind, request.ObjectName, repo);
        if (string.IsNullOrWhiteSpace(baseModel))
        {
            return ToolResult<object>.Fail(NotFoundCodeFor(kind),
                $"{kind} '{request.ObjectName}' was not found in the SQLite index and no --model override was supplied.",
                "Run `d365fo index build` + `d365fo index extract`, or pass --model <MODEL> explicitly.");
        }

        var (target, planFailure) = PlanTarget(request, kind, baseModel!, repo, warnings);
        if (planFailure is not null) return planFailure;

        // ---- 2. Read the live object (or start a new extension) ----
        var (doc, preImage, readFailure) = ReadOrCreate(client, target!, request, kind);
        if (readFailure is not null) return readFailure;

        // ---- 3. Structured edit ----
        var (applied, editFailure) = ApplyOperation(doc!, request, kind, target!, repo);
        if (editFailure is not null) return editFailure;

        // ---- 4. Write back, journaling the pre-image first ----
        var newXml = doc!.ToString(SaveOptions.DisableFormatting);
        var verb = target!.Exists ? "updateObject" : "createObject";

        RecordJournalEntry(target, preImage, DescribeCommand(request, kind), journalDbOverride);

        JsonObject? writeResult;
        try
        {
            writeResult = client.SendAsync(verb, new JsonObject
            {
                ["kind"] = target.Kind,
                ["name"] = target.Name,
                ["model"] = target.Model,
                ["xml"] = newXml,
            }).GetAwaiter().GetResult();
        }
        catch (BridgeException ex)
        {
            return ToolResult<object>.Fail(D365FoErrorCodes.WriteFailed, "Bridge error while writing the object: " + ex.Message);
        }
        if (writeResult is null)
            return ToolResult<object>.Fail(D365FoErrorCodes.WriteFailed, $"Bridge returned no result for {verb}.");
        if ((bool?)writeResult["ok"] != true)
        {
            var code = (string?)writeResult["error"] ?? D365FoErrorCodes.WriteFailed;
            var msg = (string?)writeResult["message"] ?? "unknown error";
            return ToolResult<object>.Fail(code,
                $"Bridge could not write {target.Kind} '{target.Name}': {msg}",
                BridgeFailureHint(code, target));
        }

        warnings.Add($"Index not auto-refreshed — run `d365fo index refresh --model {target.Model}` so '{request.Member}' is searchable.");

        return ToolResult<object>.Success(new
        {
            operation = request.Operation.ToString(),
            kind,
            name = request.ObjectName,
            member = request.Member,
            wroteTo = new { kind = target.Kind, name = target.Name, model = target.Model, extension = target.IsExtension },
            created = !target.Exists,
            source = "bridge",
            applied,
        }, warnings);
    }

    /// <summary>
    /// What to tell the caller when the bridge refuses a read or a write.
    /// </summary>
    /// <remarks>
    /// INVALID_KIND used to be reported as "this platform build may not expose the
    /// collection", which sent #171 chasing a platform capability that was there all
    /// along. Both halves resolve kinds through the same <see cref="ObjectTypeRegistry"/>,
    /// so if this process planned a write to a kind the bridge does not recognise, the
    /// two binaries are from different builds — that is the thing to say.
    /// </remarks>
    private static string? BridgeFailureHint(string code, WriteTarget target)
    {
        if (string.Equals(code, "INVALID_KIND", StringComparison.Ordinal))
        {
            return $"The bridge does not recognise '{target.Kind}', but this build resolves it to the " +
                   $"{ObjectTypeRegistry.Find(target.Kind)?.ProviderCollection ?? "?"} provider collection — " +
                   "the bridge executable is from an older build. Rebuild it, or point D365FO_BRIDGE_PATH at a matching one.";
        }
        return target.IsExtension
            ? $"Check `d365fo doctor` and the bridge log for why {target.Kind} '{target.Name}' was rejected."
            : null;
    }

    // ---------------------------------------------------------------- validation

    private static ToolResult<object>? ValidateRequest(ModifyRequest request, string kind)
    {
        if (!SupportedKinds.Contains(kind))
        {
            return ToolResult<object>.Fail(D365FoErrorCodes.BadInput,
                $"Unsupported object kind '{request.Kind}'. Supported: {string.Join(", ", SupportedKinds)}.");
        }
        if (string.IsNullOrWhiteSpace(request.ObjectName))
            return ToolResult<object>.Fail(D365FoErrorCodes.BadInput, "Object name is required.");
        if (string.IsNullOrWhiteSpace(request.Member))
            return ToolResult<object>.Fail(D365FoErrorCodes.BadInput, "A member name is required.");

        return request.Operation switch
        {
            Operation.SetProperty when request.Value is null =>
                ToolResult<object>.Fail(D365FoErrorCodes.BadInput, "--value is required for `modify property`."),
            Operation.AddField when kind != "table" =>
                ToolResult<object>.Fail(D365FoErrorCodes.BadInput,
                    $"`modify add-field` applies to tables, not {kind}.",
                    "Use `modify add-enum-value` for enums or `modify add-control` for forms."),
            Operation.AddField when string.IsNullOrWhiteSpace(request.Type) =>
                ToolResult<object>.Fail(D365FoErrorCodes.BadInput, "--edt is required for `modify add-field`."),
            Operation.AddEnumValue when kind != "enum" =>
                ToolResult<object>.Fail(D365FoErrorCodes.BadInput, $"`modify add-enum-value` applies to enums, not {kind}."),
            Operation.AddControl when kind != "form" =>
                ToolResult<object>.Fail(D365FoErrorCodes.BadInput, $"`modify add-control` applies to forms, not {kind}."),
            Operation.AddControl when string.IsNullOrWhiteSpace(request.Type) =>
                ToolResult<object>.Fail(D365FoErrorCodes.BadInput,
                    "--type is required for `modify add-control` (Grid, Group, TabPage, String, …)."),
            _ => null,
        };
    }

    // ------------------------------------------------------------ target planning

    /// <summary>Where the write actually lands.</summary>
    internal sealed record WriteTarget(string Kind, string Name, string Model, bool IsExtension, bool Exists);

    /// <summary>
    /// Decide whether to edit the base object or an extension of it. The rule is
    /// deliberately conservative: extend unless the base object demonstrably lives in a
    /// model this installation owns, because writing into a Microsoft or ISV model is the
    /// mistake that is expensive to undo.
    /// </summary>
    private static (WriteTarget? Target, ToolResult<object>? Failure) PlanTarget(
        ModifyRequest request, string kind, string baseModel, MetadataRepository? repo, List<string> warnings)
    {
        var forced = request.ExtensionSuffix is not null || request.RequireExtension;
        var baseWritable = !forced && IsCustomModel(baseModel, repo);

        if (baseWritable)
            return (new WriteTarget(kind, request.ObjectName, baseModel, IsExtension: false, Exists: true), null);

        var extensionType = ExtensionTypeFor(kind);
        if (extensionType is null)
        {
            if (request.RequireExtension || request.ExtensionSuffix is not null)
            {
                return (null, ToolResult<object>.Fail(D365FoErrorCodes.BadInput,
                    $"{kind} objects have no extension form — there is nothing to extend.",
                    "Drop --extension, or make the change on the object itself."));
            }
            warnings.Add($"{kind} '{request.ObjectName}' lives in model '{baseModel}', which is not a configured custom model, " +
                         "and this kind has no extension form — modifying it in place.");
            return (new WriteTarget(kind, request.ObjectName, baseModel, IsExtension: false, Exists: true), null);
        }

        // Catch a type the AOT models but the provider exposes no channel for here,
        // rather than letting the bridge answer INVALID_KIND from three layers down.
        if (extensionType.ProviderCollection is null)
        {
            return (null, ToolResult<object>.Fail(D365FoErrorCodes.BadInput,
                $"{extensionType.RootElement} cannot be written through the metadata provider, " +
                $"so '{request.ObjectName}' cannot be extended.",
                $"Scaffold the extension with `d365fo generate extension` and edit the {extensionType.RootElement} file instead."));
        }

        var extKind = extensionType.Kind;

        var suffix = request.ExtensionSuffix is { Length: > 0 } s ? s : DefaultSuffix(request, repo);
        var extName = $"{request.ObjectName}.{suffix}";
        var extModel = request.ExtensionModel ?? FirstCustomModel(repo);
        if (string.IsNullOrWhiteSpace(extModel))
        {
            return (null, ToolResult<object>.Fail(D365FoErrorCodes.ModelNotFound,
                $"Cannot extend {kind} '{request.ObjectName}': no target model. Its own model '{baseModel}' is not a " +
                "configured custom model, so the change must go into an extension.",
                "Set D365FO_CUSTOM_MODELS, or pass --extension-model <MODEL>."));
        }

        if (!forced)
        {
            warnings.Add($"{kind} '{request.ObjectName}' belongs to model '{baseModel}', which is not a configured custom model — " +
                         $"the change was written to extension '{extName}' in '{extModel}' instead.");
        }

        var exists = ExtensionExists(kind, extName, repo);
        return (new WriteTarget(extKind, extName, extModel!, IsExtension: true, Exists: exists), null);
    }

    /// <summary>
    /// Reuse the suffix an existing extension of the same object already uses, so a model
    /// does not accumulate <c>CustTable.Fleet</c> alongside <c>CustTable.Extension</c>.
    /// Falls back to the conventional <c>Extension</c>.
    /// </summary>
    private static string DefaultSuffix(ModifyRequest request, MetadataRepository? repo)
    {
        if (repo is null) return "Extension";
        try
        {
            var existing = repo.FindExtensions(request.ObjectName, null)
                .Select(e => e.ExtensionName)
                .FirstOrDefault(n => n.Contains('.'));
            if (existing is not null) return existing[(existing.IndexOf('.') + 1)..];
        }
        catch { /* index hiccup — the conventional suffix is always safe */ }
        return "Extension";
    }

    private static bool ExtensionExists(string baseKind, string extensionName, MetadataRepository? repo)
    {
        if (repo is null) return false;
        try
        {
            var baseName = extensionName[..extensionName.IndexOf('.')];
            return repo.FindExtensions(baseName, null)
                .Any(e => string.Equals(e.ExtensionName, extensionName, StringComparison.OrdinalIgnoreCase));
        }
        catch { return false; }
    }

    private static bool IsCustomModel(string model, MetadataRepository? repo)
    {
        // The index's IsCustom flag is authoritative when available (it is computed from
        // D365FO_CUSTOM_MODELS at extract time); otherwise fall back to matching the
        // configured patterns directly.
        if (repo is not null)
        {
            try
            {
                var known = repo.ListModels().FirstOrDefault(m =>
                    string.Equals(m.Name, model, StringComparison.OrdinalIgnoreCase));
                if (known is not null) return known.IsCustom;
            }
            catch { /* fall through to the pattern check */ }
        }
        return new ModelMatcher(D365FoSettings.FromEnvironment().CustomModels).IsMatch(model);
    }

    private static string? FirstCustomModel(MetadataRepository? repo)
    {
        var configured = D365FoSettings.FromEnvironment().CustomModels;
        // A literal (glob-free) entry names a model directly and is the safest default.
        var literal = configured.FirstOrDefault(p => !p.Contains('*') && !p.Contains('?') && !p.StartsWith('!'));
        if (literal is not null) return literal;

        if (repo is null) return null;
        try { return repo.ListModels().FirstOrDefault(m => m.IsCustom)?.Name; }
        catch { return null; }
    }

    // ------------------------------------------------------------------- read leg

    private static (XDocument? Doc, string? PreImage, ToolResult<object>? Failure) ReadOrCreate(
        BridgeClient client, WriteTarget target, ModifyRequest request, string kind)
    {
        if (!target.Exists)
        {
            // A brand-new extension object: start from the minimal shape the scaffolder
            // emits, so the document the bridge deserializes is the same one
            // `generate extension` would have produced. The registry owns the root
            // element — PlanTarget already refused any kind it does not know.
            var root = ExtensionTypeFor(kind)?.RootElement
                       ?? ObjectTypeRegistry.Find(target.Kind)?.RootElement
                       ?? throw new InvalidOperationException($"No registered AOT type for '{target.Kind}'.");
            var fresh = new XDocument(new XElement(root, new XElement("Name", target.Name)));
            return (fresh, null, null);
        }

        JsonObject? readResult;
        try
        {
            readResult = client.SendAsync("readObjectXml", new JsonObject
            {
                ["kind"] = target.Kind,
                ["name"] = target.Name,
            }).GetAwaiter().GetResult();
        }
        catch (BridgeException ex)
        {
            return (null, null, ToolResult<object>.Fail(D365FoErrorCodes.BridgeRequired,
                "Bridge error while reading the object: " + ex.Message));
        }

        if (readResult is null)
            return (null, null, ToolResult<object>.Fail(D365FoErrorCodes.BridgeRequired, "Bridge returned no result for readObjectXml."));
        if ((bool?)readResult["ok"] != true)
        {
            var code = (string?)readResult["error"] ?? "READ_FAILED";
            var msg = (string?)readResult["message"] ?? "unknown error";
            return (null, null, ToolResult<object>.Fail(code == "NOT_FOUND" ? NotFoundCodeFor(kind) : code,
                $"Bridge could not read {target.Kind} '{target.Name}': {msg}",
                BridgeFailureHint(code, target)));
        }

        var xml = (string?)readResult["xml"];
        if (string.IsNullOrWhiteSpace(xml))
            return (null, null, ToolResult<object>.Fail("READ_FAILED", $"Bridge returned an empty xml payload for {target.Name}."));

        try
        {
            return (XDocument.Parse(xml), xml, null);
        }
        catch (Exception ex)
        {
            return (null, null, ToolResult<object>.Fail("READ_FAILED", "Could not parse XML returned by the bridge: " + ex.Message));
        }
    }

    // ------------------------------------------------------------------ edit leg

    private static (object? Applied, ToolResult<object>? Failure) ApplyOperation(
        XDocument doc, ModifyRequest request, string kind, WriteTarget target, MetadataRepository? repo)
        => request.Operation switch
        {
            Operation.SetProperty => SetProperty(doc, request),
            Operation.AddField => AddField(doc, request, repo),
            Operation.AddEnumValue => AddEnumValue(doc, request),
            Operation.AddControl => AddControl(doc, request),
            _ => (null, ToolResult<object>.Fail(D365FoErrorCodes.BadInput, $"Unhandled operation {request.Operation}.")),
        };

    private static (object?, ToolResult<object>?) SetProperty(XDocument doc, ModifyRequest request)
    {
        var root = doc.Root!;
        var existing = root.Elements().FirstOrDefault(e =>
            string.Equals(e.Name.LocalName, request.Member, StringComparison.OrdinalIgnoreCase));

        var oldValue = existing?.Value;
        if (existing is not null)
        {
            existing.SetValue(request.Value!);
        }
        else
        {
            // Property order under an Ax* root is alphabetical after <Name>; inserting
            // after <Name> keeps the document readable without risking the serializer,
            // which is order-tolerant for simple properties on the root.
            var el = new XElement(root.Name.Namespace + request.Member, request.Value!);
            var nameEl = root.Elements().FirstOrDefault(e => e.Name.LocalName == "Name");
            if (nameEl is not null) nameEl.AddAfterSelf(el); else root.AddFirst(el);
        }

        return (new { property = request.Member, oldValue, newValue = request.Value }, null);
    }

    private static (object?, ToolResult<object>?) AddField(XDocument doc, ModifyRequest request, MetadataRepository? repo)
    {
        var root = doc.Root!;
        var ns = root.Name.Namespace;
        XNamespace xsi = "http://www.w3.org/2001/XMLSchema-instance";

        var fields = root.Elements().FirstOrDefault(e => e.Name.LocalName == "Fields");
        if (fields is null)
        {
            fields = new XElement(ns + "Fields");
            root.Add(fields);
        }

        var clash = fields.Elements().FirstOrDefault(e =>
            string.Equals(LocalValue(e, "Name"), request.Member, StringComparison.OrdinalIgnoreCase));
        if (clash is not null)
        {
            return (null, ToolResult<object>.Fail(D365FoErrorCodes.AlreadyExists,
                $"Field '{request.Member}' already exists on {request.ObjectName}.",
                "Use `d365fo modify property` to change an existing field's properties."));
        }

        // The concrete i:type discriminator is mandatory — a plain <AxTableField> makes
        // the metadata reader throw on an abstract type (issue #91). Resolve it from the
        // EDT's indexed base type, exactly as `generate table` does.
        var resolver = BuildEdtResolver(repo);
        var suffix = XppScaffolder.ConcreteFieldSuffix(request.Type!, resolver);

        var field = new XElement(ns + "AxTableField",
            new XAttribute(xsi + "type", $"AxTableField{suffix}"),
            new XElement(ns + "Name", request.Member),
            new XElement(ns + "ExtendedDataType", request.Type!));
        if (!string.IsNullOrWhiteSpace(request.Label)) field.Add(new XElement(ns + "Label", request.Label));
        if (request.Mandatory) field.Add(new XElement(ns + "Mandatory", "Yes"));

        // A root without the xsi prefix bound emits an unusable i:type attribute.
        if (root.Attributes().All(a => a.Value != xsi.NamespaceName))
            root.SetAttributeValue(XNamespace.Xmlns + "i", xsi.NamespaceName);

        fields.Add(field);
        return (new { field = request.Member, edt = request.Type, axType = $"AxTableField{suffix}", mandatory = request.Mandatory }, null);
    }

    private static (object?, ToolResult<object>?) AddEnumValue(XDocument doc, ModifyRequest request)
    {
        var root = doc.Root!;
        var ns = root.Name.Namespace;

        var values = root.Elements().FirstOrDefault(e => e.Name.LocalName == "EnumValues");
        if (values is null)
        {
            values = new XElement(ns + "EnumValues");
            root.Add(values);
        }

        if (values.Elements().Any(e => string.Equals(LocalValue(e, "Name"), request.Member, StringComparison.OrdinalIgnoreCase)))
        {
            return (null, ToolResult<object>.Fail(D365FoErrorCodes.AlreadyExists,
                $"Enum value '{request.Member}' already exists on {request.ObjectName}."));
        }

        var value = new XElement(ns + "AxEnumValue", new XElement(ns + "Name", request.Member));
        if (!string.IsNullOrWhiteSpace(request.Label)) value.Add(new XElement(ns + "Label", request.Label));

        // Never emit an explicit <Value>: an extensible enum (UseEnumValue=No) is
        // position-based, and a hard-coded ordinal there is exactly what breaks when
        // another model inserts a value ahead of it.
        values.Add(value);
        return (new { enumValue = request.Member, positional = true }, null);
    }

    private static (object?, ToolResult<object>?) AddControl(XDocument doc, ModifyRequest request)
    {
        var root = doc.Root!;
        var design = root.Elements().FirstOrDefault(e => e.Name.LocalName == "Design");
        if (design is null)
        {
            // A form extension has no <Design>; controls are added under its own
            // <Controls> collection, which the metadata model merges into the base design.
            design = root;
        }

        var container = FindContainer(design, request.Parent);
        if (container is null)
        {
            return (null, ToolResult<object>.Fail(D365FoErrorCodes.ControlNotFound,
                $"Container control '{request.Parent}' was not found on {request.ObjectName}.",
                "Omit --parent to add at the Design root, or check the names with `d365fo get form " + request.ObjectName + "`."));
        }

        var controls = container.Elements().FirstOrDefault(e => e.Name.LocalName == "Controls");
        if (controls is null)
        {
            controls = new XElement(XNamespace.None + "Controls");
            container.Add(controls);
        }

        if (controls.Elements().Any(e => string.Equals(LocalValue(e, "Name"), request.Member, StringComparison.OrdinalIgnoreCase)))
        {
            return (null, ToolResult<object>.Fail(D365FoErrorCodes.AlreadyExists,
                $"Control '{request.Member}' already exists under {request.Parent ?? "Design"}."));
        }

        var control = string.IsNullOrWhiteSpace(request.DataField)
            ? FormControlFactory.Create(request.Type!, request.Member)
            : FormControlFactory.CreateBoundField(request.Type!, request.Member, request.DataSource ?? "", request.DataField!);

        controls.Add(control);
        return (new
        {
            control = request.Member,
            type = request.Type,
            axType = FormControlFactory.AxTypeFor(request.Type!),
            parent = request.Parent ?? "Design",
            bound = !string.IsNullOrWhiteSpace(request.DataField),
        }, null);
    }

    /// <summary>Find a container control by name anywhere in the design tree; null name means the design root.</summary>
    private static XElement? FindContainer(XElement design, string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return design;
        return design.Descendants()
            .FirstOrDefault(e => e.Name.LocalName == "AxFormControl" &&
                                 string.Equals(LocalValue(e, "Name"), name, StringComparison.OrdinalIgnoreCase));
    }

    // ------------------------------------------------------------------ journaling

    /// <summary>
    /// Record the pre-image so <c>d365fo undo</c> can revert this write. Best-effort by
    /// design: a journal failure must not abort a write that is otherwise valid, but it
    /// is never silent — the entry is written before the bridge call so a crash mid-write
    /// still leaves an undo record.
    /// </summary>
    internal static void RecordJournalEntry(
        WriteTarget target, string? preImage, string command, string? journalDbOverride = null)
    {
        try
        {
            ModificationJournal.ForIndex(journalDbOverride).Append(new JournalEntry(
                Id: Guid.NewGuid().ToString("N"),
                TimestampUtc: DateTimeOffset.UtcNow,
                Command: command,
                TargetType: JournalTargetType.AotObject,
                Kind: target.Kind,
                ObjectName: target.Name,
                SecondaryKey: null,
                Model: target.Model,
                Operation: target.Exists ? JournalOperation.Update : JournalOperation.Create,
                WritePath: JournalWritePath.Bridge,
                TargetPath: null,
                PreImage: preImage,
                IsTombstone: !target.Exists,
                RnrProjDelta: null));
        }
        catch
        {
            // Journal storage problems (disk full, unwritable index dir) must not turn a
            // valid metadata write into a failure.
        }
    }

    private static string DescribeCommand(ModifyRequest request, string kind) => request.Operation switch
    {
        Operation.SetProperty => $"modify property {kind} {request.ObjectName} {request.Member}",
        Operation.AddField => $"modify add-field {request.ObjectName} {request.Member}",
        Operation.AddEnumValue => $"modify add-enum-value {request.ObjectName} {request.Member}",
        Operation.AddControl => $"modify add-control {request.ObjectName} {request.Member}",
        _ => $"modify {kind} {request.ObjectName}",
    };

    // ---------------------------------------------------------------------- helpers

    private static string? ResolveModel(string kind, string name, MetadataRepository? repo)
    {
        if (repo is null) return null;
        try
        {
            return kind switch
            {
                "class" => repo.GetClassDetails(name)?.Class.Model,
                "table" => repo.GetTableDetails(name)?.Table.Model,
                "edt" => repo.GetEdt(name)?.Model,
                "enum" => repo.GetEnum(name)?.Enum.Model,
                "form" => repo.GetForm(name)?.Form.Model,
                _ => null,
            };
        }
        catch { return null; }
    }

    private static Func<string, string?>? BuildEdtResolver(MetadataRepository? repo)
    {
        if (repo is null) return null;
        return edt =>
        {
            if (string.IsNullOrWhiteSpace(edt)) return null;
            try { return repo.GetEdt(edt)?.BaseType; }
            catch { return null; }
        };
    }

    private static string? LocalValue(XElement parent, string localName)
        => parent.Elements().FirstOrDefault(e => e.Name.LocalName == localName)?.Value;

    private static string NotFoundCodeFor(string kind) => kind switch
    {
        "class" => D365FoErrorCodes.ClassNotFound,
        "table" => D365FoErrorCodes.TableNotFound,
        "edt" => D365FoErrorCodes.EdtNotFound,
        "enum" => D365FoErrorCodes.EnumNotFound,
        "form" => D365FoErrorCodes.FormNotFound,
        _ => "NOT_FOUND",
    };
}
