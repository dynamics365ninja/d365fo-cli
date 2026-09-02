namespace D365FO.Core.Eval;

/// <summary>
/// Locates the <c>eval/</c> layout relative to the repo root. The eval loop
/// is a maintainer/CI tool that only makes sense run from within a checkout
/// of this repo (it authors goldens, corpus records, and fix PRs against
/// this source tree) — so, unlike the rest of the CLI, it is not designed to
/// work from an installed/published binary with no source tree nearby.
/// </summary>
public static class EvalPaths
{
    /// <summary>
    /// Walk up from <paramref name="startDir"/> (default: the executing
    /// assembly's directory) looking for <c>d365fo-cli.slnx</c>. Returns
    /// null when no repo root is found (e.g. a published, standalone binary).
    /// </summary>
    public static string? FindRepoRoot(string? startDir = null)
    {
        var dir = new DirectoryInfo(startDir ?? AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "d365fo-cli.slnx")))
                return dir.FullName;
            dir = dir.Parent;
        }
        return null;
    }

    public static string CasesDir(string repoRoot) => Path.Combine(repoRoot, "eval", "cases");
    public static string GoldensDir(string repoRoot) => Path.Combine(repoRoot, "eval", "goldens");
    public static string CorpusRunsDir(string repoRoot) => Path.Combine(repoRoot, "eval", "corpus", "runs");

    /// <summary>
    /// Artefacts a case starts FROM rather than produces: the existing AxTable a
    /// <c>--apply-to</c> command merges into. Copied into the replay's work directory, never
    /// mutated in place.
    /// </summary>
    public static string SeedsDir(string repoRoot) => Path.Combine(repoRoot, "eval", "seeds");

    /// <summary>The checked-in mini-AOT fixture also used by MiniAotEndToEndTests / GoldenQualityGateTests.</summary>
    public static string FixtureDir(string repoRoot) => Path.Combine(repoRoot, "tests", "Samples", "MiniAot");

    /// <summary>Reviewed exceptions for the knowledge audit — names that resolve in no index.</summary>
    public static string KnowledgeAllowPath(string repoRoot) =>
        Path.Combine(repoRoot, "eval", "knowledge-audit.allow.json");

    /// <summary>The committed knowledge-audit snapshot, captured against a full standard index.</summary>
    public static string KnowledgeSnapshotPath(string repoRoot) =>
        Path.Combine(repoRoot, "eval", "knowledge-audit.snapshot.json");
}
