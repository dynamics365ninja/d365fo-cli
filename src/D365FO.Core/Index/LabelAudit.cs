using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using D365FO.Core.Validation;

namespace D365FO.Core.Index;

/// <summary>
/// <c>d365fo labels audit</c> (issue #272): the label mistakes an ISV model ships, found before a
/// user sees them.
/// </summary>
/// <remarks>
/// <para>
/// Three checks, each answered where the answer is complete:
/// </para>
/// <list type="bullet">
/// <item><description><b>Undefined</b>: a label token the model uses, in a metadata property or in
/// an X++ string literal, that no label file defines. The model's own uses are read from its XML on
/// disk, so every property counts (the index keeps only <c>Label</c> columns). Its own label files
/// are read from disk in every language; every other model's from the index.</description></item>
/// <item><description><b>Untranslated</b>: a key of one of the model's label files that exists in
/// some of the file's languages and not in others. Read from disk, because the index holds only the
/// configured <c>D365FO_LABEL_LANGUAGES</c>.</description></item>
/// <item><description><b>Unused</b>: a key of the model's label files that nothing in the model
/// refers to. With <c>allModels</c>, also nothing in the index: the <c>Label</c> columns of every
/// model, and the method bodies of every model in the source index.</description></item>
/// </list>
/// <para>
/// Whether a defining label file is in a package the model references is not checked: the index
/// does not record which package a label file belongs to, so a label of an unreferenced model reads
/// as defined. The build reports that case; the audit reports the ones the build cannot (a key
/// missing in one language, a key nobody uses) and the ones xppbp misreports.
/// </para>
/// </remarks>
public static class LabelAudit
{
    /// <summary>A label reference in a code string or text: <c>@File:Key</c> or a legacy <c>@SYS12345</c>.</summary>
    internal static readonly Regex Token = new(
        @"(?<![\w@:])@(?:[A-Za-z_]\w*:[\w.\-]*\w|[A-Za-z]+\d+)(?![\w:])",
        RegexOptions.CultureInvariant);

    /// <summary>A property value that is exactly one label reference.</summary>
    private static readonly Regex WholeToken = new(
        @"^@(?:[A-Za-z_]\w*:[\w.\-]*\w|[A-Za-z]+\d+)$",
        RegexOptions.CultureInvariant);

    /// <summary>Elements whose text is X++, where a label is only a label inside a string literal.</summary>
    private static readonly HashSet<string> CodeElements = new(StringComparer.Ordinal) { "Source", "Declaration" };

    /// <summary>One place a label token is used.</summary>
    /// <param name="Kind">AOT kind of the object (<c>Table</c>, <c>Class</c>, <c>MenuItemDisplay</c>).</param>
    /// <param name="Object">The object.</param>
    /// <param name="Element">Path inside the object (<c>Fields/AccountNum</c>), or null for the object itself.</param>
    /// <param name="Property">The property that holds the token, or <c>Source</c> for X++.</param>
    /// <param name="Method">The method, for a use in X++.</param>
    public sealed record Use(string Kind, string Object, string? Element, string Property, string? Method);

    /// <summary>The model's label files as read from disk: file → language → keys.</summary>
    public sealed class LabelFiles
    {
        internal Dictionary<string, Dictionary<string, HashSet<string>>> Files { get; } =
            new(StringComparer.OrdinalIgnoreCase);

        internal void Add(string file, string language, string key)
        {
            if (!Files.TryGetValue(file, out var langs))
                Files[file] = langs = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
            if (!langs.TryGetValue(language, out var keys))
                langs[language] = keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            keys.Add(key);
        }

        internal void Touch(string file, string language)
        {
            if (!Files.TryGetValue(file, out var langs))
                Files[file] = langs = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
            if (!langs.ContainsKey(language))
                langs[language] = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }

        internal bool Defines(string file, string key) =>
            Files.TryGetValue(file, out var langs) && langs.Values.Any(k => k.Contains(key));
    }

    /// <summary>Which checks to run.</summary>
    [Flags]
    public enum Checks
    {
        /// <summary>Tokens no label file defines.</summary>
        Undefined = 1,

        /// <summary>Keys missing in some of their file's languages.</summary>
        Untranslated = 2,

        /// <summary>Keys nothing refers to.</summary>
        Unused = 4,

        /// <summary>All three.</summary>
        All = Undefined | Untranslated | Unused,
    }

    /// <summary>
    /// The CLI and MCP entry: find the model's folder under the configured packages roots (or take
    /// <paramref name="path"/>), parse <paramref name="checks"/>, and run.
    /// </summary>
    /// <param name="repo">The index, or null when there is none.</param>
    /// <param name="model">The model audited.</param>
    /// <param name="path">Its folder, when the caller names it instead of the model being looked up.</param>
    /// <param name="checks">Comma-separated <c>undefined</c>, <c>untranslated</c>, <c>unused</c>; all when empty.</param>
    /// <param name="allModels">Count a use anywhere in the index for the unused check.</param>
    /// <param name="limit">Findings listed per check.</param>
    public static ToolResult<object> Run(
        MetadataRepository? repo, string? model, string? path, string? checks, bool allModels, int limit)
    {
        if (string.IsNullOrWhiteSpace(model) && string.IsNullOrWhiteSpace(path))
        {
            return ToolResult<object>.Fail(D365FoErrorCodes.BadInput, "A model is required.",
                "Pass --model <MODEL> (MCP: `model`), or --path <MODEL FOLDER>.");
        }

        var selected = Checks.All;
        if (!string.IsNullOrWhiteSpace(checks))
        {
            selected = 0;
            foreach (var part in checks.Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (!Enum.TryParse<Checks>(part, ignoreCase: true, out var one) || one == Checks.All)
                {
                    return ToolResult<object>.Fail(D365FoErrorCodes.BadInput, $"Unknown check '{part}'.",
                        "Checks: undefined, untranslated, unused (comma-separated; all when omitted).");
                }
                selected |= one;
            }
        }

        string? root = path;
        if (string.IsNullOrWhiteSpace(root))
        {
            var cfg = D365FoSettings.FromEnvironment();
            var roots = new List<string?>(cfg.CustomPackagesPaths) { cfg.PackagesPath };
            root = IndexSync.FindModelDir(roots, model!);
            if (root is null)
            {
                return ToolResult<object>.Fail(D365FoErrorCodes.ModelNotFound,
                    $"No model folder called '{model}' under the configured packages roots.",
                    "Check the name with `d365fo models list`, or pass --path <MODEL FOLDER>.");
            }
        }

        var name = string.IsNullOrWhiteSpace(model) ? Path.GetFileName(Path.TrimEndingDirectorySeparator(root!)) : model!;
        return Run(repo, name, root!, selected, allModels, limit);
    }

    /// <summary>Run the audit for one model.</summary>
    /// <param name="repo">The index, for every other model's labels and, with <paramref name="allModels"/>, their uses.</param>
    /// <param name="model">The model audited.</param>
    /// <param name="modelRoot">Its folder: <c>&lt;package&gt;\&lt;model&gt;</c>, holding the <c>Ax*</c> folders.</param>
    /// <param name="checks">Which checks to run.</param>
    /// <param name="allModels">Count a use anywhere in the index, not only in the model, for the unused check.</param>
    /// <param name="limit">Findings listed per check; the counts are always complete.</param>
    public static ToolResult<object> Run(
        MetadataRepository? repo, string model, string modelRoot, Checks checks = Checks.All,
        bool allModels = false, int limit = 50)
    {
        if (!Directory.Exists(modelRoot))
            return ToolResult<object>.Fail(D365FoErrorCodes.ModelNotFound, $"No model folder at {modelRoot}.");
        limit = Math.Clamp(limit, 1, 1000);
        var warnings = new List<string>();

        var files = ReadLabelFiles(modelRoot);
        var (uses, filesScanned) = ScanUses(modelRoot);

        // ---- 1. Undefined ----
        var undefined = new List<object>();
        var undefinedCount = 0;
        if (checks.HasFlag(Checks.Undefined))
        {
            if (repo is null)
                warnings.Add("No index: a token is checked against the model's own label files only, so a label of another model reads as undefined.");

            var indexed = new Dictionary<string, IReadOnlySet<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (var (token, at) in uses.OrderBy(u => u.Key, StringComparer.OrdinalIgnoreCase))
            {
                if (IsDefined(token, files, repo, indexed)) continue;
                undefinedCount++;
                if (undefined.Count < limit)
                    undefined.Add(new { label = token, uses = at.Count, usedIn = at.Take(5).ToList() });
            }
        }

        // ---- 2. Untranslated ----
        var untranslated = new List<object>();
        var untranslatedCount = 0;
        if (checks.HasFlag(Checks.Untranslated))
        {
            foreach (var (file, langs) in files.Files.OrderBy(f => f.Key, StringComparer.OrdinalIgnoreCase))
            {
                if (langs.Count < 2) continue;
                var allKeys = langs.Values.SelectMany(k => k).ToHashSet(StringComparer.OrdinalIgnoreCase);
                foreach (var key in allKeys.OrderBy(k => k, StringComparer.OrdinalIgnoreCase))
                {
                    var missing = langs.Where(l => !l.Value.Contains(key)).Select(l => l.Key)
                        .OrderBy(l => l, StringComparer.OrdinalIgnoreCase).ToList();
                    if (missing.Count == 0) continue;
                    untranslatedCount++;
                    if (untranslated.Count < limit)
                    {
                        untranslated.Add(new
                        {
                            label = $"@{file}:{key}",
                            missingIn = missing,
                            presentIn = langs.Keys.Except(missing, StringComparer.OrdinalIgnoreCase)
                                .OrderBy(l => l, StringComparer.OrdinalIgnoreCase).ToList(),
                        });
                    }
                }
            }
        }

        // ---- 3. Unused ----
        var unused = new List<object>();
        var unusedCount = 0;
        object? unusedScope = null;
        if (checks.HasFlag(Checks.Unused))
        {
            var used = uses.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (allModels)
            {
                if (repo is null)
                {
                    warnings.Add("--all-models needs the index; only the model's own uses were counted.");
                }
                else
                {
                    var (fromIndex, notSourceIndexed) = UsesElsewhere(repo, files.Files.Keys, model);
                    used.UnionWith(fromIndex);
                    unusedScope = new
                    {
                        models = "all indexed",
                        modelsWithoutSourceIndex = notSourceIndexed.Count,
                        note = notSourceIndexed.Count == 0
                            ? null
                            : $"The X++ of {notSourceIndexed.Count} model(s) is not in the source index, so a use in their code is not seen "
                              + "(their metadata Label properties are). `d365fo index sync <Model> --index-source` adds a model's code.",
                    };
                }
            }

            foreach (var (file, langs) in files.Files.OrderBy(f => f.Key, StringComparer.OrdinalIgnoreCase))
            {
                var keys = langs.Values.SelectMany(k => k).ToHashSet(StringComparer.OrdinalIgnoreCase);
                foreach (var key in keys.OrderBy(k => k, StringComparer.OrdinalIgnoreCase))
                {
                    if (used.Contains($"@{file}:{key}")) continue;
                    unusedCount++;
                    if (unused.Count < limit) unused.Add($"@{file}:{key}");
                }
            }
        }

        var data = new
        {
            model,
            counts = new
            {
                undefined = checks.HasFlag(Checks.Undefined) ? undefinedCount : (int?)null,
                untranslated = checks.HasFlag(Checks.Untranslated) ? untranslatedCount : (int?)null,
                unused = checks.HasFlag(Checks.Unused) ? unusedCount : (int?)null,
            },
            scanned = new
            {
                xmlFiles = filesScanned,
                labelTokensUsed = uses.Count,
                labelFiles = files.Files.Select(f => new
                {
                    file = f.Key,
                    languages = f.Value.Keys.OrderBy(l => l, StringComparer.OrdinalIgnoreCase).ToList(),
                    keys = f.Value.Values.SelectMany(k => k).Distinct(StringComparer.OrdinalIgnoreCase).Count(),
                }).OrderBy(f => f.file, StringComparer.OrdinalIgnoreCase).ToList(),
            },
            undefined = checks.HasFlag(Checks.Undefined) ? undefined : null,
            untranslated = checks.HasFlag(Checks.Untranslated) ? untranslated : null,
            unused = checks.HasFlag(Checks.Unused) ? unused : null,
            unusedScope,
            truncated = undefinedCount > undefined.Count || untranslatedCount > untranslated.Count || unusedCount > unused.Count
                ? true : (bool?)null,
        };
        return ToolResult<object>.Success(data, warnings.Count > 0 ? warnings : null);
    }

    /// <summary>
    /// The model's <c>*.label.txt</c> files under <c>AxLabelFile/LabelResources/&lt;lang&gt;/</c>,
    /// named <c>&lt;File&gt;.&lt;lang&gt;.label.txt</c>.
    /// </summary>
    internal static LabelFiles ReadLabelFiles(string modelRoot)
    {
        var result = new LabelFiles();
        var folder = Path.Combine(modelRoot, "AxLabelFile");
        if (!Directory.Exists(folder)) return result;

        foreach (var path in Directory.EnumerateFiles(folder, "*.label.txt", SearchOption.AllDirectories))
        {
            var name = Path.GetFileName(path);
            var stem = name[..^".label.txt".Length];
            var dot = stem.LastIndexOf('.');
            if (dot <= 0) continue;
            var file = stem[..dot];
            var language = stem[(dot + 1)..];
            result.Touch(file, language);

            foreach (var line in File.ReadLines(path))
            {
                // `Key=Text`; a comment line under it starts with whitespace and `;`.
                if (line.Length == 0 || char.IsWhiteSpace(line[0]) || line[0] == ';') continue;
                var eq = line.IndexOf('=');
                if (eq <= 0) continue;
                var key = line[..eq].Trim().TrimStart('﻿');
                if (key.Length > 0) result.Add(file, language, key);
            }
        }
        return result;
    }

    /// <summary>Every label token the model's XML uses, with where.</summary>
    internal static (Dictionary<string, List<Use>> Uses, int FilesScanned) ScanUses(string modelRoot)
    {
        var uses = new Dictionary<string, List<Use>>(StringComparer.OrdinalIgnoreCase);
        var scanned = 0;
        foreach (var dir in Directory.EnumerateDirectories(modelRoot, "Ax*"))
        {
            if (string.Equals(Path.GetFileName(dir), "AxLabelFile", StringComparison.OrdinalIgnoreCase)) continue;
            foreach (var path in Directory.EnumerateFiles(dir, "*.xml", SearchOption.AllDirectories))
            {
                string xml;
                try { xml = File.ReadAllText(path); }
                catch (IOException) { continue; }
                scanned++;
                if (!xml.Contains('@')) continue;
                foreach (var (token, use) in ScanXml(xml))
                {
                    if (!uses.TryGetValue(token, out var list)) uses[token] = list = new List<Use>();
                    list.Add(use);
                }
            }
        }
        return (uses, scanned);
    }

    /// <summary>The label tokens of one AOT XML: whole property values, and string literals in X++.</summary>
    internal static IEnumerable<(string Token, Use Use)> ScanXml(string xml)
    {
        XElement root;
        try { root = XDocument.Parse(xml).Root!; }
        catch (XmlException) { yield break; }
        if (root is null) yield break;

        var kind = LabelPropertySearch.KindOf(root.Name.LocalName);
        var name = root.Elements().FirstOrDefault(e => e.Name.LocalName == "Name")?.Value ?? "";

        foreach (var el in root.Descendants())
        {
            if (el.HasElements) continue;
            var text = el.Value;
            if (text.IndexOf('@') < 0) continue;

            if (CodeElements.Contains(el.Name.LocalName))
            {
                var method = el.Name.LocalName == "Source"
                    ? el.Parent?.Elements().FirstOrDefault(e => e.Name.LocalName == "Name")?.Value
                    : "classDeclaration";
                var element = LabelPropertySearch.ElementPath(root, el);
                foreach (var span in XppLexer.Scan(text).Spans)
                {
                    if (span.Kind != XppSpanKind.String) continue;
                    var literal = text[span.Start..Math.Min(span.End, text.Length)];
                    foreach (Match m in Token.Matches(literal))
                        yield return (m.Value, new Use(kind, name, element, "Source", method));
                }
                continue;
            }

            var value = text.Trim();
            if (WholeToken.IsMatch(value))
                yield return (value, new Use(kind, name, LabelPropertySearch.ElementPath(root, el), el.Name.LocalName, null));
        }
    }

    /// <summary>
    /// The label file and the key spellings a token may be stored under. <c>@File:Key</c> is
    /// <c>Key</c> in <c>File</c>. A legacy <c>@SYS12345</c> is in <c>SYS</c>, stored by different
    /// extractor versions as <c>12345</c>, <c>SYS12345</c> or <c>@SYS12345</c>.
    /// </summary>
    internal static (string File, string[] Keys) Parse(string token)
    {
        var raw = token.TrimStart('@');
        var colon = raw.IndexOf(':');
        if (colon > 0) return (raw[..colon], new[] { raw[(colon + 1)..] });

        var split = 0;
        while (split < raw.Length && char.IsLetter(raw[split])) split++;
        return (raw[..split], new[] { raw[split..], raw, "@" + raw });
    }

    private static bool IsDefined(
        string token, LabelFiles own, MetadataRepository? repo, Dictionary<string, IReadOnlySet<string>> indexed)
    {
        var (file, keys) = Parse(token);
        if (keys.Any(k => own.Defines(file, k))) return true;
        if (repo is null) return false;

        if (!indexed.TryGetValue(file, out var known))
        {
            try { known = repo.LabelKeysOf(file); }
            catch { return true; } // an index hiccup must not turn into a wall of false findings
            indexed[file] = known;
        }
        return keys.Any(known.Contains);
    }

    /// <summary>
    /// Tokens into <paramref name="labelFiles"/> used anywhere in the index outside
    /// <paramref name="model"/>: <c>Label</c> columns of every model, and the method bodies of
    /// every model in the source index.
    /// </summary>
    private static (HashSet<string> Tokens, List<string> NotSourceIndexed) UsesElsewhere(
        MetadataRepository repo, IEnumerable<string> labelFiles, string model)
    {
        var tokens = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in labelFiles)
        {
            foreach (var (label, _) in repo.FindIndexedLabelTokensInFile(file))
                tokens.Add(label.Trim());
            foreach (var body in repo.MethodBodiesMentioning(file))
                foreach (Match m in Token.Matches(body))
                    tokens.Add(m.Value);
        }

        var notIndexed = repo.GetModelSourceStates()
            .Where(s => s.Level == 0 && !string.Equals(s.Name, model, StringComparison.OrdinalIgnoreCase))
            .Select(s => s.Name)
            .ToList();
        return (tokens, notIndexed);
    }
}
