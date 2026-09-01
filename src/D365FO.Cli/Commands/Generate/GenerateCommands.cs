using D365FO.Core;
using D365FO.Core.Journal;
using D365FO.Core.Scaffolding;
using Spectre.Console.Cli;
using D365FO.Cli.Commands.Get;

using static D365FO.Core.ObjectTypes.ObjectTypeRegistry;

namespace D365FO.Cli.Commands.Generate;

public abstract class GenerateSettings : D365OutputSettings
{
    [CommandOption("--out <PATH>")]
    [System.ComponentModel.Description("Output file path. Required unless --install-to is used.")]
    public string? Out { get; init; }

    [CommandOption("--overwrite")]
    public bool Overwrite { get; init; }

    [CommandOption("--install-to <MODEL>")]
    [System.ComponentModel.Description("Install the generated artefact directly into <MODEL> via the metadata bridge. Requires D365FO_BRIDGE_ENABLED=1.")]
    public string? InstallTo { get; init; }

    [CommandOption("--grounding-token <TOKEN>")]
    [System.ComponentModel.Description("Grounding token from `d365fo prepare change`/`prepare create` proving the index was consulted. Required for extension-shaped objects when D365FO_GROUNDING_ENFORCE=true.")]
    public string? GroundingToken { get; init; }

    [CommandOption("--verify")]
    [System.ComponentModel.Description("After writing, read every artefact back through the D365FO Metadata API the way Visual Studio would. Applies to --install-to; skipped (never fails) when the runtime is unavailable. The payload's `verify` field reports which happened. Requires D365FO_BRIDGE_ENABLED=1.")]
    public bool Verify { get; init; }
}

internal static class GenerateInstaller
{
    /// <summary>
    /// Run the grounding gate for a generate command. Every generate subcommand calls this
    /// before it writes anything, and writes through the handle it returns.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Issue #161 / finding G4. The gate used to be wired into three subcommands by hand, which
    /// made it decorative for the other twenty-six: the anti-hallucination token was simply
    /// optional everywhere else. Moving it here — the path every generate command already goes
    /// through to reach disk — is what makes it uniform, and pairing it with
    /// <see cref="Write"/> is what makes bypassing it require deliberately not using the shared
    /// path. <c>GenerateSurfaceTests</c> fails the build if a command does that.
    /// </para>
    /// <para>
    /// <paramref name="targetObject"/> is the object the write is bound to: the extension or CoC
    /// target for extension-shaped commands, and the artefact's own name for the rest, which is
    /// what <c>prepare create</c> issues a token against.
    /// </para>
    /// </remarks>
    internal static GroundingGate.GateResult Gate(
        GenerateSettings settings,
        string targetObject,
        System.Xml.Linq.XDocument? doc = null,
        IEnumerable<(string Owner, string Method)>? requiredMethods = null,
        IEnumerable<string>? requiredSymbols = null)
        => GroundingGate.Check(
            settings.GroundingToken, targetObject, doc, requiredMethods, requiredSymbols,
            RequestedValues(settings), TryRepo());

    /// <summary>The index the gate proves identifiers against, or null when there is none.</summary>
    /// <remarks>
    /// The gate takes the repository rather than opening one, because the MCP server already
    /// holds an open connection and Core has no <c>RepoFactory</c>. A missing index degrades the
    /// checks to warnings inside the gate — it never fails a write on gate infrastructure.
    /// </remarks>
    private static D365FO.Core.Index.MetadataRepository? TryRepo()
    {
        try { return RepoFactory.Create(); }
        catch { return null; }
    }

    /// <summary>
    /// The option/value pairs a caller actually supplied, for the property-honesty
    /// reconciliation.
    /// </summary>
    /// <remarks>
    /// Read off the settings instance rather than declared per command, because a per-command
    /// list is exactly the hand-maintained table that goes stale the first time someone adds an
    /// option. Only properties declared on the command's own settings type count: everything on
    /// <see cref="GenerateSettings"/> and <c>D365OutputSettings</c> is plumbing (<c>--out</c>,
    /// <c>--overwrite</c>, <c>--install-to</c>, the token, the output format) and has no business
    /// appearing in the generated object. Booleans are skipped too — a flag selects a shape, and
    /// its absence from the document says nothing.
    /// </remarks>
    internal static IReadOnlyList<(string Option, string Value)> RequestedValues(GenerateSettings settings)
    {
        var requested = new List<(string, string)>();

        foreach (var prop in settings.GetType().GetProperties())
        {
            if (prop.DeclaringType is null || prop.DeclaringType == typeof(GenerateSettings)) continue;
            if (!prop.DeclaringType.IsSubclassOf(typeof(GenerateSettings))) continue;

            var option = OptionNameOf(prop);
            if (option is null) continue;

            switch (prop.GetValue(settings))
            {
                case string s when !string.IsNullOrWhiteSpace(s):
                    requested.Add((option, s));
                    break;
                case string[] many:
                    foreach (var s in many.Where(s => !string.IsNullOrWhiteSpace(s)))
                        requested.Add((option, s));
                    break;
            }
        }

        return requested;
    }

    /// <summary>The command-line spelling of a settings property, or null when it is not an option.</summary>
    private static string? OptionNameOf(System.Reflection.PropertyInfo prop)
    {
        var opt = prop.GetCustomAttributes(typeof(CommandOptionAttribute), inherit: true)
            .Cast<CommandOptionAttribute>().FirstOrDefault();
        if (opt is not null)
        {
            // "--field <SPEC>" / "-f|--field <SPEC>" → "--field".
            var longest = opt.LongNames.OrderByDescending(n => n.Length).FirstOrDefault();
            return longest is null ? null : "--" + longest;
        }

        var arg = prop.GetCustomAttributes(typeof(CommandArgumentAttribute), inherit: true)
            .Cast<CommandArgumentAttribute>().FirstOrDefault();
        return arg is null ? null : "<" + arg.ValueName + ">";
    }

    /// <summary>
    /// Write a generated document. Takes the gate handle, so reaching the writer without having
    /// gated is not something a caller can express.
    /// </summary>
    internal static ScaffoldFileWriter.WriteResult Write(
        GroundingGate.GateResult gate, System.Xml.Linq.XDocument doc, string path, bool overwrite = false)
    {
        ArgumentNullException.ThrowIfNull(gate);
        var result = ScaffoldFileWriter.Write(doc, path, overwrite);
        // Write finalises the document in place, so this is the form that reached disk — not the
        // scaffolder's pre-canonicalisation draft.
        gate.Observe(doc.ToString());
        var (axKind, name) = IdentityOf(doc.Root);
        gate.RecordArtefact(axKind, name, result.Path);
        return result;
    }

    /// <summary>String-rendered counterpart of <see cref="Write(GroundingGate.GateResult, System.Xml.Linq.XDocument, string, bool)"/>.</summary>
    internal static ScaffoldFileWriter.WriteResult Write(
        GroundingGate.GateResult gate, string xml, string path, bool overwrite = false)
    {
        ArgumentNullException.ThrowIfNull(gate);
        var result = ScaffoldFileWriter.Write(xml, path, overwrite);
        gate.Observe(xml);
        System.Xml.Linq.XElement? root = null;
        try { root = System.Xml.Linq.XDocument.Parse(xml).Root; } catch { /* identity stays unknown */ }
        var (axKind, name) = IdentityOf(root);
        gate.RecordArtefact(axKind, name, result.Path);
        return result;
    }

    /// <summary>
    /// The kind and name the metadata provider would use to look a written document up,
    /// read off the document itself.
    /// </summary>
    /// <remarks>
    /// Derived rather than declared, for the reason given on
    /// <see cref="GroundingGate.GateResult.Artefacts"/>: the root element is already the
    /// registry's key (<c>AxTable</c> → <c>table</c>), and every AOT document carries its
    /// own <c>&lt;Name&gt;</c>, so a multi-file command gets each of its outputs identified
    /// correctly without a table anyone has to remember to extend.
    /// </remarks>
    internal static (string? AxKind, string? Name) IdentityOf(System.Xml.Linq.XElement? root)
    {
        if (root is null) return (null, null);
        var axKind = D365FO.Core.ObjectTypes.ObjectTypeRegistry.Find(root.Name.LocalName)?.Kind;
        var name = root.Elements().FirstOrDefault(e => e.Name.LocalName == "Name")?.Value;
        return (axKind, string.IsNullOrWhiteSpace(name) ? null : name);
    }

    /// <summary>
    /// Resolve the on-disk install path for a scaffolded artefact. When
    /// <c>--install-to &lt;MODEL&gt;</c> is supplied we ask the bridge where
    /// the model lives on disk and compose
    /// <c>&lt;modelFolder&gt;/Ax&lt;Kind&gt;/&lt;Name&gt;.xml</c> — the
    /// canonical location Visual Studio and the D365FO build tools expect.
    /// The caller then invokes the regular <see cref="ScaffoldFileWriter"/>
    /// against this path. Returns null on failure and renders an error into
    /// <paramref name="failure"/>.
    /// </summary>
    internal static string? ResolveInstallPath(OutputMode.Kind kind, string axSubfolder, string name, string model, out int? failure)
    {
        failure = null;
        var folder = BridgeGate.TryGetModelFolder(model);
        if (string.IsNullOrEmpty(folder))
        {
            failure = RenderHelpers.Render(kind, ToolResult<object>.Fail(
                "INSTALL_FAILED",
                $"Could not resolve folder for model '{model}'. Ensure the bridge can see the model: set D365FO_BRIDGE_ENABLED=1 and point D365FO_PACKAGES_PATH (and D365FO_CUSTOM_PACKAGES_PATH for custom-model roots) at the directories that contain the model, on a D365FO VM."));
            return null;
        }
        return System.IO.Path.Combine(folder!, axSubfolder, name + ".xml");
    }

    /// <summary>
    /// Build an EDT → primitive-base-type resolver backed by the SQLite index.
    /// Passed to <see cref="XppScaffolder.Table"/> so each field gets its
    /// concrete <c>i:type="AxTableField{Suffix}"</c> discriminator (issue #91).
    /// Returns null when the index is unavailable — the scaffolder then falls
    /// back to a name heuristic.
    /// </summary>
    internal static Func<string, string?>? BuildEdtBaseTypeResolver()
    {
        try
        {
            var repo = RepoFactory.Create();
            return edt =>
            {
                if (string.IsNullOrWhiteSpace(edt)) return null;
                try { return repo.GetEdt(edt)?.BaseType; }
                catch { return null; }
            };
        }
        catch { return null; }
    }

    /// <summary>
    /// Best-effort modification-journal append (issue #113) for a bridge-mediated create.
    /// Tombstone entry (no pre-image, since nothing existed before) — undo replays it by
    /// calling the bridge's <c>deleteObject</c>. Never lets a journal failure fail the
    /// create it is recording.
    /// </summary>
    private static void RecordBridgeCreate(string axKind, string name, string model)
    {
        try
        {
            ModificationJournal.ForIndex().Append(new JournalEntry(
                Id: Guid.NewGuid().ToString("N"),
                TimestampUtc: DateTimeOffset.UtcNow,
                Command: $"generate {axKind} --install-to",
                TargetType: JournalTargetType.AotObject,
                Kind: axKind,
                ObjectName: name,
                SecondaryKey: null,
                Model: model,
                Operation: JournalOperation.Create,
                WritePath: JournalWritePath.Bridge,
                TargetPath: null,
                PreImage: null,
                IsTombstone: true,
                RnrProjDelta: null));
        }
        catch { /* best-effort */ }
    }

    internal enum InstallOutcome { CreatedViaApi, WriteScaffold, Failed }

    /// <summary>
    /// Plan how to install a generated artefact into <paramref name="model"/>.
    /// Prefers the live metadata provider (bridge <c>createObject</c>) so the
    /// on-disk XML is provider-canonical and consistent with Visual Studio /
    /// <c>d365fo-mcp-server</c>. When the provider is unavailable, falls back to
    /// writing the (now valid) scaffold into the resolved model folder, with a
    /// warning. When neither path is reachable, returns a rendered failure.
    /// </summary>
    internal static (InstallOutcome outcome, string? writePath, int? failure, List<string> warnings)
        PlanInstall(OutputMode.Kind kind, string axKind, string axSubfolder, string name, string model, string xml)
    {
        var warnings = new List<string>();

        // 1) Metadata-API path — canonical, consistent output.
        var (ok, err) = BridgeGate.TrySaveObject(axKind, name, model, xml);
        if (ok)
        {
            RecordBridgeCreate(axKind, name, model);
            return (InstallOutcome.CreatedViaApi, null, null, warnings);
        }

        // 2) Fallback — write the scaffold into the model folder if resolvable.
        var folder = BridgeGate.TryGetModelFolder(model);
        if (!string.IsNullOrEmpty(folder))
        {
            warnings.Add(
                $"Metadata API unavailable or rejected the object ({err}); wrote the raw scaffold instead. " +
                "The file is structurally valid but not provider-canonicalised — open it in Visual Studio to verify.");
            return (InstallOutcome.WriteScaffold,
                System.IO.Path.Combine(folder!, axSubfolder, name + ".xml"), null, warnings);
        }

        // 3) Neither path worked.
        var failure = RenderHelpers.Render(kind, ToolResult<object>.Fail(
            "INSTALL_FAILED",
            $"Could not install '{name}' into model '{model}'. Metadata API: {err}. " +
            "Could not resolve the model folder either — set D365FO_BRIDGE_ENABLED=1 and point " +
            "D365FO_PACKAGES_PATH (and D365FO_CUSTOM_PACKAGES_PATH for custom-model roots) at the " +
            "directories that contain the model, on a D365FO VM."));
        return (InstallOutcome.Failed, null, failure, warnings);
    }

    /// <summary>Inputs handed to a command's payload factory after the artefact is written.</summary>
    internal readonly record struct EmitResult(string Source, string? Path, long? Bytes, string? Backup);

    /// <summary>
    /// Emit a generated artefact (<see cref="System.Xml.Linq.XDocument"/> form),
    /// preferring the live metadata provider for <c>--install-to</c> and falling
    /// back to the scaffold. <paramref name="axKind"/> must be one of the bridge
    /// collection kinds: <c>class | table | edt | enum | form</c>.
    /// </summary>
    /// <remarks>
    /// Takes the whole <paramref name="settings"/> rather than the three or four fields it
    /// reads off it, so that adding an option every emitter has to honour does not mean
    /// revisiting each call site to thread it through — which is how <c>--verify</c> came to
    /// be honoured by six subcommands and ignored by twenty-four (issue #180).
    /// </remarks>
    internal static int Emit(
        OutputMode.Kind kind, GroundingGate.GateResult gate, GenerateSettings settings,
        string axKind, string axSubfolder, string name,
        System.Xml.Linq.XDocument doc,
        Func<EmitResult, object> buildPayload,
        List<string>? warnings = null)
    {
        // Canonicalise here, not only in ScaffoldFileWriter: the bridge path hands the XML
        // string straight to IMetadataProvider, so a document left in the wrong namespace or
        // member order would be silently stripped of properties on the way in — with no file
        // on disk to inspect afterwards.
        ContractNamespaceApplier.Apply(doc);
        ContractOrderCanonicalizer.Apply(doc);

        return EmitCore(kind, gate, settings, axKind, axSubfolder, name, doc.ToString(),
            path => Write(gate, doc, path, settings.Overwrite), buildPayload, warnings);
    }

    /// <summary>String-rendered counterpart of <see cref="Emit"/> (used for forms).</summary>
    internal static int EmitString(
        OutputMode.Kind kind, GroundingGate.GateResult gate, GenerateSettings settings,
        string axKind, string axSubfolder, string name,
        string xml,
        Func<EmitResult, object> buildPayload,
        List<string>? warnings = null)
        => EmitCore(kind, gate, settings, axKind, axSubfolder, name, xml,
            path => Write(gate, xml, path, settings.Overwrite), buildPayload, warnings);

    /// <summary>
    /// Opt-in post-write check for <c>--verify</c>: read every artefact the run emitted back
    /// through the live metadata provider, the way Visual Studio would. Fails the command
    /// only when the provider was reachable and still could not load an object — an absent
    /// runtime is reported as a skip and never blocks generation, which has to keep working
    /// offline (CI, agent sessions, machines without the VS metadata assemblies).
    /// </summary>
    /// <remarks>
    /// Issue #180. This used to live inside <see cref="EmitCore"/>, which only six of the
    /// thirty subcommands reached: the other twenty-four accepted <c>--verify</c>, advertised
    /// it in <c>--help</c>, and did nothing — output indistinguishable from a run that really
    /// had verified. Running it from the shared terminal (<see cref="Finish"/>) puts it on the
    /// path every generate command takes to render its success, and reporting the verdict in
    /// the payload is what makes "not requested", "skipped" and "verified" tellable apart.
    /// </remarks>
    private static (object Report, int? Failure) RunVerify(
        OutputMode.Kind kind, GroundingGate.GateResult? gate, bool verify, string? installTo,
        List<string> warnings)
    {
        if (!verify) return (new { status = "not-requested" }, null);

        // The provider resolves objects by name inside the configured packages paths,
        // so an artefact parked at an arbitrary --out path is not something it can
        // look up — verifying would either miss it or match a different object of the
        // same name. Say so rather than emit a meaningless verdict.
        if (string.IsNullOrWhiteSpace(installTo))
            return (Skip(warnings, "verification reads the object back by name from the configured " +
                                   "packages paths, which only applies to --install-to."), null);

        var artefacts = gate?.Artefacts ?? [];
        if (artefacts.Count == 0)
            return (Skip(warnings, "this command installed nothing through the shared writer, so there " +
                                   "is no artefact to read back."), null);

        var verdicts = new List<object>();
        var skipped = false;

        foreach (var artefact in artefacts)
        {
            if (artefact.AxKind is null || artefact.Name is null)
            {
                skipped = true;
                const string unknown = "could not read the object's kind and name off the document root, " +
                                       "so the provider has nothing to look up.";
                warnings.Add($"--verify skipped for {artefact.Path ?? "an emitted document"}: {unknown}");
                verdicts.Add(new { kind = artefact.AxKind, name = artefact.Name, path = artefact.Path, status = "skipped", detail = unknown });
                continue;
            }

            var (outcome, detail) = BridgeGate.TryVerifyObject(artefact.AxKind, artefact.Name);
            switch (outcome)
            {
                case BridgeGate.VerifyOutcome.Readable:
                    warnings.Add(detail is null
                        ? $"--verify: the metadata provider read '{artefact.Name}' back successfully."
                        : $"--verify: the metadata provider read '{artefact.Name}' back successfully ({detail}).");
                    verdicts.Add(new { kind = artefact.AxKind, name = artefact.Name, path = artefact.Path, status = "verified", detail });
                    break;

                case BridgeGate.VerifyOutcome.Skipped:
                    skipped = true;
                    warnings.Add($"--verify skipped: {detail}");
                    verdicts.Add(new { kind = artefact.AxKind, name = artefact.Name, path = artefact.Path, status = "skipped", detail });
                    break;

                default:
                    return (new { status = "failed", artefacts = verdicts },
                        RenderHelpers.Render(kind, ToolResult<object>.Fail(
                            "VERIFY_FAILED",
                            $"Wrote '{artefact.Name}' but {detail} " +
                            (artefact.Path is null ? string.Empty : $"The file at {artefact.Path} was left in place. ") +
                            "Open it in Visual Studio to see the metadata reader's own error.")));
            }
        }

        return (new { status = skipped ? "skipped" : "verified", artefacts = verdicts }, null);

        static object Skip(List<string> warnings, string detail)
        {
            warnings.Add("--verify skipped: " + detail);
            return new { status = "skipped", detail };
        }
    }

    /// <summary>
    /// Stamp the verification verdict onto a command's payload.
    /// </summary>
    /// <remarks>
    /// The payloads are anonymous types built per command, so the field is grafted on as JSON
    /// rather than by widening thirty declarations. Serialising with the same options the
    /// renderer uses keeps naming and null-dropping identical to a payload that was never
    /// touched.
    /// </remarks>
    private static object WithVerify(object payload, object verify)
    {
        try
        {
            if (System.Text.Json.JsonSerializer.SerializeToNode(payload, D365Json.Options)
                is System.Text.Json.Nodes.JsonObject obj)
            {
                obj["verify"] = System.Text.Json.JsonSerializer.SerializeToNode(verify, D365Json.Options);
                return obj;
            }
        }
        catch { /* not an object-shaped payload — render it as it was */ }
        return payload;
    }

    /// <summary>
    /// The single success exit for every generate subcommand: run <c>--verify</c> over what was
    /// emitted, fold in the property-honesty report, and render.
    /// </summary>
    /// <remarks>
    /// <c>GenerateVerifySurfaceTests</c> fails the build if a command that writes renders its
    /// own success envelope instead, which is the same guard <c>GenerateGateSurfaceTests</c>
    /// puts on the grounding gate.
    /// </remarks>
    internal static int Done(
        OutputMode.Kind kind, GroundingGate.GateResult? gate, GenerateSettings settings,
        object payload, IEnumerable<string>? warnings = null)
        => Finish(kind, gate, settings.Verify, settings.InstallTo, payload, Merge(gate, warnings));

    private static int Finish(
        OutputMode.Kind kind, GroundingGate.GateResult? gate, bool verify, string? installTo,
        object payload, List<string> warnings)
    {
        var (report, failure) = RunVerify(kind, gate, verify, installTo, warnings);
        if (failure.HasValue) return failure.Value;

        if (gate is not null) WithHonesty(warnings, gate);

        return RenderHelpers.Render(kind, ToolResult<object>.Success(
            WithVerify(payload, report), warnings.Count > 0 ? warnings : null));
    }

    /// <summary>Gate warnings plus the caller's own, deduplicated and order-preserving.</summary>
    private static List<string> Merge(GroundingGate.GateResult? gate, IEnumerable<string>? warnings)
    {
        var merged = new List<string>();
        foreach (var w in (gate?.Warnings ?? Enumerable.Empty<string>()).Concat(warnings ?? []))
            if (!merged.Contains(w)) merged.Add(w);
        return merged;
    }

    /// <summary>
    /// Fold the gate's property-honesty findings into the warnings a command is about to
    /// return.
    /// </summary>
    /// <remarks>
    /// The gate can only reconcile once the document exists, which is after the point where
    /// <see cref="EmitCore"/> has already snapshotted the caller's warnings. Merging here rather
    /// than appending blindly keeps the report out of the list twice when the caller passed
    /// <c>gate.Warnings</c> itself, which is the normal case.
    /// </remarks>
    private static List<string> WithHonesty(List<string> warnings, GroundingGate.GateResult gate)
    {
        foreach (var gap in gate.PropertyGaps)
        {
            var message = $"property-honesty: {gap}";
            if (!warnings.Contains(message)) warnings.Add(message);
        }
        return warnings;
    }

    private static int EmitCore(
        OutputMode.Kind kind, GroundingGate.GateResult gate, GenerateSettings settings,
        string axKind, string axSubfolder, string name, string xml,
        Func<string, ScaffoldFileWriter.WriteResult> write,
        Func<EmitResult, object> buildPayload,
        List<string>? warnings)
    {
        var installTo = settings.InstallTo;
        var outPath   = settings.Out;
        var verify    = settings.Verify;
        warnings ??= new List<string>();
        var hasInstall = !string.IsNullOrWhiteSpace(installTo);
        var hasOut     = !string.IsNullOrWhiteSpace(outPath);
        if (!hasInstall && !hasOut)
            return RenderHelpers.Render(kind, ToolResult<object>.Fail("BAD_INPUT", "--out or --install-to is required."));

        if (hasInstall && !hasOut)
        {
            var plan = PlanInstall(kind, axKind, axSubfolder, name, installTo!, xml);
            if (plan.failure.HasValue) return plan.failure.Value;
            var all = warnings.Concat(plan.warnings).ToList();

            if (plan.outcome == InstallOutcome.CreatedViaApi)
            {
                // Nothing reached disk, but the provider was handed exactly this XML — which is
                // the document the honesty report has to judge, and the object --verify reads
                // back. Recorded explicitly because no Write ran to derive it.
                gate.Observe(xml);
                gate.RecordArtefact(axKind, name, null);
                return Finish(kind, gate, verify, installTo,
                    buildPayload(new EmitResult("bridge", null, null, null)), all);
            }

            try
            {
                var res = write(plan.writePath!);
                return Finish(kind, gate, verify, installTo,
                    buildPayload(new EmitResult("scaffold", res.Path, res.Bytes, res.BackupPath)), all);
            }
            catch (Exception ex)
            {
                return RenderHelpers.Render(kind, ToolResult<object>.Fail("WRITE_FAILED", ex.Message));
            }
        }

        try
        {
            var res = write(outPath!);
            return Finish(kind, gate, verify, installTo,
                buildPayload(new EmitResult("scaffold", res.Path, res.Bytes, res.BackupPath)), warnings);
        }
        catch (Exception ex)
        {
            return RenderHelpers.Render(kind, ToolResult<object>.Fail("WRITE_FAILED", ex.Message));
        }
    }
}

public sealed class GenerateTableCommand : Command<GenerateTableCommand.Settings>
{
    public sealed class Settings : GenerateSettings
    {
        [CommandArgument(0, "<NAME>")]
        public string Name { get; init; } = "";

        [CommandOption("--label <KEY>")]
        public string? Label { get; init; }

        [CommandOption("--field <SPEC>")]
        [System.ComponentModel.Description("Repeatable: <name>:<edt>[[:mandatory]]. Example: --field AccountNum:CustAccount:mandatory")]
        public string[] Fields { get; init; } = Array.Empty<string>();

        [CommandOption("--pattern <PATTERN>")]
        [System.ComponentModel.Description("Business-role preset: main|transaction|parameter|group|worksheetheader|worksheetline|reference|framework|miscellaneous. Sets <TableGroup> and provides default fields when --field is empty. Aliases: master, setup, config, transactional, lookup …")]
        public string? Pattern { get; init; }

        [CommandOption("--table-type <TYPE>")]
        [System.ComponentModel.Description("Storage kind: RegularTable|TempDB|InMemory (default RegularTable). NEVER pass TempDB to --pattern — it is a TableType, not a TableGroup.")]
        public string? TableType { get; init; }

        [CommandOption("--primary-key <FIELD>")]
        [System.ComponentModel.Description("Repeatable: field name(s) to compose the alternate-key index. Defaults to all mandatory fields, or the first field if none mandatory.")]
        public string[] PrimaryKey { get; init; } = Array.Empty<string>();

        [CommandOption("--configuration-key <NAME>")]
        [System.ComponentModel.Description("AxConfigurationKey gating the table. Omitted when not set (the table is ungated).")]
        public string? ConfigurationKey { get; init; }

        [CommandOption("--form-ref <MENUITEM>")]
        [System.ComponentModel.Description("Display menu item opened when drilling into a record of this table. Omitted when not set.")]
        public string? FormRef { get; init; }

        [CommandOption("--field-group <SPEC>")]
        [System.ComponentModel.Description("Repeatable: <Name>[[:<F1>,<F2>,…]]. Adds a caller-defined field group after the built-ins (Auto*, Overview, General). Example: --field-group Pricing:Price,Currency")]
        public string[] FieldGroups { get; init; } = Array.Empty<string>();

        [CommandOption("--index <SPEC>")]
        [System.ComponentModel.Description("Repeatable: <Name>:<F1>,<F2>[[:unique]][[:alternate-key]][[:valid-time-state[[=Gap|NoGap]]]]. Adds an index after the derived PrimaryIdx. Example: --index DateIdx:ValidFrom,ValidTo:valid-time-state=Gap")]
        public string[] Indexes { get; init; } = Array.Empty<string>();
    }

    public override int Execute(CommandContext ctx, Settings settings)
    {
        var kind = OutputMode.Resolve(settings.Output);
        if (string.IsNullOrWhiteSpace(settings.Name))
            return RenderHelpers.Render(kind, ToolResult<object>.Fail("BAD_INPUT", "Table name required."));
        if (!TablePatternNormalizer.TryNormalize(settings.Pattern, out var pattern, out var perr))
            return RenderHelpers.Render(kind, ToolResult<object>.Fail("BAD_INPUT", perr!));
        if (!TablePatternNormalizer.TryNormalizeStorage(settings.TableType, out var storage, out var serr))
            return RenderHelpers.Render(kind, ToolResult<object>.Fail("BAD_INPUT", serr!));

        var fields2 = settings.Fields.Select(ParseField).ToList();
        if (!TryParseFieldGroups(settings.FieldGroups, out var fieldGroupSpecs, out var fgErr))
            return RenderHelpers.Render(kind, ToolResult<object>.Fail("BAD_INPUT", fgErr!));
        if (!TryParseIndexes(settings.Indexes, out var indexSpecs, out var idxErr))
            return RenderHelpers.Render(kind, ToolResult<object>.Fail("BAD_INPUT", idxErr!));
        // Resolve each field's EDT base type from the index so the scaffold
        // stamps the concrete i:type discriminator on every <AxTableField>.
        var edtResolver = GenerateInstaller.BuildEdtBaseTypeResolver();
        var doc = XppScaffolder.Table(settings.Name, settings.Label, fields2, pattern, storage, settings.PrimaryKey,
            settings.ConfigurationKey, settings.FormRef, edtResolver, fieldGroupSpecs, indexSpecs);

        var fieldCount = fields2.Count > 0 ? fields2.Count : TablePatternPresets.DefaultFieldsFor(pattern).Count;
        var patternStr = pattern == TablePattern.None ? null : pattern.ToString();
        var tableTypeStr = storage == TableStorage.RegularTable ? null : storage.ToString();
        var usedDefaults = fields2.Count == 0 && pattern != TablePattern.None;

        // Without --pattern the scaffold deliberately emits no <TableGroup>, so the
        // AOT default (Miscellaneous) applies and nothing is flipped by accident.
        // The consequence is not obvious though: this tool's own `validate xpp`
        // raises XML003 on that very table. Say so here rather than letting the
        // caller discover it one command later.
        var warnings = new List<string>();
        if (pattern == TablePattern.None)
        {
            warnings.Add(
                "No --pattern given, so no <TableGroup> is emitted and the AOT default (Miscellaneous) applies. " +
                "`d365fo validate xpp` reports XML003 for that. Pass --pattern (main|transaction|parameter|group|" +
                "worksheet-header|worksheet-line|reference|framework|miscellaneous) to stamp the table's business role.");
        }

        // Grounding gate (issue #161): every generate subcommand runs it, not just the
        // extension-shaped three. A table is bound to its own name — that is what
        // `prepare create` issues a token against.
        var gate = GenerateInstaller.Gate(settings, settings.Name, doc);
        if (gate.Failure is not null) return RenderHelpers.Render(kind, gate.Failure);
        warnings.AddRange(gate.Warnings);

        // Prefer the live metadata provider for --install-to (canonical output,
        // consistent with VS / d365fo-mcp-server); fall back to the scaffold.
        return GenerateInstaller.Emit(
            kind, gate, settings, "table", Folders.Table, settings.Name,
            doc,
            r => new
            {
                kind = "AxTable",
                name = settings.Name,
                source = r.Source,
                path = r.Path,
                bytes = r.Bytes,
                backup = r.Backup,
                fieldCount,
                pattern = patternStr,
                tableType = tableTypeStr,
                configurationKey = settings.ConfigurationKey,
                formRef = settings.FormRef,
                usedPatternDefaults = usedDefaults,
                model = settings.InstallTo,
                grounding = gate.Grounding,
            },
            warnings);
    }

    private static bool TryParseFieldGroups(string[] raw, out List<TableFieldGroupSpec> specs, out string? error)
    {
        specs = [];
        error = null;
        foreach (var spec in raw)
        {
            var parts = spec.Split(':', 2, StringSplitOptions.TrimEntries);
            if (string.IsNullOrWhiteSpace(parts[0]))
            {
                error = $"--field-group '{spec}' has no name. Expected <Name>[:<F1>,<F2>,…].";
                return false;
            }
            var groupFields = parts.Length > 1
                ? parts[1].Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                : Array.Empty<string>();
            specs.Add(new TableFieldGroupSpec(parts[0], groupFields));
        }
        return true;
    }

    private static bool TryParseIndexes(string[] raw, out List<TableIndexSpec> specs, out string? error)
    {
        specs = [];
        error = null;
        foreach (var spec in raw)
        {
            var parts = spec.Split(':', StringSplitOptions.TrimEntries);
            if (parts.Length < 2 || string.IsNullOrWhiteSpace(parts[0]) || string.IsNullOrWhiteSpace(parts[1]))
            {
                error = $"--index '{spec}' needs a name and a field list: <Name>:<F1>,<F2>[:unique][:alternate-key][:valid-time-state[=Gap|NoGap]].";
                return false;
            }
            var idxFields = parts[1].Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            bool unique = false, alternateKey = false, vts = false;
            string? vtsMode = null;
            foreach (var flag in parts.Skip(2))
            {
                var f = flag.ToLowerInvariant();
                if (f == "unique") { unique = true; continue; }
                if (f is "alternate-key" or "alternatekey") { alternateKey = true; continue; }
                if (f.StartsWith("valid-time-state") || f.StartsWith("validtimestate"))
                {
                    vts = true;
                    var eq = f.IndexOf('=');
                    if (eq >= 0)
                    {
                        vtsMode = f[(eq + 1)..] switch
                        {
                            "gap" => "Gap",
                            "nogap" => "NoGap",
                            _ => null,
                        };
                        if (vtsMode is null)
                        {
                            error = $"--index '{spec}': valid-time-state mode must be Gap or NoGap.";
                            return false;
                        }
                    }
                    continue;
                }
                error = $"--index '{spec}': unknown flag '{flag}'. Expected unique | alternate-key | valid-time-state[=Gap|NoGap].";
                return false;
            }
            specs.Add(new TableIndexSpec(parts[0], idxFields, unique, alternateKey, vts, vtsMode));
        }
        return true;
    }

    private static TableFieldSpec ParseField(string raw)
    {
        var parts = raw.Split(':', StringSplitOptions.TrimEntries);
        var name = parts.Length > 0 ? parts[0] : "";
        var edt = parts.Length > 1 ? parts[1] : null;
        var mandatory = parts.Length > 2 && string.Equals(parts[2], "mandatory", StringComparison.OrdinalIgnoreCase);
        return new TableFieldSpec(name, string.IsNullOrEmpty(edt) ? null : edt, null, mandatory);
    }
}

public sealed class GenerateClassCommand : Command<GenerateClassCommand.Settings>
{
    public sealed class Settings : GenerateSettings
    {
        [CommandArgument(0, "<NAME>")]
        public string Name { get; init; } = "";

        [CommandOption("--extends <BASE>")]
        public string? Extends { get; init; }

        [CommandOption("--non-final")]
        public bool NonFinal { get; init; }
    }

    public override int Execute(CommandContext ctx, Settings settings)
    {
        var kind = OutputMode.Resolve(settings.Output);
        if (string.IsNullOrWhiteSpace(settings.Name))
            return RenderHelpers.Render(kind, ToolResult<object>.Fail("BAD_INPUT", "Class name required."));

        var doc = XppScaffolder.Class(settings.Name, settings.Extends, !settings.NonFinal);

        var gate = GenerateInstaller.Gate(
            settings, settings.Name, doc,
            requiredSymbols: string.IsNullOrWhiteSpace(settings.Extends) ? null : new[] { settings.Extends! });
        if (gate.Failure is not null) return RenderHelpers.Render(kind, gate.Failure);

        return GenerateInstaller.Emit(
            kind, gate, settings, "class", Folders.Class, settings.Name,
            doc,
            r => new
            {
                kind = "AxClass", name = settings.Name, source = r.Source,
                path = r.Path, bytes = r.Bytes, backup = r.Backup, model = settings.InstallTo,
                grounding = gate.Grounding,
            },
            gate.Warnings);
    }
}

public sealed class GenerateCocCommand : Command<GenerateCocCommand.Settings>
{
    public sealed class Settings : GenerateSettings
    {
        [CommandArgument(0, "<TARGET>")]
        [System.ComponentModel.Description("Target class name. Extension will be named <TARGET>_Extension.")]
        public string Target { get; init; } = "";

        [CommandOption("--method <NAME>")]
        [System.ComponentModel.Description("Repeatable. Each method gets a `next` wrapper.")]
        public string[] Methods { get; init; } = Array.Empty<string>();
    }

    public override int Execute(CommandContext ctx, Settings settings)
    {
        var kind = OutputMode.Resolve(settings.Output);
        if (string.IsNullOrWhiteSpace(settings.Target))
            return RenderHelpers.Render(kind, ToolResult<object>.Fail("BAD_INPUT", "Target class required."));
        if (settings.Methods.Length == 0)
            return RenderHelpers.Render(kind, ToolResult<object>.Fail("BAD_INPUT", "At least one --method required."));

        // A target spelled as the extension itself (Base.Suffix, Base_Extension) is
        // rewritten to its base rather than suffixed twice.
        var target = D365FO.Core.ObjectNamingRules.NormalizeExtensionTarget(settings.Target, out var renameNote);

        // Guardrail: warn if the target already has CoC wrappers, and resolve
        // the target's AOT kind so [ExtensionOf] uses the right intrinsic
        // (tableStr for tables, classStr for classes, …).
        var warnings = new List<string>();
        if (renameNote is not null) warnings.Add(renameNote);
        var targetKind = "class";
        try
        {
            var repo = RepoFactory.Create();
            var existing = repo.FindCocExtensions(target);
            if (existing.Count > 0)
                warnings.Add($"There are already {existing.Count} CoC extension(s) of {target}. Consider extending an existing one instead of stacking a new wrapper.");
            var kinds = repo.SymbolKinds(target);
            targetKind = kinds.FirstOrDefault(k => k is "class" or "table" or "form" or "data-entity" or "map" or "view") ?? "class";
        }
        catch { /* index may be empty; not fatal */ }

        var doc = XppScaffolder.CocExtension(target, targetKind, settings.Methods);

        // Grounding gate: prove the target and every wrapped method against the
        // index; fail closed under D365FO_GROUNDING_ENFORCE=true.
        var gate = GenerateInstaller.Gate(
            settings,
            target,
            doc,
            settings.Methods.Select(m => (target, m)));
        if (gate.Failure is not null) return RenderHelpers.Render(kind, gate.Failure);
        warnings.AddRange(gate.Warnings);

        return GenerateInstaller.Emit(
            kind, gate, settings, "class", Folders.Class, target + "_Extension",
            doc,
            r => new
            {
                kind = "AxClass",
                name = target + "_Extension",
                source = r.Source,
                path = r.Path,
                bytes = r.Bytes,
                backup = r.Backup,
                methodCount = settings.Methods.Length,
                model = settings.InstallTo,
                grounding = gate.Grounding,
            },
            warnings);
    }
}

public sealed class GenerateSimpleListCommand : Command<GenerateSimpleListCommand.Settings>
{
    public sealed class Settings : GenerateSettings
    {
        [CommandArgument(0, "<FORM_NAME>")]
        public string FormName { get; init; } = "";

        [CommandOption("--table <TABLE>")]
        public string? Table { get; init; }
    }

    public override int Execute(CommandContext ctx, Settings settings)
    {
        return GenerateFormImpl.Run(
            settings:     settings,
            output:       settings.Output,
            formName:     settings.FormName,
            table:        settings.Table,
            patternRaw:   "SimpleList",
            caption:      null,
            fields:       Array.Empty<string>(),
            sections:     Array.Empty<string>(),
            linesTable:   null);
    }
}

/// <summary>
/// Pattern-aware form scaffolder. Mirrors <c>generate_smart_form</c> from
/// <c>d365fo-mcp-server</c>: nine D365FO patterns, optional grid fields,
/// optional sections (TabPages for TOC / Dialog / Workspace), optional lines
/// datasource for <c>DetailsTransaction</c>.
/// </summary>
public sealed class GenerateFormCommand : Command<GenerateFormCommand.Settings>
{
    public sealed class Settings : GenerateSettings
    {
        [CommandArgument(0, "<FORM_NAME>")]
        public string FormName { get; init; } = "";

        [CommandOption("--pattern <PATTERN>")]
        [System.ComponentModel.Description("Form pattern: SimpleList | SimpleListDetails | DetailsMaster | DetailsTransaction | Dialog | TableOfContents | Lookup | ListPage | Workspace. Aliases (master, transaction, toc, panorama, …) are accepted. Catalog-only patterns (Wizard, DropDialog, FormPart*, …) are rejected with the list of generatable ones — they are not silently downgraded.")]
        public string? Pattern { get; init; }

        [CommandOption("--table <TABLE>")]
        [System.ComponentModel.Description("Primary datasource table.")]
        public string? Table { get; init; }

        [CommandOption("--caption <TEXT>")]
        [System.ComponentModel.Description("Caption / title (literal text or @File:Label).")]
        public string? Caption { get; init; }

        [CommandOption("--field <NAME>")]
        [System.ComponentModel.Description("Field name to render as a grid / detail column (repeatable).")]
        public string[] Fields { get; init; } = Array.Empty<string>();

        [CommandOption("--section <SECTION>")]
        [System.ComponentModel.Description("TabPage / section (repeatable). Format: <Name>:<Caption>. Used by TableOfContents, Dialog, Workspace.")]
        public string[] Sections { get; init; } = Array.Empty<string>();

        [CommandOption("--lines-table <TABLE>")]
        [System.ComponentModel.Description("Lines datasource table for DetailsTransaction.")]
        public string? LinesTable { get; init; }
    }

    public override int Execute(CommandContext ctx, Settings settings)
    {
        return GenerateFormImpl.Run(
            settings:     settings,
            output:       settings.Output,
            formName:     settings.FormName,
            table:        settings.Table,
            patternRaw:   settings.Pattern,
            caption:      settings.Caption,
            fields:       settings.Fields,
            sections:     settings.Sections,
            linesTable:   settings.LinesTable);
    }
}

internal static class GenerateFormImpl
{
    public static int Run(
        GenerateSettings settings,
        string? output,
        string formName,
        string? table,
        string? patternRaw,
        string? caption,
        IReadOnlyList<string> fields,
        IReadOnlyList<string> sections,
        string? linesTable)
    {
        var kind = OutputMode.Resolve(output);
        var installTo = settings.InstallTo;
        var outPath   = settings.Out;
        if (string.IsNullOrWhiteSpace(formName))
            return RenderHelpers.Render(kind, ToolResult<object>.Fail("BAD_INPUT", "Form name required."));

        if (!FormPatternNormalizer.TryNormalize(patternRaw, out var pattern, out var patternError))
        {
            // Registry fallback (port of upstream formControlExpander): a pattern the
            // catalog knows but no hand-written template covers is expanded from the
            // AOT-derived pattern registry — the same data the validator enforces, so
            // the skeleton is pattern-correct by construction. The nine templated
            // patterns keep their proven templates.
            var catalogSpec = D365FO.Core.FormPatterns.FormPatternCatalog.Resolve(patternRaw!);
            string? expandBlocker = null;
            if (catalogSpec is not null)
            {
                if (D365FO.Core.FormPatterns.FormPatternExpander.CanExpand(catalogSpec, out expandBlocker))
                    return RunExpanded(settings, kind, formName, table, catalogSpec, caption, fields, sections, linesTable);
            }
            var blockerNote = expandBlocker is not null
                ? $" Registry expansion is not possible either: {expandBlocker}."
                : string.Empty;
            return RenderHelpers.Render(kind, ToolResult<object>.Fail("BAD_INPUT", patternError! + blockerNote));
        }

        // Patterns that need a datasource: everything except Dialog / TableOfContents (where it is optional).
        var dsRequired = pattern is not (FormPattern.Dialog or FormPattern.TableOfContents);
        if (dsRequired && string.IsNullOrWhiteSpace(table))
            return RenderHelpers.Render(kind, ToolResult<object>.Fail("BAD_INPUT", $"--table <TABLE> required for pattern {pattern}."));

        // An operational workspace's list sections are not inline: a tabbed-list page
        // must hold a FormPartControl pointing at a *separate* form whose own pattern is
        // FormPartSectionList. Placing fields there produces a form the AOS rejects, and
        // pointing a FormPart at a menu item that does not exist is a dangling
        // reference — so this asks rather than emitting either.
        if (pattern == FormPattern.Workspace && (fields.Count > 0 || sections.Count > 0))
        {
            return RenderHelpers.Render(kind, ToolResult<object>.Fail(
                "BAD_INPUT",
                "--field / --section cannot be placed on a Workspace form.",
                "An operational workspace's lists live in separate FormPartSectionList forms referenced by a "
                + "FormPart control. Generate the workspace shell first (no --field/--section), then create each "
                + "list form with `generate form --pattern SimpleList` and add a FormPart pointing at its menu item."));
        }

        var hasInstall = !string.IsNullOrWhiteSpace(installTo);
        var hasOut     = !string.IsNullOrWhiteSpace(outPath);
        if (!hasInstall && !hasOut)
            return RenderHelpers.Render(kind, ToolResult<object>.Fail("BAD_INPUT", "--out or --install-to is required."));

        var sectionSpecs = ParseSections(sections);

        // Caption resolution: explicit --caption wins; otherwise reuse the bound
        // table's Label from the index (a raw-text caption trips
        // BPErrorLabelIsText, the table's label is already translated). Falls
        // back to no <Caption> element when neither is available.
        var effectiveCaption = caption;
        var preflightWarnings = new List<string>();
        var absentFieldGroups = new List<string>();
        if (!string.IsNullOrWhiteSpace(table))
        {
            try
            {
                var t = RepoFactory.Create().GetTableDetails(table!)?.Table;
                if (t is not null)
                {
                    if (string.IsNullOrWhiteSpace(effectiveCaption) && !string.IsNullOrWhiteSpace(t.Label))
                        effectiveCaption = t.Label;

                    // The index is a cache that is never invalidated on delete or
                    // rollback — verify the table's XML still exists before
                    // trusting the hit, so a form is not bound to a phantom table.
                    if (!string.IsNullOrEmpty(t.SourcePath) && !File.Exists(t.SourcePath))
                    {
                        preflightWarnings.Add(
                            $"Table '{table}' is in the index but its XML no longer exists on disk ({t.SourcePath}) — " +
                            "it may have been deleted or rolled back. Run `d365fo index refresh` and verify the table before using this form.");
                    }
                    else if (!string.IsNullOrEmpty(t.SourcePath))
                    {
                        // SimpleList/SimpleListDetails/DetailsMaster/DetailsTransaction
                        // controls reference the table's Overview (and sometimes
                        // General) field group via <DataGroup>; a missing group
                        // renders an empty grid and flags BP on build.
                        foreach (var group in RequiredFieldGroups(pattern))
                        {
                            if (!TableDefinesFieldGroup(t.SourcePath!, group))
                            {
                                // Positively established absent (the table's XML was read) —
                                // the binding is OMITTED from the emitted form, because naming
                                // a group the table does not declare is a build error that an
                                // incremental build passes silently. TableDefinesFieldGroup
                                // returns true when it cannot read the table, so a group only
                                // lands here on real evidence.
                                absentFieldGroups.Add(group);
                                preflightWarnings.Add(
                                    $"Form pattern {pattern} binds field group '{group}' but table '{table}' does not define it " +
                                    "(extension-added groups are not checked). The <DataGroup> binding was omitted from the generated form; " +
                                    "add the field group to the table and re-bind it for the shipped-form look.");
                            }
                        }
                    }
                }
            }
            catch { /* index may be empty; not fatal */ }
        }

        string xml;
        try
        {
            xml = XppScaffolder.Form(
                formName:        formName,
                dataSourceTable: table,
                pattern:         pattern,
                caption:         effectiveCaption,
                gridFields:      fields,
                sections:        sectionSpecs,
                linesTable:      linesTable,
                controlTypeResolver: BuildControlTypeResolver(table, linesTable),
                omitDataGroups:  absentFieldGroups);
        }
        catch (Exception ex)
        {
            return RenderHelpers.Render(kind, ToolResult<object>.Fail("RENDER_FAILED", ex.Message));
        }

        // Pre-write pattern self-test: structural violations (FP001-FP005, FP007)
        // block the write while D365FO_FORM_PATTERN_ENFORCE=true (the default),
        // mirroring the upstream MCP form-pattern write gate.
        var patternReport = D365FO.Core.FormPatterns.FormPatternValidator.ValidateXml(xml);
        var patternWarnings = preflightWarnings
            .Concat(patternReport.Violations
                .Select(v => $"form-pattern {v.Rule} [{v.Severity}] {v.Path}: {v.Excerpt}"))
            .ToList();
        if (patternReport.HasErrors && FormPatternGate.EnforcementEnabled)
        {
            var errors = patternReport.Violations.Where(v => v.Severity == "error")
                .Select(v => $"{v.Rule} {v.Path}: {v.Excerpt} → {v.Fix}");
            return RenderHelpers.Render(kind, ToolResult<object>.Fail(
                "FORM_PATTERN_VIOLATION",
                $"Generated form violates pattern {patternReport.Pattern} (D365FO_FORM_PATTERN_ENFORCE=true):\n" +
                string.Join("\n", errors),
                "Fix the structure (see `d365fo get form-pattern " + (patternReport.Pattern ?? "<pattern>") + "`), " +
                "or set D365FO_FORM_PATTERN_ENFORCE=false to bypass the gate."));
        }

        // A form carries no X++ for the gate to resolve, so what it can prove is the
        // datasource: a form bound to a table the index has never seen is the hallucination
        // this gate exists to stop, and it used to sail straight through (issue #161).
        var gate = GenerateInstaller.Gate(
            settings, formName, doc: null,
            requiredSymbols: new[] { table, linesTable }.Where(t => !string.IsNullOrWhiteSpace(t)).Select(t => t!));
        if (gate.Failure is not null) return RenderHelpers.Render(kind, gate.Failure);
        patternWarnings.AddRange(gate.Warnings);

        return GenerateInstaller.EmitString(
            kind, gate, settings, "form", Folders.Form, formName,
            xml,
            r => new
            {
                kind         = "AxForm",
                name         = formName,
                pattern      = pattern.ToString(),
                source       = r.Source,
                path         = r.Path,
                bytes        = r.Bytes,
                backup       = r.Backup,
                model        = installTo,
                fieldCount   = fields.Count,
                sectionCount = sectionSpecs.Count,
                patternCheck = new
                {
                    enforced = FormPatternGate.EnforcementEnabled,
                    errors   = patternReport.ErrorCount,
                    warnings = patternReport.WarningCount,
                },
                grounding    = gate.Grounding,
            },
            patternWarnings);
    }

    /// <summary>
    /// The registry-expansion path for patterns without a hand-written template (Wizard,
    /// DropDialog, FormPart*, Task*, …). Emits the pattern's required skeleton from the
    /// AOT-derived registry, self-tests it against the SAME validator that gates the
    /// templates, and refuses on any structural error — the expander promises
    /// pattern-correctness by construction, so an error means the pattern genuinely cannot
    /// be materialised and hand-authoring is the honest answer.
    /// </summary>
    private static int RunExpanded(
        GenerateSettings settings,
        OutputMode.Kind kind,
        string formName,
        string? table,
        D365FO.Core.FormPatterns.FormPatternSpec catalogSpec,
        string? caption,
        IReadOnlyList<string> fields,
        IReadOnlyList<string> sections,
        string? linesTable)
    {
        var patternXmlName = catalogSpec.XmlName;
        if (sections.Count > 0 || !string.IsNullOrWhiteSpace(linesTable))
        {
            return RenderHelpers.Render(kind, ToolResult<object>.Fail("BAD_INPUT",
                $"--section / --lines-table are template features; registry expansion of '{patternXmlName}' " +
                "emits the pattern's required skeleton only.",
                "Generate without them, then add sections by hand (`d365fo get form-pattern " + patternXmlName + "` shows the structure)."));
        }

        var hasInstall = !string.IsNullOrWhiteSpace(settings.InstallTo);
        var hasOut     = !string.IsNullOrWhiteSpace(settings.Out);
        if (!hasInstall && !hasOut)
            return RenderHelpers.Render(kind, ToolResult<object>.Fail("BAD_INPUT", "--out or --install-to is required."));

        // Caption: explicit wins; otherwise reuse the bound table's label, same as the
        // template path — a raw-text caption trips BPErrorLabelIsText.
        var effectiveCaption = caption;
        if (!string.IsNullOrWhiteSpace(table) && string.IsNullOrWhiteSpace(effectiveCaption))
        {
            try
            {
                var label = RepoFactory.Create().GetTableDetails(table!)?.Table.Label;
                if (!string.IsNullOrWhiteSpace(label)) effectiveCaption = label;
            }
            catch { /* index may be empty; not fatal */ }
        }

        var doc = D365FO.Core.FormPatterns.FormPatternExpander.Expand(catalogSpec, new D365FO.Core.FormPatterns.FormExpandOptions(
            formName,
            DsTable: table,
            Caption: effectiveCaption,
            GridFields: fields,
            ControlTypeResolver: BuildControlTypeResolver(table, null)));
        if (doc is null)
        {
            return RenderHelpers.Render(kind, ToolResult<object>.Fail("RENDER_FAILED",
                $"Registry expansion of '{patternXmlName}' produced nothing.",
                $"Author the form by hand — `d365fo get form-pattern {patternXmlName}` shows the required structure."));
        }
        doc.Declaration = new System.Xml.Linq.XDeclaration("1.0", "utf-8", null);
        var xml = doc.Declaration + Environment.NewLine + doc.ToString();

        // Self-test with the same gate the templates pass through. An error here is a
        // refusal, not a bypassable warning: the expander's whole promise is structural
        // correctness, and enforcement flags exist for hand-written XML, not for this.
        var patternReport = D365FO.Core.FormPatterns.FormPatternValidator.ValidateXml(xml);
        if (patternReport.HasErrors)
        {
            var errors = patternReport.Violations.Where(v => v.Severity == "error")
                .Select(v => $"{v.Rule} {v.Path}: {v.Excerpt} → {v.Fix}");
            return RenderHelpers.Render(kind, ToolResult<object>.Fail(
                "FORM_PATTERN_VIOLATION",
                $"Registry expansion of '{patternXmlName}' failed its own pattern self-test:\n" + string.Join("\n", errors),
                $"Author the form by hand — `d365fo get form-pattern {patternXmlName}` shows the required structure."));
        }
        var patternWarnings = patternReport.Violations
            .Select(v => $"form-pattern {v.Rule} [{v.Severity}] {v.Path}: {v.Excerpt}")
            .ToList();
        patternWarnings.Add(
            $"'{patternXmlName}' was expanded from the AOT pattern registry (no hand-written template exists): the " +
            "required skeleton is complete, but content — fields, parts, sub-patterns the registry leaves open — is yours to add.");

        var gate = GenerateInstaller.Gate(
            settings, formName, doc: null,
            requiredSymbols: string.IsNullOrWhiteSpace(table) ? null : new[] { table! });
        if (gate.Failure is not null) return RenderHelpers.Render(kind, gate.Failure);
        patternWarnings.AddRange(gate.Warnings);

        return GenerateInstaller.EmitString(
            kind, gate, settings, "form", Folders.Form, formName,
            xml,
            r => new
            {
                kind         = "AxForm",
                name         = formName,
                pattern      = patternXmlName,
                source       = r.Source,
                expandedFromRegistry = true,
                path         = r.Path,
                bytes        = r.Bytes,
                backup       = r.Backup,
                model        = settings.InstallTo,
                fieldCount   = fields.Count,
                patternCheck = new
                {
                    errors   = patternReport.ErrorCount,
                    warnings = patternReport.WarningCount,
                },
                grounding    = gate.Grounding,
            },
            patternWarnings);
    }

    /// <summary>
    /// Field name → the control it should be rendered as, resolved through the index.
    /// </summary>
    /// <remarks>
    /// Issue #164 / R5. The form templates emitted <c>AxFormStringControl</c> for every bound
    /// field regardless of what the field was, so a generated form put a text box on a quantity,
    /// a date and a status enum alike. The index knows each field's EDT and enum, and
    /// <see cref="D365FO.Core.FormPatterns.FieldControlTypes"/> knows what shipped forms do with
    /// them. Returns null when there is no index or no table — the templates then fall back to
    /// the string control, which is exactly the old behaviour and no worse.
    /// </remarks>
    private static Func<string, (string AxType, string TypeElement)>? BuildControlTypeResolver(
        string? table, string? linesTable)
    {
        if (string.IsNullOrWhiteSpace(table)) return null;

        Dictionary<string, (string? Edt, string? EnumType)> byField;
        try
        {
            var repo = RepoFactory.Create();
            byField = new Dictionary<string, (string?, string?)>(StringComparer.OrdinalIgnoreCase);
            foreach (var t in new[] { table, linesTable }.Where(t => !string.IsNullOrWhiteSpace(t)))
            {
                var details = repo.GetTableDetails(t!);
                if (details is null) continue;
                foreach (var f in details.Fields)
                {
                    // First table wins: the header's fields are the ones the pattern's primary
                    // controls bind to.
                    if (byField.ContainsKey(f.Name)) continue;
                    // The index records the field's kind in Type ("ExtendedDataType" /
                    // "EnumType") and the name of that type in EdtName — one column doing two
                    // jobs, so which job it is doing has to be read off Type.
                    var isEnum = string.Equals(f.Type, "EnumType", StringComparison.OrdinalIgnoreCase);
                    byField[f.Name] = (isEnum ? null : f.EdtName, isEnum ? f.EdtName : null);
                }
            }
            if (byField.Count == 0) return null;

            return field =>
            {
                if (!byField.TryGetValue(field, out var info))
                    return (D365FO.Core.FormPatterns.FieldControlTypes.DefaultControl, "String");

                if (info.EnumType is not null)
                    return D365FO.Core.FormPatterns.FieldControlTypes.For("AxTableFieldEnum", info.EnumType);

                string? baseType = null;
                try { baseType = info.Edt is null ? null : repo.GetEdt(info.Edt)?.BaseType; }
                catch { /* index hiccup — fall back below */ }

                return D365FO.Core.FormPatterns.FieldControlTypes.ForEdtBaseType(baseType);
            };
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Field groups a pattern's controls reference via &lt;DataGroup&gt; (see FormTemplates/*.template.xml).</summary>
    internal static IReadOnlyList<string> RequiredFieldGroups(FormPattern pattern) => pattern switch
    {
        FormPattern.SimpleList or FormPattern.DetailsTransaction => new[] { "Overview" },
        FormPattern.SimpleListDetails or FormPattern.DetailsMaster => new[] { "Overview", "General" },
        _ => Array.Empty<string>(),
    };

    internal static bool TableDefinesFieldGroup(string tableXmlPath, string groupName)
    {
        try
        {
            var doc = System.Xml.Linq.XDocument.Load(tableXmlPath);
            return doc.Root?
                .Element("FieldGroups")?
                .Elements("AxTableFieldGroup")
                .Any(g => string.Equals(g.Element("Name")?.Value, groupName, StringComparison.OrdinalIgnoreCase)) == true;
        }
        catch
        {
            // Unreadable XML must not block form generation — skip the warning.
            return true;
        }
    }

    private static IReadOnlyList<FormSectionSpec> ParseSections(IReadOnlyList<string> raw)
    {
        if (raw.Count == 0) return Array.Empty<FormSectionSpec>();
        var list = new List<FormSectionSpec>(raw.Count);
        foreach (var s in raw)
        {
            var idx = s.IndexOf(':');
            if (idx > 0)
                list.Add(new FormSectionSpec(s[..idx].Trim(), s[(idx + 1)..].Trim()));
            else
                list.Add(new FormSectionSpec(s.Trim(), s.Trim()));
        }
        return list;
    }
}
