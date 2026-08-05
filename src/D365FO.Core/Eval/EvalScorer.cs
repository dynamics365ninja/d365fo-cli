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
    public static EvalScoreCard Score(EvalCase @case, string actualXmlPath, string goldensRoot, MetadataRepository repo)
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

        return new EvalScoreCard(
            XppClean: xppErrors == 0,
            XppErrors: xppErrors,
            ReferencesClean: refErrors == 0,
            ReferenceErrors: refErrors,
            GoldenMatch: diff.IsMatch,
            GoldenDiff: diff);
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
