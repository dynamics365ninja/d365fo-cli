using D365FO.Core;
using Spectre.Console.Cli;

namespace D365FO.Cli.Commands;

/// <summary>
/// Emits a shell-specific tab-completion script for d365fo.
/// Usage: d365fo completion bash | source /dev/stdin
///        d365fo completion zsh  >> ~/.zshrc
///        d365fo completion powershell >> $PROFILE
/// </summary>
public sealed class CompletionCommand : Command<CompletionCommand.Settings>
{
    private static readonly string[] TopLevelCommands =
    [
        "search", "get", "find", "read", "resolve", "generate", "analyze",
        "review", "lint", "models", "index", "stats", "daemon", "report-integrations",
        "agent-prompt", "schema", "build", "sync", "test", "bp", "completion",
    ];

    private static readonly string[] SearchSubcommands =
    [
        "any", "batch", "table", "class", "edt", "enum", "form", "query", "view",
        "entity", "report", "service", "workflow", "label", "business-event",
        "security-policy", "configuration-key", "tile", "workspace",
    ];

    private static readonly string[] GetSubcommands =
    [
        "object", "table", "class", "edt", "enum", "form", "menu-item", "security",
        "label", "role", "duty", "privilege", "query", "view", "entity", "report",
        "service", "service-group", "business-event", "security-policy",
    ];

    private static readonly string[] FindSubcommands =
    [
        "coc", "relations", "usages", "extensions", "handlers", "refs",
        "form-patterns", "related", "batch-jobs", "menu-items",
    ];

    private static readonly string[] GenerateSubcommands =
    [
        "table", "class", "coc", "form", "entity", "extension", "event-handler",
        "privilege", "duty", "role", "report", "sysoperation", "number-sequence",
        "workflow", "menu-item", "edt", "enum", "query", "business-event",
        "custom-service", "migration-script", "runbase", "security-policy",
    ];

    private static readonly string[] AnalyzeSubcommands =
    [
        "completeness", "integration", "impact",
    ];

    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "<SHELL>")]
        [System.ComponentModel.Description("Target shell: bash, zsh, or powershell.")]
        public string Shell { get; init; } = "";
    }

    public override int Execute(CommandContext ctx, Settings settings)
    {
        var script = settings.Shell.ToLowerInvariant() switch
        {
            "bash" => BashScript(),
            "zsh"  => ZshScript(),
            "powershell" or "pwsh" => PowerShellScript(),
            _ => null,
        };

        if (script is null)
        {
            Console.Error.WriteLine($"Unknown shell '{settings.Shell}'. Use: bash, zsh, or powershell.");
            return 1;
        }

        Console.WriteLine(script);
        return 0;
    }

    private static string BashScript() => $@"# d365fo bash completion
# Usage: source <(d365fo completion bash)
_d365fo_complete() {{
  local cur prev words
  cur=""${{COMP_WORDS[COMP_CWORD]}}""
  prev=""${{COMP_WORDS[COMP_CWORD-1]}}""
  case ""$prev"" in
    d365fo)     COMPREPLY=($(compgen -W ""{string.Join(" ", TopLevelCommands)}"" -- ""$cur"")) ;;
    search)     COMPREPLY=($(compgen -W ""{string.Join(" ", SearchSubcommands)}"" -- ""$cur"")) ;;
    get)        COMPREPLY=($(compgen -W ""{string.Join(" ", GetSubcommands)}"" -- ""$cur"")) ;;
    find)       COMPREPLY=($(compgen -W ""{string.Join(" ", FindSubcommands)}"" -- ""$cur"")) ;;
    generate)   COMPREPLY=($(compgen -W ""{string.Join(" ", GenerateSubcommands)}"" -- ""$cur"")) ;;
    analyze)    COMPREPLY=($(compgen -W ""{string.Join(" ", AnalyzeSubcommands)}"" -- ""$cur"")) ;;
    *)          COMPREPLY=() ;;
  esac
}}
complete -F _d365fo_complete d365fo";

    private static string ZshScript() => $@"# d365fo zsh completion
# Usage: eval ""$(d365fo completion zsh)""
_d365fo() {{
  local -a cmds sub
  cmds=({string.Join(" ", TopLevelCommands.Select(c => $"'{c}'"))})
  case $words[2] in
    search)   sub=({string.Join(" ", SearchSubcommands.Select(c => $"'{c}'"))}) ;;
    get)      sub=({string.Join(" ", GetSubcommands.Select(c => $"'{c}'"))}) ;;
    find)     sub=({string.Join(" ", FindSubcommands.Select(c => $"'{c}'"))}) ;;
    generate) sub=({string.Join(" ", GenerateSubcommands.Select(c => $"'{c}'"))}) ;;
    analyze)  sub=({string.Join(" ", AnalyzeSubcommands.Select(c => $"'{c}'"))}) ;;
    *)        sub=() ;;
  esac
  if (( CURRENT == 2 )); then
    _describe 'd365fo commands' cmds
  else
    _describe 'subcommands' sub
  fi
}}
compdef _d365fo d365fo";

    private static string PowerShellScript() => $@"# d365fo PowerShell completion
# Usage: Invoke-Expression (d365fo completion powershell)
Register-ArgumentCompleter -Native -CommandName d365fo -ScriptBlock {{
  param($wordToComplete, $commandAst, $cursorPosition)
  $words = $commandAst.CommandElements
  $prev  = if ($words.Count -ge 2) {{ $words[1].ToString() }} else {{ '' }}
  $subs  = switch ($prev) {{
    'search'   {{ '{string.Join("','", SearchSubcommands)}' }}
    'get'      {{ '{string.Join("','", GetSubcommands)}' }}
    'find'     {{ '{string.Join("','", FindSubcommands)}' }}
    'generate' {{ '{string.Join("','", GenerateSubcommands)}' }}
    'analyze'  {{ '{string.Join("','", AnalyzeSubcommands)}' }}
    default    {{ '{string.Join("','", TopLevelCommands)}' }}
  }}
  $subs -split ',' | Where-Object {{ $_ -like ""$wordToComplete*"" }} |
    ForEach-Object {{ [System.Management.Automation.CompletionResult]::new($_, $_, 'ParameterValue', $_) }}
}}";
}
