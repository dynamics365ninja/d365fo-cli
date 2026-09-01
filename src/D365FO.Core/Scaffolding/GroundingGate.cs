using D365FO.Core.Guardrails;
using D365FO.Core.Index;
using D365FO.Core.Validation;
using System.Xml.Linq;

namespace D365FO.Core.Scaffolding;

/// <summary>
/// Write-side grounding enforcement for every generated artefact, on either surface —
/// port of the upstream MCP server's fail-closed gate (provenance token +
/// <c>gateOnReferenceErrors</c>).
///
/// It lived beside the CLI's generate commands, so it ran on every CLI write and on no MCP
/// write at all: setting <c>D365FO_GROUNDING_ENFORCE=true</c> enforced nothing over MCP, and a
/// deployment that believed it had turned grounding on had turned it on for one of its two
/// front doors.
///
/// Behaviour:
///   - Default: checks run, problems surface as warnings, the write proceeds.
///   - <c>D365FO_GROUNDING_ENFORCE=true</c>: a valid object-bound grounding
///     token (from <c>d365fo prepare change/create</c>) is required, and the
///     write is rejected when the generated X++ contains identifiers the index
///     cannot prove (hallucinations) or BP errors.
/// A gate failure must never be caused by gate infrastructure itself — index
/// errors degrade to warnings, mirroring upstream ("resolver failure must
/// never block writes").
/// </summary>
public static class GroundingGate
{
    /// <summary>
    /// The verdict of one gate run, and the handle a command writes through.
    /// </summary>
    /// <remarks>
    /// <see cref="GenerateInstaller.Write"/> takes one of these, so a generate command cannot
    /// reach the writer without having gated first — the guarantee issue #161 asked for. It also
    /// makes the object the natural place to hang the property-honesty report (R6): the gate
    /// knows what was requested, and every document that reaches disk passes through here, so
    /// the two halves of "did what I asked for survive?" meet without a command having to
    /// arrange it.
    /// </remarks>
    public sealed record GateResult(object Grounding, List<string> Warnings, ToolResult<object>? Failure)
    {
        private readonly List<string> _written = [];
        private readonly List<string> _honestyWarnings = [];

        /// <summary>What the caller asked for, as option/value pairs.</summary>
        public IReadOnlyList<(string Option, string Value)> Requested { get; init; } = [];

        /// <summary>Every document this gate has seen written, in order.</summary>
        public IReadOnlyList<string> WrittenDocuments => _written;

        /// <summary>
        /// One artefact this gate saw reach a target, named the way the metadata provider
        /// names it. <see cref="AxKind"/>/<see cref="Name"/> are null when the identity could
        /// not be read off the document — <c>--verify</c> reports that rather than skipping
        /// silently.
        /// </summary>
        public readonly record struct Artefact(string? AxKind, string? Name, string? Path);

        private readonly List<Artefact> _artefacts = [];

        /// <summary>Every artefact this gate has seen written or installed, in order.</summary>
        /// <remarks>
        /// Issue #180. <c>--verify</c> reads artefacts back by name through the metadata
        /// provider, and it can only do that for what was actually emitted — so the list is
        /// built by <see cref="GenerateInstaller.Write"/> itself rather than declared per
        /// command. A per-command declaration is the hand-maintained table that goes stale
        /// the first time someone adds a second output file, which is precisely how the flag
        /// came to be inert for twenty-four subcommands.
        /// </remarks>
        public IReadOnlyList<Artefact> Artefacts => _artefacts;

        /// <summary>Record an artefact as emitted. Duplicate paths collapse to one entry.</summary>
        public void RecordArtefact(string? axKind, string? name, string? path)
        {
            if (path is not null && _artefacts.Any(a => string.Equals(a.Path, path, StringComparison.OrdinalIgnoreCase)))
                return;
            _artefacts.Add(new Artefact(axKind, string.IsNullOrWhiteSpace(name) ? null : name, path));
        }

        /// <summary>Requested values that reached none of the written documents.</summary>
        public IReadOnlyList<PropertyGap> PropertyGaps { get; private set; } = [];

        /// <summary>
        /// Record a document as written and re-run the honesty reconciliation over everything
        /// written so far.
        /// </summary>
        /// <remarks>
        /// Recomputed rather than accumulated, because a multi-document command legitimately
        /// satisfies a request in its second file: <c>generate report --dataset X</c> puts the
        /// dataset in the DP class, not in the AxReport. Judging each document alone would report
        /// a gap that the next write fills. The previously reported gaps are removed from
        /// <see cref="Warnings"/> before the new ones go in, so the list never accumulates
        /// superseded findings.
        /// </remarks>
        public void Observe(string xml)
        {
            if (string.IsNullOrWhiteSpace(xml)) return;
            _written.Add(xml);

            foreach (var stale in _honestyWarnings) Warnings.Remove(stale);
            _honestyWarnings.Clear();

            PropertyGaps = PropertyHonesty.Reconcile(Requested, string.Join('\n', _written));
            _honestyWarnings.AddRange(PropertyGaps.Select(g => $"property-honesty: {g}"));
            Warnings.AddRange(_honestyWarnings);
        }
    }

    /// <param name="token">Value of --grounding-token (may be null).</param>
    /// <param name="targetObject">The AOT object this write is bound to (CoC target, extension target…).</param>
    /// <param name="doc">Scaffolded XML; X++ inside Declaration/Source elements is validated.</param>
    /// <param name="requiredMethods">Methods that must exist on their owner (e.g. CoC-wrapped methods).</param>
    /// <param name="requiredSymbols">AOT names that must exist in the index (e.g. an extension's target object).</param>
    /// <param name="requested">
    /// Option/value pairs the caller supplied, for the property-honesty reconciliation (R6).
    /// Empty means "do not reconcile" — the check is silent rather than reporting everything as
    /// missing.
    /// </param>
    public static GateResult Check(
        string? token,
        string targetObject,
        XDocument? doc,
        IEnumerable<(string Owner, string Method)>? requiredMethods = null,
        IEnumerable<string>? requiredSymbols = null,
        IReadOnlyList<(string Option, string Value)>? requested = null,
        MetadataRepository? repository = null)
    {
        var enforce = ProvenanceStore.EnforcementEnabled;
        var warnings = new List<string>();
        var asked = requested ?? [];

        // ── 1. Grounding token ───────────────────────────────────────────────
        bool tokenValid = false;
        string? tokenReason = null;
        if (!string.IsNullOrWhiteSpace(token) || enforce)
        {
            (tokenValid, var reason) = ProvenanceStore.Validate(token, targetObject);
            if (!tokenValid)
            {
                tokenReason = reason;
                if (enforce)
                {
                    return new GateResult(new { enforced = true, tokenValid = false }, warnings,
                        ToolResult<object>.Fail("GROUNDING_REQUIRED", reason,
                            $"Run `d365fo prepare change {targetObject}` (or `prepare create`) and pass the returned token via --grounding-token."))
                        { Requested = asked };
                }
                warnings.Add($"grounding: {reason}");
            }
        }

        // ── 2. Semantic + BP self-check over the generated X++ ───────────────
        int refErrors = 0, refWarnings = 0, bpErrors = 0, bpWarnings = 0, verified = 0;
        var violationDetails = new List<string>();
        var xpp = ExtractXppSource(doc);
        var repo = repository;

        if (repo is not null)
        {
            if (!string.IsNullOrWhiteSpace(xpp))
            {
                try
                {
                    var resolved = ReferenceResolver.Resolve(xpp, repo);
                    verified = resolved.VerifiedCount;
                    foreach (var v in resolved.Violations)
                    {
                        if (v.Severity == "error") refErrors++; else refWarnings++;
                        violationDetails.Add($"[{v.Kind}] line {v.Line}: {v.Identifier} — {v.Detail}");
                    }

                    var stats = repo.HasPropertyStats() ? repo : (IPropertyStatsProvider?)null;
                    foreach (var v in XppValidator.Validate(xpp, XppValidator.CodeTypeXpp, stats))
                    {
                        if (v.Severity == "error") bpErrors++; else bpWarnings++;
                        violationDetails.Add($"[{v.Rule}] line {v.Line}: {v.Excerpt} — {v.Fix}");
                    }
                }
                catch (Exception ex)
                {
                    warnings.Add($"grounding self-check skipped: {ex.Message}");
                }
            }

            foreach (var (owner, method) in requiredMethods ?? Array.Empty<(string, string)>())
            {
                try
                {
                    if (repo.FindMethod(owner, method) is null)
                    {
                        refErrors++;
                        violationDetails.Add($"[unknown-method] {owner}::{method} — not found in the index (checked inheritance chain and extensions). " +
                                             $"Use `d365fo get class {owner}` / `d365fo get table {owner}` for the real method list.");
                    }
                    else
                    {
                        verified++;
                    }
                }
                catch { /* index hiccup — do not block */ }
            }

            foreach (var symbol in requiredSymbols ?? Array.Empty<string>())
            {
                try
                {
                    // Kernel types and kernel enums (NoYes, Exception, Types, …) have no AOT
                    // artifact, so the index cannot prove them — an index-only check is in no
                    // position to fail the call. Without this, `d365fo generate edt MyFlag
                    // --extends NoYesId` derived NoYes itself and then refused it as a
                    // hallucination, and the advised search offered NoYesBlank/NoYesCombo —
                    // different types.
                    if (D365FO.Core.Validation.ReferenceResolver.IsKernelType(symbol))
                    {
                        verified++;
                    }
                    else if (repo.SymbolKinds(symbol).Count == 0)
                    {
                        refErrors++;
                        violationDetails.Add($"[unknown-type] {symbol} — not found in the index. " +
                                             $"Use `d365fo search any {symbol}` to find the correct name.");
                    }
                    else
                    {
                        verified++;
                    }
                }
                catch { /* index hiccup — do not block */ }
            }
        }
        else
        {
            warnings.Add("grounding self-check skipped: no index available (run `d365fo index build` + `extract`).");
        }

        var grounding = new
        {
            enforced = enforce,
            tokenSupplied = !string.IsNullOrWhiteSpace(token),
            tokenValid,
            tokenReason,
            verifiedReferences = verified,
            referenceErrors = refErrors,
            referenceWarnings = refWarnings,
            bpErrors,
            bpWarnings,
            violations = violationDetails.Count > 0 ? violationDetails : null,
        };

        if ((refErrors > 0 || bpErrors > 0) && enforce)
        {
            return new GateResult(grounding, warnings,
                ToolResult<object>.Fail("VALIDATION_FAILED",
                    $"Generated code contains {refErrors} unresolved reference(s) and {bpErrors} BP error(s) (D365FO_GROUNDING_ENFORCE=true):\n" +
                    string.Join("\n", violationDetails),
                    "Fix the identifiers (use the suggested lookup commands), then retry. " +
                    "Run `d365fo validate references` on the corrected code to confirm it is clean."))
                { Requested = asked };
        }

        foreach (var detail in violationDetails)
            warnings.Add($"grounding: {detail}");

        return new GateResult(grounding, warnings, null) { Requested = asked };
    }

    /// <summary>Concatenate X++ from Declaration/Source elements of a scaffolded AOT XML.</summary>
    public static string ExtractXppSource(XDocument? doc)
    {
        if (doc?.Root is null) return "";
        var parts = doc.Root.Descendants()
            .Where(e => e.Name.LocalName is "Declaration" or "Source")
            .Select(e => e.Value)
            .Where(v => !string.IsNullOrWhiteSpace(v));
        return string.Join("\n", parts);
    }
}
