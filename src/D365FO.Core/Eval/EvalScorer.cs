using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using D365FO.Core.Index;
using D365FO.Core.Validation;

namespace D365FO.Core.Eval;

/// <summary>
/// VM-free scorer: given a produced artifact file, checks it against the
/// same two offline gates <c>validate xpp</c>/<c>validate references</c>
/// expose on the CLI (called here directly against the Core APIs, not via a
/// subprocess), then diffs it against the case's committed golden.
/// </summary>
public static class EvalScorer
{
    /// <param name="case">The case being scored — its golden path and ignore list.</param>
    /// <param name="actualXmlPath">The artefact the run produced (or merged into).</param>
    /// <param name="goldensRoot">The <c>eval/goldens</c> directory.</param>
    /// <param name="repo">Index the reference check resolves against.</param>
    /// <param name="producedDir">
    /// The directory the run wrote into, when the caller owns it exclusively (the replay's work
    /// directory). Given one, a companion the golden does not have is reported as an extra;
    /// without one, only the companions the golden names are compared — a directory that
    /// happens to hold the artefact says nothing about what produced the files beside it.
    /// </param>
    public static EvalScoreCard Score(
        EvalCase @case, string actualXmlPath, string goldensRoot, MetadataRepository repo,
        string? producedDir = null)
    {
        var actualXml = File.ReadAllText(actualXmlPath);
        var actualRoot = XElement.Parse(actualXml);

        var codeType = DetectCodeType(actualXml);
        var xppViolations = XppValidator.Validate(actualXml, codeType);
        var xppErrors = xppViolations.Count(v => v.Severity == "error");

        var refResult = ReferenceResolver.Resolve(actualXml, repo);
        var refErrors = refResult.Violations.Count(v => v.Severity == "error");

        var goldenDir = Path.Combine(goldensRoot, @case.GoldenPath);
        var goldenFiles = Directory.Exists(goldenDir) ? Directory.GetFiles(goldenDir, "*.xml") : Array.Empty<string>();

        XmlGoldenDiff diff;
        if (goldenFiles.Length == 1)
        {
            var expectedRoot = XElement.Parse(File.ReadAllText(goldenFiles[0]));
            diff = XmlGolden.Diff(expectedRoot, actualRoot, @case.Ignore);
        }
        else
        {
            var note = goldenFiles.Length == 0
                ? $"<no golden captured at {goldenDir}>"
                : $"<ambiguous golden: {goldenFiles.Length} files in {goldenDir}, expected exactly 1>";
            diff = new XmlGoldenDiff(new[] { note }, Array.Empty<string>(), Array.Empty<XmlGoldenChange>());
        }

        diff = WithCompanions(diff, actualXmlPath, producedDir, goldenDir, @case.Ignore);

        return new EvalScoreCard(
            XppClean: xppErrors == 0,
            XppErrors: xppErrors,
            ReferencesClean: refErrors == 0,
            ReferenceErrors: refErrors,
            GoldenMatch: diff.IsMatch,
            GoldenDiff: diff);
    }

    /// <summary>
    /// Fold the companions into the diff: every artefact the case produces beside its main one.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Several <c>generate</c> commands emit more than one file — a custom service is a class, a
    /// service and a service group; a workflow is a template, a query, a document class and a
    /// CoC extension. Only the main artefact used to be diffed, so the companions were captured
    /// under <c>_companions/</c>, reviewed once, and then never checked again: any later drift in
    /// them was invisible to the whole corpus. That also made a case's
    /// <c>target_artifact_types</c> half true, because the families it named were the companions'
    /// families and nothing compared them to anything.
    /// </para>
    /// <para>
    /// A companion the golden has and the run did not produce is <c>Missing</c>; one the run
    /// produced and the golden does not have is <c>Extra</c> — a new file appearing unannounced
    /// is exactly the drift worth failing on, and it is only reported when the caller passed the
    /// directory the run owns. Paths are prefixed with the companion's file name so a diff line
    /// says which artefact it is about.
    /// </para>
    /// </remarks>
    private static XmlGoldenDiff WithCompanions(
        XmlGoldenDiff diff, string actualXmlPath, string? producedDir, string goldenDir,
        IReadOnlyList<string> ignore)
    {
        var companionDir = Path.Combine(goldenDir, "_companions");
        var actualDir = producedDir ?? Path.GetDirectoryName(Path.GetFullPath(actualXmlPath))!;
        var scored = Path.GetFullPath(actualXmlPath);

        // Recursive, and not only XML: a content companion sits in a sub-folder that mirrors
        // where it lands beside the artefact — LabelResources/<lang>/, ResourceContent/<Type>/ —
        // so a flat *.xml walk saw neither the golden's copy nor the run's.
        var expected = Directory.Exists(companionDir)
            ? Directory.GetFiles(companionDir, "*", SearchOption.AllDirectories)
            : Array.Empty<string>();

        // Only a directory the caller owns gets walked. Without one the scorer looks the
        // golden's companions up by path instead: the artefact may be sitting in a shared temp
        // directory, where enumerating everything is both meaningless — that is why extras are
        // not reported either — and, on Linux, an UnauthorizedAccessException from somebody
        // else's subtree of /tmp.
        var produced = producedDir is null
            ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            : Directory.GetFiles(producedDir, "*", new EnumerationOptions
                {
                    RecurseSubdirectories = true,
                    IgnoreInaccessible = true,
                })
                .Where(f => !string.Equals(Path.GetFullPath(f), scored, StringComparison.OrdinalIgnoreCase))
                .Where(f => !IsWriterSidecar(f))
                .ToDictionary(f => RelativeName(producedDir, f), f => f, StringComparer.OrdinalIgnoreCase);

        if (expected.Length == 0 && produced.Count == 0) return diff;

        var missing = diff.Missing.ToList();
        var extra = diff.Extra.ToList();
        var changed = diff.Changed.ToList();

        foreach (var goldenCompanion in expected.OrderBy(f => f, StringComparer.Ordinal))
        {
            var name = RelativeName(companionDir, goldenCompanion);
            if (!produced.Remove(name, out var actualCompanion))
            {
                var beside = Path.Combine(actualDir, name.Replace('/', Path.DirectorySeparatorChar));
                if (producedDir is not null || !File.Exists(beside))
                {
                    missing.Add($"_companions/{name}");
                    continue;
                }
                actualCompanion = beside;
            }

            // Not every companion is XML. A label file's .label.txt and a resource's image are
            // the artefact as far as the platform is concerned — the manifest beside them is
            // only a pointer — so they are compared as content. Diffing only the XML is what let
            // `generate label-file --overwrite` truncate a .label.txt to a bare BOM with both
            // its cases still green.
            if (!IsXml(name))
            {
                var expectedBytes = File.ReadAllBytes(goldenCompanion);
                var actualBytes = File.ReadAllBytes(actualCompanion);
                if (!ContentEquals(expectedBytes, actualBytes))
                    changed.Add(new XmlGoldenChange($"_companions/{name}",
                        Describe(expectedBytes), Describe(actualBytes)));
                continue;
            }

            XElement expectedRoot, actualRoot;
            try
            {
                expectedRoot = XElement.Parse(File.ReadAllText(goldenCompanion));
                actualRoot = XElement.Parse(File.ReadAllText(actualCompanion));
            }
            catch (System.Xml.XmlException ex)
            {
                changed.Add(new XmlGoldenChange($"_companions/{name}", "<well-formed XML>", ex.Message));
                continue;
            }

            var sub = XmlGolden.Diff(expectedRoot, actualRoot, ignore);
            missing.AddRange(sub.Missing.Select(m => $"_companions/{name}:{m}"));
            extra.AddRange(sub.Extra.Select(e => $"_companions/{name}:{e}"));
            changed.AddRange(sub.Changed.Select(c => c with { Path = $"_companions/{name}:{c.Path}" }));
        }

        if (producedDir is not null)
        {
            foreach (var unexpected in produced.Keys.OrderBy(k => k, StringComparer.Ordinal))
                extra.Add($"_companions/{unexpected}");
        }

        return new XmlGoldenDiff(missing, extra, changed);
    }

    /// <summary>A file's path relative to <paramref name="root"/>, always with forward slashes.</summary>
    private static string RelativeName(string root, string file)
        => Path.GetRelativePath(root, file).Replace('\\', '/');

    private static bool IsXml(string name)
        => name.EndsWith(".xml", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Files the writer leaves beside what it wrote rather than artefacts the command produced:
    /// the <c>.bak</c> an overwrite keeps for manual recovery, and the per-call
    /// <c>.&lt;guid&gt;.tmp</c> a write stages through. A case that merges into a seed
    /// (<c>apply_to_seed</c>) always makes the first of those, and it says nothing about the
    /// artefact.
    /// </summary>
    private static bool IsWriterSidecar(string path)
    {
        var name = Path.GetFileName(path);
        return name.EndsWith(".bak", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Whether two content companions are the same file. Bytes first; text that differs only in
    /// its BOM or line endings is the same content, because git owns both on the way to a
    /// checkout and neither says anything about what the tool produced.
    /// </summary>
    private static bool ContentEquals(byte[] expected, byte[] actual)
    {
        if (expected.AsSpan().SequenceEqual(actual)) return true;
        if (!LooksTextual(expected) || !LooksTextual(actual)) return false;
        return string.Equals(NormalizeText(expected), NormalizeText(actual), StringComparison.Ordinal);
    }

    private static bool LooksTextual(byte[] bytes) => !bytes.Contains((byte)0);

    private static string NormalizeText(byte[] bytes)
        => new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetString(bytes)
            .TrimStart(Bom).Replace("\r\n", "\n");

    /// <summary>U+FEFF, as it arrives when a UTF-8 file with a byte-order mark is decoded.</summary>
    private const char Bom = (char)0xFEFF;

    private static string Describe(byte[] bytes)
    {
        if (!LooksTextual(bytes)) return $"<{bytes.Length} bytes>";
        var text = NormalizeText(bytes);
        return text.Length == 0
            ? "<empty>"
            : text.Length <= 200 ? text : text[..200] + "…";
    }

    /// <summary>Same heuristic as <c>ValidateXppCommand.DetectCodeType</c> (Cli project) — duplicated rather than shared across the Core/Cli boundary for a 3-line check.</summary>
    internal static string DetectCodeType(string xml)
    {
        // Match the exact <AxTable> root tag, not other elements that merely start with
        // that prefix (e.g. <AxTableMapping> inside an AxMap, <AxTableExtension>).
        if (Regex.IsMatch(xml, @"<AxTable[\s>]", RegexOptions.IgnoreCase)) return XppValidator.CodeTypeXmlTable;
        var trimmed = xml.TrimStart();
        if (trimmed.StartsWith("<?xml") || trimmed.StartsWith('<')) return XppValidator.CodeTypeXmlAny;
        return XppValidator.CodeTypeXpp;
    }
}
