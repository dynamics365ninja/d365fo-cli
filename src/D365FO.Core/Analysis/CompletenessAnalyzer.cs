using System.Text.RegularExpressions;
using System.Xml.Linq;
using D365FO.Core.Index;

namespace D365FO.Core.Analysis;

/// <summary>
/// Walks a workspace folder (or a single AOT XML file) and cross-checks every object against
/// the index, reporting references that resolve to nothing.
/// </summary>
/// <remarks>
/// <para>Checks performed:</para>
/// <list type="bullet">
///   <item><term>MISSING_DUTY</term> — AxSecurityRole references a duty not in the index.</item>
///   <item><term>MISSING_PRIVILEGE</term> — AxSecurityRole / AxSecurityDuty references a privilege not in the index.</item>
///   <item><term>MISSING_EDT</term> — AxTable field references an EDT not in the index.</item>
///   <item><term>MISSING_LABEL</term> — an element value holds a label token (@File:Key) with no indexed translation.</item>
///   <item><term>PARSE_ERROR</term> — the file is not readable XML.</item>
/// </list>
/// <para>
/// The walk lives here rather than inside the CLI command it was written for so that both
/// surfaces can run it. While it was a command body, <c>analyze</c> over MCP offered
/// integration, impact and report but not completeness — the one check of the four that reads
/// the developer's own working tree.
/// </para>
/// </remarks>
public static class CompletenessAnalyzer
{
    /// <summary>Which families of check to run. All on by default.</summary>
    public sealed record Options(bool SkipLabels = false, bool SkipEdts = false, bool SkipSecurity = false);

    /// <param name="Severity">error | warning.</param>
    /// <param name="Code">Machine-readable check id, e.g. MISSING_EDT.</param>
    /// <param name="File">File name alone, for display.</param>
    /// <param name="FilePath">Full path, for acting on.</param>
    /// <param name="Message">What is wrong, in the words a reader can act on.</param>
    public sealed record Issue(string Severity, string Code, string File, string FilePath, string Message);

    public sealed record Report(
        string Path,
        int IssueCount,
        bool SkipLabels,
        bool SkipEdts,
        bool SkipSecurity,
        IReadOnlyList<Issue> Issues);

    // Matches @LabelFile:LabelKey or @LabelKey (no file prefix).
    private static readonly Regex LabelTokenRegex =
        new(@"@(?:[A-Za-z0-9_]+:)?[A-Za-z0-9_]+", RegexOptions.Compiled);

    /// <summary>Analyse one file or, recursively, one directory.</summary>
    public static Report Analyze(string path, MetadataRepository repo, Options? options = null)
    {
        ArgumentNullException.ThrowIfNull(repo);
        var opts = options ?? new Options();
        var issues = new List<Issue>();

        IEnumerable<string> xmlFiles = File.Exists(path)
            ? [path]
            : Directory.EnumerateFiles(path, "*.xml", SearchOption.AllDirectories);

        foreach (var file in xmlFiles)
        {
            XDocument doc;
            try
            {
                doc = XDocument.Load(file);
            }
            catch (Exception ex)
            {
                issues.Add(Make("error", "PARSE_ERROR", file, $"Could not parse XML: {ex.Message}"));
                continue;
            }

            var rootName = doc.Root?.Name.LocalName ?? "";

            // ---- Security checks ------------------------------------------------
            if (!opts.SkipSecurity)
            {
                if (rootName == "AxSecurityRole")
                {
                    foreach (var dutyRef in doc.Descendants("AxSecurityDutyReference"))
                    {
                        var duty = dutyRef.Element("Name")?.Value;
                        if (!string.IsNullOrWhiteSpace(duty) && repo.GetSecurityDuty(duty) is null)
                            issues.Add(Make("warning", "MISSING_DUTY", file,
                                $"Role references duty '{duty}' which is not in the index."));
                    }
                    foreach (var privRef in doc.Descendants("AxSecurityPrivilegeReference"))
                    {
                        var priv = privRef.Element("Name")?.Value;
                        if (!string.IsNullOrWhiteSpace(priv) && repo.GetSecurityPrivilege(priv) is null)
                            issues.Add(Make("warning", "MISSING_PRIVILEGE", file,
                                $"Role references privilege '{priv}' which is not in the index."));
                    }
                }

                if (rootName == "AxSecurityDuty")
                {
                    foreach (var privRef in doc.Descendants("AxSecurityPrivilegeReference"))
                    {
                        var priv = privRef.Element("Name")?.Value;
                        if (!string.IsNullOrWhiteSpace(priv) && repo.GetSecurityPrivilege(priv) is null)
                            issues.Add(Make("warning", "MISSING_PRIVILEGE", file,
                                $"Duty references privilege '{priv}' which is not in the index."));
                    }
                }
            }

            // ---- EDT checks (AxTable fields) ------------------------------------
            if (!opts.SkipEdts && rootName == "AxTable")
            {
                foreach (var field in doc.Descendants("AxTableField"))
                {
                    var edtName = field.Element("ExtendedDataType")?.Value
                                ?? field.Element("Edt")?.Value;
                    if (!string.IsNullOrWhiteSpace(edtName) && repo.GetEdt(edtName) is null)
                    {
                        var fieldName = field.Element("Name")?.Value ?? "(unknown)";
                        issues.Add(Make("warning", "MISSING_EDT", file,
                            $"Field '{fieldName}' references EDT '{edtName}' which is not in the index."));
                    }
                }
            }

            // ---- Label checks (any element value @File:Key) ---------------------
            if (!opts.SkipLabels)
            {
                foreach (var el in doc.Descendants())
                {
                    if (el.HasElements) continue; // only leaf text nodes
                    var text = el.Value;
                    if (string.IsNullOrWhiteSpace(text) || !text.StartsWith('@')) continue;
                    var m = LabelTokenRegex.Match(text.Trim());
                    if (!m.Success) continue;
                    var token = m.Value;
                    var hits = repo.ResolveLabel(token);
                    if (hits.Count == 0)
                        issues.Add(Make("warning", "MISSING_LABEL", file,
                            $"Element <{el.Name.LocalName}> references label '{token}' which has no indexed translation."));
                }
            }
        }

        return new Report(path, issues.Count, opts.SkipLabels, opts.SkipEdts, opts.SkipSecurity, issues);
    }

    private static Issue Make(string severity, string code, string file, string message) =>
        new(severity, code, System.IO.Path.GetFileName(file), file, message);
}
