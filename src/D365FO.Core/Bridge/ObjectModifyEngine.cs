// <copyright file="ObjectModifyEngine.cs" company="d365fo-cli contributors">
// MIT
// </copyright>

using System.Text.Json.Nodes;
using System.Xml.Linq;
using D365FO.Core.Extract;
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

        /// <summary>Add an index to a table.</summary>
        AddIndex,

        /// <summary>Add a foreign-key relation to a table.</summary>
        AddRelation,

        /// <summary>Add a field group to a table.</summary>
        AddFieldGroup,

        /// <summary>Add a delete action to a table.</summary>
        AddDeleteAction,

        /// <summary>Remove a field from a table.</summary>
        RemoveField,

        /// <summary>Remove an index from a table.</summary>
        RemoveIndex,

        /// <summary>Remove a relation from a table.</summary>
        RemoveRelation,

        /// <summary>Remove a field group from a table.</summary>
        RemoveFieldGroup,

        /// <summary>Remove a delete action from a table.</summary>
        RemoveDeleteAction,

        /// <summary>Rename a field on a table.</summary>
        RenameField,

        /// <summary>Remove a value from a base enum.</summary>
        RemoveEnumValue,

        /// <summary>Remove a control from a form's Design tree.</summary>
        RemoveControl,

        /// <summary>Add a range to a datasource of an AOT query.</summary>
        AddQueryRange,

        /// <summary>Remove a range from a datasource of an AOT query.</summary>
        RemoveQueryRange,

        /// <summary>Add an entry point to a security privilege.</summary>
        AddEntryPoint,

        /// <summary>Remove an entry point from a security privilege.</summary>
        RemoveEntryPoint,
    }

    /// <summary>
    /// Operations that only ever remove something. They share one shape — find the member by
    /// name inside its collection, refuse when it is not there, remove it — so they are
    /// described once rather than repeated thirteen times.
    /// </summary>
    /// <remarks>
    /// <c>Collection</c> is the container element under the object root, <c>Item</c> the element
    /// name of one entry, and <c>Key</c> the child element carrying the name to match on. The
    /// table collections key on <c>Name</c>; a delete action is addressed by the table it
    /// governs, which the contract calls <c>Table</c> (there is no <c>RelatedTable</c> member -
    /// writing one produced a rule naming nothing, and keying on one found nothing).
    /// </remarks>
    private sealed record RemovalShape(string Collection, string Item, string Key, string Noun);

    private static readonly IReadOnlyDictionary<Operation, RemovalShape> Removals =
        new Dictionary<Operation, RemovalShape>
        {
            [Operation.RemoveField]       = new("Fields", "AxTableField", "Name", "Field"),
            [Operation.RemoveIndex]       = new("Indexes", "AxTableIndex", "Name", "Index"),
            [Operation.RemoveRelation]    = new("Relations", "AxTableRelation", "Name", "Relation"),
            [Operation.RemoveFieldGroup]  = new("FieldGroups", "AxTableFieldGroup", "Name", "Field group"),
            [Operation.RemoveDeleteAction] = new("DeleteActions", "AxTableDeleteAction", "Table", "Delete action"),
            [Operation.RemoveEnumValue]   = new("EnumValues", "AxEnumValue", "Name", "Enum value"),
        };

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

        /// <summary>
        /// Member fields, in order, for <see cref="Operation.AddIndex"/> and
        /// <see cref="Operation.AddFieldGroup"/>. Order is data, not decoration — an index's
        /// field order decides which queries it can serve.
        /// </summary>
        public IReadOnlyList<string>? Fields { get; init; }

        /// <summary>Target table for <see cref="Operation.AddRelation"/> / <see cref="Operation.AddDeleteAction"/>.</summary>
        public string? RelatedTable { get; init; }

        /// <summary>Field on <see cref="RelatedTable"/> the relation constrains to. Defaults to <see cref="Member"/>.</summary>
        public string? RelatedField { get; init; }

        /// <summary>Cascade | Restricted | CascadeRestricted | None, for <see cref="Operation.AddDeleteAction"/>.</summary>
        public string? DeleteAction { get; init; }

        /// <summary><see cref="Operation.AddIndex"/>: allow duplicate keys. A unique index is the default.</summary>
        public bool AllowDuplicates { get; init; }

        /// <summary><see cref="Operation.AddIndex"/>: mark the index an alternate key.</summary>
        public bool AlternateKey { get; init; }

        /// <summary>New member name for <see cref="Operation.RenameField"/>.</summary>
        public string? NewName { get; init; }

        /// <summary>Query datasource the range belongs to. Defaults to the query's only datasource.</summary>
        public string? DataSourceName { get; init; }

        /// <summary>
        /// Range value for <see cref="Operation.AddQueryRange"/> — the range EXPRESSION, not a
        /// literal to be quoted. Empty is legal and common: a range with no value is one the
        /// caller sets at run time through <c>QueryBuildRange.value()</c>.
        /// </summary>
        public string? RangeValue { get; init; }

        /// <summary>AOT type of an entry point: MenuItemDisplay, MenuItemAction, MenuItemOutput, Form, …</summary>
        public string? EntryPointType { get; init; }

        /// <summary>Access level granted to an entry point: Read | Update | Create | Correct | Delete | Invoke.</summary>
        public string? Access { get; init; }

        /// <summary>
        /// Several operations against the SAME object, applied in order to one in-memory
        /// document. Null or empty means this request is the single operation.
        /// </summary>
        /// <remarks>
        /// The saving is not cosmetic. Each entry would otherwise be its own bridge read, write
        /// and journal entry, and the intermediate states get published — a table is briefly on
        /// disk with a field and no index covering it. Batched, the object goes from one valid
        /// state to the next in a single write, and a step that refuses discards the whole batch
        /// with nothing written.
        ///
        /// Steps inherit <see cref="Kind"/>, <see cref="ObjectName"/> and <see cref="Model"/>
        /// from the request carrying them; anything they set for those is ignored, because a
        /// batch that could retarget mid-flight is not a batch.
        /// </remarks>
        public IReadOnlyList<ModifyRequest>? Batch { get; init; }
    }

    /// <summary>
    /// Every kind the bridge can read and write, taken from the registry rather than listed here.
    /// </summary>
    /// <remarks>
    /// This was a hand-written set of five — class, table, edt, enum, form — while the bridge
    /// resolves its collections from <see cref="ObjectTypeRegistry.BridgeCollections"/>, which
    /// names 41. So <c>modify property</c> refused a query, a privilege, a menu item, a service
    /// group, a view, a report and a tile that the layer underneath was perfectly able to write,
    /// and the refusal named the five as if they were the platform's limit. It is the same drift
    /// the extension-type lookup had (#171), with the same fix: one source of truth.
    ///
    /// Operations narrower than a property set still gate themselves — <c>add-field</c> on
    /// anything but a table, <c>add-index</c> on anything but a table, and so on.
    /// </remarks>
    private static readonly IReadOnlySet<string> SupportedKinds =
        ObjectTypeRegistry.BridgeCollections().Keys.ToHashSet(StringComparer.OrdinalIgnoreCase);

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

        // A batch header names the object, not an operation — its Operation and Member are
        // placeholders, so validating it as if it were a step would refuse every batch for a
        // missing member name. The steps are each validated in the loop below.
        if (request.Batch is { Count: > 0 })
        {
            if (!SupportedKinds.Contains(kind))
                return ValidateRequest(request with { Batch = null, Member = "batch" }, kind)!;
            if (string.IsNullOrWhiteSpace(request.ObjectName))
                return ToolResult<object>.Fail(D365FoErrorCodes.BadInput, "Object name is required.");
        }
        else
        {
            var validation = ValidateRequest(request, kind);
            if (validation is not null) return validation;
        }

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

        // ---- 3. Structured edit(s) ----
        // A batch applies every operation to the SAME in-memory document, so N changes cost one
        // bridge read, one write and one journal entry. Stopped at the first failure, and since
        // nothing has been written at that point the object is untouched — a half-applied batch
        // is never published.
        var operations = request.Batch is { Count: > 0 } ? request.Batch : new[] { request };
        var applied = new List<object?>(operations.Count);
        for (var i = 0; i < operations.Count; i++)
        {
            var step = operations[i] with
            {
                // The batch header owns where the write lands; a step only says what to change.
                // `Kind` is `required` but callers can still pass null through it, which is why
                // the top of this method normalises it the same defensive way.
                Kind = request.Kind ?? string.Empty,
                ObjectName = request.ObjectName,
                Model = request.Model,
            };

            var stepValidation = ValidateRequest(step, kind);
            if (stepValidation is not null)
                return BatchFailure(stepValidation, i, operations.Count, applied);

            var (stepApplied, editFailure) = ApplyOperation(doc!, step, kind, target!, repo);
            if (editFailure is not null)
                return BatchFailure(editFailure, i, operations.Count, applied);

            applied.Add(stepApplied);
        }

        // ---- 4. Write back, journaling the pre-image first ----
        // An element that arrives out of contract order is not rejected by the AOT reader, it is
        // DROPPED: DataContractSerializer matches children in order and skips anything early or
        // late. The disk write path has always canonicalised (ScaffoldFileWriter.Finalize) but
        // this one did not, so an edit that created a missing collection — `<Fields>` on a table
        // that had none, `<EnumValues>`, `<Controls>` — appended it at the end of the root and
        // the write came back "ok" with the change silently absent. Canonicalise the same way
        // before serialising; it is idempotent and leaves collection item order alone.
        ContractOrderCanonicalizer.Apply(doc!);
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

        warnings.Add($"Index not auto-refreshed — run `d365fo index refresh --model {target.Model}` so the change is searchable.");

        var isBatch = request.Batch is { Count: > 0 };
        return ToolResult<object>.Success(new
        {
            operation = isBatch ? "Batch" : request.Operation.ToString(),
            operations = isBatch ? operations.Select(o => o.Operation.ToString()).ToArray() : null,
            kind,
            name = request.ObjectName,
            member = isBatch ? null : request.Member,
            wroteTo = new { kind = target.Kind, name = target.Name, model = target.Model, extension = target.IsExtension },
            created = !target.Exists,
            source = "bridge",
            applied = isBatch ? applied : applied.FirstOrDefault(),
        }, warnings);
    }

    /// <summary>
    /// Report a batch step that refused, naming which one and what had already been applied.
    /// </summary>
    /// <remarks>
    /// Nothing has been written when this runs — the failure happens against the in-memory
    /// document, before the single write — so the object on disk is untouched. Saying so
    /// matters: "step 3 of 5 failed" reads like a half-applied change unless the answer also
    /// says the other four were discarded rather than published.
    /// </remarks>
    private static ToolResult<object> BatchFailure(
        ToolResult<object> failure, int index, int total, IReadOnlyList<object?> appliedSoFar)
    {
        if (total <= 1) return failure;

        var error = failure.Error!;
        var hint = $"Step {index + 1} of {total} refused; NOTHING was written — the {appliedSoFar.Count} " +
                   "earlier step(s) were applied to an in-memory copy and discarded, so the object is unchanged. " +
                   "Fix this step and re-run the whole batch.";
        return ToolResult<object>.Fail(error.Code, error.Message,
            string.IsNullOrWhiteSpace(error.Hint) ? hint : error.Hint + " " + hint);
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
            // 41 kinds is too many to print at a caller who mistyped one of them, so name the
            // near misses and point at the full list rather than filling the terminal.
            var close = SupportedKinds
                .Where(k => k.Contains(kind, StringComparison.OrdinalIgnoreCase)
                            || kind.Contains(k, StringComparison.OrdinalIgnoreCase))
                .OrderBy(k => k.Length)
                .Take(5)
                .ToList();
            return ToolResult<object>.Fail(D365FoErrorCodes.BadInput,
                $"Unsupported object kind '{request.Kind}'.",
                close.Count > 0
                    ? $"Did you mean: {string.Join(", ", close)}? Full list: `d365fo schema --output json`."
                    : $"{SupportedKinds.Count} kinds are writable; see `d365fo schema --output json`.");
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

            // ---- table-shaped operations ----
            _ when TableOnly.Contains(request.Operation) && kind != "table" =>
                ToolResult<object>.Fail(D365FoErrorCodes.BadInput,
                    $"`modify {CommandNameFor(request.Operation)}` applies to tables, not {kind}."),
            Operation.AddIndex when (request.Fields?.Count ?? 0) == 0 =>
                ToolResult<object>.Fail(D365FoErrorCodes.BadInput,
                    "--field is required for `modify add-index` (repeat it; the order is the index's key order)."),
            Operation.AddFieldGroup when (request.Fields?.Count ?? 0) == 0 =>
                ToolResult<object>.Fail(D365FoErrorCodes.BadInput,
                    "--field is required for `modify add-field-group` (repeat it for each member)."),
            Operation.AddRelation when string.IsNullOrWhiteSpace(request.RelatedTable) =>
                ToolResult<object>.Fail(D365FoErrorCodes.BadInput,
                    "--related-table is required for `modify add-relation`."),
            Operation.AddDeleteAction when string.IsNullOrWhiteSpace(request.RelatedTable) =>
                ToolResult<object>.Fail(D365FoErrorCodes.BadInput,
                    "--related-table is required for `modify add-delete-action`."),
            Operation.AddDeleteAction when !ValidDeleteActions.Contains(request.DeleteAction ?? "") =>
                ToolResult<object>.Fail(D365FoErrorCodes.BadInput,
                    $"--action must be one of: {string.Join(", ", ValidDeleteActions)}."),
            Operation.RenameField when string.IsNullOrWhiteSpace(request.NewName) =>
                ToolResult<object>.Fail(D365FoErrorCodes.BadInput,
                    "--new-name is required for `modify rename-field`."),

            Operation.RemoveEnumValue when kind != "enum" =>
                ToolResult<object>.Fail(D365FoErrorCodes.BadInput,
                    $"`modify remove-enum-value` applies to enums, not {kind}."),
            Operation.RemoveControl when kind != "form" =>
                ToolResult<object>.Fail(D365FoErrorCodes.BadInput,
                    $"`modify remove-control` applies to forms, not {kind}."),

            _ when QueryOnly.Contains(request.Operation) && kind != "query" =>
                ToolResult<object>.Fail(D365FoErrorCodes.BadInput,
                    $"`modify {CommandNameFor(request.Operation)}` applies to AOT queries, not {kind}."),
            Operation.AddQueryRange when string.IsNullOrWhiteSpace(request.Member) =>
                ToolResult<object>.Fail(D365FoErrorCodes.BadInput,
                    "The range's field name is required for `modify add-query-range`."),

            _ when PrivilegeOnly.Contains(request.Operation) && kind != "securityprivilege" =>
                ToolResult<object>.Fail(D365FoErrorCodes.BadInput,
                    $"`modify {CommandNameFor(request.Operation)}` applies to security privileges, not {kind}."),
            Operation.AddEntryPoint when string.IsNullOrWhiteSpace(request.EntryPointType) =>
                ToolResult<object>.Fail(D365FoErrorCodes.BadInput,
                    "--type is required for `modify add-entry-point` (MenuItemDisplay, MenuItemAction, MenuItemOutput, Form, …)."),
            _ => null,
        };
    }

    /// <summary>Operations that only make sense on an <c>AxQuery</c>.</summary>
    private static readonly IReadOnlySet<Operation> QueryOnly = new HashSet<Operation>
    {
        Operation.AddQueryRange, Operation.RemoveQueryRange,
    };

    /// <summary>Operations that only make sense on an <c>AxSecurityPrivilege</c>.</summary>
    private static readonly IReadOnlySet<Operation> PrivilegeOnly = new HashSet<Operation>
    {
        Operation.AddEntryPoint, Operation.RemoveEntryPoint,
    };

    /// <summary>Operations that only make sense on an <c>AxTable</c>.</summary>
    private static readonly IReadOnlySet<Operation> TableOnly = new HashSet<Operation>
    {
        Operation.AddIndex, Operation.AddRelation, Operation.AddFieldGroup, Operation.AddDeleteAction,
        Operation.RemoveField, Operation.RemoveIndex, Operation.RemoveRelation,
        Operation.RemoveFieldGroup, Operation.RemoveDeleteAction, Operation.RenameField,
    };

    /// <summary>The four delete actions the platform defines. Anything else is a typo.</summary>
    private static readonly IReadOnlySet<string> ValidDeleteActions =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Cascade", "Restricted", "CascadeRestricted", "None" };

    /// <summary>The <c>modify</c> subcommand an operation is reached through, for error text.</summary>
    private static string CommandNameFor(Operation op) => op switch
    {
        Operation.SetProperty => "property",
        Operation.AddField => "add-field",
        Operation.AddEnumValue => "add-enum-value",
        Operation.AddControl => "add-control",
        Operation.AddIndex => "add-index",
        Operation.AddRelation => "add-relation",
        Operation.AddFieldGroup => "add-field-group",
        Operation.AddDeleteAction => "add-delete-action",
        Operation.RemoveField => "remove-field",
        Operation.RemoveIndex => "remove-index",
        Operation.RemoveRelation => "remove-relation",
        Operation.RemoveFieldGroup => "remove-field-group",
        Operation.RemoveDeleteAction => "remove-delete-action",
        Operation.RenameField => "rename-field",
        Operation.RemoveEnumValue => "remove-enum-value",
        Operation.RemoveControl => "remove-control",
        Operation.AddQueryRange => "add-query-range",
        Operation.RemoveQueryRange => "remove-query-range",
        Operation.AddEntryPoint => "add-entry-point",
        Operation.RemoveEntryPoint => "remove-entry-point",
        _ => op.ToString(),
    };

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
            Operation.AddIndex => AddIndex(doc, request),
            Operation.AddRelation => AddRelation(doc, request),
            Operation.AddFieldGroup => AddFieldGroup(doc, request),
            Operation.AddDeleteAction => AddDeleteAction(doc, request),
            Operation.RenameField => RenameField(doc, request),
            Operation.RemoveControl => RemoveControl(doc, request),
            Operation.AddQueryRange => AddQueryRange(doc, request),
            Operation.RemoveQueryRange => RemoveQueryRange(doc, request),
            Operation.AddEntryPoint => AddEntryPoint(doc, request),
            Operation.RemoveEntryPoint => RemoveEntryPoint(doc, request),
            _ when Removals.TryGetValue(request.Operation, out var shape) => RemoveMember(doc, request, shape),
            _ => (null, ToolResult<object>.Fail(D365FoErrorCodes.BadInput, $"Unhandled operation {request.Operation}.")),
        };

    /// <summary>
    /// The container element under the root, created in place if the object does not have one yet.
    /// </summary>
    /// <remarks>
    /// Creating it by appending to the root is only safe because the write path canonicalises
    /// member order before serialising — see the comment at the write-back step. Without that,
    /// a collection appended after a later-ordered sibling is dropped on read.
    /// </remarks>
    private static XElement EnsureCollection(XElement root, string name)
    {
        var existing = root.Elements().FirstOrDefault(e => e.Name.LocalName == name);
        if (existing is not null) return existing;

        var created = new XElement(root.Name.Namespace + name);
        root.Add(created);
        return created;
    }

    private static bool HasNamed(XElement collection, string key, string value) =>
        collection.Elements().Any(e => string.Equals(LocalValue(e, key), value, StringComparison.OrdinalIgnoreCase));

    private static (object?, ToolResult<object>?) AddIndex(XDocument doc, ModifyRequest request)
    {
        var root = doc.Root!;
        var ns = root.Name.Namespace;
        var indexes = EnsureCollection(root, "Indexes");

        if (HasNamed(indexes, "Name", request.Member))
        {
            return (null, ToolResult<object>.Fail(D365FoErrorCodes.AlreadyExists,
                $"Index '{request.Member}' already exists on {request.ObjectName}.",
                $"Drop it first with `d365fo modify remove-index {request.ObjectName} {request.Member}`."));
        }

        // Every named field must exist on the table, or the index is one the AOS will refuse
        // after a build cycle has already been paid for.
        var declared = root.Elements().FirstOrDefault(e => e.Name.LocalName == "Fields");
        var known = declared?.Elements()
            .Select(e => LocalValue(e, "Name"))
            .Where(n => !string.IsNullOrEmpty(n))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (known is not null && known.Count > 0)
        {
            var missing = request.Fields!.Where(f => !known.Contains(f) && !SystemFieldNames.Contains(f)).ToList();
            if (missing.Count > 0)
            {
                return (null, ToolResult<object>.Fail(D365FoErrorCodes.FieldNotFound,
                    $"{request.ObjectName} has no field named {string.Join(", ", missing.Select(m => $"'{m}'"))}.",
                    $"See the real field list with `d365fo get table {request.ObjectName}`."));
            }
        }

        var index = new XElement(ns + "AxTableIndex",
            new XElement(ns + "Name", request.Member),
            request.AlternateKey ? new XElement(ns + "AlternateKey", "Yes") : null,
            // The serializer's default is AllowDuplicates=Yes, so a unique index has to say so.
            request.AllowDuplicates ? null : new XElement(ns + "AllowDuplicates", "No"),
            new XElement(ns + "Fields",
                request.Fields!.Select(f => new XElement(ns + "AxTableIndexField",
                    new XElement(ns + "DataField", f)))));

        indexes.Add(index);
        return (new
        {
            index = request.Member,
            fields = request.Fields,
            unique = !request.AllowDuplicates,
            alternateKey = request.AlternateKey,
        }, null);
    }

    /// <summary>Fields the kernel puts on every table; they are indexable but never declared.</summary>
    private static readonly IReadOnlySet<string> SystemFieldNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "RecId", "RecVersion", "DataAreaId", "Partition", "createdBy", "createdDateTime",
        "modifiedBy", "modifiedDateTime", "createdTransactionId", "modifiedTransactionId",
    };

    private static (object?, ToolResult<object>?) AddRelation(XDocument doc, ModifyRequest request)
    {
        var root = doc.Root!;
        var ns = root.Name.Namespace;
        XNamespace xsi = "http://www.w3.org/2001/XMLSchema-instance";
        var relations = EnsureCollection(root, "Relations");

        if (HasNamed(relations, "Name", request.Member))
        {
            return (null, ToolResult<object>.Fail(D365FoErrorCodes.AlreadyExists,
                $"Relation '{request.Member}' already exists on {request.ObjectName}."));
        }

        // The constraint's concrete i:type is mandatory: AxTableRelationConstraint is abstract
        // and the metadata reader throws on it, the same way an untyped AxTableField does.
        var relatedField = string.IsNullOrWhiteSpace(request.RelatedField) ? request.Member : request.RelatedField!;
        var relation = new XElement(ns + "AxTableRelation",
            new XElement(ns + "Name", request.Member),
            new XElement(ns + "Cardinality", "ZeroMore"),
            new XElement(ns + "RelatedTable", request.RelatedTable!),
            new XElement(ns + "RelatedTableCardinality", "ExactlyOne"),
            new XElement(ns + "RelationshipType", "Association"),
            new XElement(ns + "Constraints",
                new XElement(ns + "AxTableRelationConstraint",
                    new XAttribute(xsi + "type", "AxTableRelationConstraintField"),
                    new XElement(ns + "Name", request.Member),
                    new XElement(ns + "Field", request.Member),
                    new XElement(ns + "RelatedField", relatedField))));

        if (root.Attributes().All(a => a.Value != xsi.NamespaceName))
            root.SetAttributeValue(XNamespace.Xmlns + "i", xsi.NamespaceName);

        relations.Add(relation);
        return (new
        {
            relation = request.Member,
            relatedTable = request.RelatedTable,
            field = request.Member,
            relatedField,
        }, null);
    }

    private static (object?, ToolResult<object>?) AddFieldGroup(XDocument doc, ModifyRequest request)
    {
        var root = doc.Root!;
        var ns = root.Name.Namespace;
        var groups = EnsureCollection(root, "FieldGroups");

        if (HasNamed(groups, "Name", request.Member))
        {
            return (null, ToolResult<object>.Fail(D365FoErrorCodes.AlreadyExists,
                $"Field group '{request.Member}' already exists on {request.ObjectName}.",
                "The five Auto* groups and Overview/General are created with every table."));
        }

        var group = new XElement(ns + "AxTableFieldGroup",
            new XElement(ns + "Name", request.Member),
            string.IsNullOrWhiteSpace(request.Label) ? null : new XElement(ns + "Label", request.Label),
            new XElement(ns + "Fields",
                request.Fields!.Select(f => new XElement(ns + "AxTableFieldGroupField",
                    new XElement(ns + "DataField", f)))));

        groups.Add(group);
        return (new { fieldGroup = request.Member, fields = request.Fields, label = request.Label }, null);
    }

    private static (object?, ToolResult<object>?) AddDeleteAction(XDocument doc, ModifyRequest request)
    {
        var root = doc.Root!;
        var ns = root.Name.Namespace;
        var actions = EnsureCollection(root, "DeleteActions");

        // A delete action is identified by the table it points at — two actions for the same
        // related table is not a richer rule, it is a conflict.
        if (HasNamed(actions, "RelatedTable", request.RelatedTable!))
        {
            return (null, ToolResult<object>.Fail(D365FoErrorCodes.AlreadyExists,
                $"{request.ObjectName} already has a delete action for '{request.RelatedTable}'.",
                $"Remove it first with `d365fo modify remove-delete-action {request.ObjectName} {request.RelatedTable}`."));
        }

        // The contract is `Name, DeleteAction, Relation, Table, Tags` — the related table is
        // <Table>. Writing <RelatedTable> produced a delete action naming NOTHING: the member
        // does not exist, so the serializer dropped it while the write reported ok, and what
        // landed on disk was a Cascade rule with no target. Shipped tables emit an empty
        // <Relation> alongside, and this matches them.
        actions.Add(new XElement(ns + "AxTableDeleteAction",
            new XElement(ns + "Name", request.RelatedTable!),
            new XElement(ns + "DeleteAction", request.DeleteAction!),
            new XElement(ns + "Relation", string.Empty),
            new XElement(ns + "Table", request.RelatedTable!)));

        return (new { deleteAction = request.DeleteAction, relatedTable = request.RelatedTable }, null);
    }

    private static (object?, ToolResult<object>?) RenameField(XDocument doc, ModifyRequest request)
    {
        var root = doc.Root!;
        var fields = root.Elements().FirstOrDefault(e => e.Name.LocalName == "Fields");
        var field = fields?.Elements().FirstOrDefault(e =>
            string.Equals(LocalValue(e, "Name"), request.Member, StringComparison.OrdinalIgnoreCase));

        if (field is null)
        {
            return (null, ToolResult<object>.Fail(D365FoErrorCodes.FieldNotFound,
                $"{request.ObjectName} has no field named '{request.Member}'.",
                $"See the real field list with `d365fo get table {request.ObjectName}`."));
        }

        if (fields!.Elements().Any(e =>
                string.Equals(LocalValue(e, "Name"), request.NewName, StringComparison.OrdinalIgnoreCase)))
        {
            return (null, ToolResult<object>.Fail(D365FoErrorCodes.AlreadyExists,
                $"{request.ObjectName} already has a field named '{request.NewName}'."));
        }

        field.Elements().First(e => e.Name.LocalName == "Name").SetValue(request.NewName!);

        // Indexes, field groups and relations reference a field BY NAME. Renaming the field and
        // leaving those behind produces a table that no longer resolves its own index — the AOS
        // reports it as a missing field, pointing at the index rather than at this edit.
        var rewritten = new List<string>();
        foreach (var (collection, item, member) in new[]
                 {
                     ("Indexes", "AxTableIndexField", "DataField"),
                     ("FieldGroups", "AxTableFieldGroupField", "DataField"),
                 })
        {
            var container = root.Elements().FirstOrDefault(e => e.Name.LocalName == collection);
            if (container is null) continue;
            foreach (var reference in container.Descendants()
                         .Where(e => e.Name.LocalName == member)
                         .Where(e => string.Equals(e.Value, request.Member, StringComparison.OrdinalIgnoreCase)))
            {
                reference.SetValue(request.NewName!);
                rewritten.Add($"{collection}/{item}");
            }
        }

        var relations = root.Elements().FirstOrDefault(e => e.Name.LocalName == "Relations");
        if (relations is not null)
        {
            foreach (var reference in relations.Descendants()
                         .Where(e => e.Name.LocalName == "Field")
                         .Where(e => string.Equals(e.Value, request.Member, StringComparison.OrdinalIgnoreCase)))
            {
                reference.SetValue(request.NewName!);
                rewritten.Add("Relations/AxTableRelationConstraintField");
            }
        }

        return (new
        {
            renamedField = request.Member,
            to = request.NewName,
            referencesRewritten = rewritten.Distinct().ToArray(),
            warning = "X++ that names this field by fieldStr()/fieldNum() is NOT rewritten — " +
                      $"check with `d365fo find refs {request.ObjectName}.{request.Member}` before you build.",
        }, null);
    }

    private static (object?, ToolResult<object>?) RemoveControl(XDocument doc, ModifyRequest request)
    {
        var root = doc.Root!;
        var design = root.Elements().FirstOrDefault(e => e.Name.LocalName == "Design") ?? root;

        var control = design.Descendants()
            .FirstOrDefault(e => e.Name.LocalName.StartsWith("AxFormControl", StringComparison.Ordinal)
                                 && string.Equals(LocalValue(e, "Name"), request.Member, StringComparison.OrdinalIgnoreCase));

        if (control is null)
        {
            return (null, ToolResult<object>.Fail(D365FoErrorCodes.ControlNotFound,
                $"Control '{request.Member}' was not found on {request.ObjectName}.",
                $"See the control tree with `d365fo get form {request.ObjectName}`."));
        }

        // Removing a container takes its children with it — say so, rather than reporting "1".
        var descendants = control.Descendants()
            .Count(e => e.Name.LocalName.StartsWith("AxFormControl", StringComparison.Ordinal));
        control.Remove();

        return (new { removedControl = request.Member, nestedControlsRemoved = descendants }, null);
    }

    /// <summary>
    /// The query datasource a range operation targets: the one named, or the only one there is.
    /// </summary>
    /// <remarks>
    /// Most queries have exactly one datasource, and making the caller name it would be asking
    /// for something the document already determines. With two or more it is genuinely ambiguous,
    /// so the refusal lists them rather than picking the first — picking would put the range on a
    /// join the caller never mentioned, and a range on the wrong datasource silently returns the
    /// wrong rows instead of failing.
    /// </remarks>
    private static (XElement? DataSource, ToolResult<object>? Failure) ResolveQueryDataSource(
        XElement root, ModifyRequest request)
    {
        var container = root.Elements().FirstOrDefault(e => e.Name.LocalName == "DataSources");
        // Shared with the extractor: the AOT writes AxQuerySimpleRootDataSource and
        // AxQuerySimpleEmbeddedDataSource, not the shorter names the fixtures use.
        var sources = container?.Elements()
            .Where(e => MetadataExtractor.IsQueryDataSourceElement(e.Name.LocalName))
            .ToList() ?? new List<XElement>();

        if (sources.Count == 0)
        {
            return (null, ToolResult<object>.Fail(D365FoErrorCodes.MemberNotFound,
                $"Query '{request.ObjectName}' declares no datasources.",
                $"Check it with `d365fo get query {request.ObjectName}`."));
        }

        if (!string.IsNullOrWhiteSpace(request.DataSourceName))
        {
            var named = sources.FirstOrDefault(e =>
                string.Equals(LocalValue(e, "Name"), request.DataSourceName, StringComparison.OrdinalIgnoreCase));
            if (named is null)
            {
                return (null, ToolResult<object>.Fail(D365FoErrorCodes.MemberNotFound,
                    $"Query '{request.ObjectName}' has no datasource named '{request.DataSourceName}'.",
                    $"It has: {string.Join(", ", sources.Select(e => LocalValue(e, "Name")))}."));
            }
            return (named, null);
        }

        if (sources.Count > 1)
        {
            return (null, ToolResult<object>.Fail(D365FoErrorCodes.BadInput,
                $"Query '{request.ObjectName}' has {sources.Count} datasources — name the one you mean with --data-source.",
                $"It has: {string.Join(", ", sources.Select(e => LocalValue(e, "Name")))}."));
        }

        return (sources[0], null);
    }

    private static (object?, ToolResult<object>?) AddQueryRange(XDocument doc, ModifyRequest request)
    {
        var (dataSource, failure) = ResolveQueryDataSource(doc.Root!, request);
        if (failure is not null) return (null, failure);

        var ns = doc.Root!.Name.Namespace;
        var ranges = dataSource!.Elements().FirstOrDefault(e => e.Name.LocalName == "Ranges");
        if (ranges is null)
        {
            ranges = new XElement(ns + "Ranges");
            dataSource.Add(ranges);
        }

        if (HasNamed(ranges, "Field", request.Member))
        {
            return (null, ToolResult<object>.Fail(D365FoErrorCodes.AlreadyExists,
                $"Datasource '{LocalValue(dataSource, "Name")}' already ranges on '{request.Member}'.",
                "A second range on the same field ANDs with the first, which is almost never what is wanted — " +
                "remove it first, or widen the existing value."));
        }

        // Name and Field carry the same value in every shipped query: the name is the field.
        ranges.Add(new XElement(ns + "AxQuerySimpleDataSourceRange",
            new XElement(ns + "Name", request.Member),
            new XElement(ns + "Field", request.Member),
            // An empty <Value> is legal and idiomatic — it is the shape a range gets when the
            // value is supplied at run time via QueryBuildRange.value().
            new XElement(ns + "Value", request.RangeValue ?? "")));

        return (new
        {
            range = request.Member,
            dataSource = LocalValue(dataSource, "Name"),
            value = request.RangeValue ?? "",
            note = string.IsNullOrWhiteSpace(request.RangeValue)
                ? "Empty value — set it at run time with QueryBuildRange.value()."
                : null,
        }, null);
    }

    private static (object?, ToolResult<object>?) RemoveQueryRange(XDocument doc, ModifyRequest request)
    {
        var (dataSource, failure) = ResolveQueryDataSource(doc.Root!, request);
        if (failure is not null) return (null, failure);

        var ranges = dataSource!.Elements().FirstOrDefault(e => e.Name.LocalName == "Ranges");
        var range = ranges?.Elements().FirstOrDefault(e =>
            string.Equals(LocalValue(e, "Field"), request.Member, StringComparison.OrdinalIgnoreCase));

        if (range is null)
        {
            return (null, ToolResult<object>.Fail(D365FoErrorCodes.MemberNotFound,
                $"Datasource '{LocalValue(dataSource, "Name")}' has no range on '{request.Member}'.",
                "Nothing was changed."));
        }

        var wasValue = LocalValue(range, "Value");
        range.Remove();
        return (new { removedRange = request.Member, dataSource = LocalValue(dataSource, "Name"), wasValue }, null);
    }

    private static (object?, ToolResult<object>?) AddEntryPoint(XDocument doc, ModifyRequest request)
    {
        var root = doc.Root!;
        var ns = root.Name.Namespace;
        var entryPoints = EnsureCollection(root, "EntryPoints");

        if (HasNamed(entryPoints, "Name", request.Member))
        {
            return (null, ToolResult<object>.Fail(D365FoErrorCodes.AlreadyExists,
                $"Privilege '{request.ObjectName}' already grants '{request.Member}'.",
                "Remove it and add it back to change the access level."));
        }

        // Contract order for AxSecurityEntryPointReference is Name, Grant, …, ObjectName,
        // ObjectType. Access is six independent permissions, never an <AccessLevel> element —
        // writing one produces a privilege that grants nothing and reads as deliberate.
        entryPoints.Add(new XElement(ns + "AxSecurityEntryPointReference",
            new XElement(ns + "Name", request.Member),
            XppScaffolder.SecurityGrant(request.Access),
            new XElement(ns + "ObjectName", request.Member),
            new XElement(ns + "ObjectType", request.EntryPointType!)));

        return (new
        {
            entryPoint = request.Member,
            objectType = request.EntryPointType,
            access = request.Access ?? "Read",
        }, null);
    }

    private static (object?, ToolResult<object>?) RemoveEntryPoint(XDocument doc, ModifyRequest request)
    {
        var entryPoints = doc.Root!.Elements().FirstOrDefault(e => e.Name.LocalName == "EntryPoints");
        var entryPoint = entryPoints?.Elements().FirstOrDefault(e =>
            string.Equals(LocalValue(e, "Name"), request.Member, StringComparison.OrdinalIgnoreCase)
            || string.Equals(LocalValue(e, "ObjectName"), request.Member, StringComparison.OrdinalIgnoreCase));

        if (entryPoint is null)
        {
            return (null, ToolResult<object>.Fail(D365FoErrorCodes.MemberNotFound,
                $"Privilege '{request.ObjectName}' does not grant '{request.Member}'.",
                $"See what it grants with `d365fo get privilege {request.ObjectName}`."));
        }

        var objectType = LocalValue(entryPoint, "ObjectType");
        entryPoint.Remove();
        return (new { removedEntryPoint = request.Member, objectType }, null);
    }

    private static (object?, ToolResult<object>?) RemoveMember(XDocument doc, ModifyRequest request, RemovalShape shape)
    {
        var root = doc.Root!;
        var collection = root.Elements().FirstOrDefault(e => e.Name.LocalName == shape.Collection);

        // A delete action is addressed by the table it governs, everything else by its own name.
        var wanted = shape.Key == "Table" ? request.RelatedTable ?? request.Member : request.Member;

        var member = collection?.Elements()
            .FirstOrDefault(e => string.Equals(LocalValue(e, shape.Key), wanted, StringComparison.OrdinalIgnoreCase));

        if (member is null)
        {
            return (null, ToolResult<object>.Fail(D365FoErrorCodes.MemberNotFound,
                $"{shape.Noun} '{wanted}' was not found on {request.ObjectName}.",
                $"Nothing was changed. Check the current members with `d365fo get {request.Kind} {request.ObjectName}`."));
        }

        var removed = member.ToString(SaveOptions.DisableFormatting);
        member.Remove();

        return (new
        {
            removed = wanted,
            of = shape.Noun,
            // The pre-image is in the journal, but printing it here is what lets a caller see
            // what it just gave up without going and reading the journal back.
            wasXml = removed.Length <= 2000 ? removed : removed[..2000] + "…",
        }, null);
    }

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
