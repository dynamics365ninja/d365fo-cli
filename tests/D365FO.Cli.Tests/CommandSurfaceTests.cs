using System.Reflection;
using D365FO.Cli;
using Spectre.Console.Cli;
using Xunit;

namespace D365FO.Cli.Tests;

/// <summary>
/// Every command the app registers must be one Spectre can actually construct.
/// </summary>
/// <remarks>
/// Spectre.Console.Cli resolves a command's <c>TSettings</c> by reflection at RUN time, so an
/// abstract settings type compiles, registers, and shows up in <c>--help</c> — then fails on
/// invocation with "Could not resolve type '…Settings'". That is exactly what shipped for all
/// seven <c>modify remove-*</c> commands: the engine tests passed, because they call the engine
/// rather than the command, and the defect only appeared when the commands were run for real.
///
/// This walks the registered command types instead of waiting for a live run.
/// </remarks>
public class CommandSurfaceTests
{
    /// <summary>The <c>TSettings</c> of every <c>Command&lt;T&gt;</c> in the CLI assembly.</summary>
    public static TheoryData<Type, Type> CommandsWithSettings()
    {
        var data = new TheoryData<Type, Type>();
        foreach (var type in typeof(CliApp).Assembly.GetTypes())
        {
            if (type.IsAbstract || !type.IsClass) continue;

            for (var baseType = type.BaseType; baseType is not null; baseType = baseType.BaseType)
            {
                if (!baseType.IsGenericType) continue;
                var definition = baseType.GetGenericTypeDefinition();
                if (definition != typeof(Command<>) && definition != typeof(AsyncCommand<>)) continue;

                data.Add(type, baseType.GetGenericArguments()[0]);
                break;
            }
        }
        return data;
    }

    [Theory]
    [MemberData(nameof(CommandsWithSettings))]
    public void Every_commands_settings_type_can_be_constructed(Type command, Type settings)
    {
        Assert.False(settings.IsAbstract,
            $"{command.Name} uses {settings.Name} as its settings type, but it is abstract — " +
            "Spectre constructs settings by reflection, so this fails at run time with " +
            "\"Could not resolve type\" while compiling and appearing in --help perfectly happily.");

        Assert.NotNull(settings.GetConstructor(Type.EmptyTypes));
        Assert.NotNull(Activator.CreateInstance(settings));
    }

    [Fact]
    public void The_app_builds_without_a_duplicate_or_unresolvable_registration()
    {
        // Configure() runs the whole registration tree, which is where a duplicate command name
        // or an unconstructable branch would throw.
        var app = CliApp.Build();
        Assert.NotNull(app);
    }
}
