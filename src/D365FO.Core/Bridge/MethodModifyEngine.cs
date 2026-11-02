// <copyright file="MethodModifyEngine.cs" company="d365fo-cli contributors">
// MIT
// </copyright>

using System.Text.Json.Nodes;
using System.Xml.Linq;
using D365FO.Core.Guardrails;
using D365FO.Core.Index;
using D365FO.Core.Validation;
using D365FO.Core.Bridge;

namespace D365FO.Core.Bridge;

/// <summary>
/// Structured method-level modify via <c>D365FO.Bridge</c> (issue #112) — the CLI/MCP
/// parity feature for upstream <c>d365fo_file(action=modify)</c>. Round-trips a single
/// method body through Microsoft's <c>IMetadataProvider</c>:
/// <list type="number">
/// <item><description>read the live object as XML via the bridge's <c>readObjectXml</c>
/// (never the on-disk file, never the SQLite index — those only supply the owning
/// model name and better error messages),</description></item>
/// <item><description>locate the target <c>&lt;Method&gt;</c> with <see cref="XDocument"/>
/// element navigation (never raw string/regex surgery on the XML text),</description></item>
/// <item><description>run <see cref="ReferenceResolver"/> + <see cref="XppValidator"/> over
/// the replacement body and fail closed on any error-severity violation,</description></item>
/// <item><description>write the modified document back via the bridge's
/// <c>updateObject</c>, which itself round-trips through <c>IMetadataProvider</c>.</description></item>
/// </list>
/// There is deliberately no on-disk fallback: when the bridge is unavailable (non-Windows,
/// or <c>D365FO_BRIDGE_ENABLED</c> not set) this fails <see cref="D365FoErrorCodes.BridgeRequired"/>
/// rather than falling back to CDATA string-replacement, which is exactly the failure mode
/// this command replaces (see docs/MIGRATION_FROM_MCP.md).
/// </summary>
public static class MethodModifyEngine
{
    private static readonly IReadOnlyDictionary<string, string> SupportedKinds =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["class"] = "class",
            ["table"] = "table",
            ["edt"] = "edt",
            ["form"] = "form",
        };

    /// <summary>Inputs for a single method replacement.</summary>
    /// <remarks>
    /// Exactly one of <c>NewBody</c> and <c>NewSource</c> is supplied. <c>NewBody</c> is statements
    /// only: they replace what sits between the method's outer braces, and the doc comment,
    /// attributes and signature are kept (issue #231). <c>NewSource</c> is the whole method and
    /// replaces the <c>&lt;Source&gt;</c> as given.
    /// </remarks>
    public sealed record ModifyRequest(
        string Kind,
        string ObjectName,
        string MethodName,
        string? NewBody,
        string? Model = null,
        string? GroundingToken = null,
        string? NewSource = null);

    /// <summary>
    /// Build <see cref="BridgeOptions"/> from the unified config resolver (env vars or
    /// settings.json).
    /// </summary>
    /// <remarks>
    /// This was a copy of <see cref="BridgeGate.DefaultOptions"/>, kept because the gate lived in
    /// the CLI project and Core cannot depend upwards. The gate is in Core now, so the copy —
    /// four settings keys that had to be kept identical in three places or the bridge would be
    /// configured differently depending on which entry point reached it — is gone.
    /// </remarks>
    public static BridgeOptions DefaultBridgeOptions() => BridgeGate.DefaultOptions();

    /// <summary>
    /// Entry point used by the CLI (<c>d365fo modify method</c>) and the MCP
    /// <c>modify_method</c> tool. Spawns (or resolves) the bridge process itself and
    /// fails <see cref="D365FoErrorCodes.BridgeRequired"/> up front when it is not
    /// available — no XML fallback is attempted.
    /// </summary>
    public static ToolResult<object> Modify(ModifyRequest request, MetadataRepository? repo, BridgeOptions? bridgeOptions = null)
    {
        if (CheckInput(request) is { } inputFailure)
            return inputFailure;

        var options = bridgeOptions ?? DefaultBridgeOptions();
        var switchedOff = BridgeGate.ExplicitlyDisabled();
        if (switchedOff || !BridgeClient.IsAvailable(options))
        {
            return ToolResult<object>.Fail(D365FoErrorCodes.BridgeRequired,
                "d365fo modify method requires D365FO.Bridge (.NET Framework 4.8, Windows-only, IMetadataProvider-backed) — " +
                (switchedOff
                    ? "it is switched off here (D365FO_BRIDGE_ENABLED is set to something other than 1/true)."
                    : "it is not available in this environment (non-Windows OS, or the bridge executable could not be resolved)."),
                "Run on a D365FO VM with D365FO_BRIDGE_ENABLED=1 and D365FO_BRIDGE_PATH / D365FO_PACKAGES_PATH set. " +
                "This command intentionally has no raw-XML fallback (see docs/MIGRATION_FROM_MCP.md, issue #112).");
        }

        using var client = new BridgeClient(options);
        return ModifyCore(request, repo, client);
    }

    /// <summary>
    /// The bridge round-trip itself, decoupled from process spawning so tests can pass
    /// a fake in-process <see cref="BridgeClient"/> (see <c>BridgeClientTests.FakeBridge</c>
    /// pattern). Callers are responsible for the <see cref="D365FoErrorCodes.BridgeRequired"/>
    /// availability gate — <see cref="Modify"/> does that for real callers.
    /// </summary>
    internal static ToolResult<object> ModifyCore(
        ModifyRequest request, MetadataRepository? repo, BridgeClient client, string? journalDbOverride = null)
    {
        var kind = (request.Kind ?? string.Empty).Trim().ToLowerInvariant();
        if (!SupportedKinds.ContainsKey(kind))
        {
            return ToolResult<object>.Fail(D365FoErrorCodes.BadInput,
                $"Unsupported object kind '{request.Kind}'. Supported: {string.Join(", ", SupportedKinds.Keys)}.",
                "Enums have no method bodies to modify; use `d365fo generate enum` to change values.");
        }
        if (string.IsNullOrWhiteSpace(request.ObjectName))
            return ToolResult<object>.Fail(D365FoErrorCodes.BadInput, "Object name is required.");
        if (string.IsNullOrWhiteSpace(request.MethodName))
            return ToolResult<object>.Fail(D365FoErrorCodes.BadInput, "Method name is required.");
        if (CheckInput(request) is { } inputFailure)
            return inputFailure;
        var hasBody = !string.IsNullOrWhiteSpace(request.NewBody);

        // ---- 1. Resolve the owning model (index lookup; --model overrides) ----
        var model = request.Model;
        if (string.IsNullOrWhiteSpace(model) && repo is not null)
        {
            model = kind switch
            {
                "class" => repo.GetClassDetails(request.ObjectName)?.Class.Model,
                "table" => repo.GetTableDetails(request.ObjectName)?.Table.Model,
                "edt" => repo.GetEdt(request.ObjectName)?.Model,
                "form" => repo.GetForm(request.ObjectName)?.Form.Model,
                _ => null,
            };
        }
        if (string.IsNullOrWhiteSpace(model))
        {
            return ToolResult<object>.Fail(NotFoundCodeFor(kind),
                $"{kind} '{request.ObjectName}' was not found in the SQLite index and no --model override was supplied.",
                "Run `d365fo index build` + `d365fo index extract`, or pass --model <MODEL> explicitly.");
        }

        // ---- 2. Read the live object via the bridge ----
        JsonObject? readResult;
        try
        {
            readResult = client.SendAsync("readObjectXml", new JsonObject { ["kind"] = kind, ["name"] = request.ObjectName })
                .GetAwaiter().GetResult();
        }
        catch (BridgeException ex)
        {
            return ToolResult<object>.Fail(D365FoErrorCodes.BridgeRequired, "Bridge error while reading the object: " + ex.Message);
        }
        if (readResult is null)
            return ToolResult<object>.Fail(D365FoErrorCodes.BridgeRequired, "Bridge returned no result for readObjectXml.");
        if ((bool?)readResult["ok"] != true)
        {
            var code = (string?)readResult["error"] ?? "READ_FAILED";
            var msg = (string?)readResult["message"] ?? "unknown error";
            return ToolResult<object>.Fail(code == "NOT_FOUND" ? NotFoundCodeFor(kind) : code,
                $"Bridge could not read {kind} '{request.ObjectName}': {msg}");
        }
        var xml = (string?)readResult["xml"];
        if (string.IsNullOrWhiteSpace(xml))
            return ToolResult<object>.Fail("READ_FAILED", "Bridge returned an empty xml payload for " + request.ObjectName + ".");

        // ---- 3. Locate the target method (structured XDocument navigation) ----
        XDocument doc;
        try
        {
            doc = XDocument.Parse(xml);
        }
        catch (Exception ex)
        {
            return ToolResult<object>.Fail("READ_FAILED", "Could not parse XML returned by the bridge: " + ex.Message);
        }

        var methodsContainer = LocateMethodsContainer(doc.Root);
        if (methodsContainer is null)
        {
            return ToolResult<object>.Fail(D365FoErrorCodes.MethodNotFound,
                $"{kind} '{request.ObjectName}' has no top-level <Methods> container — nothing to modify.");
        }

        var existing = methodsContainer.Elements()
            .FirstOrDefault(e => e.Name.LocalName == "Method" &&
                                  string.Equals(Local(e, "Name"), request.MethodName, StringComparison.OrdinalIgnoreCase));
        if (existing is null)
        {
            var known = methodsContainer.Elements()
                .Where(e => e.Name.LocalName == "Method")
                .Select(e => Local(e, "Name"))
                .Where(n => !string.IsNullOrEmpty(n))
                .ToList();
            return ToolResult<object>.Fail(D365FoErrorCodes.MethodNotFound,
                $"Method '{request.MethodName}' does not exist on {kind} '{request.ObjectName}'.",
                known.Count > 0
                    ? $"Existing methods: {string.Join(", ", known)}."
                    : $"{request.ObjectName} declares no methods. Use `d365fo generate` commands to scaffold a new object instead.");
        }

        var oldBody = Local(existing, "Source") ?? string.Empty;

        // ---- 3b. Build the new <Source> before anything is validated or journaled ----
        // --body is spliced between the outer braces, so the doc comment, attributes and
        // signature survive (issue #231). It used to become the whole <Source>.
        string newSource;
        if (hasBody)
        {
            if (kind == "class" && IsInterface(doc.Root))
            {
                return ToolResult<object>.Fail(D365FoErrorCodes.BadInput,
                    $"'{request.ObjectName}' is an interface, and an interface method has no body to replace.",
                    "Implement the method in a class that implements the interface.");
            }

            var spliced = MethodSourceSplicer.Splice(oldBody, request.NewBody!, request.MethodName);
            if (spliced.Error is not null)
                return ToolResult<object>.Fail(spliced.Code!, spliced.Error, spliced.Hint);
            newSource = spliced.Source!;
        }
        else
        {
            newSource = request.NewSource!;
        }

        // ---- 4. Grounding + fail-closed validation gate ----
        var warnings = new List<string>();
        var (grounding, gateFailure) = CheckGrounding(request, repo, warnings);
        if (gateFailure is not null) return gateFailure;

        // ---- 5. Structured replace: only the <Source> CDATA of the matched <Method> ----
        var srcEl = existing.Elements().FirstOrDefault(e => e.Name.LocalName == "Source");
        if (srcEl is null)
        {
            srcEl = new XElement(existing.Name.Namespace + "Source");
            existing.Add(srcEl);
        }
        srcEl.ReplaceAll(new XCData(newSource));

        // ---- 6. Write back via the bridge (IMetadataProvider, never disk) ----
        var newXml = doc.ToString(SaveOptions.DisableFormatting);

        // Journal the exact pre-image before the write. This path used to read the live
        // XML, edit it, and drop the original on the floor — making `modify method` the
        // one write in the CLI that `d365fo undo` could not revert. The entry goes in
        // first so a crash mid-write still leaves an undo record.
        ObjectModifyEngine.RecordJournalEntry(
            new ObjectModifyEngine.WriteTarget(kind, request.ObjectName, model!, IsExtension: false, Exists: true),
            xml,
            $"modify method {kind} {request.ObjectName} {request.MethodName}",
            journalDbOverride);

        JsonObject? updateResult;
        try
        {
            updateResult = client.SendAsync("updateObject", new JsonObject
            {
                ["kind"] = kind,
                ["name"] = request.ObjectName,
                ["model"] = model,
                ["xml"] = newXml,
            }).GetAwaiter().GetResult();
        }
        catch (BridgeException ex)
        {
            return ToolResult<object>.Fail(D365FoErrorCodes.WriteFailed, "Bridge error while writing the object: " + ex.Message);
        }
        if (updateResult is null)
            return ToolResult<object>.Fail(D365FoErrorCodes.WriteFailed, "Bridge returned no result for updateObject.");
        if ((bool?)updateResult["ok"] != true)
        {
            var code = (string?)updateResult["error"] ?? D365FoErrorCodes.WriteFailed;
            var msg = (string?)updateResult["message"] ?? "unknown error";
            return ToolResult<object>.Fail(code, $"Bridge could not update {kind} '{request.ObjectName}': {msg}");
        }

        // Incremental refresh: the touched model's index rows are now stale. No
        // single-object refresh primitive exists yet (index refresh is model-scoped),
        // so — mirroring the existing form-method commands (GenerateFormMethodCommands) —
        // surface a warning instead of re-entrantly invoking the index pipeline.
        warnings.Add($"Index not auto-refreshed — run `d365fo index refresh --model {model}` so '{request.MethodName}' is searchable.");

        return ToolResult<object>.Success(new
        {
            kind,
            name = request.ObjectName,
            method = request.MethodName,
            model,
            source = "bridge",
            mode = hasBody ? "body" : "source",
            oldBody,
            newBody = newSource,
            grounding,
        }, warnings);
    }

    /// <summary>
    /// Grounding token (opt-in enforcement, same convention as
    /// <c>D365FO.Cli.Commands.Generate.GroundingGate</c>) plus an <b>unconditional</b>
    /// reference/BP validation gate — acceptance criteria for #112 requires the write
    /// to be blocked on validation failure regardless of D365FO_GROUNDING_ENFORCE,
    /// since this mutates a live, already-shipped object rather than a scaffold file.
    /// </summary>
    private static (object Grounding, ToolResult<object>? Failure) CheckGrounding(
        ModifyRequest request, MetadataRepository? repo, List<string> warnings)
    {
        int refErrors = 0, refWarnings = 0, bpErrors = 0, bpWarnings = 0, verified = 0;
        var violationDetails = new List<string>();

        if (repo is not null)
        {
            try
            {
                var code = (string.IsNullOrWhiteSpace(request.NewBody) ? request.NewSource : request.NewBody) ?? string.Empty;
                var resolved = ReferenceResolver.Resolve(code, repo);
                verified = resolved.VerifiedCount;
                foreach (var v in resolved.Violations)
                {
                    if (v.Severity == "error") refErrors++; else refWarnings++;
                    violationDetails.Add($"[{v.Kind}] line {v.Line}: {v.Identifier} — {v.Detail}");
                }

                var stats = repo.HasPropertyStats() ? repo : (IPropertyStatsProvider?)null;
                foreach (var v in XppValidator.Validate(code, XppValidator.CodeTypeXpp, stats))
                {
                    if (v.Severity == "error") bpErrors++; else bpWarnings++;
                    violationDetails.Add($"[{v.Rule}] line {v.Line}: {v.Excerpt} — {v.Fix}");
                }
            }
            catch (Exception ex)
            {
                warnings.Add("validation self-check skipped: " + ex.Message);
            }
        }
        else
        {
            warnings.Add("validation self-check skipped: no index available (run `d365fo index build` + `d365fo index extract`).");
        }

        var enforceToken = ProvenanceStore.EnforcementEnabled;
        bool tokenValid = false;
        string? tokenReason = null;
        if (!string.IsNullOrWhiteSpace(request.GroundingToken) || enforceToken)
        {
            (tokenValid, var reason) = ProvenanceStore.Validate(request.GroundingToken, request.ObjectName);
            if (!tokenValid)
            {
                tokenReason = reason;
                if (enforceToken)
                {
                    var groundingFail = new
                    {
                        enforced = true,
                        tokenValid = false,
                    };
                    return (groundingFail, ToolResult<object>.Fail(D365FoErrorCodes.GroundingRequired, reason,
                        $"Run `d365fo prepare change {request.ObjectName} --method {request.MethodName}` and pass the returned token via --grounding-token."));
                }
                warnings.Add("grounding: " + reason);
            }
        }

        var grounding = new
        {
            enforced = enforceToken,
            tokenSupplied = !string.IsNullOrWhiteSpace(request.GroundingToken),
            tokenValid,
            tokenReason,
            verifiedReferences = verified,
            referenceErrors = refErrors,
            referenceWarnings = refWarnings,
            bpErrors,
            bpWarnings,
            violations = violationDetails.Count > 0 ? violationDetails : null,
        };

        if (refErrors > 0 || bpErrors > 0)
        {
            return (grounding, ToolResult<object>.Fail(D365FoErrorCodes.ValidationFailed,
                $"New body for {request.ObjectName}::{request.MethodName} contains {refErrors} unresolved reference(s) " +
                $"and {bpErrors} BP error(s):\n" + string.Join("\n", violationDetails),
                "Fix the identifiers / BP issues (`d365fo validate references`, `d365fo validate xpp`), then retry. " +
                "Unlike `generate`, this gate is not bypassable via D365FO_GROUNDING_ENFORCE — it always blocks writes to a live object."));
        }

        foreach (var detail in violationDetails)
            warnings.Add("validation: " + detail);

        return (grounding, null);
    }

    /// <summary>
    /// Checks that need no bridge: exactly one of body and source, a body that is not a whole
    /// declaration, a source that declares this one method. <see cref="Modify"/> runs them before
    /// the bridge gate, so a malformed call is answered the same way on any machine.
    /// </summary>
    internal static ToolResult<object>? CheckInput(ModifyRequest request)
    {
        var hasBody = !string.IsNullOrWhiteSpace(request.NewBody);
        var hasSource = !string.IsNullOrWhiteSpace(request.NewSource);
        if (hasBody == hasSource)
        {
            return ToolResult<object>.Fail(D365FoErrorCodes.BadInput,
                hasBody
                    ? "Pass either a body or a full source, not both."
                    : "A method body (or a full method source) is required.",
                "--body (MCP: `body`) takes the statements between the braces and keeps the signature. " +
                "--source (MCP: `source`) replaces the whole method, signature included.");
        }

        // A full declaration passed as a body would be spliced inside the old method's braces.
        if (hasBody && MethodSourceSplicer.RefuseDeclarationAsBody(request.NewBody!, request.MethodName ?? string.Empty) is { } refusal)
            return ToolResult<object>.Fail(refusal.Code!, refusal.Error!, refusal.Hint);

        return hasSource ? CheckFullSource(request.NewSource!, request.MethodName ?? string.Empty) : null;
    }

    /// <summary>
    /// A full source must declare exactly one method, under the method's own name. Otherwise the
    /// write would leave a <c>&lt;Method&gt;</c> node the metadata provider rejects (XML014).
    /// </summary>
    private static ToolResult<object>? CheckFullSource(string source, string methodName)
    {
        var declared = MethodSourceSplicer.DeclaredMethodNames(source);
        if (declared is null)
        {
            return ToolResult<object>.Fail(D365FoErrorCodes.BadInput,
                "The source cannot be checked statically (a macro-declared method, or unbalanced braces).",
                "Pass a complete X++ method with balanced braces, or edit this method in Visual Studio.");
        }

        if (declared.Count == 0)
        {
            return ToolResult<object>.Fail(D365FoErrorCodes.BadInput,
                $"--source declares no method. It replaces the whole method, so it needs the signature of '{methodName}' and its braces.",
                "To replace only the statements and keep the signature, use --body (MCP: `body`) instead.");
        }

        if (declared.Count > 1)
        {
            return ToolResult<object>.Fail(D365FoErrorCodes.BadInput,
                $"--source declares {declared.Count} methods ({string.Join(", ", declared)}). One <Method> node holds one method.",
                "Pass one method. Add further methods with `generate` instead.");
        }

        if (!string.Equals(declared[0], methodName, StringComparison.OrdinalIgnoreCase))
        {
            return ToolResult<object>.Fail(D365FoErrorCodes.BadInput,
                $"--source declares '{declared[0]}', but the method being replaced is '{methodName}'.",
                "Keep the method's name. Renaming a method is not a body change.");
        }

        return null;
    }

    /// <summary>
    /// True for an interface: the <c>IsInterface</c> property when the XML carries it, else the
    /// <c>interface</c> keyword in the declaration's header (the on-disk AOT file has only that).
    /// </summary>
    private static bool IsInterface(XElement? root)
    {
        var value = root?.Elements().FirstOrDefault(e => e.Name.LocalName == "IsInterface")?.Value?.Trim();
        if (string.Equals(value, "Yes", StringComparison.OrdinalIgnoreCase)
            || string.Equals(value, "true", StringComparison.OrdinalIgnoreCase))
            return true;

        var declaration = root?.Descendants().FirstOrDefault(e => e.Name.LocalName == "Declaration")?.Value;
        if (string.IsNullOrEmpty(declaration)) return false;
        var blanked = AotMethodSourceRules.BlankComments(declaration);
        var open = blanked.IndexOf('{');
        var header = open < 0 ? blanked : blanked[..open];
        return System.Text.RegularExpressions.Regex.IsMatch(header, @"\binterface\b");
    }

    private static string NotFoundCodeFor(string kind) => kind switch
    {
        "class" => D365FoErrorCodes.ClassNotFound,
        "table" => D365FoErrorCodes.TableNotFound,
        "edt" => D365FoErrorCodes.EdtNotFound,
        "form" => D365FoErrorCodes.FormNotFound,
        _ => "NOT_FOUND",
    };

    /// <summary>
    /// Locate the top-level method container: a direct child <c>&lt;Methods&gt;</c>
    /// of the root (AxTable's shape) or a <c>&lt;Methods&gt;</c> whose parent is
    /// <c>&lt;SourceCode&gt;</c> (AxClass/AxForm's shape) — mirrors the same
    /// resolution order <c>MetadataExtractor.ExtractMethodsWithSources</c> uses, so
    /// "the method the index knows about" and "the method this command edits" never
    /// disagree. Deliberately ignores nested Methods containers under DataSources/
    /// DataControls (form datasource/control override methods — see
    /// <c>FormMethodScaffolder</c>, which has its own dedicated commands).
    /// </summary>
    internal static XElement? LocateMethodsContainer(XElement? root)
    {
        if (root is null) return null;
        return root.Elements().FirstOrDefault(x => x.Name.LocalName == "Methods")
               ?? root.Descendants().FirstOrDefault(x =>
                   x.Name.LocalName == "Methods" &&
                   x.Parent is { } p && p.Name.LocalName == "SourceCode");
    }

    private static string? Local(XElement parent, string localName)
        => parent.Elements().FirstOrDefault(e => e.Name.LocalName == localName)?.Value;
}
