using System.Text.RegularExpressions;
using D365FO.Core.Extract;

namespace D365FO.Core.Index;

/// <summary>One matching line inside a method body.</summary>
public sealed record SourceRefMatchLine(int Line, string Text);

/// <summary>A method that references the searched symbol.</summary>
public sealed record SourceRefHit(
    string Kind,
    string Name,
    string Model,
    string Method,
    string? Path,
    IReadOnlyList<SourceRefMatchLine> Matches);

/// <summary>Where a method-body search looked.</summary>
/// <param name="Scope">
/// <c>model</c> (one named model), <c>default</c> (models with source indexed plus custom models)
/// or <c>all</c>.
/// </param>
/// <param name="FromIndex">Models answered from <c>MethodSourceFts</c>.</param>
/// <param name="FromDisk">Models with at least one source file scanned from disk.</param>
/// <param name="NotSearched">Indexed models outside the scope.</param>
/// <param name="Note">How the scope was chosen and how to widen it, when that matters.</param>
public sealed record SearchCoverage(
    string Scope,
    IReadOnlyList<string> FromIndex,
    IReadOnlyList<string> FromDisk,
    IReadOnlyList<string> NotSearched,
    string? Note);

/// <summary>An <c>Owner.member</c> needle, split.</summary>
public sealed record MemberNeedle(string Owner, string Member);

/// <summary>Result of a reverse-reference search over X++ method bodies.</summary>
/// <param name="Needle">The symbol that was searched for.</param>
/// <param name="Via">"fts" when served from MethodSourceFts, else "scan".</param>
/// <param name="FilesScanned">Source files opened. Zero with no hits means nothing was read.</param>
/// <param name="Truncated">The limit was reached, so there may be more.</param>
/// <param name="Hits">The methods that reference the symbol.</param>
/// <param name="Searched">
/// False when the corpus could not be read at all — no FTS rows and no source file opened.
/// </param>
/// <param name="Caveat">
/// Why an empty result is not evidence of absence, when it is not. Null when the search really
/// did look.
/// </param>
public sealed record SourceRefResult(
    string Needle,
    string Via,
    int FilesScanned,
    bool Truncated,
    IReadOnlyList<SourceRefHit> Hits,
    bool Searched = true,
    string? Caveat = null)
{
    /// <summary>Which models were answered from the index, which from disk, and which not at all.</summary>
    public SearchCoverage? Coverage { get; init; }

    /// <summary>
    /// Set when the needle was <c>Owner.member</c>: the member's name was searched and a hit kept
    /// when its method also mentions <c>Owner</c> or belongs to it. Text-based, not compiler-resolved.
    /// </summary>
    public MemberNeedle? Member { get; init; }

    /// <summary>A note when the needle is not a symbol the index knows. Never an error.</summary>
    public string? Hint { get; init; }

    /// <summary>
    /// The reachability verdict for a finished search.
    /// </summary>
    /// <remarks>
    /// A search over method bodies returns nothing when nothing matches, and also when there was
    /// nothing to search: no opt-in source index, and no source path on this machine — an index
    /// built elsewhere looks exactly like a codebase where the symbol is unused. Reporting the
    /// second as a clean zero is how "no callers" becomes "safe to delete".
    /// </remarks>
    public static SourceRefResult Finish(
        string needle, string via, int filesScanned, bool truncated,
        IReadOnlyList<SourceRefHit> hits, long ftsRows)
    {
        var searched = ftsRows > 0 || filesScanned > 0;
        return new SourceRefResult(needle, via, filesScanned, truncated, hits, searched,
            searched
                ? null
                : "Nothing was searched: the method-source index is empty and no source file could be opened. "
                  + "An empty result here is not evidence that the symbol is unused — run "
                  + "`d365fo index extract --index-source`, or point D365FO_PACKAGES_PATH at the installation "
                  + "the index was built from.");
    }
}

/// <summary>
/// Shared reverse-reference search over X++ method bodies, used by both
/// <c>find refs</c> (CLI) and the <c>find_references</c> MCP tool so the two
/// surfaces stay in parity.
///
/// When the opt-in <c>MethodSourceFts</c> index is populated
/// (<c>index extract --index-source</c>) class, table and form methods are
/// answered by a single FTS5 <c>MATCH</c> instead of opening every source XML on
/// disk. Forms of a model indexed before form bodies joined the index
/// (<see cref="SourceIndexLevel.ClassesAndTables"/>) are still scanned from disk,
/// and only those. With no FTS index the whole corpus falls back to a disk scan,
/// preserving the previous behaviour.
/// </summary>
/// <remarks>
/// A form hit's <see cref="SourceRefHit.Method"/> names the member the method belongs to
/// (<see cref="FormMethodSource"/>): <c>init</c> on the form, <c>CustTable.init</c> on a data
/// source, <c>CustTable.AccountNum.modified</c> on a field, <c>OkButton.clicked</c> on a
/// control — the same on the FTS and the disk path.
/// </remarks>
public static class MethodSourceSearch
{
    /// <summary>Model lists longer than this are reported as a count only.</summary>
    public const int CoverageListCap = 20;

    /// <summary>
    /// <c>Owner.member</c> or <c>Owner::member</c> — a member named through its declaring type.
    /// </summary>
    private static readonly Regex QualifiedNeedle =
        new(@"^(?<owner>[A-Za-z_]\w*)(?:\.|::)(?<member>[A-Za-z_]\w*)$", RegexOptions.CultureInvariant);

    /// <param name="repo">Index to read.</param>
    /// <param name="needle">
    /// The symbol. <c>Owner.member</c> (or <c>Owner::member</c>) searches the member's name and keeps
    /// the methods that also mention <c>Owner</c>, or belong to it — see <see cref="SourceRefResult.Member"/>.
    /// </param>
    /// <param name="kind">class | table | form, or null for all three.</param>
    /// <param name="model">One model to search. Wins over <paramref name="allModels"/>.</param>
    /// <param name="limit">Most hits returned.</param>
    /// <param name="sampleLinesPerHit">Matching lines read back per hit; 0 opens no source file on the index path.</param>
    /// <param name="allModels">
    /// Without <paramref name="model"/>: search every model, scanning from disk the ones that have no
    /// source index. Off, the default scope is the source-indexed models plus the custom models.
    /// </param>
    /// <param name="customModelPatterns">
    /// <c>D365FO_CUSTOM_MODELS</c> patterns that also put a model in the default scope. Null reads
    /// them from the configuration. A model extracted as custom (<c>Models.IsCustom</c>) is in it either way.
    /// </param>
    public static SourceRefResult Find(
        MetadataRepository repo,
        string needle,
        string? kind,
        string? model,
        int limit,
        int sampleLinesPerHit = 3,
        bool allModels = false,
        IEnumerable<string>? customModelPatterns = null)
    {
        var normKind = NormalizeKind(kind);
        var hits = new List<SourceRefHit>();
        int scanned = 0;
        bool truncated = false;

        // Owner.member has no text form of its own: X++ writes Owner::member(), obj.member() or
        // this.member(). Searched literally it matched almost nothing and answered count:0, which
        // reads as "no callers". The member is searched instead, kept where Owner is mentioned too.
        var qualified = QualifiedNeedle.Match(needle.Trim());
        var member = qualified.Success
            ? new MemberNeedle(qualified.Groups["owner"].Value, qualified.Groups["member"].Value)
            : null;
        var token = member?.Member ?? needle;
        var rx = new Regex($@"\b{Regex.Escape(token)}\b", RegexOptions.IgnoreCase);
        var ownerRx = member is null ? null : new Regex($@"\b{Regex.Escape(member.Owner)}\b", RegexOptions.IgnoreCase);
        bool Keep(string objectName, string body) =>
            ownerRx is null
            || string.Equals(objectName, member!.Owner, StringComparison.OrdinalIgnoreCase)
            || ownerRx.IsMatch(body);

        // Which models to read, and from where. Per model, because the source index is per model:
        // a model with FTS rows is answered from them, every other model in scope from disk.
        var states = repo.GetModelSourceStates();
        var coverage = ResolveScope(states, model, allModels, customModelPatterns, out var inScope);
        var levels = states
            .GroupBy(m => m.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Max(m => m.Level), StringComparer.OrdinalIgnoreCase);
        var ftsModels = inScope
            .Where(m => levels.GetValueOrDefault(m) >= SourceIndexLevel.ClassesAndTables)
            .OrderBy(m => m, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var ftsRows = ftsModels.Count > 0 ? repo.CountMethodSource() : 0;
        if (ftsRows == 0) ftsModels.Clear();

        if (ftsModels.Count > 0 && normKind is null or "Class" or "Table" or "Form")
        {
            // Quote the token so identifiers with FTS-special characters can't break MATCH
            // syntax. unicode61 tokenization makes this an identifier-token match, close to the
            // \bword\b disk scan.
            static string Quote(string t) => "\"" + t.Replace("\"", "\"\"") + "\"";
            IEnumerable<MethodSourceMatch> rows = member is null
                ? repo.SearchMethodSource(Quote(token), limit, ftsModels, normKind, objectName: null)
                : repo.SearchMethodSource(Quote(token) + " AND " + Quote(member.Owner), limit, ftsModels, normKind, objectName: null)
                    .Concat(repo.SearchMethodSource(Quote(token), limit, ftsModels, normKind, objectName: member.Owner))
                    .DistinctBy(r => (r.Kind, r.ObjectName, r.MethodName, r.Model));
            foreach (var mm in rows)
            {
                if (hits.Count >= limit) { truncated = true; break; }
                hits.Add(new SourceRefHit(
                    mm.Kind, mm.ObjectName, mm.Model, mm.MethodName, mm.SourcePath,
                    SampleLines(mm.Kind, mm.SourcePath, mm.MethodName, rx, sampleLinesPerHit)));
            }
        }

        // From disk: class and table methods of models with no source index, and forms of models
        // whose index has no form bodies (built before forms joined it, or not built at all).
        var ftsSet = new HashSet<string>(ftsModels, StringComparer.OrdinalIgnoreCase);
        int Covered(string m) => ftsSet.Contains(m) ? levels.GetValueOrDefault(m) : SourceIndexLevel.None;
        var diskModels = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var sources = repo.EnumerateSourcePaths(model)
            .Where(s => normKind is null || string.Equals(s.Kind, normKind, StringComparison.OrdinalIgnoreCase))
            .Where(s => inScope.Contains(s.Model))
            .Where(s => s.Kind == "Form"
                ? Covered(s.Model) < SourceIndexLevel.WithForms
                : Covered(s.Model) < SourceIndexLevel.ClassesAndTables)
            .Select(s => { diskModels.Add(s.Model); return s; });
        ScanSources(sources, rx, Keep, limit, sampleLinesPerHit, hits, ref scanned, ref truncated);

        var result = SourceRefResult.Finish(needle, ftsModels.Count > 0 ? "fts" : "scan",
            scanned, truncated, hits, ftsRows);
        coverage = coverage with
        {
            FromIndex = ftsModels,
            FromDisk = diskModels.OrderBy(m => m, StringComparer.OrdinalIgnoreCase).ToList(),
        };

        // An empty answer with models left out is not "unused": say which part was not looked at.
        // Not for a named --model: the rest was left out on purpose, and --all-models with --model is refused.
        var caveat = result.Caveat;
        if (caveat is null && hits.Count == 0 && coverage.Scope == "default" && coverage.NotSearched.Count > 0)
            caveat = $"{coverage.NotSearched.Count} model(s) were not searched. An empty result covers only the "
                   + "models in `coverage`. Pass --all-models (MCP: allModels) to scan the rest from disk, or "
                   + "--model to name one.";

        return result with
        {
            Caveat = caveat,
            Coverage = coverage,
            Member = member,
            Hint = UnknownSymbolHint(repo, needle, member),
        };
    }

    /// <summary>
    /// The models a search may read (<paramref name="inScope"/>), and the coverage block that will
    /// name them. <see cref="SearchCoverage.FromIndex"/> and <see cref="SearchCoverage.FromDisk"/>
    /// are filled in once the search has run.
    /// </summary>
    private static SearchCoverage ResolveScope(
        IReadOnlyList<ModelSourceState> states, string? model, bool allModels,
        IEnumerable<string>? customModelPatterns, out HashSet<string> inScope)
    {
        var all = states.Select(s => s.Name).ToList();
        inScope = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (!string.IsNullOrWhiteSpace(model))
        {
            inScope.Add(model);
            var others = inScope;
            return new SearchCoverage("model", [], [], all.Where(m => !others.Contains(m)).ToList(), Note: null);
        }
        if (allModels)
        {
            inScope.UnionWith(all);
            return new SearchCoverage("all", [], [], [], Note: null);
        }

        var matcher = new ModelMatcher(customModelPatterns ?? D365FoSettings.FromEnvironment().CustomModels);
        foreach (var s in states)
            if (s.Level >= SourceIndexLevel.ClassesAndTables || s.IsCustom || matcher.IsMatch(s.Name))
                inScope.Add(s.Name);

        if (inScope.Count == 0)
        {
            // Nothing is source-indexed and nothing is custom: there is no cheaper scope to offer,
            // and searching nothing would be the silent zero this scope exists to remove.
            inScope.UnionWith(all);
            return new SearchCoverage("all", [], [], [],
                Note: "No model has source indexed and none is a custom model (D365FO_CUSTOM_MODELS), so every "
                    + "model was scanned from disk. `d365fo index refresh --model <Model> --force --index-source` "
                    + "makes a model you search often answer from the index.");
        }

        var scope = inScope;
        var notSearched = all.Where(m => !scope.Contains(m)).ToList();
        return new SearchCoverage("default", [], [], notSearched,
            Note: notSearched.Count == 0
                ? null
                : "Default scope: models with source indexed plus custom models (D365FO_CUSTOM_MODELS). "
                  + "--all-models (MCP: allModels) also scans the others from disk, which on a full installation "
                  + "takes minutes.");
    }

    /// <summary>
    /// The <c>coverage</c> block of a <c>find refs</c> / <c>find_references</c> answer: the scope,
    /// then each model list as <c>{ count, models }</c>, with <c>models</c> left out past
    /// <see cref="CoverageListCap"/> so an all-models answer does not spend its tokens on names.
    /// </summary>
    public static object? CoveragePayload(SearchCoverage? coverage)
    {
        if (coverage is null) return null;
        static object Part(IReadOnlyList<string> models) => new
        {
            count = models.Count,
            models = models.Count <= CoverageListCap ? models : null,
        };
        return new
        {
            scope = coverage.Scope,
            fromIndex = Part(coverage.FromIndex),
            fromDisk = Part(coverage.FromDisk),
            notSearched = Part(coverage.NotSearched),
            note = coverage.Note,
        };
    }

    /// <summary>The <c>match</c> block for an <c>Owner.member</c> needle, or null for a plain one.</summary>
    public static object? MemberMatchPayload(MemberNeedle? member) => member is null ? null : new
    {
        kind = "member-text",
        owner = member.Owner,
        member = member.Member,
        note = $"Text-based: methods that mention '{member.Member}' and also '{member.Owner}' (or belong to it). "
             + "A call through a variable of another name is missed, and a same-named member of another type "
             + "can match. `--xref` answers from the compiler's cross-reference instead.",
    };

    /// <summary>
    /// A note when the needle names nothing the index knows, which is usually a typo or a model
    /// that was never extracted. Not an error: the text may still appear in code (a kernel
    /// function, a macro, a string), and the search runs regardless.
    /// </summary>
    private static string? UnknownSymbolHint(MetadataRepository repo, string needle, MemberNeedle? member)
    {
        try
        {
            if (member is not null)
            {
                var owner = repo.Resolve(member.Owner);
                if (owner is null)
                    return $"'{member.Owner}' is not an object in the index. Check the spelling with "
                         + $"`d365fo search any {member.Owner}`, or extract the model that declares it.";
                return repo.HasMember(owner.Canonical, member.Member) || repo.IsKnownMemberName(member.Member)
                    ? null
                    : $"'{owner.Canonical}' has no member '{member.Member}' in the index.";
            }
            if (needle.StartsWith('@')) return null; // a label reference is text, not a symbol
            return repo.Resolve(needle) is not null || repo.IsKnownMemberName(needle)
                ? null
                : $"'{needle}' is not a known object, method or field in the index. The text search ran anyway "
                  + "(it also finds kernel functions and macros). If you meant an object, check the spelling with "
                  + $"`d365fo search any {needle}`.";
        }
        catch (Microsoft.Data.Sqlite.SqliteException)
        {
            return null;
        }
    }

    private static void ScanSources(
        IEnumerable<(string Kind, string Name, string Model, string SourcePath)> sources,
        Regex rx, Func<string, string, bool> keep, int limit, int sampleLinesPerHit,
        List<SourceRefHit> hits, ref int scanned, ref bool truncated)
    {
        foreach (var row in sources)
        {
            if (hits.Count >= limit) { truncated = true; break; }
            scanned++;
            var src = ReadMethods(row.Kind, row.SourcePath);
            if (src is null) continue;
            foreach (var m in src)
            {
                if (!rx.IsMatch(m.Body) || !keep(row.Name, m.Body)) continue;
                if (hits.Count >= limit) { truncated = true; break; }
                hits.Add(new SourceRefHit(
                    row.Kind, row.Name, row.Model, m.Name, row.SourcePath,
                    SampleLinesFromBody(m.Body, rx, sampleLinesPerHit)));
            }
        }
    }

    private static IReadOnlyList<SourceRefMatchLine> SampleLines(string kind, string? path, string method, Regex rx, int max)
    {
        if (string.IsNullOrEmpty(path) || max <= 0) return Array.Empty<SourceRefMatchLine>();
        var body = ReadMethodBody(kind, path, method);
        return body is null ? Array.Empty<SourceRefMatchLine>() : SampleLinesFromBody(body, rx, max);
    }

    /// <summary>
    /// The body of the method a hit names, read from its source file — a form member by its
    /// qualified name (<see cref="FormMethodSource"/>), a class or table method by its own.
    /// </summary>
    public static string? ReadMethodBody(SourceRefHit hit) => ReadMethodBody(hit.Kind, hit.Path, hit.Method);

    /// <summary>The method's own name, without the form member that owns it (<c>CustTable.init</c> → <c>init</c>).</summary>
    public static string MethodNameOf(SourceRefHit hit) =>
        hit.Kind == "Form" && hit.Method.LastIndexOf('.') is var dot and >= 0 ? hit.Method[(dot + 1)..] : hit.Method;

    private static string? ReadMethodBody(string kind, string? path, string method)
    {
        if (string.IsNullOrEmpty(path)) return null;
        if (kind == "Form") return FormMethodSource.FindBody(path, method);
        var src = XppSourceReader.Read(path);
        return src is null ? null : XppSourceReader.FindMethod(src, method)?.Body;
    }

    private static IEnumerable<(string Name, string Body)>? ReadMethods(string kind, string path)
    {
        if (kind == "Form") return FormMethodSource.Read(path)?.Select(m => (m.Name, m.Body));
        return XppSourceReader.Read(path)?.Methods.Select(m => (m.Name, m.Body));
    }

    private static IReadOnlyList<SourceRefMatchLine> SampleLinesFromBody(string body, Regex rx, int max)
    {
        var res = new List<SourceRefMatchLine>();
        if (max <= 0) return res;
        var lines = body.Replace("\r\n", "\n").Split('\n');
        for (int i = 0; i < lines.Length && res.Count < max; i++)
            if (rx.IsMatch(lines[i]))
                res.Add(new SourceRefMatchLine(i + 1, lines[i].Trim()));
        return res;
    }

    /// <summary>Map a free-form kind filter to a stored source kind (Class/Table/Form), or null.</summary>
    private static string? NormalizeKind(string? kind)
    {
        if (string.IsNullOrWhiteSpace(kind)) return null;
        return kind.Trim().ToLowerInvariant() switch
        {
            "class" => "Class",
            "table" => "Table",
            "form"  => "Form",
            _       => kind, // pass through unknown values; they simply match nothing
        };
    }
}
