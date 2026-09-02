using System.Text.RegularExpressions;
using D365FO.Core.Index;

namespace D365FO.Core.Analysis;

/// <summary>
/// "How is this done here?" answered from the installation rather than from training data:
/// which APIs a scenario usually pulls in, who else has implemented this method, and how an
/// API is actually constructed and called.
/// </summary>
/// <remarks>
/// <para>
/// The three modes the gap analysis lists as missing from <c>analyze</c>. Each is a reading of
/// the same corpus — the X++ method bodies the index can reach — so they share one search and
/// one honesty rule.
/// </para>
/// <para>
/// The honesty rule is the point. A search over method bodies can return nothing because
/// nothing matches, or because the corpus was not readable: no FTS index, no source paths on
/// this machine, an index built somewhere else. Those are different answers, and reporting the
/// second as an empty result is how "no callers" comes to mean "unused" for something that is
/// used everywhere. Every result here carries how it was searched and how much it saw, and
/// says so plainly when it saw nothing at all.
/// </para>
/// </remarks>
public static class CodeAnalysis
{
    /// <summary>Corpus reachability, so an empty answer can be told apart from an unread one.</summary>
    /// <param name="Via">fts | scan — which path served the search.</param>
    /// <param name="FilesScanned">Source files opened; zero with a zero result means nothing was read.</param>
    /// <param name="Searched">False when the corpus could not be read at all.</param>
    /// <param name="Caveat">Why an empty answer is not evidence of absence, when it is not.</param>
    public sealed record Coverage(string Via, int FilesScanned, bool Searched, string? Caveat);

    /// <remarks>
    /// The verdict comes from the search itself rather than being recomputed here, so
    /// <c>find refs</c> and these three modes cannot disagree about whether the corpus was read.
    /// </remarks>
    private static Coverage CoverageOf(MetadataRepository repo, SourceRefResult result) =>
        new(result.Via, result.FilesScanned, result.Searched, result.Caveat);

    // ------------------------------------------------------------- patterns

    /// <summary>
    /// Which APIs the code around a scenario actually reaches for, ranked by how many distinct
    /// objects use each.
    /// </summary>
    /// <param name="repo">Index to read the corpus through.</param>
    /// <param name="scenario">Free text: "number sequence", "posting", "batch job".</param>
    /// <param name="model">Restrict to one model — e.g. your own, to see how THIS codebase does it.</param>
    /// <param name="limit">How many APIs and objects to return.</param>
    public static ToolResult<object> Patterns(MetadataRepository repo, string scenario, string? model, int limit = 20)
    {
        ArgumentNullException.ThrowIfNull(repo);
        if (string.IsNullOrWhiteSpace(scenario))
            return ToolResult<object>.Fail(D365FoErrorCodes.BadInput, "A scenario is required.",
                "Describe it in the words the code would use: \"number sequence\", \"posting\", \"batch job\".");

        // Each word narrows the result; a scenario is a conjunction, not a bag.
        var words = scenario.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(w => w.Length > 2)
            .ToList();
        if (words.Count == 0)
            return ToolResult<object>.Fail(D365FoErrorCodes.BadInput,
                "The scenario has no word longer than two characters to search on.");

        var result = MethodSourceSearch.Find(repo, words[0], kind: null, model: model, limit: 400);
        var hits = result.Hits
            .Where(h => words.Skip(1).All(w =>
                h.Matches.Any(m => m.Text.Contains(w, StringComparison.OrdinalIgnoreCase))
                || h.Name.Contains(w, StringComparison.OrdinalIgnoreCase)
                || h.Method.Contains(w, StringComparison.OrdinalIgnoreCase)))
            .ToList();

        // The APIs those methods reach for. Read from the WHOLE method body, not from the lines
        // that matched: the line mentioning "number sequence" is a comment or a parameter name,
        // and the NumberSeq call that answers the question is three lines below it. Counted by
        // distinct owner so one verbose class cannot make its own habits look like the
        // codebase's.
        var byApi = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        var bodiesRead = 0;
        foreach (var hit in hits.Take(BodyReadCap))
        {
            var body = ReadBody(hit);
            if (body is null) continue;
            bodiesRead++;
            foreach (Match m in ApiReference.Matches(body))
            {
                var api = m.Groups["api"].Value;
                if (api.Length < 4 || Noise.Contains(api)) continue;
                if (!byApi.TryGetValue(api, out var owners)) byApi[api] = owners = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                owners.Add(hit.Name);
            }
        }

        var apis = byApi
            .OrderByDescending(kv => kv.Value.Count)
            .ThenBy(kv => kv.Key, StringComparer.Ordinal)
            .Take(limit)
            .Select(kv => new { api = kv.Key, usedBy = kv.Value.Count, examples = kv.Value.Take(3).ToArray() })
            .ToList();

        var coverage = CoverageOf(repo, result);
        return ToolResult<object>.Success(new
        {
            scenario,
            coverage,
            matchingMethods = hits.Count,
            bodiesRead,
            objects = hits.Select(h => h.Name).Distinct(StringComparer.OrdinalIgnoreCase).Take(limit).ToArray(),
            apis,
            note = "Ranked by how many DISTINCT objects use each API, not by how often it appears — one "
                 + "verbose class should not make its own habits look like the codebase's.",
        }, coverage.Caveat is null ? null : [coverage.Caveat]);
    }

    // ------------------------------------------------------ implementations

    /// <summary>
    /// Who else implements this method, with the signature each declared — the answer before
    /// writing an override or a CoC wrapper.
    /// </summary>
    public static ToolResult<object> Implementations(
        MetadataRepository repo, string methodName, string? model, int limit = 20)
    {
        ArgumentNullException.ThrowIfNull(repo);
        if (string.IsNullOrWhiteSpace(methodName))
            return ToolResult<object>.Fail(D365FoErrorCodes.BadInput, "A method name is required.");

        var declared = repo.FindMethodDeclarations(methodName, model, limit);

        // Bodies are a separate question from declarations: the index knows every declaration,
        // and only the opt-in source index knows what is inside them.
        var result = MethodSourceSearch.Find(repo, methodName, kind: null, model: model, limit: limit);
        var bodies = result.Hits
            .Where(h => string.Equals(h.Method, methodName, StringComparison.OrdinalIgnoreCase))
            .ToList();

        var coverage = CoverageOf(repo, result);
        return ToolResult<object>.Success(new
        {
            method = methodName,
            coverage,
            count = declared.Count,
            declarations = declared.Select(d => new
            {
                d.Owner,
                d.OwnerKind,
                d.Model,
                d.Signature,
            }),
            bodies = bodies.Select(h => new
            {
                owner = h.Name,
                ownerKind = h.Kind,
                h.Model,
                lines = h.Matches.Select(m => new { m.Line, m.Text }),
                path = h.Path,
            }),
            note = declared.Count == 0
                ? "No object in the index declares this method. It may be a kernel method, which has no AOT "
                  + "declaration to find — `d365fo knowledge get system-objects` covers the ones that do not exist as files."
                : "Read one in full with `d365fo read class <Owner> --method " + methodName + "`.",
        }, coverage.Caveat is null ? null : [coverage.Caveat]);
    }

    // ----------------------------------------------------------- api-usage

    /// <summary>
    /// How an API is actually reached: constructed, called statically, declared as a type, or
    /// taken as a parameter — with the lines that do it.
    /// </summary>
    public static ToolResult<object> ApiUsage(MetadataRepository repo, string api, string? model, int limit = 25)
    {
        ArgumentNullException.ThrowIfNull(repo);
        if (string.IsNullOrWhiteSpace(api))
            return ToolResult<object>.Fail(D365FoErrorCodes.BadInput, "An API name is required.");

        var result = MethodSourceSearch.Find(repo, api, kind: null, model: model, limit: limit * 4);

        var construction = new List<object>();
        var staticCalls = new List<object>();
        var declarations = new List<object>();
        var other = new List<object>();

        var newRx = new Regex($@"\bnew\s+{Regex.Escape(api)}\s*\(", RegexOptions.IgnoreCase);
        var staticRx = new Regex($@"\b{Regex.Escape(api)}\s*::\s*(?<member>\w+)", RegexOptions.IgnoreCase);
        var declRx = new Regex($@"^\s*{Regex.Escape(api)}\s+\w+\s*[;=]", RegexOptions.IgnoreCase | RegexOptions.Multiline);

        foreach (var hit in result.Hits)
        foreach (var line in hit.Matches)
        {
            var entry = new { owner = hit.Name, ownerKind = hit.Kind, hit.Model, method = hit.Method, line.Line, line.Text };
            if (newRx.IsMatch(line.Text)) construction.Add(entry);
            else if (staticRx.IsMatch(line.Text)) staticCalls.Add(entry);
            else if (declRx.IsMatch(line.Text)) declarations.Add(entry);
            else other.Add(entry);
        }

        // Which static members are reached, so the answer names the API's real entry points
        // rather than only showing lines.
        var members = result.Hits
            .SelectMany(h => h.Matches)
            .SelectMany(m => staticRx.Matches(m.Text).Select(x => x.Groups["member"].Value))
            .Where(v => v.Length > 0)
            .GroupBy(v => v, StringComparer.Ordinal)
            .OrderByDescending(g => g.Count())
            .Select(g => new { member = g.Key, uses = g.Count() })
            .Take(limit)
            .ToList();

        var coverage = CoverageOf(repo, result);
        return ToolResult<object>.Success(new
        {
            api,
            coverage,
            indexed = repo.SymbolKinds(api),
            summary = new
            {
                construction = construction.Count,
                staticCalls = staticCalls.Count,
                declarations = declarations.Count,
                other = other.Count,
            },
            staticMembers = members,
            construction = construction.Take(limit),
            staticCallSites = staticCalls.Take(limit),
            declarations = declarations.Take(limit),
            note = construction.Count == 0 && staticCalls.Count > 0
                ? "Never constructed here — this API is reached statically, so a `new` is probably the wrong shape."
                : null,
        }, coverage.Caveat is null ? null : [coverage.Caveat]);
    }

    // ------------------------------------------------------------- helpers

    /// <summary>
    /// How many method bodies <see cref="Patterns"/> will open. Each is a file read, and the
    /// ranking is stable long before the cap — a scenario answered by four hundred methods is
    /// not a scenario.
    /// </summary>
    private const int BodyReadCap = 60;

    /// <summary>The full X++ body behind a hit, or null when the source is not reachable.</summary>
    private static string? ReadBody(SourceRefHit hit)
    {
        if (string.IsNullOrEmpty(hit.Path)) return null;
        try
        {
            var src = Extract.XppSourceReader.Read(hit.Path);
            return src is null ? null : Extract.XppSourceReader.FindMethod(src, hit.Method)?.Body;
        }
        catch { return null; }
    }

    /// <summary>PascalCase identifier used as a static call, a constructor, or a declared type.</summary>
    private static readonly Regex ApiReference = new(
        // Not preceded by '[': an attribute is a declaration ABOUT the method, not something the
        // method reaches for, and [SubscribesTo(...)] would otherwise outrank the API being asked
        // about.
        @"(?<!\[)\b(?:new\s+)?(?<api>[A-Z][A-Za-z0-9_]{3,})\s*(?:::|\()",
        RegexOptions.Compiled);

    /// <summary>
    /// Language and framework noise that appears in every method and says nothing about a
    /// scenario.
    /// </summary>
    private static readonly HashSet<string> Noise = new(StringComparer.Ordinal)
    {
        "SysTest", "Debug", "Global", "Error", "Exception", "Types", "NoYes", "Info", "Warning",
        "Throw", "Assert", "String", "Integer", "Container", "Query", "Args", "Common", "Object",
        // Attributes and intrinsics: they name an object without being one the method reaches
        // for, so they would rank above the API the caller actually asked about.
        "SysObsolete", "SysEntryPointAttribute", "DataMemberAttribute", "SysTestMethodAttribute",
        "SubscribesTo", "PostHandlerFor", "PreHandlerFor", "ExtensionOf", "DataEventHandler",
        "FormEventHandler", "FormDataSourceEventHandler", "SysAttribute",
    };
}
