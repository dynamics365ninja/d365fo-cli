using D365FO.Core;
using D365FO.Core.Eval;

namespace D365FO.Cli.Commands.Eval;

/// <summary>Shared repo-root resolution for every <c>eval</c> subcommand.</summary>
internal static class EvalPathsResolver
{
    public static (string? RepoRoot, int? Failure) Resolve(OutputMode.Kind kind)
    {
        var root = EvalPaths.FindRepoRoot();
        if (root is null)
        {
            var failure = RenderHelpers.Render(kind, ToolResult<object>.Fail(
                "EVAL_NO_REPO_ROOT",
                "Could not locate the repo root (no d365fo-cli.slnx found above the executing assembly).",
                "Run `d365fo eval` commands from within a checkout of the d365fo-cli repo — the eval loop authors goldens/corpus/PRs against this source tree, so it does not work from a standalone installed binary."));
            return (null, failure);
        }
        return (root, null);
    }
}
