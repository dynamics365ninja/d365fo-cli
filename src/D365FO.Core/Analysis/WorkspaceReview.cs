using System.Diagnostics;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace D365FO.Core.Analysis;

/// <summary>
/// Reviews what has changed in a working tree: the files <c>git diff</c> reports, and a small
/// rule engine over the AOT XML among them.
/// </summary>
/// <remarks>
/// The rules are deliberately shallow — regex and tree probes, not a compiler. The intent is a
/// fast "cheap pass" that tells a caller whether a deeper check (<c>bp check</c>, a build) is
/// worth its minutes. It lives in Core because "review my changes" is a question asked at least
/// as often through an agent as through a shell, and the shell was the only place that could
/// answer it.
/// </remarks>
public static class WorkspaceReview
{
    public static ToolResult<object> Diff(string? repoPath, string baseRev = "HEAD", string? headRev = null)
    {
        var repo = string.IsNullOrWhiteSpace(repoPath) ? Directory.GetCurrentDirectory() : repoPath!;
        if (!Directory.Exists(Path.Combine(repo, ".git")))
            return ToolResult<object>.Fail("NOT_A_GIT_REPO", $"No .git found at {repo}",
                "Pass a repository path, or run this from inside a git work tree.");

        var args = new List<string> { "-C", repo, "--no-pager", "diff", "--name-only" };
        args.Add(string.IsNullOrWhiteSpace(baseRev) ? "HEAD" : baseRev);
        if (!string.IsNullOrEmpty(headRev)) args.Add(headRev!);

        var (exit, stdout, stderr) = RunGit(args);
        if (exit != 0)
            return ToolResult<object>.Fail("GIT_FAILED", stderr.Trim());

        var changed = stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        var violations = new List<object>();

        foreach (var rel in changed)
        {
            if (!rel.EndsWith(".xml", StringComparison.OrdinalIgnoreCase) &&
                !rel.EndsWith(".xpp", StringComparison.OrdinalIgnoreCase)) continue;
            var full = Path.Combine(repo, rel);
            if (!File.Exists(full)) continue;

            string text;
            try { text = File.ReadAllText(full); } catch { continue; }

            if (rel.Contains("/AxTable/", StringComparison.Ordinal))
                InspectTableXml(rel, text, violations);
            if (rel.Contains("/AxClass/", StringComparison.Ordinal))
                InspectClassXml(rel, text, violations);
        }

        return ToolResult<object>.Success(new
        {
            baseRev,
            headRev,
            changedFiles = changed.Count,
            violationCount = violations.Count,
            violations,
        });
    }

    public static void InspectTableXml(string path, string text, List<object> bag)
    {
        XDocument doc;
        try { doc = XDocument.Parse(text); }
        catch (Exception ex)
        {
            bag.Add(new { file = path, rule = "XML_PARSE", severity = "error", message = ex.Message });
            return;
        }
        // Real field definitions live only under AxTable/Fields/AxTableField*.
        // Never descend into <FieldGroups> — <AxTableFieldGroup> names and
        // <AxTableFieldGroupField><DataField> references are not fields and
        // must not be evaluated as such.
        var fieldsContainer = doc.Root?.Elements().FirstOrDefault(e => e.Name.LocalName == "Fields");
        var fields = fieldsContainer?.Elements().Where(e => e.Name.LocalName.StartsWith("AxTableField", StringComparison.Ordinal))
            ?? Enumerable.Empty<XElement>();
        foreach (var f in fields)
        {
            var name = f.Elements().FirstOrDefault(e => e.Name.LocalName == "Name")?.Value ?? "?";
            var edtValue = f.Elements().FirstOrDefault(e => e.Name.LocalName == "ExtendedDataType")?.Value;
            var enumValue = f.Elements().FirstOrDefault(e => e.Name.LocalName == "EnumType")?.Value;
            // EnumType is an equivalent, valid type declaration for AxTableFieldEnum
            // fields — either it or a non-empty ExtendedDataType satisfies the rule,
            // and both also carry an inherited label from their type definition.
            var hasTypeDeclaration = !string.IsNullOrWhiteSpace(edtValue) || !string.IsNullOrWhiteSpace(enumValue);
            var hasLabel = f.Elements().Any(e => e.Name.LocalName == "Label");
            if (!hasTypeDeclaration)
                bag.Add(new { file = path, rule = "FIELD_WITHOUT_EDT", severity = "warning", field = name, message = $"Field '{name}' has no ExtendedDataType; prefer typed EDTs over raw types." });
            if (!hasLabel && !hasTypeDeclaration)
                bag.Add(new { file = path, rule = "FIELD_WITHOUT_LABEL", severity = "info", field = name, message = $"Field '{name}' has no Label; required for user-facing fields." });
        }
    }

    private static readonly Regex HardcodedString = new(@"@""[^""]{3,}""", RegexOptions.Compiled);

    private static void InspectClassXml(string path, string text, List<object> bag)
    {
        foreach (Match m in HardcodedString.Matches(text))
        {
            bag.Add(new
            {
                file = path,
                rule = "HARDCODED_STRING",
                severity = "warning",
                snippet = m.Value,
                message = "Hard-coded string literal found; consider using a labeled resource.",
            });
        }
        if (text.Contains("str2Con(", StringComparison.Ordinal) && text.Contains("new Query", StringComparison.Ordinal))
        {
            bag.Add(new
            {
                file = path,
                rule = "DYNAMIC_QUERY",
                severity = "info",
                message = "Dynamic Query construction detected; ensure security checks are in place.",
            });
        }
    }

    private static (int Exit, string StdOut, string StdErr) RunGit(IEnumerable<string> args)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "git",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi) ?? throw new InvalidOperationException("Failed to launch git");
        var so = p.StandardOutput.ReadToEnd();
        var se = p.StandardError.ReadToEnd();
        p.WaitForExit();
        return (p.ExitCode, so, se);
    }
}
