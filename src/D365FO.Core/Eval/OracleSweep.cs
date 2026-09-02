using System.Collections.Concurrent;
using System.Diagnostics;
using System.Xml.Linq;
using D365FO.Core.Validation;

namespace D365FO.Core.Eval;

/// <summary>
/// Runs the offline validator over an entire installation and reports what it says about code
/// nobody is allowed to be wrong about.
/// </summary>
/// <remarks>
/// <para>
/// The validator's rules are written from documentation, from the compiler's own messages and
/// from what the AOT appears to do. None of that proves a rule does not fire on correct code —
/// and a rule that flags Microsoft's own X++ is worse than a missing rule, because it teaches a
/// caller to ignore findings. The bar is therefore not "few" false positives on shipped code but
/// <b>zero errors</b>, and the only way to hold it is to run every rule over every file and look.
/// </para>
/// <para>
/// Warnings are counted and reported but do not fail the bar: several are style rules that
/// shipped code legitimately breaks (a table with one index, a method without a doc comment).
/// Errors are the claim that the artefact does not work, and that claim is falsifiable by the
/// fact that the file ships and builds.
/// </para>
/// </remarks>
public static class OracleSweep
{
    /// <param name="Model">Restrict to one model.</param>
    /// <param name="AxFolder">Restrict to one AOT folder, e.g. AxTable.</param>
    /// <param name="Limit">Stop after this many files. 0 sweeps everything.</param>
    /// <param name="SamplesPerRule">How many example findings to keep per rule.</param>
    /// <param name="IncludeWarnings">Report warnings alongside errors.</param>
    /// <param name="Parallelism">Files parsed at once. Defaults to half the cores.</param>
    public sealed record Options(
        string? Model = null,
        string? AxFolder = null,
        int Limit = 0,
        int SamplesPerRule = 3,
        bool IncludeWarnings = false,
        int? Parallelism = null);

    /// <param name="Rule">Rule id, e.g. XML007 or SEL001.</param>
    /// <param name="Where">xml | xpp — which half of the file the rule judged.</param>
    /// <param name="Severity">error | warning, as the rule reported it.</param>
    /// <param name="Model">Model the file belongs to.</param>
    /// <param name="File">Full path to the file the rule fired on.</param>
    /// <param name="Line">Line the rule points at, when it knows one.</param>
    /// <param name="Excerpt">What the rule was looking at.</param>
    /// <param name="Fix">What the rule says to do about it.</param>
    public sealed record Finding(
        string Rule, string Severity, string Where, string Model, string File, int? Line, string Excerpt, string Fix);

    public sealed record RuleTally(string Rule, string Severity, string Where, int Count, IReadOnlyList<Finding> Samples);

    public sealed record Report(
        string Root,
        int FilesScanned,
        int FilesUnreadable,
        int XppBlocksScanned,
        int Errors,
        int Warnings,
        bool BarHeld,
        long ElapsedMs,
        IReadOnlyList<RuleTally> ByRule);

    /// <summary>Sweep an installation (or any packages-shaped root).</summary>
    public static Report Run(string root, Options? options = null, IPropertyStatsProvider? stats = null)
    {
        var opts = options ?? new Options();
        var sw = Stopwatch.StartNew();

        var files = EnumerateAotFiles(root, opts).ToList();
        if (opts.Limit > 0 && files.Count > opts.Limit) files = files.Take(opts.Limit).ToList();

        // Tallied as the sweep runs, not collected and grouped afterwards. An installation is
        // 200 000 files and several million findings once warnings are counted; keeping each one
        // to group it at the end costs gigabytes and tells you nothing the counters do not. Only
        // the first few examples per rule are held.
        var tallies = new ConcurrentDictionary<(string Rule, string Severity, string Where), Tally>();
        var unreadable = 0;
        var xppBlocks = 0;

        Parallel.ForEach(
            files,
            new ParallelOptions { MaxDegreeOfParallelism = opts.Parallelism ?? Math.Max(1, Environment.ProcessorCount / 2) },
            file =>
            {
                string text;
                try { text = File.ReadAllText(file.Path); }
                catch { Interlocked.Increment(ref unreadable); return; }

                // The XML half: the document's own shape.
                try
                {
                    foreach (var v in XppValidator.Validate(text, XppValidator.CodeTypeXmlAny, stats, file.Path))
                        Keep(tallies, v, "xml", file, opts.SamplesPerRule);
                }
                catch (Exception ex)
                {
                    // A rule that throws is itself a defect, and silence would hide it.
                    Keep(tallies, new XppViolation("SWEEP_THREW", "error", null, ex.GetType().Name, ex.Message),
                        "xml", file, opts.SamplesPerRule);
                }

                // The X++ half: every Declaration/Source block the document carries.
                var xpp = ExtractXpp(text);
                if (xpp.Length == 0) return;
                Interlocked.Increment(ref xppBlocks);
                try
                {
                    foreach (var v in XppValidator.Validate(xpp, XppValidator.CodeTypeXpp, stats, file.Path))
                        Keep(tallies, v, "xpp", file, opts.SamplesPerRule);
                }
                catch (Exception ex)
                {
                    Keep(tallies, new XppViolation("SWEEP_THREW", "error", null, ex.GetType().Name, ex.Message),
                        "xpp", file, opts.SamplesPerRule);
                }
            });

        var errors = tallies.Where(t => t.Key.Severity == "error").Sum(t => t.Value.Count);
        var warnings = tallies.Where(t => t.Key.Severity != "error").Sum(t => t.Value.Count);

        var byRule = tallies
            .Where(t => opts.IncludeWarnings || t.Key.Severity == "error")
            .OrderByDescending(t => t.Key.Severity == "error")
            .ThenByDescending(t => t.Value.Count)
            .ThenBy(t => t.Key.Rule, StringComparer.Ordinal)
            .Select(t => new RuleTally(
                t.Key.Rule, t.Key.Severity, t.Key.Where, t.Value.Count, t.Value.Snapshot()))
            .ToList();

        sw.Stop();
        return new Report(
            root, files.Count, unreadable, xppBlocks, errors, warnings,
            BarHeld: errors == 0,
            sw.ElapsedMilliseconds,
            byRule);
    }

    /// <summary>One rule's running count, plus the first few examples of it.</summary>
    /// <remarks>
    /// Bounded on purpose: the count is what the bar is judged on, and a few examples are what
    /// makes a count actionable. Holding the rest would make the sweep's memory a function of how
    /// wrong the rules are, which is the wrong thing to be unable to measure.
    /// </remarks>
    private sealed class Tally
    {
        private readonly List<Finding> _samples = [];
        private int _count;

        public int Count => Volatile.Read(ref _count);

        public void Add(Finding finding, int sampleCap)
        {
            Interlocked.Increment(ref _count);
            if (sampleCap <= 0) return;
            lock (_samples)
            {
                if (_samples.Count < sampleCap) _samples.Add(finding);
            }
        }

        public IReadOnlyList<Finding> Snapshot()
        {
            lock (_samples) return _samples.ToList();
        }
    }

    private static void Keep(
        ConcurrentDictionary<(string, string, string), Tally> into,
        XppViolation v, string where, AotFile file, int sampleCap) =>
        into.GetOrAdd((v.Rule, v.Severity, where), _ => new Tally())
            .Add(new Finding(v.Rule, v.Severity, where, file.Model, file.Path, v.Line, v.Excerpt, v.Fix), sampleCap);

    /// <param name="Path">Full path to the AOT XML.</param>
    /// <param name="Model">Model the file belongs to.</param>
    private readonly record struct AotFile(string Path, string Model);

    private static IEnumerable<AotFile> EnumerateAotFiles(string root, Options opts)
    {
        if (!Directory.Exists(root)) yield break;

        foreach (var package in SafeDirectories(root))
        foreach (var model in SafeDirectories(package))
        {
            var modelName = Path.GetFileName(model);
            if (opts.Model is { Length: > 0 } wanted
                && !string.Equals(modelName, wanted, StringComparison.OrdinalIgnoreCase))
                continue;

            foreach (var axDir in SafeDirectories(model))
            {
                var folder = Path.GetFileName(axDir);
                if (!folder.StartsWith("Ax", StringComparison.Ordinal)) continue;
                if (opts.AxFolder is { Length: > 0 } kind
                    && !string.Equals(folder, kind, StringComparison.OrdinalIgnoreCase))
                    continue;

                IEnumerable<string> xml;
                try { xml = Directory.EnumerateFiles(axDir, "*.xml", SearchOption.TopDirectoryOnly); }
                catch { continue; }

                foreach (var path in xml) yield return new AotFile(path, modelName);
            }
        }
    }

    private static IEnumerable<string> SafeDirectories(string dir)
    {
        try { return Directory.EnumerateDirectories(dir); }
        catch { return Array.Empty<string>(); }
    }

    /// <summary>Every X++ block a document carries, concatenated.</summary>
    /// <remarks>
    /// Parsed rather than regexed: a <c>&lt;Source&gt;</c> inside a comment or a doc-comment
    /// mentioning one would otherwise be swept as if it were code, and the finding would name a
    /// line that is not there.
    /// </remarks>
    private static string ExtractXpp(string text)
    {
        try
        {
            var doc = XDocument.Parse(text);
            var parts = doc.Root?.Descendants()
                .Where(e => e.Name.LocalName is "Declaration" or "Source")
                .Select(e => e.Value)
                .Where(s => !string.IsNullOrWhiteSpace(s));
            return parts is null ? "" : string.Join("\n", parts);
        }
        catch
        {
            return "";
        }
    }
}
