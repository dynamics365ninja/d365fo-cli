using System.Reflection;
using System.Text.RegularExpressions;
using D365FO.Cli.Commands.Generate;
using Xunit;

namespace D365FO.Cli.Tests;

/// <summary>
/// The structural half of issue #161: no <c>generate</c> subcommand can reach disk without
/// having run the grounding gate.
/// </summary>
/// <remarks>
/// <para>
/// The gate is enforced by the type system as far as it can be —
/// <c>GenerateInstaller.Write</c> takes a <c>GateResult</c>, so a caller that has not gated has
/// nothing to pass it. What the type system cannot prevent is a command calling
/// <c>ScaffoldFileWriter.Write</c> directly, which is exactly how the gate came to be wired
/// into three subcommands out of twenty-nine. This suite is that guard: it reads the command
/// sources and fails if one of them writes around the shared path.
/// </para>
/// <para>
/// A source scan rather than a reflection check, because the property under test is "which API
/// does this code call", and that is not observable on the compiled surface.
/// </para>
/// </remarks>
public class GenerateGateSurfaceTests
{
    private static string GenerateCommandsDirectory()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "src", "D365FO.Cli")))
            dir = dir.Parent;

        Assert.NotNull(dir);
        return Path.Combine(dir!.FullName, "src", "D365FO.Cli", "Commands", "Generate");
    }

    /// <summary>Source files holding the generate subcommands, minus the shared installer itself.</summary>
    private static IEnumerable<(string Name, string Source)> CommandSources()
        => Directory.EnumerateFiles(GenerateCommandsDirectory(), "*.cs")
            .Where(f => Path.GetFileName(f) != "GenerateCommands.cs")
            .Select(f => (Path.GetFileName(f), File.ReadAllText(f)));

    [Fact]
    public void No_generate_command_calls_the_scaffold_writer_directly()
    {
        var offenders = CommandSources()
            .Where(f => Regex.IsMatch(f.Source, @"ScaffoldFileWriter\s*\.\s*Write\s*\("))
            .Select(f => f.Name)
            .ToArray();

        Assert.Empty(offenders);
    }

    [Fact]
    public void Every_generate_command_source_that_writes_also_gates()
    {
        var offenders = new List<string>();

        foreach (var (name, source) in CommandSources())
        {
            var writes = Regex.IsMatch(source, @"GenerateInstaller\s*\.\s*(Write|Emit|EmitString)\s*\(")
                         || source.Contains("AtomicSave(", StringComparison.Ordinal);
            if (!writes) continue;

            if (!Regex.IsMatch(source, @"GenerateInstaller\s*\.\s*Gate\s*\("))
                offenders.Add(name);
        }

        Assert.Empty(offenders);
    }

    [Fact]
    public void The_shared_installer_is_the_only_place_that_names_the_scaffold_writer()
    {
        // GenerateCommands.cs holds GenerateInstaller. It is allowed to call the writer —
        // that is the whole point of a choke point — but it must do so from the gated
        // wrappers, so every call site there sits inside a method taking a GateResult.
        var installer = File.ReadAllText(Path.Combine(GenerateCommandsDirectory(), "GenerateCommands.cs"));
        var calls = Regex.Matches(installer, @"ScaffoldFileWriter\s*\.\s*Write\s*\(\s*(doc|xml)\s*,").Count;

        Assert.Equal(2, calls); // the XDocument overload and the string one
    }

    [Fact]
    public void Requested_values_come_from_the_command_s_own_options_only()
    {
        // The honesty report must not treat plumbing as a request: --out is not a property of
        // the generated object, and reporting it as "missing from the document" every time
        // would bury the findings that matter.
        var settings = new GenerateTableCommand.Settings
        {
            Name = "ConVehicle",
            Out = @"C:\scratch\ConVehicle.xml",
            InstallTo = "ConModel",
            GroundingToken = "deadbeef",
            Pattern = "main",
            Fields = ["Plate:Name:mandatory"],
        };

        var requested = GenerateInstaller.RequestedValues(settings);
        var options = requested.Select(r => r.Option).ToArray();

        Assert.Contains("<NAME>", options);
        Assert.Contains("--pattern", options);
        Assert.Contains("--field", options);
        Assert.DoesNotContain("--out", options);
        Assert.DoesNotContain("--install-to", options);
        Assert.DoesNotContain("--grounding-token", options);
        Assert.DoesNotContain("--output", options);
    }

    [Fact]
    public void Every_generate_settings_type_derives_from_the_gated_base()
    {
        // A command whose settings do not derive from GenerateSettings cannot be gated by
        // GenerateInstaller.Gate at all, so it would silently sit outside the guarantee.
        var offenders = typeof(GenerateInstaller).Assembly.GetTypes()
            .Where(t => t is { IsAbstract: false, IsClass: true }
                        && t.Namespace == "D365FO.Cli.Commands.Generate"
                        && t.Name.StartsWith("Generate", StringComparison.Ordinal)
                        && t.Name.EndsWith("Command", StringComparison.Ordinal))
            .Select(t => (Command: t, Settings: t.GetNestedType("Settings", BindingFlags.Public | BindingFlags.NonPublic)))
            .Where(x => x.Settings is not null && !typeof(GenerateSettings).IsAssignableFrom(x.Settings))
            .Select(x => x.Command.Name)
            .ToArray();

        Assert.Empty(offenders);
    }
}
