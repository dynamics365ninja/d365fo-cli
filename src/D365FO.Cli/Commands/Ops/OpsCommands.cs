using D365FO.Core;
using Spectre.Console;
using Spectre.Console.Cli;

namespace D365FO.Cli.Commands.Ops;

public sealed class DoctorCommand : Command<DoctorCommand.Settings>
{
    public sealed class Settings : D365OutputSettings { }

    public override int Execute(CommandContext ctx, Settings settings)
    {
        var kind = OutputMode.Resolve(settings.Output);
        var cfg = D365FoSettings.FromEnvironment();
        var checks = new List<object>();
        bool allOk = true;

        void Add(string name, bool ok, string? detail = null)
        {
            checks.Add(new { name, ok, detail });
            if (!ok) allOk = false;
        }

        Add("config.databasePath resolvable", !string.IsNullOrEmpty(cfg.DatabasePath), cfg.DatabasePath);
        Add("config.packagesPath set", !string.IsNullOrEmpty(cfg.PackagesPath),
            cfg.PackagesPath ?? "Set D365FO_PACKAGES_PATH or use --packages.");
        Add("index db exists", File.Exists(cfg.DatabasePath),
            File.Exists(cfg.DatabasePath) ? null : "Run 'd365fo index build'.");
        Add("runtime", true, $".NET {Environment.Version} on {Environment.OSVersion.Platform}");

        var result = allOk
            ? ToolResult<object>.Success(new { checks })
            : ToolResult<object>.Fail("DOCTOR_FAILED", "One or more checks failed.", "See 'checks' array in --output json.") with
            {
                // keep failed payload visible:
            };
        // Re-wrap to keep data on failure too
        var payload = ToolResult<object>.Success(new { ok = allOk, checks });

        return RenderHelpers.Render(kind, payload, _ =>
        {
            foreach (dynamic c in checks)
            {
                var ok = (bool)c.ok;
                AnsiConsole.MarkupLine($"{(ok ? "[green]✓[/]" : "[red]✗[/]")} {c.name} {(c.detail is null ? "" : $"[grey]— {RenderHelpers.Escape((string)c.detail)}[/]")}");
            }
            AnsiConsole.MarkupLine(allOk ? "[green]All checks passed.[/]" : "[red]Some checks failed.[/]");
        });
    }
}

public sealed class VersionCommand : Command<VersionCommand.Settings>
{
    public sealed class Settings : D365OutputSettings { }

    public override int Execute(CommandContext ctx, Settings settings)
    {
        var kind = OutputMode.Resolve(settings.Output);
        var asm = typeof(VersionCommand).Assembly.GetName();
        var payload = ToolResult<object>.Success(new
        {
            name = "d365fo",
            version = asm.Version?.ToString() ?? "0.1.0-dev",
            runtime = $".NET {Environment.Version}",
            os = Environment.OSVersion.ToString(),
        });
        return RenderHelpers.Render(kind, payload);
    }
}
