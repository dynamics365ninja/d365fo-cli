using System.Diagnostics;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using D365FO.Core;
using Spectre.Console.Cli;

namespace D365FO.Cli.Commands.Review;

/// <summary>
/// Runs a diff (via <c>git diff</c>) against the current workspace and applies
/// a tiny rule engine to surface common D365FO review hazards. The rules are
/// deliberately shallow — they're regex/tree probes, not a compiler. Intent:
/// give the agent a fast "cheap pass" so it can ask the user for a deeper
/// check (via <c>d365fo bp check</c>) when something looks off.
/// </summary>
public sealed class ReviewDiffCommand : Command<ReviewDiffCommand.Settings>
{
    public sealed class Settings : D365OutputSettings
    {
        [CommandOption("--base <REV>")]
        public string BaseRev { get; init; } = "HEAD";

        [CommandOption("--head <REV>")]
        public string HeadRev { get; init; } = "";

        [CommandOption("--repo <PATH>")]
        public string? RepoPath { get; init; }
    }

    public override int Execute(CommandContext ctx, Settings settings)
    {
        var kind = OutputMode.Resolve(settings.Output);
        var repo = settings.RepoPath ?? Directory.GetCurrentDirectory();
        if (!Directory.Exists(Path.Combine(repo, ".git")))
            return RenderHelpers.Render(kind, ToolResult<object>.Fail(
                "NOT_A_GIT_REPO", $"No .git found at {repo}", "Pass --repo <PATH> or cd into a git repo."));

        var args = new List<string> { "-C", repo, "--no-pager", "diff", "--name-only" };
        args.Add(settings.BaseRev);
        if (!string.IsNullOrEmpty(settings.HeadRev)) args.Add(settings.HeadRev);

        var (exit, stdout, stderr) = RunGit(args);
        if (exit != 0)
            return RenderHelpers.Render(kind, ToolResult<object>.Fail("GIT_FAILED", stderr.Trim()));

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

        return RenderHelpers.Render(kind, ToolResult<object>.Success(new
        {
            baseRev = settings.BaseRev,
            headRev = settings.HeadRev,
            changedFiles = changed.Count,
            violationCount = violations.Count,
            violations,
        }));
    }

    private static void InspectTableXml(string path, string text, List<object> bag)
    {
        XDocument doc;
        try { doc = XDocument.Parse(text); }
        catch (Exception ex)
        {
            bag.Add(new { file = path, rule = "XML_PARSE", severity = "error", message = ex.Message });
            return;
        }
        var fields = doc.Descendants().Where(e => e.Name.LocalName.StartsWith("AxTableField", StringComparison.Ordinal));
        foreach (var f in fields)
        {
            var name = f.Elements().FirstOrDefault(e => e.Name.LocalName == "Name")?.Value ?? "?";
            var hasEdt = f.Elements().Any(e => e.Name.LocalName == "ExtendedDataType");
            var hasLabel = f.Elements().Any(e => e.Name.LocalName == "Label");
            if (!hasEdt)
                bag.Add(new { file = path, rule = "FIELD_WITHOUT_EDT", severity = "warning", field = name, message = $"Field '{name}' has no ExtendedDataType; prefer typed EDTs over raw types." });
            if (!hasLabel)
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
